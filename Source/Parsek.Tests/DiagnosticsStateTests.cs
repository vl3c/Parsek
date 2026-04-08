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

            buf.ComputeStats(4.0, 10.0, out double avgMs, out double peakMs, out double window);

            // avg = (1000+2000+3000+1500+2500)/5 = 10000/5 = 2000 microseconds = 2.0 ms
            Assert.Equal(2.0, avgMs, 6);
            // peak = 3000 microseconds = 3.0 ms
            Assert.Equal(3.0, peakMs, 6);
            // window = 4.0 - 0.0 = 4.0
            Assert.Equal(4.0, window, 6);
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
            buf.ComputeStats(6.0, 3.0, out double avgMs, out double peakMs, out double window);

            // avg = 100us = 0.1ms for all entries
            Assert.Equal(0.1, avgMs, 6);
            Assert.Equal(0.1, peakMs, 6);
            // window = 6.0 - 3.0 = 3.0
            Assert.Equal(3.0, window, 6);
        }

        [Fact]
        public void ComputeStats_EmptyBuffer()
        {
            var buf = new RollingTimingBuffer();

            buf.ComputeStats(5.0, 10.0, out double avgMs, out double peakMs, out double window);

            Assert.Equal(0.0, avgMs);
            Assert.Equal(0.0, peakMs);
            Assert.Equal(0.0, window);
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
            buf.ComputeStats(1024, 0.5, out double avgMs, out double peakMs, out _);
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
            h.softCapActivations = 4;
            h.softCapDespawns = 2;
            h.ghostBuildsThisSession = 7;
            h.ghostDestroysThisSession = 6;

            h.Reset();

            Assert.Equal(0, h.waypointCacheHits);
            Assert.Equal(0, h.waypointCacheMisses);
            Assert.Equal(0, h.snapshotRefreshSpikes);
            Assert.Equal(0, h.spawnFailures);
            Assert.Equal(0, h.spawnRetries);
            Assert.Equal(0, h.softCapActivations);
            Assert.Equal(0, h.softCapDespawns);
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
    }

    #endregion
}
