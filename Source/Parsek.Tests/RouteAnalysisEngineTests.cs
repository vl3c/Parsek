using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RouteAnalysisEngineTests
    {
        [Fact]
        public void AnalyzeRecording_CompletedWindow_ExtractsDeliveryManifest()
        {
            Recording rec = new Recording
            {
                RecordingId = "route-source",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    BuildDeliveryWindow()
                }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.True(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.Eligible, result.Status);
            Assert.Same(rec, result.SourceRecording);
            Assert.Equal(50.0, result.ResourceDeliveryManifest["LiquidFuel"]);
            Assert.Single(result.InventoryDeliveryManifest);
            Assert.Equal("evaJetpack", result.InventoryDeliveryManifest[0].PartName);
            Assert.Equal(1, result.InventoryDeliveryManifest[0].Quantity);
            Assert.Equal(1, result.InventoryDeliveryManifest[0].SlotsTaken);
            Assert.Equal("1", result.InventoryDeliveryManifest[0]
                .StoredPartSnapshot.GetValue("quantity"));
        }

        [Fact]
        public void AnalyzeRecording_BaseManifestOnly_RejectsAsMissingProof()
        {
            Recording rec = new Recording
            {
                RecordingId = "old-recording",
                StartResources = new Dictionary<string, ResourceAmount>
                {
                    ["LiquidFuel"] = new ResourceAmount { amount = 100.0, maxAmount = 100.0 }
                },
                EndResources = new Dictionary<string, ResourceAmount>
                {
                    ["LiquidFuel"] = new ResourceAmount { amount = 50.0, maxAmount = 100.0 }
                }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.MissingRouteProof, result.Status);
        }

        [Fact]
        public void AnalyzeRecording_MultipleCompletedWindows_RejectsV0MultiStop()
        {
            Recording rec = new Recording
            {
                RecordingId = "multi-stop",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    BuildDeliveryWindow("one"),
                    BuildDeliveryWindow("two")
                }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.MultipleConnectionWindows, result.Status);
        }

        [Fact]
        public void AnalyzeRecording_CompleteWindowWithoutTransfer_RejectsNoDelivery()
        {
            Recording rec = new Recording
            {
                RecordingId = "no-transfer",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "empty",
                        DockUT = 10.0,
                        UndockUT = 20.0,
                        DockTransportResources = Manifest(100.0, 100.0),
                        UndockTransportResources = Manifest(100.0, 100.0),
                        DockEndpointResources = Manifest(0.0, 200.0),
                        UndockEndpointResources = Manifest(0.0, 200.0),
                        TransferTargetVesselPid = 9001,
                        TransferKind = RouteConnectionKind.DockingPort,
                        EndpointAtDock = Endpoint(),
                        TransferEndpointSituation = 4
                    }
                }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.NoDeliveryManifest, result.Status);
        }

        [Fact]
        public void AnalyzeRecording_MixedPickupDelivery_RejectsV0Route()
        {
            RouteConnectionWindow window = BuildDeliveryWindow();
            window.DockEndpointResources["Ore"] =
                new ResourceAmount { amount = 10.0, maxAmount = 50.0 };
            window.UndockEndpointResources["Ore"] =
                new ResourceAmount { amount = 0.0, maxAmount = 50.0 };
            window.DockTransportResources["Ore"] =
                new ResourceAmount { amount = 0.0, maxAmount = 50.0 };
            window.UndockTransportResources["Ore"] =
                new ResourceAmount { amount = 10.0, maxAmount = 50.0 };
            Recording rec = new Recording
            {
                RecordingId = "mixed-resource",
                RouteConnectionWindows = new List<RouteConnectionWindow> { window }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.MixedPickupDelivery, result.Status);
        }

        [Fact]
        public void AnalyzeRecording_InventoryPickup_RejectsV0Route()
        {
            RouteConnectionWindow window = BuildDeliveryWindow();
            InventoryPayloadItem pickup = Payload("ore-container", "smallCargoContainer", 1, slotsTaken: 1);
            window.DockEndpointInventory = new List<InventoryPayloadItem> { pickup.DeepClone() };
            window.UndockTransportInventory = new List<InventoryPayloadItem> { pickup.DeepClone() };
            Recording rec = new Recording
            {
                RecordingId = "mixed-inventory",
                RouteConnectionWindows = new List<RouteConnectionWindow> { window }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.MixedPickupDelivery, result.Status);
        }

        [Fact]
        public void AnalyzeRecording_MissingEndpointProof_RejectsCandidate()
        {
            RouteConnectionWindow window = BuildDeliveryWindow();
            window.EndpointAtDock = null;
            window.TransferEndpointSituation = -1;
            Recording rec = new Recording
            {
                RecordingId = "missing-endpoint",
                RouteConnectionWindows = new List<RouteConnectionWindow> { window }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.MissingEndpointProof, result.Status);
        }

        [Fact]
        public void AnalyzeTree_FindsCompletedWindowRecording()
        {
            Recording root = new Recording { RecordingId = "root" };
            Recording source = new Recording
            {
                RecordingId = "source",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    BuildDeliveryWindow()
                }
            };
            RecordingTree tree = new RecordingTree { Id = "tree" };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(source);
            tree.RootRecordingId = root.RecordingId;
            tree.ActiveRecordingId = source.RecordingId;
            string bpId = "bp";
            source.ParentBranchPointId = bpId;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = bpId,
                ParentRecordingIds = new List<string> { root.RecordingId },
                ChildRecordingIds = new List<string> { source.RecordingId }
            });

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.True(result.IsEligible);
            Assert.Same(source, result.SourceRecording);
        }

        [Fact]
        public void AnalyzeTree_IgnoresCompletedWindowOffActivePath()
        {
            Recording active = new Recording { RecordingId = "active" };
            Recording alternate = new Recording
            {
                RecordingId = "alternate",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    BuildDeliveryWindow()
                }
            };
            RecordingTree tree = new RecordingTree
            {
                Id = "tree",
                RootRecordingId = active.RecordingId,
                ActiveRecordingId = active.RecordingId
            };
            tree.AddOrReplaceRecording(active);
            tree.AddOrReplaceRecording(alternate);

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.MissingRouteProof, result.Status);
        }

        [Fact]
        public void AnalyzeRecording_StackQuantityDelta_MatchesByPayloadIdentity()
        {
            string hash = BuildPayloadHash("evaRepairKit");
            Recording rec = new Recording
            {
                RecordingId = "stacked-inventory",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "window",
                        DockUT = 100.0,
                        UndockUT = 160.0,
                        TransferTargetVesselPid = 9001,
                        TransferKind = RouteConnectionKind.DockingPort,
                        EndpointAtDock = Endpoint(),
                        TransferEndpointSituation = 4,
                        DockTransportInventory = new List<InventoryPayloadItem>
                        {
                            Payload(hash, "evaRepairKit", 3, slotsTaken: 1)
                        },
                        UndockTransportInventory = new List<InventoryPayloadItem>
                        {
                            Payload(hash, "evaRepairKit", 2, slotsTaken: 1)
                        },
                        UndockEndpointInventory = new List<InventoryPayloadItem>
                        {
                            Payload(hash, "evaRepairKit", 1, slotsTaken: 1)
                        }
                    }
                }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.True(result.IsEligible);
            Assert.Single(result.InventoryDeliveryManifest);
            Assert.Equal(1, result.InventoryDeliveryManifest[0].Quantity);
            Assert.Equal("1", result.InventoryDeliveryManifest[0]
                .StoredPartSnapshot.GetValue("quantity"));
        }

        private static RouteConnectionWindow BuildDeliveryWindow(string id = "window")
        {
            InventoryPayloadItem item = Payload("payload-hash", "evaJetpack", 1, slotsTaken: 1);
            return new RouteConnectionWindow
            {
                WindowId = id,
                DockUT = 100.0,
                UndockUT = 160.0,
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                DockTransportResources = Manifest(80.0, 100.0),
                UndockTransportResources = Manifest(30.0, 100.0),
                DockEndpointResources = Manifest(0.0, 200.0),
                UndockEndpointResources = Manifest(50.0, 200.0),
                DockTransportInventory = new List<InventoryPayloadItem> { item.DeepClone() },
                UndockTransportInventory = null,
                DockEndpointInventory = null,
                UndockEndpointInventory = new List<InventoryPayloadItem> { item.DeepClone() },
                EndpointAtDock = Endpoint(),
                TransferEndpointSituation = 4
            };
        }

        private static Dictionary<string, ResourceAmount> Manifest(double amount, double maxAmount)
        {
            return new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = amount, maxAmount = maxAmount }
            };
        }

        private static InventoryPayloadItem Payload(
            string hash,
            string partName,
            int quantity,
            int slotsTaken)
        {
            ConfigNode storedPart = new ConfigNode("STOREDPART");
            storedPart.AddValue("partName", partName);
            storedPart.AddValue("quantity", quantity.ToString(CultureInfo.InvariantCulture));

            return new InventoryPayloadItem
            {
                IdentityHash = hash,
                PartName = partName,
                Quantity = quantity,
                SlotsTaken = slotsTaken,
                StoredPartSnapshot = storedPart
            };
        }

        private static RouteEndpoint Endpoint()
        {
            return new RouteEndpoint
            {
                VesselPersistentId = 9001,
                BodyName = "Mun",
                Latitude = 1.0,
                Longitude = 2.0,
                Altitude = 3.0,
                IsSurface = true
            };
        }

        private static string BuildPayloadHash(string partName)
        {
            ConfigNode storedPart = new ConfigNode("STOREDPART");
            storedPart.AddValue("slotIndex", "0");
            storedPart.AddValue("partName", partName);
            storedPart.AddValue("quantity", "1");
            return VesselSpawner.ComputeInventoryPayloadIdentityHash(storedPart);
        }
    }
}
