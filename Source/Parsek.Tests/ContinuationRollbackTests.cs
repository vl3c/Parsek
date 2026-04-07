using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #95 continuation rollback: boundary index tracking,
    /// snapshot backup/restore, and bake-in behavior.
    /// </summary>
    [Collection("Sequential")]
    public class ContinuationRollbackTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ContinuationRollbackTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        private static Recording MakeRecordingWithPoints(int count, double startUT = 100.0)
        {
            var rec = new Recording
            {
                VesselName = "TestVessel",
                RecordingId = "test-" + count
            };
            for (int i = 0; i < count; i++)
            {
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = startUT + i,
                    latitude = i * 0.1,
                    longitude = i * 0.2,
                    altitude = 100 + i
                });
            }
            return rec;
        }

        private static ConfigNode MakeSnapshot(string label)
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("name", label);
            return node;
        }

        [Fact]
        public void RollbackTruncatesContinuationPoints()
        {
            var rec = MakeRecordingWithPoints(10);
            rec.ContinuationBoundaryIndex = 10;

            // Simulate continuation adding 5 points
            for (int i = 0; i < 5; i++)
                rec.Points.Add(new TrajectoryPoint { ut = 110 + i });

            Assert.Equal(15, rec.Points.Count);

            RecordingStore.RollbackContinuationData(rec);

            Assert.Equal(10, rec.Points.Count);
            Assert.Equal(-1, rec.ContinuationBoundaryIndex);
        }

        [Fact]
        public void RollbackRestoresSnapshots()
        {
            var rec = MakeRecordingWithPoints(5);
            var originalVessel = MakeSnapshot("original-vessel");
            var originalGhost = MakeSnapshot("original-ghost");
            var continuationVessel = MakeSnapshot("continuation-vessel");
            var continuationGhost = MakeSnapshot("continuation-ghost");

            rec.VesselSnapshot = originalVessel;
            rec.GhostVisualSnapshot = originalGhost;
            rec.ContinuationBoundaryIndex = 5;
            rec.PreContinuationVesselSnapshot = originalVessel;
            rec.PreContinuationGhostSnapshot = originalGhost;

            // Simulate continuation overwriting snapshots
            rec.VesselSnapshot = continuationVessel;
            rec.GhostVisualSnapshot = continuationGhost;

            RecordingStore.RollbackContinuationData(rec);

            Assert.Same(originalVessel, rec.VesselSnapshot);
            Assert.Same(originalGhost, rec.GhostVisualSnapshot);
            Assert.Null(rec.PreContinuationVesselSnapshot);
            Assert.Null(rec.PreContinuationGhostSnapshot);
        }

        [Fact]
        public void NoRollbackWhenBoundaryNotSet()
        {
            var rec = MakeRecordingWithPoints(10);
            Assert.Equal(-1, rec.ContinuationBoundaryIndex);

            RecordingStore.RollbackContinuationData(rec);

            Assert.Equal(10, rec.Points.Count);
            Assert.Equal(-1, rec.ContinuationBoundaryIndex);
        }

        [Fact]
        public void BoundaryAtZeroClearsAllPoints()
        {
            var rec = MakeRecordingWithPoints(8);
            rec.ContinuationBoundaryIndex = 0;

            RecordingStore.RollbackContinuationData(rec);

            Assert.Empty(rec.Points);
            Assert.Equal(-1, rec.ContinuationBoundaryIndex);
        }

        [Fact]
        public void FilesDirtySetAfterRollback()
        {
            var rec = MakeRecordingWithPoints(5);
            rec.ContinuationBoundaryIndex = 5;
            rec.Points.Add(new TrajectoryPoint { ut = 200 });
            rec.FilesDirty = false;

            RecordingStore.RollbackContinuationData(rec);

            Assert.True(rec.FilesDirty);
        }

        [Fact]
        public void FilesDirtyNotSetWhenNothingToRollback()
        {
            var rec = MakeRecordingWithPoints(5);
            rec.FilesDirty = false;
            // No boundary set

            RecordingStore.RollbackContinuationData(rec);

            Assert.False(rec.FilesDirty);
        }

        [Fact]
        public void EndUTReflectsRollback()
        {
            var rec = MakeRecordingWithPoints(10, startUT: 100.0);
            // Last base point is at UT 109
            rec.ContinuationBoundaryIndex = 10;

            // Add continuation points extending to UT 115
            for (int i = 0; i < 6; i++)
                rec.Points.Add(new TrajectoryPoint { ut = 110 + i });

            Assert.Equal(115.0, rec.EndUT);

            RecordingStore.RollbackContinuationData(rec);

            Assert.Equal(109.0, rec.EndUT);
        }

        [Fact]
        public void BakeInClearsBoundary()
        {
            var rec = MakeRecordingWithPoints(10);
            rec.ContinuationBoundaryIndex = 10;
            rec.PreContinuationVesselSnapshot = MakeSnapshot("backup");
            rec.PreContinuationGhostSnapshot = MakeSnapshot("backup-ghost");

            // Add continuation points
            for (int i = 0; i < 3; i++)
                rec.Points.Add(new TrajectoryPoint { ut = 110 + i });

            ChainSegmentManager.BakeContinuationData(rec);

            Assert.Equal(-1, rec.ContinuationBoundaryIndex);
            Assert.Null(rec.PreContinuationVesselSnapshot);
            Assert.Null(rec.PreContinuationGhostSnapshot);

            // After bake, rollback should not truncate
            RecordingStore.RollbackContinuationData(rec);
            Assert.Equal(13, rec.Points.Count);
        }

        [Fact]
        public void RollbackLogsMessage()
        {
            var rec = MakeRecordingWithPoints(5);
            rec.ContinuationBoundaryIndex = 5;
            rec.Points.Add(new TrajectoryPoint { ut = 200 });
            rec.Points.Add(new TrajectoryPoint { ut = 201 });

            RecordingStore.RollbackContinuationData(rec);

            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("Rolled back 2 continuation point(s)") &&
                l.Contains("boundary=5"));
        }

        [Fact]
        public void RollbackWithBoundaryEqualToPointCount()
        {
            // Boundary set but no continuation points added yet
            var rec = MakeRecordingWithPoints(10);
            rec.ContinuationBoundaryIndex = 10;
            rec.PreContinuationVesselSnapshot = MakeSnapshot("backup");

            RecordingStore.RollbackContinuationData(rec);

            // No truncation (boundary == Points.Count, condition is boundary < Points.Count)
            Assert.Equal(10, rec.Points.Count);
            Assert.Equal(-1, rec.ContinuationBoundaryIndex);
            Assert.Null(rec.PreContinuationVesselSnapshot);
        }
    }
}
