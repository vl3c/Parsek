namespace Parsek.InGameTests
{
    /// <summary>
    /// In-game tests for the diagnostics/observability system.
    /// Verifies ComputeSnapshot and FormatReport produce sane output in a live KSP environment.
    /// </summary>
    public static class DiagnosticsTests
    {
        [InGameTest(Category = "Diagnostics", Scene = GameScenes.FLIGHT,
            Description = "DiagnosticsComputation.FormatReport produces a report with all expected sections")]
        public static void DiagnosticsReportProducesOutput()
        {
            double ut = Planetarium.GetUniversalTime();
            var snapshot = DiagnosticsComputation.ComputeSnapshot(ut);
            string report = DiagnosticsComputation.FormatReport(snapshot);

            InGameAssert.IsNotNull(report, "FormatReport returned null");
            InGameAssert.Contains(report, "DIAGNOSTICS REPORT", "Report missing header");
            InGameAssert.Contains(report, "Storage:", "Report missing Storage section");
            InGameAssert.Contains(report, "Memory:", "Report missing Memory section");
            InGameAssert.Contains(report, "Playback budget:", "Report missing Playback section");
            InGameAssert.Contains(report, "Health:", "Report missing Health section");
            InGameAssert.Contains(report, "END REPORT", "Report missing footer");

            ParsekLog.Verbose("TestRunner",
                $"Diagnostics report produced {report.Length} chars with all expected sections");
        }

        [InGameTest(Category = "Diagnostics", Scene = GameScenes.FLIGHT,
            Description = "ComputeSnapshot ghost count is non-negative")]
        public static void DiagnosticsGhostCountNonNegative()
        {
            double ut = Planetarium.GetUniversalTime();
            var snapshot = DiagnosticsComputation.ComputeSnapshot(ut);

            InGameAssert.IsTrue(snapshot.lastPlaybackBudget.ghostsProcessed >= 0,
                $"Ghost count should be non-negative, was {snapshot.lastPlaybackBudget.ghostsProcessed}");
            InGameAssert.IsTrue(snapshot.activeGhostCount >= 0,
                $"Active ghost count should be non-negative, was {snapshot.activeGhostCount}");

            ParsekLog.Verbose("TestRunner",
                $"Ghost counts: processed={snapshot.lastPlaybackBudget.ghostsProcessed}, active={snapshot.activeGhostCount}");
        }

        [InGameTest(Category = "Diagnostics",
            Description = "Storage breakdown values are non-negative")]
        public static void StorageBreakdownNonNegative()
        {
            double ut = 0.0;
            try { ut = Planetarium.GetUniversalTime(); }
            catch { /* not in flight */ }

            var snapshot = DiagnosticsComputation.ComputeSnapshot(ut);

            InGameAssert.IsTrue(snapshot.totalStorageBytes >= 0,
                $"Total storage should be non-negative, was {snapshot.totalStorageBytes}");
            InGameAssert.IsTrue(snapshot.recordingCount >= 0,
                $"Recording count should be non-negative, was {snapshot.recordingCount}");
            InGameAssert.IsTrue(snapshot.estimatedMemoryBytes >= 0,
                $"Estimated memory should be non-negative, was {snapshot.estimatedMemoryBytes}");

            ParsekLog.Verbose("TestRunner",
                $"Storage: {DiagnosticsComputation.FormatBytes(snapshot.totalStorageBytes)}, " +
                $"{snapshot.recordingCount} recordings, " +
                $"~{DiagnosticsComputation.FormatBytes(snapshot.estimatedMemoryBytes)} memory");
        }

        [InGameTest(Category = "Diagnostics",
            Description = "RunDiagnosticsReport executes without throwing")]
        public static void RunDiagnosticsReportDoesNotThrow()
        {
            string report = DiagnosticsComputation.RunDiagnosticsReport();

            InGameAssert.IsNotNull(report, "RunDiagnosticsReport returned null");
            InGameAssert.IsTrue(report.Length > 0, "RunDiagnosticsReport returned empty string");

            ParsekLog.Verbose("TestRunner",
                $"RunDiagnosticsReport completed, {report.Length} chars");
        }

        [InGameTest(Category = "Diagnostics",
            Description = "Recording count in snapshot matches RecordingStore")]
        public static void RecordingCountMatchesStore()
        {
            var recordings = RecordingStore.CommittedRecordings;
            int storeCount = recordings != null ? recordings.Count : 0;

            double ut = 0.0;
            try { ut = Planetarium.GetUniversalTime(); }
            catch { /* not in flight */ }

            var snapshot = DiagnosticsComputation.ComputeSnapshot(ut);

            InGameAssert.AreEqual(storeCount, snapshot.recordingCount,
                $"Snapshot recording count ({snapshot.recordingCount}) != RecordingStore count ({storeCount})");

            ParsekLog.Verbose("TestRunner",
                $"Recording count consistent: store={storeCount}, snapshot={snapshot.recordingCount}");
        }
    }
}
