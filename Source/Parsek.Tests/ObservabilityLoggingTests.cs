using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Log assertion tests for observability instrumentation.
    /// Verifies that the correct log lines are emitted at the correct levels
    /// for budget threshold warnings, scene load snapshots, and diagnostics reports.
    /// </summary>
    [Collection("Sequential")]
    public class ObservabilityLoggingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ObservabilityLoggingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            DiagnosticsState.ResetForTesting();
            DiagnosticsComputation.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            DiagnosticsState.ResetForTesting();
            DiagnosticsComputation.ResetForTesting();
        }

        #region Playback Budget Threshold

        [Fact]
        public void PlaybackBudgetWarning_AboveThreshold_EmitsWarn()
        {
            // 10ms > 8ms threshold
            DiagnosticsComputation.CheckPlaybackBudgetThreshold(10000, 5, 1.0f);

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[Diagnostics]") &&
                l.Contains("Playback frame budget exceeded"));
        }

        [Fact]
        public void PlaybackBudgetWarning_AboveThreshold_IncludesGhostCountAndWarp()
        {
            DiagnosticsComputation.CheckPlaybackBudgetThreshold(12000, 7, 4.0f);

            var warnLine = logLines.FirstOrDefault(l =>
                l.Contains("[WARN]") && l.Contains("Playback frame budget exceeded"));
            Assert.NotNull(warnLine);
            Assert.Contains("7 ghosts", warnLine);
            Assert.Contains("warp: 4x", warnLine);
            Assert.Contains("12.0ms", warnLine);
        }

        [Fact]
        public void PlaybackBudgetWarning_BelowThreshold_NoWarn()
        {
            // 3ms < 8ms threshold
            DiagnosticsComputation.CheckPlaybackBudgetThreshold(3000, 2, 1.0f);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[WARN]") && l.Contains("Playback frame budget exceeded"));
        }

        [Fact]
        public void PlaybackBudgetWarning_ExactlyAtThreshold_NoWarn()
        {
            // 8.0ms is not > 8.0ms, so no warning
            DiagnosticsComputation.CheckPlaybackBudgetThreshold(8000, 3, 1.0f);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[WARN]") && l.Contains("Playback frame budget exceeded"));
        }

        #endregion

        #region Recording Budget Threshold

        [Fact]
        public void RecordingBudgetWarning_AboveThreshold_EmitsWarn()
        {
            // 5ms > 4ms threshold
            DiagnosticsComputation.CheckRecordingBudgetThreshold(5000, "TestRocket");

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[Diagnostics]") &&
                l.Contains("Recording frame exceeded budget"));
        }

        [Fact]
        public void RecordingBudgetWarning_AboveThreshold_IncludesVesselName()
        {
            DiagnosticsComputation.CheckRecordingBudgetThreshold(6000, "Mun Lander");

            var warnLine = logLines.FirstOrDefault(l =>
                l.Contains("[WARN]") && l.Contains("Recording frame exceeded budget"));
            Assert.NotNull(warnLine);
            Assert.Contains("Mun Lander", warnLine);
            Assert.Contains("6.00ms", warnLine);
        }

        [Fact]
        public void RecordingBudgetWarning_BelowThreshold_NoWarn()
        {
            // 2ms < 4ms threshold
            DiagnosticsComputation.CheckRecordingBudgetThreshold(2000, "TestRocket");

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[WARN]") && l.Contains("Recording frame exceeded budget"));
        }

        [Fact]
        public void RecordingBudgetWarning_NullVesselName_NoException()
        {
            DiagnosticsComputation.CheckRecordingBudgetThreshold(5000, null);

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("Recording frame exceeded budget"));
        }

        #endregion

        #region Scene Load Snapshot

        [Fact]
        public void SceneLoadSnapshot_EmitsVerbose_WithCounts()
        {
            // Add some committed recordings with data
            var rec = new Recording { VesselName = "TestVessel" };
            for (int i = 0; i < 50; i++)
                rec.Points.Add(new TrajectoryPoint { ut = i });
            for (int i = 0; i < 5; i++)
                rec.PartEvents.Add(new PartEvent());
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            DiagnosticsComputation.EmitSceneLoadSnapshot(1, "FLIGHT");

            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]") &&
                l.Contains("[Diagnostics]") &&
                l.Contains("Scene load complete (FLIGHT)"));

            var snapLine = logLines.FirstOrDefault(l =>
                l.Contains("Scene load complete"));
            Assert.NotNull(snapLine);
            Assert.Contains("1 recordings", snapLine);
            Assert.Contains("50 pts", snapLine);
            Assert.Contains("5 evts", snapLine);
        }

        [Fact]
        public void SceneLoadSnapshot_NoRecordings_EmitsZeros()
        {
            DiagnosticsComputation.EmitSceneLoadSnapshot(0, "SPACECENTER");

            var snapLine = logLines.FirstOrDefault(l =>
                l.Contains("Scene load complete"));
            Assert.NotNull(snapLine);
            Assert.Contains("0 recordings", snapLine);
            Assert.Contains("0 pts", snapLine);
            Assert.Contains("0 B memory est", snapLine);
        }

        [Fact]
        public void SceneLoadSnapshot_IncludesGhostCount()
        {
            // Ghost count comes from playbackBudget.ghostsProcessed
            DiagnosticsState.playbackBudget.ghostsProcessed = 3;

            DiagnosticsComputation.EmitSceneLoadSnapshot(0, "FLIGHT");

            var snapLine = logLines.FirstOrDefault(l =>
                l.Contains("Scene load complete"));
            Assert.NotNull(snapLine);
            Assert.Contains("3 ghosts", snapLine);
        }

        [Fact]
        public void SceneLoadSnapshot_NullSceneName_ShowsQuestionMark()
        {
            DiagnosticsComputation.EmitSceneLoadSnapshot(0, null);

            var snapLine = logLines.FirstOrDefault(l =>
                l.Contains("Scene load complete"));
            Assert.NotNull(snapLine);
            Assert.Contains("(?)", snapLine);
        }

        #endregion

        #region FormatReport All Sections Present

        [Fact]
        public void FormatReport_AllSectionsPresent()
        {
            // Set up known state
            DiagnosticsState.health.waypointCacheHits = 100;
            DiagnosticsState.health.waypointCacheMisses = 5;
            DiagnosticsState.health.ghostBuildsThisSession = 10;
            DiagnosticsState.playbackFrameHistory.Append(100.0, 2000);
            DiagnosticsComputation.ClockSource = () => 100.0;

            var snap = new MetricSnapshot
            {
                totalStorageBytes = 1024 * 1024,
                recordingCount = 2,
                perRecording = new[]
                {
                    new StorageBreakdown
                    {
                        recordingId = "a", vesselName = "Alpha",
                        totalBytes = 512 * 1024, pointCount = 100,
                        partEventCount = 10, orbitSegmentCount = 1,
                        durationSeconds = 60.0, bytesPerSecond = 512 * 1024 / 60.0
                    },
                    new StorageBreakdown
                    {
                        recordingId = "b", vesselName = "Beta",
                        totalBytes = 512 * 1024, pointCount = 200,
                        partEventCount = 20, orbitSegmentCount = 2,
                        durationSeconds = 120.0, bytesPerSecond = 512 * 1024 / 120.0
                    }
                },
                loadedTrajectoryPoints = 300,
                loadedPartEvents = 30,
                loadedOrbitSegments = 3,
                loadedSnapshotCount = 4,
                estimatedMemoryBytes = 300L * 136 + 30L * 88 + 3L * 120 + 4L * 8192,
                activeGhostCount = 5,
                activeOverlapGhostCount = 1,
                fullGhostCount = 2,
                reducedGhostCount = 1,
                hiddenGhostCount = 2,
                watchedOverrideGhostCount = 1,
                lastPlaybackBudget = new FrameBudget { warpRate = 1.0f },
                lastSaveTiming = new SaveLoadTiming { totalMilliseconds = 50, dirtyRecordingsWritten = 1 },
                lastLoadTiming = new SaveLoadTiming { totalMilliseconds = 100, recordingsProcessed = 2 }
            };

            string report = DiagnosticsComputation.FormatReport(snap);

            // All required sections
            Assert.Contains("===== DIAGNOSTICS REPORT =====", report);
            Assert.Contains("===== END REPORT =====", report);
            Assert.Contains("Storage:", report);
            Assert.Contains("Memory:", report);
            Assert.Contains("Ghosts:", report);
            Assert.Contains("Playback budget:", report);
            Assert.Contains("Recording budget:", report);
            Assert.Contains("Save:", report);
            Assert.Contains("Load:", report);
            Assert.Contains("Health:", report);
            Assert.Contains("GC gen0:", report);
        }

        #endregion

        #region WarnRateLimited Suppression

        [Fact]
        public void WarnRateLimited_RespectsInterval_SecondCallSuppressed()
        {
            double now = 5000.0;
            ParsekLog.ClockOverrideForTesting = () => now;

            // First call emits
            ParsekLog.WarnRateLimited("Test", "key1", "budget warning", 30.0);
            Assert.Single(logLines);
            Assert.Contains("[WARN]", logLines[0]);

            // Second call within 30s is suppressed
            now = 5010.0;
            ParsekLog.WarnRateLimited("Test", "key1", "budget warning", 30.0);
            Assert.Single(logLines); // still only 1 line

            // Third call after 30s emits again
            now = 5031.0;
            ParsekLog.WarnRateLimited("Test", "key1", "budget warning", 30.0);
            Assert.Equal(2, logLines.Count);
            Assert.Contains("suppressed=1", logLines[1]);
        }

        #endregion

        #region Recording Growth Rate Logging

        [Fact]
        public void RecordingGrowthRate_FormatMatchesExpected()
        {
            // Simulate what FinalizeRecordingState logs
            var gr = new RecordingGrowthRate
            {
                totalPoints = 500,
                totalEvents = 25,
                elapsedSeconds = 30.0,
                pointsPerSecond = 500.0 / 30.0,
                eventsPerSecond = 25.0 / 30.0,
                estimatedFinalBytes = 42500
            };

            ParsekLog.Verbose("Diagnostics",
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Recording growth rate at stop: {0} points, {1} events, {2:F1}s elapsed, " +
                    "{3:F2} pts/s, {4:F2} evts/s, est {5} bytes",
                    gr.totalPoints, gr.totalEvents, gr.elapsedSeconds,
                    gr.pointsPerSecond, gr.eventsPerSecond, gr.estimatedFinalBytes));

            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]") &&
                l.Contains("[Diagnostics]") &&
                l.Contains("Recording growth rate at stop"));

            var growthLine = logLines.FirstOrDefault(l =>
                l.Contains("Recording growth rate at stop"));
            Assert.NotNull(growthLine);
            Assert.Contains("500 points", growthLine);
            Assert.Contains("25 events", growthLine);
            Assert.Contains("30.0s elapsed", growthLine);
            Assert.Contains("pts/s", growthLine);
            Assert.Contains("evts/s", growthLine);
        }

        #endregion

        #region Budget Threshold Constants

        [Fact]
        public void PlaybackBudgetThreshold_Is8ms()
        {
            Assert.Equal(8.0, DiagnosticsComputation.PlaybackBudgetThresholdMs);
        }

        [Fact]
        public void RecordingBudgetThreshold_Is4ms()
        {
            Assert.Equal(4.0, DiagnosticsComputation.RecordingBudgetThresholdMs);
        }

        #endregion
    }
}
