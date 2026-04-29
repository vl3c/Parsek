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

        private static ReFlySessionMarker Marker(string originId, string supersedeTargetId = null)
        {
            return new ReFlySessionMarker
            {
                SessionId = "sess_1",
                TreeId = "tree_1",
                ActiveReFlyRecordingId = "rec_provisional",
                OriginChildRecordingId = originId,
                SupersedeTargetId = supersedeTargetId,
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
        public void NullOrigin_ReturnsEmpty()
        {
            InstallScenario(Marker(null));

            var closure = EffectiveState.ComputeSessionSuppressedSubtree(Marker(null));

            Assert.Empty(closure);
        }

        [Fact]
        public void PublicWrapper_ReturnsDefensiveCopy()
        {
            var origin = Rec("rec_origin", "tree_1", childBranchPointId: "bp_c");
            var child = Rec("rec_child", "tree_1", parentBranchPointId: "bp_c");
            var bp = Bp("bp_c", BranchPointType.Undock,
                parents: new List<string> { "rec_origin" },
                children: new List<string> { "rec_child" });
            InstallTree("tree_1",
                new List<Recording> { origin, child },
                new List<BranchPoint> { bp });
            var marker = Marker("rec_origin");
            InstallScenario(marker);

            var first = EffectiveState.ComputeSessionSuppressedSubtree(marker);
            var mutable = Assert.IsType<HashSet<string>>(first);
            mutable.Add("rec_poison");
            var second = EffectiveState.ComputeSessionSuppressedSubtree(marker);

            Assert.DoesNotContain("rec_poison", second);
            Assert.Contains("rec_child", second);
        }

        [Fact]
        public void InternalClosure_OriginRootMatchesPublicWrapper()
        {
            var origin = Rec("rec_origin", "tree_1", childBranchPointId: "bp_c");
            var child = Rec("rec_child", "tree_1", parentBranchPointId: "bp_c");
            var bp = Bp("bp_c", BranchPointType.Undock,
                parents: new List<string> { "rec_origin" },
                children: new List<string> { "rec_child" });
            InstallTree("tree_1",
                new List<Recording> { origin, child },
                new List<BranchPoint> { bp });
            var marker = Marker("rec_origin");
            InstallScenario(marker);

            var publicClosure = EffectiveState.ComputeSessionSuppressedSubtree(marker);
            var internalClosure = EffectiveState.ComputeSubtreeClosureInternal(
                marker, marker.OriginChildRecordingId);

            Assert.True(new HashSet<string>(publicClosure).SetEquals(internalClosure));
        }

        [Fact]
        public void PublicWrapper_RemainsOriginRootedWhenSupersedeTargetDiffers()
        {
            var origin = Rec("rec_origin", "tree_1", childBranchPointId: "bp_origin");
            var originChild = Rec("rec_origin_child", "tree_1", parentBranchPointId: "bp_origin");
            var prior = Rec("rec_prior_tip", "tree_1", childBranchPointId: "bp_prior");
            var priorChild = Rec("rec_prior_child", "tree_1", parentBranchPointId: "bp_prior");
            var bpOrigin = Bp("bp_origin", BranchPointType.Undock,
                parents: new List<string> { "rec_origin" },
                children: new List<string> { "rec_origin_child" });
            var bpPrior = Bp("bp_prior", BranchPointType.Undock,
                parents: new List<string> { "rec_prior_tip" },
                children: new List<string> { "rec_prior_child" });
            InstallTree("tree_1",
                new List<Recording> { origin, originChild, prior, priorChild },
                new List<BranchPoint> { bpOrigin, bpPrior });
            var marker = Marker("rec_origin", supersedeTargetId: "rec_prior_tip");
            InstallScenario(marker);

            var publicClosure = EffectiveState.ComputeSessionSuppressedSubtree(marker);
            var mergeClosure = EffectiveState.ComputeSubtreeClosureInternal(
                marker, marker.SupersedeTargetId);

            Assert.Contains("rec_origin", publicClosure);
            Assert.Contains("rec_origin_child", publicClosure);
            Assert.DoesNotContain("rec_prior_tip", publicClosure);
            Assert.DoesNotContain("rec_prior_child", publicClosure);
            Assert.Contains("rec_prior_tip", mergeClosure);
            Assert.Contains("rec_prior_child", mergeClosure);
        }

        [Fact]
        public void InternalClosure_CacheKeyIncludesRootOverride()
        {
            var origin = Rec("rec_origin", "tree_1", childBranchPointId: "bp_origin");
            var originChild = Rec("rec_origin_child", "tree_1", parentBranchPointId: "bp_origin");
            var prior = Rec("rec_prior_tip", "tree_1", childBranchPointId: "bp_prior");
            var priorChild = Rec("rec_prior_child", "tree_1", parentBranchPointId: "bp_prior");
            var bpOrigin = Bp("bp_origin", BranchPointType.Undock,
                parents: new List<string> { "rec_origin" },
                children: new List<string> { "rec_origin_child" });
            var bpPrior = Bp("bp_prior", BranchPointType.Undock,
                parents: new List<string> { "rec_prior_tip" },
                children: new List<string> { "rec_prior_child" });
            InstallTree("tree_1",
                new List<Recording> { origin, originChild, prior, priorChild },
                new List<BranchPoint> { bpOrigin, bpPrior });
            var marker = Marker("rec_origin");
            InstallScenario(marker);

            var originClosure = EffectiveState.ComputeSubtreeClosureInternal(
                marker, "rec_origin");
            var priorClosure = EffectiveState.ComputeSubtreeClosureInternal(
                marker, "rec_prior_tip");

            Assert.Contains("rec_origin_child", originClosure);
            Assert.DoesNotContain("rec_prior_child", originClosure);
            Assert.Contains("rec_prior_child", priorClosure);
            Assert.DoesNotContain("rec_origin_child", priorClosure);
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

        // =====================================================================
        // Chain-sibling expansion (item 23 / fix-chain-sibling-supersede)
        //
        // Merge-time RecordingOptimizer.SplitAtSection splits a single live
        // recording at env boundaries (atmo<->exo) into a ChainId-linked
        // HEAD + TIP. The HEAD keeps the parent-branch-point link to the
        // RewindPoint and ends with ChildBranchPointId=null; the TIP carries
        // the terminal=Destroyed and (if any) the ChildBranchPointId from
        // before the split. The closure walker must expand chain siblings
        // sharing both ChainId and ChainBranch so a re-fly merge supersede
        // covers the entire chain instead of leaving the TIP orphaned as a
        // stale "kerbal destroyed in atmo" row alongside the new re-fly.
        // =====================================================================

        [Fact]
        public void ChainExpansion_HeadOrigin_IncludesTip()
        {
            // HEAD has the BP link, TIP has null ParentBp + the moved
            // ChildBranchPointId (post-split shape, RecordingStore.cs:2018-2019).
            // For this test the chain has no further BP descendants.
            var head = Rec("rec_head", "tree_1", parentBranchPointId: "bp_split");
            head.ChainId = "chain_a";
            head.ChainBranch = 0;
            head.ChainIndex = 0;
            var tip = Rec("rec_tip", "tree_1", state: MergeState.Immutable);
            tip.ChainId = "chain_a";
            tip.ChainBranch = 0;
            tip.ChainIndex = 1;
            tip.TerminalStateValue = TerminalState.Destroyed;

            // bp_split represents the EVA / undock that produced the chained
            // recording in the first place; it lists head as its child.
            var bp_split = Bp("bp_split", BranchPointType.EVA,
                parents: new List<string> { "rec_parent" },
                children: new List<string> { "rec_head" });

            InstallTree("tree_1",
                new List<Recording> { head, tip },
                new List<BranchPoint> { bp_split });
            InstallScenario(Marker("rec_head"));

            var closure = EffectiveState.ComputeSessionSuppressedSubtree(Marker("rec_head"));

            Assert.Contains("rec_head", closure);
            Assert.Contains("rec_tip", closure);
            Assert.Equal(2, closure.Count);

            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") && l.Contains("siblingsAdded="));
        }

        [Fact]
        public void ChainExpansion_TipOrigin_IncludesHead()
        {
            // Symmetric: marker points at the TIP (defensive for future marker
            // shape changes — chain expansion runs on every dequeued member,
            // not just origin, so HEAD must still be picked up).
            var head = Rec("rec_head", "tree_1", parentBranchPointId: "bp_split");
            head.ChainId = "chain_a";
            head.ChainBranch = 0;
            head.ChainIndex = 0;
            var tip = Rec("rec_tip", "tree_1");
            tip.ChainId = "chain_a";
            tip.ChainBranch = 0;
            tip.ChainIndex = 1;
            tip.TerminalStateValue = TerminalState.Destroyed;

            var bp_split = Bp("bp_split", BranchPointType.EVA,
                parents: new List<string> { "rec_parent" },
                children: new List<string> { "rec_head" });

            InstallTree("tree_1",
                new List<Recording> { head, tip },
                new List<BranchPoint> { bp_split });
            InstallScenario(Marker("rec_tip"));

            var closure = EffectiveState.ComputeSessionSuppressedSubtree(Marker("rec_tip"));

            Assert.Contains("rec_head", closure);
            Assert.Contains("rec_tip", closure);
            Assert.Equal(2, closure.Count);
        }

        [Fact]
        public void ChainExpansion_DifferentChainBranch_Excluded()
        {
            // Same ChainId, different ChainBranch → siblings independent
            // (per IsChainMemberOfUnfinishedFlight contract). Ghost-only
            // parallel continuations on ChainBranch>0 must not auto-suppress.
            var seg0 = Rec("rec_seg0", "tree_1", parentBranchPointId: "bp_split");
            seg0.ChainId = "chain_a";
            seg0.ChainBranch = 0;
            seg0.ChainIndex = 0;
            var seg1 = Rec("rec_seg1", "tree_1");
            seg1.ChainId = "chain_a";
            seg1.ChainBranch = 0;
            seg1.ChainIndex = 1;
            seg1.TerminalStateValue = TerminalState.Destroyed;
            var seg_alt = Rec("rec_seg_alt", "tree_1");
            seg_alt.ChainId = "chain_a";
            seg_alt.ChainBranch = 1;       // different branch
            seg_alt.ChainIndex = 0;

            var bp_split = Bp("bp_split", BranchPointType.EVA,
                parents: new List<string> { "rec_parent" },
                children: new List<string> { "rec_seg0" });

            InstallTree("tree_1",
                new List<Recording> { seg0, seg1, seg_alt },
                new List<BranchPoint> { bp_split });
            InstallScenario(Marker("rec_seg0"));

            var closure = EffectiveState.ComputeSessionSuppressedSubtree(Marker("rec_seg0"));

            Assert.Contains("rec_seg0", closure);
            Assert.Contains("rec_seg1", closure);
            Assert.DoesNotContain("rec_seg_alt", closure);
            Assert.Equal(2, closure.Count);
        }

        [Fact]
        public void ChainExpansion_ThreeSegments_AllIncluded()
        {
            // Multi-env crossing produces a 3-segment chain (e.g. exo -> atmo
            // -> ground impact). All three siblings on ChainBranch=0 must
            // end up in the closure regardless of which one the marker names.
            var seg0 = Rec("rec_seg0", "tree_1", parentBranchPointId: "bp_split");
            seg0.ChainId = "chain_b";
            seg0.ChainBranch = 0;
            seg0.ChainIndex = 0;
            var seg1 = Rec("rec_seg1", "tree_1");
            seg1.ChainId = "chain_b";
            seg1.ChainBranch = 0;
            seg1.ChainIndex = 1;
            var seg2 = Rec("rec_seg2", "tree_1");
            seg2.ChainId = "chain_b";
            seg2.ChainBranch = 0;
            seg2.ChainIndex = 2;
            seg2.TerminalStateValue = TerminalState.Destroyed;

            var bp_split = Bp("bp_split", BranchPointType.EVA,
                parents: new List<string> { "rec_parent" },
                children: new List<string> { "rec_seg0" });

            InstallTree("tree_1",
                new List<Recording> { seg0, seg1, seg2 },
                new List<BranchPoint> { bp_split });
            InstallScenario(Marker("rec_seg0"));

            var closure = EffectiveState.ComputeSessionSuppressedSubtree(Marker("rec_seg0"));

            Assert.Contains("rec_seg0", closure);
            Assert.Contains("rec_seg1", closure);
            Assert.Contains("rec_seg2", closure);
            Assert.Equal(3, closure.Count);
        }

        [Fact]
        public void ChainExpansion_DifferentTree_Excluded()
        {
            // Defense-in-depth: a recording carrying the same ChainId +
            // ChainBranch but a DIFFERENT TreeId must NOT be pulled into
            // the closure. SplitAtSection always emits same-tree chain
            // segments by construction (RecordingStore.cs sets
            // second.TreeId = original.TreeId at line 1992), but a future
            // clone path / import / legacy save could collide ChainIds
            // across trees, and supersede + tombstone consumers reuse
            // this closure — silently crossing tree boundaries would hide
            // unrelated recordings or retire kerbal-death actions stamped
            // against another mission.
            var head = Rec("rec_head", "tree_1", parentBranchPointId: "bp_split");
            head.ChainId = "chain_a";
            head.ChainBranch = 0;
            head.ChainIndex = 0;
            var tip = Rec("rec_tip", "tree_1");
            tip.ChainId = "chain_a";
            tip.ChainBranch = 0;
            tip.ChainIndex = 1;
            tip.TerminalStateValue = TerminalState.Destroyed;
            var foreign = Rec("rec_foreign", "tree_2");
            foreign.ChainId = "chain_a";    // colliding ChainId
            foreign.ChainBranch = 0;        // colliding ChainBranch
            foreign.ChainIndex = 0;

            var bp_split = Bp("bp_split", BranchPointType.EVA,
                parents: new List<string> { "rec_parent" },
                children: new List<string> { "rec_head" });

            // Multi-tree install: tree_1 owns head+tip, tree_2 owns foreign.
            var tree1 = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Test_tree_1",
                BranchPoints = new List<BranchPoint> { bp_split }
            };
            tree1.AddOrReplaceRecording(head);
            tree1.AddOrReplaceRecording(tip);
            var tree2 = new RecordingTree
            {
                Id = "tree_2",
                TreeName = "Test_tree_2",
                BranchPoints = new List<BranchPoint>()
            };
            tree2.AddOrReplaceRecording(foreign);

            RecordingStore.AddRecordingWithTreeForTesting(head, "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(tip, "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(foreign, "tree_2");

            var trees = RecordingStore.CommittedTrees;
            for (int i = trees.Count - 1; i >= 0; i--)
                trees.RemoveAt(i);
            trees.Add(tree1);
            trees.Add(tree2);

            InstallScenario(Marker("rec_head"));

            var closure = EffectiveState.ComputeSessionSuppressedSubtree(Marker("rec_head"));

            Assert.Contains("rec_head", closure);
            Assert.Contains("rec_tip", closure);
            Assert.DoesNotContain("rec_foreign", closure);
            Assert.Equal(2, closure.Count);
        }

        [Fact]
        public void ChainExpansion_TipWithChildBranchPointId_BpDescendantsAlsoIncluded()
        {
            // After SplitAtSection, the TIP receives the moved
            // ChildBranchPointId. If the TIP has its own BP descendant
            // (e.g. the kerbal docked back with the mother before crashing),
            // the chain expansion must enqueue the TIP so the BP walk runs
            // on it and picks up the downstream child.
            var head = Rec("rec_head", "tree_1", parentBranchPointId: "bp_split");
            head.ChainId = "chain_c";
            head.ChainBranch = 0;
            head.ChainIndex = 0;
            var tip = Rec("rec_tip", "tree_1", childBranchPointId: "bp_downstream");
            tip.ChainId = "chain_c";
            tip.ChainBranch = 0;
            tip.ChainIndex = 1;
            var downstream = Rec("rec_downstream", "tree_1", parentBranchPointId: "bp_downstream");

            var bp_split = Bp("bp_split", BranchPointType.EVA,
                parents: new List<string> { "rec_parent" },
                children: new List<string> { "rec_head" });
            var bp_downstream = Bp("bp_downstream", BranchPointType.Undock,
                parents: new List<string> { "rec_tip" },
                children: new List<string> { "rec_downstream" });

            InstallTree("tree_1",
                new List<Recording> { head, tip, downstream },
                new List<BranchPoint> { bp_split, bp_downstream });
            InstallScenario(Marker("rec_head"));

            var closure = EffectiveState.ComputeSessionSuppressedSubtree(Marker("rec_head"));

            Assert.Contains("rec_head", closure);
            Assert.Contains("rec_tip", closure);
            Assert.Contains("rec_downstream", closure);
            Assert.Equal(3, closure.Count);
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

        // =====================================================================
        // Same-PID-only BP-children gate (bug fix-refly-suppress-side-off,
        // 2026-04-27).
        //
        // Reproduces the LU staging case from KSP.log 2026-04-27_2157:
        // origin U (pid X) re-flown after staging; the BP child A continues
        // U's PID (linear continuation, MUST be in closure); the BP child B
        // is the side-off lower-stage L (different PID Y, MUST NOT be in
        // closure — L is a separate physical vessel still standing on its own
        // chain). The new flight will produce its own side-offs at its own
        // future staging events; those will supersede old side-offs at that
        // future moment, not now.
        // =====================================================================

        [Fact]
        public void BpChildrenWalk_SidePidChild_Excluded()
        {
            // origin U (pid 100) -> bp_stage -> child A (pid 100, linear) +
            //                                   child B (pid 200, side-off / L)
            const uint upperPid = 100u;
            const uint lowerPid = 200u;

            var origin = Rec("rec_origin_U", "tree_1", childBranchPointId: "bp_stage");
            origin.VesselPersistentId = upperPid;
            var sameLine = Rec("rec_child_U_continuation", "tree_1",
                parentBranchPointId: "bp_stage");
            sameLine.VesselPersistentId = upperPid;
            var sideOff = Rec("rec_child_L_lower", "tree_1",
                parentBranchPointId: "bp_stage");
            sideOff.VesselPersistentId = lowerPid;

            var bp_stage = Bp("bp_stage", BranchPointType.Undock,
                parents: new List<string> { "rec_origin_U" },
                children: new List<string> { "rec_child_U_continuation", "rec_child_L_lower" });

            InstallTree("tree_1",
                new List<Recording> { origin, sameLine, sideOff },
                new List<BranchPoint> { bp_stage });
            InstallScenario(Marker("rec_origin_U"));

            var closure = EffectiveState.ComputeSessionSuppressedSubtree(Marker("rec_origin_U"));

            Assert.Contains("rec_origin_U", closure);
            Assert.Contains("rec_child_U_continuation", closure);
            Assert.DoesNotContain("rec_child_L_lower", closure);
            Assert.Equal(2, closure.Count);

            // Verbose log line for the skipped side-off.
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("skipped side-off")
                && l.Contains("rec_child_L_lower"));
            // Summary counter present.
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("SessionSuppressedSubtree")
                && l.Contains("sideOffSkips=1"));
        }

        [Fact]
        public void BpChildrenWalk_DownstreamOfSideOff_AlsoExcluded()
        {
            // origin U (pid 100) -> bp_stage -> child U_cont (pid 100) [in]
            //                                  child L (pid 200) [side-off, OUT]
            //                                                    -> bp_l_dock -> L_merged (pid 200) [OUT — never enqueued]
            // Confirms the side-off skip stops walking the L subtree entirely.
            const uint upperPid = 100u;
            const uint lowerPid = 200u;

            var origin = Rec("rec_origin_U", "tree_1", childBranchPointId: "bp_stage");
            origin.VesselPersistentId = upperPid;
            var uCont = Rec("rec_u_cont", "tree_1", parentBranchPointId: "bp_stage");
            uCont.VesselPersistentId = upperPid;
            var lSide = Rec("rec_l_side", "tree_1",
                parentBranchPointId: "bp_stage", childBranchPointId: "bp_l_dock");
            lSide.VesselPersistentId = lowerPid;
            var lDownstream = Rec("rec_l_downstream", "tree_1",
                parentBranchPointId: "bp_l_dock");
            lDownstream.VesselPersistentId = lowerPid;

            var bp_stage = Bp("bp_stage", BranchPointType.Undock,
                parents: new List<string> { "rec_origin_U" },
                children: new List<string> { "rec_u_cont", "rec_l_side" });
            var bp_l_dock = Bp("bp_l_dock", BranchPointType.Undock,
                parents: new List<string> { "rec_l_side" },
                children: new List<string> { "rec_l_downstream" });

            InstallTree("tree_1",
                new List<Recording> { origin, uCont, lSide, lDownstream },
                new List<BranchPoint> { bp_stage, bp_l_dock });
            InstallScenario(Marker("rec_origin_U"));

            var closure = EffectiveState.ComputeSessionSuppressedSubtree(Marker("rec_origin_U"));

            Assert.Contains("rec_origin_U", closure);
            Assert.Contains("rec_u_cont", closure);
            Assert.DoesNotContain("rec_l_side", closure);
            Assert.DoesNotContain("rec_l_downstream", closure);
            Assert.Equal(2, closure.Count);
        }

        [Fact]
        public void BpChildrenWalk_BothPidsZero_LegacyWideWalk_Preserved()
        {
            // Legacy / unset PID on both sides (== 0) — fall back to the prior
            // wide-walk behavior so legacy data is not silently re-scoped.
            // Same fixture as ForwardOnlyClosure_LinearChain_IncludesAllDescendants
            // but with sibling structure to mirror the side-off case.
            var origin = Rec("rec_origin", "tree_1", childBranchPointId: "bp_c");
            // Both siblings have VesselPersistentId == 0 (default).
            var siblingA = Rec("rec_sibA", "tree_1", parentBranchPointId: "bp_c");
            var siblingB = Rec("rec_sibB", "tree_1", parentBranchPointId: "bp_c");

            var bp_c = Bp("bp_c", BranchPointType.Undock,
                parents: new List<string> { "rec_origin" },
                children: new List<string> { "rec_sibA", "rec_sibB" });

            InstallTree("tree_1",
                new List<Recording> { origin, siblingA, siblingB },
                new List<BranchPoint> { bp_c });
            InstallScenario(Marker("rec_origin"));

            var closure = EffectiveState.ComputeSessionSuppressedSubtree(Marker("rec_origin"));

            // PID 0 == 0 ⇒ both children admitted (legacy wide walk).
            Assert.Contains("rec_origin", closure);
            Assert.Contains("rec_sibA", closure);
            Assert.Contains("rec_sibB", closure);
            Assert.Equal(3, closure.Count);
        }

        [Fact]
        public void BpChildrenWalk_OriginPidZero_ChildPidNonZero_AdmittedAsLegacy()
        {
            // Asymmetric unknown-PID: origin has unset PID (legacy data),
            // child has a real PID. We cannot tell whether the child is a
            // side-off branch or a same-vessel continuation that simply got
            // its PID assigned later, so the gate must NOT skip — preserve the
            // prior wide-walk behavior and admit the child. Only the fully
            // known asymmetric case (both PIDs nonzero AND different) is a
            // confident side-off.
            var origin = Rec("rec_origin", "tree_1", childBranchPointId: "bp_c");
            // origin.VesselPersistentId left as 0
            var child = Rec("rec_child", "tree_1", parentBranchPointId: "bp_c");
            child.VesselPersistentId = 200u;

            var bp_c = Bp("bp_c", BranchPointType.Undock,
                parents: new List<string> { "rec_origin" },
                children: new List<string> { "rec_child" });

            InstallTree("tree_1",
                new List<Recording> { origin, child },
                new List<BranchPoint> { bp_c });
            InstallScenario(Marker("rec_origin"));

            var closure = EffectiveState.ComputeSessionSuppressedSubtree(Marker("rec_origin"));

            Assert.Contains("rec_origin", closure);
            Assert.Contains("rec_child", closure);
            Assert.Equal(2, closure.Count);
        }

        [Fact]
        public void BpChildrenWalk_OriginPidNonZero_ChildPidZero_AdmittedAsLegacy()
        {
            // Symmetric edge: origin has a real PID, child has unset PID
            // (legacy seed). Same reasoning — unknown PID on the child means
            // we cannot confidently classify it as side-off. Admit it and
            // preserve the wide-walk behavior for legacy data.
            var origin = Rec("rec_origin", "tree_1", childBranchPointId: "bp_c");
            origin.VesselPersistentId = 100u;
            var child = Rec("rec_child", "tree_1", parentBranchPointId: "bp_c");
            // child.VesselPersistentId left as 0

            var bp_c = Bp("bp_c", BranchPointType.Undock,
                parents: new List<string> { "rec_origin" },
                children: new List<string> { "rec_child" });

            InstallTree("tree_1",
                new List<Recording> { origin, child },
                new List<BranchPoint> { bp_c });
            InstallScenario(Marker("rec_origin"));

            var closure = EffectiveState.ComputeSessionSuppressedSubtree(Marker("rec_origin"));

            Assert.Contains("rec_origin", closure);
            Assert.Contains("rec_child", closure);
            Assert.Equal(2, closure.Count);
        }
    }
}
