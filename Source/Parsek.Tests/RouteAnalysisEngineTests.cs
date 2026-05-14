using System.Collections.Generic;
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
                        UndockEndpointResources = Manifest(0.0, 200.0)
                    }
                }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.NoDeliveryManifest, result.Status);
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

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.True(result.IsEligible);
            Assert.Same(source, result.SourceRecording);
        }

        private static RouteConnectionWindow BuildDeliveryWindow(string id = "window")
        {
            InventoryPayloadItem item = Payload("payload-hash", "evaJetpack", 1);
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
                UndockEndpointInventory = new List<InventoryPayloadItem> { item.DeepClone() }
            };
        }

        private static Dictionary<string, ResourceAmount> Manifest(double amount, double maxAmount)
        {
            return new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = amount, maxAmount = maxAmount }
            };
        }

        private static InventoryPayloadItem Payload(string hash, string partName, int quantity)
        {
            ConfigNode storedPart = new ConfigNode("STOREDPART");
            storedPart.AddValue("partName", partName);

            return new InventoryPayloadItem
            {
                IdentityHash = hash,
                PartName = partName,
                Quantity = quantity,
                SlotsTaken = quantity,
                StoredPartSnapshot = storedPart
            };
        }
    }
}
