using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Guards against schema drift on the Rewind-to-Staging <c>RewindPointId</c>
    /// field on BranchPoint (design doc section 5.4). Null stays null; a
    /// non-empty id round-trips exactly.
    /// </summary>
    public class BranchPointRewindPointIdRoundTripTests
    {
        [Fact]
        public void BranchPoint_NullRewindPointId_StaysNull_OnRoundTrip()
        {
            var bp = new BranchPoint
            {
                Id = "bp_no_rp",
                UT = 1000.0,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "rec_p" },
                ChildRecordingIds = new List<string> { "rec_c1", "rec_c2" }
                // RewindPointId left at default null
            };

            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);
            Assert.Null(node.GetValue("rewindPointId"));

            var restored = RecordingTree.LoadBranchPointFrom(node);
            Assert.Null(restored.RewindPointId);
        }

        [Fact]
        public void BranchPoint_PopulatedRewindPointId_RoundTripsExactly()
        {
            var bp = new BranchPoint
            {
                Id = "bp_with_rp",
                UT = 2000.0,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "rec_p" },
                ChildRecordingIds = new List<string> { "rec_c1", "rec_c2" },
                RewindPointId = "rp_abc"
            };

            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);
            Assert.Equal("rp_abc", node.GetValue("rewindPointId"));

            var restored = RecordingTree.LoadBranchPointFrom(node);
            Assert.Equal("rp_abc", restored.RewindPointId);
        }

        [Fact]
        public void BranchPoint_EmptyStringRewindPointId_TreatedAsNull()
        {
            // Defensive: the load path normalizes empty-string payload to null so
            // downstream code does not have to distinguish null vs "".
            var node = new ConfigNode("BRANCH_POINT");
            node.AddValue("id", "bp_empty_rp");
            node.AddValue("ut", "1500");
            node.AddValue("type", "0");
            node.AddValue("rewindPointId", "");

            var restored = RecordingTree.LoadBranchPointFrom(node);
            Assert.Null(restored.RewindPointId);
        }
    }
}
