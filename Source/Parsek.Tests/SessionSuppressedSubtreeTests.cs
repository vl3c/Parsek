using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 2 guards for <see cref="EffectiveState.ComputeSessionSuppressedSubtree"/>
    /// and <see cref="EffectiveState.IsInSessionSuppressedSubtree"/> (design §3.3 /
    /// §7.40). Exercises the forward-only closure with mixed-parent halt at
    /// Dock / Board merges.
    /// </summary>
    [Collection("Sequential")]
    public class SessionSuppressedSubtreeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public SessionSuppressedSubtreeTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // --- Helpers ----------------------------------------------------------

        private static Recording Rec(string id, string treeId,
            string parentBranchPointId = null, string childBranchPointId = null,
            MergeState state = MergeState.Immutable)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                MergeState = state,
                ParentBranchPointId = parentBranchPointId,
                ChildBranchPointId = childBranchPointId
            };
        }

        private static BranchPoint Bp(string id, BranchPointType type,
            List<string> parents = null, List<string> children = null)
        {
            return new BranchPoint
            {
                Id = id,
                Type = type,
                UT = 0.0,
                ParentRecordingIds = parents ?? new List<string>(),
                ChildRecordingIds = children ?? new List<string>()
            };
        }

        private static void InstallTree(string treeId, List<Recording> recordings, List<BranchPoint> branchPoints)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Test_" + treeId,
                BranchPoints = branchPoints ?? new List<BranchPoint>()
            };
            foreach (var rec in recordings)
            {
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec, treeId);
            }
            // AddRecordingWithTreeForTesting creates single-node trees per recording.
            // Override with the shared tree we actually want to install so the
            // walker can find the branch points.
            var trees = RecordingStore.CommittedTrees;
            for (int i = trees.Count - 1; i >= 0; i--)
                trees.RemoveAt(i);
            trees.Add(tree);
        }

        private static ParsekScenario InstallScenario(ReFlySessionMarker marker)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = marker
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        private static ReFlySessionMarker Marker(string originId)
        {
            return new ReFlySessionMarker
            {
                SessionId = "sess_1",
                TreeId = "tree_1",
                ActiveReFlyRecordingId = "rec_provisional",
                OriginChildRecordingId = originId,
                RewindPointId = "rp_1",
                InvokedUT = 0.0
            };
        }

        // =====================================================================
        // Forward-only closure semantics
        // =====================================================================

        [Fact]
        public void ForwardOnlyClosure_LinearChain_IncludesAllDescendants()
        {
            // origin -> bp_c1 -> child1 -> bp_c2 -> child2
            var origin = Rec("rec_origin", "tree_1", childBranchPointId: "bp_c1");
            var child1 = Rec("rec_child1", "tree_1", parentBranchPointId: "bp_c1", childBranchPointId: "bp_c2");
            var child2 = Rec("rec_child2", "tree_1", parentBranchPointId: "bp_c2");

            var bp_c1 = Bp("bp_c1", BranchPointType.Undock,
                parents: new List<string> { "rec_origin" },
                children: new List<string> { "rec_child1" });
            var bp_c2 = Bp("bp_c2", BranchPointType.Undock,
                parents: new List<string> { "rec_child1" },
                children: new List<string> { "rec_child2" });

            InstallTree("tree_1",
                new List<Recording> { origin, child1, child2 },
                new List<BranchPoint> { bp_c1, bp_c2 });
            InstallScenario(Marker("rec_origin"));

            var closure = EffectiveState.ComputeSessionSuppressedSubtree(Marker("rec_origin"));

            Assert.Contains("rec_origin", closure);
            Assert.Contains("rec_child1", closure);
            Assert.Contains("rec_child2", closure);
            Assert.Equal(3, closure.Count);

            // Design §10.3 per-decision log tagged [ReFlySession].
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") && l.Contains("SessionSuppressedSubtree"));
        }

        [Fact]
        public void ForwardOnlyClosure_ExcludesAncestors()
        {
            // parent -> bp_p -> origin -> bp_c -> child
            // Ancestor walk is forbidden: ancestor_rec and parent are NOT in closure.
            var ancestor = Rec("rec_ancestor", "tree_1", childBranchPointId: "bp_p");
            var parent = Rec("rec_parent", "tree_1", parentBranchPointId: "bp_p");
            var origin = Rec("rec_origin", "tree_1", parentBranchPointId: "bp_p", childBranchPointId: "bp_c");
            var child = Rec("rec_child", "tree_1", parentBranchPointId: "bp_c");

            var bp_p = Bp("bp_p", BranchPointType.Undock,
                parents: new List<string> { "rec_ancestor" },
                children: new List<string> { "rec_parent", "rec_origin" });
            var bp_c = Bp("bp_c", BranchPointType.Undock,
                parents: new List<string> { "rec_origin" },
                children: new List<string> { "rec_child" });

            InstallTree("tree_1",
                new List<Recording> { ancestor, parent, origin, child },
                new List<BranchPoint> { bp_p, bp_c });
            InstallScenario(Marker("rec_origin"));

            var closure = EffectiveState.ComputeSessionSuppressedSubtree(Marker("rec_origin"));

            Assert.Contains("rec_origin", closure);
            Assert.Contains("rec_child", closure);
            Assert.DoesNotContain("rec_ancestor", closure);
            Assert.DoesNotContain("rec_parent", closure);
        }

        [Fact]
        public void MixedParentHalt_DockedMergeHaltsClosure()
        {
            // origin -> bp_child -> child1 -> bp_dock(Dock; parents=[child1, outside_rec]) -> merged
            // The dock BP has an outside parent → closure MUST halt before `merged`.
            var origin = Rec("rec_origin", "tree_1", childBranchPointId: "bp_child");
            var child1 = Rec("rec_child1", "tree_1", parentBranchPointId: "bp_child",
                childBranchPointId: "bp_dock");
            var outside = Rec("rec_outside", "tree_1"); // outside the closure
            var merged = Rec("rec_merged", "tree_1", parentBranchPointId: "bp_dock");

            var bp_child = Bp("bp_child", BranchPointType.Undock,
                parents: new List<string> { "rec_origin" },
                children: new List<string> { "rec_child1" });
            var bp_dock = Bp("bp_dock", BranchPointType.Dock,
                parents: new List<string> { "rec_child1", "rec_outside" },
                children: new List<string> { "rec_merged" });

            InstallTree("tree_1",
                new List<Recording> { origin, child1, outside, merged },
                new List<BranchPoint> { bp_child, bp_dock });
            InstallScenario(Marker("rec_origin"));

            var closure = EffectiveState.ComputeSessionSuppressedSubtree(Marker("rec_origin"));

            Assert.Contains("rec_origin", closure);
            Assert.Contains("rec_child1", closure);
            Assert.DoesNotContain("rec_outside", closure);
            Assert.DoesNotContain("rec_merged", closure);

            // Log-assertion: a mixed-parent halt must emit a diagnostic so the
            // decision is visible post-hoc (design §10.3 / §10 tag "ReFlySession").
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") && l.Contains("mixed-parent halt"));
        }

        [Fact]
        public void NullMarker_ReturnsEmpty()
        {
            InstallScenario(marker: null);
            var closure = EffectiveState.ComputeSessionSuppressedSubtree(null);
            Assert.Empty(closure);
        }

        [Fact]
        public void MarkerWithNoChildren_ReturnsOriginOnly()
        {
            // Origin has no ChildBranchPointId → no descendants to walk.
            var origin = Rec("rec_origin", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint>());
            InstallScenario(Marker("rec_origin"));

            var closure = EffectiveState.ComputeSessionSuppressedSubtree(Marker("rec_origin"));
            Assert.Single(closure);
            Assert.Contains("rec_origin", closure);
        }

        [Fact]
        public void IsInSessionSuppressedSubtree_PositiveAndNegativeCases()
        {
            var origin = Rec("rec_origin", "tree_1", childBranchPointId: "bp_c");
            var inside = Rec("rec_inside", "tree_1", parentBranchPointId: "bp_c");
            var outside = Rec("rec_outside", "tree_1");

            var bp_c = Bp("bp_c", BranchPointType.Undock,
                parents: new List<string> { "rec_origin" },
                children: new List<string> { "rec_inside" });

            InstallTree("tree_1",
                new List<Recording> { origin, inside, outside },
                new List<BranchPoint> { bp_c });
            var marker = Marker("rec_origin");
            InstallScenario(marker);

            Assert.True(EffectiveState.IsInSessionSuppressedSubtree(origin, marker));
            Assert.True(EffectiveState.IsInSessionSuppressedSubtree(inside, marker));
            Assert.False(EffectiveState.IsInSessionSuppressedSubtree(outside, marker));

            // Null marker → always false.
            Assert.False(EffectiveState.IsInSessionSuppressedSubtree(inside, null));
        }
    }
}
