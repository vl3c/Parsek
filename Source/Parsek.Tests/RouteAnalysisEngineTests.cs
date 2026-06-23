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

        // M4a (plan D1): two completed windows at the SAME recorded DockUT are
        // genuinely unorderable, so MultipleConnectionWindows still fires (the
        // enum value is RE-PURPOSED from "multi-stop unsupported" to
        // "unorderable"). Both BuildDeliveryWindow stops default to DockUT=100,
        // so this is the duplicate-DockUT case. Distinct-DockUT multi-window
        // runs now ACCEPT (the AcceptsAndOrders test below).
        // catches: a duplicate-DockUT pair sneaking through as Eligible (no
        // deterministic stop order) instead of rejecting.
        [Fact]
        public void AnalyzeRecording_DuplicateDockUT_RejectsMultipleConnectionWindows()
        {
            Recording rec = new Recording
            {
                RecordingId = "duplicate-dock-ut",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    BuildDeliveryWindow("one"),   // DockUT 100
                    BuildDeliveryWindow("two")    // DockUT 100 -> collides
                }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.MultipleConnectionWindows, result.Status);
            // The unorderable reject names the colliding DockUT in the log.
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("unorderable windows (duplicate DockUT)") &&
                l.Contains("dockUT=100"));
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

        // M3: a window with BOTH a delivery (LiquidFuel transport->endpoint) AND
        // a resource pickup (Ore endpoint->transport) is now a MIXED window that
        // ADMITS both directions (plan D2: classify, not reject). Pre-M3 this
        // rejected MixedPickupDelivery. The delivery manifest carries the
        // LiquidFuel, the load manifest carries the Ore.
        [Fact]
        public void AnalyzeRecording_MixedResourceWindow_AdmitsBothDirections()
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

            Assert.True(result.IsEligible,
                $"a mixed resource window must admit both directions, got {result.Status}");
            Assert.Equal(50.0, result.ResourceDeliveryManifest["LiquidFuel"]);
            Assert.NotNull(result.ResourceLoadManifest);
            Assert.Equal(10.0, result.ResourceLoadManifest["Ore"]);
            // The delivery manifest must NOT carry the picked-up resource.
            Assert.False(result.ResourceDeliveryManifest.ContainsKey("Ore"));
        }

        // M3 Phase 5 (plan D7): an inventory pickup matched by an endpoint LOSS
        // (the endpoint LOSES the container AND the transport GAINS it) now
        // CLASSIFIES into the inventory load manifest and ADMITS instead of
        // rejecting MixedPickupDelivery. BuildDeliveryWindow also delivers
        // LiquidFuel, so this is a MIXED resource-delivery + inventory-pickup
        // window. Pre-M3 (and pre-Phase-5) this rejected; the inventory pickup
        // is now the sign-flip mirror of inventory delivery.
        [Fact]
        public void AnalyzeRecording_InventoryPickup_ClassifiesAndAdmits()
        {
            RouteConnectionWindow window = BuildDeliveryWindow();
            InventoryPayloadItem pickup = Payload("ore-container", "smallCargoContainer", 1, slotsTaken: 1);
            // Endpoint LOSES the container across the window (Dock has it,
            // Undock does not) AND the transport GAINS it (Dock lacks it,
            // Undock has it) -> a clean, witnessed inventory pickup.
            window.DockEndpointInventory = new List<InventoryPayloadItem> { pickup.DeepClone() };
            window.UndockEndpointInventory = null;
            window.DockTransportInventory = null;
            window.UndockTransportInventory = new List<InventoryPayloadItem> { pickup.DeepClone() };
            Recording rec = new Recording
            {
                RecordingId = "mixed-inventory",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteConnectionWindows = new List<RouteConnectionWindow> { window }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.True(result.IsEligible,
                $"a clean inventory pickup must classify and admit, got {result.Status}");
            Assert.NotNull(result.InventoryLoadManifest);
            Assert.Single(result.InventoryLoadManifest);
            Assert.Equal("ore-container", result.InventoryLoadManifest[0].IdentityHash);
            Assert.Equal(1, result.InventoryLoadManifest[0].Quantity);
        }

        // M3 Phase 5 (plan D7 / OQ3): an UNWITNESSED transport inventory gain -
        // the transport gains a container the endpoint did NOT lose - fails
        // closed window-locally (inventory is non-fungible, no harvest
        // provenance). The window rejects MixedPickupDelivery.
        [Fact]
        public void AnalyzeRecording_UnwitnessedInventoryGain_RejectsWindowLocal()
        {
            RouteConnectionWindow window = BuildDeliveryWindow();
            InventoryPayloadItem phantom = Payload("phantom-container", "smallCargoContainer", 1, slotsTaken: 1);
            // Transport GAINS a container (Dock lacks it, Undock has it) with NO
            // matching endpoint loss (the endpoint never held it) -> unwitnessed.
            window.DockTransportInventory = null;
            window.UndockTransportInventory = new List<InventoryPayloadItem> { phantom.DeepClone() };
            window.DockEndpointInventory = null;
            window.UndockEndpointInventory = null;
            Recording rec = new Recording
            {
                RecordingId = "phantom-inventory",
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

        // M3 (plan D2): a clean resource pickup (the endpoint LOSES Ore AND the
        // transport GAINS it across the window) now CLASSIFIES and admits via
        // the load manifest instead of rejecting MixedPickupDelivery. A
        // pure-pickup window (no delivery) is Eligible with only the load
        // manifest set. Pre-M3 this rejected.
        [Fact]
        public void AnalyzeRecording_ResourcePickup_ClassifiesAndAdmits()
        {
            // Clean pickup: Ore flows endpoint (10 -> 0) onto transport (0 -> 10),
            // and there is NO delivery (no transport->endpoint flow), so this is a
            // PURE-pickup window.
            var window = new RouteConnectionWindow
            {
                WindowId = "pure-pickup",
                DockUT = 100.0,
                UndockUT = 160.0,
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                DockEndpointResources = new Dictionary<string, ResourceAmount>
                {
                    ["Ore"] = new ResourceAmount { amount = 10.0, maxAmount = 50.0 }
                },
                UndockEndpointResources = new Dictionary<string, ResourceAmount>
                {
                    ["Ore"] = new ResourceAmount { amount = 0.0, maxAmount = 50.0 }
                },
                DockTransportResources = new Dictionary<string, ResourceAmount>
                {
                    ["Ore"] = new ResourceAmount { amount = 0.0, maxAmount = 50.0 }
                },
                UndockTransportResources = new Dictionary<string, ResourceAmount>
                {
                    ["Ore"] = new ResourceAmount { amount = 10.0, maxAmount = 50.0 }
                },
                EndpointAtDock = Endpoint(),
                TransferEndpointSituation = 4
            };
            Recording rec = new Recording
            {
                RecordingId = "ore-pickup",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteConnectionWindows = new List<RouteConnectionWindow> { window }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.True(result.IsEligible,
                $"a clean resource pickup must classify and admit, got {result.Status}");
            Assert.NotNull(result.ResourceLoadManifest);
            Assert.Equal(10.0, result.ResourceLoadManifest["Ore"]);
            // No delivery occurred.
            Assert.True(result.ResourceDeliveryManifest == null ||
                !result.ResourceDeliveryManifest.ContainsKey("Ore"));
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("RouteAnalysis eligible") &&
                l.Contains("load=1"));
        }

        // M3 Phase 5 (plan D7 / OQ3): the UNWITNESSED-inventory-gain reject
        // diagnostic names the culprit payload identity + the unwitnessed
        // quantity. logLines is captured by the constructor sink.
        [Fact]
        public void AnalyzeRecording_UnwitnessedInventoryGain_DiagnosticNamesIdentity()
        {
            RouteConnectionWindow window = BuildDeliveryWindow();
            InventoryPayloadItem phantom =
                Payload("ore-container", "smallCargoContainer", 1, slotsTaken: 1);
            // Transport gains it with no endpoint loss -> unwitnessed.
            window.DockTransportInventory = null;
            window.UndockTransportInventory = new List<InventoryPayloadItem> { phantom.DeepClone() };
            window.DockEndpointInventory = null;
            window.UndockEndpointInventory = null;
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
                l.Contains("unwitnessed inventory gain") &&
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

        // ===============================================================
        // M3 Phase 1 - direction generality: pickup classification, load
        // manifest, flow closure (resources). Plan
        // docs/dev/plan-logistics-m3-direction-generality.md Phase 1.
        // ===============================================================

        // ---- BuildResourceLoadManifest: the sign-flip mirror (D2) ----

        // catches: the load manifest computing the WRONG sign or failing to
        // mirror the delivery builder. A clean pickup (endpoint LOSES, transport
        // GAINS) yields loaded = min(endpointLoss, transportGain).
        [Fact]
        public void BuildResourceLoadManifest_CleanPickup_MirrorsDeliveryWithSignFlip()
        {
            RouteConnectionWindow window = BuildResourcePickupWindow("Ore", 50.0);

            Dictionary<string, double> load =
                RouteAnalysisEngine.BuildResourceLoadManifest(
                    window, "pickup-run", RouteAnalysisLogMode.Diagnostic);

            Assert.NotNull(load);
            Assert.Equal(50.0, load["Ore"]);
        }

        // catches: the load builder admitting a one-sided "pickup" where only
        // the endpoint lost the resource but the transport never gained it (or
        // vice versa). Both > epsilon is required, exactly like the delivery
        // builder needs both endpointGain AND transportLoss.
        [Fact]
        public void BuildResourceLoadManifest_OneSidedFlow_NotAdmitted()
        {
            // Endpoint loses Ore but the transport does NOT gain it (leaked /
            // vented). transportGain = 0, so no load term.
            var window = new RouteConnectionWindow
            {
                WindowId = "one-sided",
                DockUT = 10.0,
                UndockUT = 20.0,
                DockEndpointResources = OreAmount(10.0),
                UndockEndpointResources = OreAmount(0.0),
                DockTransportResources = OreAmount(0.0),
                UndockTransportResources = OreAmount(0.0)
            };

            Dictionary<string, double> load =
                RouteAnalysisEngine.BuildResourceLoadManifest(
                    window, "one-sided", RouteAnalysisLogMode.Diagnostic);

            Assert.Null(load);
        }

        // catches (D2 direction pin): an UNDEFINED resource name leaking into
        // the ADMISSION-direction load manifest. It must be EXCLUDED and logged,
        // exactly as the delivery manifest excludes it; the rejection-direction
        // HasResourcePickup keeps seeing it (the separate
        // HasResourcePickup_UndefinedResourcePickup_StillDetected pins that).
        [Fact]
        public void BuildResourceLoadManifest_UndefinedResource_ExcludedAndLogged()
        {
            ResourceTransferability.DefinitionLookupOverrideForTesting =
                CrpFixtures.DefinedLookup;
            // Karbonite (defined) AND UninstalledModResource (undefined) both
            // flow endpoint -> transport.
            RouteConnectionWindow window = BuildResourcePickupWindow(
                CrpFixtures.Karbonite, 50.0);
            window.DockEndpointResources[CrpFixtures.UninstalledModResource] =
                new ResourceAmount { amount = 30.0, maxAmount = 50.0 };
            window.UndockEndpointResources[CrpFixtures.UninstalledModResource] =
                new ResourceAmount { amount = 0.0, maxAmount = 50.0 };
            window.DockTransportResources[CrpFixtures.UninstalledModResource] =
                new ResourceAmount { amount = 0.0, maxAmount = 50.0 };
            window.UndockTransportResources[CrpFixtures.UninstalledModResource] =
                new ResourceAmount { amount = 30.0, maxAmount = 50.0 };

            Dictionary<string, double> load =
                RouteAnalysisEngine.BuildResourceLoadManifest(
                    window, "load-modless", RouteAnalysisLogMode.Diagnostic);

            Assert.NotNull(load);
            Assert.Equal(50.0, load[CrpFixtures.Karbonite]);
            Assert.False(load.ContainsKey(CrpFixtures.UninstalledModResource),
                "undefined resource must be excluded from the admission-direction load manifest");
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains($"Resource excluded: name={CrpFixtures.UninstalledModResource}") &&
                l.Contains("reason=undefined") &&
                l.Contains("recording=load-modless"));
        }

        // catches: EC/IntakeAir noise being admitted as a load term (the
        // transport recharging its batteries from the depot reads as an EC
        // "pickup"). They are environmental noise on BOTH directions.
        [Fact]
        public void BuildResourceLoadManifest_ElectricCharge_Excluded()
        {
            RouteConnectionWindow window = BuildResourcePickupWindow("Ore", 50.0);
            // EC flows endpoint -> transport too, but must not be a load term.
            window.DockEndpointResources["ElectricCharge"] =
                new ResourceAmount { amount = 400.0, maxAmount = 1000.0 };
            window.UndockEndpointResources["ElectricCharge"] =
                new ResourceAmount { amount = 0.0, maxAmount = 1000.0 };
            window.DockTransportResources["ElectricCharge"] =
                new ResourceAmount { amount = 100.0, maxAmount = 1000.0 };
            window.UndockTransportResources["ElectricCharge"] =
                new ResourceAmount { amount = 500.0, maxAmount = 1000.0 };

            Dictionary<string, double> load =
                RouteAnalysisEngine.BuildResourceLoadManifest(
                    window, "ec-load", RouteAnalysisLogMode.Diagnostic);

            Assert.NotNull(load);
            Assert.Equal(50.0, load["Ore"]);
            Assert.False(load.ContainsKey("ElectricCharge"));
        }

        // ---- AnalyzeRecording: pure-pickup admits, not NoDeliveryManifest ----

        // catches (gate fix a, D4): a PURE-pickup window (no delivery manifest)
        // rejecting at NoDeliveryManifest before reaching Eligible. The widened
        // gate ("no delivery AND no load") must let it through.
        [Fact]
        public void AnalyzeRecording_PurePickup_NotRejectedAtNoDeliveryManifest()
        {
            RouteConnectionWindow window = BuildResourcePickupWindow("Ore", 50.0);
            window.EndpointAtDock = Endpoint();
            window.TransferEndpointSituation = 4;
            Recording rec = new Recording
            {
                RecordingId = "pure-pickup-admit",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteConnectionWindows = new List<RouteConnectionWindow> { window }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.True(result.IsEligible,
                $"a pure-pickup window must not reject at NoDeliveryManifest, got {result.Status}");
            Assert.NotEqual(RouteAnalysisStatus.NoDeliveryManifest, result.Status);
            Assert.NotNull(result.ResourceLoadManifest);
            Assert.Equal(50.0, result.ResourceLoadManifest["Ore"]);
        }

        // catches: a window with NO flow in either direction failing to reject
        // NoDeliveryManifest (the widened gate must still reject when there is
        // genuinely no cargo).
        [Fact]
        public void AnalyzeRecording_NoFlowEitherDirection_RejectsNoDeliveryManifest()
        {
            var window = new RouteConnectionWindow
            {
                WindowId = "no-flow",
                DockUT = 10.0,
                UndockUT = 20.0,
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                DockTransportResources = OreAmount(100.0),
                UndockTransportResources = OreAmount(100.0),
                DockEndpointResources = OreAmount(0.0),
                UndockEndpointResources = OreAmount(0.0),
                EndpointAtDock = Endpoint(),
                TransferEndpointSituation = 4
            };
            Recording rec = new Recording
            {
                RecordingId = "no-flow",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteConnectionWindows = new List<RouteConnectionWindow> { window }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.NoDeliveryManifest, result.Status);
        }

        // catches (D2/D4 fail-closed pin): a MIXED window carrying a DEFINED-name
        // delivery PLUS an UNDEFINED-name pickup. Pre-M3 this rejected
        // MixedPickupDelivery. Now the undefined name is excluded from the load
        // manifest (hasLoad=false from that name), the defined delivery sets
        // hasDelivery=true, and the window reaches Eligible carrying ONLY the
        // delivery: the undefined pickup is silently dropped, NO phantom resource
        // routed in either direction (a mod uninstall can never conjure cargo).
        [Fact]
        public void AnalyzeRecording_MixedDefinedDeliveryUndefinedPickup_AdmitsDeliveryOnly()
        {
            ResourceTransferability.DefinitionLookupOverrideForTesting =
                CrpFixtures.DefinedLookup;
            // Defined Karbonite delivery: transport (80 -> 30) onto endpoint
            // (0 -> 50), so delivered = min(50, 50) = 50.
            RouteConnectionWindow window =
                BuildNamedResourceDeliveryWindow(CrpFixtures.Karbonite);
            // Undefined-name pickup: endpoint (10 -> 0) onto transport (0 -> 10).
            window.DockEndpointResources[CrpFixtures.UninstalledModResource] =
                new ResourceAmount { amount = 10.0, maxAmount = 50.0 };
            window.UndockEndpointResources[CrpFixtures.UninstalledModResource] =
                new ResourceAmount { amount = 0.0, maxAmount = 50.0 };
            window.DockTransportResources[CrpFixtures.UninstalledModResource] =
                new ResourceAmount { amount = 0.0, maxAmount = 50.0 };
            window.UndockTransportResources[CrpFixtures.UninstalledModResource] =
                new ResourceAmount { amount = 10.0, maxAmount = 50.0 };
            Recording rec = new Recording
            {
                RecordingId = "mixed-defined-undefined",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteConnectionWindows = new List<RouteConnectionWindow> { window }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.True(result.IsEligible,
                $"a defined delivery + undefined pickup must admit as pure delivery, got {result.Status}");
            // The defined delivery is carried.
            Assert.NotNull(result.ResourceDeliveryManifest);
            Assert.Equal(50.0, result.ResourceDeliveryManifest[CrpFixtures.Karbonite]);
            // The undefined name is NOT routed in EITHER direction (no phantom).
            Assert.False(
                result.ResourceDeliveryManifest.ContainsKey(CrpFixtures.UninstalledModResource));
            Assert.True(result.ResourceLoadManifest == null ||
                !result.ResourceLoadManifest.ContainsKey(CrpFixtures.UninstalledModResource),
                "the undefined pickup must NOT appear in the load manifest");
        }

        // catches (gate fix b, D3 item 7): a loaded (pickup) tree, with the M2
        // gain check ENGAGED (complete run manifests), false-rejecting
        // UntrackedCargoGain or failing to admit. A physically-consistent pure
        // pickup (the transport launches empty, picks up 50 at the window, ends
        // with 50) must analyze Eligible with the load manifest set. The
        // loaded-term composition keeps the gain check from tripping on the
        // pickup; the gate order (manifest build -> gain check -> closure) holds.
        [Fact]
        public void AnalyzeTree_PurePickup_GainCheckEngaged_AdmitsLoad()
        {
            // KSC origin, transport launches empty (rootStart 0 = dockTransport
            // 0, no in-transit gain so the gain check passes trivially). At the
            // window it picks up 50 Ore (endpoint 50 -> 0, transport 0 -> 50).
            // Run-end residual 50 closes the flow.
            RecordingTree tree = BuildLoadedPickupTree(
                rootStartOre: 0.0, dockTransportOre: 0.0, undockTransportOre: 50.0,
                pickupOre: 50.0, residualOre: 50.0, kscOrigin: true);

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.NotEqual(RouteAnalysisStatus.UntrackedCargoGain, result.Status);
            Assert.True(result.IsEligible,
                $"a pure-pickup tree must admit the load, got {result.Status}");
            Assert.NotNull(result.ResourceLoadManifest);
            Assert.Equal(50.0, result.ResourceLoadManifest["Ore"], 6);
        }

        // catches: an unwitnessed dock-time gain (no harvest, no prior load)
        // still rejecting UntrackedCargoGain after the M3 changes - the load-term
        // composition must NOT become a blanket gain-check disable. A KSC drill
        // run with a 120 Ore dock gain and no harvest must still reject.
        [Fact]
        public void AnalyzeTree_UnwitnessedDockGain_RejectsUntrackedCargoGain()
        {
            RecordingTree tree = BuildDrillRunTree(
                out _, out _,
                rootStartOre: 0.0, dockOre: 120.0, undockOre: 20.0,
                kscOrigin: true);
            // No harvest windows, no pickup in the window: the gain is unwitnessed.

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.UntrackedCargoGain, result.Status);
        }

        // ---- ComputeFlowClosure (D3) ----

        // byte-identity regression guard for plain KSC delivery: a delivery-only
        // run with a complete run manifest, no harvest, and no pickup must
        // CLOSE (no FlowDoesNotClose reject). Launched 100, loaded 0, harvested
        // 0, delivered 60, residual 40 -> slack = 0, exactly balanced. Closure
        // must never regress the pre-M3 plain-delivery path into a rejection.
        [Fact]
        public void ComputeFlowClosure_PlainDeliveryOnly_Closes()
        {
            FlowClosureResult closure = RouteAnalysisEngine.ComputeFlowClosure(
                OreAmountD(100.0),
                new List<Dictionary<string, double>> { null },
                null,
                new List<Dictionary<string, double>> { OreAmountD(60.0) },
                OreAmountD(40.0));

            Assert.True(closure.Closes,
                $"plain delivery-only must close, offending={closure.OffendingResource}");
            Assert.Null(closure.OffendingResource);
        }

        // catches: the closure REJECTING a legitimate fuel-burning run. A
        // POSITIVE balancing slack (consumed = launched + loaded + harvested -
        // delivered - residual > 0) is consumption, never a rejection.
        [Fact]
        public void ComputeFlowClosure_PositiveConsumptionSlack_Closes()
        {
            // launched 0, loaded 0, harvested 120, delivered 100, residual 5
            // -> slack = +15 (15 units consumed / burned). Must close.
            FlowClosureResult closure = RouteAnalysisEngine.ComputeFlowClosure(
                OreAmountD(0.0),
                new List<Dictionary<string, double>> { null },
                OreAmountD(120.0),
                new List<Dictionary<string, double>> { OreAmountD(100.0) },
                OreAmountD(5.0));

            Assert.True(closure.Closes,
                $"positive consumption slack must close, offending={closure.OffendingResource}");
        }

        // catches (D3 risk): over-delivery NOT rejecting. The transport ended
        // with more of a resource than ever arrived (delivered + residual >
        // launched + loaded + harvested) - phantom cargo. Must reject and name
        // the unaccounted quantity.
        [Fact]
        public void ComputeFlowClosure_OverDelivery_RejectsAndNamesQuantity()
        {
            // launched 0, loaded 0, harvested 120, delivered 100, residual 50
            // -> slack = -30: delivered + residual (150) exceeds arrived (120).
            FlowClosureResult closure = RouteAnalysisEngine.ComputeFlowClosure(
                OreAmountD(0.0),
                new List<Dictionary<string, double>> { null },
                OreAmountD(120.0),
                new List<Dictionary<string, double>> { OreAmountD(100.0) },
                OreAmountD(50.0));

            Assert.False(closure.Closes);
            Assert.Equal("Ore", closure.OffendingResource);
            Assert.Equal(30.0, closure.UnaccountedQuantity, 6);
            Assert.Equal("Ore: 30.0 over-delivered", closure.RejectDetail);
        }

        // catches: the loaded term not counting toward the arrived side. A
        // pickup that exactly accounts for the residual must close.
        [Fact]
        public void ComputeFlowClosure_LoadedTermCovers_Closes()
        {
            // launched 0, loaded 50, harvested 0, delivered 0, residual 50
            // -> slack = 0. The loaded cargo accounts for the residual.
            FlowClosureResult closure = RouteAnalysisEngine.ComputeFlowClosure(
                OreAmountD(0.0),
                new List<Dictionary<string, double>> { OreAmountD(50.0) },
                null,
                new List<Dictionary<string, double>> { null },
                OreAmountD(50.0));

            Assert.True(closure.Closes);
        }

        // catches: the per-window list NOT summing. Two delivery windows summing
        // to over-delivery must reject (the length-1 list shape generalizes to
        // M4 multi-window by SUM, not rewrite).
        [Fact]
        public void ComputeFlowClosure_MultiWindowSum_OverDeliveryAcrossWindows()
        {
            // launched 100, harvested 0, loaded 0, residual 50, but two delivery
            // windows of 60 each = 120 delivered. delivered + residual = 170 >
            // arrived 100 -> over-delivered 70.
            FlowClosureResult closure = RouteAnalysisEngine.ComputeFlowClosure(
                OreAmountD(100.0),
                new List<Dictionary<string, double>> { null, null },
                null,
                new List<Dictionary<string, double>> { OreAmountD(60.0), OreAmountD(60.0) },
                OreAmountD(50.0));

            Assert.False(closure.Closes);
            Assert.Equal(70.0, closure.UnaccountedQuantity, 6);
        }

        // catches (M3 Phase 6, the mixed-window closure case): the SAME resource
        // appearing in BOTH the loaded AND the delivered window list for one window
        // not summing BOTH terms into the balance. A mixed window that delivers 60
        // Ore AND picks up 50 Ore at one dock must count the loaded 50 on the
        // arrived side and the delivered 60 on the departed side; ComputeFlowClosure
        // already takes both as LISTS and sums per-name, so a same-resource mixed
        // window closes when the terms balance. launched 100, loaded 50,
        // harvested 0, delivered 60, residual 90 -> slack = 100 + 50 - 60 - 90 = 0.
        // The pickup raises the arrived side so the elevated residual (the run kept
        // the picked-up cargo aboard) is fully accounted - no phantom over-delivery.
        [Fact]
        public void ComputeFlowClosure_MixedSameResourceWindow_BothTermsBalance()
        {
            FlowClosureResult closure = RouteAnalysisEngine.ComputeFlowClosure(
                OreAmountD(100.0),                                                 // launched
                new List<Dictionary<string, double>> { OreAmountD(50.0) },         // loaded (pickup)
                null,                                                              // harvested
                new List<Dictionary<string, double>> { OreAmountD(60.0) },         // delivered
                OreAmountD(90.0));                                                  // residual

            Assert.True(closure.Closes,
                $"a balanced same-resource mixed window must close, offending={closure.OffendingResource}");
            Assert.Null(closure.OffendingResource);
        }

        // catches (M3 Phase 6 inverse): the loaded term on a mixed window NOT
        // covering an elevated residual, so a real over-delivery slips through. If
        // the SAME-resource mixed window's loaded + launched + harvested cannot
        // cover delivered + residual, closure must still reject and name the
        // unaccounted quantity. launched 0, loaded 50, harvested 0, delivered 60,
        // residual 50 -> slack = 50 - 60 - 50 = -60: delivered + residual (110)
        // exceeds arrived (50). Over-delivered 60.
        [Fact]
        public void ComputeFlowClosure_MixedSameResourceWindow_OverDelivery_Rejects()
        {
            FlowClosureResult closure = RouteAnalysisEngine.ComputeFlowClosure(
                OreAmountD(0.0),
                new List<Dictionary<string, double>> { OreAmountD(50.0) },         // loaded (pickup)
                null,
                new List<Dictionary<string, double>> { OreAmountD(60.0) },         // delivered
                OreAmountD(50.0));                                                  // residual

            Assert.False(closure.Closes);
            Assert.Equal("Ore", closure.OffendingResource);
            Assert.Equal(60.0, closure.UnaccountedQuantity, 6);
            Assert.Equal("Ore: 60.0 over-delivered", closure.RejectDetail);
        }

        // catches: EC/IntakeAir participating in the balance. They are
        // environmental noise on every term and must be skipped even if a
        // spurious delta would otherwise read as over-delivery.
        [Fact]
        public void ComputeFlowClosure_ElectricChargeNoise_Ignored()
        {
            var residual = new Dictionary<string, double> { ["ElectricCharge"] = 999.0 };
            FlowClosureResult closure = RouteAnalysisEngine.ComputeFlowClosure(
                null,
                new List<Dictionary<string, double>> { null },
                null,
                new List<Dictionary<string, double>> { null },
                residual);

            Assert.True(closure.Closes,
                "ElectricCharge must not participate in the flow balance");
        }

        // catches: IntakeAir participating in the balance. Sibling of the
        // ElectricCharge case - both are IsAlwaysIgnored environmental noise and
        // must be skipped on every term even when a spurious delta would read as
        // over-delivery. A residual-only IntakeAir spike must still close.
        [Fact]
        public void ComputeFlowClosure_IntakeAirNoise_Ignored()
        {
            var residual = new Dictionary<string, double> { ["IntakeAir"] = 999.0 };
            FlowClosureResult closure = RouteAnalysisEngine.ComputeFlowClosure(
                null,
                new List<Dictionary<string, double>> { null },
                null,
                new List<Dictionary<string, double>> { null },
                residual);

            Assert.True(closure.Closes,
                "IntakeAir must not participate in the flow balance");
        }

        // ---- AnalyzeTree closure end-to-end (presence-gated) ----

        // catches: an over-delivering run analyzing Eligible instead of
        // FlowDoesNotClose, and the reject not threading the RejectDetail + log.
        // End to end through AnalyzeTree: the window delivers LiquidFuel cleanly
        // (so it clears the NoDeliveryManifest gate and the M2 gain check), but
        // the transport's post-window ORE residual (150) exceeds what ever
        // arrived (120 harvested) - a phantom 30 the dock-time gain check misses
        // and the full-run closure catches.
        [Fact]
        public void AnalyzeTree_OverDelivery_RejectsFlowDoesNotClose()
        {
            // Anchor (root) launched 50 LiquidFuel + 0 Ore. Drills 120 Ore
            // (harvest-covered). The colony window: deliver 50 LiquidFuel
            // (transport 50 -> 0, endpoint 0 -> 50) AND end with 150 Ore aboard
            // (dock-transport 120, undock-transport 150) with NO Ore pickup
            // (endpoint Ore flat) -> 30 Ore phantom over-delivery.
            var root = new Recording
            {
                RecordingId = "od-root",
                TreeId = "tree-od",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 500.0,
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { TransportPid },
                    StartTransportResources = new Dictionary<string, ResourceAmount>
                    {
                        ["LiquidFuel"] = RA(50.0),
                        ["Ore"] = RA(0.0)
                    },
                    EndTransportResources = new Dictionary<string, ResourceAmount>
                    {
                        ["LiquidFuel"] = RA(0.0),
                        ["Ore"] = RA(150.0)
                    },
                    EndCaptured = true
                },
                RouteHarvestWindows = new List<RouteHarvestWindow>
                {
                    OreHarvestWindow(150.0, 400.0, 0.0, 120.0)
                }
            };
            var merge = new Recording
            {
                RecordingId = "od-merge",
                TreeId = "tree-od",
                ExplicitStartUT = 500.0,
                ExplicitEndUT = 600.0,
                ParentBranchPointId = "bp-dock",
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { TransportPid, ColonyPid },
                    StartTransportResources = OreAmount(120.0),
                    EndTransportResources = OreAmount(150.0),
                    EndCaptured = true
                },
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "od-window",
                        DockUT = 500.0,
                        UndockUT = 600.0,
                        TransferTargetVesselPid = 9001,
                        TransferKind = RouteConnectionKind.DockingPort,
                        TransportPartPersistentIds = new List<uint> { TransportPid },
                        EndpointPartPersistentIds = new List<uint> { ColonyPid },
                        DockTransportResources = new Dictionary<string, ResourceAmount>
                        {
                            ["LiquidFuel"] = RA(50.0),
                            ["Ore"] = RA(120.0)
                        },
                        UndockTransportResources = new Dictionary<string, ResourceAmount>
                        {
                            ["LiquidFuel"] = RA(0.0),
                            ["Ore"] = RA(150.0)
                        },
                        DockEndpointResources = new Dictionary<string, ResourceAmount>
                        {
                            ["LiquidFuel"] = RA(0.0),
                            ["Ore"] = RA(0.0)
                        },
                        UndockEndpointResources = new Dictionary<string, ResourceAmount>
                        {
                            ["LiquidFuel"] = RA(50.0),
                            ["Ore"] = RA(0.0)
                        },
                        EndpointAtDock = Endpoint(),
                        TransferEndpointSituation = 1
                    }
                }
            };
            var tree = new RecordingTree
            {
                Id = "tree-od",
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

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.FlowDoesNotClose, result.Status);
            Assert.Equal("Ore: 30.0 over-delivered", result.RejectDetail);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("flow does not close") &&
                l.Contains("resource=Ore") &&
                l.Contains("unaccounted=30"));
        }

        // catches: the presence gate not skipping closure on a DEGRADED lineage.
        // A pickup run with NO complete run manifests must still classify the
        // pickup window-locally (admit the load) and NOT run closure (which
        // would need the launched/residual terms it lacks) - never drop the
        // pickup by falling back to delivery-only.
        [Fact]
        public void AnalyzeTree_DegradedLineage_ClassifiesPickupWindowLocally()
        {
            // A pickup run with no RouteRunManifest anywhere (pre-M2 / degraded):
            // the harvest gain check returns LegacyFallback, so closure is
            // skipped, but the window-direction classification still admits the
            // pickup window-locally. Pure pickup: dock-transport 0, undock 50,
            // endpoint 50 -> 0.
            RecordingTree tree = BuildLoadedPickupTree(
                rootStartOre: 0.0, dockTransportOre: 0.0, undockTransportOre: 50.0,
                pickupOre: 50.0, residualOre: 50.0, kscOrigin: true);
            foreach (Recording r in tree.Recordings.Values)
                r.RouteRunManifest = null; // degrade the whole lineage

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.True(result.IsEligible,
                $"a degraded-lineage pickup must classify window-locally, got {result.Status}");
            Assert.NotNull(result.ResourceLoadManifest);
            Assert.Equal(50.0, result.ResourceLoadManifest["Ore"], 6);
            // Closure did not engage (no run manifests), so no harvest data.
            Assert.Null(result.HarvestedManifest);
        }

        // ===============================================================
        // M4a Phase A1 - multi-window acceptance + ordering + run-level
        // closure. Plan docs/dev/plan-logistics-m4-shape-generality.md
        // Phase A1 (D1 / D2 / D3).
        // ===============================================================

        // catches (D1): a multi-window run rejecting MultipleConnectionWindows
        // (the pre-M4 behavior) instead of accepting + ordering the stops, and
        // the per-stop list not being populated in ascending DockUT order. Two
        // distinct-DockUT delivery windows on one recording (KSC origin, no run
        // manifests so the harvest check stays legacy) must analyze Eligible
        // with Stops ordered by DockUT ascending.
        [Fact]
        public void AnalyzeRecording_TwoDeliveryWindows_AcceptsAndOrdersByDockUT()
        {
            Recording rec = new Recording
            {
                RecordingId = "two-stop-delivery",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    // Listed out of order: DockUT 300 before DockUT 100.
                    BuildDeliveryWindowAt("late", dockUT: 300.0, undockUT: 360.0),
                    BuildDeliveryWindowAt("early", dockUT: 100.0, undockUT: 160.0)
                }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.True(result.IsEligible,
                $"a distinct-DockUT multi-window run must accept, got {result.Status}");
            Assert.NotNull(result.Stops);
            Assert.Equal(2, result.Stops.Count);
            // Ordered ascending by DockUT.
            Assert.Equal(100.0, result.Stops[0].DockUT);
            Assert.Equal(300.0, result.Stops[1].DockUT);
            Assert.Equal("early", result.Stops[0].ConnectionWindow.WindowId);
            Assert.Equal("late", result.Stops[1].ConnectionWindow.WindowId);
            // Scalar fields mirror the FIRST (anchor) stop so existing consumers
            // and a single-stop build keep working in A1.
            Assert.Same(result.Stops[0].ConnectionWindow, result.ConnectionWindow);
            Assert.Equal(50.0, result.Stops[0].ResourceDeliveryManifest["LiquidFuel"]);
            Assert.Equal(50.0, result.Stops[1].ResourceDeliveryManifest["LiquidFuel"]);
            // The accept+order summary names the count + sorted DockUT order.
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("windows accepted+ordered") &&
                l.Contains("count=2"));
        }

        // catches (D1): the sort being a no-op / depending on input order. A
        // reversed-DockUT input list must still produce ascending stop order.
        // AnalyzeTree iterates an UNORDERED HashSet, so the deterministic sort is
        // mandatory; this pins it via the single-recording entry (same ordering
        // path) with a deliberately descending input list.
        [Fact]
        public void AnalyzeRecording_ReversedDockUTInput_StillOrdersAscending()
        {
            Recording rec = new Recording
            {
                RecordingId = "reversed-input",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    BuildDeliveryWindowAt("c", dockUT: 500.0, undockUT: 560.0),
                    BuildDeliveryWindowAt("b", dockUT: 300.0, undockUT: 360.0),
                    BuildDeliveryWindowAt("a", dockUT: 100.0, undockUT: 160.0)
                }
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.True(result.IsEligible, $"got {result.Status}");
            Assert.Equal(3, result.Stops.Count);
            Assert.Equal(100.0, result.Stops[0].DockUT);
            Assert.Equal(300.0, result.Stops[1].DockUT);
            Assert.Equal(500.0, result.Stops[2].DockUT);
        }

        // catches (D3): over-delivery summed across MULTIPLE delivery windows not
        // rejecting FlowDoesNotClose. With complete run manifests the run-level
        // closure feeds ALL N delivered windows; two deliveries that together
        // exceed what ever arrived must reject naming the unaccounted quantity.
        // (If the closure folded only one window it would close and slip through.)
        [Fact]
        public void AnalyzeTree_TwoDeliveryWindows_OverDeliveryAcrossWindows_RejectsFlowDoesNotClose()
        {
            // KSC origin, launched 100 Ore, no harvest. Two delivery windows of
            // 60 Ore each (120 delivered total) and the transport still ends with
            // 30 Ore residual -> delivered + residual (150) exceeds arrived (100):
            // 50 Ore over-delivered. A single-window fold (60 delivered) would
            // close (60 + 30 <= 100); the SUM (120) catches it.
            RecordingTree tree = BuildTwoDeliveryWindowTree(
                launchedOre: 100.0,
                window1DeliverOre: 60.0, window1DockUT: 100.0,
                window2DeliverOre: 60.0, window2DockUT: 300.0,
                residualOre: 30.0);

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.False(result.IsEligible);
            Assert.Equal(RouteAnalysisStatus.FlowDoesNotClose, result.Status);
            Assert.Equal("Ore: 50.0 over-delivered", result.RejectDetail);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("flow does not close") &&
                l.Contains("resource=Ore") &&
                l.Contains("unaccounted=50"));
        }

        // catches (D3, the multi-window covered-sum): a legit multi-PICKUP run
        // false-rejecting UntrackedCargoGain because the harvest "covered" term
        // folded only ONE window's load. Three pickups of 50 each; the gain check
        // anchors on the LAST window, whose dock holds the 100 accumulated from
        // the first two pickups (gain = 100 - 0). Covered only when ALL THREE
        // loads sum (150 >= 100); folding one window (50) would false-reject.
        [Fact]
        public void AnalyzeTree_MultiPickup_CompleteManifest_NoFalseUntrackedCargoGain()
        {
            RecordingTree tree = BuildThreePickupTree();

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.NotEqual(RouteAnalysisStatus.UntrackedCargoGain, result.Status);
            Assert.True(result.IsEligible,
                $"a witnessed multi-pickup run must admit, got {result.Status}");
            Assert.Equal(3, result.Stops.Count);
            // Every stop carries its own 50-unit load manifest.
            for (int i = 0; i < result.Stops.Count; i++)
            {
                Assert.NotNull(result.Stops[i].ResourceLoadManifest);
                Assert.Equal(50.0, result.Stops[i].ResourceLoadManifest["Ore"], 6);
            }
        }

        // catches (D2, no-regression): a SINGLE-window run analyzing differently
        // than pre-M4. The scalar fields must be unchanged (a single-stop build
        // is byte-identical) and Stops must carry exactly one entry mirroring the
        // scalars.
        [Fact]
        public void AnalyzeRecording_SingleWindow_ByteIdenticalScalarsPlusOneStop()
        {
            Recording rec = new Recording
            {
                RecordingId = "single-window",
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
            // Scalar fields unchanged from the pre-M4 single-window result.
            Assert.Same(rec, result.SourceRecording);
            Assert.Equal(50.0, result.ResourceDeliveryManifest["LiquidFuel"]);
            Assert.Single(result.InventoryDeliveryManifest);
            Assert.Equal("evaJetpack", result.InventoryDeliveryManifest[0].PartName);
            // Exactly one stop, mirroring the scalars.
            Assert.NotNull(result.Stops);
            Assert.Single(result.Stops);
            Assert.Same(result.ConnectionWindow, result.Stops[0].ConnectionWindow);
            Assert.Same(result.ResourceDeliveryManifest, result.Stops[0].ResourceDeliveryManifest);
            Assert.Equal(100.0, result.Stops[0].DockUT);
        }

        // Delivery window at an explicit DockUT/UndockUT (M4a multi-window
        // fixtures). Same clean LiquidFuel delivery (50 = min(endpointGain,
        // transportLoss)) + inventory as BuildDeliveryWindow, parameterized so a
        // test can place several windows at distinct recorded phases.
        private static RouteConnectionWindow BuildDeliveryWindowAt(
            string id, double dockUT, double undockUT)
        {
            InventoryPayloadItem item = Payload("payload-hash", "evaJetpack", 1, slotsTaken: 1);
            return new RouteConnectionWindow
            {
                WindowId = id,
                DockUT = dockUT,
                UndockUT = undockUT,
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

        // Two-delivery-window tree (M4a closure fixture): KSC-origin root with a
        // complete run manifest (launched/residual Ore) + a dock-merge child
        // carrying TWO Ore delivery windows at distinct DockUTs. Both legs carry
        // complete manifests so the run-level closure engages and is fed BOTH
        // delivered windows.
        private static RecordingTree BuildTwoDeliveryWindowTree(
            double launchedOre,
            double window1DeliverOre, double window1DockUT,
            double window2DeliverOre, double window2DockUT,
            double residualOre)
        {
            var root = new Recording
            {
                RecordingId = "md-root",
                TreeId = "tree-md",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 50.0,
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { TransportPid },
                    StartTransportResources = OreAmount(launchedOre),
                    EndTransportResources = OreAmount(residualOre),
                    EndCaptured = true
                }
            };
            var merge = new Recording
            {
                RecordingId = "md-merge",
                TreeId = "tree-md",
                ExplicitStartUT = 50.0,
                ExplicitEndUT = window2DockUT + 60.0,
                ParentBranchPointId = "bp-dock",
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { TransportPid, ColonyPid },
                    StartTransportResources = OreAmount(launchedOre),
                    EndTransportResources = OreAmount(launchedOre),
                    EndCaptured = true
                },
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    // window1: deliver window1DeliverOre, transport ends at 0 (its
                    // undock is ignored by the closure; only the LAST window's
                    // undock is the residual).
                    OreDeliveryWindowAt(window1DockUT,
                        dockTransport: window1DeliverOre, undockTransport: 0.0,
                        deliverOre: window1DeliverOre),
                    // window2 (last): deliver window2DeliverOre AND leave
                    // residualOre aboard - the closure's residual term.
                    OreDeliveryWindowAt(window2DockUT,
                        dockTransport: window2DeliverOre + residualOre, undockTransport: residualOre,
                        deliverOre: window2DeliverOre)
                }
            };
            var tree = new RecordingTree
            {
                Id = "tree-md",
                RootRecordingId = root.RecordingId,
                ActiveRecordingId = merge.RecordingId
            };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(merge);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock",
                UT = 50.0,
                Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { root.RecordingId },
                ChildRecordingIds = new List<string> { merge.RecordingId }
            });
            return tree;
        }

        // An Ore delivery window with explicit transport corners: the endpoint
        // gains deliverOre (0 -> deliverOre) and the transport drops from
        // dockTransport to undockTransport. delivered = min(deliverOre,
        // dockTransport - undockTransport); the caller picks corners so
        // transportLoss == deliverOre. The LAST window's undockTransport is the
        // run-end residual the closure reads.
        private static RouteConnectionWindow OreDeliveryWindowAt(
            double dockUT, double dockTransport, double undockTransport, double deliverOre)
        {
            return new RouteConnectionWindow
            {
                WindowId = "ore-window-" + dockUT.ToString(CultureInfo.InvariantCulture),
                DockUT = dockUT,
                UndockUT = dockUT + 60.0,
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                TransportPartPersistentIds = new List<uint> { TransportPid },
                EndpointPartPersistentIds = new List<uint> { ColonyPid },
                DockTransportResources = OreAmount(dockTransport),
                UndockTransportResources = OreAmount(undockTransport),
                DockEndpointResources = OreAmount(0.0),
                UndockEndpointResources = OreAmount(deliverOre),
                EndpointAtDock = Endpoint(),
                TransferEndpointSituation = 1
            };
        }

        // Three-pickup tree (M4a covered-sum fixture): KSC-origin transport
        // launches empty, picks up 50 Ore at each of three windows (DockUTs 100 /
        // 200 / 300), ends with 150 aboard. The LAST window's DOCK transport
        // holds 100 (the first two pickups), so the gain check sees a 100 gain
        // that is covered only when ALL THREE loads sum (150). Complete run
        // manifests so the harvest check engages.
        private static RecordingTree BuildThreePickupTree()
        {
            var root = new Recording
            {
                RecordingId = "tp-root",
                TreeId = "tree-tp",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 50.0,
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { TransportPid },
                    StartTransportResources = OreAmount(0.0),
                    EndTransportResources = OreAmount(150.0),
                    EndCaptured = true
                }
            };
            var merge = new Recording
            {
                RecordingId = "tp-merge",
                TreeId = "tree-tp",
                ExplicitStartUT = 50.0,
                ExplicitEndUT = 360.0,
                ParentBranchPointId = "bp-dock",
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { TransportPid, ColonyPid },
                    StartTransportResources = OreAmount(0.0),
                    EndTransportResources = OreAmount(0.0),
                    EndCaptured = true
                },
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    // dock-transport accumulates 0 -> 50 -> 100 as the run picks up.
                    OrePickupWindowAt(100.0, dockTransport: 0.0, undockTransport: 50.0, pickup: 50.0),
                    OrePickupWindowAt(200.0, dockTransport: 50.0, undockTransport: 100.0, pickup: 50.0),
                    OrePickupWindowAt(300.0, dockTransport: 100.0, undockTransport: 150.0, pickup: 50.0)
                }
            };
            var tree = new RecordingTree
            {
                Id = "tree-tp",
                RootRecordingId = root.RecordingId,
                ActiveRecordingId = merge.RecordingId
            };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(merge);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock",
                UT = 50.0,
                Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { root.RecordingId },
                ChildRecordingIds = new List<string> { merge.RecordingId }
            });
            return tree;
        }

        // An Ore pickup window: endpoint (pickup -> 0) onto transport
        // (dockTransport -> undockTransport). Pure pickup, no delivery.
        private static RouteConnectionWindow OrePickupWindowAt(
            double dockUT, double dockTransport, double undockTransport, double pickup)
        {
            return new RouteConnectionWindow
            {
                WindowId = "pickup-" + dockUT.ToString(CultureInfo.InvariantCulture),
                DockUT = dockUT,
                UndockUT = dockUT + 30.0,
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                TransportPartPersistentIds = new List<uint> { TransportPid },
                EndpointPartPersistentIds = new List<uint> { ColonyPid },
                DockEndpointResources = OreAmount(pickup),
                UndockEndpointResources = OreAmount(0.0),
                DockTransportResources = OreAmount(dockTransport),
                UndockTransportResources = OreAmount(undockTransport),
                EndpointAtDock = Endpoint(),
                TransferEndpointSituation = 1
            };
        }

        // A pickup window: Ore flows endpoint (amount -> 0) onto transport
        // (0 -> amount). Pure pickup, no delivery.
        private static RouteConnectionWindow BuildResourcePickupWindow(
            string resourceName, double amount)
        {
            return new RouteConnectionWindow
            {
                WindowId = "pickup-window",
                DockUT = 100.0,
                UndockUT = 160.0,
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                DockEndpointResources = new Dictionary<string, ResourceAmount>
                {
                    [resourceName] = new ResourceAmount { amount = amount, maxAmount = amount * 2.0 }
                },
                UndockEndpointResources = new Dictionary<string, ResourceAmount>
                {
                    [resourceName] = new ResourceAmount { amount = 0.0, maxAmount = amount * 2.0 }
                },
                DockTransportResources = new Dictionary<string, ResourceAmount>
                {
                    [resourceName] = new ResourceAmount { amount = 0.0, maxAmount = amount * 2.0 }
                },
                UndockTransportResources = new Dictionary<string, ResourceAmount>
                {
                    [resourceName] = new ResourceAmount { amount = amount, maxAmount = amount * 2.0 }
                },
                EndpointAtDock = Endpoint(),
                TransferEndpointSituation = 4
            };
        }

        private static Dictionary<string, double> OreAmountD(double amount)
        {
            return new Dictionary<string, double> { ["Ore"] = amount };
        }

        // A pickup-flavored drill-run tree: root (transport) -> Dock BP -> merge
        // child carrying a pickup window (Ore flows endpoint -> transport).
        // Both legs carry complete run manifests so the M2/M3 closure engages.
        // The root anchor manifest Start=rootStartOre, End=residualOre. The
        // window's transport corners (dockTransportOre / undockTransportOre) and
        // the endpoint pickup (pickupOre -> 0) are set independently so a test
        // can model an elevated dock-transport with or without a witnessed
        // pickup.
        private static RecordingTree BuildLoadedPickupTree(
            double rootStartOre, double dockTransportOre, double undockTransportOre,
            double pickupOre, double residualOre, bool kscOrigin)
        {
            var root = new Recording
            {
                RecordingId = "pickup-root",
                TreeId = "tree-pickup",
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 500.0,
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { TransportPid },
                    StartTransportResources = OreAmount(rootStartOre),
                    EndTransportResources = OreAmount(residualOre),
                    EndCaptured = true
                }
            };
            if (kscOrigin)
            {
                root.StartBodyName = "Kerbin";
                root.LaunchSiteName = "LaunchPad";
            }
            var merge = new Recording
            {
                RecordingId = "pickup-merge",
                TreeId = "tree-pickup",
                ExplicitStartUT = 500.0,
                ExplicitEndUT = 600.0,
                ParentBranchPointId = "bp-dock",
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { TransportPid, ColonyPid },
                    StartTransportResources = OreAmount(dockTransportOre),
                    EndTransportResources = OreAmount(dockTransportOre),
                    EndCaptured = true
                },
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "pickup-window",
                        DockUT = 500.0,
                        UndockUT = 600.0,
                        TransferTargetVesselPid = 9001,
                        TransferKind = RouteConnectionKind.DockingPort,
                        TransportPartPersistentIds = new List<uint> { TransportPid },
                        EndpointPartPersistentIds = new List<uint> { ColonyPid },
                        // Pickup: endpoint (pickupOre -> 0), transport
                        // (dockTransportOre -> undockTransportOre).
                        DockEndpointResources = OreAmount(pickupOre),
                        UndockEndpointResources = OreAmount(0.0),
                        DockTransportResources = OreAmount(dockTransportOre),
                        UndockTransportResources = OreAmount(undockTransportOre),
                        EndpointAtDock = Endpoint(),
                        TransferEndpointSituation = 1
                    }
                }
            };
            var tree = new RecordingTree
            {
                Id = "tree-pickup",
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
    }
}
