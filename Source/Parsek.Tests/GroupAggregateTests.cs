using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for group aggregate methods: GetGroupEarliestStartUT, GetGroupTotalDuration,
    /// GetGroupStatus, GetGroupSortKey, GetRecordingSortKey, GetChainSortKey.
    /// </summary>
    [Collection("Sequential")]
    public class GroupAggregateTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GroupAggregateTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GroupHierarchyStore.ResetGroupsForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
        }

        private static Recording MakeRec(double startUT, double endUT, string name = "Test")
        {
            var rec = new Recording
            {
                VesselName = name,
            };
            // StartUT/EndUT derived from Points[0].ut and Points[last].ut
            rec.Points.Add(new TrajectoryPoint { ut = startUT });
            if (endUT > startUT)
                rec.Points.Add(new TrajectoryPoint { ut = endUT });
            return rec;
        }

        // ── GetGroupEarliestStartUT ──

        [Fact]
        public void EarliestStartUT_EmptyDescendants_ReturnsMaxValue()
        {
            var descendants = new HashSet<int>();
            var committed = new List<Recording>();
            double result = ParsekUI.GetGroupEarliestStartUT(descendants, committed);
            Assert.Equal(double.MaxValue, result);
        }

        [Fact]
        public void EarliestStartUT_SingleRecording_ReturnsItsStartUT()
        {
            var committed = new List<Recording> { MakeRec(100, 200) };
            var descendants = new HashSet<int> { 0 };
            double result = ParsekUI.GetGroupEarliestStartUT(descendants, committed);
            Assert.Equal(100, result);
        }

        [Fact]
        public void EarliestStartUT_MultipleRecordings_ReturnsEarliest()
        {
            var committed = new List<Recording>
            {
                MakeRec(300, 400),
                MakeRec(100, 200),
                MakeRec(200, 300)
            };
            var descendants = new HashSet<int> { 0, 1, 2 };
            double result = ParsekUI.GetGroupEarliestStartUT(descendants, committed);
            Assert.Equal(100, result);
        }

        // ── GetGroupTotalDuration ──

        [Fact]
        public void TotalDuration_EmptyDescendants_ReturnsZero()
        {
            var descendants = new HashSet<int>();
            var committed = new List<Recording>();
            double result = ParsekUI.GetGroupTotalDuration(descendants, committed);
            Assert.Equal(0, result);
        }

        [Fact]
        public void TotalDuration_SumsDurations()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, 150),  // 50s
                MakeRec(200, 280),  // 80s
                MakeRec(300, 310)   // 10s
            };
            var descendants = new HashSet<int> { 0, 1, 2 };
            double result = ParsekUI.GetGroupTotalDuration(descendants, committed);
            Assert.Equal(140, result);
        }

        [Fact]
        public void TotalDuration_SkipsZeroDurationRecordings()
        {
            // MakeRec(100, 50) produces a single-point recording (endUT <= startUT),
            // so StartUT == EndUT == 100, giving duration 0 which is excluded.
            var committed = new List<Recording>
            {
                MakeRec(100, 50),   // 0s (single point), skipped
                MakeRec(200, 300)   // 100s
            };
            var descendants = new HashSet<int> { 0, 1 };
            double result = ParsekUI.GetGroupTotalDuration(descendants, committed);
            Assert.Equal(100, result);
        }

        // ── GetGroupStatus ──

        [Fact]
        public void GroupStatus_EmptyDescendants_ReturnsDash()
        {
            var descendants = new HashSet<int>();
            var committed = new List<Recording>();
            string text;
            int order;
            ParsekUI.GetGroupStatus(descendants, committed, 500, out text, out order);
            Assert.Equal("-", text);
            Assert.Equal(2, order);
        }

        [Fact]
        public void GroupStatus_AllFuture_ReturnsClosestFuture()
        {
            var committed = new List<Recording>
            {
                MakeRec(1000, 1100),
                MakeRec(800, 900)
            };
            var descendants = new HashSet<int> { 0, 1 };
            string text;
            int order;
            ParsekUI.GetGroupStatus(descendants, committed, 500, out text, out order);
            Assert.Equal(0, order); // future
            // Should show countdown for the closer future recording (800)
            Assert.Contains("T-", text);
        }

        [Fact]
        public void GroupStatus_ActivePreferredOverFuture()
        {
            var committed = new List<Recording>
            {
                MakeRec(400, 600),  // active at now=500
                MakeRec(800, 900)   // future
            };
            var descendants = new HashSet<int> { 0, 1 };
            string text;
            int order;
            ParsekUI.GetGroupStatus(descendants, committed, 500, out text, out order);
            Assert.Equal(1, order); // active
            Assert.Contains("T+", text); // T+ since now > StartUT
        }

        [Fact]
        public void GroupStatus_MultipleActive_ReturnsClosestToNow()
        {
            var committed = new List<Recording>
            {
                MakeRec(300, 600),  // active, delta from StartUT = 200
                MakeRec(490, 600)   // active, delta from StartUT = 10 (closer)
            };
            var descendants = new HashSet<int> { 0, 1 };
            string text;
            int order;
            ParsekUI.GetGroupStatus(descendants, committed, 500, out text, out order);
            Assert.Equal(1, order); // active
        }

        [Fact]
        public void GroupStatus_AllPast_ReturnsPast()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, 200),
                MakeRec(300, 400)
            };
            var descendants = new HashSet<int> { 0, 1 };
            string text;
            int order;
            ParsekUI.GetGroupStatus(descendants, committed, 500, out text, out order);
            Assert.Equal(2, order);
            Assert.Equal("past", text);
        }

        // ── GetRecordingSortKey ──

        [Fact]
        public void RecordingSortKey_LaunchTime_ReturnsStartUT()
        {
            var rec = MakeRec(150, 300);
            double key = ParsekUI.GetRecordingSortKey(rec, ParsekUI.SortColumn.LaunchTime, 0, 5);
            Assert.Equal(150, key);
        }

        [Fact]
        public void RecordingSortKey_Duration_ReturnsDuration()
        {
            var rec = MakeRec(100, 250);
            double key = ParsekUI.GetRecordingSortKey(rec, ParsekUI.SortColumn.Duration, 0, 5);
            Assert.Equal(150, key);
        }

        [Fact]
        public void RecordingSortKey_Status_ReturnsStatusOrder()
        {
            var rec = MakeRec(100, 200);
            double key = ParsekUI.GetRecordingSortKey(rec, ParsekUI.SortColumn.Status, 150, 5);
            Assert.Equal(1, key); // active
        }

        [Fact]
        public void RecordingSortKey_Index_ReturnsRowFallback()
        {
            var rec = MakeRec(100, 200);
            double key = ParsekUI.GetRecordingSortKey(rec, ParsekUI.SortColumn.Index, 0, 7);
            Assert.Equal(7, key);
        }

        // ── GetChainSortKey ──

        [Fact]
        public void ChainSortKey_LaunchTime_ReturnsEarliest()
        {
            var committed = new List<Recording>
            {
                MakeRec(300, 400),
                MakeRec(100, 200),
                MakeRec(200, 300)
            };
            var members = new List<int> { 0, 1, 2 };
            double key = ParsekUI.GetChainSortKey(members, committed,
                ParsekUI.SortColumn.LaunchTime, 0);
            Assert.Equal(100, key);
        }

        [Fact]
        public void ChainSortKey_Duration_ReturnsTotalDuration()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, 150),  // 50s
                MakeRec(200, 300)   // 100s
            };
            var members = new List<int> { 0, 1 };
            double key = ParsekUI.GetChainSortKey(members, committed,
                ParsekUI.SortColumn.Duration, 0);
            Assert.Equal(150, key);
        }

        [Fact]
        public void ChainSortKey_Status_ReturnsBestStatus()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, 200),  // past at now=500
                MakeRec(400, 600)   // active at now=500
            };
            var members = new List<int> { 0, 1 };
            double key = ParsekUI.GetChainSortKey(members, committed,
                ParsekUI.SortColumn.Status, 500);
            Assert.Equal(1, key); // active is best
        }

        // ── GetGroupSortKey ──

        [Fact]
        public void GroupSortKey_EmptyDescendants_ReturnsMaxValue()
        {
            var descendants = new HashSet<int>();
            var committed = new List<Recording>();
            double key = ParsekUI.GetGroupSortKey(descendants, committed,
                ParsekUI.SortColumn.LaunchTime, 0);
            Assert.Equal(double.MaxValue, key);
        }

        [Fact]
        public void GroupSortKey_LaunchTime_ReturnsEarliestStartUT()
        {
            var committed = new List<Recording>
            {
                MakeRec(300, 400),
                MakeRec(100, 200)
            };
            var descendants = new HashSet<int> { 0, 1 };
            double key = ParsekUI.GetGroupSortKey(descendants, committed,
                ParsekUI.SortColumn.LaunchTime, 0);
            Assert.Equal(100, key);
        }

        [Fact]
        public void GroupSortKey_Duration_ReturnsTotalDuration()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, 150),
                MakeRec(200, 350)
            };
            var descendants = new HashSet<int> { 0, 1 };
            double key = ParsekUI.GetGroupSortKey(descendants, committed,
                ParsekUI.SortColumn.Duration, 0);
            Assert.Equal(200, key); // 50 + 150
        }

        [Fact]
        public void GroupSortKey_Status_ReturnsStatusOrder()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, 200),  // past at now=500
                MakeRec(400, 600)   // active at now=500
            };
            var descendants = new HashSet<int> { 0, 1 };
            double key = ParsekUI.GetGroupSortKey(descendants, committed,
                ParsekUI.SortColumn.Status, 500);
            Assert.Equal(1, key); // active
        }

        [Fact]
        public void GroupStatus_ActiveAndPast_NoFuture_ReturnsActive()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, 200),  // past at now=500
                MakeRec(400, 600)   // active at now=500
            };
            var descendants = new HashSet<int> { 0, 1 };
            string text;
            int order;
            ParsekUI.GetGroupStatus(descendants, committed, 500, out text, out order);
            Assert.Equal(1, order); // active wins over past
        }

        [Fact]
        public void ChainSortKey_EmptyMembers_ReturnsDefaults()
        {
            var committed = new List<Recording>();
            var members = new List<int>();

            double launchKey = ParsekUI.GetChainSortKey(members, committed,
                ParsekUI.SortColumn.LaunchTime, 0);
            Assert.Equal(double.MaxValue, launchKey);

            double durKey = ParsekUI.GetChainSortKey(members, committed,
                ParsekUI.SortColumn.Duration, 0);
            Assert.Equal(0, durKey);

            double statusKey = ParsekUI.GetChainSortKey(members, committed,
                ParsekUI.SortColumn.Status, 0);
            Assert.Equal(2, statusKey); // past (default)
        }

        // ── Name sort key (string path) ──

        [Fact]
        public void GroupSortKey_Name_ReturnsZero()
        {
            // For Name sort, the numeric key is unused (string comparison is used instead).
            // GetGroupSortKey returns 0 for Name column.
            var committed = new List<Recording> { MakeRec(100, 200) };
            var descendants = new HashSet<int> { 0 };
            double key = ParsekUI.GetGroupSortKey(descendants, committed,
                ParsekUI.SortColumn.Name, 0);
            Assert.Equal(0, key);
        }

        [Fact]
        public void RecordingSortKey_Name_ReturnsRowFallback()
        {
            // For Name/Phase, numeric key falls back to row position
            var rec = MakeRec(100, 200, "Zulu");
            double key = ParsekUI.GetRecordingSortKey(rec, ParsekUI.SortColumn.Name, 0, 3);
            Assert.Equal(3, key); // row fallback
        }

        [Fact]
        public void ChainSortKey_Name_ReturnsZero()
        {
            var committed = new List<Recording> { MakeRec(100, 200) };
            var members = new List<int> { 0 };
            double key = ParsekUI.GetChainSortKey(members, committed,
                ParsekUI.SortColumn.Name, 0);
            Assert.Equal(0, key);
        }

        // ── Index sort key ──

        [Fact]
        public void GroupSortKey_Index_ReturnsNegativeOne()
        {
            // Groups get -1 for Index sort so they appear before recordings (row 0+)
            var committed = new List<Recording> { MakeRec(17000, 17100) };
            var descendants = new HashSet<int> { 0 };
            double key = ParsekUI.GetGroupSortKey(descendants, committed,
                ParsekUI.SortColumn.Index, 0);
            Assert.Equal(-1, key);
        }

        [Fact]
        public void IndexSort_GroupBeforeRecording()
        {
            // Groups (key=-1) should sort before recordings (key=row>=0)
            var committed = new List<Recording> { MakeRec(17000, 17100) };
            var descendants = new HashSet<int> { 0 };
            double groupKey = ParsekUI.GetGroupSortKey(descendants, committed,
                ParsekUI.SortColumn.Index, 0);
            double recKey = ParsekUI.GetRecordingSortKey(committed[0],
                ParsekUI.SortColumn.Index, 0, 0);
            Assert.True(groupKey < recKey, "Group should sort before recording in Index sort");
        }
    }
}
