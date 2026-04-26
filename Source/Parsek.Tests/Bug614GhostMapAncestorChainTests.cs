using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #614 (2026-04-26 playtest follow-up to #587 / #611): the parent-chain
    /// suppression walk in <see cref="GhostMapPresence.IsRecordingInParentChainOfActiveReFly"/>
    /// followed only <see cref="Recording.ParentBranchPointId"/> links and stopped
    /// at the first recording whose parent BP was missing — which is the steady
    /// state for the second half of an optimizer-split chain. Optimizer splits
    /// (<see cref="RecordingStore"/> RunOptimizationSplitPass) connect chain
    /// segments via shared <see cref="Recording.ChainId"/> alone; the second
    /// half never receives a <see cref="Recording.ParentBranchPointId"/>. The
    /// pre-#614 walk therefore declared the root recording "not in the parent
    /// chain" and let GhostMap synthesise a state-vector ProtoVessel for it
    /// during an in-place Re-Fly — the bogus sma=2 ecc=1 ghost the user
    /// reported.
    ///
    /// The fix extends the BFS to enqueue the chain predecessor (same
    /// <see cref="Recording.ChainId"/>, <see cref="Recording.ChainBranch"/>,
    /// <see cref="Recording.ChainIndex"/> minus one) for both the active
    /// recording (seed) and every recording reached during fan-out. The walk
    /// trace gains a <c>chainHops</c> counter so a future regression of the
    /// same shape is visible from KSP.log alone.
    /// </summary>
    [Collection("Sequential")]
    public class Bug614GhostMapAncestorChainTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug614GhostMapAncestorChainTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private const string TreeId = "tree-614";

        // -----------------------------------------------------------------
        // The user's exact production shape from
        // logs/2026-04-26_1025_3bugs-refly/KSP.log line ~14211:
        // - root c9df8d86 (Kerbal X) was created at launch
        // - optimizer split it at UT 163.4 producing mid 1d6d2116, sharing
        //   ChainId / ChainIndex 0 -> 1; only mid keeps the Breakup
        //   ChildBranchPointId f1c7b08f — root's BP linkage was rewritten by
        //   RunOptimizationSplitPass to point at mid, leaving root with NO
        //   downstream BP at all.
        // - breakup at UT 163.4 spawned leaf 89eff843 (Kerbal X Probe) whose
        //   ParentBranchPointId is f1c7b08f, with ParentRecordingIds=[mid].
        //
        // The pre-#614 walk for victim=root activeId=leaf:
        //   leaf.ParentBranchPointId = f1c7b08f
        //   -> BP f1c7b08f.ParentRecordingIds = [mid]
        //   -> mid.ParentBranchPointId = NULL (chain-only link to root)
        //   -> walk exhausted, root never reached, doubled vessel created.
        // -----------------------------------------------------------------

        /// <summary>
        /// Builds the user's exact 3-recording chain topology:
        /// root ChainIndex=0, mid ChainIndex=1 (chain-linked to root via
        /// ChainId, no BP link), leaf with ParentBranchPointId pointing at a
        /// Breakup BP whose parent is mid.
        /// </summary>
        private static List<RecordingTree> BuildChainSplitThenBreakupTree(
            string rootId,
            string midId,
            string leafId,
            string sharedChainId,
            string breakupBpId)
        {
            var tree = new RecordingTree { Id = TreeId };
            tree.Recordings[rootId] = new Recording
            {
                RecordingId = rootId,
                TreeId = TreeId,
                VesselName = "Kerbal X",
                VesselPersistentId = 2708531065u,
                ChainId = sharedChainId,
                ChainIndex = 0,
                ChainBranch = 0,
                // Root has no ParentBranchPointId AND no ChildBranchPointId
                // after the optimizer split moves the BP linkage onto `mid`.
            };
            tree.Recordings[midId] = new Recording
            {
                RecordingId = midId,
                TreeId = TreeId,
                VesselName = "Kerbal X",
                VesselPersistentId = 2708531065u,
                ChainId = sharedChainId,
                ChainIndex = 1,
                ChainBranch = 0,
                // Optimizer split contract: second half receives no
                // ParentBranchPointId. The chain link to `root` is via ChainId
                // alone. Mid keeps the original ChildBranchPointId.
                ChildBranchPointId = breakupBpId,
            };
            tree.Recordings[leafId] = new Recording
            {
                RecordingId = leafId,
                TreeId = TreeId,
                VesselName = "Kerbal X Probe",
                VesselPersistentId = 3151978247u,
                ParentBranchPointId = breakupBpId,
            };
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = breakupBpId,
                Type = BranchPointType.Breakup,
                ParentRecordingIds = new List<string> { midId },
                ChildRecordingIds = new List<string> { leafId },
                BreakupCause = "CRASH",
            });
            return new List<RecordingTree> { tree };
        }

        // -----------------------------------------------------------------
        // Regression: root must be suppressed.
        // -----------------------------------------------------------------

        [Fact]
        public void SuppressesRoot_WhenChainSplitMidIsBetweenRootAndLeafActive()
        {
            // The user's named regression: 3-recording chain where leaf is
            // the active Re-Fly target. Both root AND mid must be suppressed.
            var trees = BuildChainSplitThenBreakupTree(
                rootId: "rec-root-c9df8d86",
                midId: "rec-mid-1d6d2116",
                leafId: "rec-leaf-89eff843",
                sharedChainId: "chain-Kerbal-X",
                breakupBpId: "bp-breakup-f1c7b08f");

            Assert.True(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                victimRecordingId: "rec-root-c9df8d86",
                activeRecordingId: "rec-leaf-89eff843",
                searchTrees: trees,
                walkTrace: out string trace));
            Assert.Contains("found-victim-in-parent-chain", trace);
            Assert.Contains("victim=rec-root-c9df8d86", trace);
            // The walk MUST have traversed at least one chain hop to reach
            // root from mid. chainHops=0 here would be a regression.
            Assert.Contains("chainHops=", trace);
            Assert.DoesNotContain("chainHops=0 ", trace);
        }

        [Fact]
        public void SuppressesMid_WhenChainSplitMidIsDirectParentOfLeafActive()
        {
            // Symmetric check: the mid recording is the direct BP-parent of
            // the leaf (via the breakup BP). This already worked pre-#614 —
            // pin it so the chain-aware walk does not break the BP-only path.
            var trees = BuildChainSplitThenBreakupTree(
                rootId: "rec-root-c9df8d86",
                midId: "rec-mid-1d6d2116",
                leafId: "rec-leaf-89eff843",
                sharedChainId: "chain-Kerbal-X",
                breakupBpId: "bp-breakup-f1c7b08f");

            Assert.True(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                victimRecordingId: "rec-mid-1d6d2116",
                activeRecordingId: "rec-leaf-89eff843",
                searchTrees: trees,
                walkTrace: out string trace));
            Assert.Contains("found-victim-in-parent-chain", trace);
            Assert.Contains("victim=rec-mid-1d6d2116", trace);
        }

        // -----------------------------------------------------------------
        // Negative: a sibling branch must NOT be suppressed.
        // -----------------------------------------------------------------

        [Fact]
        public void DoesNotSuppressSibling_WhenLeafIsOnDifferentBranchFromActiveLeaf()
        {
            // Sibling shape: same root c9df8d86 -> mid 1d6d2116, then the
            // breakup BP fans out into two leaves (the active Re-Fly target
            // 89eff843 AND a sibling other-leaf). The other-leaf is a
            // breakup-twin: it shares the BP but is not on the active's
            // ancestor walk, so the walk must NOT reach it from the active.
            //
            // Note the BFS does fan out via BP.ParentRecordingIds, not BP.
            // ChildRecordingIds — so a sibling that shares only the BP as
            // children (not as parents) is correctly invisible to the walk.
            var trees = BuildChainSplitThenBreakupTree(
                rootId: "rec-root-c9df8d86",
                midId: "rec-mid-1d6d2116",
                leafId: "rec-leaf-89eff843",
                sharedChainId: "chain-Kerbal-X",
                breakupBpId: "bp-breakup-f1c7b08f");

            // Add the sibling leaf to the BP's ChildRecordingIds so it shares
            // the same BP id but is NOT in the parents list.
            var tree = trees[0];
            tree.Recordings["rec-other-leaf"] = new Recording
            {
                RecordingId = "rec-other-leaf",
                TreeId = TreeId,
                VesselName = "Kerbal X Debris",
                VesselPersistentId = 4242u,
                ParentBranchPointId = "bp-breakup-f1c7b08f",
            };
            tree.BranchPoints[0].ChildRecordingIds.Add("rec-other-leaf");

            Assert.False(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                victimRecordingId: "rec-other-leaf",
                activeRecordingId: "rec-leaf-89eff843",
                searchTrees: trees,
                walkTrace: out string trace));
            Assert.Contains("exhausted-without-victim", trace);
            Assert.Contains("victim=rec-other-leaf", trace);
        }

        [Fact]
        public void DoesNotSuppressUnrelatedTreeRecording()
        {
            // Belt-and-suspenders: a recording on a totally unrelated tree
            // (different ChainId, different tree) must NOT be matched by the
            // chain-predecessor lookup. The lookup keys on ChainId + ChainBranch
            // + ChainIndex within the active's tree only.
            var trees = BuildChainSplitThenBreakupTree(
                rootId: "rec-root-c9df8d86",
                midId: "rec-mid-1d6d2116",
                leafId: "rec-leaf-89eff843",
                sharedChainId: "chain-Kerbal-X",
                breakupBpId: "bp-breakup-f1c7b08f");

            var foreignTree = new RecordingTree { Id = "tree-foreign" };
            foreignTree.Recordings["rec-foreign"] = new Recording
            {
                RecordingId = "rec-foreign",
                TreeId = "tree-foreign",
                VesselName = "Mun Lander",
                VesselPersistentId = 7777u,
                // Same ChainId / ChainIndex shape as root; the per-tree
                // scoping must prevent cross-tree match.
                ChainId = "chain-Kerbal-X",
                ChainIndex = 0,
                ChainBranch = 0,
            };

            var combined = new List<RecordingTree>(trees) { foreignTree };

            Assert.False(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                victimRecordingId: "rec-foreign",
                activeRecordingId: "rec-leaf-89eff843",
                searchTrees: combined,
                walkTrace: out string trace));
            Assert.Contains("exhausted-without-victim", trace);
        }

        // -----------------------------------------------------------------
        // Multi-hop chain (root -> chain[0] -> chain[1] -> chain[2] -> leaf):
        // every chain ancestor must be reachable, not just the immediate
        // chain predecessor.
        // -----------------------------------------------------------------

        [Fact]
        public void SuppressesAllChainAncestors_WhenChainHasMultipleSplitSegments()
        {
            // Multiple optimizer splits stack: c0 -> c1 -> c2 -> c3 (leaf).
            // The walk from c3 must reach c0 across three chain hops, even
            // though no recording in the chain has a ParentBranchPointId.
            var tree = new RecordingTree { Id = TreeId };
            const string chain = "chain-multi";
            for (int i = 0; i < 4; i++)
            {
                tree.Recordings["c" + i] = new Recording
                {
                    RecordingId = "c" + i,
                    TreeId = TreeId,
                    VesselName = "Kerbal X",
                    VesselPersistentId = 1u,
                    ChainId = chain,
                    ChainIndex = i,
                    ChainBranch = 0,
                };
            }
            var trees = new List<RecordingTree> { tree };

            // Every chain ancestor c0..c2 of leaf c3 is suppressible.
            for (int i = 0; i < 3; i++)
            {
                Assert.True(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                    victimRecordingId: "c" + i,
                    activeRecordingId: "c3",
                    searchTrees: trees,
                    walkTrace: out string trace),
                    $"victim=c{i} should have been found in parent chain of c3");
                Assert.Contains("found-victim-in-parent-chain", trace);
            }

            // c4 isn't in the chain, must NOT be suppressed.
            Assert.False(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                victimRecordingId: "c4",
                activeRecordingId: "c3",
                searchTrees: trees,
                walkTrace: out _));
        }

        // -----------------------------------------------------------------
        // Active recording mid-chain (not at the top): the walk must seed
        // both the BP parent AND the chain predecessor of the active.
        // -----------------------------------------------------------------

        [Fact]
        public void SuppressesChainPredecessor_WhenActiveIsMidChainNoBpParent()
        {
            // Active = mid-chain with no ParentBranchPointId. Pre-#614 the
            // walk bailed immediately ("active-has-no-parent-bp"); post-#614
            // the chain predecessor (root) is reachable.
            var trees = BuildChainSplitThenBreakupTree(
                rootId: "rec-root",
                midId: "rec-mid",
                leafId: "rec-leaf",
                sharedChainId: "chain-X",
                breakupBpId: "bp-1");

            // Active = mid (not leaf). Mid's ParentBranchPointId is null;
            // pre-#614 this returned false with active-has-no-parent-bp.
            Assert.True(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                victimRecordingId: "rec-root",
                activeRecordingId: "rec-mid",
                searchTrees: trees,
                walkTrace: out string trace));
            Assert.Contains("found-victim-in-parent-chain", trace);
            Assert.Contains("victim=rec-root", trace);
            // chainHops counts the active-seed chain-predecessor enqueue plus
            // any subsequent fan-out — must be at least 1 here.
            Assert.Contains("chainHops=", trace);
            Assert.DoesNotContain("chainHops=0 ", trace);
        }

        [Fact]
        public void BailsWithDistinctReason_WhenActiveIsTrueRootNoChainNoBp()
        {
            // Active is the standalone root (no chain, no BP). Walk has
            // nothing to walk; bail with the new active-has-no-parent reason
            // (renamed from active-has-no-parent-bp because chain links are
            // also considered now).
            var tree = new RecordingTree { Id = TreeId };
            tree.Recordings["rec-solo"] = new Recording
            {
                RecordingId = "rec-solo",
                TreeId = TreeId,
            };
            tree.Recordings["rec-victim"] = new Recording
            {
                RecordingId = "rec-victim",
                TreeId = TreeId,
            };

            Assert.False(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                victimRecordingId: "rec-victim",
                activeRecordingId: "rec-solo",
                searchTrees: new List<RecordingTree> { tree },
                walkTrace: out string trace));
            Assert.Contains("active-has-no-parent", trace);
            Assert.Contains("activeId=rec-solo", trace);
        }

        // -----------------------------------------------------------------
        // Cycles: a chain with a self-referential ChainIndex must not
        // infinite-loop. The visited-set bounds the walk regardless.
        // -----------------------------------------------------------------

        [Fact]
        public void HandlesChainCycleGracefully_VisitedSetCapsTheWalk()
        {
            // Pathological: two recordings claim ChainIndex 0 in the same
            // chain (legitimately rare — usually a corrupted save). The
            // chain-predecessor lookup for ChainIndex=1 returns the first
            // ChainIndex=0 it finds; enqueueing it doesn't loop because
            // ChainIndex=0 has no predecessor.
            var tree = new RecordingTree { Id = TreeId };
            tree.Recordings["rec-A"] = new Recording
            {
                RecordingId = "rec-A",
                TreeId = TreeId,
                ChainId = "chain-cycle",
                ChainIndex = 0,
            };
            tree.Recordings["rec-B"] = new Recording
            {
                RecordingId = "rec-B",
                TreeId = TreeId,
                ChainId = "chain-cycle",
                ChainIndex = 0, // duplicate index
            };
            tree.Recordings["rec-active"] = new Recording
            {
                RecordingId = "rec-active",
                TreeId = TreeId,
                ChainId = "chain-cycle",
                ChainIndex = 1,
            };

            // Walking from rec-active should visit either A or B (whichever
            // the dictionary yields first), but not infinite loop. Either is
            // a valid match for victim=rec-A (one is suppressed; the cycle
            // is benign because ChainIndex=0 terminates the walk).
            Assert.False(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                victimRecordingId: "rec-victim-not-in-tree",
                activeRecordingId: "rec-active",
                searchTrees: new List<RecordingTree> { tree },
                walkTrace: out string trace));
            Assert.Contains("exhausted-without-victim", trace);
        }

        // -----------------------------------------------------------------
        // Pure helper: TryFindChainPredecessor edge cases. Pin the contract
        // so a future refactor cannot accidentally let the chain walk leak
        // into unrelated trees / branches / standalone recordings.
        // -----------------------------------------------------------------

        [Fact]
        public void TryFindChainPredecessor_ReturnsNullForStandaloneRecording()
        {
            var tree = new RecordingTree { Id = TreeId };
            var rec = new Recording { RecordingId = "rec-solo" };
            tree.Recordings["rec-solo"] = rec;

            Assert.Null(GhostMapPresence.TryFindChainPredecessor(tree, rec));
        }

        [Fact]
        public void TryFindChainPredecessor_ReturnsNullForChainIndexZero()
        {
            var tree = new RecordingTree { Id = TreeId };
            var rec = new Recording
            {
                RecordingId = "rec-first",
                ChainId = "chain-X",
                ChainIndex = 0,
            };
            tree.Recordings["rec-first"] = rec;

            Assert.Null(GhostMapPresence.TryFindChainPredecessor(tree, rec));
        }

        [Fact]
        public void TryFindChainPredecessor_ScopedByChainBranch()
        {
            // Recording.ChainBranch separates parallel continuation paths.
            // A recording at ChainBranch=0 ChainIndex=1 must NOT match a
            // ChainBranch=1 ChainIndex=0 predecessor.
            var tree = new RecordingTree { Id = TreeId };
            var pred0 = new Recording
            {
                RecordingId = "pred-branch0",
                ChainId = "chain-X",
                ChainIndex = 0,
                ChainBranch = 0,
            };
            var pred1 = new Recording
            {
                RecordingId = "pred-branch1",
                ChainId = "chain-X",
                ChainIndex = 0,
                ChainBranch = 1,
            };
            var rec = new Recording
            {
                RecordingId = "rec",
                ChainId = "chain-X",
                ChainIndex = 1,
                ChainBranch = 0,
            };
            tree.Recordings["pred-branch0"] = pred0;
            tree.Recordings["pred-branch1"] = pred1;
            tree.Recordings["rec"] = rec;

            Assert.Equal("pred-branch0", GhostMapPresence.TryFindChainPredecessor(tree, rec));
        }

        [Fact]
        public void TryFindChainPredecessor_ReturnsNullForMissingPredecessor()
        {
            // Recording claims ChainIndex=2 but no ChainIndex=1 exists in the
            // tree. The lookup must return null (not stumble onto ChainIndex=0).
            var tree = new RecordingTree { Id = TreeId };
            tree.Recordings["rec-zero"] = new Recording
            {
                RecordingId = "rec-zero",
                ChainId = "chain-X",
                ChainIndex = 0,
            };
            var rec = new Recording
            {
                RecordingId = "rec-two",
                ChainId = "chain-X",
                ChainIndex = 2,
            };
            tree.Recordings["rec-two"] = rec;

            Assert.Null(GhostMapPresence.TryFindChainPredecessor(tree, rec));
        }

        // -----------------------------------------------------------------
        // End-to-end: the production gate
        // ShouldSuppressStateVectorProtoVesselForActiveReFly must now
        // suppress the root in the user's exact 3-recording chain shape.
        // -----------------------------------------------------------------

        [Fact]
        public void EndToEnd_SuppressesRoot_ProductionGate_UserShape()
        {
            // Compose the production-gate inputs for the user's exact
            // production scenario. The gate must return true (suppress) and
            // the suppressReason must carry relationship=parent and the
            // walk-trace's chainHops counter so playtest log readers can
            // verify the chain hop happened.
            const uint leafPid = 3151978247u; // Kerbal X Probe (active)
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_614_test",
                TreeId = TreeId,
                ActiveReFlyRecordingId = "rec-leaf-89eff843",
                OriginChildRecordingId = "rec-leaf-89eff843",
                InvokedUT = 159.5,
            };
            var trees = BuildChainSplitThenBreakupTree(
                rootId: "rec-root-c9df8d86",
                midId: "rec-mid-1d6d2116",
                leafId: "rec-leaf-89eff843",
                sharedChainId: "chain-Kerbal-X",
                breakupBpId: "bp-breakup-f1c7b08f");
            var committed = new List<Recording>
            {
                trees[0].Recordings["rec-root-c9df8d86"],
                trees[0].Recordings["rec-mid-1d6d2116"],
                trees[0].Recordings["rec-leaf-89eff843"],
            };

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: leafPid,
                victimRecordingId: "rec-root-c9df8d86",
                committedRecordings: committed,
                committedTrees: trees,
                out string reason);

            Assert.True(suppressed);
            Assert.StartsWith("refly-relative-anchor=active relationship=parent", reason);
            Assert.Contains("chainHops=", reason);
        }

        // -----------------------------------------------------------------
        // Log-assertion: the walkTrace's chainHops counter is the public
        // signal a future regression scraper / log audit would key on.
        // -----------------------------------------------------------------

        [Fact]
        public void WalkTrace_OnSuccess_CarriesChainHopsCounter_ForFutureRegressionVisibility()
        {
            // The chainHops counter MUST be present in both success and
            // failure traces so a playtest log scraper can grep
            // [GhostMap]...chainHops=N regardless of outcome.
            var trees = BuildChainSplitThenBreakupTree(
                rootId: "rec-root",
                midId: "rec-mid",
                leafId: "rec-leaf",
                sharedChainId: "chain-X",
                breakupBpId: "bp-1");

            // Success trace
            Assert.True(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                "rec-root", "rec-leaf", trees, out string successTrace));
            Assert.Contains("chainHops=", successTrace);

            // Failure trace (victim not in parent chain)
            var trees2 = BuildChainSplitThenBreakupTree(
                rootId: "rec-root2",
                midId: "rec-mid2",
                leafId: "rec-leaf2",
                sharedChainId: "chain-Y",
                breakupBpId: "bp-2");
            Assert.False(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                "rec-not-in-tree", "rec-leaf2", trees2, out string failTrace));
            Assert.Contains("chainHops=", failTrace);
            Assert.Contains("chainHopsViaAncestors=", failTrace);
        }
    }
}
