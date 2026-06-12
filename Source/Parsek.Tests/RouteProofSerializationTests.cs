using System.Collections.Generic;
using Parsek.Tests.Generators;
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
            Assert.Equal("Mun", loaded.RouteOriginProof.StartDockedOriginBodyName);
            Assert.True(loaded.RouteOriginProof.StartDockedOriginIsSurface);
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
        public void RouteProof_OriginDescriptor_RoundTrips()
        {
            // FAILS IF: any of the six origin endpoint descriptor fields (M1) is
            // lost or reformatted on the round trip through ConfigNode.
            var rec = new Recording
            {
                RecordingId = "route-proof-descriptor",
                RouteOriginProof = new RouteOriginProof
                {
                    StartDockedOriginVesselPid = 7007,
                    StartDockedOriginBodyName = "Minmus",
                    StartDockedOriginLatitude = -0.55,
                    StartDockedOriginLongitude = 78.25,
                    StartDockedOriginAltitude = 2412.5,
                    StartDockedOriginIsSurface = true,
                    StartDockedOriginSituation = (int)Vessel.Situations.LANDED
                }
            };

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingResourceAndState(node, rec);

            var loaded = new Recording { RecordingId = "route-proof-descriptor" };
            RecordingTree.LoadRecordingResourceAndState(node, loaded);

            Assert.NotNull(loaded.RouteOriginProof);
            Assert.Equal(7007u, loaded.RouteOriginProof.StartDockedOriginVesselPid);
            Assert.Equal("Minmus", loaded.RouteOriginProof.StartDockedOriginBodyName);
            Assert.Equal(-0.55, loaded.RouteOriginProof.StartDockedOriginLatitude);
            Assert.Equal(78.25, loaded.RouteOriginProof.StartDockedOriginLongitude);
            Assert.Equal(2412.5, loaded.RouteOriginProof.StartDockedOriginAltitude);
            Assert.True(loaded.RouteOriginProof.StartDockedOriginIsSurface);
            Assert.Equal((int)Vessel.Situations.LANDED, loaded.RouteOriginProof.StartDockedOriginSituation);
        }

        [Fact]
        public void RouteProof_OriginDescriptorAbsent_ReadsBackDefaults()
        {
            // FAILS IF: an old-shape proof (pid-only, recorded before the M1
            // descriptor) does not read back with the field defaults: empty body
            // name, zero coords, IsSurface false, situation -1. The sparse writer
            // must also omit the descriptor values when the body name is empty.
            var rec = new Recording
            {
                RecordingId = "route-proof-old-shape",
                RouteOriginProof = new RouteOriginProof
                {
                    StartDockedOriginVesselPid = 7007
                }
            };

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingResourceAndState(node, rec);

            var loaded = new Recording { RecordingId = "route-proof-old-shape" };
            RecordingTree.LoadRecordingResourceAndState(node, loaded);

            Assert.NotNull(loaded.RouteOriginProof);
            Assert.Equal(7007u, loaded.RouteOriginProof.StartDockedOriginVesselPid);
            Assert.True(string.IsNullOrEmpty(loaded.RouteOriginProof.StartDockedOriginBodyName));
            Assert.Equal(0.0, loaded.RouteOriginProof.StartDockedOriginLatitude);
            Assert.Equal(0.0, loaded.RouteOriginProof.StartDockedOriginLongitude);
            Assert.Equal(0.0, loaded.RouteOriginProof.StartDockedOriginAltitude);
            Assert.False(loaded.RouteOriginProof.StartDockedOriginIsSurface);
            Assert.Equal(-1, loaded.RouteOriginProof.StartDockedOriginSituation);
        }

        [Fact]
        public void RouteProof_CrpResourceNames_RoundTrip()
        {
            // FAILS IF: the proof codec carries any stock-name assumption, or
            // an UNDEFINED name is dropped/altered on the round trip. Capture
            // is permissive (M2 D2): recordings are immutable witnesses, so a
            // resource name with no current PartResourceDefinition must
            // serialize and read back verbatim - the exclusion happens at
            // analysis, never in storage.
            var rec = new Recording
            {
                RecordingId = "route-proof-crp",
                RouteOriginProof = new RouteOriginProof
                {
                    StartDockedOriginVesselPid = 7007,
                    StartTransportResources = new Dictionary<string, ResourceAmount>
                    {
                        [CrpFixtures.Karbonite] =
                            new ResourceAmount { amount = 320.5, maxAmount = 400.0 }
                    }
                },
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "crp-window",
                        DockUT = 100.0,
                        UndockUT = 180.0,
                        TransferTargetVesselPid = 9001,
                        TransferKind = RouteConnectionKind.DockingPort,
                        DockTransportResources = new Dictionary<string, ResourceAmount>
                        {
                            [CrpFixtures.Karbonite] =
                                new ResourceAmount { amount = 320.5, maxAmount = 400.0 },
                            [CrpFixtures.UninstalledModResource] =
                                new ResourceAmount { amount = 12.25, maxAmount = 50.0 }
                        },
                        UndockTransportResources = new Dictionary<string, ResourceAmount>
                        {
                            [CrpFixtures.Karbonite] =
                                new ResourceAmount { amount = 20.5, maxAmount = 400.0 },
                            [CrpFixtures.UninstalledModResource] =
                                new ResourceAmount { amount = 12.25, maxAmount = 50.0 }
                        },
                        DockEndpointResources = new Dictionary<string, ResourceAmount>
                        {
                            [CrpFixtures.MetallicOre] =
                                new ResourceAmount { amount = 0.0, maxAmount = 1000.0 }
                        },
                        UndockEndpointResources = new Dictionary<string, ResourceAmount>
                        {
                            [CrpFixtures.MetallicOre] =
                                new ResourceAmount { amount = 300.0, maxAmount = 1000.0 }
                        }
                    }
                }
            };

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingResourceAndState(node, rec);

            var loaded = new Recording { RecordingId = "route-proof-crp" };
            RecordingTree.LoadRecordingResourceAndState(node, loaded);

            Assert.Equal(320.5,
                loaded.RouteOriginProof.StartTransportResources[CrpFixtures.Karbonite].amount);
            RouteConnectionWindow window = loaded.RouteConnectionWindows[0];
            Assert.Equal(320.5, window.DockTransportResources[CrpFixtures.Karbonite].amount);
            Assert.Equal(20.5, window.UndockTransportResources[CrpFixtures.Karbonite].amount);
            Assert.Equal(12.25,
                window.DockTransportResources[CrpFixtures.UninstalledModResource].amount);
            Assert.Equal(12.25,
                window.UndockTransportResources[CrpFixtures.UninstalledModResource].amount);
            Assert.Equal(300.0, window.UndockEndpointResources[CrpFixtures.MetallicOre].amount);
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
            // M2 (D10 null-preservation pin): an old-shape node must read back
            // a NULL run manifest - a codec that lazily allocates an empty one
            // flips every existing route to SourceChanged on revalidate.
            Assert.Null(loaded.RouteRunManifest);
        }

        [Fact]
        public void RouteRunManifest_RoundTripsViaTreeMetadata()
        {
            // FAILS IF: the M2 run-manifest node loses the pid scope, either
            // resource half, or the explicit endCaptured completion marker on
            // the tree persistence path.
            var rec = new Recording
            {
                RecordingId = "run-manifest-tree",
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { 100u, 200u },
                    StartTransportResources = new Dictionary<string, ResourceAmount>
                    {
                        ["Ore"] = new ResourceAmount { amount = 0.0, maxAmount = 1500.0 },
                        [CrpFixtures.Karbonite] = new ResourceAmount { amount = 12.5, maxAmount = 50.0 }
                    },
                    EndTransportResources = new Dictionary<string, ResourceAmount>
                    {
                        ["Ore"] = new ResourceAmount { amount = 1200.0, maxAmount = 1500.0 }
                    },
                    EndCaptured = true
                }
            };

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingResourceAndState(node, rec);

            var loaded = new Recording { RecordingId = "run-manifest-tree" };
            RecordingTree.LoadRecordingResourceAndState(node, loaded);

            Assert.NotNull(loaded.RouteRunManifest);
            Assert.Equal(new List<uint> { 100u, 200u },
                loaded.RouteRunManifest.TransportPartPersistentIds);
            Assert.Equal(0.0, loaded.RouteRunManifest.StartTransportResources["Ore"].amount);
            Assert.Equal(1500.0, loaded.RouteRunManifest.StartTransportResources["Ore"].maxAmount);
            Assert.Equal(12.5,
                loaded.RouteRunManifest.StartTransportResources[CrpFixtures.Karbonite].amount);
            Assert.Equal(1200.0, loaded.RouteRunManifest.EndTransportResources["Ore"].amount);
            Assert.True(loaded.RouteRunManifest.EndCaptured);
            Assert.True(loaded.RouteRunManifest.IsComplete);
        }

        [Fact]
        public void RouteRunManifest_RoundTripsViaScenarioMetadata()
        {
            var rec = new Recording
            {
                RecordingId = "run-manifest-scenario",
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { 11u },
                    StartTransportResources = new Dictionary<string, ResourceAmount>
                    {
                        ["MonoPropellant"] = new ResourceAmount { amount = 40.0, maxAmount = 40.0 }
                    },
                    EndCaptured = false
                }
            };

            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, rec);

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadataForTests(node, loaded);

            Assert.NotNull(loaded.RouteRunManifest);
            Assert.Equal(new List<uint> { 11u }, loaded.RouteRunManifest.TransportPartPersistentIds);
            Assert.Equal(40.0, loaded.RouteRunManifest.StartTransportResources["MonoPropellant"].amount);
            // A ForceStop-shaped start-only manifest must read back start-only:
            // the analysis presence gate requires BOTH halves (round-2
            // correction 5), so endCaptured may never invent itself on load.
            Assert.False(loaded.RouteRunManifest.EndCaptured);
            Assert.Null(loaded.RouteRunManifest.EndTransportResources);
            Assert.False(loaded.RouteRunManifest.IsComplete);
        }

        [Fact]
        public void RouteHarvestWindows_RoundTripViaTreeMetadata()
        {
            // FAILS IF: any harvest-window field (span, flags, manifests,
            // converter ids, open-time location) is lost or reformatted on the
            // tree persistence path - or an OPEN window's NaN EndUT does not
            // read back as open.
            var rec = new Recording
            {
                RecordingId = "harvest-windows-tree",
                RouteHarvestWindows = new List<RouteHarvestWindow>
                {
                    new RouteHarvestWindow
                    {
                        WindowId = "harvest-1000",
                        StartUT = 1000.0,
                        EndUT = 1600.5,
                        OpenedAtRecordingStart = true,
                        ClosedAtRecordingStop = false,
                        StartTransportResources = new Dictionary<string, ResourceAmount>
                        {
                            ["Ore"] = new ResourceAmount { amount = 0.0, maxAmount = 1500.0 }
                        },
                        EndTransportResources = new Dictionary<string, ResourceAmount>
                        {
                            ["Ore"] = new ResourceAmount { amount = 850.25, maxAmount = 1500.0 }
                        },
                        ActiveConverters = new List<string>
                        {
                            "100:ModuleResourceHarvester:Drill-O-Matic"
                        },
                        BodyName = "Minmus",
                        Latitude = -0.55,
                        Longitude = 78.25,
                        Altitude = 2412.5,
                        SituationAtOpen = (int)Vessel.Situations.LANDED
                    },
                    new RouteHarvestWindow
                    {
                        WindowId = "harvest-2000",
                        StartUT = 2000.0,
                        // EndUT NaN: still open at save time
                        ClosedAtRecordingStop = true
                    }
                }
            };

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingResourceAndState(node, rec);

            var loaded = new Recording { RecordingId = "harvest-windows-tree" };
            RecordingTree.LoadRecordingResourceAndState(node, loaded);

            Assert.NotNull(loaded.RouteHarvestWindows);
            Assert.Equal(2, loaded.RouteHarvestWindows.Count);

            RouteHarvestWindow first = loaded.RouteHarvestWindows[0];
            Assert.Equal("harvest-1000", first.WindowId);
            Assert.Equal(1000.0, first.StartUT);
            Assert.Equal(1600.5, first.EndUT);
            Assert.False(first.IsOpen);
            Assert.True(first.OpenedAtRecordingStart);
            Assert.False(first.ClosedAtRecordingStop);
            Assert.Equal(0.0, first.StartTransportResources["Ore"].amount);
            Assert.Equal(850.25, first.EndTransportResources["Ore"].amount);
            Assert.Equal("100:ModuleResourceHarvester:Drill-O-Matic", first.ActiveConverters[0]);
            Assert.Equal("Minmus", first.BodyName);
            Assert.Equal(-0.55, first.Latitude);
            Assert.Equal(78.25, first.Longitude);
            Assert.Equal(2412.5, first.Altitude);
            Assert.Equal((int)Vessel.Situations.LANDED, first.SituationAtOpen);

            RouteHarvestWindow second = loaded.RouteHarvestWindows[1];
            Assert.True(second.IsOpen);
            Assert.True(double.IsNaN(second.EndUT));
            Assert.True(second.ClosedAtRecordingStop);
            Assert.Null(second.StartTransportResources);
            Assert.Null(second.EndTransportResources);
            Assert.Null(second.ActiveConverters);
            Assert.True(string.IsNullOrEmpty(second.BodyName));
            Assert.Equal(-1, second.SituationAtOpen);
        }

        [Fact]
        public void RouteHarvestWindows_AbsentNode_ReadsBackNull()
        {
            var node = new ConfigNode("RECORDING");
            var loaded = new Recording();

            RecordingTree.LoadRecordingResourceAndState(node, loaded);

            // Same null-preservation contract as the run manifest: a codec
            // that lazily allocates a window list would widen the hasher gate
            // for every old recording.
            Assert.Null(loaded.RouteHarvestWindows);
        }

        [Fact]
        public void RouteHarvestWindows_BuilderFixture_RoundTrips()
        {
            ConfigNode node = new Generators.RecordingBuilder("Drill Transport")
                .WithHarvestWindow(
                    1000.0, 1600.0,
                    new Dictionary<string, ResourceAmount>
                    {
                        ["Ore"] = new ResourceAmount { amount = 0.0, maxAmount = 1500.0 }
                    },
                    new Dictionary<string, ResourceAmount>
                    {
                        ["Ore"] = new ResourceAmount { amount = 500.0, maxAmount = 1500.0 }
                    },
                    openedAtRecordingStart: true,
                    bodyName: "Minmus",
                    situationAtOpen: (int)Vessel.Situations.LANDED)
                .BuildV3Metadata();

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadataForTests(node, loaded);

            Assert.NotNull(loaded.RouteHarvestWindows);
            Assert.Single(loaded.RouteHarvestWindows);
            Assert.Equal(500.0, loaded.RouteHarvestWindows[0].EndTransportResources["Ore"].amount);
            Assert.True(loaded.RouteHarvestWindows[0].OpenedAtRecordingStart);
        }

        [Fact]
        public void RouteRunManifest_BuilderFixture_RoundTrips()
        {
            // Pins the generator support (Post-Change Checklist): a
            // RecordingBuilder fixture carrying a run manifest produces the
            // production node shape.
            ConfigNode node = new Generators.RecordingBuilder("Drill Transport")
                .WithRouteRunManifest(
                    new List<uint> { 100u, 200u },
                    new Dictionary<string, ResourceAmount>
                    {
                        ["Ore"] = new ResourceAmount { amount = 0.0, maxAmount = 1500.0 }
                    },
                    new Dictionary<string, ResourceAmount>
                    {
                        ["Ore"] = new ResourceAmount { amount = 900.0, maxAmount = 1500.0 }
                    },
                    endCaptured: true)
                .BuildV3Metadata();

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadataForTests(node, loaded);

            Assert.NotNull(loaded.RouteRunManifest);
            Assert.True(loaded.RouteRunManifest.IsComplete);
            Assert.Equal(900.0, loaded.RouteRunManifest.EndTransportResources["Ore"].amount);
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
            Assert.Equal("Mun", clone.RouteOriginProof.StartDockedOriginBodyName);
            Assert.True(clone.RouteOriginProof.StartDockedOriginIsSurface);
            Assert.Equal((int)Vessel.Situations.LANDED, clone.RouteOriginProof.StartDockedOriginSituation);
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
                    StartDockedOriginBodyName = "Mun",
                    StartDockedOriginLatitude = 12.0,
                    StartDockedOriginLongitude = -45.0,
                    StartDockedOriginAltitude = 612.0,
                    StartDockedOriginIsSurface = true,
                    StartDockedOriginSituation = (int)Vessel.Situations.LANDED,
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
