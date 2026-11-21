using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// LOOP-SYNC-PARENT-INDEX-NOT-SHIFTED (review L4) and LOOP-SYNC-PARENT-IGNORES-ANCHOR (L5):
    /// a debris recording's loop-sync parent link is a committed-list position, so a mid-list
    /// removal or insertion shifts it; and the link prefers the debris's own parent
    /// (<see cref="Recording.ParentAnchorRecordingId"/>) over any other recording that covers
    /// the debris's start.
    /// </summary>
    [Collection("Sequential")]
    public class OptimizerLoopSyncParentTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public OptimizerLoopSyncParentTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        private static Recording Rec(string id, uint pid, double start, double end, bool debris = false)
        {
            var rec = new Recording
            {
                RecordingId = id,
                TreeId = "tree-loop",
                VesselPersistentId = pid,
                IsDebris = debris,
            };
            rec.Points.Add(new TrajectoryPoint { ut = start, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = end, bodyName = "Kerbin" });
            return rec;
        }

        [Fact]
        public void MidListRemoval_ShiftsTheLinkDown_AndDropsALinkToTheRemovedRecording()
        {
            var other = Rec("other", 1, 0, 50);
            var parent = Rec("parent", 2, 0, 100);
            var debris = Rec("debris", 3, 10, 60, debris: true);
            var debris2 = Rec("debris2", 4, 10, 60, debris: true);
            foreach (var r in new[] { other, parent, debris, debris2 })
                RecordingStore.AddRecordingWithTreeForTesting(r);
            debris.LoopSyncParentIdx = 1;   // parent
            debris2.LoopSyncParentIdx = 0;  // other

            RecordingStore.RemoveCommittedAt(0, null);

            Assert.Same(parent, RecordingStore.CommittedRecordings[debris.LoopSyncParentIdx]);
            Assert.Equal(-1, debris2.LoopSyncParentIdx);
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("Loop sync parent links after removal at index 0: shifted=1 dropped=1"));
        }

        [Fact]
        public void MidListInsertion_ShiftsTheLinkUp()
        {
            var parent = Rec("parent", 2, 0, 100);
            var debris = Rec("debris", 3, 10, 60, debris: true);
            RecordingStore.AddRecordingWithTreeForTesting(parent);
            RecordingStore.AddRecordingWithTreeForTesting(debris);
            debris.LoopSyncParentIdx = 0;

            RecordingStore.InsertCommittedAt(0, Rec("inserted", 9, 0, 10));

            Assert.Same(parent, RecordingStore.CommittedRecordings[debris.LoopSyncParentIdx]);
        }

        [Fact]
        public void Populate_PrefersTheDebrisOwnParentOverAnEarlierCoveringRecording()
        {
            // A sibling stage that also covers the debris's birth sits first in the list.
            var sibling = Rec("sibling", 1, 0, 100);
            var parent = Rec("parent", 2, 0, 100);
            var debris = Rec("debris", 3, 20, 60, debris: true);
            debris.ParentAnchorRecordingId = "parent";
            var list = new List<Recording> { sibling, parent, debris };

            CommittedListOptimizer.PopulateLoopSyncParentIndices(list);

            Assert.Equal(1, debris.LoopSyncParentIdx);
            Assert.Contains(logLines, l => l.Contains("Loop sync: linked 1 debris recording(s)")
                && l.Contains("byParentAnchor=1"));
        }

        [Fact]
        public void Populate_AnchorSplitByTheOptimizer_LinksThePieceThatCoversTheBirth()
        {
            // The parent was split: the anchor names the head, the debris was born on the tail.
            var sibling = Rec("sibling", 1, 0, 200);
            var head = Rec("parent-head", 2, 0, 100);
            var tail = Rec("parent-tail", 2, 100, 200);
            head.ChainId = tail.ChainId = "chain-parent";
            head.ChainIndex = 0;
            tail.ChainIndex = 1;
            var debris = Rec("debris", 3, 150, 180, debris: true);
            debris.ParentAnchorRecordingId = "parent-head";
            var list = new List<Recording> { sibling, head, tail, debris };

            CommittedListOptimizer.PopulateLoopSyncParentIndices(list);

            Assert.Equal(2, debris.LoopSyncParentIdx);
        }

        [Fact]
        public void Populate_NoAnchor_KeepsTheCoveringRule()
        {
            var sibling = Rec("sibling", 1, 0, 100);
            var debris = Rec("debris", 3, 20, 60, debris: true);
            var list = new List<Recording> { sibling, debris };

            CommittedListOptimizer.PopulateLoopSyncParentIndices(list);

            Assert.Equal(0, debris.LoopSyncParentIdx);
        }
    }
}
