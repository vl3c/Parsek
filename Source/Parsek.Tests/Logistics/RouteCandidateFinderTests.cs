using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins <see cref="RouteCandidateFinder"/>: the "fully sealed" gate
    /// (every recording <see cref="MergeState.Immutable"/>) and the derivation
    /// of Supply Run candidates from committed trees that are sealed, eligible,
    /// and not already promoted to a route.
    /// </summary>
    [Collection("Sequential")]
    public class RouteCandidateFinderTests : IDisposable
    {
        public RouteCandidateFinderTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---- IsTreeFullySealed ----

        [Fact]
        public void IsTreeFullySealed_NullOrEmpty_ReturnsFalse()
        {
            Assert.False(RouteCandidateFinder.IsTreeFullySealed(null));
            Assert.False(RouteCandidateFinder.IsTreeFullySealed(new RecordingTree { Id = "t" }));
        }

        [Fact]
        public void IsTreeFullySealed_AllImmutable_ReturnsTrue()
        {
            RecordingTree tree = BuildEligibleTree("t-sealed");
            Assert.True(RouteCandidateFinder.IsTreeFullySealed(tree));
        }

        [Fact]
        public void IsTreeFullySealed_OneCommittedProvisional_ReturnsFalse()
        {
            RecordingTree tree = BuildEligibleTree("t-prov");
            tree.Recordings["mid"].MergeState = MergeState.CommittedProvisional;
            Assert.False(RouteCandidateFinder.IsTreeFullySealed(tree));
        }

        [Fact]
        public void IsTreeFullySealed_OneNotCommitted_ReturnsFalse()
        {
            RecordingTree tree = BuildEligibleTree("t-nc");
            tree.Recordings["root"].MergeState = MergeState.NotCommitted;
            Assert.False(RouteCandidateFinder.IsTreeFullySealed(tree));
        }

        // ---- DeriveCandidates ----

        [Fact]
        public void DeriveCandidates_EligibleSealedTree_ReturnsCandidate()
        {
            RecordingTree tree = BuildEligibleTree("t1");
            var candidates = RouteCandidateFinder.DeriveCandidates(
                new List<RecordingTree> { tree }, new List<Route>());

            Assert.Single(candidates);
            Assert.Same(tree, candidates[0].Tree);
            Assert.Equal("mid", candidates[0].Analysis.SourceRecording?.RecordingId);
            Assert.True(candidates[0].Analysis.IsEligible);
        }

        [Fact]
        public void DeriveCandidates_UnsealedTree_Skipped()
        {
            RecordingTree tree = BuildEligibleTree("t1");
            tree.Recordings["mid"].MergeState = MergeState.CommittedProvisional;

            var candidates = RouteCandidateFinder.DeriveCandidates(
                new List<RecordingTree> { tree }, new List<Route>());

            Assert.Empty(candidates);
        }

        [Fact]
        public void DeriveCandidates_IneligibleTree_Skipped()
        {
            // Sealed tree with no route connection window — not eligible.
            var tree = new RecordingTree { Id = "t-empty", RootRecordingId = "root" };
            tree.AddOrReplaceRecording(new Recording { RecordingId = "root", TreeId = "t-empty" });

            var candidates = RouteCandidateFinder.DeriveCandidates(
                new List<RecordingTree> { tree }, new List<Route>());

            Assert.Empty(candidates);
        }

        [Fact]
        public void DeriveCandidates_AlreadyPromoted_Skipped()
        {
            RecordingTree tree = BuildEligibleTree("t1"); // source recording id == "mid"
            var existing = new List<Route>
            {
                new Route
                {
                    Id = "route-mid",
                    SourceRefs = new List<RouteSourceRef>
                    {
                        new RouteSourceRef { RecordingId = "mid", TreeId = "t1" }
                    }
                }
            };

            var candidates = RouteCandidateFinder.DeriveCandidates(
                new List<RecordingTree> { tree }, existing);

            Assert.Empty(candidates);
        }

        [Fact]
        public void DeriveCandidates_NoTrees_ReturnsEmpty()
        {
            Assert.Empty(RouteCandidateFinder.DeriveCandidates(null, null));
            Assert.Empty(RouteCandidateFinder.DeriveCandidates(new List<RecordingTree>(), new List<Route>()));
        }

        // ------------------------------------------------------------------
        // Helpers: build a sealed tree carrying one eligible dock-deliver-undock
        // window on its dock-merged child recording ("mid"). Mirrors the
        // RouteAnalysisEngineTests window shape (transport loses 50 LF that the
        // endpoint gains → matched delivery). All recordings default to
        // MergeState.Immutable, so the tree is fully sealed unless a test flips one.
        // ------------------------------------------------------------------

        private static RecordingTree BuildEligibleTree(string treeId)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                RootRecordingId = "root",
                ActiveRecordingId = null
            };
            tree.BranchPoints = new List<BranchPoint>
            {
                new BranchPoint { Id = "bp-dock", ParentRecordingIds = new List<string> { "root" } },
                new BranchPoint { Id = "bp-undock", ParentRecordingIds = new List<string> { "mid" } }
            };
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "root",
                TreeId = treeId,
                ParentBranchPointId = null
            });
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "mid",
                TreeId = treeId,
                ParentBranchPointId = "bp-dock",
                RouteConnectionWindows = new List<RouteConnectionWindow> { BuildDeliveryWindow() }
            });
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "post",
                TreeId = treeId,
                ParentBranchPointId = "bp-undock"
            });
            return tree;
        }

        private static RouteConnectionWindow BuildDeliveryWindow()
        {
            return new RouteConnectionWindow
            {
                WindowId = "window",
                DockUT = 100.0,
                UndockUT = 160.0,
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                DockTransportResources = Manifest(80.0, 100.0),
                UndockTransportResources = Manifest(30.0, 100.0),
                DockEndpointResources = Manifest(0.0, 200.0),
                UndockEndpointResources = Manifest(50.0, 200.0),
                EndpointAtDock = new RouteEndpoint
                {
                    VesselPersistentId = 9001,
                    BodyName = "Mun",
                    Latitude = 1.0,
                    Longitude = 2.0,
                    Altitude = 3.0,
                    IsSurface = true
                },
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
    }
}
