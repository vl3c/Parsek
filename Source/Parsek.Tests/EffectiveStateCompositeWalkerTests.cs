using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Guards <see cref="EffectiveState.EffectiveTipRecordingId"/> — the
    /// chain-then-supersede composite walker introduced by the
    /// fix-supersede-identity-scope plan §4. The walker is used by
    /// <see cref="ChildSlot.EffectiveRecordingId"/>, the
    /// <c>ResolveRewindPointSlotIndexForRecording</c> comparison hop, and the
    /// <c>IsInSupersedeForwardTrail</c> BFS extension; pure-supersede readers
    /// (<see cref="EffectiveState.IsVisible"/>, etc.) stay on the id-local
    /// walker. These tests cover supersede-only, chain-only, both, and the two
    /// cycle-detection cases — including the cross-edge cycle that exercises
    /// the single shared visited set spanning both hop kinds.
    /// </summary>
    [Collection("Sequential")]
    public class EffectiveStateCompositeWalkerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public EffectiveStateCompositeWalkerTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // --- Helpers ----------------------------------------------------------

        private static RecordingSupersedeRelation Rel(string oldId, string newId)
        {
            return new RecordingSupersedeRelation
            {
                RelationId = "rsr_" + oldId + "_" + newId,
                OldRecordingId = oldId,
                NewRecordingId = newId,
                UT = 0.0,
            };
        }

        private static Recording Rec(
            string id,
            string treeId = null,
            string chainId = null,
            int chainIndex = -1,
            int chainBranch = 0)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                MergeState = MergeState.Immutable,
                TreeId = treeId,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = chainBranch,
            };
        }

        /// <summary>
        /// Registers a chain of recordings under a single tree so the
        /// <see cref="EffectiveState.ResolveChainTerminalRecording"/> tree scan
        /// can find the tip via the tree's <c>Recordings</c> dictionary.
        /// Each segment is added both to the tree and to
        /// <see cref="RecordingStore.CommittedRecordings"/> (so
        /// <c>FindRecordingById</c> in the composite walker finds the current
        /// recording id and reads its <c>ChainId</c>).
        /// </summary>
        private static void RegisterTreeWithChain(string treeId, string chainId, params Recording[] segments)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeId,
            };
            if (segments != null && segments.Length > 0)
                tree.RootRecordingId = segments[0].RecordingId;

            for (int i = 0; i < segments.Length; i++)
            {
                var seg = segments[i];
                seg.TreeId = treeId;
                if (!string.IsNullOrEmpty(chainId))
                    seg.ChainId = chainId;
                tree.AddOrReplaceRecording(seg);
                RecordingStore.AddCommittedInternal(seg);
            }

            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        /// <summary>
        /// Registers a single recording (no chain) under its own tree so
        /// <c>FindRecordingById</c> resolves it for the composite walker's
        /// chain-hop probe (which checks <c>ChainId</c> on the lookup result).
        /// </summary>
        private static void RegisterStandalone(Recording rec)
        {
            var tree = new RecordingTree
            {
                Id = "tree_" + rec.RecordingId,
                TreeName = rec.RecordingId,
                RootRecordingId = rec.RecordingId,
            };
            rec.TreeId = tree.Id;
            tree.AddOrReplaceRecording(rec);
            RecordingStore.AddCommittedInternal(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        // =====================================================================
        // 1. Supersede only
        // =====================================================================

        [Fact]
        public void EffectiveTipRecordingId_SupersedeOnly_WalksSupersede()
        {
            // Three recordings with NO ChainId: A, B, C. Supersede edges
            // A -> B -> C. Composite must walk both supersede edges and return C.
            var a = Rec("rec_A");
            var b = Rec("rec_B");
            var c = Rec("rec_C");
            RegisterStandalone(a);
            RegisterStandalone(b);
            RegisterStandalone(c);

            var sups = new List<RecordingSupersedeRelation>
            {
                Rel("rec_A", "rec_B"),
                Rel("rec_B", "rec_C"),
            };

            string tip = EffectiveState.EffectiveTipRecordingId("rec_A", sups);
            Assert.Equal("rec_C", tip);
        }

        // =====================================================================
        // 2. Chain only
        // =====================================================================

        [Fact]
        public void EffectiveTipRecordingId_ChainOnly_WalksChainTip()
        {
            // Three recordings sharing ChainId, ascending ChainIndex, same
            // ChainBranch; no supersede rows. Composite should follow the chain
            // hop on the first iteration (current = A -> tip = C) and then
            // terminate when no supersede edge exists for C.
            var a = Rec("rec_A", chainIndex: 0);
            var b = Rec("rec_B", chainIndex: 1);
            var c = Rec("rec_C", chainIndex: 2);
            RegisterTreeWithChain("tree_chain", "chain_X", a, b, c);

            string tip = EffectiveState.EffectiveTipRecordingId(
                "rec_A", new List<RecordingSupersedeRelation>());
            Assert.Equal("rec_C", tip);
        }

        // =====================================================================
        // 3. Chain then supersede
        // =====================================================================

        [Fact]
        public void EffectiveTipRecordingId_ChainThenSupersede_WalksBoth()
        {
            // A and B chained (B is chain tip). Supersede B -> C (fork).
            // The canonical post-rewind-UT-split-then-fork shape. Composite
            // must hop chain A -> B, then supersede B -> C.
            var a = Rec("rec_A", chainIndex: 0);
            var b = Rec("rec_B", chainIndex: 1);
            var fork = Rec("rec_C");
            RegisterTreeWithChain("tree_split", "chain_split", a, b);
            RegisterStandalone(fork);

            var sups = new List<RecordingSupersedeRelation> { Rel("rec_B", "rec_C") };

            string tip = EffectiveState.EffectiveTipRecordingId("rec_A", sups);
            Assert.Equal("rec_C", tip);
        }

        // =====================================================================
        // 4. Supersede into chain
        // =====================================================================

        [Fact]
        public void EffectiveTipRecordingId_SupersedeIntoChain_WalksBoth()
        {
            // Supersede A -> B; B is the head of a chain with tip C. Composite
            // hops supersede A -> B, then chain B -> C.
            var a = Rec("rec_A");
            RegisterStandalone(a);

            var b = Rec("rec_B", chainIndex: 0);
            var c = Rec("rec_C", chainIndex: 1);
            RegisterTreeWithChain("tree_post", "chain_post", b, c);

            var sups = new List<RecordingSupersedeRelation> { Rel("rec_A", "rec_B") };

            string tip = EffectiveState.EffectiveTipRecordingId("rec_A", sups);
            Assert.Equal("rec_C", tip);
        }

        // =====================================================================
        // 5. Supersede cycle
        // =====================================================================

        [Fact]
        public void EffectiveTipRecordingId_CycleDetected_ReturnsLastVisited()
        {
            // Pathological supersede cycle A -> B -> A. Following the existing
            // EffectiveRecordingId policy, the walk returns the last-visited
            // id reached before the cycle closes (B) and logs a Warn.
            var a = Rec("rec_A");
            var b = Rec("rec_B");
            RegisterStandalone(a);
            RegisterStandalone(b);

            var sups = new List<RecordingSupersedeRelation>
            {
                Rel("rec_A", "rec_B"),
                Rel("rec_B", "rec_A"),
            };

            string tip = EffectiveState.EffectiveTipRecordingId("rec_A", sups);
            Assert.Equal("rec_B", tip);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("cycle detected")
                && l.Contains("supersede-hop"));
        }

        // =====================================================================
        // 6. Cross-edge cycle (chain hop + supersede hop)
        // =====================================================================

        [Fact]
        public void EffectiveTipRecordingId_CrossEdgeCycle_ReturnsLastVisited()
        {
            // A and B share ChainId + ChainBranch (B is chain tip of A). A
            // supersede row B -> A also exists. Walking from A:
            //   chain hop A -> B (visited = {A, B})
            //   supersede hop B -> A is rejected by the visited guard
            // Returns B and logs a Warn. Verifies the single shared visited
            // set spans both hop kinds — the plan §"Edge cases" #4 trace.
            var a = Rec("rec_A", chainIndex: 0);
            var b = Rec("rec_B", chainIndex: 1);
            RegisterTreeWithChain("tree_xedge", "chain_xedge", a, b);

            var sups = new List<RecordingSupersedeRelation> { Rel("rec_B", "rec_A") };

            string tip = EffectiveState.EffectiveTipRecordingId("rec_A", sups);
            Assert.Equal("rec_B", tip);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("cycle detected")
                && l.Contains("supersede-hop"));
        }

        // =====================================================================
        // 7. Nested Re-Fly transitive chase (Pass 4 review L3)
        //
        // Two consecutive Re-Flys on the same slot produce:
        //   chain X: HEAD1 -> TIP1 (created by first Re-Fly's splitter)
        //   supersede: TIP1 -> fork1 (first Re-Fly's commit)
        //   chain Y: fork1 (= HEAD2) -> TIP2 (second Re-Fly's splitter)
        //   supersede: TIP2 -> fork2 (second Re-Fly's commit)
        //
        // Walking from HEAD1 must trace all four hops and return fork2.
        // The pre-Pass-4 carve-out regression manifested in AppendRelations
        // (separate code path), but if the composite walker had had this
        // coverage it would have shown HEAD2 == fork1 — the same id collision
        // that broke the id-match carve-out form. Lock the contract now.
        // =====================================================================

        [Fact]
        public void EffectiveTipRecordingId_NestedReFly_WalksHeadOneThroughForkTwo()
        {
            // First Re-Fly's wreckage: chain X with HEAD1 + TIP1, supersede
            // row TIP1 -> fork1.
            var head1 = Rec("rec_HEAD1", chainIndex: 0);
            var tip1 = Rec("rec_TIP1", chainIndex: 1);
            RegisterTreeWithChain("tree_first", "chain_X", head1, tip1);

            // Second Re-Fly's wreckage: chain Y with fork1 (= HEAD2) + TIP2,
            // supersede row TIP2 -> fork2. fork1 must be on chain Y here —
            // after the second splitter ran it shares chain Y with TIP2.
            // Re-use the same RecordingId fork1.id by creating the recording
            // anew and registering it under tree_second on chain_Y.
            var head2 = Rec("rec_fork1", chainIndex: 0); // RecordingId == fork1's id
            var tip2 = Rec("rec_TIP2", chainIndex: 1);
            RegisterTreeWithChain("tree_second", "chain_Y", head2, tip2);

            // fork2 is standalone (the second Re-Fly's provisional, not yet
            // superseded by anything).
            var fork2 = Rec("rec_fork2");
            RegisterStandalone(fork2);

            var sups = new List<RecordingSupersedeRelation>
            {
                Rel("rec_TIP1", "rec_fork1"),  // first Re-Fly commit
                Rel("rec_TIP2", "rec_fork2"),  // second Re-Fly commit
            };

            string tip = EffectiveState.EffectiveTipRecordingId("rec_HEAD1", sups);
            Assert.Equal("rec_fork2", tip);
        }

        // =====================================================================
        // 8. Mid-chain supersede assumption pinning (Pass 6 review M1)
        //
        // The walker hops chain-tip BEFORE checking the supersede table. For a
        // splitter-produced supersede (always anchored at the chain TIP) this
        // is correct. For a hypothetical mid-chain supersede (e.g. A1 → B on
        // a chain {A0, A1, A2}), the walker silently routes past B by going
        // A0 → chain-tip(A2) → no supersede edge → return A2.
        //
        // Today no production code creates a mid-chain supersede — splits
        // always supersede the chain TIP. This test PINS the current
        // (correct-by-assumption) behavior so a future change that introduces
        // mid-chain supersedes can find this test, see that A1 → B is
        // expected to be skipped, and rework the walker to enumerate
        // supersede edges on every chain member (not just the tip).
        // =====================================================================

        [Fact]
        public void EffectiveTipRecordingId_MidChainSupersede_AssumptionHolds()
        {
            // Chain {A0, A1, A2}; supersede anchored at A1 (mid-chain, NOT tip).
            var a0 = Rec("rec_A0", chainIndex: 0);
            var a1 = Rec("rec_A1", chainIndex: 1);
            var a2 = Rec("rec_A2", chainIndex: 2);
            RegisterTreeWithChain("tree_midchain", "chain_midchain", a0, a1, a2);

            var b = Rec("rec_B");
            RegisterStandalone(b);

            var sups = new List<RecordingSupersedeRelation>
            {
                Rel("rec_A1", "rec_B"),  // mid-chain supersede
            };

            // Walker hops chain-tip first: A0 → A2 (chain-tip). A2 has no
            // supersede edge in the table. Walker exits with A2 — silently
            // routing past the mid-chain B target. This is the assumption
            // documented in EffectiveTipRecordingId's "Pass 6 review M1"
            // comment block.
            string tip = EffectiveState.EffectiveTipRecordingId("rec_A0", sups);
            Assert.Equal("rec_A2", tip);

            // For comparison, a supersede anchored at the chain TIP routes
            // correctly. This pins the "splitter-produced supersedes always
            // target the tip" invariant — if the walker is ever reworked to
            // handle mid-chain supersedes, this second assertion must still
            // pass.
            var sups2 = new List<RecordingSupersedeRelation>
            {
                Rel("rec_A2", "rec_B"),  // tip-anchored supersede
            };
            string tip2 = EffectiveState.EffectiveTipRecordingId("rec_A0", sups2);
            Assert.Equal("rec_B", tip2);
        }
    }
}
