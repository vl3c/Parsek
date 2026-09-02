using System;
using System.Collections.Generic;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The "basic ground supply route" shape, pinned against a real flight
    /// (save <c>logistics-rover-a</c>, 2026-08-30): a KSC-Runway launch, two
    /// rovers, a surface dock on the Kerbin flats, and a fuel + inventory
    /// transfer. Every fixture constant is the flight's measured value and
    /// lives on <see cref="RouteWindowFixtures"/> with its provenance.
    ///
    /// <para><b>What this file pins that nothing else did.</b> The suite's
    /// existing KSC-origin route cells all dock at an ORBITING endpoint
    /// (<c>TransferEndpointSituation = 4</c>, a station), and the one
    /// LANDED-endpoint cell is a Minmus DEPOT-origin run. The combination a
    /// ground supply route actually is — KSC origin AND a landed surface
    /// endpoint — was untested end to end.</para>
    /// </summary>
    [Collection("Sequential")]
    public class GroundSupplyRouteTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GroundSupplyRouteTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            // The analysis engine probes PartResourceLibrary for resource
            // routability; reset to the production default (headless null
            // library treats every name as defined).
            ResourceTransferability.ResetForTesting();
        }

        public void Dispose()
        {
            ResourceTransferability.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private const string TreeId = "tree-ground-supply";
        private const string RootRecordingId = "rover-root";
        private const string DockChildRecordingId = "rover-dock-merge";
        private const string BranchPointId = "bp-rover-dock";

        // ------------------------------------------------------------------
        // (a) Rover-route end to end: AnalyzeTree -> BuildRoute
        // ------------------------------------------------------------------

        // catches: the KSC-origin + LANDED-surface-endpoint combination
        // regressing anywhere along analyze -> build. A surface endpoint
        // losing IsSurface (or its body-fixed coordinates) would send the
        // dispatch-time endpoint resolver hunting an orbital rendezvous for a
        // rover parked 65 m up on the Kerbin flats.
        [Fact]
        public void GroundSupplyRun_KscOriginLandedEndpoint_AnalyzesAndBuilds()
        {
            RecordingTree tree = BuildRoverSupplyTree(out Recording root, out Recording dockChild);

            RouteAnalysisResult analysis = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.True(analysis.IsEligible,
                $"the rover ground supply run must analyze Eligible, got {analysis.Status}");
            Assert.Same(dockChild, analysis.SourceRecording);

            // Resource delivery: the flight's 97.6 LiquidFuel, exactly.
            Assert.NotNull(analysis.ResourceDeliveryManifest);
            Assert.Single(analysis.ResourceDeliveryManifest);
            Assert.Equal(
                RouteWindowFixtures.RoverLiquidFuelDelivered,
                analysis.ResourceDeliveryManifest[RouteWindowFixtures.RoverDeliveredResourceName],
                6);
            // Pure delivery: nothing came back the other way.
            Assert.Null(analysis.ResourceLoadManifest);
            Assert.Null(analysis.InventoryLoadManifest);

            // Inventory delivery: one evaChute + one evaScienceKit. The
            // manifest is ordered by IdentityHash, so compare as a set.
            Assert.NotNull(analysis.InventoryDeliveryManifest);
            Assert.Equal(2, analysis.InventoryDeliveryManifest.Count);
            var deliveredParts = new List<string>();
            for (int i = 0; i < analysis.InventoryDeliveryManifest.Count; i++)
            {
                InventoryPayloadItem item = analysis.InventoryDeliveryManifest[i];
                deliveredParts.Add(item.PartName);
                Assert.Equal(1, item.Quantity);
                Assert.Equal(1, item.SlotsTaken);
            }
            Assert.Contains(RouteWindowFixtures.RoverInventoryPartA, deliveredParts);
            Assert.Contains(RouteWindowFixtures.RoverInventoryPartB, deliveredParts);

            // One stop, and it is the LANDED transfer. TransferEndpointSituation
            // lives on the connection window (it is an analysis-gate input and is
            // deliberately NOT persisted onto the built RouteStop).
            Assert.Single(analysis.Stops);
            Assert.Equal(
                RouteWindowFixtures.LandedSituation,
                analysis.Stops[0].ConnectionWindow.TransferEndpointSituation);
            Assert.Equal(RouteWindowFixtures.RoverDockUT, analysis.Stops[0].DockUT);

            // The rendered span is [root launch .. dock]; feeding it back as the
            // dispatch interval yields the natural single-span cadence.
            double span = RouteWindowFixtures.RoverDockUT - RouteWindowFixtures.RoverRootSpanStartUT;
            RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                analysis,
                tree,
                new RouteBuilder.RouteCreationInputs
                {
                    Name = "Runway Fuel Run",
                    DispatchIntervalSeconds = span
                },
                Game.Modes.SANDBOX);

            Assert.Null(outcome.RejectReason);
            Route route = outcome.Route;
            Assert.NotNull(route);

            // Origin: KSC. The launch site lives on the tree ROOT, not on the
            // window-carrying dock child.
            Assert.True(route.IsKscOrigin);
            Assert.False(route.IsHarvestOrigin);
            Assert.Equal("Kerbin", route.Origin.BodyName);
            Assert.True(route.Origin.IsSurface);
            Assert.Equal(RouteWindowFixtures.RoverLaunchSiteName, root.LaunchSiteName);

            // Stop: the LANDED rover, body-fixed coordinates intact.
            Assert.Single(route.Stops);
            RouteStop stop = route.Stops[0];
            Assert.Equal(RouteConnectionKind.DockingPort, stop.ConnectionKind);
            Assert.True(stop.Endpoint.IsSurface);
            Assert.Equal(RouteWindowFixtures.RoverBodyName, stop.Endpoint.BodyName);
            Assert.Equal(RouteWindowFixtures.RoverEndpointPid, stop.Endpoint.VesselPersistentId);
            Assert.Equal(RouteWindowFixtures.RoverEndpointLatitude, stop.Endpoint.Latitude, 9);
            Assert.Equal(RouteWindowFixtures.RoverEndpointLongitude, stop.Endpoint.Longitude, 9);
            Assert.Equal(RouteWindowFixtures.RoverEndpointAltitude, stop.Endpoint.Altitude, 9);

            // Span + cadence: interval == span at N = 1.
            Assert.Equal(span, route.TransitDuration, 6);
            Assert.Equal(1, route.CadenceMultiplier);
            Assert.Equal(route.TransitDuration, route.DispatchInterval, 9);
            Assert.Equal(RouteWindowFixtures.RoverRootSpanStartUT, route.DispatchWindowEpochUT, 9);
            Assert.Equal(RouteWindowFixtures.RoverDockUT, route.RecordedDockUT, 9);

            // Stop cargo counts: one resource, two inventory identities.
            Assert.Single(stop.DeliveryManifest);
            Assert.Equal(
                RouteWindowFixtures.RoverLiquidFuelDelivered,
                stop.DeliveryManifest[RouteWindowFixtures.RoverDeliveredResourceName],
                6);
            Assert.Equal(2, stop.InventoryDeliveryManifest.Count);

            // Both tree recordings are revalidation-tracked (root..dock path).
            Assert.Contains(RootRecordingId, route.RecordingIds);
            Assert.Contains(DockChildRecordingId, route.RecordingIds);
        }

        // ------------------------------------------------------------------
        // (b) Codec round-trip through the production tree-metadata pair
        // ------------------------------------------------------------------

        // catches: RecordingBuilder's new route-proof authoring drifting from
        // the format RouteProofCodec actually writes/reads - a fixture that
        // authors a window the production parse cannot read back would make
        // every downstream synthetic route test measure the wrong thing.
        [Fact]
        public void RouteProofAuthoredByBuilder_RoundTripsThroughTreeMetadataCodec()
        {
            RouteConnectionWindow authored = RouteWindowFixtures.SurfaceDeliveryWindow();
            RouteOriginProof authoredProof = BuildRoverOriginProof();

            ConfigNode node = new RecordingBuilder("Supply Rover")
                .WithRecordingId(DockChildRecordingId)
                .WithRouteConnectionWindow(authored)
                .WithRouteOriginProof(authoredProof)
                .BuildV3Metadata();

            // The production tree-metadata pair (RecordingTree ->
            // RecordingTreeRecordCodec -> RouteProofCodec), the same one
            // RouteProofSerializationTests round-trips through.
            var loaded = new Recording { RecordingId = DockChildRecordingId };
            RecordingTree.LoadRecordingResourceAndState(node, loaded);

            Assert.NotNull(loaded.RouteConnectionWindows);
            RouteConnectionWindow window = Assert.Single(loaded.RouteConnectionWindows);
            Assert.Equal(authored.WindowId, window.WindowId);
            Assert.Equal(RouteWindowFixtures.RoverDockUT, window.DockUT);
            Assert.Equal(RouteWindowFixtures.RoverUndockUT, window.UndockUT);
            Assert.True(window.IsComplete);
            Assert.Equal(RouteWindowFixtures.RoverEndpointPid, window.TransferTargetVesselPid);
            Assert.Equal(RouteConnectionKind.DockingPort, window.TransferKind);
            Assert.Equal(RouteWindowFixtures.LandedSituation, window.TransferEndpointSituation);
            Assert.Equal(
                new List<uint> { RouteWindowFixtures.RoverTransportPid },
                window.TransportPartPersistentIds);
            Assert.Equal(
                new List<uint> { RouteWindowFixtures.RoverEndpointPid },
                window.EndpointPartPersistentIds);

            // Endpoint-at-dock: the surface flag AND the body-fixed coordinates.
            Assert.True(window.EndpointAtDock.HasValue);
            RouteEndpoint endpoint = window.EndpointAtDock.Value;
            Assert.True(endpoint.IsSurface);
            Assert.Equal(RouteWindowFixtures.RoverBodyName, endpoint.BodyName);
            Assert.Equal(RouteWindowFixtures.RoverEndpointLatitude, endpoint.Latitude, 9);
            Assert.Equal(RouteWindowFixtures.RoverEndpointLongitude, endpoint.Longitude, 9);
            Assert.Equal(RouteWindowFixtures.RoverEndpointAltitude, endpoint.Altitude, 9);

            // Cargo corners.
            Assert.Equal(
                RouteWindowFixtures.RoverLiquidFuelDelivered,
                window.DockTransportResources[RouteWindowFixtures.RoverDeliveredResourceName].amount,
                9);
            Assert.Equal(
                0.0,
                window.UndockTransportResources[RouteWindowFixtures.RoverDeliveredResourceName].amount,
                9);
            Assert.Equal(
                RouteWindowFixtures.RoverLiquidFuelDelivered,
                window.UndockEndpointResources[RouteWindowFixtures.RoverDeliveredResourceName].amount,
                9);
            Assert.Null(window.UndockTransportInventory);
            Assert.Null(window.DockEndpointInventory);

            // Inventory KIND keys survive - the key every delivery / pickup
            // match is made on.
            Assert.Equal(2, window.DockTransportInventory.Count);
            for (int i = 0; i < authored.DockTransportInventory.Count; i++)
            {
                Assert.Equal(
                    authored.DockTransportInventory[i].IdentityHash,
                    window.DockTransportInventory[i].IdentityHash);
                Assert.Equal(
                    authored.DockTransportInventory[i].PartName,
                    window.DockTransportInventory[i].PartName);
                Assert.Equal("STOREDPART",
                    window.DockTransportInventory[i].StoredPartSnapshot.name);
            }
            Assert.Equal(2, window.UndockEndpointInventory.Count);

            // Origin proof, including the M1 endpoint descriptor.
            Assert.NotNull(loaded.RouteOriginProof);
            RouteOriginProof proof = loaded.RouteOriginProof;
            Assert.Equal(
                authoredProof.StartDockedOriginVesselPid, proof.StartDockedOriginVesselPid);
            Assert.Equal(RouteWindowFixtures.RoverBodyName, proof.StartDockedOriginBodyName);
            Assert.True(proof.StartDockedOriginIsSurface);
            Assert.Equal(RouteWindowFixtures.LandedSituation, proof.StartDockedOriginSituation);
            Assert.Equal(
                RouteWindowFixtures.RoverEndpointLatitude, proof.StartDockedOriginLatitude, 9);
            Assert.Equal(
                RouteWindowFixtures.RoverLiquidFuelDelivered,
                proof.StartTransportResources[RouteWindowFixtures.RoverDeliveredResourceName].amount,
                9);
            Assert.Equal(
                authoredProof.StartTransportInventory[0].IdentityHash,
                proof.StartTransportInventory[0].IdentityHash);
        }

        // ------------------------------------------------------------------
        // (c) VesselSnapshotBuilder inventory helper fidelity
        // ------------------------------------------------------------------

        // catches: the new AddStoredPartToInventory emitting a node shape
        // production cannot walk (wrong module name, a second inventory module
        // per call, a missing STOREDPARTS wrapper) - the helper would silently
        // author snapshots whose payloads extract as nothing.
        [Fact]
        public void AddStoredPartToInventory_ExtractsThroughProductionWalk()
        {
            ConfigNode snapshot = BuildRoverCargoSnapshot();

            List<InventoryPayloadItem> items =
                VesselSpawner.ExtractInventoryPayloadItems(snapshot);

            Assert.NotNull(items);
            Assert.Equal(2, items.Count);

            var byPartName = new Dictionary<string, InventoryPayloadItem>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++)
                byPartName[items[i].PartName] = items[i];

            Assert.True(byPartName.ContainsKey(RouteWindowFixtures.RoverInventoryPartA));
            Assert.True(byPartName.ContainsKey(RouteWindowFixtures.RoverInventoryPartB));
            foreach (InventoryPayloadItem item in byPartName.Values)
            {
                Assert.Equal(1, item.Quantity);
                Assert.Equal(1, item.SlotsTaken);
                Assert.False(string.IsNullOrEmpty(item.IdentityHash));
                Assert.Equal("STOREDPART", item.StoredPartSnapshot.name);
            }

            // Both stored parts landed in ONE ModuleInventoryPart, the way a
            // real part snapshot writes them (a second module would double the
            // extracted SlotsTaken for a repeated identity).
            ConfigNode hostPart = snapshot.GetNodes("PART")[0];
            ConfigNode[] modules = hostPart.GetNodes("MODULE");
            int inventoryModules = 0;
            for (int i = 0; i < modules.Length; i++)
            {
                if (modules[i].GetValue("name") == "ModuleInventoryPart")
                    inventoryModules++;
            }
            Assert.Equal(1, inventoryModules);
            ConfigNode inventoryModule = FindInventoryModule(hostPart);
            Assert.Equal(2, inventoryModule.GetNode("STOREDPARTS").GetNodes("STOREDPART").Length);

            // Identity is stable across an identical rebuild: the hash is a
            // function of the stored geometry, not of build order or instance.
            List<InventoryPayloadItem> rebuilt =
                VesselSpawner.ExtractInventoryPayloadItems(BuildRoverCargoSnapshot());
            Assert.Equal(items.Count, rebuilt.Count);
            for (int i = 0; i < items.Count; i++)
                Assert.Equal(items[i].IdentityHash, rebuilt[i].IdentityHash);
        }

        // ------------------------------------------------------------------
        // (d) Candidate surfacing + the sealed gate
        // ------------------------------------------------------------------

        // catches: the sealed-tree gate flipping in either direction. A sealed
        // rover tree must surface as a Create-Route candidate; a still-re-flyable
        // one must NOT (an open CommittedProvisional recording can be rewritten,
        // which is exactly what disqualified the H35 fixture).
        [Fact]
        public void SealedRoverTree_SurfacesAsCandidate_ProvisionalChildHidesIt()
        {
            RecordingTree tree = BuildRoverSupplyTree(out _, out Recording dockChild);
            var trees = new List<RecordingTree> { tree };
            var noRoutes = new List<Route>();

            List<RouteCandidate> sealedCandidates =
                RouteCandidateFinder.DeriveCandidates(trees, noRoutes);

            RouteCandidate candidate = Assert.Single(sealedCandidates);
            Assert.Same(tree, candidate.Tree);
            Assert.True(candidate.Analysis.IsEligible);
            Assert.Empty(RouteCandidateFinder.DeriveNearMisses(trees));

            // Re-open the dock-merged child: the proof can still change, so the
            // tree stops being a candidate and becomes a not-sealed near-miss.
            dockChild.MergeState = MergeState.CommittedProvisional;

            Assert.Empty(RouteCandidateFinder.DeriveCandidates(trees, noRoutes));

            RouteNearMiss nearMiss = Assert.Single(RouteCandidateFinder.DeriveNearMisses(trees));
            Assert.Same(tree, nearMiss.Tree);
            Assert.True(nearMiss.NotSealed);
            Assert.Equal(1, nearMiss.ReflyableCount);
        }

        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        /// <summary>
        /// The two-tier committed tree the rover flight produced: a
        /// Runway-launched root, and a dock-merged child carrying the one
        /// surface-delivery connection window. Both recordings are authored
        /// through <see cref="RecordingBuilder"/> and materialized through the
        /// production tree-metadata parse, so the route proof under test is the
        /// one the codec writes.
        /// </summary>
        private static RecordingTree BuildRoverSupplyTree(
            out Recording root, out Recording dockChild)
        {
            root = Materialize(
                new RecordingBuilder("Supply Rover")
                    .WithRecordingId(RootRecordingId)
                    .WithLaunchIdentity(RouteWindowFixtures.RoverLaunchSiteName),
                RootRecordingId,
                treeOrder: 0,
                startUT: RouteWindowFixtures.RoverRootSpanStartUT,
                endUT: RouteWindowFixtures.RoverDockUT);

            dockChild = Materialize(
                new RecordingBuilder("Supply Rover + Depot Rover")
                    .WithRecordingId(DockChildRecordingId)
                    .WithRouteConnectionWindow(RouteWindowFixtures.SurfaceDeliveryWindow()),
                DockChildRecordingId,
                treeOrder: 1,
                startUT: RouteWindowFixtures.RoverDockUT,
                endUT: RouteWindowFixtures.RoverUndockUT,
                parentBranchPointId: BranchPointId);

            var tree = new RecordingTree
            {
                Id = TreeId,
                RootRecordingId = RootRecordingId,
                ActiveRecordingId = DockChildRecordingId
            };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(dockChild);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = BranchPointId,
                ParentRecordingIds = new List<string> { RootRecordingId },
                ChildRecordingIds = new List<string> { DockChildRecordingId }
            });
            return tree;
        }

        /// <summary>
        /// Turns a <see cref="RecordingBuilder"/> into a live
        /// <see cref="Recording"/> by writing its v3 metadata node and reading
        /// it back through the production
        /// <c>RecordingTree.LoadRecordingResourceAndState</c> parse.
        /// <para>
        /// The fields set directly afterwards are the ones the builder does not
        /// author: the tree linkage (<c>TreeId</c> / <c>TreeOrder</c> /
        /// <c>ParentBranchPointId</c>), the UT span (<c>StartUT</c> /
        /// <c>EndUT</c> are computed from trajectory bounds, so the Explicit*
        /// anchors are the only way to pin them without points), and
        /// <c>StartBodyName</c> — <see cref="RecordingBuilder.WithLaunchIdentity"/>
        /// covers the launch site + situation but not the body, and the
        /// KSC-origin gate needs both halves.
        /// </para>
        /// </summary>
        private static Recording Materialize(
            RecordingBuilder builder,
            string recordingId,
            int treeOrder,
            double startUT,
            double endUT,
            string parentBranchPointId = null,
            string startBodyName = RouteWindowFixtures.RoverBodyName)
        {
            ConfigNode node = builder.BuildV3Metadata();

            var rec = new Recording { RecordingId = recordingId };
            RecordingTree.LoadRecordingResourceAndState(node, rec);

            rec.TreeId = TreeId;
            rec.TreeOrder = treeOrder;
            rec.ParentBranchPointId = parentBranchPointId;
            rec.StartBodyName = startBodyName;
            rec.ExplicitStartUT = startUT;
            rec.ExplicitEndUT = endUT;
            return rec;
        }

        /// <summary>
        /// The start-docked origin proof shape (M1 descriptor + cargo), used
        /// only by the codec round-trip cell — the rover run itself is
        /// KSC-launched and carries no origin proof.
        /// </summary>
        private static RouteOriginProof BuildRoverOriginProof()
        {
            return new RouteOriginProof
            {
                StartDockedOriginVesselPid = RouteWindowFixtures.RoverEndpointPid,
                StartDockedOriginBodyName = RouteWindowFixtures.RoverBodyName,
                StartDockedOriginLatitude = RouteWindowFixtures.RoverEndpointLatitude,
                StartDockedOriginLongitude = RouteWindowFixtures.RoverEndpointLongitude,
                StartDockedOriginAltitude = RouteWindowFixtures.RoverEndpointAltitude,
                StartDockedOriginIsSurface = true,
                StartDockedOriginSituation = RouteWindowFixtures.LandedSituation,
                StartTransportResources = new Dictionary<string, ResourceAmount>
                {
                    [RouteWindowFixtures.RoverDeliveredResourceName] = new ResourceAmount
                    {
                        amount = RouteWindowFixtures.RoverLiquidFuelDelivered,
                        maxAmount = RouteWindowFixtures.RoverLiquidFuelDelivered
                    }
                },
                StartTransportInventory = new List<InventoryPayloadItem>
                {
                    RouteWindowFixtures.StoredPartPayload(RouteWindowFixtures.RoverInventoryPartA)
                }
            };
        }

        /// <summary>
        /// A one-part transport snapshot carrying the flight's two cargo items
        /// in a single inventory module.
        /// </summary>
        private static ConfigNode BuildRoverCargoSnapshot()
        {
            return new VesselSnapshotBuilder()
                .WithName("Supply Rover")
                .WithPersistentId(RouteWindowFixtures.RoverTransportPid)
                .AddPart("probeCoreSphere")
                .AddStoredPartToInventory(0, RouteWindowFixtures.RoverInventoryPartA, slot: 0)
                .AddStoredPartToInventory(0, RouteWindowFixtures.RoverInventoryPartB, slot: 1)
                .AsLanded(
                    RouteWindowFixtures.RoverEndpointLatitude,
                    RouteWindowFixtures.RoverEndpointLongitude,
                    RouteWindowFixtures.RoverEndpointAltitude)
                .Build();
        }

        private static ConfigNode FindInventoryModule(ConfigNode partNode)
        {
            ConfigNode[] modules = partNode.GetNodes("MODULE");
            for (int i = 0; i < modules.Length; i++)
            {
                if (modules[i].GetValue("name") == "ModuleInventoryPart")
                    return modules[i];
            }
            return null;
        }
    }
}
