using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Parsek;
using Parsek.Logistics;
using Xunit;
using Xunit.Abstractions;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// THE ORACLE for the origin-binder defect
    /// (ROUTE-ORIGIN-PROOF-BIND-FOLLOWS-FOCUS-NOT-THE-RUN). The fixture
    /// <c>Fixtures/RoverRelayC/rover-c-tree.cfg</c> is the VERBATIM
    /// <c>RECORDING_TREE</c> node of rover C, dedented to column 0, lifted out of
    /// the operator's hand-flown save
    /// <c>logs/2026-09-03_0026_rover-c/saves/logistics-rover-c/persistent.sfs</c>.
    ///
    /// <para>WHAT THE OPERATOR FLEW: rover C launched from the Runway with 200
    /// LiquidFuel, drove to rover B, docked at UT 155.82 and TOOK 154.4
    /// LiquidFuel plus three inventory items off B, undocked at UT 212.54, drove
    /// to lander A, docked at UT 274.18, GAVE A 200 LiquidFuel, and undocked at
    /// UT 335.32. Under the operator ruling of 2026-09-02 that is exactly one
    /// supply route: source B, destination A.</para>
    ///
    /// <para>The session's own log shows the run refused with zero candidates.
    /// These cells are the headless replay of that refusal and of the fix.</para>
    /// </summary>
    public class RoverRelayCOracleTests
    {
        private readonly ITestOutputHelper output;

        public RoverRelayCOracleTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        internal const string TreeId = "88c012a6eed94bf09ff73397a4a31410";
        internal const string RootRecordingId = "8604fbc77d54482eae83424b7e401954";
        /// <summary>The dock-merged child at rover B (hop 1, the PICKUP).</summary>
        internal const string PickupRecordingId = "39ac117a8a8b4d61b1296983e7d538a8";
        /// <summary>The dock-merged child at lander A (hop 2, the DELIVERY).</summary>
        internal const string DeliveryRecordingId = "b9df0ee00fd84831a0d9619b4e34fc97";

        internal const uint RoverBPid = 90564594;
        internal const uint LanderAPid = 2123618197;
        internal const uint RoverBRootPartUId = 549109006;
        internal const uint RoverCRootPartUId = 3466447829;
        internal const uint LanderARootPartUId = 701791207;

        internal static string FixturePath()
        {
            return Path.Combine(
                SyntheticRecordingTests.ResolveProjectRoot(),
                "Source", "Parsek.Tests", "Fixtures", "RoverRelayC", "rover-c-tree.cfg");
        }

        internal static RecordingTree LoadTree()
        {
            string path = FixturePath();
            Assert.True(File.Exists(path), $"fixture not found at '{path}'");
            ConfigNode treeNode = ConfigNode.Load(path);
            Assert.NotNull(treeNode);
            RecordingTree tree = RecordingTree.Load(treeNode);
            Assert.NotNull(tree);
            return tree;
        }

        [Fact]
        public void ReportTheOracle()
        {
            RecordingTree tree = LoadTree();
            var sb = new StringBuilder();
            sb.AppendLine($"tree={tree.Id} name='{tree.TreeName}' recordings={tree.Recordings.Count} root={tree.RootRecordingId}");
            foreach (KeyValuePair<string, Recording> kvp in tree.Recordings)
            {
                Recording r = kvp.Value;
                sb.AppendLine(
                    $"  rec={r.RecordingId} vessel='{r.VesselName}' pid={r.VesselPersistentId} " +
                    $"order={r.TreeOrder} launchSite='{r.LaunchSiteName}' " +
                    $"windows={r.RouteConnectionWindows?.Count ?? 0} " +
                    $"proof={(r.RouteOriginProof != null ? r.RouteOriginProof.StartDockedOriginBindState.ToString() : "<none>")}" +
                    (r.RouteOriginProof != null
                        ? $" originRoot={r.RouteOriginProof.StartDockedOriginRootPartUId}" +
                          $" originName='{r.RouteOriginProof.StartDockedOriginVesselName}'" +
                          $" transportRoot={r.RouteOriginProof.StartDockedTransportRootPartUId}" +
                          $" pickup={r.RouteOriginProof.StartDockedOriginPickupKind}" +
                          $" validated={r.RouteOriginProof.StartDockedOriginPickupValidated}"
                        : string.Empty));
                if (r.RouteConnectionWindows != null)
                {
                    for (int i = 0; i < r.RouteConnectionWindows.Count; i++)
                    {
                        RouteConnectionWindow w = r.RouteConnectionWindows[i];
                        sb.AppendLine(
                            $"    window={w.WindowId} dock={w.DockUT.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"undock={w.UndockUT.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"target={w.TransferTargetVesselPid} kind={w.TransferKind} " +
                            $"complete={w.IsComplete} endpoint={(w.EndpointAtDock.HasValue ? "yes" : "no")}");
                    }
                }
            }

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);
            sb.AppendLine($"ANALYSIS status={result.Status} detail={result.RejectDetail ?? "<none>"}");
            sb.AppendLine($"  stops={result.Stops?.Count ?? 0} midTreeDockedOrigin={result.IsMidTreeDockedOrigin} harvestOrigin={result.IsHarvestOrigin}");
            if (result.Stops != null)
            {
                for (int i = 0; i < result.Stops.Count; i++)
                {
                    RouteAnalysisStop s = result.Stops[i];
                    sb.AppendLine(
                        $"  stop[{i}] dockUT={s.DockUT.ToString("R", CultureInfo.InvariantCulture)} " +
                        $"endpointPid={s.EndpointAtDock.VesselPersistentId} " +
                        $"deliver={FormatDouble(s.ResourceDeliveryManifest)} " +
                        $"load={FormatDouble(s.ResourceLoadManifest)} " +
                        $"deliverInv={s.InventoryDeliveryManifest?.Count ?? 0} " +
                        $"loadInv={s.InventoryLoadManifest?.Count ?? 0}");
                }
            }

            output.WriteLine(sb.ToString());
        }

        // catches: the relay silently ceasing to derive. This is THE oracle for the whole
        // package - the operator's own bytes, run through the production engine.
        [Fact]
        public void TheRelayIsEligibleWithRoverBAsTheSourceAndLanderAAsTheDestination()
        {
            RecordingTree tree = LoadTree();
            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.Equal(RouteAnalysisStatus.Eligible, result.Status);
            Assert.NotNull(result.Stops);
            Assert.Equal(2, result.Stops.Count);

            // STOP 0 IS THE SOURCE: rover B, where 154.4 LiquidFuel and three stored parts
            // came ONTO rover C and nothing went the other way.
            RouteAnalysisStop source = result.Stops[0];
            Assert.Equal(RoverBPid, source.EndpointAtDock.VesselPersistentId);
            Assert.Equal(155.8200000000059, source.DockUT, 6);
            Assert.NotNull(source.ResourceLoadManifest);
            Assert.Equal(154.4, source.ResourceLoadManifest["LiquidFuel"], 3);
            Assert.Equal(3, source.InventoryLoadManifest.Count);
            Assert.True(source.ResourceDeliveryManifest == null
                || source.ResourceDeliveryManifest.Count == 0);

            // STOP 1 IS THE DESTINATION: lander A, where the 200 LiquidFuel went off.
            RouteAnalysisStop destination = result.Stops[1];
            Assert.Equal(LanderAPid, destination.EndpointAtDock.VesselPersistentId);
            Assert.Equal(274.18000000004059, destination.DockUT, 6);
            Assert.NotNull(destination.ResourceDeliveryManifest);
            Assert.Equal(200.0, destination.ResourceDeliveryManifest["LiquidFuel"], 3);
            Assert.Equal(3, destination.InventoryDeliveryManifest.Count);
            Assert.True(destination.ResourceLoadManifest == null
                || destination.ResourceLoadManifest.Count == 0);

            // The delivered 200 is covered by the KSC launch manifest, not by an unwitnessed
            // gain: the tree root left the Runway carrying exactly 200 LiquidFuel.
            Recording root = tree.Recordings[RootRecordingId];
            Assert.Equal("Runway", root.LaunchSiteName);
            Assert.Equal("Kerbin", root.StartBodyName);
            Assert.True(RouteAnalysisEngine.IsKscOriginRecording(root));
            Assert.Equal(200.0, root.RouteRunManifest.StartTransportResources["LiquidFuel"].amount, 3);
        }

        // catches: the tree falling out of the candidate sweep for a reason the analysis
        // status does not show (seal state, promotion, dismissal).
        [Fact]
        public void TheTreeYieldsExactlyOneRouteCandidate()
        {
            RecordingTree tree = LoadTree();
            Assert.True(RouteCandidateFinder.IsTreeFullySealed(tree),
                "the harvested tree must be fully sealed, or the sweep never analyzes it");

            List<RouteCandidate> candidates = RouteCandidateFinder.DeriveCandidates(
                new List<RecordingTree> { tree }, new List<Route>());

            Assert.Single(candidates);
            Assert.Equal(TreeId, candidates[0].Tree.Id);
            Assert.Equal(RouteAnalysisStatus.Eligible, candidates[0].Analysis.Status);
        }

        // catches: the pre-fix binder's two wrong proofs being trusted by the read side.
        // The fixture is the ONLY committed copy of what the defect actually wrote.
        //
        // THIS RUNS FROM PERSISTED DATA ALONE. The proofs reach these assertions through the
        // production load path - ConfigNode.Load -> RecordingTree.Load -> RouteProofCodec -
        // and every predicate below is pure over the resulting DTOs, with no live vessel, no
        // recorder and no session state. That is the contract the harness fixture
        // `rover-relay-c-recorded` (harvested from this same save in a sibling worktree) will
        // rely on: it ships these two WRONG proofs on purpose, and nothing in the analysis may
        // need a live bind to refuse them.
        [Fact]
        public void ThePersistedProofsAreTheOnesTheDefectWroteAndBothAreRefused()
        {
            RecordingTree tree = LoadTree();

            // HOP 1: THE INVERSION. The proof names rover C - the transport, the recording's
            // own subject - as the origin and rover B, the depot it took the fuel off, as the
            // transport. Both stamps are the focus reading, and both are the wrong way round.
            // Note the roots are NOT equal on disk, so this is not the literal self-origin
            // shape IsSelfOriginProof catches; what refuses it is that the binder measured the
            // pickup on the WRONG half and could not witness a gain.
            RouteOriginProof hop1 = tree.Recordings[PickupRecordingId].RouteOriginProof;
            Assert.NotNull(hop1);
            Assert.Equal(StartDockedOriginBindState.BoundAtUndock, hop1.StartDockedOriginBindState);
            Assert.Equal(RoverCRootPartUId, hop1.StartDockedOriginRootPartUId);
            Assert.Equal(RoverBRootPartUId, hop1.StartDockedTransportRootPartUId);
            Assert.Equal("C", hop1.StartDockedOriginVesselName);
            Assert.False(hop1.StartDockedOriginPickupValidated);
            Assert.Equal(OriginPickupKind.Carried, hop1.StartDockedOriginPickupKind);
            Assert.False(RouteAnalysisEngine.HasDockedOriginProof(
                tree.Recordings[PickupRecordingId]));

            // HOP 2: the delivery partner stamped as the origin.
            RouteOriginProof hop2 = tree.Recordings[DeliveryRecordingId].RouteOriginProof;
            Assert.NotNull(hop2);
            Assert.Equal(LanderARootPartUId, hop2.StartDockedOriginRootPartUId);
            Assert.Equal("A", hop2.StartDockedOriginVesselName);
            Assert.False(hop2.StartDockedOriginPickupValidated);
            Assert.False(RouteAnalysisEngine.HasDockedOriginProof(
                tree.Recordings[DeliveryRecordingId]));

            // NEITHER refusal needs live state: both predicates are pure over the DTOs the
            // codec produced, so a fixture that ships these bytes refuses them at load.
            Assert.False(RouteAnalysisEngine.IsSelfOriginProof(hop1));
            Assert.False(RouteAnalysisEngine.IsSelfOriginProof(hop2));
            Assert.False(RouteAnalysisEngine.HasDockedOriginProof(
                new Recording { RecordingId = "detached", RouteOriginProof = hop1 }));
            Assert.False(RouteAnalysisEngine.HasDockedOriginProof(
                new Recording { RecordingId = "detached", RouteOriginProof = hop2 }));

            // AND THE RELAY DERIVES ANYWAY, because the tree ROOT is a KSC launch: the
            // undocked-start gate never consults these proofs. That is why the two wrong
            // proofs cost this particular run nothing once the inventory kind key (#1620)
            // let the pickup window through - and why the binder fix is about the bytes
            // being wrong, not about this run's verdict.
            Assert.Equal(RouteAnalysisStatus.Eligible, RouteAnalysisEngine.AnalyzeTree(tree).Status);
        }

        private static string FormatDouble(Dictionary<string, double> m)
        {
            if (m == null || m.Count == 0) return "<none>";
            var parts = new List<string>();
            foreach (KeyValuePair<string, double> kvp in m)
                parts.Add($"{kvp.Key}={kvp.Value.ToString("R", CultureInfo.InvariantCulture)}");
            return string.Join(";", parts);
        }
    }
}
