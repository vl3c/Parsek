using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RouteAnalysisEngineTests : IDisposable
    {
        // Capture ParsekLog output so the pickup-rejection diagnostic can be
        // asserted on. Following the canonical RewindLoggingTests pattern:
        // SuppressLogging is a global flag toggled by every static-touching
        // test, so it must be forced false before capture and restored after,
        // otherwise this test inherits a stale true and the sink stays empty.
        private readonly List<string> logLines = new List<string>();

        public RouteAnalysisEngineTests()
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

        // ---------------------------------------------------------------
        // EC / IntakeAir filtering — design section 5.3 rule 7 and section 6
        // ---------------------------------------------------------------
        // ElectricCharge and IntakeAir are environmental noise, never cargo.
        // They must be filtered out of BOTH the pickup gate (HasResourcePickup)
        // and the delivery manifest (BuildResourceDeliveryManifest) so a clean
        // delivery-only run whose batteries recharge from the depot, or whose
        // IntakeAir reading drifts across the dock/undock snapshots, is not
        // falsely rejected as a mixed pickup/delivery transfer.

        [Fact]
        public void AnalyzeRecording_TransportRechargesEC_StillEligible()
        {
            // Transport recharges its batteries from the docked depot: EC climbs
            // from dock to undock (transportGain > 0). Pre-fix this tripped the
            // pickup gate and rejected an otherwise clean LiquidFuel delivery.
            RouteConnectionWindow window = BuildDeliveryWindow();
            window.DockTransportResources["ElectricCharge"] =
                new ResourceAmount { amount = 100.0, maxAmount = 1000.0 };
            window.UndockTransportResources["ElectricCharge"] =
                new ResourceAmount { amount = 800.0, maxAmount = 1000.0 };
            Recording rec = new Recording
            {
                RecordingId = "ec-recharge",
                RouteConnectionWindows = new List<RouteConnectionWindow> { window }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.True(result.IsEligible,
                $"EC recharge must not trip the pickup gate, got {result.Status}");
            Assert.Equal(50.0, result.ResourceDeliveryManifest["LiquidFuel"]);
            Assert.False(result.ResourceDeliveryManifest.ContainsKey("ElectricCharge"));
        }

        [Fact]
        public void AnalyzeRecording_IntakeAirDrift_StillEligible()
        {
            // IntakeAir reads differently across the dock/undock snapshots
            // (atmosphere dependent). The endpoint shows a loss; pre-fix this
            // tripped the pickup gate.
            RouteConnectionWindow window = BuildDeliveryWindow();
            window.DockEndpointResources["IntakeAir"] =
                new ResourceAmount { amount = 5.0, maxAmount = 5.0 };
            window.UndockEndpointResources["IntakeAir"] =
                new ResourceAmount { amount = 0.0, maxAmount = 5.0 };
            Recording rec = new Recording
            {
                RecordingId = "intakeair-drift",
                RouteConnectionWindows = new List<RouteConnectionWindow> { window }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.True(result.IsEligible,
                $"IntakeAir drift must not trip the pickup gate, got {result.Status}");
            Assert.Equal(50.0, result.ResourceDeliveryManifest["LiquidFuel"]);
            Assert.False(result.ResourceDeliveryManifest.ContainsKey("IntakeAir"));
        }

        [Fact]
        public void AnalyzeRecording_ElectricChargeDelivery_ExcludedFromManifest()
        {
            // EC flows transport -> endpoint (transport drains, endpoint gains)
            // alongside the real LiquidFuel cargo. It must not appear as a
            // delivered resource in the manifest (design section 6).
            RouteConnectionWindow window = BuildDeliveryWindow();
            window.DockTransportResources["ElectricCharge"] =
                new ResourceAmount { amount = 500.0, maxAmount = 1000.0 };
            window.UndockTransportResources["ElectricCharge"] =
                new ResourceAmount { amount = 100.0, maxAmount = 1000.0 };
            window.DockEndpointResources["ElectricCharge"] =
                new ResourceAmount { amount = 0.0, maxAmount = 1000.0 };
            window.UndockEndpointResources["ElectricCharge"] =
                new ResourceAmount { amount = 400.0, maxAmount = 1000.0 };
            Recording rec = new Recording
            {
                RecordingId = "ec-delivery",
                RouteConnectionWindows = new List<RouteConnectionWindow> { window }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.True(result.IsEligible);
            Assert.Equal(50.0, result.ResourceDeliveryManifest["LiquidFuel"]);
            Assert.False(result.ResourceDeliveryManifest.ContainsKey("ElectricCharge"),
                "ElectricCharge must never appear as delivered cargo");
        }

        [Fact]
        public void AnalyzeRecording_ElectricChargeOnly_RejectsNoDelivery()
        {
            // EC is the only resource that moves and there is no inventory cargo:
            // after filtering, the manifest is empty so the candidate is rejected
            // as no-delivery, not treated as an EC supply run (design section 6:
            // "EC-only Supply Runs are not route-eligible in v1").
            var window = new RouteConnectionWindow
            {
                WindowId = "ec-only",
                DockUT = 10.0,
                UndockUT = 20.0,
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                EndpointAtDock = Endpoint(),
                TransferEndpointSituation = 4,
                DockTransportResources = new Dictionary<string, ResourceAmount>
                {
                    ["ElectricCharge"] = new ResourceAmount { amount = 500.0, maxAmount = 1000.0 }
                },
                UndockTransportResources = new Dictionary<string, ResourceAmount>
                {
                    ["ElectricCharge"] = new ResourceAmount { amount = 100.0, maxAmount = 1000.0 }
                },
                DockEndpointResources = new Dictionary<string, ResourceAmount>
                {
                    ["ElectricCharge"] = new ResourceAmount { amount = 0.0, maxAmount = 1000.0 }
                },
                UndockEndpointResources = new Dictionary<string, ResourceAmount>
                {
                    ["ElectricCharge"] = new ResourceAmount { amount = 400.0, maxAmount = 1000.0 }
                }
            };
            Recording rec = new Recording
            {
                RecordingId = "ec-only",
                RouteConnectionWindows = new List<RouteConnectionWindow> { window }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.NoDeliveryManifest, result.Status);
        }

        [Fact]
        public void AnalyzeRecording_NonIgnoredResourcePickup_DiagnosticNamesResource()
        {
            // A genuine (non-ignored) pickup still rejects, and the rejection
            // diagnostic names the culprit resource so a confusing "mixed
            // pickup/delivery" rejection is debuggable from the log. logLines is
            // captured by the constructor sink.
            RouteConnectionWindow window = BuildDeliveryWindow();
            window.DockEndpointResources["Ore"] =
                new ResourceAmount { amount = 10.0, maxAmount = 50.0 };
            window.UndockEndpointResources["Ore"] =
                new ResourceAmount { amount = 0.0, maxAmount = 50.0 };
            Recording rec = new Recording
            {
                RecordingId = "ore-pickup",
                RouteConnectionWindows = new List<RouteConnectionWindow> { window }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.MixedPickupDelivery, result.Status);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("mixed pickup/delivery") &&
                l.Contains("resource=Ore"));
        }

        [Fact]
        public void AnalyzeRecording_InventoryPickup_DiagnosticNamesIdentity()
        {
            // The inventory branch of the pickup gate also names its culprit (the
            // payload identity) in the rejection diagnostic. logLines is captured
            // by the constructor sink.
            RouteConnectionWindow window = BuildDeliveryWindow();
            InventoryPayloadItem pickup =
                Payload("ore-container", "smallCargoContainer", 1, slotsTaken: 1);
            window.DockEndpointInventory = new List<InventoryPayloadItem> { pickup.DeepClone() };
            window.UndockTransportInventory = new List<InventoryPayloadItem> { pickup.DeepClone() };
            Recording rec = new Recording
            {
                RecordingId = "mixed-inventory-diag",
                RouteConnectionWindows = new List<RouteConnectionWindow> { window }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.MixedPickupDelivery, result.Status);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("mixed pickup/delivery") &&
                l.Contains("inventory=ore-container"));
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

        // ---------------------------------------------------------------
        // CollectSourcePathRecordingIds — regression for playtest 6
        // ---------------------------------------------------------------
        // When the player switches vessels before committing,
        // ParsekFlight.OnVesselSwitchComplete nulls activeTree.ActiveRecordingId
        // (line 3029, transitioning old recorder to background). The previous
        // leaf-to-root walk fell back to RootRecordingId and only found the
        // root itself — a route window on a non-root branch (e.g. a dock-merged
        // child after dock+undock) was invisible and AnalyzeTree returned
        // MissingRouteProof even though the saved tree carried a complete
        // RouteConnectionWindow with both DOCK_/UNDOCK_ resource manifests.

        [Fact]
        public void CollectSourcePathRecordingIds_NoActiveRecordingId_IncludesAllRecordings()
        {
            var tree = new RecordingTree
            {
                Id = "tree-x",
                RootRecordingId = "root",
                ActiveRecordingId = null
            };
            tree.AddOrReplaceRecording(new Recording { RecordingId = "root", TreeId = "tree-x" });
            tree.AddOrReplaceRecording(new Recording { RecordingId = "merged-child", TreeId = "tree-x" });
            tree.AddOrReplaceRecording(new Recording { RecordingId = "post-undock", TreeId = "tree-x" });

            HashSet<string> path = RouteAnalysisEngine.CollectSourcePathRecordingIds(tree);

            Assert.NotNull(path);
            Assert.Contains("root", path);
            Assert.Contains("merged-child", path);
            Assert.Contains("post-undock", path);
        }

        [Fact]
        public void CollectSourcePathRecordingIds_ActiveSet_WalksLeafToRoot()
        {
            var tree = new RecordingTree
            {
                Id = "tree-x",
                RootRecordingId = "root",
                ActiveRecordingId = "post-undock"
            };
            tree.BranchPoints = new List<BranchPoint>
            {
                new BranchPoint
                {
                    Id = "bp-undock",
                    ParentRecordingIds = new List<string> { "merged-child" }
                },
                new BranchPoint
                {
                    Id = "bp-dock",
                    ParentRecordingIds = new List<string> { "root" }
                }
            };
            tree.AddOrReplaceRecording(new Recording { RecordingId = "root", TreeId = "tree-x" });
            tree.AddOrReplaceRecording(new Recording { RecordingId = "merged-child", TreeId = "tree-x", ParentBranchPointId = "bp-dock" });
            tree.AddOrReplaceRecording(new Recording { RecordingId = "post-undock", TreeId = "tree-x", ParentBranchPointId = "bp-undock" });

            HashSet<string> path = RouteAnalysisEngine.CollectSourcePathRecordingIds(tree);

            Assert.NotNull(path);
            Assert.Equal(3, path.Count);
            Assert.Contains("root", path);
            Assert.Contains("merged-child", path);
            Assert.Contains("post-undock", path);
        }

        [Fact]
        public void AnalyzeTree_DockUndockOnTreeWithoutActiveRecordingId_FindsWindowOnMergedChild()
        {
            // Replicate playtest 6 topology: tree has root (pre-dock) +
            // dock-merged child (carrying complete route window) + post-undock
            // survivor, and ActiveRecordingId is null because the player
            // switched vessels before committing.
            var tree = new RecordingTree
            {
                Id = "tree-playtest6",
                RootRecordingId = "ef3dacb5",
                ActiveRecordingId = null // <-- the bug condition
            };
            tree.BranchPoints = new List<BranchPoint>
            {
                new BranchPoint
                {
                    Id = "bp-dock",
                    ParentRecordingIds = new List<string> { "ef3dacb5" }
                },
                new BranchPoint
                {
                    Id = "bp-undock",
                    ParentRecordingIds = new List<string> { "daaeb89c" }
                }
            };
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "ef3dacb5",
                TreeId = "tree-playtest6",
                ParentBranchPointId = null
            });
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "daaeb89c",
                TreeId = "tree-playtest6",
                ParentBranchPointId = "bp-dock",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    BuildDeliveryWindow()
                }
            });
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "31665c16",
                TreeId = "tree-playtest6",
                ParentBranchPointId = "bp-undock"
            });

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.True(result.IsEligible,
                $"Expected route eligible, got {result.Status} — daaeb89c carries the window and must be reachable when ActiveRecordingId is null");
            Assert.Equal(RouteAnalysisStatus.Eligible, result.Status);
            Assert.Equal("daaeb89c", result.SourceRecording?.RecordingId);
        }
    }
}
