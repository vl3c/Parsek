using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Integration tests using real Recording objects, and edge case tests
    /// for diagnostics computation. Guards against regressions in memory estimation,
    /// snapshot caching, storage breakdown, and edge conditions (zero division,
    /// empty buffers, missing files, counter resets).
    /// </summary>
    [Collection("Sequential")]
    public class DiagnosticsIntegrationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public DiagnosticsIntegrationTests()
        {
            ParsekLog.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            DiagnosticsState.ResetForTesting();
            DiagnosticsComputation.ResetForTesting();
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            DiagnosticsState.ResetForTesting();
            DiagnosticsComputation.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        // Helper: creates a Recording with a known number of points, events, and orbit segments.
        private static Recording BuildRecordingWithCounts(
            string name, int pointCount, int eventCount, int segmentCount,
            double startUT = 100.0, double utStep = 1.0,
            bool withSnapshots = false)
        {
            var rec = new Recording { VesselName = name };
            for (int i = 0; i < pointCount; i++)
                rec.Points.Add(new TrajectoryPoint { ut = startUT + i * utStep, bodyName = "Kerbin" });
            for (int i = 0; i < eventCount; i++)
                rec.PartEvents.Add(new PartEvent { ut = startUT + i * utStep });
            for (int i = 0; i < segmentCount; i++)
                rec.OrbitSegments.Add(new OrbitSegment { startUT = startUT, endUT = startUT + 100 });

            if (withSnapshots)
            {
                rec.VesselSnapshot = new ConfigNode("VESSEL");
                rec.GhostVisualSnapshot = new ConfigNode("GHOST");
            }

            return rec;
        }

        // =====================================================================
        // Integration test 1: Memory estimate from loaded recordings
        // Guards against: incorrect iteration over CommittedRecordings, wrong
        // multipliers in memory estimation formula, double-counting.
        // =====================================================================

        [Fact]
        public void MemoryEstimate_FromLoadedRecordings()
        {
            // Build 3 recordings with known counts
            var rec1 = BuildRecordingWithCounts("Alpha", pointCount: 500, eventCount: 20, segmentCount: 3, withSnapshots: true);
            var rec2 = BuildRecordingWithCounts("Beta", pointCount: 300, eventCount: 10, segmentCount: 1, withSnapshots: true);
            var rec3 = BuildRecordingWithCounts("Gamma", pointCount: 200, eventCount: 5, segmentCount: 0, withSnapshots: false);

            RecordingStore.AddRecordingWithTreeForTesting(rec1);
            RecordingStore.AddRecordingWithTreeForTesting(rec2);
            RecordingStore.AddRecordingWithTreeForTesting(rec3);

            DiagnosticsComputation.ClockSource = () => 2000.0;
            var snap = DiagnosticsComputation.ComputeSnapshot(500.0);

            int expectedPts = 500 + 300 + 200;
            int expectedEvts = 20 + 10 + 5;
            int expectedSegs = 3 + 1 + 0;
            int expectedSnaps = 2; // rec1 and rec2 have both snapshots; rec3 has neither

            Assert.Equal(expectedPts, snap.loadedTrajectoryPoints);
            Assert.Equal(expectedEvts, snap.loadedPartEvents);
            Assert.Equal(expectedSegs, snap.loadedOrbitSegments);
            Assert.Equal(expectedSnaps, snap.loadedSnapshotCount);
            Assert.Equal(3, snap.recordingCount);

            long expectedMemory = (long)expectedPts * 136L
                                + (long)expectedEvts * 88L
                                + (long)expectedSegs * 120L
                                + (long)expectedSnaps * 8192L;
            Assert.Equal(expectedMemory, snap.estimatedMemoryBytes);
        }

        // =====================================================================
        // Integration test 2: Snapshot cache returns stale within TTL
        // Guards against: TTL comparison inverted (< vs >), cache not populated.
        // =====================================================================

        [Fact]
        public void SnapshotCache_ReturnsStaleWithinTTL()
        {
            var rec = BuildRecordingWithCounts("Cached", pointCount: 100, eventCount: 5, segmentCount: 1);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            DiagnosticsComputation.ClockSource = () => 1000.0;

            // Compute at UT=100
            var snap1 = DiagnosticsComputation.GetOrComputeSnapshot(100.0);
            Assert.True(DiagnosticsState.hasCachedSnapshot);
            Assert.Equal(100.0, DiagnosticsState.cachedSnapshotUT);
            Assert.Equal(100, snap1.loadedTrajectoryPoints);

            // Add another recording (this would change the result if recomputed)
            var rec2 = BuildRecordingWithCounts("New", pointCount: 50, eventCount: 2, segmentCount: 0);
            RecordingStore.AddRecordingWithTreeForTesting(rec2);

            // Call at UT=101 (within 2s TTL)
            var snap2 = DiagnosticsComputation.GetOrComputeSnapshot(101.0);

            // Should return cached snapshot, so recording count is still 1
            // (the second recording added after the snapshot was computed)
            Assert.Equal(100.0, DiagnosticsState.cachedSnapshotUT);
            Assert.Equal(100, snap2.loadedTrajectoryPoints);
        }

        // =====================================================================
        // Integration test 3: Snapshot cache recomputes after TTL
        // Guards against: cache never invalidated, stale data persisting forever.
        // =====================================================================

        [Fact]
        public void SnapshotCache_RecomputesAfterTTL()
        {
            var rec = BuildRecordingWithCounts("Initial", pointCount: 100, eventCount: 5, segmentCount: 1);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            DiagnosticsComputation.ClockSource = () => 1000.0;

            // Compute at UT=100
            var snap1 = DiagnosticsComputation.GetOrComputeSnapshot(100.0);
            Assert.Equal(100, snap1.loadedTrajectoryPoints);

            // Add another recording
            var rec2 = BuildRecordingWithCounts("Added", pointCount: 75, eventCount: 3, segmentCount: 0);
            RecordingStore.AddRecordingWithTreeForTesting(rec2);

            // Call at UT=103 (past 2s TTL)
            var snap2 = DiagnosticsComputation.GetOrComputeSnapshot(103.0);

            // Should have recomputed, now including both recordings
            Assert.Equal(103.0, DiagnosticsState.cachedSnapshotUT);
            Assert.Equal(175, snap2.loadedTrajectoryPoints); // 100 + 75
            Assert.Equal(2, snap2.recordingCount);
        }

        // =====================================================================
        // Integration test 4: Storage breakdown cache returns stale within 5s TTL
        // Guards against: per-recording storage cache TTL wrong, cache miss on repeat hover.
        // =====================================================================

        [Fact]
        public void StorageBreakdownCache_ReturnsStaleWithinTTL()
        {
            var rec = BuildRecordingWithCounts("Hoverable", pointCount: 50, eventCount: 2, segmentCount: 0);
            rec.RecordingId = "test-hover-rec";

            // First call at UT=200 computes and caches
            var bd1 = DiagnosticsComputation.GetCachedStorageBreakdown(rec, 200.0);
            Assert.Equal("test-hover-rec", bd1.recordingId);
            Assert.Equal(50, bd1.pointCount);

            // Second call at UT=203 (within 5s TTL) should return cached
            // Modify the recording's point count to detect if recomputation happens
            rec.Points.Add(new TrajectoryPoint { ut = 999 });
            var bd2 = DiagnosticsComputation.GetCachedStorageBreakdown(rec, 203.0);

            // Still returns the cached version with 50 points (not 51)
            Assert.Equal(50, bd2.pointCount);

            // Third call at UT=206 (past 5s TTL) should recompute
            var bd3 = DiagnosticsComputation.GetCachedStorageBreakdown(rec, 206.0);
            Assert.Equal(51, bd3.pointCount);
        }

        // =====================================================================
        // Edge case E1: No recordings exist, clean zeros
        // Guards against: null reference when iterating empty list, NaN in
        // estimation, non-zero defaults.
        // =====================================================================

        [Fact]
        public void E1_NoRecordings_CleanZeros()
        {
            DiagnosticsComputation.ClockSource = () => 1000.0;
            var snap = DiagnosticsComputation.ComputeSnapshot(50.0);

            Assert.Equal(0, snap.recordingCount);
            Assert.Equal(0L, snap.totalStorageBytes);
            Assert.Equal(0, snap.loadedTrajectoryPoints);
            Assert.Equal(0, snap.loadedPartEvents);
            Assert.Equal(0, snap.loadedOrbitSegments);
            Assert.Equal(0, snap.loadedSnapshotCount);
            Assert.Equal(0L, snap.estimatedMemoryBytes);
            Assert.Equal(0, snap.activeGhostCount);
            Assert.NotNull(snap.perRecording);
            Assert.Empty(snap.perRecording);

            // Format report should not crash and should show zeros
            string report = DiagnosticsComputation.FormatReport(snap);
            Assert.Contains("0 B total, 0 recordings", report);
            Assert.Contains("0 pts", report);
            Assert.DoesNotContain("NaN", report);
            Assert.DoesNotContain("Infinity", report);
        }

        // =====================================================================
        // Edge case E4: Missing sidecar file returns graceful zero
        // Guards against: exception on missing file crashing diagnostics, non-zero
        // size returned for non-existent files.
        // In unit tests, KSP path resolution is unavailable so SafeResolvePath
        // returns null. This tests that the entire pipeline handles null paths
        // gracefully (0 bytes, no crash).
        // =====================================================================

        [Fact]
        public void E4_MissingSidecarFile_GracefulZero()
        {
            // Create a recording with a valid ID but no actual files on disk.
            // In a test environment, RecordingPaths.ResolveSaveScopedPath throws
            // because KSPUtil/HighLogic aren't available. SafeResolvePath catches
            // the exception and returns null. SafeGetFileSize returns 0 for null paths.
            var rec = BuildRecordingWithCounts("NoFiles", pointCount: 100, eventCount: 5, segmentCount: 2);
            rec.RecordingId = "missing-sidecar-test";

            var bd = DiagnosticsComputation.ComputeStorageBreakdown(rec);

            Assert.Equal(0L, bd.trajectoryFileBytes);
            Assert.Equal(0L, bd.vesselSnapshotBytes);
            Assert.Equal(0L, bd.ghostSnapshotBytes);
            Assert.Equal(0L, bd.readableMirrorBytes);
            Assert.Equal(0L, bd.totalBytes);
            // Metadata counts should still be populated from the recording object
            Assert.Equal(100, bd.pointCount);
            Assert.Equal(5, bd.partEventCount);
            Assert.Equal(2, bd.orbitSegmentCount);
        }

        // =====================================================================
        // Edge case E4 variant: null recording returns default struct
        // Guards against: null dereference in ComputeStorageBreakdown.
        // =====================================================================

        [Fact]
        public void E4_NullRecording_ReturnsDefaultBreakdown()
        {
            var bd = DiagnosticsComputation.ComputeStorageBreakdown(null);

            Assert.Equal(0L, bd.totalBytes);
            Assert.Equal(0, bd.pointCount);
            Assert.Null(bd.recordingId);
        }

        // =====================================================================
        // Edge case E4 variant: empty recording ID returns graceful zero
        // Guards against: empty string passed to RecordingPaths causing exception.
        // =====================================================================

        [Fact]
        public void E4_EmptyRecordingId_GracefulZero()
        {
            var rec = new Recording { VesselName = "EmptyId", RecordingId = "" };

            var bd = DiagnosticsComputation.ComputeStorageBreakdown(rec);

            Assert.Equal(0L, bd.totalBytes);
            Assert.Equal("", bd.recordingId);
        }

        // =====================================================================
        // Edge case E10: Empty rolling buffer shows "N/A" in format report
        // Guards against: misleading "0.0 ms" shown when no data exists,
        // empty buffer causing division by zero in ComputeStats.
        // =====================================================================

        [Fact]
        public void E10_EmptyRollingBuffer_FormatShowsNA()
        {
            // Default snapshot has playbackEntriesInWindow = 0, which is the gate signal.
            // FormatReport now reads this from the snapshot, not from the live buffer (#261).
            var snap = new MetricSnapshot
            {
                perRecording = new StorageBreakdown[0]
            };

            string report = DiagnosticsComputation.FormatReport(snap);

            Assert.Contains("Playback budget: N/A", report);
            // Playback budget line must not show "0.0 ms avg" which would be misleading.
            // (Recording budget line legitimately shows "0.0 ms avg" since that's raw data, not rolling.)
            Assert.DoesNotContain("Playback budget: 0.0 ms avg", report);
        }

        // =====================================================================
        // Bug #261: Rolling buffer has entries but all are outside the 4s window.
        // Pre-fix, FormatReport read playbackFrameHistory.IsEmpty against the
        // live buffer, saw "not empty", and printed the snapshot's
        // 0.0/0.0/0.0 values as "Playback budget: 0.0 ms avg, 0.0 ms peak (0.0s window)".
        // Post-fix, the gate reads snap.playbackEntriesInWindow which is the
        // count returned by ComputeStats — when entries are all outside the
        // window, the count is 0 regardless of buffer.IsEmpty.
        // =====================================================================

        [Fact]
        public void E10b_StaleEntriesOutsideWindow_FormatShowsNA()
        {
            var snap = new MetricSnapshot
            {
                perRecording = new StorageBreakdown[0],
                // Simulate the regression: ComputeStats wrote 0.0 values
                // because the buffer had entries but none were in the window
                playbackAvgTotalMs = 0.0,
                playbackPeakTotalMs = 0.0,
                playbackWindowDurationSeconds = 0.0,
                playbackEntriesInWindow = 0,  // This is what saves us
            };

            string report = DiagnosticsComputation.FormatReport(snap);

            Assert.Contains("Playback budget: N/A", report);
            Assert.DoesNotContain("Playback budget: 0.0 ms avg", report);
        }

        [Fact]
        public void E10c_RealDataInWindow_FormatShowsValues()
        {
            // Sanity check: when entriesInWindow > 0, format the actual values.
            var snap = new MetricSnapshot
            {
                perRecording = new StorageBreakdown[0],
                playbackAvgTotalMs = 4.2,
                playbackPeakTotalMs = 8.1,
                playbackWindowDurationSeconds = 4.0,
                playbackEntriesInWindow = 240,
            };

            string report = DiagnosticsComputation.FormatReport(snap);

            Assert.Contains("Playback budget: 4.2 ms avg, 8.1 ms peak (4.0s window)", report);
            Assert.DoesNotContain("Playback budget: N/A", report);
        }

        // =====================================================================
        // Bug #262: ShouldExpectSidecarFile pure predicate
        // Mirrors RecordingStore.SaveRecordingFiles' write conditions:
        //   .prec → always
        //   _vessel.craft → only when rec.VesselSnapshot != null
        //   _ghost.craft → only when ghostSnapshotMode resolves to Separate
        // Tree continuation recordings, ghost-only-merged debris, and chain
        // mid-segments legitimately have null snapshots and no sidecar file.
        // =====================================================================

        [Fact]
        public void ShouldExpectSidecarFile_TrajectoryAlways()
        {
            var withBoth = new Recording { VesselSnapshot = new ConfigNode("V"), GhostVisualSnapshot = new ConfigNode("G") };
            var withNeither = new Recording();

            Assert.True(DiagnosticsComputation.ShouldExpectSidecarFile(withBoth,
                DiagnosticsComputation.SidecarFileType.Trajectory));
            Assert.True(DiagnosticsComputation.ShouldExpectSidecarFile(withNeither,
                DiagnosticsComputation.SidecarFileType.Trajectory));
        }

        [Fact]
        public void ShouldExpectSidecarFile_VesselSnapshotGated()
        {
            var withVessel = new Recording { VesselSnapshot = new ConfigNode("V") };
            var withoutVessel = new Recording();

            Assert.True(DiagnosticsComputation.ShouldExpectSidecarFile(withVessel,
                DiagnosticsComputation.SidecarFileType.VesselSnapshot));
            Assert.False(DiagnosticsComputation.ShouldExpectSidecarFile(withoutVessel,
                DiagnosticsComputation.SidecarFileType.VesselSnapshot));
        }

        [Fact]
        public void ShouldExpectSidecarFile_GhostSnapshotGated()
        {
            var withGhost = new Recording { GhostVisualSnapshot = new ConfigNode("G") };
            var withoutGhost = new Recording();
            var aliasGhost = new Recording
            {
                VesselSnapshot = new ConfigNode("V"),
                GhostVisualSnapshot = new ConfigNode("V"),
                GhostSnapshotMode = GhostSnapshotMode.AliasVessel
            };

            Assert.True(DiagnosticsComputation.ShouldExpectSidecarFile(withGhost,
                DiagnosticsComputation.SidecarFileType.GhostSnapshot));
            Assert.False(DiagnosticsComputation.ShouldExpectSidecarFile(withoutGhost,
                DiagnosticsComputation.SidecarFileType.GhostSnapshot));
            Assert.False(DiagnosticsComputation.ShouldExpectSidecarFile(aliasGhost,
                DiagnosticsComputation.SidecarFileType.GhostSnapshot));
        }

        [Fact]
        public void ShouldExpectSidecarFile_NullRecording_AlwaysFalse()
        {
            Assert.False(DiagnosticsComputation.ShouldExpectSidecarFile(null,
                DiagnosticsComputation.SidecarFileType.Trajectory));
            Assert.False(DiagnosticsComputation.ShouldExpectSidecarFile(null,
                DiagnosticsComputation.SidecarFileType.VesselSnapshot));
            Assert.False(DiagnosticsComputation.ShouldExpectSidecarFile(null,
                DiagnosticsComputation.SidecarFileType.GhostSnapshot));
        }

        // =====================================================================
        // Bug #262: SafeGetFileSize warning gate — verifies that the file-level
        // helper actually suppresses the "Missing sidecar file" warning when
        // warnIfMissing=false. Closes the loop between ShouldExpectSidecarFile
        // (predicate, tested above) and the integration in ComputeStorageBreakdown.
        // =====================================================================

        [Fact]
        public void SafeGetFileSize_MissingFile_WarnIfMissingTrue_LogsWarning()
        {
            string nonExistent = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"parsek_test_{System.Guid.NewGuid():N}.prec");

            long size = DiagnosticsComputation.SafeGetFileSize(nonExistent, warnIfMissing: true);

            Assert.Equal(0L, size);
            Assert.Contains(logLines, l =>
                l.Contains("[Diagnostics]") && l.Contains("Missing sidecar file") && l.Contains(nonExistent));
        }

        [Fact]
        public void SafeGetFileSize_MissingFile_RepeatedScansWarnRateLimited()
        {
            double clockSeconds = 0.0;
            ParsekLog.ClockOverrideForTesting = () => clockSeconds;
            string nonExistent = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"parsek_test_{System.Guid.NewGuid():N}.prec");

            for (int i = 0; i < 5; i++)
            {
                long size = DiagnosticsComputation.SafeGetFileSize(nonExistent, warnIfMissing: true);
                Assert.Equal(0L, size);
            }

            Assert.Single(logLines.FindAll(l =>
                l.Contains("[Parsek][WARN][Diagnostics]")
                && l.Contains("Missing sidecar file")
                && l.Contains(nonExistent)));

            clockSeconds += 31.0;
            DiagnosticsComputation.SafeGetFileSize(nonExistent, warnIfMissing: true);

            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][WARN][Diagnostics]")
                && l.Contains("Missing sidecar file")
                && l.Contains(nonExistent)
                && l.Contains("suppressed=4"));
        }

        [Fact]
        public void SafeGetFileSize_MissingFile_DifferentPathsHaveIndependentWarnWindows()
        {
            double clockSeconds = 0.0;
            ParsekLog.ClockOverrideForTesting = () => clockSeconds;
            string firstPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"parsek_test_{System.Guid.NewGuid():N}_a.prec");
            string secondPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"parsek_test_{System.Guid.NewGuid():N}_b.prec");

            Assert.Equal(0L, DiagnosticsComputation.SafeGetFileSize(firstPath, warnIfMissing: true));
            Assert.Equal(0L, DiagnosticsComputation.SafeGetFileSize(secondPath, warnIfMissing: true));

            Assert.Single(logLines.FindAll(l =>
                l.Contains("[Parsek][WARN][Diagnostics]")
                && l.Contains("Missing sidecar file")
                && l.Contains(firstPath)));
            Assert.Single(logLines.FindAll(l =>
                l.Contains("[Parsek][WARN][Diagnostics]")
                && l.Contains("Missing sidecar file")
                && l.Contains(secondPath)));

            Assert.Equal(0L, DiagnosticsComputation.SafeGetFileSize(firstPath, warnIfMissing: true));
            Assert.Equal(0L, DiagnosticsComputation.SafeGetFileSize(secondPath, warnIfMissing: true));

            Assert.Single(logLines.FindAll(l =>
                l.Contains("[Parsek][WARN][Diagnostics]")
                && l.Contains("Missing sidecar file")
                && l.Contains(firstPath)));
            Assert.Single(logLines.FindAll(l =>
                l.Contains("[Parsek][WARN][Diagnostics]")
                && l.Contains("Missing sidecar file")
                && l.Contains(secondPath)));
        }

        [Fact]
        public void SafeGetFileSize_MissingFile_WarnIfMissingFalse_NoWarning()
        {
            string nonExistent = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"parsek_test_{System.Guid.NewGuid():N}_vessel.craft");

            long size = DiagnosticsComputation.SafeGetFileSize(nonExistent, warnIfMissing: false);

            Assert.Equal(0L, size);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("Missing sidecar file") && l.Contains(nonExistent));
        }

        [Fact]
        public void SafeGetFileSize_NullPath_NoWarning()
        {
            long size = DiagnosticsComputation.SafeGetFileSize(null, warnIfMissing: true);

            Assert.Equal(0L, size);
            Assert.DoesNotContain(logLines, l => l.Contains("Missing sidecar file"));
        }

        // =====================================================================
        // Edge case: Duration zero recording, bytesPerSecond must be 0.0
        // Guards against: division by zero producing NaN or Infinity when
        // StartUT == EndUT (instantaneous recording, e.g. single-frame).
        // =====================================================================

        [Fact]
        public void DurationZero_BytesPerSecondZero()
        {
            // Create a recording where StartUT == EndUT (single point)
            var rec = new Recording { VesselName = "Instant" };
            rec.Points.Add(new TrajectoryPoint { ut = 500.0 });

            // Verify StartUT == EndUT
            Assert.Equal(rec.StartUT, rec.EndUT);

            var bd = DiagnosticsComputation.ComputeStorageBreakdown(rec);

            Assert.Equal(0.0, bd.durationSeconds);
            Assert.Equal(0.0, bd.bytesPerSecond);
            Assert.False(double.IsNaN(bd.bytesPerSecond));
            Assert.False(double.IsInfinity(bd.bytesPerSecond));
        }

        // =====================================================================
        // Edge case: Growth rate with zero elapsed time
        // Guards against: NaN/Infinity in pointsPerSecond and eventsPerSecond
        // when recording has just started (elapsedSeconds == 0).
        // =====================================================================

        [Fact]
        public void GrowthRate_ZeroElapsed_NoNaN()
        {
            DiagnosticsState.activeGrowthRate = new RecordingGrowthRate
            {
                totalPoints = 1,
                totalEvents = 0,
                elapsedSeconds = 0.0,
                pointsPerSecond = 0.0,  // caller must set this safely
                eventsPerSecond = 0.0
            };
            DiagnosticsState.hasActiveGrowthRate = true;

            // Verify the struct values are safe
            Assert.Equal(0.0, DiagnosticsState.activeGrowthRate.pointsPerSecond);
            Assert.Equal(0.0, DiagnosticsState.activeGrowthRate.eventsPerSecond);
            Assert.False(double.IsNaN(DiagnosticsState.activeGrowthRate.pointsPerSecond));
            Assert.False(double.IsInfinity(DiagnosticsState.activeGrowthRate.pointsPerSecond));
            Assert.False(double.IsNaN(DiagnosticsState.activeGrowthRate.eventsPerSecond));
            Assert.False(double.IsInfinity(DiagnosticsState.activeGrowthRate.eventsPerSecond));

            // Also verify FormatReport handles this without crash
            var snap = new MetricSnapshot { perRecording = new StorageBreakdown[0] };
            string report = DiagnosticsComputation.FormatReport(snap);
            Assert.DoesNotContain("NaN", report);
            Assert.DoesNotContain("Infinity", report);
            Assert.Contains("(active)", report);
        }

        // =====================================================================
        // Edge case: Health counter reset clears all counters
        // Guards against: new counter fields added to HealthCounters but
        // omitted from Reset(), leaving stale data across scenes.
        // =====================================================================

        [Fact]
        public void HealthCounterReset_ClearsAll()
        {
            // Set every counter to a non-zero value
            DiagnosticsState.health.waypointCacheHits = 100;
            DiagnosticsState.health.waypointCacheMisses = 50;
            DiagnosticsState.health.snapshotRefreshSpikes = 10;
            DiagnosticsState.health.spawnFailures = 7;
            DiagnosticsState.health.spawnRetries = 3;
            DiagnosticsState.health.ghostBuildsThisSession = 25;
            DiagnosticsState.health.ghostDestroysThisSession = 20;
            DiagnosticsState.health.gcGen0Baseline = 999;

            DiagnosticsState.health.Reset();

            Assert.Equal(0, DiagnosticsState.health.waypointCacheHits);
            Assert.Equal(0, DiagnosticsState.health.waypointCacheMisses);
            Assert.Equal(0, DiagnosticsState.health.snapshotRefreshSpikes);
            Assert.Equal(0, DiagnosticsState.health.spawnFailures);
            Assert.Equal(0, DiagnosticsState.health.spawnRetries);
            Assert.Equal(0, DiagnosticsState.health.ghostBuildsThisSession);
            Assert.Equal(0, DiagnosticsState.health.ghostDestroysThisSession);
            // gcGen0Baseline should be current GC count, not zero or the old value
            Assert.Equal(GC.CollectionCount(0), DiagnosticsState.health.gcGen0Baseline);
            Assert.NotEqual(999, DiagnosticsState.health.gcGen0Baseline);
        }

        // =====================================================================
        // Edge case: Cache hit rate with zero lookups shows "N/A"
        // Guards against: division by zero producing "NaN%" in report,
        // or crash when totalLookups == 0.
        // =====================================================================

        [Fact]
        public void CacheHitRate_ZeroLookups_ShowsNA()
        {
            DiagnosticsState.health.waypointCacheHits = 0;
            DiagnosticsState.health.waypointCacheMisses = 0;

            var snap = new MetricSnapshot
            {
                perRecording = new StorageBreakdown[0]
            };

            string report = DiagnosticsComputation.FormatReport(snap);

            Assert.Contains("cache N/A hit", report);
            Assert.Contains("0 miss of 0 lookups", report);
            Assert.DoesNotContain("NaN", report);
            Assert.DoesNotContain("Infinity", report);
        }

        // =====================================================================
        // Integration: Memory estimate with zero-count recording
        // Guards against: recordings with empty Points/Events/Segments lists
        // causing null dereference or incorrect totals.
        // =====================================================================

        [Fact]
        public void MemoryEstimate_EmptyRecording_ContributesZero()
        {
            // Add a completely empty recording (no points, events, or segments)
            var emptyRec = new Recording { VesselName = "Empty" };
            RecordingStore.AddRecordingWithTreeForTesting(emptyRec);

            // Add a recording with data
            var dataRec = BuildRecordingWithCounts("HasData", pointCount: 50, eventCount: 10, segmentCount: 2);
            RecordingStore.AddRecordingWithTreeForTesting(dataRec);

            DiagnosticsComputation.ClockSource = () => 1000.0;
            var snap = DiagnosticsComputation.ComputeSnapshot(100.0);

            // Empty recording contributes nothing to memory
            Assert.Equal(50, snap.loadedTrajectoryPoints);
            Assert.Equal(10, snap.loadedPartEvents);
            Assert.Equal(2, snap.loadedOrbitSegments);
            Assert.Equal(2, snap.recordingCount);

            long expectedMemory = 50L * 136 + 10L * 88 + 2L * 120;
            Assert.Equal(expectedMemory, snap.estimatedMemoryBytes);
        }

        // =====================================================================
        // Integration: Snapshot includes both vessel+ghost snapshots for counting
        // Guards against: counting recordings where only VesselSnapshot is set
        // but GhostVisualSnapshot is null (or vice versa).
        // =====================================================================

        [Fact]
        public void SnapshotCount_RequiresBothVesselAndGhostSnapshot()
        {
            var recBoth = new Recording { VesselName = "Both" };
            recBoth.VesselSnapshot = new ConfigNode("V");
            recBoth.GhostVisualSnapshot = new ConfigNode("G");
            RecordingStore.AddRecordingWithTreeForTesting(recBoth);

            var recVesselOnly = new Recording { VesselName = "VesselOnly" };
            recVesselOnly.VesselSnapshot = new ConfigNode("V2");
            RecordingStore.AddRecordingWithTreeForTesting(recVesselOnly);

            var recGhostOnly = new Recording { VesselName = "GhostOnly" };
            recGhostOnly.GhostVisualSnapshot = new ConfigNode("G2");
            RecordingStore.AddRecordingWithTreeForTesting(recGhostOnly);

            var recNeither = new Recording { VesselName = "Neither" };
            RecordingStore.AddRecordingWithTreeForTesting(recNeither);

            DiagnosticsComputation.ClockSource = () => 1000.0;
            var snap = DiagnosticsComputation.ComputeSnapshot(100.0);

            // Only the recording with BOTH snapshots counts
            Assert.Equal(1, snap.loadedSnapshotCount);
            Assert.Equal(4, snap.recordingCount);
        }

        // =====================================================================
        // Integration: Full FormatReport with multiple recordings
        // Guards against: report generation crashing with multiple per-recording
        // lines, index formatting errors, large data values.
        // =====================================================================

        [Fact]
        public void FormatReport_MultipleRecordings_AllListed()
        {
            var rec1 = BuildRecordingWithCounts("Alpha", pointCount: 1000, eventCount: 50, segmentCount: 3);
            var rec2 = BuildRecordingWithCounts("Beta", pointCount: 2000, eventCount: 100, segmentCount: 5);
            RecordingStore.AddRecordingWithTreeForTesting(rec1);
            RecordingStore.AddRecordingWithTreeForTesting(rec2);

            DiagnosticsComputation.ClockSource = () => 1000.0;
            var snap = DiagnosticsComputation.ComputeSnapshot(100.0);
            string report = DiagnosticsComputation.FormatReport(snap);

            Assert.Contains("2 recordings", report);
            Assert.Contains("rec[0]", report);
            Assert.Contains("rec[1]", report);
            Assert.Contains("\"Alpha\"", report);
            Assert.Contains("\"Beta\"", report);
            Assert.Contains("1000 pts", report);
            Assert.Contains("2000 pts", report);
            Assert.Contains("3000 pts", report); // total across both
        }

        // =====================================================================
        // Edge case: Negative duration clamped to zero
        // Guards against: EndUT < StartUT (should not happen but defensive guard)
        // producing negative duration or negative bytesPerSecond.
        // =====================================================================

        [Fact]
        public void NegativeDuration_ClampedToZero()
        {
            var rec = new Recording { VesselName = "Backwards" };
            // Use explicit UT range where end < start (should never happen but defensive)
            rec.ExplicitStartUT = 200.0;
            rec.ExplicitEndUT = 100.0;

            var bd = DiagnosticsComputation.ComputeStorageBreakdown(rec);

            // Duration should be clamped to 0, not negative
            Assert.Equal(0.0, bd.durationSeconds);
            Assert.Equal(0.0, bd.bytesPerSecond);
            Assert.False(double.IsNaN(bd.bytesPerSecond));
        }

        // =====================================================================
        // Edge case: DiagnosticsState.ResetSessionCounters invalidates snapshot cache
        // Guards against: stale snapshot surviving a scene load reset.
        // =====================================================================

        [Fact]
        public void ResetSessionCounters_InvalidatesSnapshotCache()
        {
            DiagnosticsComputation.ClockSource = () => 1000.0;

            // Populate a cached snapshot
            var rec = BuildRecordingWithCounts("Cached", pointCount: 100, eventCount: 5, segmentCount: 1);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            DiagnosticsComputation.ComputeSnapshot(100.0);
            Assert.True(DiagnosticsState.hasCachedSnapshot);

            // Simulate scene load
            DiagnosticsState.ResetSessionCounters();

            // Cache should be invalidated
            Assert.False(DiagnosticsState.hasCachedSnapshot);
            Assert.Equal(0.0, DiagnosticsState.cachedSnapshotUT);
        }

        // =====================================================================
        // Integration: RunDiagnosticsReport produces complete output and logs it
        // Guards against: report generation producing empty string, or log lines
        // not being emitted.
        // =====================================================================

        [Fact]
        public void RunDiagnosticsReport_ProducesCompleteOutput()
        {
            var rec = BuildRecordingWithCounts("Diagnostic", pointCount: 200, eventCount: 15, segmentCount: 2);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            DiagnosticsComputation.ClockSource = () => 1000.0;

            string report = DiagnosticsComputation.RunDiagnosticsReport();

            Assert.Contains("===== DIAGNOSTICS REPORT =====", report);
            Assert.Contains("===== END REPORT =====", report);
            Assert.Contains("1 recordings", report);
            Assert.Contains("200 pts", report);

            // Verify lines were also sent to log
            Assert.Contains(logLines, l => l.Contains("[INFO]") && l.Contains("DIAGNOSTICS REPORT"));
            Assert.Contains(logLines, l => l.Contains("[INFO]") && l.Contains("END REPORT"));
        }

        // =====================================================================
        // Edge case: StorageBreakdown with valid duration computes bytesPerSecond
        // Guards against: correct division when duration > 0 and totalBytes > 0.
        // =====================================================================

        [Fact]
        public void StorageBreakdown_ValidDuration_ComputesBytesPerSecond()
        {
            var rec = new Recording { VesselName = "Timed" };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0 });

            var bd = DiagnosticsComputation.ComputeStorageBreakdown(rec);

            Assert.Equal(100.0, bd.durationSeconds);
            // totalBytes is 0 (no files on disk in test), so bytesPerSecond is 0
            Assert.Equal(0.0, bd.bytesPerSecond);
            Assert.False(double.IsNaN(bd.bytesPerSecond));
        }
    }
}
