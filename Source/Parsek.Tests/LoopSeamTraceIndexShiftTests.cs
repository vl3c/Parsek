using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    // The re-aim and loop-seam trace dictionaries are keyed by committed-list index and must
    // shift with the list (LOOP-SEAM-TRACE-INDEX-NOT-SHIFTED).
    [Collection("Sequential")]
    public class LoopSeamTraceIndexShiftTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly ParsekFlightGhostPositioner positioner = new ParsekFlightGhostPositioner(null);

        public LoopSeamTraceIndexShiftTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private void Seed(params int[] indices)
        {
            foreach (int i in indices)
            {
                positioner.ReaimLastGhostFrameForTesting[i] = (i, new Vector3(i, 0f, 0f), i * 10.0);
                positioner.LoopSeamLastGhostFrameForTesting[i] = (i, new Vector3(i, 0f, 0f), i * 10.0, i * 100.0, i);
            }
        }

        [Fact]
        public void DeleteMidList_DropsRemovedIndex_AndShiftsHigherEntriesDown()
        {
            Seed(0, 2, 3, 5);

            positioner.ShiftLoopSeamTracesAfterDelete(2);

            var reaim = positioner.ReaimLastGhostFrameForTesting;
            var seam = positioner.LoopSeamLastGhostFrameForTesting;
            Assert.Equal(new[] { 0, 2, 4 }, Sorted(reaim.Keys));
            Assert.Equal(new[] { 0, 2, 4 }, Sorted(seam.Keys));
            // The entry recorded for old index 3 now sits at 2; old 2 is gone.
            Assert.Equal(3, reaim[2].segIdx);
            Assert.Equal(5, reaim[4].segIdx);
            Assert.Equal(0, reaim[0].segIdx);
            Assert.Equal(3, seam[2].frame);
            Assert.Equal(5, seam[4].frame);
            Assert.Equal(0, seam[0].frame);
        }

        [Fact]
        public void InsertMidList_ShiftsEntriesAtAndAboveIndexUp_AndLeavesSlotEmpty()
        {
            Seed(0, 2, 3);

            positioner.ShiftLoopSeamTracesAfterInsert(2);

            var reaim = positioner.ReaimLastGhostFrameForTesting;
            var seam = positioner.LoopSeamLastGhostFrameForTesting;
            Assert.Equal(new[] { 0, 3, 4 }, Sorted(reaim.Keys));
            Assert.Equal(new[] { 0, 3, 4 }, Sorted(seam.Keys));
            Assert.Equal(2, reaim[3].segIdx);
            Assert.Equal(3, reaim[4].segIdx);
            Assert.Equal(2, seam[3].frame);
            Assert.Equal(3, seam[4].frame);
            Assert.False(reaim.ContainsKey(2));
            Assert.False(seam.ContainsKey(2));
        }

        [Fact]
        public void Shifts_LogOneSummaryLineEach_WithCounts()
        {
            Seed(1, 4, 6);
            positioner.LoopSeamLastGhostFrameForTesting.Remove(6);

            positioner.ShiftLoopSeamTracesAfterDelete(4);

            Assert.Single(logLines, l => l.Contains("Loop-seam traces reindexed"));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("Loop-seam traces reindexed after delete at #4:")
                && l.Contains("reaim shifted=1 dropped=1 count=2")
                && l.Contains("loopSeam shifted=0 dropped=1 count=1"));

            logLines.Clear();
            positioner.ShiftLoopSeamTracesAfterInsert(1);

            Assert.Single(logLines, l => l.Contains("Loop-seam traces reindexed"));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("Loop-seam traces reindexed after insert at #1:")
                && l.Contains("reaim shifted=2 count=2")
                && l.Contains("loopSeam shifted=1 count=1"));
            Assert.Equal(new[] { 2, 6 }, Sorted(positioner.ReaimLastGhostFrameForTesting.Keys));
            Assert.Equal(new[] { 2 }, Sorted(positioner.LoopSeamLastGhostFrameForTesting.Keys));
        }

        [Fact]
        public void Shifts_OnEmptyTraces_AreNoOps()
        {
            positioner.ShiftLoopSeamTracesAfterDelete(0);
            positioner.ShiftLoopSeamTracesAfterInsert(0);

            Assert.Empty(positioner.ReaimLastGhostFrameForTesting);
            Assert.Empty(positioner.LoopSeamLastGhostFrameForTesting);
            Assert.Contains(logLines, l => l.Contains("reaim shifted=0 dropped=0 count=0"));
        }

        private static int[] Sorted(IEnumerable<int> keys)
        {
            var list = new List<int>(keys);
            list.Sort();
            return list.ToArray();
        }
    }
}
