using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;
using Parsek.Tests.Generators;
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
            // The M2 cases install a definition-lookup seam; start and end
            // every test with the production default (headless null library
            // treats all names as defined, so legacy fixtures are unaffected).
            ResourceTransferability.ResetForTesting();
        }

        public void Dispose()
        {
            ResourceTransferability.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void AnalyzeRecording_CompletedWindow_ExtractsDeliveryManifest()
        {
            Recording rec = new Recording
            {
                RecordingId = "route-source",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
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
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
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
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
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
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
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

        // ---------------------------------------------------------------
        // M1 undocked-start workflow gate (design D7)
        // ---------------------------------------------------------------
        // A run whose ORIGIN recording proves neither a KSC launch
        // (LaunchSiteName + StartBodyName == "Kerbin") nor a start-docked
        // origin partner (RouteOriginProof pid) started undocked with cargo
        // already aboard: the cargo's source was never witnessed, so analysis
        // rejects it with workflow guidance instead of letting it surface as a
        // candidate that fails at create time with endpoint-missing.

        // catches: an undocked-start single recording analyzing Eligible (the
        // pre-M1 behavior) instead of the workflow rejection.
        [Fact]
        public void AnalyzeRecording_UndockedStart_Rejected()
        {
            Recording rec = new Recording
            {
                RecordingId = "undocked-start",
                // No LaunchSiteName / StartBodyName, no RouteOriginProof.
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    BuildDeliveryWindow()
                }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.UndockedStartOrigin, result.Status);
            // Populated like the sibling rejects so the near-miss row can render.
            Assert.Same(rec, result.SourceRecording);
            Assert.NotNull(result.ConnectionWindow);
        }

        // catches: the rejection diagnostic not naming the origin recording and
        // the absent proofs (the log is the debugging surface for a confusing
        // "why is my run rejected" report).
        [Fact]
        public void AnalyzeRecording_UndockedStart_DiagnosticNamesOriginRecording()
        {
            Recording rec = new Recording
            {
                RecordingId = "undocked-diag",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    BuildDeliveryWindow()
                }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.UndockedStartOrigin, result.Status);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("undocked-start origin") &&
                l.Contains("originRec=undocked-diag") &&
                l.Contains("launchSite=<none>") &&
                l.Contains("originProof=no"));
        }

        // catches: a tree whose ROOT (the origin recording) lacks both origin
        // proofs analyzing Eligible. The window lives on the dock child; the
        // gate must classify off the root, not the source.
        [Fact]
        public void AnalyzeTree_UndockedStartOrigin_Rejected()
        {
            RecordingTree tree = BuildTwoRecordingTree(out Recording source);
            // Root deliberately carries NO origin fields.

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.UndockedStartOrigin, result.Status);
            Assert.Same(source, result.SourceRecording);
        }

        // catches: the gate firing on a legitimate KSC launch (root carries a
        // Kerbin launch site).
        [Fact]
        public void AnalyzeTree_KscOrigin_StillEligible()
        {
            RecordingTree tree = BuildTwoRecordingTree(out _);
            Recording root = tree.Recordings["root"];
            root.StartBodyName = "Kerbin";
            root.LaunchSiteName = "LaunchPad";

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.True(result.IsEligible,
                $"KSC-origin tree must stay eligible, got {result.Status}");
        }

        // catches: the gate firing on a captured start-docked depot origin
        // (root carries a RouteOriginProof with a partner pid).
        [Fact]
        public void AnalyzeTree_DockedProofOrigin_StillEligible()
        {
            RecordingTree tree = BuildTwoRecordingTree(out _);
            Recording root = tree.Recordings["root"];
            root.RouteOriginProof = new RouteOriginProof
            {
                StartDockedOriginVesselPid = 7777
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.True(result.IsEligible,
                $"docked-proof-origin tree must stay eligible, got {result.Status}");
        }

        // Two-recording tree: root (the origin recording, NO origin fields
        // unless the test adds them) + dock child carrying the eligible window.
        private static RecordingTree BuildTwoRecordingTree(out Recording source)
        {
            Recording root = new Recording { RecordingId = "root" };
            source = new Recording
            {
                RecordingId = "source",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    BuildDeliveryWindow()
                }
            };
            RecordingTree tree = new RecordingTree { Id = "tree-origin-gate" };
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
            return tree;
        }

        [Fact]
        public void AnalyzeTree_FindsCompletedWindowRecording()
        {
            // The tree ROOT is the origin recording for the M1 undocked-start
            // gate; give it a KSC origin so the tree stays eligible.
            Recording root = new Recording
            {
                RecordingId = "root",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad"
            };
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
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
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
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
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
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
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
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
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
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
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
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
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
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteConnectionWindows = new List<RouteConnectionWindow> { window }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.MixedPickupDelivery, result.Status);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("mixed pickup/delivery") &&
                l.Contains("inventory=ore-container"));
        }

        // ---------------------------------------------------------------
        // M2 transferability rule (plan D1/D2): CRP-style mod resources,
        // undefined names, zero-cost defined resources
        // ---------------------------------------------------------------
        // Any defined resource is routable; an UNDEFINED name (uninstalled
        // mod) is excluded from the ADMISSION-direction delivery manifest and
        // logged, but stays visible to the REJECTION-direction pickup gate so
        // a mod uninstall can never flip a rejection into Eligible.

        // catches: a defined mod resource failing to deliver (the M2 point:
        // the pipeline must be name-agnostic for every defined resource).
        [Fact]
        public void BuildResourceDeliveryManifest_CrpNames_Deliver()
        {
            ResourceTransferability.DefinitionLookupOverrideForTesting =
                CrpFixtures.DefinedLookup;
            RouteConnectionWindow window = BuildNamedResourceDeliveryWindow(
                CrpFixtures.Karbonite, CrpFixtures.Supplies, CrpFixtures.Uraninite);

            Dictionary<string, double> manifest =
                RouteAnalysisEngine.BuildResourceDeliveryManifest(
                    window, "crp-run", RouteAnalysisLogMode.Diagnostic);

            Assert.NotNull(manifest);
            Assert.Equal(3, manifest.Count);
            Assert.Equal(50.0, manifest[CrpFixtures.Karbonite]);
            Assert.Equal(50.0, manifest[CrpFixtures.Supplies]);
            Assert.Equal(50.0, manifest[CrpFixtures.Uraninite]);
        }

        // catches: an undefined name flowing into the delivery manifest (and
        // from there into CostManifest / the funds charge) instead of being
        // excluded and logged with the recording id.
        [Fact]
        public void BuildResourceDeliveryManifest_UndefinedResource_ExcludedAndLogged()
        {
            ResourceTransferability.DefinitionLookupOverrideForTesting =
                CrpFixtures.DefinedLookup;
            RouteConnectionWindow window = BuildNamedResourceDeliveryWindow(
                CrpFixtures.Karbonite, CrpFixtures.UninstalledModResource);

            Dictionary<string, double> manifest =
                RouteAnalysisEngine.BuildResourceDeliveryManifest(
                    window, "modless-run", RouteAnalysisLogMode.Diagnostic);

            Assert.NotNull(manifest);
            Assert.Equal(50.0, manifest[CrpFixtures.Karbonite]);
            Assert.False(manifest.ContainsKey(CrpFixtures.UninstalledModResource),
                "undefined resource must be excluded from the admission-direction manifest");
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains($"Resource excluded: name={CrpFixtures.UninstalledModResource}") &&
                l.Contains("reason=undefined") &&
                l.Contains("recording=modless-run"));
        }

        // catches: a zero-cost defined resource (transferability never
        // consults cost) being dropped from the manifest.
        [Fact]
        public void BuildResourceDeliveryManifest_ZeroCostDefinedResource_StillDelivers()
        {
            ResourceTransferability.DefinitionLookupOverrideForTesting =
                CrpFixtures.DefinedLookup;
            RouteConnectionWindow window =
                BuildNamedResourceDeliveryWindow(CrpFixtures.MetallicOre);

            Dictionary<string, double> manifest =
                RouteAnalysisEngine.BuildResourceDeliveryManifest(
                    window, "zero-cost-run", RouteAnalysisLogMode.Diagnostic);

            Assert.NotNull(manifest);
            Assert.Equal(50.0, manifest[CrpFixtures.MetallicOre]);
        }

        // catches (D2 direction pin, review finding 8): the undefined-name
        // skip leaking into the REJECTION-direction pickup gate. An
        // undefined-name pickup must STILL be detected - excluding it would
        // let a resource-mod uninstall flip a recorded MixedPickupDelivery
        // rejection into Eligible (fail-open).
        [Fact]
        public void HasResourcePickup_UndefinedResourcePickup_StillDetected()
        {
            ResourceTransferability.DefinitionLookupOverrideForTesting =
                CrpFixtures.DefinedLookup;
            // Undefined resource flows endpoint -> transport: endpoint loses
            // 10, transport gains 10 across the window.
            var window = new RouteConnectionWindow
            {
                WindowId = "undefined-pickup",
                DockUT = 10.0,
                UndockUT = 20.0,
                DockEndpointResources = new Dictionary<string, ResourceAmount>
                {
                    [CrpFixtures.UninstalledModResource] =
                        new ResourceAmount { amount = 10.0, maxAmount = 50.0 }
                },
                UndockEndpointResources = new Dictionary<string, ResourceAmount>
                {
                    [CrpFixtures.UninstalledModResource] =
                        new ResourceAmount { amount = 0.0, maxAmount = 50.0 }
                },
                DockTransportResources = new Dictionary<string, ResourceAmount>
                {
                    [CrpFixtures.UninstalledModResource] =
                        new ResourceAmount { amount = 0.0, maxAmount = 50.0 }
                },
                UndockTransportResources = new Dictionary<string, ResourceAmount>
                {
                    [CrpFixtures.UninstalledModResource] =
                        new ResourceAmount { amount = 10.0, maxAmount = 50.0 }
                }
            };

            bool pickup = RouteAnalysisEngine.HasResourcePickup(window, out string reason);

            Assert.True(pickup,
                "undefined-name pickup must stay visible to the rejection-direction gate");
            Assert.Contains(CrpFixtures.UninstalledModResource, reason);
        }

        // catches: the D2 degrade contract - a delivery whose ONLY resource
        // is undefined must reject as NoDeliveryManifest (no phantom route),
        // not analyze Eligible or throw.
        [Fact]
        public void AnalyzeRecording_UndefinedOnlyDelivery_RejectsNoDelivery()
        {
            ResourceTransferability.DefinitionLookupOverrideForTesting =
                CrpFixtures.DefinedLookup;
            Recording rec = new Recording
            {
                RecordingId = "undefined-only",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    BuildNamedResourceDeliveryWindow(CrpFixtures.UninstalledModResource)
                }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.NoDeliveryManifest, result.Status);
        }

        // catches: a CRP-resource run failing end to end through AnalyzeRecording
        // (the engine-level twin of the direct manifest test).
        [Fact]
        public void AnalyzeRecording_CrpDelivery_Eligible()
        {
            ResourceTransferability.DefinitionLookupOverrideForTesting =
                CrpFixtures.DefinedLookup;
            Recording rec = new Recording
            {
                RecordingId = "crp-delivery",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    BuildNamedResourceDeliveryWindow(
                        CrpFixtures.Karbonite, CrpFixtures.MetallicOre)
                }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.True(result.IsEligible,
                $"CRP delivery must analyze Eligible, got {result.Status}");
            Assert.Equal(50.0, result.ResourceDeliveryManifest[CrpFixtures.Karbonite]);
            Assert.Equal(50.0, result.ResourceDeliveryManifest[CrpFixtures.MetallicOre]);
        }

        // catches: the undefined-name skip spamming Info from the ~1/second
        // candidate sweep (Quiet mode must route through the shared
        // rate-limited Verbose key, M2 logging plan row 1).
        [Fact]
        public void AnalyzeRecording_UndefinedResource_QuietMode_SweepLogIsRateLimitedVerbose()
        {
            ResourceTransferability.DefinitionLookupOverrideForTesting =
                CrpFixtures.DefinedLookup;
            ParsekLog.VerboseOverrideForTesting = true;
            Recording rec = new Recording
            {
                RecordingId = "quiet-sweep",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    BuildNamedResourceDeliveryWindow(
                        CrpFixtures.Karbonite, CrpFixtures.UninstalledModResource)
                }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(
                rec, RouteAnalysisLogMode.Quiet);

            Assert.True(result.IsEligible);
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]") &&
                l.Contains($"Resource excluded: name={CrpFixtures.UninstalledModResource}") &&
                l.Contains("recording=quiet-sweep"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[INFO]") && l.Contains("Resource excluded"));
        }

        // Clean delivery window for the given resource names: each flows
        // transport (80 -> 30) to endpoint (0 -> 50), so delivered =
        // min(endpointGain, transportLoss) = 50 per resource. No inventory.
        private static RouteConnectionWindow BuildNamedResourceDeliveryWindow(
            params string[] resourceNames)
        {
            var dockTransport = new Dictionary<string, ResourceAmount>();
            var undockTransport = new Dictionary<string, ResourceAmount>();
            var dockEndpoint = new Dictionary<string, ResourceAmount>();
            var undockEndpoint = new Dictionary<string, ResourceAmount>();
            foreach (string name in resourceNames)
            {
                dockTransport[name] = new ResourceAmount { amount = 80.0, maxAmount = 100.0 };
                undockTransport[name] = new ResourceAmount { amount = 30.0, maxAmount = 100.0 };
                dockEndpoint[name] = new ResourceAmount { amount = 0.0, maxAmount = 200.0 };
                undockEndpoint[name] = new ResourceAmount { amount = 50.0, maxAmount = 200.0 };
            }

            return new RouteConnectionWindow
            {
                WindowId = "named-resource-window",
                DockUT = 100.0,
                UndockUT = 160.0,
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                DockTransportResources = dockTransport,
                UndockTransportResources = undockTransport,
                DockEndpointResources = dockEndpoint,
                UndockEndpointResources = undockEndpoint,
                EndpointAtDock = Endpoint(),
                TransferEndpointSituation = 4
            };
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
        // M2 harvest provenance (plan D6): the gain check engages when the
        // transport lineage carries complete run manifests; the undocked-start
        // verdict is then DEFERRED until after the gain check (two-phase gate,
        // plan finding 11), and a fully-harvest-covered undocked delivery
        // becomes Eligible as a harvest-origin run (D7).
        // ---------------------------------------------------------------

        private const uint TransportPid = 100;
        private const uint ColonyPid = 300;

        private static ResourceAmount RA(double amount)
        {
            return new ResourceAmount { amount = amount, maxAmount = 1000.0 };
        }

        private static Dictionary<string, ResourceAmount> OreAmount(double amount)
        {
            return new Dictionary<string, ResourceAmount> { ["Ore"] = RA(amount) };
        }

        private static RouteRunCargoManifest CompleteManifest(
            uint[] pids, double startOre, double endOre)
        {
            return new RouteRunCargoManifest
            {
                TransportPartPersistentIds = new List<uint>(pids),
                StartTransportResources = OreAmount(startOre),
                EndTransportResources = OreAmount(endOre),
                EndCaptured = true
            };
        }

        // Transport-scoped ore delivery window at the colony: dock with
        // dockOre aboard, deliver (dockOre - undockOre) to the endpoint.
        private static RouteConnectionWindow OreDeliveryWindow(
            double dockOre, double undockOre)
        {
            return new RouteConnectionWindow
            {
                WindowId = "ore-window",
                DockUT = 500.0,
                UndockUT = 600.0,
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                TransportPartPersistentIds = new List<uint> { TransportPid },
                EndpointPartPersistentIds = new List<uint> { ColonyPid },
                DockTransportResources = OreAmount(dockOre),
                UndockTransportResources = OreAmount(undockOre),
                DockEndpointResources = OreAmount(0.0),
                UndockEndpointResources = OreAmount(dockOre - undockOre),
                EndpointAtDock = Endpoint(),
                TransferEndpointSituation = 1
            };
        }

        private static RouteHarvestWindow OreHarvestWindow(
            double startUT, double endUT, double startOre, double endOre)
        {
            return new RouteHarvestWindow
            {
                WindowId = "hw",
                StartUT = startUT,
                EndUT = endUT,
                StartTransportResources = OreAmount(startOre),
                EndTransportResources = OreAmount(endOre),
                BodyName = "Minmus",
                Latitude = 5.0,
                Longitude = 6.0,
                Altitude = 7.0,
                SituationAtOpen = 1
            };
        }

        // Drill-run tree: root (the transport; KSC fields optional) -> Dock
        // BP -> merge child carrying the delivery window. Both legs carry
        // complete run manifests so the M2 gain check engages.
        private static RecordingTree BuildDrillRunTree(
            out Recording root, out Recording merge,
            double rootStartOre, double dockOre, double undockOre,
            bool kscOrigin)
        {
            root = new Recording
            {
                RecordingId = "drill-root",
                TreeId = "tree-drill",
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 500.0,
                RouteRunManifest = CompleteManifest(
                    new[] { TransportPid }, rootStartOre, dockOre)
            };
            if (kscOrigin)
            {
                root.StartBodyName = "Kerbin";
                root.LaunchSiteName = "LaunchPad";
            }
            merge = new Recording
            {
                RecordingId = "drill-merge",
                TreeId = "tree-drill",
                ExplicitStartUT = 500.0,
                ExplicitEndUT = 600.0,
                ParentBranchPointId = "bp-dock",
                RouteRunManifest = CompleteManifest(
                    new[] { TransportPid, ColonyPid }, dockOre, dockOre),
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    OreDeliveryWindow(dockOre, undockOre)
                }
            };
            var tree = new RecordingTree
            {
                Id = "tree-drill",
                RootRecordingId = root.RecordingId,
                ActiveRecordingId = merge.RecordingId
            };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(merge);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock",
                UT = 500.0,
                Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { root.RecordingId },
                ChildRecordingIds = new List<string> { merge.RecordingId }
            });
            return tree;
        }

        // The scenario-family-4 pin: an undocked-start drill run whose
        // delivery is FULLY covered by witnessed harvest analyzes Eligible
        // as a harvest-origin run instead of rejecting UndockedStartOrigin.
        [Fact]
        public void AnalyzeTree_DrillRun_UndockedStart_FullyHarvested_Eligible()
        {
            RecordingTree tree = BuildDrillRunTree(
                out Recording root, out _,
                rootStartOre: 0.0, dockOre: 120.0, undockOre: 20.0,
                kscOrigin: false);
            root.RouteHarvestWindows = new List<RouteHarvestWindow>
            {
                OreHarvestWindow(150.0, 400.0, 0.0, 120.0)
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.True(result.IsEligible,
                $"fully-harvested drill run must be Eligible, got {result.Status}");
            Assert.True(result.IsHarvestOrigin);
            Assert.Equal(100.0, result.ResourceDeliveryManifest["Ore"], 6);
            Assert.NotNull(result.HarvestedManifest);
            Assert.Equal(120.0, result.HarvestedManifest["Ore"], 6);
            Assert.NotNull(result.FirstHarvestWindow);
            Assert.Equal("Minmus", result.FirstHarvestWindow.BodyName);
        }

        // catches: the refined gate admitting an undocked start whose
        // delivery EXCEEDS the witnessed harvest (start cargo of unknown
        // provenance) - it must keep rejecting UndockedStartOrigin.
        [Fact]
        public void AnalyzeTree_DrillRun_PartiallyHarvested_UndockedStartOrigin()
        {
            // Starts undocked with 50 Ore already aboard (gain check passes:
            // gain = 100 - 50 = 50, all witnessed) but delivers 80 > 50
            // harvested - the 30 difference is the unwitnessed start cargo.
            RecordingTree tree = BuildDrillRunTree(
                out Recording root, out _,
                rootStartOre: 50.0, dockOre: 100.0, undockOre: 20.0,
                kscOrigin: false);
            root.RouteHarvestWindows = new List<RouteHarvestWindow>
            {
                OreHarvestWindow(150.0, 400.0, 50.0, 100.0)
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.UndockedStartOrigin, result.Status);
            Assert.False(result.IsHarvestOrigin);
        }

        // catches: a KSC drill run whose harvested ore was silently treated
        // as launch cargo (the pre-M2 behavior) - an unwitnessed gain must
        // reject with the exact quantity named.
        [Fact]
        public void AnalyzeTree_KscOrigin_UntrackedGain_Rejected()
        {
            RecordingTree tree = BuildDrillRunTree(
                out _, out _,
                rootStartOre: 0.0, dockOre: 120.0, undockOre: 20.0,
                kscOrigin: true);
            // No harvest windows: the 120 Ore aboard at dock has no source.

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.UntrackedCargoGain, result.Status);
            Assert.Equal("Ore: 120.0 gained, 0.0 harvested", result.RejectDetail);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("untracked cargo gain") &&
                l.Contains("resource=Ore") &&
                l.Contains("gained=120"));
        }

        // catches: a witnessed KSC drill run failing the gain check, or the
        // harvest data flipping its origin classification (KSC stays KSC).
        [Fact]
        public void AnalyzeTree_KscOrigin_HarvestedGain_Eligible()
        {
            RecordingTree tree = BuildDrillRunTree(
                out Recording root, out _,
                rootStartOre: 0.0, dockOre: 120.0, undockOre: 20.0,
                kscOrigin: true);
            root.RouteHarvestWindows = new List<RouteHarvestWindow>
            {
                OreHarvestWindow(150.0, 400.0, 0.0, 120.0)
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.True(result.IsEligible,
                $"witnessed KSC drill run must be Eligible, got {result.Status}");
            Assert.False(result.IsHarvestOrigin);
            Assert.NotNull(result.HarvestedManifest);
            Assert.Equal(120.0, result.HarvestedManifest["Ore"], 6);
        }

        // Regression pin (plan risk 2): a recording WITHOUT harvest data -
        // every pre-M2 recording - must analyze exactly as today, even when
        // the transport visibly gained a resource across the run. The gain
        // check is presence-gated and must never reject legacy data.
        [Fact]
        public void AnalyzeTree_OldRecordingNoHarvestData_AnalyzesAsToday()
        {
            RecordingTree tree = BuildDrillRunTree(
                out Recording root, out Recording merge,
                rootStartOre: 0.0, dockOre: 120.0, undockOre: 20.0,
                kscOrigin: true);
            // Strip the M2 data: pre-M2 recordings carry neither manifests
            // nor windows.
            root.RouteRunManifest = null;
            merge.RouteRunManifest = null;

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.True(result.IsEligible,
                $"pre-M2 recording must keep analyzing Eligible, got {result.Status}");
            Assert.Null(result.HarvestedManifest);
            Assert.False(result.IsHarvestOrigin);
            Assert.Equal(100.0, result.ResourceDeliveryManifest["Ore"], 6);
        }

        // catches: the legacy path losing today's rejection ORDER - an
        // undocked start without harvest data must still reject
        // UndockedStartOrigin (not NoDeliveryManifest or anything later).
        [Fact]
        public void AnalyzeTree_OldRecordingUndockedStart_StillRejectsUndockedStartOrigin()
        {
            RecordingTree tree = BuildDrillRunTree(
                out Recording root, out Recording merge,
                rootStartOre: 0.0, dockOre: 120.0, undockOre: 20.0,
                kscOrigin: false);
            root.RouteRunManifest = null;
            merge.RouteRunManifest = null;

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.UndockedStartOrigin, result.Status);
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
                ParentBranchPointId = null,
                // Root = origin recording for the M1 undocked-start gate.
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad"
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
