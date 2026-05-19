using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure-logic tests for
    /// <see cref="GhostPlaybackLogic.ResolveChainNextSlotIndex"/> — the
    /// chain-next lookup that the engine consults to coordinate the
    /// chain-seam handoff with <see cref="ChainHandoffLogic"/>. The live
    /// adapter <c>ParsekPlaybackPolicy.ResolveChainNextSlotIndex</c> is a
    /// thin wrapper that reads the live committed list and supersede graph
    /// and delegates here; its behaviour is fully covered by these
    /// fixtures.
    ///
    /// <para>Marked <c>Sequential</c> because the
    /// <c>DuplicateChainSuccessor_PicksFirstAndWarnsOnce</c> test captures
    /// <see cref="ParsekLog"/> output via the shared static
    /// <c>TestSinkForTesting</c> hook.</para>
    /// </summary>
    [Collection("Sequential")]
    public class ResolveChainNextSlotIndexTests
    {
        private static Recording MakeRec(
            string id,
            uint vesselPid = 100u,
            string chainId = null,
            int chainIndex = -1,
            int chainBranch = 0)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Ship",
                VesselPersistentId = vesselPid,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = chainBranch,
            };
        }

        // -------------------- Returns -1 (no continuation) --------------------

        [Fact]
        public void NullCommittedList_ReturnsMinusOne()
        {
            Assert.Equal(-1, GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex: 0,
                committed: null,
                supersedes: null));
        }

        [Fact]
        public void NegativeSlotIndex_ReturnsMinusOne()
        {
            var recs = new List<Recording>
            {
                MakeRec("r0", chainId: "c1", chainIndex: 0),
                MakeRec("r1", chainId: "c1", chainIndex: 1),
            };
            Assert.Equal(-1, GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex: -1, committed: recs, supersedes: null));
        }

        [Fact]
        public void OutOfBoundsSlotIndex_ReturnsMinusOne()
        {
            var recs = new List<Recording>
            {
                MakeRec("r0", chainId: "c1", chainIndex: 0),
            };
            Assert.Equal(-1, GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex: 99, committed: recs, supersedes: null));
        }

        [Fact]
        public void NullRecordingAtSlot_ReturnsMinusOne()
        {
            var recs = new List<Recording>
            {
                null,
                MakeRec("r1", chainId: "c1", chainIndex: 1),
            };
            Assert.Equal(-1, GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex: 0, committed: recs, supersedes: null));
        }

        [Fact]
        public void RecordingWithoutChainId_ReturnsMinusOne()
        {
            var recs = new List<Recording>
            {
                MakeRec("r0"), // no chain id
                MakeRec("r1", chainId: "c1", chainIndex: 0),
            };
            Assert.Equal(-1, GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex: 0, committed: recs, supersedes: null));
        }

        [Fact]
        public void RecordingWithEmptyChainId_ReturnsMinusOne()
        {
            var recs = new List<Recording>
            {
                MakeRec("r0", chainId: "", chainIndex: 0),
            };
            Assert.Equal(-1, GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex: 0, committed: recs, supersedes: null));
        }

        [Fact]
        public void RecordingWithNegativeChainIndex_ReturnsMinusOne()
        {
            var recs = new List<Recording>
            {
                MakeRec("r0", chainId: "c1", chainIndex: -1),
                MakeRec("r1", chainId: "c1", chainIndex: 0),
            };
            // ChainIndex < 0 is a "not-on-chain" sentinel; skip.
            Assert.Equal(-1, GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex: 0, committed: recs, supersedes: null));
        }

        [Fact]
        public void RecordingOnParallelBranch_ReturnsMinusOne()
        {
            var recs = new List<Recording>
            {
                MakeRec("r0", chainId: "c1", chainIndex: 0, chainBranch: 1),
                MakeRec("r1", chainId: "c1", chainIndex: 1),
            };
            // Branch > 0 segments are parallel continuations (ghost-only);
            // they despawn normally and don't participate in the chain-handoff
            // dance.
            Assert.Equal(-1, GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex: 0, committed: recs, supersedes: null));
        }

        [Fact]
        public void NoSuccessorAtNextIndex_ReturnsMinusOne()
        {
            var recs = new List<Recording>
            {
                MakeRec("r0", chainId: "c1", chainIndex: 0),
                // No chainIndex=1 in this list — the chain head has no
                // continuation, so the lookup returns -1.
            };
            Assert.Equal(-1, GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex: 0, committed: recs, supersedes: null));
        }

        [Fact]
        public void SuccessorBelongsToDifferentChain_ReturnsMinusOne()
        {
            var recs = new List<Recording>
            {
                MakeRec("r0", chainId: "c1", chainIndex: 0),
                MakeRec("r1", chainId: "c2", chainIndex: 1), // different chain
            };
            Assert.Equal(-1, GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex: 0, committed: recs, supersedes: null));
        }

        [Fact]
        public void SuccessorOnParallelBranch_ReturnsMinusOne()
        {
            var recs = new List<Recording>
            {
                MakeRec("r0", chainId: "c1", chainIndex: 0),
                MakeRec("r1", chainId: "c1", chainIndex: 1, chainBranch: 1), // parallel branch
            };
            Assert.Equal(-1, GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex: 0, committed: recs, supersedes: null));
        }

        // -------------------- Returns a valid successor index --------------------

        [Fact]
        public void DirectChainSuccessor_ReturnsItsIndex()
        {
            var recs = new List<Recording>
            {
                MakeRec("r0", chainId: "c1", chainIndex: 0),
                MakeRec("r1", chainId: "c1", chainIndex: 1),
            };
            Assert.Equal(1, GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex: 0, committed: recs, supersedes: null));
        }

        [Fact]
        public void DirectChainSuccessor_SkipsBranchRecordings()
        {
            var recs = new List<Recording>
            {
                MakeRec("r0", chainId: "c1", chainIndex: 0),
                MakeRec("r1", chainId: "c1", chainIndex: 1, chainBranch: 1), // branch
                MakeRec("r2", chainId: "c1", chainIndex: 1, chainBranch: 0), // main
            };
            Assert.Equal(2, GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex: 0, committed: recs, supersedes: null));
        }

        [Fact]
        public void DirectChainSuccessor_PicksTheImmediateNextIndex()
        {
            var recs = new List<Recording>
            {
                MakeRec("r0", chainId: "c1", chainIndex: 0),
                MakeRec("r1", chainId: "c1", chainIndex: 1),
                MakeRec("r2", chainId: "c1", chainIndex: 2),
            };
            // From the head, the resolver returns the immediate next index
            // (chainIndex=1), not the chain tip (chainIndex=2). The
            // continuation slot then runs its own resolver to find its own
            // chain-next, transparently extending the handoff across
            // multi-segment chains.
            Assert.Equal(1, GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex: 0, committed: recs, supersedes: null));
        }

        // -------------------- Supersede walking --------------------

        [Fact]
        public void DirectChainSuccessor_WalksSupersedeEdgeToFork()
        {
            // Pre-rewind chain: r0 (head) -> r1 (chain successor).
            // Re-fly forks the post-rewind continuation as r2 and writes a
            // supersede edge r1 -> r2. The chain-handoff lookup must follow
            // that edge so the engine shadows r0 against the LIVE fork ghost,
            // not the inert superseded chain slot.
            var r0 = MakeRec("r0", chainId: "c1", chainIndex: 0);
            var r1 = MakeRec("r1", chainId: "c1", chainIndex: 1);
            var r2 = MakeRec("r2", chainId: "c1", chainIndex: 1); // fork sharing chain coords
            var recs = new List<Recording> { r0, r1, r2 };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation
                {
                    OldRecordingId = "r1",
                    NewRecordingId = "r2",
                },
            };
            // Note: this fixture uses two recordings sharing chainIndex=1; the
            // resolver lands on the first match (r1) and walks the supersede
            // edge to r2. Production data does not normally have duplicate
            // ChainIndex on the same branch, but the edge walk is the
            // load-bearing case for re-fly forks regardless of how the fork
            // representation is stored.
            Assert.Equal(2, GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex: 0, committed: recs, supersedes: supersedes));
        }

        [Fact]
        public void SupersedeLandsBackOnSameSlot_ReturnsMinusOne()
        {
            // Defensive: if a supersede edge somehow resolves back to the
            // querying slot (cycle), the engine must not be told to shadow
            // itself or it would freeze its own ghost. Returning -1 lets the
            // normal lifecycle run.
            var r0 = MakeRec("r0", chainId: "c1", chainIndex: 0);
            var r1 = MakeRec("r1", chainId: "c1", chainIndex: 1);
            var recs = new List<Recording> { r0, r1 };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation
                {
                    OldRecordingId = "r1",
                    NewRecordingId = "r0",
                },
            };
            Assert.Equal(-1, GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex: 0, committed: recs, supersedes: supersedes));
        }

        // -------------------- Cascade (multi-segment chain) --------------------

        [Fact]
        public void ThreeSegmentChain_EachSlotResolvesToItsImmediateSuccessor()
        {
            // Three-segment chain: head r0 -> mid r1 -> tip r2. Pins the
            // "immediate next" contract: at each slot the resolver returns
            // the chainIndex+1 successor (cascade shadow in the engine
            // operates pairwise, not by tip-skip). Tip itself has no
            // successor and returns -1.
            var recs = new List<Recording>
            {
                MakeRec("r0", chainId: "c1", chainIndex: 0),
                MakeRec("r1", chainId: "c1", chainIndex: 1),
                MakeRec("r2", chainId: "c1", chainIndex: 2),
            };
            Assert.Equal(1, GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex: 0, committed: recs, supersedes: null));
            Assert.Equal(2, GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex: 1, committed: recs, supersedes: null));
            Assert.Equal(-1, GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex: 2, committed: recs, supersedes: null));
        }

        // -------------------- Duplicate-match anomaly --------------------

        [Fact]
        public void DuplicateChainSuccessor_PicksFirstAndWarnsOnce()
        {
            // Data anomaly: two branch-0 recordings share the same
            // (ChainId, ChainIndex). The resolver picks the first match
            // deterministically (so engine behaviour is reproducible) and
            // emits a Warn so the recorder bug or merge conflict that
            // produced the duplicate leaves a breadcrumb in KSP.log.
            // Rate-limited so the steady-state cost is bounded.
            var logLines = new System.Collections.Generic.List<string>();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.SuppressLogging = false;
            try
            {
                var recs = new List<Recording>
                {
                    MakeRec("r0", chainId: "c1", chainIndex: 0),
                    MakeRec("r1a", chainId: "c1", chainIndex: 1), // first match
                    MakeRec("r1b", chainId: "c1", chainIndex: 1), // duplicate
                };
                int resolved = GhostPlaybackLogic.ResolveChainNextSlotIndex(
                    slotIndex: 0, committed: recs, supersedes: null);
                Assert.Equal(1, resolved); // first match (r1a at index 1)
                Assert.Contains(logLines, l =>
                    l.Contains("[Parsek][WARN][ChainHandoff]")
                    && l.Contains("duplicate branch-0 successor")
                    && l.Contains("chainId=c1")
                    && l.Contains("chainIndex=1")
                    && l.Contains("firstMatch=1")
                    && l.Contains("duplicateMatch=2"));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
            }
        }
    }
}
