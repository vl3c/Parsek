using System;
using System.Collections.Generic;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Claw/grapple connection producer tests
    /// (docs/dev/design-logistics-claw-producer.md). Covers the pure
    /// classification core, the producer kind admission gate, the
    /// empty-grapple-window skip, the mid-run grab tree shape, kind threading
    /// through BuildMergeBranchData, codec round-trips, proof-hash pins, and
    /// the reject formatter text.
    /// </summary>
    [Collection("Sequential")]
    public class ClawProducerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ClawProducerTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ResourceTransferability.ResetForTesting();
        }

        public void Dispose()
        {
            ResourceTransferability.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---------------------------------------------------------------
        // ConnectionProducerClassifier.ClassifyCore truth table (design 2.1)
        // ---------------------------------------------------------------

        [Theory]
        // dock on both ends -> DockingPort
        [InlineData(true, true, false, false, (int)RouteConnectionKind.DockingPort)]
        // grapple on either end -> Grapple, regardless of the other side
        [InlineData(false, false, true, false, (int)RouteConnectionKind.Grapple)]
        [InlineData(false, false, false, true, (int)RouteConnectionKind.Grapple)]
        [InlineData(false, false, true, true, (int)RouteConnectionKind.Grapple)]
        // a claw grabbing a docking-port PART is still a grapple (dock module
        // on one end only, so no dock pair forms)
        [InlineData(true, false, false, true, (int)RouteConnectionKind.Grapple)]
        [InlineData(false, true, true, false, (int)RouteConnectionKind.Grapple)]
        // DOCK PAIR wins over an incidental grapple module (modded combo part
        // docking normally; review fix - wrong-guess direction is stricter)
        [InlineData(true, true, true, false, (int)RouteConnectionKind.DockingPort)]
        [InlineData(true, true, false, true, (int)RouteConnectionKind.DockingPort)]
        // dock module on only ONE end and no grapple -> not a dock: Unknown
        [InlineData(true, false, false, false, (int)RouteConnectionKind.Unknown)]
        [InlineData(false, true, false, false, (int)RouteConnectionKind.Unknown)]
        // no recognized producer module anywhere -> Unknown
        [InlineData(false, false, false, false, (int)RouteConnectionKind.Unknown)]
        public void ClassifyCore_TruthTable(
            bool fromDock, bool toDock, bool fromGrapple, bool toGrapple,
            int expected)
        {
            Assert.Equal((RouteConnectionKind)expected,
                ConnectionProducerClassifier.ClassifyCore(
                    fromDock, toDock, fromGrapple, toGrapple));
        }

        // ---------------------------------------------------------------
        // Admission kind gate (design 2.2)
        // ---------------------------------------------------------------

        [Theory]
        [InlineData((int)RouteConnectionKind.None, false)]
        [InlineData((int)RouteConnectionKind.DockingPort, false)]
        [InlineData((int)RouteConnectionKind.Grapple, false)]
        [InlineData((int)RouteConnectionKind.StockCrossfeed, true)]
        [InlineData((int)RouteConnectionKind.Unknown, true)]
        public void IsUnsupportedConnectionKind_TruthTable(
            int kind, bool expected)
        {
            Assert.Equal(expected,
                RouteAnalysisEngine.IsUnsupportedConnectionKind(
                    (RouteConnectionKind)kind));
        }

        [Fact]
        public void AnalyzeRecording_GrappleWindowWithTransfer_Eligible()
        {
            Recording rec = KscRecordingWithWindows(
                DeliveryWindow("grapple-transfer", RouteConnectionKind.Grapple));

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.True(result.IsEligible,
                $"grapple window with a real transfer must be eligible, got {result.Status}");
            Assert.Equal(50.0, result.ResourceDeliveryManifest["LiquidFuel"]);
        }

        [Fact]
        public void AnalyzeRecording_UnknownKind_RejectsUnsupportedConnectionKind()
        {
            Recording rec = KscRecordingWithWindows(
                DeliveryWindow("modded-couple", RouteConnectionKind.Unknown));

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.UnsupportedConnectionKind, result.Status);
            Assert.Equal("Unknown", result.RejectDetail);
            Assert.Contains(logLines, l =>
                l.Contains("unsupported connection kind") && l.Contains("kind=Unknown"));
        }

        [Fact]
        public void AnalyzeRecording_StockCrossfeedKind_RejectsUnsupportedConnectionKind()
        {
            Recording rec = KscRecordingWithWindows(
                DeliveryWindow("crossfeed", RouteConnectionKind.StockCrossfeed));

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.UnsupportedConnectionKind, result.Status);
            Assert.Equal("StockCrossfeed", result.RejectDetail);
        }

        // ---------------------------------------------------------------
        // Empty-grapple-window skip (design 2.2 / 4.2)
        // ---------------------------------------------------------------

        [Fact]
        public void AnalyzeRecording_EmptyGrapplePlusDockDelivery_EligibleOneStop()
        {
            // The asteroid-mining shape: a structural grab (zero transfer at
            // the corners) followed by a station delivery. The grab must skip
            // as a non-stop; the delivery is the route.
            Recording rec = KscRecordingWithWindows(
                EmptyWindow("grab", RouteConnectionKind.Grapple, dockUT: 100.0),
                DeliveryWindow("station", RouteConnectionKind.DockingPort, dockUT: 200.0));

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.True(result.IsEligible,
                $"empty grapple + dock delivery must be eligible, got {result.Status}");
            Assert.NotNull(result.Stops);
            Assert.Single(result.Stops);
            Assert.Equal("station", result.Stops[0].ConnectionWindow.WindowId);
            Assert.Equal(50.0, result.ResourceDeliveryManifest["LiquidFuel"]);
            Assert.Contains(logLines, l =>
                l.Contains("skipped 1 empty grapple window"));
        }

        [Fact]
        public void AnalyzeRecording_OnlyEmptyGrappleWindows_RejectsNoDelivery()
        {
            // Pure grab-and-release run: nothing transferred anywhere is still
            // not a route (design 4.3).
            Recording rec = KscRecordingWithWindows(
                EmptyWindow("grab-1", RouteConnectionKind.Grapple, dockUT: 100.0),
                EmptyWindow("grab-2", RouteConnectionKind.Grapple, dockUT: 200.0));

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.NoDeliveryManifest, result.Status);
            // The reject anchors the FIRST skipped window as context.
            Assert.Equal("grab-1", result.ConnectionWindow.WindowId);
            Assert.Contains(logLines, l =>
                l.Contains("no stop-bearing window after empty-grapple skip"));
        }

        [Fact]
        public void AnalyzeRecording_EmptyDockWindow_StillRejectsNoDelivery()
        {
            // Contrast pin: the empty-window reject is UNCHANGED for docks
            // (an empty dock window is a workflow smell, not a structural grab).
            Recording rec = KscRecordingWithWindows(
                EmptyWindow("dead-dock", RouteConnectionKind.DockingPort, dockUT: 100.0));

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.NoDeliveryManifest, result.Status);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("empty grapple window"));
        }

        [Fact]
        public void AnalyzeRecording_GrapplePickupWindow_EligibleAsLoad()
        {
            // A grapple window where cargo DID move (fuel pumped from a
            // derelict onto the transport) builds a load manifest through the
            // untouched builders and is a normal stop.
            RouteConnectionWindow window = EmptyWindow(
                "derelict-siphon", RouteConnectionKind.Grapple, dockUT: 100.0);
            window.DockEndpointResources = Manifest(60.0, 100.0);
            window.UndockEndpointResources = Manifest(10.0, 100.0);
            window.DockTransportResources = Manifest(0.0, 100.0);
            window.UndockTransportResources = Manifest(50.0, 100.0);
            Recording rec = KscRecordingWithWindows(window);

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.True(result.IsEligible,
                $"grapple pickup must be eligible, got {result.Status}");
            Assert.NotNull(result.ResourceLoadManifest);
            Assert.Equal(50.0, result.ResourceLoadManifest["LiquidFuel"]);
        }

        // ---------------------------------------------------------------
        // Mid-run grab across recordings (design 4.2): the grab creates a
        // merge boundary, so the empty grapple window lives on a merged CHILD
        // recording and the delivery on a later one; M4a's cross-recording
        // collection plus the skip make the whole run analyze Eligible.
        // ---------------------------------------------------------------

        [Fact]
        public void AnalyzeTree_MidRunGrabThenDockDelivery_Eligible()
        {
            Recording root = new Recording
            {
                RecordingId = "root",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad"
            };
            Recording grabChild = new Recording
            {
                RecordingId = "grab-child",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    EmptyWindow("asteroid-grab", RouteConnectionKind.Grapple, dockUT: 100.0)
                }
            };
            Recording dockChild = new Recording
            {
                RecordingId = "dock-child",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    DeliveryWindow("station-delivery", RouteConnectionKind.DockingPort, dockUT: 300.0)
                }
            };

            RecordingTree tree = new RecordingTree { Id = "mid-run-grab" };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(grabChild);
            tree.AddOrReplaceRecording(dockChild);
            tree.RootRecordingId = root.RecordingId;
            tree.ActiveRecordingId = dockChild.RecordingId;
            grabChild.ParentBranchPointId = "bp-grab";
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-grab",
                ParentRecordingIds = new List<string> { root.RecordingId },
                ChildRecordingIds = new List<string> { grabChild.RecordingId }
            });
            dockChild.ParentBranchPointId = "bp-dock";
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock",
                ParentRecordingIds = new List<string> { grabChild.RecordingId },
                ChildRecordingIds = new List<string> { dockChild.RecordingId }
            });

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.True(result.IsEligible,
                $"mid-run grab + delivery tree must be eligible, got {result.Status}");
            Assert.Single(result.Stops);
            Assert.Equal("station-delivery", result.Stops[0].ConnectionWindow.WindowId);
        }

        // ---------------------------------------------------------------
        // Kind threading through the merge branch data (design 2.1)
        // ---------------------------------------------------------------

        [Fact]
        public void BuildMergeBranchData_StampsGrappleKind()
        {
            var (_, child) = ParsekFlight.BuildMergeBranchData(
                new List<string> { "parent" }, "tree", 100.0,
                BranchPointType.Dock, 42, "Merged",
                targetVesselPersistentId: 9001,
                transferKind: RouteConnectionKind.Grapple);

            Assert.Equal(RouteConnectionKind.Grapple, child.TransferKind);
            Assert.Equal(9001u, child.TransferTargetVesselPid);
        }

        [Fact]
        public void BuildMergeBranchData_NoneKindKeepsDockingPortDefault()
        {
            // The None -> DockingPort fallback is what keeps pre-classifier
            // call sites and old recordings meaning "dock".
            var (_, child) = ParsekFlight.BuildMergeBranchData(
                new List<string> { "parent" }, "tree", 100.0,
                BranchPointType.Dock, 42, "Merged",
                targetVesselPersistentId: 9001,
                transferKind: RouteConnectionKind.None);

            Assert.Equal(RouteConnectionKind.DockingPort, child.TransferKind);
        }

        [Fact]
        public void BuildMergeBranchData_UnknownKindStampedTruthfully()
        {
            // An unrecognized producer must stay Unknown on the recording so
            // admission fails closed; it must NOT degrade to DockingPort.
            var (_, child) = ParsekFlight.BuildMergeBranchData(
                new List<string> { "parent" }, "tree", 100.0,
                BranchPointType.Dock, 42, "Merged",
                targetVesselPersistentId: 9001,
                transferKind: RouteConnectionKind.Unknown);

            Assert.Equal(RouteConnectionKind.Unknown, child.TransferKind);
        }

        [Fact]
        public void BuildMergeBranchData_BoardBranchKindNone()
        {
            var (_, child) = ParsekFlight.BuildMergeBranchData(
                new List<string> { "parent" }, "tree", 100.0,
                BranchPointType.Board, 42, "Merged",
                targetVesselPersistentId: 9001,
                transferKind: RouteConnectionKind.Grapple);

            Assert.Equal(RouteConnectionKind.None, child.TransferKind);
        }

        // ---------------------------------------------------------------
        // Codec round-trips (design 6): Grapple and Unknown survive
        // serialize -> deserialize on the connection window.
        // ---------------------------------------------------------------

        [Theory]
        [InlineData((int)RouteConnectionKind.Grapple)]
        [InlineData((int)RouteConnectionKind.Unknown)]
        public void RouteProofCodec_WindowKindRoundTrips(int kindValue)
        {
            RouteConnectionKind kind = (RouteConnectionKind)kindValue;
            Recording rec = new Recording
            {
                RecordingId = "codec-src",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    DeliveryWindow("w", kind)
                }
            };

            var node = new ConfigNode("TEST");
            RouteProofCodec.SerializeRouteProofMetadata(node, rec);
            Recording restored = new Recording { RecordingId = "codec-dst" };
            RouteProofCodec.DeserializeRouteProofMetadata(node, restored);

            Assert.NotNull(restored.RouteConnectionWindows);
            Assert.Single(restored.RouteConnectionWindows);
            Assert.Equal(kind, restored.RouteConnectionWindows[0].TransferKind);
        }

        // ---------------------------------------------------------------
        // Proof-hash pins (design 6). transferKind IS hashed
        // (RouteProofHasher writes (int)TransferKind), so a Grapple window
        // hashes differently from the same window stamped DockingPort, and
        // the Grapple shape gets its own byte-stability pin. Pre-existing
        // recordings are stamped DockingPort and keep their pinned hash
        // (Hash_PreM3Recording_ByteStable, untouched).
        // ---------------------------------------------------------------

        [Fact]
        public void Hash_GrappleWindow_DiffersFromDockWindow()
        {
            Recording dockRec = KscRecordingWithWindows(
                DeliveryWindow("w", RouteConnectionKind.DockingPort));
            Recording grappleRec = KscRecordingWithWindows(
                DeliveryWindow("w", RouteConnectionKind.Grapple));

            Assert.NotEqual(
                RouteProofHasher.ComputeRouteProofHashFromRecording(dockRec),
                RouteProofHasher.ComputeRouteProofHashFromRecording(grappleRec));
        }

        [Fact]
        public void Hash_GrappleWindow_ByteStable()
        {
            Recording rec = KscRecordingWithWindows(
                DeliveryWindow("w", RouteConnectionKind.Grapple));

            Assert.Equal(
                "4723234fb2d3bf50",
                RouteProofHasher.ComputeRouteProofHashFromRecording(rec));
        }

        // ---------------------------------------------------------------
        // PotatoRoid snapshot part-name path (design 6): the asteroid part
        // name carries no underscores, so the snapshot suffix-strip yields the
        // exact PartLoader name and the underscore-dot conversion is a no-op.
        // Pins the headless half; live PartLoader resolution + ghost-visual
        // build are the in-game/operator half (LogisticsGrapple category).
        // ---------------------------------------------------------------

        [Theory]
        [InlineData("PotatoRoid", "PotatoRoid")]
        [InlineData("PotatoRoid_4294590964", "PotatoRoid")]
        [InlineData("PotatoComet_123456789", "PotatoComet")]
        public void TryExtractPartName_AsteroidSnapshotNames(string raw, string expected)
        {
            Assert.Equal(expected, GhostVisualBuilder.TryExtractPartName(raw));
        }

        // ---------------------------------------------------------------
        // Formatter text (design 5)
        // ---------------------------------------------------------------

        [Fact]
        public void FormatRejectMessage_UnsupportedConnectionKind_NamesKind()
        {
            string msg = RouteCreationFormatters.FormatRejectMessage(
                RouteAnalysisStatus.UnsupportedConnectionKind, "Unknown");

            Assert.Contains("connection type", msg);
            Assert.Contains("(Unknown)", msg);
            Assert.Contains("claw-grappled", msg);
        }

        [Fact]
        public void FormatRejectMessage_UnsupportedConnectionKind_NoDetail()
        {
            string msg = RouteCreationFormatters.FormatRejectMessage(
                RouteAnalysisStatus.UnsupportedConnectionKind, null);

            Assert.Contains("connection type", msg);
            Assert.DoesNotContain("()", msg);
        }

        // ---------------------------------------------------------------
        // Review-pass fail-closed refinements (design 2.2)
        // ---------------------------------------------------------------

        [Fact]
        public void AnalyzeRecording_ProoflessEmptyGrapplePlusDelivery_Eligible()
        {
            // Review fix 3: a proof-less STRUCTURAL grab (endpoint capture
            // failed at couple time) skips as a non-stop instead of rejecting
            // the whole run with MissingEndpointProof.
            RouteConnectionWindow grab = EmptyWindow("proofless-grab",
                RouteConnectionKind.Grapple, dockUT: 100.0);
            grab.EndpointAtDock = null;
            grab.TransferEndpointSituation = -1;
            Recording rec = KscRecordingWithWindows(
                grab,
                DeliveryWindow("station", RouteConnectionKind.DockingPort, dockUT: 200.0));

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.True(result.IsEligible,
                $"proof-less structural grab must skip, got {result.Status}");
            Assert.Single(result.Stops);
        }

        [Fact]
        public void AnalyzeRecording_ProoflessEmptyDockWindow_StillRejectsEndpointProof()
        {
            // Contrast pin: dock windows always survive the filter, so a
            // proof-less dock window keeps its MissingEndpointProof reject.
            RouteConnectionWindow deadDock = EmptyWindow("proofless-dock",
                RouteConnectionKind.DockingPort, dockUT: 100.0);
            deadDock.EndpointAtDock = null;
            deadDock.TransferEndpointSituation = -1;
            Recording rec = KscRecordingWithWindows(
                deadDock,
                DeliveryWindow("station", RouteConnectionKind.DockingPort, dockUT: 200.0));

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.MissingEndpointProof, result.Status);
        }

        [Fact]
        public void AnalyzeRecording_EmptyGrappleWithInventoryGain_RejectsMixedPickup()
        {
            // Review fix 1: an unwitnessed transport INVENTORY gain on an
            // otherwise-empty grapple window must reject exactly as the
            // per-window gate would have, not slip past via the skip.
            RouteConnectionWindow grab = EmptyWindow("inventory-grab",
                RouteConnectionKind.Grapple, dockUT: 100.0);
            grab.DockTransportInventory = null;
            grab.UndockTransportInventory = new List<InventoryPayloadItem>
            {
                new InventoryPayloadItem
                {
                    IdentityHash = "phantom",
                    PartName = "smallCargoContainer",
                    Quantity = 1,
                    SlotsTaken = 1
                }
            };
            Recording rec = KscRecordingWithWindows(
                grab,
                DeliveryWindow("station", RouteConnectionKind.DockingPort, dockUT: 200.0));

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.MixedPickupDelivery, result.Status);
            Assert.Contains(logLines, l =>
                l.Contains("unwitnessed inventory gain on structural grapple window"));
        }

        [Fact]
        public void AnalyzeRecording_GrappleUnmatchedGain_LegacyTree_RejectsUntrackedGain()
        {
            // Review fix 2: an unmatched transport resource gain inside a
            // skipped grapple window (drilling-while-grappled shape) fails
            // closed on a legacy tree (no complete run manifests): nothing
            // downstream can witness the gain.
            RouteConnectionWindow grab = EmptyWindow("drill-grab",
                RouteConnectionKind.Grapple, dockUT: 100.0);
            grab.DockTransportResources = OreManifest(0.0);
            grab.UndockTransportResources = OreManifest(120.0);
            Recording rec = KscRecordingWithWindows(
                grab,
                DeliveryWindow("station", RouteConnectionKind.DockingPort, dockUT: 200.0));

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.UntrackedCargoGain, result.Status);
            Assert.Contains("Ore", result.RejectDetail);
            Assert.Contains(logLines, l =>
                l.Contains("unmatched transport gain in skipped grapple window on legacy tree"));
        }

        [Fact]
        public void AnalyzeTree_GrappleDrillGain_EngagedManifests_Eligible()
        {
            // The asteroid-mining shape end to end WITH the gain machinery
            // engaged: complete run manifests + a harvest window witness the
            // Ore gained during the (skipped) grapple window, so the run
            // analyzes Eligible with the station delivery as its one stop.
            RouteConnectionWindow grab = EmptyWindow("asteroid-grab",
                RouteConnectionKind.Grapple, dockUT: 450.0);
            grab.DockTransportResources = OreManifest(0.0);
            grab.UndockTransportResources = OreManifest(120.0);

            RouteConnectionWindow delivery = new RouteConnectionWindow
            {
                WindowId = "ore-delivery",
                DockUT = 500.0,
                UndockUT = 600.0,
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                TransportPartPersistentIds = new List<uint> { 100 },
                EndpointPartPersistentIds = new List<uint> { 300 },
                DockTransportResources = OreManifest(120.0),
                UndockTransportResources = OreManifest(20.0),
                DockEndpointResources = OreManifest(0.0),
                UndockEndpointResources = OreManifest(100.0),
                EndpointAtDock = Endpoint(),
                TransferEndpointSituation = 1
            };

            Recording root = new Recording
            {
                RecordingId = "mine-root",
                TreeId = "tree-mine",
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 500.0,
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { 100 },
                    StartTransportResources = OreManifest(0.0),
                    EndTransportResources = OreManifest(120.0),
                    EndCaptured = true
                },
                RouteHarvestWindows = new List<RouteHarvestWindow>
                {
                    new RouteHarvestWindow
                    {
                        WindowId = "hw",
                        StartUT = 150.0,
                        EndUT = 440.0,
                        StartTransportResources = OreManifest(0.0),
                        EndTransportResources = OreManifest(120.0),
                        BodyName = "Minmus",
                        Latitude = 5.0,
                        Longitude = 6.0,
                        Altitude = 7.0,
                        SituationAtOpen = 1
                    }
                },
                RouteConnectionWindows = new List<RouteConnectionWindow> { grab }
            };
            Recording merge = new Recording
            {
                RecordingId = "mine-merge",
                TreeId = "tree-mine",
                ExplicitStartUT = 500.0,
                ExplicitEndUT = 600.0,
                ParentBranchPointId = "bp-dock",
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { 100, 300 },
                    StartTransportResources = OreManifest(120.0),
                    EndTransportResources = OreManifest(120.0),
                    EndCaptured = true
                },
                RouteConnectionWindows = new List<RouteConnectionWindow> { delivery }
            };
            var tree = new RecordingTree
            {
                Id = "tree-mine",
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

            Assert.True(result.IsEligible,
                $"engaged mining run with skipped grab must be Eligible, got {result.Status}");
            Assert.Single(result.Stops);
            Assert.Equal("ore-delivery", result.Stops[0].ConnectionWindow.WindowId);
        }

        [Theory]
        [InlineData(0.0, 120.0, 0.0, 0.0, true)]    // gain, no endpoint loss -> unmatched
        [InlineData(0.0, 120.0, 120.0, 0.0, false)] // matched pickup -> witnessed, not unmatched
        [InlineData(120.0, 0.0, 0.0, 0.0, false)]   // transport LOSS -> never trips
        public void TryFindUnmatchedTransportResourceGain_Cases(
            double dockT, double undockT, double dockE, double undockE, bool expected)
        {
            var window = new RouteConnectionWindow
            {
                DockTransportResources = OreManifest(dockT),
                UndockTransportResources = OreManifest(undockT),
                DockEndpointResources = OreManifest(dockE),
                UndockEndpointResources = OreManifest(undockE)
            };

            bool hit = RouteAnalysisEngine.TryFindUnmatchedTransportResourceGain(
                window, out string name);

            Assert.Equal(expected, hit);
            if (expected)
                Assert.Equal("Ore", name);
        }

        [Fact]
        public void TryFindUnmatchedTransportResourceGain_IgnoresElectricCharge()
        {
            // EC is environmental noise (batteries charge constantly) and is
            // excluded from admission everywhere; it must not trip the
            // fail-closed gain detector either.
            var window = new RouteConnectionWindow
            {
                DockTransportResources = new Dictionary<string, ResourceAmount>
                {
                    ["ElectricCharge"] = new ResourceAmount { amount = 0.0, maxAmount = 100.0 }
                },
                UndockTransportResources = new Dictionary<string, ResourceAmount>
                {
                    ["ElectricCharge"] = new ResourceAmount { amount = 90.0, maxAmount = 100.0 }
                },
                DockEndpointResources = new Dictionary<string, ResourceAmount>(),
                UndockEndpointResources = new Dictionary<string, ResourceAmount>()
            };

            Assert.False(RouteAnalysisEngine.TryFindUnmatchedTransportResourceGain(
                window, out _));
        }

        // ---------------------------------------------------------------
        // EVA route-window suppression (design 2.1, capture side)
        // ---------------------------------------------------------------

        [Fact]
        public void SuppressRouteWindowForEvaGrab_EvaGrab_SuppressesAndLogs()
        {
            uint result = ParsekFlight.SuppressRouteWindowForEvaGrab(
                9001u, involvesEva: true, RouteConnectionKind.Grapple, pathLabel: "");

            Assert.Equal(0u, result);
            Assert.Contains(logLines, l =>
                l.Contains("[Flight]") && l.Contains("EVA grab, route window suppressed")
                && l.Contains("partnerPid=9001"));
        }

        [Fact]
        public void SuppressRouteWindowForEvaGrab_NonEva_PassesThroughSilently()
        {
            int before = logLines.Count;
            uint result = ParsekFlight.SuppressRouteWindowForEvaGrab(
                9001u, involvesEva: false, RouteConnectionKind.Grapple, pathLabel: "");

            Assert.Equal(9001u, result);
            Assert.Equal(before, logLines.Count);
        }

        [Fact]
        public void SuppressRouteWindowForEvaGrab_NoTarget_NoLog()
        {
            int before = logLines.Count;
            uint result = ParsekFlight.SuppressRouteWindowForEvaGrab(
                0u, involvesEva: true, RouteConnectionKind.Grapple, pathLabel: " (retroactive)");

            Assert.Equal(0u, result);
            Assert.Equal(before, logLines.Count);
        }

        [Fact]
        public void ConnectionKindSuffix_GrappleOnly()
        {
            Assert.Equal(" (grappled)",
                RouteCreationFormatters.ConnectionKindSuffix(RouteConnectionKind.Grapple));
            Assert.Equal(string.Empty,
                RouteCreationFormatters.ConnectionKindSuffix(RouteConnectionKind.DockingPort));
            Assert.Equal(string.Empty,
                RouteCreationFormatters.ConnectionKindSuffix(RouteConnectionKind.None));
        }

        private static Dictionary<string, ResourceAmount> OreManifest(double amount)
        {
            return new Dictionary<string, ResourceAmount>
            {
                ["Ore"] = new ResourceAmount { amount = amount, maxAmount = 1000.0 }
            };
        }

        // ---------------------------------------------------------------
        // Fixtures
        // ---------------------------------------------------------------

        private static Recording KscRecordingWithWindows(
            params RouteConnectionWindow[] windows)
        {
            return new Recording
            {
                RecordingId = "claw-source",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteConnectionWindows = new List<RouteConnectionWindow>(windows)
            };
        }

        // Clean delivery window: LiquidFuel flows transport (80 -> 30) to
        // endpoint (0 -> 50), delivered = 50. Mirrors the canonical fixture in
        // RouteAnalysisEngineTests.BuildDeliveryWindow.
        private static RouteConnectionWindow DeliveryWindow(
            string id, RouteConnectionKind kind, double dockUT = 100.0)
        {
            return new RouteConnectionWindow
            {
                WindowId = id,
                DockUT = dockUT,
                UndockUT = dockUT + 60.0,
                TransferTargetVesselPid = 9001,
                TransferKind = kind,
                DockTransportResources = Manifest(80.0, 100.0),
                UndockTransportResources = Manifest(30.0, 100.0),
                DockEndpointResources = Manifest(0.0, 200.0),
                UndockEndpointResources = Manifest(50.0, 200.0),
                EndpointAtDock = Endpoint(),
                TransferEndpointSituation = 4
            };
        }

        // Zero-transfer window: identical manifests at both corners (the
        // structural-grab shape; an asteroid endpoint carries no
        // PartResources at all, modeled here as empty endpoint manifests).
        private static RouteConnectionWindow EmptyWindow(
            string id, RouteConnectionKind kind, double dockUT)
        {
            return new RouteConnectionWindow
            {
                WindowId = id,
                DockUT = dockUT,
                UndockUT = dockUT + 60.0,
                TransferTargetVesselPid = 9001,
                TransferKind = kind,
                DockTransportResources = Manifest(80.0, 100.0),
                UndockTransportResources = Manifest(80.0, 100.0),
                DockEndpointResources = new Dictionary<string, ResourceAmount>(),
                UndockEndpointResources = new Dictionary<string, ResourceAmount>(),
                EndpointAtDock = Endpoint(),
                TransferEndpointSituation = 4
            };
        }

        private static Dictionary<string, ResourceAmount> Manifest(
            double amount, double maxAmount)
        {
            return new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = amount, maxAmount = maxAmount }
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
                IsSurface = false
            };
        }
    }
}
