using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Guards against EffectiveRecordingId walk regressions (design doc
    /// section 5.2). Five cases cover chain lengths 0-3, cycle detection
    /// (A-&gt;B-&gt;A triggers Warn + last-visited return), and the orphan
    /// endpoint case (A-&gt;B where B never appears as OldRecordingId
    /// again, so the walk returns B).
    /// </summary>
    [Collection("Sequential")]
    public class ChildSlotEffectiveRecordingIdTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public ChildSlotEffectiveRecordingIdTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            // Reset the recording store: the composite walker that ChildSlot
            // now delegates to (EffectiveTipRecordingId) reads
            // RecordingStore.CommittedRecordings to look up the current
            // recording's ChainId. Tests that don't register recordings
            // observe an empty store and a no-op chain hop, matching the
            // pre-composite behavior.
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
        }

        private static RecordingSupersedeRelation Rel(string oldId, string newId)
        {
            return new RecordingSupersedeRelation
            {
                RelationId = "rsr_" + oldId + "_" + newId,
                OldRecordingId = oldId,
                NewRecordingId = newId,
                UT = 0.0
            };
        }

        [Fact]
        public void EffectiveRecordingId_ChainLengthZero_NoSupersedes_ReturnsOrigin()
        {
            var slot = new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_A" };
            string eff = slot.EffectiveRecordingId(new List<RecordingSupersedeRelation>());
            Assert.Equal("rec_A", eff);
        }

        [Fact]
        public void EffectiveRecordingId_ChainLengthOne_AtoB_ReturnsB()
        {
            var slot = new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_A" };
            var list = new List<RecordingSupersedeRelation> { Rel("rec_A", "rec_B") };
            string eff = slot.EffectiveRecordingId(list);
            Assert.Equal("rec_B", eff);
        }

        [Fact]
        public void EffectiveRecordingId_ChainLengthTwo_AtoBtoC_ReturnsC()
        {
            var slot = new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_A" };
            var list = new List<RecordingSupersedeRelation>
            {
                Rel("rec_A", "rec_B"),
                Rel("rec_B", "rec_C")
            };
            string eff = slot.EffectiveRecordingId(list);
            Assert.Equal("rec_C", eff);
        }

        [Fact]
        public void EffectiveRecordingId_ChainLengthThree_AtoBtoCtoD_ReturnsD()
        {
            var slot = new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_A" };
            var list = new List<RecordingSupersedeRelation>
            {
                Rel("rec_A", "rec_B"),
                Rel("rec_B", "rec_C"),
                Rel("rec_C", "rec_D")
            };
            string eff = slot.EffectiveRecordingId(list);
            Assert.Equal("rec_D", eff);
        }

        [Fact]
        public void EffectiveRecordingId_Cycle_AtoBtoA_WarnsAndReturnsLastVisited()
        {
            var slot = new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_A" };
            var list = new List<RecordingSupersedeRelation>
            {
                Rel("rec_A", "rec_B"),
                Rel("rec_B", "rec_A")
            };
            string eff = slot.EffectiveRecordingId(list);

            // When at rec_B the walk finds a relation pointing back to rec_A which is
            // already visited — the method logs Warn and returns the last-visited id,
            // i.e. the id reached before the cycle closed (rec_B).
            Assert.Equal("rec_B", eff);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("cycle detected") &&
                l.Contains("rec_A"));
        }

        [Fact]
        public void EffectiveRecordingId_OrphanEndpoint_AtoB_BNotInOldList_ReturnsB()
        {
            // rec_B is the new id for rec_A, and no supersede has rec_B as Old.
            // Per design section 5.2, B IS the effective id — the walk returns B.
            var slot = new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_A" };
            var list = new List<RecordingSupersedeRelation> { Rel("rec_A", "rec_B") };
            string eff = slot.EffectiveRecordingId(list);
            Assert.Equal("rec_B", eff);
        }

        [Fact]
        public void EffectiveRecordingId_NullOrigin_ReturnsNull()
        {
            var slot = new ChildSlot { SlotIndex = 0, OriginChildRecordingId = null };
            string eff = slot.EffectiveRecordingId(new List<RecordingSupersedeRelation> { Rel("rec_A", "rec_B") });
            Assert.Null(eff);
        }

        [Fact]
        public void EffectiveRecordingId_SlotPointsAtChainHead_ReturnsTipAfterChainAndSupersede()
        {
            // Post-rewind-UT-split shape: the slot's OriginChildRecordingId
            // names the chain HEAD (the pre-rewind half). HEAD <-> TIP are
            // chained (TIP carries the larger ChainIndex). A supersede row
            // TIP -> fork attaches the Re-Fly fork to the chain tip. The
            // slot's effective recording id must follow chain HEAD -> TIP,
            // then supersede TIP -> fork.
            var tree = new RecordingTree
            {
                Id = "tree_split",
                TreeName = "Split",
                RootRecordingId = "rec_head",
            };
            var head = new Recording
            {
                RecordingId = "rec_head",
                VesselName = "Kerbal X (HEAD)",
                MergeState = MergeState.Immutable,
                TreeId = "tree_split",
                ChainId = "chain_split",
                ChainIndex = 0,
            };
            var tip = new Recording
            {
                RecordingId = "rec_tip",
                VesselName = "Kerbal X (TIP)",
                MergeState = MergeState.Immutable,
                TreeId = "tree_split",
                ChainId = "chain_split",
                ChainIndex = 1,
            };
            tree.AddOrReplaceRecording(head);
            tree.AddOrReplaceRecording(tip);
            RecordingStore.AddCommittedInternal(head);
            RecordingStore.AddCommittedInternal(tip);
            RecordingStore.AddCommittedTreeForTesting(tree);

            var fork = new Recording
            {
                RecordingId = "rec_fork",
                VesselName = "Kerbal X (fork)",
                MergeState = MergeState.Immutable,
            };
            var forkTree = new RecordingTree
            {
                Id = "tree_fork",
                TreeName = "Fork",
                RootRecordingId = "rec_fork",
            };
            fork.TreeId = "tree_fork";
            forkTree.AddOrReplaceRecording(fork);
            RecordingStore.AddCommittedInternal(fork);
            RecordingStore.AddCommittedTreeForTesting(forkTree);

            var slot = new ChildSlot
            {
                SlotIndex = 0,
                OriginChildRecordingId = "rec_head",
            };
            var sups = new List<RecordingSupersedeRelation>
            {
                Rel("rec_tip", "rec_fork"),
            };

            string eff = slot.EffectiveRecordingId(sups);
            Assert.Equal("rec_fork", eff);
        }
    }
}
