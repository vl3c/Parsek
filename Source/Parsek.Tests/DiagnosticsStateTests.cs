using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    #region WarnRateLimited

    [Collection("Sequential")]
    public class WarnRateLimitedTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public WarnRateLimitedTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void EmitsAtWarnLevel_EvenWhenVerboseDisabled()
        {
            ParsekLog.VerboseOverrideForTesting = false;

            ParsekLog.WarnRateLimited("Budget", "over", "exceeded threshold", 5.0);

            Assert.Single(logLines);
            Assert.Contains("[WARN]", logLines[0]);
            Assert.Contains("[Budget]", logLines[0]);
            Assert.Contains("exceeded threshold", logLines[0]);
        }

        [Fact]
        public void RateLimits_SecondCallSuppressed()
        {
            double now = 1000.0;
            ParsekLog.ClockOverrideForTesting = () => now;

            ParsekLog.WarnRateLimited("Budget", "cap", "over budget", 5.0);
            now = 1001.0;
            ParsekLog.WarnRateLimited("Budget", "cap", "over budget", 5.0);

            Assert.Single(logLines);
        }

        [Fact]
        public void EmitsAfterIntervalPasses()
        {
            double now = 1000.0;
            ParsekLog.ClockOverrideForTesting = () => now;

            ParsekLog.WarnRateLimited("Budget", "cap", "over budget", 2.0);

            // Two suppressed calls
            now = 1000.5;
            ParsekLog.WarnRateLimited("Budget", "cap", "over budget", 2.0);
            now = 1001.0;
            ParsekLog.WarnRateLimited("Budget", "cap", "over budget", 2.0);

            // Past the interval
            now = 1002.1;
            ParsekLog.WarnRateLimited("Budget", "cap", "over budget", 2.0);

            Assert.Equal(2, logLines.Count);
            Assert.Contains("[WARN]", logLines[0]);
            Assert.DoesNotContain("suppressed", logLines[0]);
            Assert.Contains("[WARN]", logLines[1]);
            Assert.Contains("suppressed=2", logLines[1]);
        }

        [Fact]
        public void DifferentKeys_IndependentRateLimiting()
        {
            double now = 1000.0;
            ParsekLog.ClockOverrideForTesting = () => now;

            ParsekLog.WarnRateLimited("Budget", "a", "warning A", 5.0);
            ParsekLog.WarnRateLimited("Budget", "b", "warning B", 5.0);

            Assert.Equal(2, logLines.Count);
            Assert.Contains("warning A", logLines[0]);
            Assert.Contains("warning B", logLines[1]);
        }
    }

    #endregion

    #region RollingTimingBuffer

    public class RollingTimingBufferTests
    {
        [Fact]
        public void Append_SingleEntry_CountIsOne()
        {
            var buf = new RollingTimingBuffer();

            buf.Append(1.0, 500);

            Assert.Equal(1, buf.Count);
            Assert.False(buf.IsEmpty);
        }

        [Fact]
        public void ComputeStats_KnownSequence()
        {
            var buf = new RollingTimingBuffer();
            // Append [1000, 2000, 3000, 1500, 2500] microseconds at timestamps 0-4
            buf.Append(0.0, 1000);
            buf.Append(1.0, 2000);
            buf.Append(2.0, 3000);
            buf.Append(3.0, 1500);
            buf.Append(4.0, 2500);

            buf.ComputeStats(4.0, 10.0, out double avgMs, out double peakMs, out double window, out int entries);

            // avg = (1000+2000+3000+1500+2500)/5 = 10000/5 = 2000 microseconds = 2.0 ms
            Assert.Equal(2.0, avgMs, 6);
            // peak = 3000 microseconds = 3.0 ms
            Assert.Equal(3.0, peakMs, 6);
            // window = 4.0 - 0.0 = 4.0
            Assert.Equal(4.0, window, 6);
            Assert.Equal(5, entries);
        }

        [Fact]
        public void ComputeStats_WindowEviction()
        {
            var buf = new RollingTimingBuffer();
            // Append entries at t=0,1,2,3,4,5,6 with 100us each
            for (int i = 0; i <= 6; i++)
                buf.Append(i, 100);

            // Compute with window=3s at t=6 => windowStart=3.0
            // Only entries at t=3,4,5,6 should be included (4 entries)
            buf.ComputeStats(6.0, 3.0, out double avgMs, out double peakMs, out double window, out int entries);

            // avg = 100us = 0.1ms for all entries
            Assert.Equal(0.1, avgMs, 6);
            Assert.Equal(0.1, peakMs, 6);
            // window = 6.0 - 3.0 = 3.0
            Assert.Equal(3.0, window, 6);
            Assert.Equal(4, entries);
        }

        [Fact]
        public void ComputeStats_EmptyBuffer()
        {
            var buf = new RollingTimingBuffer();

            buf.ComputeStats(5.0, 10.0, out double avgMs, out double peakMs, out double window, out int entries);

            Assert.Equal(0.0, avgMs);
            Assert.Equal(0.0, peakMs);
            Assert.Equal(0.0, window);
            Assert.Equal(0, entries);
        }

        [Fact]
        public void ComputeStats_AllEntriesOutsideWindow_ReportsZeroEntries()
        {
            // Bug #261: buffer has entries but they're all older than the window.
            // Pre-fix, this returned avgMs=0/peakMs=0/window=0 with no way to
            // distinguish "no data" from "data is genuinely 0.0 ms".
            var buf = new RollingTimingBuffer();
            buf.Append(0.0, 5000);
            buf.Append(1.0, 7000);
            buf.Append(2.0, 6000);

            // Compute at t=100 with a 4s window — every entry is far older than windowStart=96
            buf.ComputeStats(100.0, 4.0, out double avgMs, out double peakMs, out double window, out int entries);

            Assert.Equal(0.0, avgMs);
            Assert.Equal(0.0, peakMs);
            Assert.Equal(0.0, window);
            Assert.Equal(0, entries);
            // Sanity: buffer was never empty
            Assert.False(buf.IsEmpty);
            Assert.Equal(3, buf.Count);
        }

        [Fact]
        public void Reset_ClearsBuffer()
        {
            var buf = new RollingTimingBuffer();
            buf.Append(1.0, 100);
            buf.Append(2.0, 200);

            buf.Reset();

            Assert.True(buf.IsEmpty);
            Assert.Equal(0, buf.Count);
        }

        [Fact]
        public void Append_FullBuffer_OverwritesOldest()
        {
            var buf = new RollingTimingBuffer();

            // Fill all 1024 entries
            for (int i = 0; i < 1024; i++)
                buf.Append(i, 100);

            Assert.Equal(1024, buf.Count);

            // One more — should overwrite oldest, count stays 1024
            buf.Append(1024, 9999);
            Assert.Equal(1024, buf.Count);

            // Verify the newest entry is included in stats
            buf.ComputeStats(1024, 0.5, out double avgMs, out double peakMs, out _, out _);
            // Only entry at t=1024 (within 0.5s window from 1024)
            Assert.Equal(9.999, peakMs, 3); // 9999 us = 9.999 ms
        }
    }

    #endregion

    #region HealthCounters

    public class HealthCountersTests
    {
        [Fact]
        public void Reset_ZerosAllFields()
        {
            var h = new HealthCounters();
            h.waypointCacheHits = 10;
            h.waypointCacheMisses = 5;
            h.snapshotRefreshSpikes = 3;
            h.spawnFailures = 2;
            h.spawnRetries = 1;
            h.ghostBuildsThisSession = 7;
            h.ghostDestroysThisSession = 6;

            h.Reset();

            Assert.Equal(0, h.waypointCacheHits);
            Assert.Equal(0, h.waypointCacheMisses);
            Assert.Equal(0, h.snapshotRefreshSpikes);
            Assert.Equal(0, h.spawnFailures);
            Assert.Equal(0, h.spawnRetries);
            Assert.Equal(0, h.ghostBuildsThisSession);
            Assert.Equal(0, h.ghostDestroysThisSession);
            // gcGen0Baseline should be set to current GC count, not zero
            Assert.Equal(GC.CollectionCount(0), h.gcGen0Baseline);
        }
    }

    #endregion

    #region DiagnosticsState

    [Collection("Sequential")]
    public class DiagnosticsStateTests : IDisposable
    {
        public DiagnosticsStateTests()
        {
            ParsekLog.SuppressLogging = true;
            DiagnosticsState.ResetForTesting();
        }

        public void Dispose()
        {
            DiagnosticsState.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void ResetSessionCounters_ClearsEverything()
        {
            // Set various fields to non-default values
            DiagnosticsState.health.waypointCacheHits = 50;
            DiagnosticsState.health.spawnFailures = 3;
            DiagnosticsState.playbackFrameHistory.Append(1.0, 500);
            DiagnosticsState.playbackFrameHistory.Append(2.0, 600);
            DiagnosticsState.playbackBudget = new FrameBudget { totalMicroseconds = 999 };
            DiagnosticsState.recordingBudget = new FrameBudget { ghostsProcessed = 5 };
            DiagnosticsState.hasCachedSnapshot = true;

            DiagnosticsState.ResetSessionCounters();

            // Health counters are zeroed (except gcGen0Baseline)
            Assert.Equal(0, DiagnosticsState.health.waypointCacheHits);
            Assert.Equal(0, DiagnosticsState.health.spawnFailures);
            Assert.Equal(GC.CollectionCount(0), DiagnosticsState.health.gcGen0Baseline);

            // Rolling buffer is empty
            Assert.True(DiagnosticsState.playbackFrameHistory.IsEmpty);

            // Frame budgets are default
            Assert.Equal(0, DiagnosticsState.playbackBudget.totalMicroseconds);
            Assert.Equal(0, DiagnosticsState.recordingBudget.ghostsProcessed);

            // Snapshot cache invalidated
            Assert.False(DiagnosticsState.hasCachedSnapshot);
        }

        [Fact]
        public void ResetForTesting_ClearsAllState()
        {
            // Set everything to non-default
            DiagnosticsState.health.ghostBuildsThisSession = 10;
            DiagnosticsState.playbackFrameHistory.Append(1.0, 100);
            DiagnosticsState.activeGrowthRate = new RecordingGrowthRate { totalPoints = 42 };
            DiagnosticsState.hasActiveGrowthRate = true;
            DiagnosticsState.hasCachedSnapshot = true;
            DiagnosticsState.cachedSnapshotUT = 999.0;
            DiagnosticsState.storageBreakdownCache["test"] = (new StorageBreakdown(), 1.0);
            DiagnosticsState.avgBytesPerPoint = 200.0;

            DiagnosticsState.ResetForTesting();

            // All fields back to defaults
            Assert.Equal(0, DiagnosticsState.health.ghostBuildsThisSession);
            Assert.True(DiagnosticsState.playbackFrameHistory.IsEmpty);
            Assert.Equal(0, DiagnosticsState.activeGrowthRate.totalPoints);
            Assert.False(DiagnosticsState.hasActiveGrowthRate);
            Assert.False(DiagnosticsState.hasCachedSnapshot);
            Assert.Equal(0.0, DiagnosticsState.cachedSnapshotUT);
            Assert.Empty(DiagnosticsState.storageBreakdownCache);
            Assert.Equal(85.0, DiagnosticsState.avgBytesPerPoint);
        }

        [Fact]
        public void InvalidateSnapshotCache_ClearsFlag()
        {
            DiagnosticsState.hasCachedSnapshot = true;
            DiagnosticsState.cachedSnapshotUT = 500.0;

            DiagnosticsState.InvalidateSnapshotCache();

            Assert.False(DiagnosticsState.hasCachedSnapshot);
            Assert.Equal(0.0, DiagnosticsState.cachedSnapshotUT);
        }

        [Fact]
        public void WaypointCache_SequentialAccess_CountsAsHit()
        {
            // Build a trajectory with 10 points at UT 0,1,2,...,9
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < 10; i++)
                points.Add(new TrajectoryPoint { ut = i });

            int cachedIndex = 0;

            // Sequential lookups: UT 0.5, 1.5, 2.5, ... should all be cache hits
            // (first call seeds the cache, subsequent calls hit cached or next-index)
            for (int i = 0; i < 8; i++)
            {
                TrajectoryMath.FindWaypointIndex(points, ref cachedIndex, i + 0.5);
            }

            Assert.True(DiagnosticsState.health.waypointCacheHits > 0,
                $"Expected cache hits > 0, got {DiagnosticsState.health.waypointCacheHits}");
            Assert.Equal(0, DiagnosticsState.health.waypointCacheMisses);
        }

        [Fact]
        public void WaypointCache_RandomJump_CountsAsMiss()
        {
            // Build a trajectory with 20 points at UT 0,1,2,...,19
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < 20; i++)
                points.Add(new TrajectoryPoint { ut = i });

            int cachedIndex = 0;

            // First call at UT 0.5 seeds the cache (binary search = miss)
            TrajectoryMath.FindWaypointIndex(points, ref cachedIndex, 0.5);

            // Jump far away — cached index 0 won't cover UT 15.5, nor will index 1
            TrajectoryMath.FindWaypointIndex(points, ref cachedIndex, 15.5);

            Assert.True(DiagnosticsState.health.waypointCacheMisses > 0,
                $"Expected cache misses > 0, got {DiagnosticsState.health.waypointCacheMisses}");
        }

        [Fact]
        public void WaypointCache_Reset_ZeroesBoth()
        {
            DiagnosticsState.health.waypointCacheHits = 42;
            DiagnosticsState.health.waypointCacheMisses = 7;

            DiagnosticsState.health.Reset();

            Assert.Equal(0, DiagnosticsState.health.waypointCacheHits);
            Assert.Equal(0, DiagnosticsState.health.waypointCacheMisses);
        }
    }

    #endregion

    #region DiagnosticsComputation

    [Collection("Sequential")]
    public class DiagnosticsComputationTests : IDisposable
    {
        public DiagnosticsComputationTests()
        {
            ParsekLog.SuppressLogging = true;
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
        }

        // --- FormatBytes ---

        [Fact]
        public void FormatBytes_Zero()
        {
            Assert.Equal("0 B", DiagnosticsComputation.FormatBytes(0));
        }

        [Fact]
        public void FormatBytes_Bytes()
        {
            Assert.Equal("512 B", DiagnosticsComputation.FormatBytes(512));
        }

        [Fact]
        public void FormatBytes_Kilobytes()
        {
            // 1536 bytes = 1.5 KB
            Assert.Equal("1.5 KB", DiagnosticsComputation.FormatBytes(1536));
        }

        [Fact]
        public void FormatBytes_Megabytes()
        {
            // 12.4 MB = 12.4 * 1024 * 1024 = 13,002,342.4 bytes
            long bytes = (long)(12.4 * 1024 * 1024);
            string result = DiagnosticsComputation.FormatBytes(bytes);
            Assert.Contains("MB", result);
            Assert.StartsWith("12.4", result);
        }

        [Fact]
        public void FormatBytes_Gigabytes()
        {
            // 2.1 GB = 2.1 * 1024^3
            long bytes = (long)(2.1 * 1024 * 1024 * 1024);
            string result = DiagnosticsComputation.FormatBytes(bytes);
            Assert.Contains("GB", result);
            Assert.StartsWith("2.1", result);
        }

        // --- FormatDuration ---

        [Fact]
        public void FormatDuration_Zero()
        {
            Assert.Equal("0s", DiagnosticsComputation.FormatDuration(0.0));
        }

        [Fact]
        public void FormatDuration_Seconds()
        {
            Assert.Equal("45s", DiagnosticsComputation.FormatDuration(45.0));
        }

        [Fact]
        public void FormatDuration_MinutesAndSeconds()
        {
            // 12 * 60 + 34 = 754 seconds
            Assert.Equal("12m 34s", DiagnosticsComputation.FormatDuration(754.0));
        }

        // --- FormatReport ---

        [Fact]
        public void FormatReport_KnownValues()
        {
            // Set up health counters for known values
            DiagnosticsState.health.waypointCacheHits = 1488;
            DiagnosticsState.health.waypointCacheMisses = 12;
            DiagnosticsState.health.snapshotRefreshSpikes = 2;
            DiagnosticsState.health.spawnFailures = 0;
            DiagnosticsState.health.ghostBuildsThisSession = 14;
            DiagnosticsState.health.gcGen0Baseline = GC.CollectionCount(0);

            var bd = new StorageBreakdown
            {
                recordingId = "test-1",
                vesselName = "Mun Landing",
                totalBytes = 2 * 1024 * 1024, // 2.0 MB
                pointCount = 8432,
                partEventCount = 47,
                orbitSegmentCount = 2,
                durationSeconds = 754.0, // 12m 34s
                bytesPerSecond = 2.0 * 1024 * 1024 / 754.0
            };

            var snap = new MetricSnapshot
            {
                totalStorageBytes = 12 * 1024 * 1024, // ~12.0 MB
                recordingCount = 1,
                perRecording = new[] { bd },
                loadedTrajectoryPoints = 47200,
                loadedPartEvents = 312,
                loadedOrbitSegments = 18,
                loadedSnapshotCount = 46,
                estimatedMemoryBytes = 47200L * 136 + 312L * 88 + 18L * 120 + 46L * 8192,
                activeGhostCount = 8,
                activeOverlapGhostCount = 2,
                fullGhostCount = 3,
                reducedGhostCount = 2,
                hiddenGhostCount = 3,
                watchedOverrideGhostCount = 1,
                lastPlaybackBudget = new FrameBudget { warpRate = 1.0f },
                // Populate playback rolling stats so the gate (entriesInWindow > 0) shows formatted values
                playbackAvgTotalMs = 2.5,
                playbackPeakTotalMs = 3.2,
                playbackWindowDurationSeconds = 1.0,
                playbackEntriesInWindow = 2,
                lastSaveTiming = new SaveLoadTiming { totalMilliseconds = 120, dirtyRecordingsWritten = 3 },
                lastLoadTiming = new SaveLoadTiming { totalMilliseconds = 340, recordingsProcessed = 23 }
            };

            string report = DiagnosticsComputation.FormatReport(snap);

            Assert.Contains("===== DIAGNOSTICS REPORT =====", report);
            Assert.Contains("===== END REPORT =====", report);
            Assert.Contains("12.0 MB total, 1 recordings", report);
            Assert.Contains("\"Mun Landing\"", report);
            Assert.Contains("8432 pts", report);
            Assert.Contains("47 evts", report);
            Assert.Contains("2 segs", report);
            Assert.Contains("12m 34s", report);
            Assert.Contains("47200 pts", report);
            Assert.Contains("312 evts", report);
            Assert.Contains("18 segs", report);
            Assert.Contains("46 snapshots", report);
            Assert.Contains("Ghosts: 8 active (2 overlap), 3 full, 2 reduced, 3 hidden, 1 watched override", report);
            Assert.Contains("Playback budget:", report);
            Assert.Contains("ms avg", report);
            Assert.Contains("ms peak", report);
            Assert.Contains("Save: 120 ms last (3 dirty)", report);
            Assert.Contains("Load: 340 ms last (23 total)", report);
            Assert.Contains("cache", report);
            Assert.Contains("GC gen0:", report);
        }

        [Fact]
        public void FormatReport_EmptyRollingBuffer()
        {
            // Rolling buffer is empty by default after reset
            var snap = new MetricSnapshot
            {
                perRecording = new StorageBreakdown[0]
            };

            string report = DiagnosticsComputation.FormatReport(snap);

            Assert.Contains("Playback budget: N/A", report);
        }

        [Fact]
        public void FormatReport_ZeroDuration()
        {
            var bd = new StorageBreakdown
            {
                recordingId = "zero-dur",
                vesselName = "Zero",
                totalBytes = 1024,
                pointCount = 10,
                partEventCount = 0,
                orbitSegmentCount = 0,
                durationSeconds = 0.0,
                bytesPerSecond = 0.0
            };

            var snap = new MetricSnapshot
            {
                totalStorageBytes = 1024,
                recordingCount = 1,
                perRecording = new[] { bd }
            };

            string report = DiagnosticsComputation.FormatReport(snap);

            // Should not contain NaN or Infinity
            Assert.DoesNotContain("NaN", report);
            Assert.DoesNotContain("Infinity", report);
            // bytesPerSecond is 0.0 so formatted as "0 B/s"
            Assert.Contains("0 B/s", report);
            // Duration should show "0s"
            Assert.Contains("0s", report);
        }

        [Fact]
        public void FormatReport_NoRecordings()
        {
            var snap = new MetricSnapshot
            {
                totalStorageBytes = 0,
                recordingCount = 0,
                perRecording = new StorageBreakdown[0]
            };

            string report = DiagnosticsComputation.FormatReport(snap);

            Assert.Contains("0 B total, 0 recordings", report);
        }

        [Fact]
        public void MemoryEstimation_Formula()
        {
            // 100 pts + 10 evts + 5 segs + 2 snaps
            // = 100*136 + 10*88 + 5*120 + 2*8192 = 13600 + 880 + 600 + 16384 = 31464
            var rec = new Recording
            {
                VesselName = "TestVessel"
            };
            for (int i = 0; i < 100; i++)
                rec.Points.Add(new TrajectoryPoint { ut = i });
            for (int i = 0; i < 10; i++)
                rec.PartEvents.Add(new PartEvent());
            for (int i = 0; i < 5; i++)
                rec.OrbitSegments.Add(new OrbitSegment());
            // 2 snapshots: set VesselSnapshot + GhostVisualSnapshot (both non-null)
            rec.VesselSnapshot = new ConfigNode("VESSEL");
            rec.GhostVisualSnapshot = new ConfigNode("GHOST");

            RecordingStore.AddRecordingWithTreeForTesting(rec);

            // Build a second recording with a snapshot too
            var rec2 = new Recording { VesselName = "TestVessel2" };
            rec2.VesselSnapshot = new ConfigNode("VESSEL2");
            rec2.GhostVisualSnapshot = new ConfigNode("GHOST2");
            RecordingStore.AddRecordingWithTreeForTesting(rec2);

            DiagnosticsComputation.ClockSource = () => 1000.0;
            var snap = DiagnosticsComputation.ComputeSnapshot(100.0);

            // 100 pts, 10 evts, 5 segs, 2 snapshots (both recordings have both snapshots)
            long expected = 100L * 136 + 10L * 88 + 5L * 120 + 2L * 8192;
            Assert.Equal(expected, snap.estimatedMemoryBytes);
            Assert.Equal(100, snap.loadedTrajectoryPoints);
            Assert.Equal(10, snap.loadedPartEvents);
            Assert.Equal(5, snap.loadedOrbitSegments);
            Assert.Equal(2, snap.loadedSnapshotCount);
        }

        [Fact]
        public void CacheHitRate_ZeroLookups()
        {
            // Zero cache hits and misses
            DiagnosticsState.health.waypointCacheHits = 0;
            DiagnosticsState.health.waypointCacheMisses = 0;

            var snap = new MetricSnapshot
            {
                perRecording = new StorageBreakdown[0]
            };

            string report = DiagnosticsComputation.FormatReport(snap);

            // Should show "N/A" for hit rate, not division by zero
            Assert.Contains("cache N/A hit", report);
            Assert.DoesNotContain("NaN", report);
            Assert.DoesNotContain("Infinity", report);
        }

        [Fact]
        public void GrowthRate_ZeroElapsed()
        {
            // A growth rate with zero elapsed shouldn't cause NaN
            DiagnosticsState.activeGrowthRate = new RecordingGrowthRate
            {
                totalPoints = 1,
                totalEvents = 0,
                elapsedSeconds = 0.0,
                pointsPerSecond = 0.0,
                eventsPerSecond = 0.0
            };
            DiagnosticsState.hasActiveGrowthRate = true;

            var snap = new MetricSnapshot
            {
                perRecording = new StorageBreakdown[0]
            };

            string report = DiagnosticsComputation.FormatReport(snap);

            // Should not crash or contain NaN
            Assert.DoesNotContain("NaN", report);
            Assert.DoesNotContain("Infinity", report);
            // Recording budget should show "(active)" because hasActiveGrowthRate is true
            Assert.Contains("(active)", report);
        }

        [Fact]
        public void ComputeSnapshot_NoRecordings_Zeros()
        {
            // Empty RecordingStore
            DiagnosticsComputation.ClockSource = () => 1000.0;
            var snap = DiagnosticsComputation.ComputeSnapshot(100.0);

            Assert.Equal(0, snap.recordingCount);
            Assert.Equal(0L, snap.totalStorageBytes);
            Assert.Equal(0, snap.loadedTrajectoryPoints);
            Assert.Equal(0, snap.loadedPartEvents);
            Assert.Equal(0, snap.loadedOrbitSegments);
            Assert.Equal(0, snap.loadedSnapshotCount);
            Assert.Equal(0L, snap.estimatedMemoryBytes);
            Assert.NotNull(snap.perRecording);
            Assert.Empty(snap.perRecording);
        }

        [Fact]
        public void PopulateGhostStateCounts_ClassifiesLiveLodTiers()
        {
            var primary = new Dictionary<int, GhostPlaybackState>
            {
                { 0, new GhostPlaybackState
                    {
                        currentZone = RenderingZone.Physics,
                        lastDistance = 1000.0
                    }
                },
                { 1, new GhostPlaybackState
                    {
                        currentZone = RenderingZone.Visual,
                        lastDistance = 10000.0,
                        distanceLodReduced = true
                    }
                },
                { 2, new GhostPlaybackState
                    {
                        currentZone = RenderingZone.Visual,
                        lastDistance = 60000.0
                    }
                },
                { 3, new GhostPlaybackState
                    {
                        currentZone = RenderingZone.Beyond,
                        lastDistance = 150000.0
                    }
                }
            };

            var overlap = new Dictionary<int, List<GhostPlaybackState>>
            {
                { 1, new List<GhostPlaybackState>
                    {
                        new GhostPlaybackState
                        {
                            currentZone = RenderingZone.Visual,
                            lastDistance = 5000.0,
                            distanceLodReduced = true
                        }
                    }
                }
            };

            var snap = new MetricSnapshot();
            DiagnosticsComputation.PopulateGhostStateCounts(
                ref snap,
                primary,
                overlap,
                watchedIndex: 3,
                fallbackActiveGhostCount: 99);

            Assert.Equal(5, snap.activeGhostCount);
            Assert.Equal(1, snap.activeOverlapGhostCount);
            Assert.Equal(2, snap.fullGhostCount);
            Assert.Equal(2, snap.reducedGhostCount);
            Assert.Equal(1, snap.hiddenGhostCount);
            Assert.Equal(1, snap.watchedOverrideGhostCount);
        }

        [Fact]
        public void GetOrComputeSnapshot_ReturnsCachedWithinTTL()
        {
            DiagnosticsComputation.ClockSource = () => 1000.0;

            // First call computes
            var snap1 = DiagnosticsComputation.GetOrComputeSnapshot(100.0);
            Assert.True(DiagnosticsState.hasCachedSnapshot);

            // Second call within TTL (2.0s) returns cached
            var snap2 = DiagnosticsComputation.GetOrComputeSnapshot(101.0);
            Assert.Equal(100.0, DiagnosticsState.cachedSnapshotUT);

            // Still cached because 101 - 100 = 1.0 < 2.0 TTL
            Assert.Equal(snap1.loadedTrajectoryPoints, snap2.loadedTrajectoryPoints);
        }

        [Fact]
        public void GetOrComputeSnapshot_RecomputesAfterTTL()
        {
            DiagnosticsComputation.ClockSource = () => 1000.0;

            var snap1 = DiagnosticsComputation.GetOrComputeSnapshot(100.0);

            // Now commit a recording
            var rec = new Recording { VesselName = "New" };
            for (int i = 0; i < 50; i++)
                rec.Points.Add(new TrajectoryPoint { ut = i });
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            // Compute after TTL expires (100 + 2.1 > 2.0 TTL)
            var snap2 = DiagnosticsComputation.GetOrComputeSnapshot(102.1);

            // Should have recomputed with the new recording's data
            Assert.Equal(50, snap2.loadedTrajectoryPoints);
        }

        [Fact]
        public void FormatReport_RecordingBudget_InactiveWhenNoGrowthRate()
        {
            DiagnosticsState.hasActiveGrowthRate = false;

            var snap = new MetricSnapshot
            {
                perRecording = new StorageBreakdown[0]
            };

            string report = DiagnosticsComputation.FormatReport(snap);
            Assert.Contains("(inactive)", report);
        }

        [Fact]
        public void FormatReport_HealthSection_WithHits()
        {
            DiagnosticsState.health.waypointCacheHits = 990;
            DiagnosticsState.health.waypointCacheMisses = 10;
            DiagnosticsState.health.snapshotRefreshSpikes = 3;
            DiagnosticsState.health.spawnFailures = 1;
            DiagnosticsState.health.ghostBuildsThisSession = 20;

            var snap = new MetricSnapshot
            {
                perRecording = new StorageBreakdown[0]
            };

            string report = DiagnosticsComputation.FormatReport(snap);

            Assert.Contains("cache 99.0% hit", report);
            Assert.Contains("10 miss of 1000 lookups", report);
            Assert.Contains("spikes 3", report);
            Assert.Contains("spawn fail 1", report);
            Assert.Contains("builds 20", report);
        }
    }

    #endregion
}
