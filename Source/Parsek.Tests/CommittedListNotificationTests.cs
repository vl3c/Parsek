using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// COMMITTED-LIST-SILENT-REMOVERS (2026-09-02): every store removal helper routes
    /// through one primitive that raises <c>CommittedRecordingRemoving</c> /
    /// <c>Removed</c> and bumps <c>StateVersion</c>, and the KSC ghost host shifts its
    /// index-keyed state from those notifications like the flight controller does.
    /// </summary>
    [Collection("Sequential")]
    public class CommittedListNotificationTests : IDisposable
    {
        private readonly List<string> sequence = new List<string>();

        public CommittedListNotificationTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            RecordingStore.CommittedRecordingRemoving += (index, removed) =>
                sequence.Add($"removing:{index}:{removed.RecordingId}:count={RecordingStore.CommittedRecordings.Count}");
            RecordingStore.CommittedRecordingRemoved += (index, removed, absorbedInto) =>
                sequence.Add($"removed:{index}:{removed.RecordingId}:target={(absorbedInto == null ? "none" : absorbedInto.RecordingId)}:count={RecordingStore.CommittedRecordings.Count}");
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
        }

        private static Recording MakeRecording(string id, string chainId = null, int chainIndex = -1)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "V-" + id,
                ChainId = chainId,
                ChainIndex = chainIndex,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 1000, altitude = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 1100, altitude = 100, bodyName = "Kerbin" });
            return rec;
        }

        private static void AddThree()
        {
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("a"));
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("b"));
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("c"));
        }

        [Fact]
        public void RemoveCommittedInternal_RaisesRemovingThenRemoved_AndBumps()
        {
            AddThree();
            var b = RecordingStore.CommittedRecordings[1];
            int versionBefore = RecordingStore.StateVersion;

            Assert.True(RecordingStore.RemoveCommittedInternal(b));

            Assert.NotEqual(versionBefore, RecordingStore.StateVersion);
            Assert.Equal(new[] { "removing:1:b:count=3", "removed:1:b:target=none:count=2" }, sequence);
            Assert.Equal("c", RecordingStore.CommittedRecordings[1].RecordingId);
        }

        [Fact]
        public void RemoveCommittedInternal_AbsentOrNull_RaisesNothing()
        {
            AddThree();
            int versionBefore = RecordingStore.StateVersion;

            Assert.False(RecordingStore.RemoveCommittedInternal(MakeRecording("zzz")));
            Assert.False(RecordingStore.RemoveCommittedInternal(null));

            Assert.Equal(versionBefore, RecordingStore.StateVersion);
            Assert.Empty(sequence);
        }

        [Fact]
        public void RemoveCommittedById_RaisesAtTheMatchedIndex()
        {
            AddThree();

            Assert.True(RecordingStore.RemoveCommittedById("c"));
            Assert.False(RecordingStore.RemoveCommittedById("c"));

            Assert.Equal(new[] { "removing:2:c:count=3", "removed:2:c:target=none:count=2" }, sequence);
        }

        [Fact]
        public void RemoveChainRecordings_RaisesOncePerMember_DescendingSoIndicesStayValid()
        {
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("x0", "chain", 0));
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("solo"));
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("x1", "chain", 1));

            RecordingStore.RemoveChainRecordings("chain");

            Assert.Equal(new[]
            {
                "removing:2:x1:count=3",
                "removed:2:x1:target=none:count=2",
                "removing:0:x0:count=2",
                "removed:0:x0:target=none:count=1",
            }, sequence);
            var only = Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("solo", only.RecordingId);
        }

        [Fact]
        public void RemoveCommittedTreeById_RaisesForEachOfTheTreesRecordings()
        {
            var tree = new RecordingTree { Id = "tree-1", TreeName = "T" };
            var r1 = MakeRecording("t1");
            var r2 = MakeRecording("t2");
            r1.TreeId = tree.Id;
            r2.TreeId = tree.Id;
            tree.AddOrReplaceRecording(r1);
            tree.AddOrReplaceRecording(r2);
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("other"));
            RecordingStore.AddRecordingWithTreeForTesting(r1);
            RecordingStore.AddRecordingWithTreeForTesting(r2);
            RecordingStore.CommittedTrees.Add(tree);

            Assert.True(RecordingStore.RemoveCommittedTreeById(tree.Id, "test"));

            Assert.Equal(4, sequence.Count);
            Assert.Contains("removing:1:t1:count=3", sequence);
            Assert.Contains("removed:1:t2:target=none:count=1", sequence);
            var only = Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("other", only.RecordingId);
        }

        [Fact]
        public void IndexOfRecordingId_FindsOrdinalMatch_ElseMinusOne()
        {
            AddThree();
            var committed = RecordingStore.CommittedRecordings;
            Assert.Equal(1, RecordingStore.IndexOfRecordingId(committed, "b"));
            Assert.Equal(-1, RecordingStore.IndexOfRecordingId(committed, "B"));
            Assert.Equal(-1, RecordingStore.IndexOfRecordingId(committed, null));
            Assert.Equal(-1, RecordingStore.IndexOfRecordingId(null, "b"));
        }

        [Fact]
        public void KscIndexKeyedState_ShiftsOnInsertAndDelete_AndClearsRebuiltTables()
        {
            var g1 = new GhostPlaybackState { vesselName = "g1" };
            var g3 = new GhostPlaybackState { vesselName = "g3" };
            var primary = new Dictionary<int, GhostPlaybackState> { [1] = g1, [3] = g3 };
            var overlap = new Dictionary<int, List<GhostPlaybackState>> { [3] = new List<GhostPlaybackState> { g3 } };
            var loggedSpawn = new HashSet<int> { 1, 3 };
            var loggedReshow = new HashSet<int> { 3 };
            var cadence = new Dictionary<int, (double, double, double)> { [3] = (1, 2, 3) };
            var schedules = new Dictionary<int, GhostPlaybackLogic.AutoLoopLaunchSchedule>
            {
                [1] = default(GhostPlaybackLogic.AutoLoopLaunchSchedule)
            };
            var selection = new Dictionary<int, (long, int)> { [1] = (5L, 1) };

            ParsekKSC.ShiftKscIndexKeyedState(2, insert: true,
                primary, overlap, loggedSpawn, loggedReshow, cadence, schedules, selection);

            Assert.Same(g1, primary[1]);
            Assert.Same(g3, primary[4]);
            Assert.Equal(2, primary.Count);
            Assert.Same(g3, overlap[4][0]);
            Assert.Equal(new HashSet<int> { 1, 4 }, loggedSpawn);
            Assert.Equal(new HashSet<int> { 4 }, loggedReshow);
            Assert.True(cadence.ContainsKey(4));
            Assert.Empty(schedules);
            Assert.Empty(selection);

            ParsekKSC.ShiftKscIndexKeyedState(1, insert: false,
                primary, overlap, loggedSpawn, loggedReshow, cadence, schedules, selection);

            Assert.False(primary.ContainsKey(1));
            Assert.Same(g3, primary[3]);
            Assert.Single(primary);
            Assert.Same(g3, overlap[3][0]);
            Assert.Equal(new HashSet<int> { 3 }, loggedSpawn);
            Assert.True(cadence.ContainsKey(3));
        }
    }
}
