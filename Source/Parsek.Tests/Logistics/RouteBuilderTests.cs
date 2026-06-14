using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek;
using Parsek.Logistics;
using Xunit;
// `RouteBuilder` here refers to the production class
// `Parsek.Logistics.RouteBuilder`. The fluent test fixture lives in
// `Parsek.Tests.Generators.RouteFixtureBuilder`; it is not imported here.

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pure-logic tests for <see cref="RouteBuilder.BuildRoute"/>. Covers the
    /// happy path, every reject reason, and the log-line contract. Captured
    /// via <see cref="ParsekLog.TestSinkForTesting"/>; runs Sequential because
    /// the log sink is global static state.
    /// </summary>
    [Collection("Sequential")]
    public class RouteBuilderTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo originalCulture;

        public RouteBuilderTests()
        {
            originalCulture = Thread.CurrentThread.CurrentCulture;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // -----------------------------------------------------------------
        // Fixtures
        // -----------------------------------------------------------------

        private static InventoryPayloadItem MakeInventoryItem(
            string identityHash = "payload-hash",
            string partName = "evaJetpack",
            int quantity = 1,
            int slotsTaken = 1,
            string variantName = null)
        {
            ConfigNode storedPart = new ConfigNode("STOREDPART");
            storedPart.AddValue("partName", partName);
            storedPart.AddValue("quantity", quantity.ToString(CultureInfo.InvariantCulture));
            return new InventoryPayloadItem
            {
                IdentityHash = identityHash,
                PartName = partName,
                VariantName = variantName,
                Quantity = quantity,
                SlotsTaken = slotsTaken,
                StoredPartSnapshot = storedPart
            };
        }

        private static RouteEndpoint MakeMunEndpoint(uint pid = 9001)
        {
            return new RouteEndpoint
            {
                VesselPersistentId = pid,
                BodyName = "Mun",
                Latitude = 12.345,
                Longitude = -45.678,
                Altitude = 612.5,
                IsSurface = true
            };
        }

        // The window dock/undock UTs sit INSIDE the source recording span so the
        // backing-mission [launch..undock] span (undockUT - rootLaunchUT) is
        // positive. For the single-recording fixtures rootLaunchUT == source.StartUT
        // (no committedTree passed), so a window whose dock/undock fall after the
        // recording start keeps the span valid.
        private static RouteConnectionWindow MakeCompleteWindow(
            double dockUT = 1100.0, double undockUT = 1200.0)
        {
            return new RouteConnectionWindow
            {
                WindowId = "w",
                DockUT = dockUT,
                UndockUT = undockUT,
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                EndpointAtDock = MakeMunEndpoint(),
                TransferEndpointSituation = 4
            };
        }

        // KSC-origin source recording with completed transfer. The window's
        // dock/undock default to 1100/1200, inside the default 1000..1300 span.
        private static Recording MakeKscSource(
            double startUT = 1000.0,
            double endUT = 1300.0,
            string recordingId = "src-ksc",
            double dockUT = 1100.0,
            double undockUT = 1200.0)
        {
            return new Recording
            {
                RecordingId = recordingId,
                TreeId = "tree-1",
                TreeOrder = 3,
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                    { MakeCompleteWindow(dockUT, undockUT) },
                RouteOriginProof = null
            }.WithUtSpan(startUT, endUT);
        }

        // Non-KSC source: empty LaunchSiteName + RouteOriginProof with PID.
        private static Recording MakeNonKscSource(
            uint originPid = 123,
            double startUT = 5000.0,
            double endUT = 5600.0,
            string recordingId = "src-non-ksc")
        {
            return new Recording
            {
                RecordingId = recordingId,
                TreeId = "tree-non-ksc",
                TreeOrder = 1,
                StartBodyName = "Mun",
                LaunchSiteName = null,
                RouteConnectionWindows = new List<RouteConnectionWindow>
                    { MakeCompleteWindow(dockUT: 5100.0, undockUT: 5200.0) },
                RouteOriginProof = new RouteOriginProof { StartDockedOriginVesselPid = originPid }
            }.WithUtSpan(startUT, endUT);
        }

        // No-origin source: empty LaunchSiteName, non-Kerbin body, and
        // null RouteOriginProof. Used to drive the endpoint-missing reject.
        private static Recording MakeNoOriginSource(
            double startUT = 5000.0,
            double endUT = 5600.0,
            string recordingId = "src-no-origin")
        {
            return new Recording
            {
                RecordingId = recordingId,
                TreeId = "tree-no-origin",
                TreeOrder = 0,
                StartBodyName = "Mun",
                LaunchSiteName = null,
                RouteConnectionWindows = new List<RouteConnectionWindow>
                    { MakeCompleteWindow(dockUT: 5100.0, undockUT: 5200.0) },
                RouteOriginProof = null
            }.WithUtSpan(startUT, endUT);
        }

        private static RouteAnalysisResult EligibleAnalysisFromSource(Recording source)
        {
            RouteConnectionWindow window = source.RouteConnectionWindows[0];
            return new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.Eligible,
                SourceRecording = source,
                ConnectionWindow = window,
                ResourceDeliveryManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 50.0 }
                },
                InventoryDeliveryManifest = new List<InventoryPayloadItem>
                {
                    MakeInventoryItem()
                }
            };
        }

        private static RouteBuilder.RouteCreationInputs Inputs(
            double interval = 600.0,
            string name = "Test Route")
        {
            return new RouteBuilder.RouteCreationInputs
            {
                Name = name,
                DispatchIntervalSeconds = interval
            };
        }

        // -----------------------------------------------------------------
        // Happy-path tests
        // -----------------------------------------------------------------

        [Fact]
        public void Build_FromEligibleSingleRecordingResult_ProducesActiveRoute_WithFreshGuid()
        {
            // catches: a regression in BuildRoute that returns a route with
            // Status != Active or an empty Id. Both would corrupt the route
            // lifecycle from the moment of creation — the scheduler would
            // refuse to dispatch and the codec would round-trip a malformed id.
            Recording source = MakeKscSource();
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Assert.Null(outcome.RejectReason);
            Assert.Equal(RouteStatus.Active, outcome.Route.Status);
            Assert.False(string.IsNullOrEmpty(outcome.Route.Id));
            // GUID "N" is 32 hex chars.
            Assert.Equal(32, outcome.Route.Id.Length);
        }

        [Fact]
        public void Build_PopulatesSourceRefsWithRouteProofHash()
        {
            // catches: a drift between the route-proof-hash the builder writes
            // into the source ref and the hash RouteStore computes from the
            // same recording. If they ever diverge the validation pass would
            // start invalidating every freshly-built route on the next load.
            Recording source = MakeKscSource();
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.SANDBOX);

            Assert.Single(outcome.Route.SourceRefs);
            RouteSourceRef sref = outcome.Route.SourceRefs[0];
            Assert.Equal(source.RecordingId, sref.RecordingId);
            Assert.Equal(source.TreeId, sref.TreeId);
            Assert.Equal(source.TreeOrder, sref.TreeOrder);
            Assert.Equal(source.StartUT, sref.StartUT);
            Assert.Equal(source.EndUT, sref.EndUT);

            // Hash must match the same computation the store does.
            string expected = RouteProofHasher.ComputeRouteProofHashFromRecording(source);
            Assert.Equal(expected, sref.RouteProofHash);
        }

        [Fact]
        public void Build_KscOriginRecording_SetsIsKscOriginTrue()
        {
            // catches: KSC-origin classification quietly regressing — e.g.
            // a refactor that requires RouteOriginProof for every route would
            // start rejecting Kerbin-launch recordings. Origin.BodyName/PID
            // are the shape the dispatch scheduler relies on for KSC routes.
            Recording source = MakeKscSource();
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.CAREER);

            Assert.True(outcome.Route.IsKscOrigin);
            Assert.Equal("Kerbin", outcome.Route.Origin.BodyName);
            Assert.Equal(0u, outcome.Route.Origin.VesselPersistentId);
        }

        [Fact]
        public void Build_NonKscOriginRecording_PopulatesOriginVesselPid_FromRouteOriginProof()
        {
            // catches: silently dropping the docked-origin vessel pid for
            // non-KSC routes. Without that pid the scheduler cannot resolve
            // the origin vessel at dispatch, so the route would degrade into
            // an undispatchable "ghost" route that survives reload.
            Recording source = MakeNonKscSource(originPid: 4242);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Assert.False(outcome.Route.IsKscOrigin);
            Assert.Equal(4242u, outcome.Route.Origin.VesselPersistentId);
        }

        // catches (M2, plan D7): a harvest-origin analysis (undocked start,
        // fully harvest-covered) not building a route, or building one whose
        // origin endpoint / cost manifests are wrong. The origin must be the
        // FIRST harvest window's open location with pid 0, IsSurface from
        // SituationAtOpen, and the CostManifest must be EMPTY (harvested
        // cargo debits nothing - 19.2.2 item 3).
        [Fact]
        public void BuildRoute_HarvestOrigin_BuildsLocationEndpoint_EmptyCostManifest()
        {
            Recording source = MakeNoOriginSource();
            RouteAnalysisResult analysis = new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.Eligible,
                SourceRecording = source,
                ConnectionWindow = source.RouteConnectionWindows[0],
                ResourceDeliveryManifest = new Dictionary<string, double>
                {
                    { "Ore", 100.0 }
                },
                // Harvest origins never deliver inventory (the refined gate
                // rejects inventory deliveries as UndockedStartOrigin).
                InventoryDeliveryManifest = null,
                IsHarvestOrigin = true,
                HarvestedManifest = new Dictionary<string, double> { { "Ore", 120.0 } },
                FirstHarvestWindow = new RouteHarvestWindow
                {
                    WindowId = "hw",
                    StartUT = 5050.0,
                    EndUT = 5090.0,
                    BodyName = "Minmus",
                    Latitude = -0.55,
                    Longitude = 78.25,
                    Altitude = 2412.5,
                    SituationAtOpen = 1 // LANDED
                }
            };

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Assert.Null(outcome.RejectReason);
            Assert.True(outcome.Route.IsHarvestOrigin);
            Assert.False(outcome.Route.IsKscOrigin);
            // Display-only endpoint from the first window's open location.
            Assert.Equal(0u, outcome.Route.Origin.VesselPersistentId);
            Assert.Equal("Minmus", outcome.Route.Origin.BodyName);
            Assert.Equal(-0.55, outcome.Route.Origin.Latitude);
            Assert.Equal(78.25, outcome.Route.Origin.Longitude);
            Assert.Equal(2412.5, outcome.Route.Origin.Altitude);
            Assert.True(outcome.Route.Origin.IsSurface);
            // Harvested cargo debits NOTHING; delivery stays untouched.
            Assert.Empty(outcome.Route.CostManifest);
            Assert.Empty(outcome.Route.InventoryCostManifest);
            Assert.Equal(100.0, outcome.Route.Stops[0].DeliveryManifest["Ore"]);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("Built route") &&
                l.Contains("origin=harvest"));
        }

        // catches (M2, plan D7 backstop): an IsHarvestOrigin analysis WITHOUT
        // a first harvest window cannot resolve a display endpoint and must
        // fall to the defensive endpoint-missing reject, never NRE.
        [Fact]
        public void BuildRoute_HarvestOriginWithoutWindow_RejectsEndpointMissing()
        {
            Recording source = MakeNoOriginSource();
            RouteAnalysisResult analysis = new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.Eligible,
                SourceRecording = source,
                ConnectionWindow = source.RouteConnectionWindows[0],
                ResourceDeliveryManifest = new Dictionary<string, double>
                {
                    { "Ore", 100.0 }
                },
                IsHarvestOrigin = true,
                FirstHarvestWindow = null
            };

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.SANDBOX);

            Assert.Null(outcome.Route);
            Assert.Equal("endpoint-missing", outcome.RejectReason);
        }

        // catches (M2, plan D8): a docked-origin run with witnessed harvest
        // keeping the full delivery as its depot debit. The CostManifest must
        // be reduced per resource by the harvested amount (max(0, ...)), with
        // zero entries REMOVED, while the delivery manifest stays untouched.
        [Fact]
        public void BuildRoute_DockedOriginWithHarvest_CostManifestReducedByHarvested()
        {
            Recording source = MakeNonKscSource(originPid: 4242);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);
            // Delivery is 50 LiquidFuel (EligibleAnalysisFromSource); 20 of it
            // was witnessed harvested en route -> debit basis 30.
            analysis.HarvestedManifest = new Dictionary<string, double>
            {
                { "LiquidFuel", 20.0 }
            };

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Assert.False(outcome.Route.IsHarvestOrigin);
            Assert.Equal(30.0, outcome.Route.CostManifest["LiquidFuel"], 6);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("costManifestReducedByHarvest=[LiquidFuel:50->30]"));
        }

        // catches (M2, plan D8 / finding 16): a resource fully covered by
        // harvest leaving a zero ENTRY in the CostManifest (it must be
        // REMOVED), and the reduction leaking into the stop's delivery
        // manifest (delivery stays what the recording witnessed).
        [Fact]
        public void BuildRoute_DockedOriginWithHarvest_DeliveryManifestUnchanged()
        {
            Recording source = MakeNonKscSource(originPid: 4242);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);
            // Harvested >= delivered: the LiquidFuel debit entry is removed.
            analysis.HarvestedManifest = new Dictionary<string, double>
            {
                { "LiquidFuel", 60.0 }
            };

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Assert.False(outcome.Route.CostManifest.ContainsKey("LiquidFuel"));
            // Delivery manifests untouched (D8: delivery is what the recording
            // witnessed; only the DEBIT basis shrinks).
            Assert.Equal(50.0, outcome.Route.Stops[0].DeliveryManifest["LiquidFuel"], 6);
            Assert.Single(outcome.Route.InventoryCostManifest);
            Assert.Contains(logLines, l =>
                l.Contains("costManifestReducedByHarvest=[LiquidFuel:50->removed]"));
        }

        // catches (M2 review follow-up NIT 6): a fully-harvested delivery
        // whose subtraction leaves double-rounding residue keeping a
        // float-residue debit entry. "Zero" is RouteHarvestAnalysis.GainEpsilon,
        // not exact 0.0 - residue at or below it removes the entry; a real
        // remainder above it stays.
        [Fact]
        public void ReduceCostManifestByHarvested_FloatResidue_RemovedAtGainEpsilon()
        {
            var costManifest = new Dictionary<string, double>
            {
                { "Ore", 50.0 },
                { "LiquidFuel", 50.0 }
            };
            var harvested = new Dictionary<string, double>
            {
                // Residue 1e-9 (positive, below GainEpsilon): must remove.
                { "Ore", 50.0 - 1e-9 },
                // Real remainder above epsilon: must keep.
                { "LiquidFuel", 50.0 - 1e-3 }
            };

            RouteBuilder.ReduceCostManifestByHarvested(
                costManifest, harvested, CultureInfo.InvariantCulture);

            Assert.False(costManifest.ContainsKey("Ore"));
            Assert.True(costManifest.ContainsKey("LiquidFuel"));
            Assert.Equal(1e-3, costManifest["LiquidFuel"], 9);
            Assert.Contains(logLines, l => l.Contains("costManifestReducedByHarvest=")
                && l.Contains("Ore:50->removed"));
        }

        // catches: KSC routes' CostManifest semantics changing (the funds
        // basis is OQ1 / Phase 6 work, NOT D8) - harvested data on a KSC run
        // must leave the cost manifest as the full delivery clone.
        [Fact]
        public void BuildRoute_KscOriginWithHarvest_CostManifestUnchanged()
        {
            Recording source = MakeKscSource();
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);
            analysis.HarvestedManifest = new Dictionary<string, double>
            {
                { "LiquidFuel", 60.0 }
            };

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Assert.True(outcome.Route.IsKscOrigin);
            Assert.Equal(50.0, outcome.Route.CostManifest["LiquidFuel"], 6);
        }

        // End-to-end pin for the CANONICAL DEPOT DRILL RUN (M2 scenario
        // family 4, depot variant): start DOCKED at the depot (combined
        // root, RouteOriginProof), undock (split child = the transport),
        // drill 120 Ore witnessed by a harvest window, dock at the colony
        // (merge child carries the delivery window). The engine must analyze
        // Eligible as a DOCKED origin (not harvest origin) with the
        // harvested manifest populated, and the built route's CostManifest
        // must drop the fully-harvested Ore entry (D8) while delivery stays
        // 100 Ore.
        [Fact]
        public void BuildRoute_DepotDrillRun_EndToEnd_AnalysisThroughDebitReduction()
        {
            RecordingTree tree = BuildDepotDrillRunTree(
                out Recording root, out Recording solo, out Recording merge);

            RouteAnalysisResult analysis = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.True(analysis.IsEligible,
                $"depot drill run must analyze Eligible, got {analysis.Status}");
            Assert.False(analysis.IsHarvestOrigin,
                "a start-docked depot run is a DOCKED origin, not a harvest origin");
            Assert.NotNull(analysis.HarvestedManifest);
            Assert.Equal(120.0, analysis.HarvestedManifest["Ore"], 6);
            Assert.Equal(100.0, analysis.ResourceDeliveryManifest["Ore"], 6);

            RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                analysis, tree, Inputs(interval: 600.0), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Assert.False(outcome.Route.IsKscOrigin);
            Assert.False(outcome.Route.IsHarvestOrigin);
            Assert.Equal(7777u, outcome.Route.Origin.VesselPersistentId);
            // D8: delivery 100 Ore fully covered by 120 harvested -> the
            // depot is debited NOTHING for it (entry removed).
            Assert.False(outcome.Route.CostManifest.ContainsKey("Ore"));
            Assert.Equal(100.0, outcome.Route.Stops[0].DeliveryManifest["Ore"], 6);
        }

        // Canonical depot-run tree (mirrors the RouteHarvestAnalysisTests
        // fixture): root = combined depot+transport (scope {100, 200}, docked
        // origin proof), Undock BP -> solo transport (scope {100}, drills 120
        // Ore in a witnessed window), Dock BP -> merge child (scope
        // {100, 300}) carrying the colony delivery window. All legs carry
        // COMPLETE run manifests so the M2 gain check engages.
        private static RecordingTree BuildDepotDrillRunTree(
            out Recording root, out Recording solo, out Recording merge)
        {
            Dictionary<string, ResourceAmount> OreAt(double amount)
            {
                return new Dictionary<string, ResourceAmount>
                {
                    ["Ore"] = new ResourceAmount { amount = amount, maxAmount = 1000.0 }
                };
            }
            RouteRunCargoManifest Manifest(uint[] pids, double startOre, double endOre)
            {
                return new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint>(pids),
                    StartTransportResources = OreAt(startOre),
                    EndTransportResources = OreAt(endOre),
                    EndCaptured = true
                };
            }

            root = new Recording
            {
                RecordingId = "depot-root",
                TreeId = "tree-depot-e2e",
                TreeOrder = 0,
                StartBodyName = "Minmus",
                RouteOriginProof = new RouteOriginProof { StartDockedOriginVesselPid = 7777 },
                RouteRunManifest = Manifest(new uint[] { 100, 200 }, 0.0, 0.0)
            }.WithUtSpan(0.0, 100.0);
            solo = new Recording
            {
                RecordingId = "depot-solo",
                TreeId = "tree-depot-e2e",
                TreeOrder = 1,
                ParentBranchPointId = "bp-undock",
                RouteRunManifest = Manifest(new uint[] { 100 }, 0.0, 120.0),
                RouteHarvestWindows = new List<RouteHarvestWindow>
                {
                    new RouteHarvestWindow
                    {
                        WindowId = "hw-drill",
                        StartUT = 150.0,
                        EndUT = 400.0,
                        StartTransportResources = OreAt(0.0),
                        EndTransportResources = OreAt(120.0),
                        BodyName = "Minmus",
                        Latitude = 5.0,
                        Longitude = 6.0,
                        Altitude = 7.0,
                        SituationAtOpen = 1
                    }
                }
            }.WithUtSpan(100.0, 500.0);
            merge = new Recording
            {
                RecordingId = "depot-merge",
                TreeId = "tree-depot-e2e",
                TreeOrder = 2,
                ParentBranchPointId = "bp-dock",
                RouteRunManifest = Manifest(new uint[] { 100, 300 }, 120.0, 120.0),
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "w-colony",
                        DockUT = 500.0,
                        UndockUT = 600.0,
                        TransferTargetVesselPid = 9001,
                        TransferKind = RouteConnectionKind.DockingPort,
                        TransportPartPersistentIds = new List<uint> { 100 },
                        EndpointPartPersistentIds = new List<uint> { 300 },
                        DockTransportResources = OreAt(120.0),
                        UndockTransportResources = OreAt(20.0),
                        DockEndpointResources = OreAt(0.0),
                        UndockEndpointResources = OreAt(100.0),
                        EndpointAtDock = MakeMunEndpoint(),
                        TransferEndpointSituation = 1
                    }
                }
            }.WithUtSpan(500.0, 600.0);

            var tree = new RecordingTree
            {
                Id = "tree-depot-e2e",
                RootRecordingId = root.RecordingId,
                ActiveRecordingId = merge.RecordingId
            };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(solo);
            tree.AddOrReplaceRecording(merge);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-undock",
                UT = 100.0,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { root.RecordingId },
                ChildRecordingIds = new List<string> { solo.RecordingId }
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock",
                UT = 500.0,
                Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { solo.RecordingId },
                ChildRecordingIds = new List<string> { merge.RecordingId }
            });
            return tree;
        }

        // IsSurfaceSituation: surface situations (LANDED=1, SPLASHED=2,
        // PRELAUNCH=4) classify true; flight/orbit and unknown classify false.
        [Theory]
        [InlineData(1, true)]   // LANDED
        [InlineData(2, true)]   // SPLASHED
        [InlineData(4, true)]   // PRELAUNCH
        [InlineData(8, false)]  // FLYING
        [InlineData(16, false)] // SUB_ORBITAL
        [InlineData(32, false)] // ORBITING
        [InlineData(64, false)] // ESCAPING
        [InlineData(-1, false)] // unknown
        [InlineData(0, false)]  // unset
        public void IsSurfaceSituation_ClassifiesSurfaceSituations(int situation, bool expected)
        {
            Assert.Equal(expected, RouteBuilder.IsSurfaceSituation(situation));
        }

        [Fact]
        public void BuildRoute_DockedOrigin_WithDescriptor_BuildsSurfaceEndpoint()
        {
            // catches: dropping the M1 origin endpoint descriptor on the floor:
            // a proof that carries the depot's body/coords/IsSurface must produce
            // a real-coordinate origin endpoint, or surface-base origins never
            // reach RouteEndpointResolver's proximity rebuild fallback when the
            // depot's pid stops resolving.
            Recording source = MakeNonKscSource(originPid: 4242);
            source.RouteOriginProof.StartDockedOriginBodyName = "Minmus";
            source.RouteOriginProof.StartDockedOriginLatitude = -0.55;
            source.RouteOriginProof.StartDockedOriginLongitude = 78.25;
            source.RouteOriginProof.StartDockedOriginAltitude = 2412.5;
            source.RouteOriginProof.StartDockedOriginIsSurface = true;
            source.RouteOriginProof.StartDockedOriginSituation = 1;
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Assert.False(outcome.Route.IsKscOrigin);
            Assert.Equal(4242u, outcome.Route.Origin.VesselPersistentId);
            Assert.Equal("Minmus", outcome.Route.Origin.BodyName);
            Assert.Equal(-0.55, outcome.Route.Origin.Latitude);
            Assert.Equal(78.25, outcome.Route.Origin.Longitude);
            Assert.Equal(2412.5, outcome.Route.Origin.Altitude);
            Assert.True(outcome.Route.Origin.IsSurface);

            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("Built route")
                && l.Contains("originSurface=1")
                && l.Contains("originLat=-0.55")
                && l.Contains("originLon=78.25")
                && l.Contains("originAlt=2412.5"));
        }

        [Fact]
        public void BuildRoute_DockedOrigin_WithoutDescriptor_FallsBackPidOnly()
        {
            // catches: a pre-descriptor proof (pid-only, recorded before M1)
            // regressing off today's PID-only endpoint shape: BodyName from the
            // recording's StartBodyName, zero coords, IsSurface false.
            Recording source = MakeNonKscSource(originPid: 4242);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Assert.Equal(4242u, outcome.Route.Origin.VesselPersistentId);
            Assert.Equal("Mun", outcome.Route.Origin.BodyName);
            Assert.Equal(0.0, outcome.Route.Origin.Latitude);
            Assert.Equal(0.0, outcome.Route.Origin.Longitude);
            Assert.Equal(0.0, outcome.Route.Origin.Altitude);
            Assert.False(outcome.Route.Origin.IsSurface);

            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("Built route")
                && l.Contains("originSurface=0"));
        }

        [Fact]
        public void Build_StopCarriesResourceAndInventoryManifestsFromResult()
        {
            // catches: stop manifests being lost in the analysis-to-route copy
            // or shared by reference. Either failure would surface as empty
            // deliveries at runtime or as accidental cross-route mutation of
            // the analysis result after Build.
            Recording source = MakeKscSource();
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.SANDBOX);

            Assert.Single(outcome.Route.Stops);
            RouteStop stop = outcome.Route.Stops[0];
            Assert.Equal(RouteConnectionKind.DockingPort, stop.ConnectionKind);
            Assert.Equal(50.0, stop.DeliveryManifest["LiquidFuel"]);
            Assert.Single(stop.InventoryDeliveryManifest);
            Assert.Equal("evaJetpack", stop.InventoryDeliveryManifest[0].PartName);
        }

        [Fact]
        public void Build_TransitDurationIsRenderedSpan_DockMinusRootLaunch()
        {
            // (must-fix #3 + playtest follow-up) TransitDuration is the RENDERED span
            // [rootLaunchUT .. DOCK]: rendering stops at the docking moment, so the
            // docked-together combined vessel (dock..undock) is NOT rendered. NOT
            // undock-launch and NOT the leaf-only source span. With no committedTree,
            // rootLaunchUT == source.StartUT. Span here is dock(2500) - launch(2000) = 500.
            Recording source = MakeKscSource(
                startUT: 2000.0, endUT: 2900.0, dockUT: 2500.0, undockUT: 2700.0);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(interval: 900.0), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Assert.Equal(500.0, outcome.Route.TransitDuration);
        }

        [Fact]
        public void Build_PopulatesDispatchWindowEpochUTFromRootLaunchUT()
        {
            // catches: a regression where the dispatch-window epoch drifts off
            // the tree-root launch UT. With no committedTree the root launch is
            // source.StartUT. The scheduler anchors the dispatch cadence on this
            // epoch — losing it would phase the cycle off the player's launch.
            Recording source = MakeKscSource(
                startUT: 17_000.0, endUT: 17_500.0, dockUT: 17_200.0, undockUT: 17_400.0);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(interval: 600.0), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Assert.Equal(17_000.0, outcome.Route.DispatchWindowEpochUT);
        }

        // -----------------------------------------------------------------
        // Reject paths
        // -----------------------------------------------------------------

        [Fact]
        public void Build_DispatchIntervalBelowTransit_Rejected()
        {
            // catches: accepting a dispatch interval shorter than the rendered span
            // (undockUT - rootLaunchUT). That would let the loop clock tick on
            // max(interval, span) != interval, so a crossing would NOT equal a
            // dispatch cycle. Span here is undock(900) - launch(0) = 900; interval
            // 500 < 900 -> reject (when clamp not allowed).
            Recording source = MakeKscSource(
                startUT: 0.0, endUT: 1000.0, dockUT: 800.0, undockUT: 900.0);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(interval: 500.0), Game.Modes.SANDBOX);

            Assert.Null(outcome.Route);
            Assert.Equal("interval-below-transit", outcome.RejectReason);
        }

        [Fact]
        public void Build_DispatchIntervalBelowSpan_ClampsUpWhenAllowed()
        {
            // (must-fix #2) When allowIntervalBelowTransit is set (the debug /
            // candidate Create Route path), an interval below the rendered span is
            // CLAMPED UP to the span (not rejected) so cadence == interval == span
            // and one crossing == one dispatch cycle. Span = dock(800)-launch(0) = 800
            // (the segment ends at the dock, not the undock).
            Recording source = MakeKscSource(
                startUT: 0.0, endUT: 1000.0, dockUT: 800.0, undockUT: 900.0);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                analysis, null, Inputs(interval: 30.0), Game.Modes.SANDBOX,
                idFactory: null,
                initialStatus: RouteStatus.Paused,
                allowIntervalBelowTransit: true);

            Assert.NotNull(outcome.Route);
            Assert.Null(outcome.RejectReason);
            // Interval clamped UP to the span (= dock - launch = 800).
            Assert.Equal(800.0, outcome.Route.DispatchInterval);
            Assert.Equal(800.0, outcome.Route.TransitDuration);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("interval below span")
                && l.Contains("clamped up"));
        }

        [Fact]
        public void Build_NegativeOrZeroInterval_Rejected()
        {
            // catches: a non-positive dispatch interval slipping past
            // validation. Zero would trigger an infinite-dispatch loop in the
            // scheduler; negative would underflow the next-dispatch UT and
            // could fire on every tick.
            Recording source = MakeKscSource();
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome zero =
                RouteBuilder.BuildRoute(analysis, null, Inputs(interval: 0.0), Game.Modes.SANDBOX);
            RouteBuilder.RouteBuildOutcome neg =
                RouteBuilder.BuildRoute(analysis, null, Inputs(interval: -10.0), Game.Modes.SANDBOX);

            Assert.Equal("interval-invalid", zero.RejectReason);
            Assert.Equal("interval-invalid", neg.RejectReason);
        }

        [Fact]
        public void Build_IneligibleResult_ReturnsNullRouteWithRejectReason()
        {
            // catches: BuildRoute silently producing a route from an
            // ineligible analysis result. A pre-flight gate change that
            // dropped the IsEligible check would surface here.
            RouteAnalysisResult analysis = new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.MissingRouteProof
            };

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.SANDBOX);

            Assert.Null(outcome.Route);
            Assert.Equal("source-no-longer-eligible", outcome.RejectReason);
        }

        [Fact]
        public void Build_NoOrigin_Rejected()
        {
            // catches: the endpoint-missing rejection branch silently changing
            // wording or accepting routes without a determinable origin.
            // Without this test a future refactor could swallow the case and
            // produce a route with a zero-PID, empty-body origin that the
            // scheduler would treat as KSC by accident.
            Recording source = MakeNoOriginSource();
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.SANDBOX);

            Assert.Null(outcome.Route);
            Assert.Equal("endpoint-missing", outcome.RejectReason);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("endpoint-missing"));
        }

        [Fact]
        public void Build_OriginResolvedFromTreeRoot_WhenWindowChildHasNoLaunchSite()
        {
            // Regression (2026-05-22 playtest): the window-carrying recording is
            // the dock-merged CHILD, which started mid-flight at the dock and so
            // has no LaunchSiteName. Origin must come from the tree ROOT (the
            // launch). Before the fix, Create Route rejected with endpoint-missing
            // because BuildRoute read launch-site from the child.
            var windowChild = new Recording
            {
                RecordingId = "merged-child",
                TreeId = "tree-x",
                StartBodyName = "Kerbin",
                LaunchSiteName = null, // child started at the dock, not at launch
                RouteConnectionWindows = new List<RouteConnectionWindow> { MakeCompleteWindow() },
                RouteOriginProof = null
            }.WithUtSpan(1200.0, 1300.0);
            var root = new Recording
            {
                RecordingId = "root",
                TreeId = "tree-x",
                StartBodyName = "Kerbin",
                LaunchSiteName = "Runway", // the launch carries the origin
                RouteOriginProof = null
            }.WithUtSpan(1000.0, 1200.0);
            var tree = new RecordingTree { Id = "tree-x", RootRecordingId = "root" };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(windowChild);

            RouteAnalysisResult analysis = EligibleAnalysisFromSource(windowChild);

            RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                analysis, tree, Inputs(), Game.Modes.SANDBOX,
                idFactory: null,
                initialStatus: RouteStatus.Paused,
                allowIntervalBelowTransit: true);

            Assert.NotNull(outcome.Route);
            Assert.Null(outcome.RejectReason);
            Assert.True(outcome.Route.IsKscOrigin);
            Assert.Equal("Kerbin", outcome.Route.Origin.BodyName);
            Assert.Equal(RouteStatus.Paused, outcome.Route.Status);
        }

        // -----------------------------------------------------------------
        // Phase 5: backing-mission definition + dock/undock capture
        // -----------------------------------------------------------------

        [Fact]
        public void Build_PopulatesBackingMissionDefinition_FromSource()
        {
            // catches: the backing-mission DEFINITION (tree id, dock binding, loop
            // anchor) not being captured at creation. Without it the route is not a
            // loop-route (IsLoopRoute false) and never renders / fires the loop clock.
            Recording source = MakeKscSource(
                startUT: 1000.0, endUT: 1500.0, dockUT: 1200.0, undockUT: 1400.0);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(interval: 600.0), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Route route = outcome.Route;
            // BackingMissionTreeId == the source tree id (guard/selector key match).
            Assert.Equal(source.TreeId, route.BackingMissionTreeId);
            Assert.True(route.IsLoopRoute);
            // Dock binding lifted from the connection window.
            Assert.Equal(1200.0, route.RecordedDockUT);
            Assert.Equal(source.RecordingId, route.DockMemberRecordingId);
            // Created Active -> LoopAnchorUT seeded to the root launch UT.
            Assert.Equal(1000.0, route.LoopAnchorUT);
            // Fresh loop-clock cursor.
            Assert.Equal(-1, route.LastObservedLoopCycleIndex);
            // BackingMissionTreeId == SourceRefs[].TreeId (the design guarantee).
            foreach (RouteSourceRef sref in route.SourceRefs)
                Assert.Equal(route.BackingMissionTreeId, sref.TreeId);
        }

        [Fact]
        public void Build_PausedRoute_LeavesLoopAnchorUnset()
        {
            // catches: a Paused-created route (candidate path) wrongly seeding
            // LoopAnchorUT. The anchor is set on ACTIVATE (TryActivate), not at
            // create-Paused; a stale anchor would mis-seed the diagnostic field.
            Recording source = MakeKscSource(
                startUT: 1000.0, endUT: 1500.0, dockUT: 1200.0, undockUT: 1400.0);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                analysis, null, Inputs(interval: 600.0), Game.Modes.SANDBOX,
                idFactory: null,
                initialStatus: RouteStatus.Paused);

            Assert.NotNull(outcome.Route);
            Assert.Equal(-1.0, outcome.Route.LoopAnchorUT);
        }

        [Fact]
        public void Build_MemberSet_WidensToRootLaunchPath_OnMultiLegTree()
        {
            // (must-fix #3) On a multi-recording flight the route's RecordingIds /
            // SourceRefs cover EVERY [root..undock] member, not just the dock-child
            // leaf, so RevalidateSources tracks the whole rendered path. The leaf
            // (window-carrying child) stays the delivery-binding carrier.
            var windowChild = new Recording
            {
                RecordingId = "docked-child",
                TreeId = "tree-multi",
                TreeOrder = 1,
                StartBodyName = "Kerbin",
                LaunchSiteName = null,
                RouteConnectionWindows = new List<RouteConnectionWindow>
                    { MakeCompleteWindow(dockUT: 2000.0, undockUT: 3000.0) },
                RouteOriginProof = null
            }.WithUtSpan(2000.0, 3000.0);
            var root = new Recording
            {
                RecordingId = "launch-root",
                TreeId = "tree-multi",
                TreeOrder = 0,
                StartBodyName = "Kerbin",
                LaunchSiteName = "Runway",
                RouteOriginProof = null
            }.WithUtSpan(1000.0, 2000.0);
            var survivor = new Recording
            {
                RecordingId = "survivor",
                TreeId = "tree-multi",
                TreeOrder = 2,
                StartBodyName = "Kerbin",
                RouteOriginProof = null
            }.WithUtSpan(3000.0, 4000.0);
            var tree = new RecordingTree { Id = "tree-multi", RootRecordingId = "launch-root" };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(windowChild);
            tree.AddOrReplaceRecording(survivor);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "dock-bp",
                Type = BranchPointType.Dock,
                UT = 2000.0,
                ParentRecordingIds = new List<string> { "launch-root" },
                ChildRecordingIds = new List<string> { "docked-child" }
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "undock-bp",
                Type = BranchPointType.Undock,
                UT = 3000.0,
                SplitCause = "UNDOCK",
                ParentRecordingIds = new List<string> { "docked-child" },
                ChildRecordingIds = new List<string> { "survivor" }
            });

            RouteAnalysisResult analysis = EligibleAnalysisFromSource(windowChild);

            RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                analysis, tree, Inputs(interval: 3000.0), Game.Modes.SANDBOX,
                idFactory: null,
                initialStatus: RouteStatus.Paused);

            Assert.NotNull(outcome.Route);
            Route route = outcome.Route;
            // Span = dock(2000) - rootLaunch(1000) = 1000 (segment ends at the dock,
            // so the docked combined-vessel tail is not rendered).
            Assert.Equal(1000.0, route.TransitDuration);
            // Member set covers the [root..dock] kept recordings (the launch root)
            // plus the force-added delivery-binding leaf (the dock child, which
            // carries the proof even though it is not rendered). The post-undock
            // survivor is NOT a member.
            Assert.Contains("launch-root", route.RecordingIds);
            Assert.Contains("docked-child", route.RecordingIds);
            Assert.DoesNotContain("survivor", route.RecordingIds);
            // One SourceRef per member, all on the same tree.
            Assert.Equal(route.RecordingIds.Count, route.SourceRefs.Count);
            // The leaf (dock child) stays the delivery-binding carrier.
            Assert.Equal("docked-child", route.DockMemberRecordingId);
        }

        // -----------------------------------------------------------------
        // Phase 6: cadence multiplier
        // -----------------------------------------------------------------

        [Fact]
        public void Build_DefaultCadence_IsOne_IntervalEqualsSpan()
        {
            // (Phase 6) An interval equal to the rendered span -> N=1 (the floor),
            // and DispatchInterval == TransitDuration. Default cadence is the
            // minimum loop time. Span = dock(1200) - launch(1000) = 200 (segment
            // ends at the dock).
            Recording source = MakeKscSource(
                startUT: 1000.0, endUT: 1500.0, dockUT: 1200.0, undockUT: 1400.0);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(interval: 200.0), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Assert.Equal(1, outcome.Route.CadenceMultiplier);
            Assert.Equal(200.0, outcome.Route.TransitDuration);
            Assert.Equal(200.0, outcome.Route.DispatchInterval);
        }

        [Fact]
        public void Build_IntervalAtNxSpan_DerivesCadenceN_AndReDerivesInterval()
        {
            // (Phase 6) An interval of N x span derives CadenceMultiplier=N and the
            // interval is re-derived as N x span so the two stay in lock-step. Span =
            // dock(1200) - launch(1000) = 200 (segment ends at the dock); entered
            // interval 600 = 3 x 200.
            Recording source = MakeKscSource(
                startUT: 1000.0, endUT: 1500.0, dockUT: 1200.0, undockUT: 1400.0);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(interval: 600.0), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Assert.Equal(3, outcome.Route.CadenceMultiplier);
            Assert.Equal(600.0, outcome.Route.DispatchInterval);
            // DispatchInterval == N x TransitDuration exactly.
            Assert.Equal(outcome.Route.CadenceMultiplier * outcome.Route.TransitDuration,
                outcome.Route.DispatchInterval);
            // The "Built route" log carries the cadence multiplier for greppability.
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("Built route") && l.Contains("cadenceN=3"));
        }

        [Fact]
        public void Build_ClampedUpInterval_DerivesCadenceOne()
        {
            // (Phase 6 + must-fix #2) When an interval below the span is clamped up,
            // the resulting cadence is N=1 (the clamp lands exactly at the span).
            // Span = dock(800) - launch(0) = 800 (segment ends at the dock).
            Recording source = MakeKscSource(
                startUT: 0.0, endUT: 1000.0, dockUT: 800.0, undockUT: 900.0);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                analysis, null, Inputs(interval: 30.0), Game.Modes.SANDBOX,
                idFactory: null,
                initialStatus: RouteStatus.Paused,
                allowIntervalBelowTransit: true);

            Assert.NotNull(outcome.Route);
            Assert.Equal(1, outcome.Route.CadenceMultiplier);
            Assert.Equal(800.0, outcome.Route.DispatchInterval);
        }

        // -----------------------------------------------------------------
        // Phase 5: backing-mission-unresolvable reject
        // -----------------------------------------------------------------

        [Fact]
        public void Build_NonFiniteUndockUT_Rejected_BackingMissionUnresolvable()
        {
            // catches: a window with a non-finite UndockUT slipping through. The
            // [launch..undock] render geometry cannot be derived, so the route
            // would render nothing / never fire the loop clock. Must reject.
            Recording source = MakeKscSource(startUT: 1000.0, endUT: 1500.0);
            source.RouteConnectionWindows[0].UndockUT = double.NaN;
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(interval: 600.0), Game.Modes.SANDBOX);

            Assert.Null(outcome.Route);
            Assert.Equal("backing-mission-unresolvable", outcome.RejectReason);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("backing-mission-unresolvable"));
        }

        [Fact]
        public void Build_UndockBeforeRootLaunch_Rejected_BackingMissionUnresolvable()
        {
            // catches: an empty / inverted [launch..undock] window (undock at or
            // before the root launch) producing a non-positive span. Must reject
            // rather than build a zero/negative-span loop.
            var windowChild = new Recording
            {
                RecordingId = "child",
                TreeId = "tree-inv",
                StartBodyName = "Kerbin",
                LaunchSiteName = null,
                // undock (900) is BEFORE the root launch (1000).
                RouteConnectionWindows = new List<RouteConnectionWindow>
                    { MakeCompleteWindow(dockUT: 850.0, undockUT: 900.0) },
                RouteOriginProof = null
            }.WithUtSpan(800.0, 950.0);
            var root = new Recording
            {
                RecordingId = "root",
                TreeId = "tree-inv",
                StartBodyName = "Kerbin",
                LaunchSiteName = "Runway",
                RouteOriginProof = null
            }.WithUtSpan(1000.0, 1100.0);
            var tree = new RecordingTree { Id = "tree-inv", RootRecordingId = "root" };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(windowChild);

            RouteAnalysisResult analysis = EligibleAnalysisFromSource(windowChild);

            RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                analysis, tree, Inputs(interval: 600.0), Game.Modes.SANDBOX,
                idFactory: null,
                initialStatus: RouteStatus.Paused,
                allowIntervalBelowTransit: true);

            Assert.Null(outcome.Route);
            Assert.Equal("backing-mission-unresolvable", outcome.RejectReason);
        }

        [Fact]
        public void Build_LogsRouteTagOnBuildAndOnReject()
        {
            // catches: the [Route] subsystem tag or the "Built route" /
            // "BuildRoute rejected" log wording drifting. KSP.log is the
            // primary debugging tool; the post-mortem checker greps these
            // exact strings.
            // Happy path log
            Recording source = MakeKscSource();
            RouteAnalysisResult ok = EligibleAnalysisFromSource(source);
            RouteBuilder.BuildRoute(ok, null, Inputs(), Game.Modes.SANDBOX);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[Route]")
                && l.Contains("Built route"));

            logLines.Clear();

            // Reject log
            RouteAnalysisResult bad = new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.MissingRouteProof
            };
            RouteBuilder.BuildRoute(bad, null, Inputs(), Game.Modes.SANDBOX);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[Route]")
                && l.Contains("BuildRoute rejected")
                && l.Contains("source-no-longer-eligible"));
        }

        // -----------------------------------------------------------------
        // CRE-2: player-shown transit equals the created route's span
        // -----------------------------------------------------------------

        // Build the multi-leg fixture shared by the CRE-2 / CRE-4 tests: a tree
        // with a launch ROOT (1000..2000), a dock CHILD leaf carrying the window
        // (2000..3000, dock 2000 -> undock 3000), and a post-undock survivor. The
        // rendered span is [root.StartUT(1000) .. undockUT(3000)] = 2000, while the
        // leaf-only span (child.EndUT - child.StartUT = 1000) is HALF that.
        private static RecordingTree MakeMultiLegTree(out Recording windowChild)
        {
            windowChild = new Recording
            {
                RecordingId = "docked-child",
                TreeId = "tree-multi",
                TreeOrder = 1,
                StartBodyName = "Kerbin",
                LaunchSiteName = null,
                RouteConnectionWindows = new List<RouteConnectionWindow>
                    { MakeCompleteWindow(dockUT: 2500.0, undockUT: 3000.0) },
                RouteOriginProof = null
            }.WithUtSpan(2000.0, 3000.0);
            var root = new Recording
            {
                RecordingId = "launch-root",
                TreeId = "tree-multi",
                TreeOrder = 0,
                StartBodyName = "Kerbin",
                LaunchSiteName = "Runway",
                RouteOriginProof = null
            }.WithUtSpan(1000.0, 2000.0);
            var survivor = new Recording
            {
                RecordingId = "survivor",
                TreeId = "tree-multi",
                TreeOrder = 2,
                StartBodyName = "Kerbin",
                RouteOriginProof = null
            }.WithUtSpan(3000.0, 4000.0);
            var tree = new RecordingTree { Id = "tree-multi", RootRecordingId = "launch-root" };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(windowChild);
            tree.AddOrReplaceRecording(survivor);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "dock-bp",
                Type = BranchPointType.Dock,
                UT = 2000.0,
                ParentRecordingIds = new List<string> { "launch-root" },
                ChildRecordingIds = new List<string> { "docked-child" }
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "undock-bp",
                Type = BranchPointType.Undock,
                UT = 3000.0,
                SplitCause = "UNDOCK",
                ParentRecordingIds = new List<string> { "docked-child" },
                ChildRecordingIds = new List<string> { "survivor" }
            });
            return tree;
        }

        // (M-MIS-9-R1) The creation-time tree snapshot scoping the recovery
        // credit. It must capture the WHOLE tree, including the post-undock
        // survivor leg that is NOT a route member (gotcha G1: that leg carries
        // the fly-home-and-recover rows).
        [Fact]
        public void BuildRoute_CapturesCreationTreeSnapshot_WholeTree_IncludingPostUndockLeg()
        {
            RecordingTree tree = MakeMultiLegTree(out Recording windowChild);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(windowChild);

            RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                analysis, tree, Inputs(interval: 2000.0), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Assert.Equal(3, outcome.Route.CreationTreeRecordingIds.Count);
            Assert.Contains("launch-root", outcome.Route.CreationTreeRecordingIds);
            Assert.Contains("docked-child", outcome.Route.CreationTreeRecordingIds);
            // The post-undock leg is NOT in RecordingIds but MUST be in the snapshot.
            Assert.Contains("survivor", outcome.Route.CreationTreeRecordingIds);
            Assert.DoesNotContain("survivor", outcome.Route.RecordingIds);
            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("Built route") && l.Contains("creationTreeRecordings=3"));
        }

        // (M-MIS-9-R1) The defensive null-tree path (production dialog and
        // candidate sources always pass a committed tree) must leave the
        // snapshot EMPTY so the run-cost resolver fails open to the whole
        // current tree. A member-id fallback would exclude the post-undock
        // recover leg if the route's tree id resolved later (the G1
        // silent-zero regression this fix exists to avoid).
        [Fact]
        public void BuildRoute_NullTree_CreationSnapshotStaysEmpty_FailOpen()
        {
            Recording source = MakeKscSource();
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                analysis, null, Inputs(), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Assert.Empty(outcome.Route.CreationTreeRecordingIds);
            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("Built route") && l.Contains("creationTreeRecordings=0"));
        }

        [Fact]
        public void Cre2_CandidateTransitSpanHelper_EqualsCreatedRouteTransitDuration_OnMultiLegTree()
        {
            // (CRE-2) The player-shown transit (LogisticsWindowUI.CandidateTransit and
            // the dialog summary) both route through ComputeRootToUndockSpan. That
            // span MUST equal the created route's TransitDuration; otherwise the table
            // / summary misreports while the route flies the rendered [root..DOCK] span
            // (rendering stops at the dock). On this fixture the leaf dock-child span
            // (1000) differs from the rendered [root..dock] span (dock 2500 - launch
            // 1000 = 1500), so a leaf-span display would be visibly wrong.
            RecordingTree tree = MakeMultiLegTree(out Recording windowChild);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(windowChild);

            // The exact helper both display sites reuse.
            double shownSpan = RouteCreationDialog.ComputeRootToUndockSpan(analysis, tree);

            RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                analysis, tree, Inputs(interval: 2000.0), Game.Modes.SANDBOX,
                idFactory: null,
                initialStatus: RouteStatus.Paused);

            Assert.NotNull(outcome.Route);
            Assert.Equal(1500.0, outcome.Route.TransitDuration);
            // Display span == created route span (the whole point of CRE-2).
            Assert.Equal(outcome.Route.TransitDuration, shownSpan);
            // And it is NOT the leaf-only dock-child span (which would be 1000).
            Assert.NotEqual(windowChild.EndUT - windowChild.StartUT, shownSpan);
        }

        [Fact]
        public void Cre2_BuildSummaryBlock_TransitLine_UsesFullRootToUndockSpan_NotLeafSpan()
        {
            // (CRE-2) The dialog summary "Transit:" line must render the rendered
            // [root..DOCK] span (rendering stops at the dock), not the leaf dock-child
            // span. Span here = dock(2500) - root(2000) = 500s (under one hour, so
            // FormatDuration never reads GameSettings -> no Unity dependency); leaf span
            // would be 900s, which formats differently. Pin the override anyway so the
            // format is deterministic.
            ParsekTimeFormat.KerbinTimeOverrideForTesting = true;
            try
            {
                var windowChild = new Recording
                {
                    RecordingId = "child",
                    TreeId = "tree-s",
                    TreeOrder = 1,
                    StartBodyName = "Kerbin",
                    LaunchSiteName = null,
                    RouteConnectionWindows = new List<RouteConnectionWindow>
                        { MakeCompleteWindow(dockUT: 2500.0, undockUT: 2700.0) },
                    RouteOriginProof = null
                }.WithUtSpan(2000.0, 2900.0); // leaf span = 900
                var root = new Recording
                {
                    RecordingId = "root-s",
                    TreeId = "tree-s",
                    TreeOrder = 0,
                    StartBodyName = "Kerbin",
                    LaunchSiteName = "Runway",
                    RouteOriginProof = null
                }.WithUtSpan(2000.0, 2500.0);
                var tree = new RecordingTree { Id = "tree-s", RootRecordingId = "root-s" };
                tree.AddOrReplaceRecording(root);
                tree.AddOrReplaceRecording(windowChild);

                RouteAnalysisResult analysis = EligibleAnalysisFromSource(windowChild);

                // root.StartUT(2000) .. dock(2500) -> 500s.
                string block = RouteCreationFormatters.BuildSummaryBlock(
                    analysis, Game.Modes.SANDBOX, tree);

                Assert.Contains("Transit: " + ParsekTimeFormat.FormatDuration(500.0), block);
                // The leaf span (900s) must NOT appear on the Transit line.
                Assert.DoesNotContain("Transit: " + ParsekTimeFormat.FormatDuration(900.0), block);
            }
            finally
            {
                ParsekTimeFormat.KerbinTimeOverrideForTesting = null;
            }
        }

        [Fact]
        public void Cre2_BuildSummaryBlock_NullTree_FallsBackToLeafSpan_StillCompiles()
        {
            // (CRE-2) With no tree the span helper falls back to the leaf span (no
            // worse than the old behaviour), and the 2-arg call site keeps compiling
            // via the optional tree parameter. Source span 0..600 -> 600s -> "10m 0s".
            ParsekTimeFormat.KerbinTimeOverrideForTesting = true;
            try
            {
                Recording source = MakeKscSource(
                    startUT: 0.0, endUT: 600.0, dockUT: 200.0, undockUT: 500.0);
                RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

                // 2-arg call (the read-only dialog's current call site shape).
                string block = RouteCreationFormatters.BuildSummaryBlock(
                    analysis, Game.Modes.SANDBOX);

                // No tree -> root falls back to source.StartUT(0); span = dock(200) - 0
                // = 200 (segment ends at the dock, not the undock).
                Assert.Contains("Transit: " + ParsekTimeFormat.FormatDuration(200.0), block);
            }
            finally
            {
                ParsekTimeFormat.KerbinTimeOverrideForTesting = null;
            }
        }

        // -----------------------------------------------------------------
        // CRE-4: root-recording-unresolvable reject
        // -----------------------------------------------------------------

        [Fact]
        public void Cre4_TreeWithMissingRootRecording_Rejected_RootUnresolvable()
        {
            // (CRE-4) A committed tree whose RootRecordingId does NOT resolve to a
            // recording must REJECT, not silently fall back to source.StartUT (the
            // dock-child leaf's mid-flight DOCK UT). Falling back would build a
            // [dock..undock] segment instead of [launch..undock]. Here the tree
            // points RootRecordingId at "ghost-root" which is absent from Recordings.
            var windowChild = new Recording
            {
                RecordingId = "docked-child",
                TreeId = "tree-noroot",
                TreeOrder = 1,
                StartBodyName = "Kerbin",
                LaunchSiteName = null,
                RouteConnectionWindows = new List<RouteConnectionWindow>
                    { MakeCompleteWindow(dockUT: 2500.0, undockUT: 3000.0) },
                RouteOriginProof = null
            }.WithUtSpan(2000.0, 3000.0);
            var tree = new RecordingTree { Id = "tree-noroot", RootRecordingId = "ghost-root" };
            tree.AddOrReplaceRecording(windowChild); // root id never added

            RouteAnalysisResult analysis = EligibleAnalysisFromSource(windowChild);

            RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                analysis, tree, Inputs(interval: 2000.0), Game.Modes.SANDBOX,
                idFactory: null,
                initialStatus: RouteStatus.Paused,
                allowIntervalBelowTransit: true);

            Assert.Null(outcome.Route);
            Assert.Equal("root-recording-unresolvable", outcome.RejectReason);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[Route]")
                && l.Contains("BuildRoute rejected")
                && l.Contains("root-recording-unresolvable"));
        }

        [Fact]
        public void Cre4_TreeWithEmptyRootRecordingId_Rejected_RootUnresolvable()
        {
            // (CRE-4) An empty/null RootRecordingId is equally unresolvable: there is
            // no launch recording to anchor [launch..undock], so reject rather than
            // build a wrong-span route off the dock-child leaf.
            var windowChild = new Recording
            {
                RecordingId = "docked-child",
                TreeId = "tree-emptyroot",
                TreeOrder = 1,
                StartBodyName = "Kerbin",
                LaunchSiteName = null,
                RouteConnectionWindows = new List<RouteConnectionWindow>
                    { MakeCompleteWindow(dockUT: 2500.0, undockUT: 3000.0) },
                RouteOriginProof = null
            }.WithUtSpan(2000.0, 3000.0);
            var tree = new RecordingTree { Id = "tree-emptyroot", RootRecordingId = "" };
            tree.AddOrReplaceRecording(windowChild);

            RouteAnalysisResult analysis = EligibleAnalysisFromSource(windowChild);

            RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                analysis, tree, Inputs(interval: 2000.0), Game.Modes.SANDBOX,
                idFactory: null,
                initialStatus: RouteStatus.Paused,
                allowIntervalBelowTransit: true);

            Assert.Null(outcome.Route);
            Assert.Equal("root-recording-unresolvable", outcome.RejectReason);
        }

        [Fact]
        public void Cre4_NullTree_SingleRecording_NotRejected_SourceIsRoot()
        {
            // (CRE-4) The legacy single-recording path (committedTree == null) must
            // still build: the source recording IS the whole flight (its own root), so
            // source.StartUT genuinely IS the launch UT. Guards the narrow reject from
            // catching the legitimate standalone case.
            Recording source = MakeKscSource(
                startUT: 1000.0, endUT: 1500.0, dockUT: 1200.0, undockUT: 1400.0);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(interval: 400.0), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Assert.Null(outcome.RejectReason);
            // rootLaunchUT == source.StartUT, span = dock(1200) - 1000 = 200 (segment
            // ends at the dock, not the undock).
            Assert.Equal(200.0, outcome.Route.TransitDuration);
        }

        // -----------------------------------------------------------------
        // CRE-5: dock-UT-out-of-span reject
        // -----------------------------------------------------------------

        [Theory]
        [InlineData(false, 1100.0, 1000.0, 1200.0, true)]  // dock strictly inside -> valid
        [InlineData(false, 1000.0, 1000.0, 1200.0, false)] // dock == launch -> invalid
        [InlineData(false, 1200.0, 1000.0, 1200.0, false)] // dock == undock -> invalid
        [InlineData(false, 999.0, 1000.0, 1200.0, false)]  // dock before launch -> invalid
        [InlineData(false, 1300.0, 1000.0, 1200.0, false)] // dock after undock -> invalid
        [InlineData(true, 1100.0, 1000.0, 1200.0, false)]  // NaN dock -> invalid
        public void Cre5_IsDockUTWithinSpan_StrictBounds_RejectsDegenerate(
            bool dockIsNaN, double dockUT, double rootLaunchUT, double undockUT, bool expected)
        {
            // (CRE-5) The pure predicate: dock UT must be STRICTLY between launch and
            // undock (a mid-flight event). Boundary and out-of-range inputs (and any
            // NaN) reject. This is the fail-fast subset of RouteLoopClock.IsDockUTInSpan.
            double dock = dockIsNaN ? double.NaN : dockUT;
            Assert.Equal(expected, RouteBuilder.IsDockUTWithinSpan(dock, rootLaunchUT, undockUT));
        }

        [Fact]
        public void Cre5_DockUTOutsideSpan_Rejected_DockUtOutOfSpan()
        {
            // (CRE-5) A malformed window whose dock UT falls OUTSIDE the rendered
            // [rootLaunch..undock] span must reject at build time. Otherwise the loop
            // clock's IsDockUTInSpan is false forever and the route never delivers
            // (a dead route persisted to the save). Here the window's dock UT (3500)
            // is AFTER the undock UT (3000) on the multi-leg tree.
            RecordingTree tree = MakeMultiLegTree(out Recording windowChild);
            // Corrupt the window: dock now lands past undock.
            windowChild.RouteConnectionWindows[0].DockUT = 3500.0;
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(windowChild);

            // Interval >= the (corrupt) dock-based span (dock 3500 - launch 1000 =
            // 2500) so the interval-below-transit clamp does not preempt the CRE-5
            // dock-out-of-span reject. A corrupt dock past undock must reject as
            // dock-ut-out-of-span.
            RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                analysis, tree, Inputs(interval: 2500.0), Game.Modes.SANDBOX,
                idFactory: null,
                initialStatus: RouteStatus.Paused);

            Assert.Null(outcome.Route);
            Assert.Equal("dock-ut-out-of-span", outcome.RejectReason);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[Route]")
                && l.Contains("BuildRoute rejected")
                && l.Contains("dock-ut-out-of-span"));
        }

        [Fact]
        public void Cre5_DockUTAtSpanBoundary_Rejected_DockUtOutOfSpan()
        {
            // (CRE-5) A dock UT exactly AT the undock instant is a degenerate window
            // (zero delivery slack) the loop clock cannot cross meaningfully; the
            // strict bound rejects it. Window dock == undock == 3000 on the multi-leg
            // tree (rootLaunch 1000, undock 3000).
            RecordingTree tree = MakeMultiLegTree(out Recording windowChild);
            windowChild.RouteConnectionWindows[0].DockUT = 3000.0; // == undockUT
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(windowChild);

            RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                analysis, tree, Inputs(interval: 2000.0), Game.Modes.SANDBOX,
                idFactory: null,
                initialStatus: RouteStatus.Paused);

            Assert.Null(outcome.Route);
            Assert.Equal("dock-ut-out-of-span", outcome.RejectReason);
        }

        // -----------------------------------------------------------------
        // CRE-3: sub-2x interval snaps to span (lock-step), never below
        // -----------------------------------------------------------------

        [Fact]
        public void Cre3_SubTwoXInterval_SnapsToSpan_NeverBelow()
        {
            // (CRE-3) A player-entered interval between 1x and 2x the span (1.4x here)
            // rounds to N=1 and is rewritten back DOWN to exactly the span. That is the
            // documented lock-step intent (whole-span cadence; one crossing == one
            // cycle), and it can NEVER undercut the span. Span = dock(1200) -
            // launch(1000) = 200 (segment ends at the dock); entered interval = 280
            // (1.4x) -> N=1 -> interval 200.
            Recording source = MakeKscSource(
                startUT: 1000.0, endUT: 1500.0, dockUT: 1200.0, undockUT: 1400.0);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(interval: 280.0), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Assert.Equal(1, outcome.Route.CadenceMultiplier);
            Assert.Equal(200.0, outcome.Route.TransitDuration);
            // Re-derived interval lands exactly at the span, never below it.
            Assert.Equal(200.0, outcome.Route.DispatchInterval);
            Assert.True(outcome.Route.DispatchInterval >= outcome.Route.TransitDuration);
        }
    }

    /// <summary>
    /// Tiny helper to set the UT-anchor fields on a <see cref="Recording"/>
    /// used by tests. <see cref="Recording.StartUT"/> / <see cref="Recording.EndUT"/>
    /// are computed properties; this routes through the test entry points so
    /// fixtures stay declarative.
    /// </summary>
    internal static class RecordingTimingExtensions
    {
        internal static Recording WithUtSpan(this Recording rec, double startUT, double endUT)
        {
            // StartUT/EndUT are computed properties derived from
            // trajectory bounds or the Explicit* anchors when no points
            // exist. For these unit tests there are no Points, so the
            // Explicit* fields are the only way to pin the span.
            rec.ExplicitStartUT = startUT;
            rec.ExplicitEndUT = endUT;
            return rec;
        }
    }
}
