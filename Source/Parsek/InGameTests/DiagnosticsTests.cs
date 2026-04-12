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
            InGameAssert.Contains(report, "Ghosts:", "Report missing Ghost section");
            InGameAssert.Contains(report, "FX:", "Report missing FX section");
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
            InGameAssert.IsTrue(snapshot.activeOverlapGhostCount >= 0,
                $"Overlap ghost count should be non-negative, was {snapshot.activeOverlapGhostCount}");
            InGameAssert.IsTrue(snapshot.ghostsWithEngineFx >= 0,
                $"Engine FX ghost count should be non-negative, was {snapshot.ghostsWithEngineFx}");
            InGameAssert.IsTrue(snapshot.engineParticleSystemCount >= 0,
                $"Engine particle system count should be non-negative, was {snapshot.engineParticleSystemCount}");
            InGameAssert.IsTrue(snapshot.ghostsWithRcsFx >= 0,
                $"RCS FX ghost count should be non-negative, was {snapshot.ghostsWithRcsFx}");
            InGameAssert.IsTrue(snapshot.rcsParticleSystemCount >= 0,
                $"RCS particle system count should be non-negative, was {snapshot.rcsParticleSystemCount}");
            InGameAssert.IsTrue(snapshot.lastPlaybackBudget.destroyMicroseconds >= 0,
                $"Destroy timing should be non-negative, was {snapshot.lastPlaybackBudget.destroyMicroseconds}");

            ParsekLog.Verbose("TestRunner",
                $"Ghost counts: processed={snapshot.lastPlaybackBudget.ghostsProcessed}, " +
                $"primary={snapshot.activeGhostCount}, overlap={snapshot.activeOverlapGhostCount}, " +
                $"engineFx={snapshot.ghostsWithEngineFx}/{snapshot.engineParticleSystemCount}, " +
                $"rcsFx={snapshot.ghostsWithRcsFx}/{snapshot.rcsParticleSystemCount}");
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

        [InGameTest(Category = "Diagnostics", Scene = GameScenes.FLIGHT,
            Description = "Snapshot ghost/FX counts match GhostPlaybackEngine observability")]
        public static void DiagnosticsSnapshotMatchesEngineObservability()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            double ut = Planetarium.GetUniversalTime();
            var snapshot = DiagnosticsComputation.ComputeSnapshot(ut);
            var expected = flight.Engine.CaptureGhostObservability();

            InGameAssert.AreEqual(expected.activePrimaryGhostCount, snapshot.activeGhostCount,
                $"Primary ghost count mismatch: expected {expected.activePrimaryGhostCount}, got {snapshot.activeGhostCount}");
            InGameAssert.AreEqual(expected.activeOverlapGhostCount, snapshot.activeOverlapGhostCount,
                $"Overlap ghost count mismatch: expected {expected.activeOverlapGhostCount}, got {snapshot.activeOverlapGhostCount}");
            InGameAssert.AreEqual(expected.zone1GhostCount, snapshot.zone1GhostCount,
                $"Zone 1 ghost count mismatch: expected {expected.zone1GhostCount}, got {snapshot.zone1GhostCount}");
            InGameAssert.AreEqual(expected.zone2GhostCount, snapshot.zone2GhostCount,
                $"Zone 2 ghost count mismatch: expected {expected.zone2GhostCount}, got {snapshot.zone2GhostCount}");
            InGameAssert.AreEqual(expected.softCapReducedCount, snapshot.softCapReducedCount,
                $"Reduced ghost count mismatch: expected {expected.softCapReducedCount}, got {snapshot.softCapReducedCount}");
            InGameAssert.AreEqual(expected.softCapSimplifiedCount, snapshot.softCapSimplifiedCount,
                $"Simplified ghost count mismatch: expected {expected.softCapSimplifiedCount}, got {snapshot.softCapSimplifiedCount}");
            InGameAssert.AreEqual(expected.ghostsWithEngineFx, snapshot.ghostsWithEngineFx,
                $"Engine FX ghost count mismatch: expected {expected.ghostsWithEngineFx}, got {snapshot.ghostsWithEngineFx}");
            InGameAssert.AreEqual(expected.engineModuleCount, snapshot.engineModuleCount,
                $"Engine FX module count mismatch: expected {expected.engineModuleCount}, got {snapshot.engineModuleCount}");
            InGameAssert.AreEqual(expected.engineParticleSystemCount, snapshot.engineParticleSystemCount,
                $"Engine FX particle count mismatch: expected {expected.engineParticleSystemCount}, got {snapshot.engineParticleSystemCount}");
            InGameAssert.AreEqual(expected.ghostsWithRcsFx, snapshot.ghostsWithRcsFx,
                $"RCS FX ghost count mismatch: expected {expected.ghostsWithRcsFx}, got {snapshot.ghostsWithRcsFx}");
            InGameAssert.AreEqual(expected.rcsModuleCount, snapshot.rcsModuleCount,
                $"RCS FX module count mismatch: expected {expected.rcsModuleCount}, got {snapshot.rcsModuleCount}");
            InGameAssert.AreEqual(expected.rcsParticleSystemCount, snapshot.rcsParticleSystemCount,
                $"RCS FX particle count mismatch: expected {expected.rcsParticleSystemCount}, got {snapshot.rcsParticleSystemCount}");

            ParsekLog.Verbose("TestRunner",
                $"Diagnostics snapshot matches engine observability: primary={snapshot.activeGhostCount}, " +
                $"overlap={snapshot.activeOverlapGhostCount}, engineFx={snapshot.ghostsWithEngineFx}, rcsFx={snapshot.ghostsWithRcsFx}");
        }
    }
}
