using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the Recordings Manager window features:
    /// FormatDuration, GetStatusOrder, sort logic, RemoveRecordingAt.
    /// </summary>
    public class FormatDurationTests
    {
        [Theory]
        [InlineData(0, "0s")]
        [InlineData(1, "1s")]
        [InlineData(30, "30s")]
        [InlineData(59, "59s")]
        [InlineData(59.4, "59s")]
        [InlineData(59.9, "59s")]
        public void FormatDuration_Seconds(double seconds, string expected)
        {
            Assert.Equal(expected, ParsekUI.FormatDuration(seconds));
        }

        [Theory]
        [InlineData(60, "1m 0s")]
        [InlineData(90, "1m 30s")]
        [InlineData(125, "2m 5s")]
        [InlineData(3599, "59m 59s")]
        public void FormatDuration_Minutes(double seconds, string expected)
        {
            Assert.Equal(expected, ParsekUI.FormatDuration(seconds));
        }

        [Theory]
        [InlineData(3600, "1h 0m")]
        [InlineData(5400, "1h 30m")]
        [InlineData(7200, "2h 0m")]
        [InlineData(86400, "24h 0m")]
        public void FormatDuration_Hours(double seconds, string expected)
        {
            Assert.Equal(expected, ParsekUI.FormatDuration(seconds));
        }
    }

    public class StatusOrderTests
    {
        private Recording MakeRecording(double startUT, double endUT)
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = startUT, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = endUT, bodyName = "Kerbin" });
            return rec;
        }

        [Fact]
        public void GetStatusOrder_BeforeStart_ReturnsFuture()
        {
            var rec = MakeRecording(100, 200);
            Assert.Equal(0, ParsekUI.GetStatusOrder(rec, 50));
        }

        [Fact]
        public void GetStatusOrder_AtStart_ReturnsActive()
        {
            var rec = MakeRecording(100, 200);
            Assert.Equal(1, ParsekUI.GetStatusOrder(rec, 100));
        }

        [Fact]
        public void GetStatusOrder_DuringPlayback_ReturnsActive()
        {
            var rec = MakeRecording(100, 200);
            Assert.Equal(1, ParsekUI.GetStatusOrder(rec, 150));
        }

        [Fact]
        public void GetStatusOrder_AtEnd_ReturnsActive()
        {
            var rec = MakeRecording(100, 200);
            Assert.Equal(1, ParsekUI.GetStatusOrder(rec, 200));
        }

        [Fact]
        public void GetStatusOrder_AfterEnd_ReturnsPast()
        {
            var rec = MakeRecording(100, 200);
            Assert.Equal(2, ParsekUI.GetStatusOrder(rec, 201));
        }
    }

    public class SortComparisonTests
    {
        private static List<TrajectoryPoint> MakePoints(double startUT, double endUT)
        {
            return new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = startUT, bodyName = "Kerbin" },
                new TrajectoryPoint { ut = endUT, bodyName = "Kerbin" }
            };
        }

        private Recording MakeRec(string name, double startUT, double endUT)
        {
            var rec = new Recording { VesselName = name };
            rec.Points.AddRange(MakePoints(startUT, endUT));
            return rec;
        }

        [Fact]
        public void CompareByName_Ascending_AlphabeticalOrder()
        {
            var a = MakeRec("Alpha", 100, 200);
            var b = MakeRec("Beta", 100, 200);

            int cmp = ParsekUI.CompareRecordings(a, b,
                ParsekUI.SortColumn.Name, ascending: true, now: 0);

            Assert.True(cmp < 0, "Alpha should come before Beta");
        }

        [Fact]
        public void CompareByName_Descending_ReverseOrder()
        {
            var a = MakeRec("Alpha", 100, 200);
            var b = MakeRec("Beta", 100, 200);

            int cmp = ParsekUI.CompareRecordings(a, b,
                ParsekUI.SortColumn.Name, ascending: false, now: 0);

            Assert.True(cmp > 0, "Alpha should come after Beta in descending");
        }

        [Fact]
        public void CompareByName_CaseInsensitive()
        {
            var a = MakeRec("alpha", 100, 200);
            var b = MakeRec("Alpha", 100, 200);

            int cmp = ParsekUI.CompareRecordings(a, b,
                ParsekUI.SortColumn.Name, ascending: true, now: 0);

            Assert.Equal(0, cmp);
        }

        [Fact]
        public void CompareByName_EmptyTreatedAsUntitled()
        {
            var a = MakeRec("", 100, 200);
            var b = MakeRec("Untitled", 100, 200);

            int cmp = ParsekUI.CompareRecordings(a, b,
                ParsekUI.SortColumn.Name, ascending: true, now: 0);

            Assert.Equal(0, cmp);
        }

        [Fact]
        public void CompareByLaunchTime_EarlierFirst()
        {
            var a = MakeRec("A", 50, 100);
            var b = MakeRec("B", 100, 200);

            int cmp = ParsekUI.CompareRecordings(a, b,
                ParsekUI.SortColumn.LaunchTime, ascending: true, now: 0);

            Assert.True(cmp < 0);
        }

        [Fact]
        public void CompareByLaunchTime_SameTime_ReturnsZero()
        {
            var a = MakeRec("A", 100, 200);
            var b = MakeRec("B", 100, 300);

            int cmp = ParsekUI.CompareRecordings(a, b,
                ParsekUI.SortColumn.LaunchTime, ascending: true, now: 0);

            Assert.Equal(0, cmp);
        }

        [Fact]
        public void CompareByDuration_ShorterFirst()
        {
            var a = MakeRec("A", 100, 130);  // 30s
            var b = MakeRec("B", 100, 200);  // 100s

            int cmp = ParsekUI.CompareRecordings(a, b,
                ParsekUI.SortColumn.Duration, ascending: true, now: 0);

            Assert.True(cmp < 0);
        }

        [Fact]
        public void CompareByDuration_Descending_LongerFirst()
        {
            var a = MakeRec("A", 100, 130);  // 30s
            var b = MakeRec("B", 100, 200);  // 100s

            int cmp = ParsekUI.CompareRecordings(a, b,
                ParsekUI.SortColumn.Duration, ascending: false, now: 0);

            Assert.True(cmp > 0);
        }

        [Fact]
        public void CompareByStatus_FutureBeforeActive()
        {
            var future = MakeRec("F", 200, 300);
            var active = MakeRec("A", 100, 200);

            int cmp = ParsekUI.CompareRecordings(future, active,
                ParsekUI.SortColumn.Status, ascending: true, now: 150);

            Assert.True(cmp < 0);
        }

        [Fact]
        public void CompareByStatus_ActiveBeforePast()
        {
            var active = MakeRec("A", 100, 200);
            var past = MakeRec("P", 10, 50);

            int cmp = ParsekUI.CompareRecordings(active, past,
                ParsekUI.SortColumn.Status, ascending: true, now: 150);

            Assert.True(cmp < 0);
        }

        [Fact]
        public void CompareByIndex_ReturnsZero()
        {
            var a = MakeRec("A", 100, 200);
            var b = MakeRec("B", 200, 300);

            // Index sort doesn't compare recordings — it preserves original order
            int cmp = ParsekUI.CompareRecordings(a, b,
                ParsekUI.SortColumn.Index, ascending: true, now: 0);

            Assert.Equal(0, cmp);
        }
    }

    public class BuildSortedIndicesTests
    {
        private static List<TrajectoryPoint> MakePoints(double startUT, double endUT)
        {
            return new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = startUT, bodyName = "Kerbin" },
                new TrajectoryPoint { ut = endUT, bodyName = "Kerbin" }
            };
        }

        private Recording MakeRec(string name, double startUT, double endUT)
        {
            var rec = new Recording { VesselName = name };
            rec.Points.AddRange(MakePoints(startUT, endUT));
            return rec;
        }

        [Fact]
        public void IndexAscending_NaturalOrder()
        {
            var list = new List<Recording>
            {
                MakeRec("C", 300, 400),
                MakeRec("A", 100, 200),
                MakeRec("B", 200, 300),
            };

            var indices = ParsekUI.BuildSortedIndices(list,
                ParsekUI.SortColumn.Index, ascending: true, now: 0);

            Assert.Equal(new[] { 0, 1, 2 }, indices);
        }

        [Fact]
        public void IndexDescending_ReversedOrder()
        {
            var list = new List<Recording>
            {
                MakeRec("C", 300, 400),
                MakeRec("A", 100, 200),
                MakeRec("B", 200, 300),
            };

            var indices = ParsekUI.BuildSortedIndices(list,
                ParsekUI.SortColumn.Index, ascending: false, now: 0);

            Assert.Equal(new[] { 2, 1, 0 }, indices);
        }

        [Fact]
        public void SortByName_Ascending()
        {
            var list = new List<Recording>
            {
                MakeRec("Charlie", 100, 200),
                MakeRec("Alpha", 100, 200),
                MakeRec("Bravo", 100, 200),
            };

            var indices = ParsekUI.BuildSortedIndices(list,
                ParsekUI.SortColumn.Name, ascending: true, now: 0);

            // Alpha(1), Bravo(2), Charlie(0)
            Assert.Equal(new[] { 1, 2, 0 }, indices);
        }

        [Fact]
        public void SortByLaunchTime_Ascending()
        {
            var list = new List<Recording>
            {
                MakeRec("Late", 300, 400),
                MakeRec("Early", 100, 200),
                MakeRec("Mid", 200, 300),
            };

            var indices = ParsekUI.BuildSortedIndices(list,
                ParsekUI.SortColumn.LaunchTime, ascending: true, now: 0);

            // Early(1), Mid(2), Late(0)
            Assert.Equal(new[] { 1, 2, 0 }, indices);
        }

        [Fact]
        public void SortByDuration_Ascending()
        {
            var list = new List<Recording>
            {
                MakeRec("Medium", 100, 200),  // 100s
                MakeRec("Short", 100, 120),   // 20s
                MakeRec("Long", 100, 400),    // 300s
            };

            var indices = ParsekUI.BuildSortedIndices(list,
                ParsekUI.SortColumn.Duration, ascending: true, now: 0);

            // Short(1), Medium(0), Long(2)
            Assert.Equal(new[] { 1, 0, 2 }, indices);
        }

        [Fact]
        public void SortByStatus_FutureActivesPast()
        {
            double now = 250;
            var list = new List<Recording>
            {
                MakeRec("Past", 100, 200),      // past at now=250
                MakeRec("Active", 200, 300),     // active at now=250
                MakeRec("Future", 300, 400),     // future at now=250
            };

            var indices = ParsekUI.BuildSortedIndices(list,
                ParsekUI.SortColumn.Status, ascending: true, now: now);

            // Future(2), Active(1), Past(0)
            Assert.Equal(new[] { 2, 1, 0 }, indices);
        }

        [Fact]
        public void SortByName_Descending()
        {
            var list = new List<Recording>
            {
                MakeRec("Alpha", 100, 200),
                MakeRec("Charlie", 100, 200),
                MakeRec("Bravo", 100, 200),
            };

            var indices = ParsekUI.BuildSortedIndices(list,
                ParsekUI.SortColumn.Name, ascending: false, now: 0);

            // Charlie(1), Bravo(2), Alpha(0)
            Assert.Equal(new[] { 1, 2, 0 }, indices);
        }

        [Fact]
        public void EmptyList_ReturnsEmptyArray()
        {
            var list = new List<Recording>();

            var indices = ParsekUI.BuildSortedIndices(list,
                ParsekUI.SortColumn.Name, ascending: true, now: 0);

            Assert.Empty(indices);
        }

        [Fact]
        public void SingleItem_ReturnsSingleIndex()
        {
            var list = new List<Recording>
            {
                MakeRec("Only", 100, 200),
            };

            var indices = ParsekUI.BuildSortedIndices(list,
                ParsekUI.SortColumn.Name, ascending: true, now: 0);

            Assert.Equal(new[] { 0 }, indices);
        }
    }

    [Collection("Sequential")]
    public class RemoveRecordingAtTests
    {
        public RemoveRecordingAtTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        private List<TrajectoryPoint> MakePoints(int count, double startUT = 100)
        {
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < count; i++)
            {
                points.Add(new TrajectoryPoint
                {
                    ut = startUT + i * 10,
                    latitude = 0, longitude = 0, altitude = 100,
                    rotation = Quaternion.identity, velocity = Vector3.zero,
                    bodyName = "Kerbin"
                });
            }
            return points;
        }

        private void CommitRecording(string name, double startUT = 100, string chainId = null, int chainIndex = -1, int chainBranch = 0)
        {
            RecordingStore.StashPending(MakePoints(3, startUT), name);
            if (chainId != null)
            {
                RecordingStore.Pending.ChainId = chainId;
                RecordingStore.Pending.ChainIndex = chainIndex;
                RecordingStore.Pending.ChainBranch = chainBranch;
            }
            RecordingStore.CommitPending();
        }

        [Fact]
        public void RemoveRecordingAt_RemovesCorrectRecording()
        {
            CommitRecording("First", 100);
            CommitRecording("Second", 200);
            CommitRecording("Third", 300);

            RecordingStore.RemoveRecordingAt(1);

            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
            Assert.Equal("First", RecordingStore.CommittedRecordings[0].VesselName);
            Assert.Equal("Third", RecordingStore.CommittedRecordings[1].VesselName);
        }

        [Fact]
        public void RemoveRecordingAt_FirstItem()
        {
            CommitRecording("First", 100);
            CommitRecording("Second", 200);

            RecordingStore.RemoveRecordingAt(0);

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("Second", RecordingStore.CommittedRecordings[0].VesselName);
        }

        [Fact]
        public void RemoveRecordingAt_LastItem()
        {
            CommitRecording("First", 100);
            CommitRecording("Second", 200);

            RecordingStore.RemoveRecordingAt(1);

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("First", RecordingStore.CommittedRecordings[0].VesselName);
        }

        [Fact]
        public void RemoveRecordingAt_OnlyItem()
        {
            CommitRecording("Only", 100);

            RecordingStore.RemoveRecordingAt(0);

            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void RemoveRecordingAt_OutOfRange_Negative_DoesNothing()
        {
            CommitRecording("A", 100);

            RecordingStore.RemoveRecordingAt(-1);

            Assert.Single(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void RemoveRecordingAt_OutOfRange_TooHigh_DoesNothing()
        {
            CommitRecording("A", 100);

            RecordingStore.RemoveRecordingAt(5);

            Assert.Single(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void RemoveRecordingAt_DegradesSiblingsInSameChain()
        {
            string chainId = "chain-abc";
            CommitRecording("Seg0", 100, chainId, 0);
            CommitRecording("Seg1", 200, chainId, 1);
            CommitRecording("Seg2", 300, chainId, 2);
            CommitRecording("Standalone", 400); // not in chain

            // Remove middle segment
            RecordingStore.RemoveRecordingAt(1);

            // Remaining chain members should be degraded to standalone
            Assert.Equal(3, RecordingStore.CommittedRecordings.Count);
            var seg0 = RecordingStore.CommittedRecordings[0];
            var seg2 = RecordingStore.CommittedRecordings[1];
            var standalone = RecordingStore.CommittedRecordings[2];

            Assert.Null(seg0.ChainId);
            Assert.Equal(-1, seg0.ChainIndex);
            Assert.Equal(0, seg0.ChainBranch);

            Assert.Null(seg2.ChainId);
            Assert.Equal(-1, seg2.ChainIndex);
            Assert.Equal(0, seg2.ChainBranch);

            // Standalone should be unaffected
            Assert.Null(standalone.ChainId);
            Assert.Equal(-1, standalone.ChainIndex);
        }

        [Fact]
        public void RemoveRecordingAt_ChainBranchAlsoDegraded()
        {
            string chainId = "chain-xyz";
            CommitRecording("Primary0", 100, chainId, 0, 0);
            CommitRecording("Branch0", 100, chainId, 0, 1);

            // Remove primary
            RecordingStore.RemoveRecordingAt(0);

            // Branch member should be degraded
            Assert.Single(RecordingStore.CommittedRecordings);
            var remaining = RecordingStore.CommittedRecordings[0];
            Assert.Null(remaining.ChainId);
            Assert.Equal(-1, remaining.ChainIndex);
            Assert.Equal(0, remaining.ChainBranch);
        }

        [Fact]
        public void RemoveRecordingAt_StandaloneDoesNotAffectChains()
        {
            string chainId = "chain-keep";
            CommitRecording("Standalone", 50);
            CommitRecording("Seg0", 100, chainId, 0);
            CommitRecording("Seg1", 200, chainId, 1);

            // Remove standalone (index 0)
            RecordingStore.RemoveRecordingAt(0);

            // Chain should be intact
            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
            Assert.Equal(chainId, RecordingStore.CommittedRecordings[0].ChainId);
            Assert.Equal(0, RecordingStore.CommittedRecordings[0].ChainIndex);
            Assert.Equal(chainId, RecordingStore.CommittedRecordings[1].ChainId);
            Assert.Equal(1, RecordingStore.CommittedRecordings[1].ChainIndex);
        }

        [Fact]
        public void RemoveRecordingAt_DifferentChainsUnaffected()
        {
            CommitRecording("A0", 100, "chain-a", 0);
            CommitRecording("A1", 200, "chain-a", 1);
            CommitRecording("B0", 300, "chain-b", 0);
            CommitRecording("B1", 400, "chain-b", 1);

            // Remove from chain-a
            RecordingStore.RemoveRecordingAt(0);

            // chain-a sibling degraded
            Assert.Null(RecordingStore.CommittedRecordings[0].ChainId);
            Assert.Equal("A1", RecordingStore.CommittedRecordings[0].VesselName);

            // chain-b intact
            Assert.Equal("chain-b", RecordingStore.CommittedRecordings[1].ChainId);
            Assert.Equal(0, RecordingStore.CommittedRecordings[1].ChainIndex);
            Assert.Equal("chain-b", RecordingStore.CommittedRecordings[2].ChainId);
            Assert.Equal(1, RecordingStore.CommittedRecordings[2].ChainIndex);
        }
    }
}
