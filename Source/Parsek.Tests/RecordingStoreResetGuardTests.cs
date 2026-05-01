using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Regression tests for the data-loss guard in
    /// <see cref="RecordingStore.ResetForTesting"/> and the
    /// <see cref="RecordingStoreTestSnapshot"/> non-destructive snapshot pattern.
    ///
    /// Production bug source: 2026-05-01 KSP.log shows
    /// <c>PersistenceSplitOptimizerTest</c> calling <c>ResetForTesting()</c> from inside
    /// the in-game test runner (Ctrl+Shift+T), which silently wiped the player's 5
    /// committed recordings (R0 + 4×R1) from the live save. The next OnSave wrote 0
    /// RECORDING_TREE nodes and the user only recovered through quicksave.sfs.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingStoreResetGuardTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecordingStoreResetGuardTests()
        {
            RecordingStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ApplicationIsPlayingForTesting = null;
            // Direct reset is safe here: outside Unity play mode the guard is a no-op.
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ApplicationIsPlayingForTesting = null;
            RecordingStore.ResetForTesting();
        }

        private static List<TrajectoryPoint> MakePoints(int count)
        {
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < count; i++)
            {
                points.Add(new TrajectoryPoint
                {
                    ut = 100 + i * 10,
                    latitude = 0,
                    longitude = 0,
                    altitude = 100,
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero,
                    bodyName = "Kerbin"
                });
            }
            return points;
        }

        // ─────────── Guard tests ───────────

        [Fact]
        public void ResetForTesting_NoOpInPlayMode_WhenStoreIsEmpty()
        {
            // Even with the play-mode flag asserted, an empty store is safe to reset —
            // there is nothing the guard could lose.
            RecordingStore.ApplicationIsPlayingForTesting = () => true;

            RecordingStore.ResetForTesting();

            Assert.Empty(RecordingStore.CommittedRecordings);
            Assert.Empty(RecordingStore.CommittedTrees);
        }

        [Fact]
        public void ResetForTesting_ThrowsInPlayMode_WhenCommittedRecordingsExist()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "LiveShip");
            Assert.NotNull(rec);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Assert.Single(RecordingStore.CommittedRecordings);

            // Simulate the in-game test runner: Application.isPlaying = true.
            RecordingStore.ApplicationIsPlayingForTesting = () => true;

            var ex = Assert.Throws<InvalidOperationException>(
                () => RecordingStore.ResetForTesting());

            Assert.Contains("ResetForTesting blocked", ex.Message);
            Assert.Contains("committedRecordings=1", ex.Message);

            // Live data must survive the failed reset.
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("LiveShip", RecordingStore.CommittedRecordings[0].VesselName);

            // The error must hit the log so the failure is visible in KSP.log.
            Assert.Contains(logLines,
                l => l.Contains("[ERROR][RecordingStore]") && l.Contains("ResetForTesting blocked"));
        }

        [Fact]
        public void ResetForTesting_ThrowsInPlayMode_WhenPendingTreeExists()
        {
            var tree = new RecordingTree { Id = "pendT", TreeName = "PendingShip" };
            RecordingStore.StashPendingTree(tree);
            Assert.True(RecordingStore.HasPendingTree);

            RecordingStore.ApplicationIsPlayingForTesting = () => true;

            var ex = Assert.Throws<InvalidOperationException>(
                () => RecordingStore.ResetForTesting());

            Assert.Contains("hasPendingTree=True", ex.Message);
            Assert.True(RecordingStore.HasPendingTree);

            // Parity with the committed-recordings sibling test: the throw must reach
            // KSP.log so the failure is visible in production diagnostics.
            Assert.Contains(logLines,
                l => l.Contains("[ERROR][RecordingStore]") && l.Contains("ResetForTesting blocked"));
        }

        [Fact]
        public void ResetForTesting_AllowsReset_WhenNotInPlayMode()
        {
            // xUnit / dotnet-test path: Application.isPlaying = false. The guard must
            // not interfere with existing test fixtures that legitimately reset state.
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Probe");
            Assert.NotNull(rec);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Assert.Single(RecordingStore.CommittedRecordings);

            RecordingStore.ApplicationIsPlayingForTesting = () => false;
            RecordingStore.ResetForTesting();

            Assert.Empty(RecordingStore.CommittedRecordings);
            Assert.Empty(RecordingStore.CommittedTrees);
        }

        // ─────────── Snapshot/Restore tests ───────────

        [Fact]
        public void Snapshot_RestoresCommittedRecordingsAndTrees()
        {
            var live = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Live");
            Assert.NotNull(live);
            RecordingStore.AddRecordingWithTreeForTesting(live);
            int liveTreeCount = RecordingStore.CommittedTrees.Count;
            Assert.Equal(1, RecordingStore.CommittedRecordings.Count);

            // Capture the player's pre-test state.
            var snapshot = RecordingStoreTestSnapshot.Capture();
            Assert.Equal(1, snapshot.CommittedRecordingCount);
            Assert.Equal(liveTreeCount, snapshot.CommittedTreeCount);

            // Inject a synthetic recording the way an in-game test would.
            var synthetic = RecordingStore.CreateRecordingFromFlightData(MakePoints(2), "Synthetic");
            Assert.NotNull(synthetic);
            RecordingStore.AddRecordingWithTreeForTesting(synthetic);
            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);

            // Restore: synthetic gone, live recording still there.
            snapshot.Restore();

            Assert.Equal(1, RecordingStore.CommittedRecordings.Count);
            Assert.Equal("Live", RecordingStore.CommittedRecordings[0].VesselName);
            Assert.Equal(liveTreeCount, RecordingStore.CommittedTrees.Count);
        }

        [Fact]
        public void Snapshot_RestoresPendingTreeAndState()
        {
            var pending = new RecordingTree { Id = "p1", TreeName = "Pending" };
            RecordingStore.StashPendingTree(pending);
            var preState = RecordingStore.PendingTreeStateValue;
            Assert.True(RecordingStore.HasPendingTree);

            var snapshot = RecordingStoreTestSnapshot.Capture();

            // Test mutates the pending slot (e.g. discards it as the in-game test
            // might if it were poorly designed, or replaces it with a synthetic one).
            RecordingStore.DiscardPendingTree();
            Assert.False(RecordingStore.HasPendingTree);

            snapshot.Restore();

            Assert.True(RecordingStore.HasPendingTree);
            Assert.Equal("Pending", RecordingStore.PendingTree.TreeName);
            Assert.Equal(preState, RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void Snapshot_GuardStillBlocksDirectReset_AfterRestore()
        {
            // End-to-end: even if the test correctly snapshot/restored its work, a
            // stray call to ResetForTesting() AFTER the restore (still in play mode)
            // must still throw — the live data is back, the guard must protect it.
            var live = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "PostRestoreLive");
            Assert.NotNull(live);
            RecordingStore.AddRecordingWithTreeForTesting(live);

            var snapshot = RecordingStoreTestSnapshot.Capture();
            var synth = RecordingStore.CreateRecordingFromFlightData(MakePoints(2), "Synth");
            Assert.NotNull(synth);
            RecordingStore.AddRecordingWithTreeForTesting(synth);
            snapshot.Restore();

            RecordingStore.ApplicationIsPlayingForTesting = () => true;

            Assert.Throws<InvalidOperationException>(() => RecordingStore.ResetForTesting());
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("PostRestoreLive", RecordingStore.CommittedRecordings[0].VesselName);
        }
    }
}
