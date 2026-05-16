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
    }
}
