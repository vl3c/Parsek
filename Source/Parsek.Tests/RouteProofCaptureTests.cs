using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RouteProofCaptureTests
    {
        [Fact]
        public void ExtractResourceManifest_WithPartPidScope_FiltersResources()
        {
            ConfigNode vessel = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 80.0, 100.0)),
                MakePart(200, "endpointTank", MakeResource("LiquidFuel", 5.0, 200.0)));

            Dictionary<string, ResourceAmount> scoped =
                VesselSpawner.ExtractResourceManifest(vessel, new List<uint> { 100 });

            Assert.NotNull(scoped);
            Assert.Single(scoped);
            Assert.Equal(80.0, scoped["LiquidFuel"].amount);
            Assert.Equal(100.0, scoped["LiquidFuel"].maxAmount);
        }

        [Fact]
        public void ExtractInventoryPayloadItems_PreservesExactStoredPartSnapshot()
        {
            ConfigNode storedPart = MakeStoredPart(
                "evaJetpack",
                "white",
                1,
                MakeResource("MonoPropellant", 5.0, 5.0));
            ConfigNode vessel = MakeVessel(
                MakePart(100, "cargoBay", MakeInventoryModule(storedPart)),
                MakePart(200, "stationCargo", MakeInventoryModule(
                    MakeStoredPart("evaRepairKit", null, 2))));

            List<InventoryPayloadItem> payload =
                VesselSpawner.ExtractInventoryPayloadItems(vessel, new List<uint> { 100 });

            Assert.NotNull(payload);
            Assert.Single(payload);
            Assert.Equal("evaJetpack", payload[0].PartName);
            Assert.Equal("white", payload[0].VariantName);
            Assert.Equal(1, payload[0].Quantity);
            Assert.Equal(1, payload[0].SlotsTaken);
            Assert.Equal(5.0, payload[0].StoredResources["MonoPropellant"].amount);
            Assert.Equal("STOREDPART", payload[0].StoredPartSnapshot.name);
            Assert.Equal("evaJetpack", payload[0].StoredPartSnapshot.GetValue("partName"));
        }

        [Fact]
        public void InventoryPayloadIdentityHash_IgnoresSlotAndQuantityButIncludesPayload()
        {
            ConfigNode first = new ConfigNode("STOREDPART");
            first.AddValue("slotIndex", "0");
            first.AddValue("partName", "evaJetpack");
            first.AddValue("variantName", "white");
            first.AddValue("quantity", "1");

            ConfigNode second = new ConfigNode("STOREDPART");
            second.AddValue("quantity", "3");
            second.AddValue("slotIndex", "4");
            second.AddValue("variantName", "white");
            second.AddValue("partName", "evaJetpack");

            string firstHash = VesselSpawner.ComputeInventoryPayloadIdentityHash(first);
            string secondHash = VesselSpawner.ComputeInventoryPayloadIdentityHash(second);
            Assert.Equal(firstHash, secondHash);

            second.SetValue("variantName", "orange", true);
            Assert.NotEqual(firstHash, VesselSpawner.ComputeInventoryPayloadIdentityHash(second));
        }

        [Fact]
        public void ExtractInventoryPayloadItems_GroupsStacksByPayloadIdentity()
        {
            ConfigNode first = MakeStoredPart("evaRepairKit", null, 2);
            first.AddValue("slotIndex", "0");
            ConfigNode second = MakeStoredPart("evaRepairKit", null, 3);
            second.AddValue("slotIndex", "1");
            ConfigNode vessel = MakeVessel(
                MakePart(100, "cargoBay", MakeInventoryModule(first, second)));

            List<InventoryPayloadItem> payload =
                VesselSpawner.ExtractInventoryPayloadItems(vessel, new List<uint> { 100 });

            Assert.NotNull(payload);
            Assert.Single(payload);
            Assert.Equal("evaRepairKit", payload[0].PartName);
            Assert.Equal(5, payload[0].Quantity);
            Assert.Equal(2, payload[0].SlotsTaken);
        }

        [Fact]
        public void RouteConnectionWindow_CapturesDockAndCompletesAtUndock()
        {
            ConfigNode dockedSnapshot = MakeVessel(
                MakePart(
                    100,
                    "transportTank",
                    MakeResource("LiquidFuel", 80.0, 100.0),
                    MakeInventoryModule(MakeStoredPart("evaJetpack", "white", 1))),
                MakePart(
                    200,
                    "endpointTank",
                    MakeResource("LiquidFuel", 0.0, 200.0)));

            RouteConnectionWindow window = RouteProofCapture.BuildDockRouteConnectionWindow(
                100.0,
                9001,
                RouteConnectionKind.DockingPort,
                dockedSnapshot,
                new List<uint> { 100 },
                new List<uint> { 200 },
                new RouteEndpoint
                {
                    VesselPersistentId = 9001,
                    BodyName = "Mun",
                    Latitude = 1.0,
                    Longitude = 2.0,
                    Altitude = 3.0,
                    IsSurface = true
                },
                4);

            Assert.NotNull(window);
            Assert.False(window.IsComplete);
            Assert.Equal(80.0, window.DockTransportResources["LiquidFuel"].amount);
            Assert.Equal(0.0, window.DockEndpointResources["LiquidFuel"].amount);
            Assert.Single(window.DockTransportInventory);
            Assert.Null(window.DockEndpointInventory);

            ConfigNode undockedTransport = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 30.0, 100.0)));
            ConfigNode undockedEndpoint = MakeVessel(
                MakePart(
                    200,
                    "endpointTank",
                    MakeResource("LiquidFuel", 50.0, 200.0),
                    MakeInventoryModule(MakeStoredPart("evaJetpack", "white", 1))));

            Assert.True(RouteProofCapture.CompleteRouteConnectionWindowAtUndock(
                window,
                160.0,
                undockedTransport,
                undockedEndpoint));

            Assert.True(window.IsComplete);
            Assert.Equal(160.0, window.UndockUT);
            Assert.Equal(30.0, window.UndockTransportResources["LiquidFuel"].amount);
            Assert.Equal(50.0, window.UndockEndpointResources["LiquidFuel"].amount);
            Assert.Null(window.UndockTransportInventory);
            Assert.Single(window.UndockEndpointInventory);
            Assert.Equal("evaJetpack", window.UndockEndpointInventory[0].PartName);
        }

        [Fact]
        public void CompleteRouteConnectionWindowAtUndock_MissingEndpointPart_DoesNotMarkComplete()
        {
            var window = new RouteConnectionWindow
            {
                WindowId = "window",
                DockUT = 100.0,
                TransferTargetVesselPid = 9001,
                TransportPartPersistentIds = new List<uint> { 100 },
                EndpointPartPersistentIds = new List<uint> { 200 }
            };
            ConfigNode onlyTransport = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 30.0, 100.0)));

            Assert.False(RouteProofCapture.CompleteRouteConnectionWindowAtUndock(
                window,
                160.0,
                onlyTransport));

            Assert.False(window.IsComplete);
            Assert.True(double.IsNaN(window.UndockUT));
        }

        [Fact]
        public void CompleteRouteConnectionWindowAtUndock_RouteSetsStillDocked_DoesNotMarkComplete()
        {
            var window = new RouteConnectionWindow
            {
                WindowId = "window",
                DockUT = 100.0,
                TransferTargetVesselPid = 9001,
                TransportPartPersistentIds = new List<uint> { 100 },
                EndpointPartPersistentIds = new List<uint> { 200 }
            };
            ConfigNode routePairStillDocked = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 30.0, 100.0)),
                MakePart(200, "endpointTank", MakeResource("LiquidFuel", 50.0, 200.0)));
            ConfigNode unrelatedUndockedCraft = MakeVessel(
                MakePart(300, "thirdCraft", MakeResource("Ore", 1.0, 10.0)));

            Assert.False(RouteProofCapture.CompleteRouteConnectionWindowAtUndock(
                window,
                160.0,
                routePairStillDocked,
                unrelatedUndockedCraft));

            Assert.False(window.IsComplete);
            Assert.True(double.IsNaN(window.UndockUT));
        }

        private static ConfigNode MakeVessel(params ConfigNode[] parts)
        {
            ConfigNode vessel = new ConfigNode("VESSEL");
            for (int i = 0; i < parts.Length; i++)
                vessel.AddNode(parts[i]);
            return vessel;
        }

        private static ConfigNode MakePart(uint persistentId, string name, params ConfigNode[] children)
        {
            ConfigNode part = new ConfigNode("PART");
            part.AddValue("name", name);
            part.AddValue("persistentId", persistentId.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < children.Length; i++)
                part.AddNode(children[i]);
            return part;
        }

        private static ConfigNode MakeResource(string name, double amount, double maxAmount)
        {
            ConfigNode resource = new ConfigNode("RESOURCE");
            resource.AddValue("name", name);
            resource.AddValue("amount", amount.ToString("R", CultureInfo.InvariantCulture));
            resource.AddValue("maxAmount", maxAmount.ToString("R", CultureInfo.InvariantCulture));
            return resource;
        }

        private static ConfigNode MakeInventoryModule(params ConfigNode[] storedParts)
        {
            ConfigNode module = new ConfigNode("MODULE");
            module.AddValue("name", "ModuleInventoryPart");
            module.AddValue("InventorySlots", "4");
            ConfigNode storedPartsNode = module.AddNode("STOREDPARTS");
            for (int i = 0; i < storedParts.Length; i++)
                storedPartsNode.AddNode(storedParts[i]);
            return module;
        }

        private static ConfigNode MakeStoredPart(
            string partName,
            string variantName,
            int quantity,
            ConfigNode resource = null)
        {
            ConfigNode storedPart = new ConfigNode("STOREDPART");
            storedPart.AddValue("partName", partName);
            if (!string.IsNullOrEmpty(variantName))
                storedPart.AddValue("variantName", variantName);
            storedPart.AddValue("quantity", quantity.ToString(CultureInfo.InvariantCulture));
            if (resource != null)
            {
                ConfigNode part = storedPart.AddNode("PART");
                part.AddValue("name", partName);
                part.AddNode(resource);
            }
            return storedPart;
        }
    }
}
