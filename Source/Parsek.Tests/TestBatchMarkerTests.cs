using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure xUnit coverage for <see cref="TestBatchMarker"/>: the
    /// ShouldReconcileOnLoad crash-reconcile decision and the SaveInto/LoadFrom
    /// ConfigNode round-trip.
    /// </summary>
    public class TestBatchMarkerTests
    {
        // ----- ShouldReconcileOnLoad -----

        [Fact]
        public void Reconcile_NullMarker_NoMarker()
        {
            string reason;
            bool result = TestBatchMarker.ShouldReconcileOnLoad(
                null, "proc-current", "save", out reason);
            Assert.False(result);
            Assert.Equal("no-marker", reason);
        }

        [Fact]
        public void Reconcile_NoBackupPath_False()
        {
            var marker = new TestBatchMarker
            {
                ProcessSessionId = "proc-old",
                SaveFolder = "save",
                PersistentBackupPath = null,
            };
            string reason;
            bool result = TestBatchMarker.ShouldReconcileOnLoad(
                marker, "proc-current", "save", out reason);
            Assert.False(result);
            Assert.Equal("no-backup-path", reason);
        }

        [Fact]
        public void Reconcile_SaveFolderMismatch_False()
        {
            var marker = new TestBatchMarker
            {
                ProcessSessionId = "proc-old",
                SaveFolder = "save-A",
                PersistentBackupPath = "/x/y-persistent.bak",
            };
            string reason;
            bool result = TestBatchMarker.ShouldReconcileOnLoad(
                marker, "proc-current", "save-B", out reason);
            Assert.False(result);
            Assert.Equal("save-folder-mismatch", reason);
        }

        [Fact]
        public void Reconcile_SameProcess_NoCrash_False()
        {
            var marker = new TestBatchMarker
            {
                ProcessSessionId = "proc-current",
                SaveFolder = "save",
                PersistentBackupPath = "/x/y-persistent.bak",
            };
            string reason;
            bool result = TestBatchMarker.ShouldReconcileOnLoad(
                marker, "proc-current", "save", out reason);
            Assert.False(result);
            Assert.Equal("same-process-no-crash", reason);
        }

        [Fact]
        public void Reconcile_DifferentProcess_SaveMatch_BackupPresent_True()
        {
            var marker = new TestBatchMarker
            {
                ProcessSessionId = "proc-old",
                SaveFolder = "save",
                PersistentBackupPath = "/x/y-persistent.bak",
            };
            string reason;
            bool result = TestBatchMarker.ShouldReconcileOnLoad(
                marker, "proc-current", "save", out reason);
            Assert.True(result);
            Assert.Equal("interrupted-batch-crash-recovery", reason);
        }

        // ----- SaveInto / LoadFrom round-trip -----

        [Fact]
        public void RoundTrip_AllFieldsSurvive()
        {
            var marker = new TestBatchMarker
            {
                ProcessSessionId = "proc-1",
                BatchInstanceId = "batch-1",
                PersistentBackupPath = "/saves/x-persistent.bak",
                ParsekSnapshotDir = "/saves/MyCareer/x-parsek",
                SaveFolder = "MyCareer",
                CapturedScene = "FLIGHT",
                StartedRealTime = "2026-06-09T12:00:00.0000000Z",
            };
            var parent = new ConfigNode("PARENT");
            marker.SaveInto(parent);

            var node = parent.GetNode(TestBatchMarker.NodeName);
            Assert.NotNull(node);

            var loaded = TestBatchMarker.LoadFrom(node);
            Assert.Equal("proc-1", loaded.ProcessSessionId);
            Assert.Equal("batch-1", loaded.BatchInstanceId);
            Assert.Equal("/saves/x-persistent.bak", loaded.PersistentBackupPath);
            Assert.Equal("/saves/MyCareer/x-parsek", loaded.ParsekSnapshotDir);
            Assert.Equal("MyCareer", loaded.SaveFolder);
            Assert.Equal("FLIGHT", loaded.CapturedScene);
            Assert.Equal("2026-06-09T12:00:00.0000000Z", loaded.StartedRealTime);
        }

        [Fact]
        public void RoundTrip_AbsentFields_DefaultToNull()
        {
            var marker = new TestBatchMarker
            {
                ProcessSessionId = null,
                BatchInstanceId = null,
                PersistentBackupPath = null,
                ParsekSnapshotDir = null,
                SaveFolder = null,
                CapturedScene = null,
                StartedRealTime = null,
            };
            var parent = new ConfigNode("PARENT");
            marker.SaveInto(parent);

            var loaded = TestBatchMarker.LoadFrom(parent.GetNode(TestBatchMarker.NodeName));
            Assert.Null(loaded.ProcessSessionId);
            Assert.Null(loaded.BatchInstanceId);
            Assert.Null(loaded.PersistentBackupPath);
            // DiskOnly-mode markers carry no snapshot dir; it round-trips to null.
            Assert.Null(loaded.ParsekSnapshotDir);
            Assert.Null(loaded.SaveFolder);
            // StartedRealTime is only written when non-empty, so it round-trips to null.
            Assert.Null(loaded.StartedRealTime);
        }

        [Fact]
        public void RoundTrip_ParsekSnapshotDir_PresentSurvives()
        {
            var marker = new TestBatchMarker
            {
                ProcessSessionId = "proc-1",
                PersistentBackupPath = "/saves/x-persistent.bak",
                ParsekSnapshotDir = "/saves/MyCareer/x-parsek",
                SaveFolder = "MyCareer",
            };
            var parent = new ConfigNode("PARENT");
            marker.SaveInto(parent);

            var loaded = TestBatchMarker.LoadFrom(parent.GetNode(TestBatchMarker.NodeName));
            Assert.Equal("/saves/MyCareer/x-parsek", loaded.ParsekSnapshotDir);
        }

        [Fact]
        public void RoundTrip_ParsekSnapshotDir_DiskOnlyAbsent_RoundTripsNull()
        {
            // DiskOnly mode takes no Parsek sidecar snapshot, so the field is null;
            // it must round-trip back to null (not empty string) so the crash
            // finisher's null-check treats it as "no sidecar restore".
            var marker = new TestBatchMarker
            {
                ProcessSessionId = "proc-1",
                PersistentBackupPath = "/saves/x-persistent.bak",
                ParsekSnapshotDir = null,
                SaveFolder = "MyCareer",
            };
            var parent = new ConfigNode("PARENT");
            marker.SaveInto(parent);

            var loaded = TestBatchMarker.LoadFrom(parent.GetNode(TestBatchMarker.NodeName));
            Assert.Null(loaded.ParsekSnapshotDir);
        }

        [Fact]
        public void NodeName_IsStable()
        {
            Assert.Equal("PARSEK_TEST_BATCH_MARKER", TestBatchMarker.NodeName);
        }
    }
}
