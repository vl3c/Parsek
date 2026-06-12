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

        // catches (M1, D7): a sealed tree whose root proves neither a KSC launch
        // nor a docked-origin partner surfacing as a CANDIDATE. The undocked-start
        // workflow rejection must keep it out of the candidate list and route it
        // to the near-miss list carrying the new status.
        [Fact]
        public void DeriveNearMisses_CountsUndockedStart()
        {
            RecordingTree tree = BuildEligibleTree("t-undocked");
            // Strip the origin proof off the root: now an undocked start.
            tree.Recordings["root"].StartBodyName = null;
            tree.Recordings["root"].LaunchSiteName = null;

            var candidates = RouteCandidateFinder.DeriveCandidates(
                new List<RecordingTree> { tree }, new List<Route>());
            var nearMisses = RouteCandidateFinder.DeriveNearMisses(
                new List<RecordingTree> { tree });

            Assert.Empty(candidates);
            RouteNearMiss nm = Assert.Single(nearMisses);
            Assert.False(nm.NotSealed);
            Assert.Equal(RouteAnalysisStatus.UndockedStartOrigin, nm.Status);
        }

        // catches (M2, plan Phase 4 step 4): a sealed tree rejected with the
        // new UntrackedCargoGain status not surfacing in the per-reason
        // breakdown counters and the near-miss list (with the quantity
        // detail). The fixture gives the lineage complete run manifests and
        // an unwitnessed Ore gain.
        [Fact]
        public void DeriveNearMisses_CountsUntrackedGain_WithDetail()
        {
            // Re-enable the log so the batch-summary counter line is assertable.
            var lines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            try
            {
                RecordingTree tree = BuildUntrackedGainTree("t-untracked");

                var candidates = RouteCandidateFinder.DeriveCandidates(
                    new List<RecordingTree> { tree }, new List<Route>());
                var nearMisses = RouteCandidateFinder.DeriveNearMisses(
                    new List<RecordingTree> { tree });

                Assert.Empty(candidates);
                RouteNearMiss nm = Assert.Single(nearMisses);
                Assert.False(nm.NotSealed);
                Assert.Equal(RouteAnalysisStatus.UntrackedCargoGain, nm.Status);
                Assert.Equal("Ore: 120.0 gained, 0.0 harvested", nm.RejectDetail);
                Assert.Contains(lines, l =>
                    l.Contains("DeriveCandidates:") && l.Contains("untrackedGain=1"));
                Assert.Contains(lines, l =>
                    l.Contains("DeriveNearMisses:") && l.Contains("untrackedGain=1"));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
                ParsekLog.SuppressLogging = true;
            }
        }

        // Sealed two-recording tree (KSC root -> Dock BP -> merge child with
        // the window) whose lineage carries COMPLETE run manifests showing an
        // unwitnessed 120 Ore transport gain: analysis rejects
        // UntrackedCargoGain (M2 plan D6).
        private static RecordingTree BuildUntrackedGainTree(string treeId)
        {
            Dictionary<string, ResourceAmount> OreAt(double amount)
            {
                return new Dictionary<string, ResourceAmount>
                {
                    ["Ore"] = new ResourceAmount { amount = amount, maxAmount = 1000.0 }
                };
            }

            var tree = new RecordingTree
            {
                Id = treeId,
                RootRecordingId = "root",
                ActiveRecordingId = "merge"
            };
            var root = new Recording
            {
                RecordingId = "root",
                TreeId = treeId,
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 500.0,
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { 100 },
                    StartTransportResources = OreAt(0.0),
                    EndTransportResources = OreAt(120.0),
                    EndCaptured = true
                }
            };
            var merge = new Recording
            {
                RecordingId = "merge",
                TreeId = treeId,
                ExplicitStartUT = 500.0,
                ExplicitEndUT = 600.0,
                ParentBranchPointId = "bp-dock",
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { 100, 300 },
                    StartTransportResources = OreAt(120.0),
                    EndTransportResources = OreAt(120.0),
                    EndCaptured = true
                },
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "w-ore",
                        DockUT = 500.0,
                        UndockUT = 600.0,
                        TransferTargetVesselPid = 9001,
                        TransferKind = RouteConnectionKind.DockingPort,
                        TransportPartPersistentIds = new List<uint> { 100 },
                        DockTransportResources = OreAt(120.0),
                        UndockTransportResources = OreAt(20.0),
                        DockEndpointResources = OreAt(0.0),
                        UndockEndpointResources = OreAt(100.0),
                        EndpointAtDock = new RouteEndpoint
                        {
                            VesselPersistentId = 9001,
                            BodyName = "Mun",
                            Latitude = 1.0,
                            Longitude = 2.0,
                            Altitude = 3.0,
                            IsSurface = true
                        },
                        TransferEndpointSituation = 1
                    }
                }
            };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(merge);
            tree.BranchPoints = new List<BranchPoint>
            {
                new BranchPoint
                {
                    Id = "bp-dock",
                    UT = 500.0,
                    Type = BranchPointType.Dock,
                    ParentRecordingIds = new List<string> { "root" },
                    ChildRecordingIds = new List<string> { "merge" }
                }
            };
            return tree;
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
                ParentBranchPointId = null,
                // Root = origin recording for the M1 undocked-start gate; a KSC
                // origin keeps the tree eligible.
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad"
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
