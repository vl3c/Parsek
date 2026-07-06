using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the M6 candidate intent helper: the RouteStore-owned dismissed
    /// candidate tree-id set (dismiss / restore / persistence / stale-id sweep)
    /// and the <see cref="RouteCandidateFinder"/> skip that removes a dismissed
    /// tree from BOTH the candidates and the near-miss lists.
    /// Touches RouteStore + RecordingStore + ParsekLog shared static state, so
    /// the class is Sequential with full resets in constructor and Dispose.
    /// </summary>
    [Collection("Sequential")]
    public class RouteCandidateDismissTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteCandidateDismissTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            // The resets themselves emit log lines; clear so each test asserts
            // only on its own output.
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Dismiss / restore mutators
        // ------------------------------------------------------------------

        // catches: a dismiss that mutates silently (no Info line with the tree
        // id + display name) or fails to register in the membership check.
        [Fact]
        public void DismissCandidateTree_HappyPath_AddsAndLogsInfo()
        {
            bool ok = RouteStore.DismissCandidateTree("t-dismiss", "Mun Run");

            Assert.True(ok);
            Assert.True(RouteStore.IsCandidateDismissed("t-dismiss"));
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[Route]")
                && l.Contains("Candidate dismissed")
                && l.Contains("treeId=t-dismiss")
                && l.Contains("name='Mun Run'"));
        }

        [Fact]
        public void DismissCandidateTree_NullEmptyOrDuplicate_NoOp()
        {
            Assert.False(RouteStore.DismissCandidateTree(null, "x"));
            Assert.False(RouteStore.DismissCandidateTree("", "x"));
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("DismissCandidateTree") && l.Contains("null or empty"));

            Assert.True(RouteStore.DismissCandidateTree("t-dup", "Dup"));
            Assert.False(RouteStore.DismissCandidateTree("t-dup", "Dup"));
            Assert.True(RouteStore.IsCandidateDismissed("t-dup"));
            Assert.Contains(logLines, l =>
                l.Contains("already dismissed") && l.Contains("t-dup"));
        }

        // catches: a restore that leaves the id behind (tree stays hidden
        // forever) or restores silently.
        [Fact]
        public void RestoreCandidateTree_RemovesAndLogsInfo()
        {
            RouteStore.DismissCandidateTree("t-restore", "Minmus Run");

            bool ok = RouteStore.RestoreCandidateTree("t-restore", "Minmus Run");

            Assert.True(ok);
            Assert.False(RouteStore.IsCandidateDismissed("t-restore"));
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[Route]")
                && l.Contains("Candidate restored")
                && l.Contains("treeId=t-restore")
                && l.Contains("name='Minmus Run'"));
        }

        [Fact]
        public void RestoreCandidateTree_NotDismissed_WarnNoOp()
        {
            Assert.False(RouteStore.RestoreCandidateTree("t-never", "Never"));
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("RestoreCandidateTree") && l.Contains("not dismissed"));
        }

        // ------------------------------------------------------------------
        // Finder skip: dismissed trees leave the WHOLE Candidates section
        // ------------------------------------------------------------------

        // catches: DeriveCandidates offering a dismissed tree, or the batch
        // summary losing the dismissed counter.
        [Fact]
        public void DeriveCandidates_DismissedTree_SkippedWithBatchCounter()
        {
            RecordingTree tree = BuildEligibleTree("t1");
            var dismissed = new HashSet<string>(StringComparer.Ordinal) { "t1" };

            var candidates = RouteCandidateFinder.DeriveCandidates(
                new List<RecordingTree> { tree }, new List<Route>(), dismissed);

            Assert.Empty(candidates);
            Assert.Contains(logLines, l =>
                l.Contains("DeriveCandidates:") && l.Contains("dismissed=1") && l.Contains("candidates=0"));
        }

        // catches: a dismissed tree still surfacing as a near-miss (the section
        // is supposed to lose the tree entirely, not just its candidate row).
        [Fact]
        public void DeriveNearMisses_DismissedTree_SkippedWithBatchCounter()
        {
            RecordingTree tree = BuildEligibleTree("t-nm");
            // Un-seal one recording so the undismissed tree WOULD be a near-miss.
            tree.Recordings["mid"].MergeState = MergeState.CommittedProvisional;

            var withoutDismiss = RouteCandidateFinder.DeriveNearMisses(
                new List<RecordingTree> { tree });
            Assert.Single(withoutDismiss);

            var dismissed = new HashSet<string>(StringComparer.Ordinal) { "t-nm" };
            var withDismiss = RouteCandidateFinder.DeriveNearMisses(
                new List<RecordingTree> { tree }, dismissed);

            Assert.Empty(withDismiss);
            Assert.Contains(logLines, l =>
                l.Contains("DeriveNearMisses:") && l.Contains("dismissed=1") && l.Contains("nearMisses=0"));
        }

        // catches: restore not re-including the tree on the next finder pass
        // (the live RouteStore set is the production input).
        [Fact]
        public void DismissThenRestore_ReincludesCandidate()
        {
            RecordingTree tree = BuildEligibleTree("t-cycle");
            var trees = new List<RecordingTree> { tree };
            var routes = new List<Route>();

            RouteStore.DismissCandidateTree("t-cycle", "Cycle");
            Assert.Empty(RouteCandidateFinder.DeriveCandidates(
                trees, routes, RouteStore.DismissedCandidateTreeIds));

            RouteStore.RestoreCandidateTree("t-cycle", "Cycle");
            var candidates = RouteCandidateFinder.DeriveCandidates(
                trees, routes, RouteStore.DismissedCandidateTreeIds);

            Assert.Single(candidates);
            Assert.Same(tree, candidates[0].Tree);
        }

        // ------------------------------------------------------------------
        // Persistence: sparse sibling node + round-trip + stale-id sweep
        // ------------------------------------------------------------------

        // catches: an empty set leaking a DISMISSED_ROUTE_CANDIDATES node into
        // the save, or a stale node from a prior save surviving an empty-set save.
        [Fact]
        public void SaveRoutesTo_EmptySet_WritesNothingAndStripsStaleNode()
        {
            var parent = new ConfigNode("SCENARIO");
            parent.AddNode("DISMISSED_ROUTE_CANDIDATES").AddValue("treeId", "stale");

            RouteStore.SaveRoutesTo(parent);

            Assert.Null(parent.GetNode("DISMISSED_ROUTE_CANDIDATES"));
        }

        // catches: the dismissed set not surviving a save/load round-trip, or
        // the sweep wrongly dropping ids whose trees still exist.
        [Fact]
        public void SaveLoad_RoundTrip_PersistsDismissedIds()
        {
            RecordingStore.AddCommittedTreeForTesting(new RecordingTree { Id = "t-a" });
            RecordingStore.AddCommittedTreeForTesting(new RecordingTree { Id = "t-b" });
            RouteStore.DismissCandidateTree("t-a", "A");
            RouteStore.DismissCandidateTree("t-b", "B");

            var parent = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(parent);

            ConfigNode dismissedNode = parent.GetNode("DISMISSED_ROUTE_CANDIDATES");
            Assert.NotNull(dismissedNode);
            Assert.Equal(2, dismissedNode.GetValues("treeId").Length);

            // Wholesale replace on load: dirty the in-memory set first so the
            // round-trip provably comes from the node, not leftover state.
            RouteStore.DismissCandidateTree("t-leftover", "Leftover");
            RouteStore.LoadRoutesFrom(parent);

            Assert.True(RouteStore.IsCandidateDismissed("t-a"));
            Assert.True(RouteStore.IsCandidateDismissed("t-b"));
            Assert.False(RouteStore.IsCandidateDismissed("t-leftover"));
        }

        // catches: a save with dismissals but zero routes losing the set (the
        // dismissed node is a SIBLING of ROUTES, loaded before the ROUTES check).
        [Fact]
        public void Load_ZeroRoutesWithDismissals_RestoresSet()
        {
            RecordingStore.AddCommittedTreeForTesting(new RecordingTree { Id = "t-solo" });
            RouteStore.DismissCandidateTree("t-solo", "Solo");

            var parent = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(parent);
            Assert.Null(parent.GetNode("ROUTES")); // no routes stored

            RouteStore.ResetForTesting();
            int loaded = RouteStore.LoadRoutesFrom(parent);

            Assert.Equal(0, loaded);
            Assert.True(RouteStore.IsCandidateDismissed("t-solo"));
        }

        // catches: stale ids of deleted / pruned trees lingering in the set
        // forever (and the sweep Verbose count going missing).
        [Fact]
        public void LoadRoutesFrom_SweepsStaleDismissedIds()
        {
            RecordingStore.AddCommittedTreeForTesting(new RecordingTree { Id = "t-live" });
            RouteStore.DismissCandidateTree("t-live", "Live");
            RouteStore.DismissCandidateTree("t-gone", "Gone");

            var parent = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(parent);

            // Simulate "t-gone" deleted before the next load: it is in the save
            // node but no committed tree carries the id anymore.
            RouteStore.LoadRoutesFrom(parent);

            Assert.True(RouteStore.IsCandidateDismissed("t-live"));
            Assert.False(RouteStore.IsCandidateDismissed("t-gone"));
            Assert.Contains(logLines, l =>
                l.Contains("SweepStaleDismissedCandidates")
                && l.Contains("swept=1")
                && l.Contains("kept=1"));
        }

        // Direct sweep-seam test: null tree list sweeps everything (no live ids).
        [Fact]
        public void SweepStaleDismissedCandidates_NullTrees_SweepsAll()
        {
            RouteStore.DismissCandidateTree("t-x", "X");
            RouteStore.DismissCandidateTree("t-y", "Y");

            int swept = RouteStore.SweepStaleDismissedCandidates(null);

            Assert.Equal(2, swept);
            Assert.Empty(RouteStore.DismissedCandidateTreeIds);
        }

        // catches: ResetForTesting leaking the dismissed set into the next test.
        [Fact]
        public void ResetForTesting_ClearsDismissedSet()
        {
            RouteStore.DismissCandidateTree("t-reset", "Reset");
            Assert.True(RouteStore.IsCandidateDismissed("t-reset"));

            RouteStore.ResetForTesting();

            Assert.False(RouteStore.IsCandidateDismissed("t-reset"));
            Assert.Empty(RouteStore.DismissedCandidateTreeIds);
        }

        // ------------------------------------------------------------------
        // Fixture: sealed eligible tree (mirrors RouteCandidateFinderTests).
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
