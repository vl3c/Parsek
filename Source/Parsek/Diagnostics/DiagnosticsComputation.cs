using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// Computes diagnostics metrics and formats the human-readable diagnostics report.
    /// All methods are static. File-system access is on-demand only (never per-frame).
    /// All numeric formatting uses InvariantCulture.
    /// </summary>
    internal static class DiagnosticsComputation
    {
        /// <summary>
        /// Clock source for wall-time in ComputeStats calls.
        /// Defaults to DateTime.UtcNow ticks. Override in tests for deterministic time.
        /// </summary>
        internal static Func<double> ClockSource;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static double GetWallTimestamp()
        {
            if (ClockSource != null)
                return ClockSource();

            // Fallback: use DateTime since we can't rely on UnityEngine.Time in tests
            return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        }

        // ------------------------------------------------------------------
        // FormatBytes
        // ------------------------------------------------------------------

        /// <summary>
        /// Formats a byte count as a human-readable string: "0 B", "1.2 KB", "3.4 MB", "5.6 GB".
        /// 1 KB = 1024 bytes. Uses InvariantCulture.
        /// </summary>
        internal static string FormatBytes(long bytes)
        {
            if (bytes < 0) bytes = 0;

            if (bytes < 1024L)
                return string.Format(Inv, "{0} B", bytes);

            if (bytes < 1024L * 1024L)
                return string.Format(Inv, "{0:F1} KB", bytes / 1024.0);

            if (bytes < 1024L * 1024L * 1024L)
                return string.Format(Inv, "{0:F1} MB", bytes / (1024.0 * 1024.0));

            return string.Format(Inv, "{0:F1} GB", bytes / (1024.0 * 1024.0 * 1024.0));
        }

        // ------------------------------------------------------------------
        // FormatDuration
        // ------------------------------------------------------------------

        /// <summary>
        /// Formats seconds as "12m 34s". Zero or negative durations return "0s".
        /// </summary>
        internal static string FormatDuration(double seconds)
        {
            if (seconds <= 0.0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
                return "0s";

            int totalSec = (int)Math.Floor(seconds);
            int m = totalSec / 60;
            int s = totalSec % 60;

            if (m > 0)
                return string.Format(Inv, "{0}m {1:D2}s", m, s);

            return string.Format(Inv, "{0}s", s);
        }

        // ------------------------------------------------------------------
        // ComputeStorageBreakdown
        // ------------------------------------------------------------------

        /// <summary>
        /// Computes per-recording storage breakdown by stat-ing sidecar files on disk.
        /// IMPORTANT: accesses file system. Call on-demand only, never per-frame.
        /// </summary>
        internal static StorageBreakdown ComputeStorageBreakdown(Recording rec)
        {
            var bd = new StorageBreakdown();
            if (rec == null) return bd;

            bd.recordingId = rec.RecordingId ?? "";
            bd.vesselName = rec.VesselName ?? "";
            bd.pointCount = rec.Points != null ? rec.Points.Count : 0;
            bd.partEventCount = rec.PartEvents != null ? rec.PartEvents.Count : 0;
            bd.orbitSegmentCount = rec.OrbitSegments != null ? rec.OrbitSegments.Count : 0;
            bd.durationSeconds = rec.EndUT - rec.StartUT;
            if (bd.durationSeconds < 0.0 || double.IsNaN(bd.durationSeconds))
                bd.durationSeconds = 0.0;

            // Resolve file paths via RecordingPaths
            string precPath = SafeResolvePath(RecordingPaths.BuildTrajectoryRelativePath(rec.RecordingId));
            string vesselPath = SafeResolvePath(RecordingPaths.BuildVesselSnapshotRelativePath(rec.RecordingId));
            string ghostPath = SafeResolvePath(RecordingPaths.BuildGhostSnapshotRelativePath(rec.RecordingId));
            string geomPath = SafeResolvePath(RecordingPaths.BuildGhostGeometryRelativePath(rec.RecordingId));

            bd.trajectoryFileBytes = SafeGetFileSize(precPath);
            bd.vesselSnapshotBytes = SafeGetFileSize(vesselPath);
            bd.ghostSnapshotBytes = SafeGetFileSize(ghostPath);
            bd.geometryFileBytes = SafeGetFileSize(geomPath);
            bd.totalBytes = bd.trajectoryFileBytes + bd.vesselSnapshotBytes
                          + bd.ghostSnapshotBytes + bd.geometryFileBytes;

            bd.bytesPerSecond = bd.durationSeconds > 0.0
                ? bd.totalBytes / bd.durationSeconds
                : 0.0;

            return bd;
        }

        /// <summary>
        /// Resolves a relative recording path to an absolute path.
        /// Returns null if KSP context is unavailable (e.g., in unit tests).
        /// </summary>
        private static string SafeResolvePath(string relativePath)
        {
            try
            {
                return RecordingPaths.ResolveSaveScopedPath(relativePath);
            }
            catch
            {
                // KSPUtil / HighLogic may not be available in tests
                return null;
            }
        }

        /// <summary>
        /// Gets file size in bytes. Returns 0 if file doesn't exist or path is null.
        /// Logs a warning for missing files when path is non-null.
        /// </summary>
        private static long SafeGetFileSize(string path)
        {
            if (string.IsNullOrEmpty(path))
                return 0;

            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists)
                {
                    ParsekLog.Warn("Diagnostics", $"Missing sidecar file during storage scan: {path}");
                    return 0;
                }
                return fi.Length;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Diagnostics", $"Error reading file size for '{path}': {ex.Message}");
                return 0;
            }
        }

        // ------------------------------------------------------------------
        // GetCachedStorageBreakdown
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns cached StorageBreakdown if within 5s TTL, otherwise computes and caches.
        /// </summary>
        internal static StorageBreakdown GetCachedStorageBreakdown(Recording rec, double currentUT)
        {
            if (rec == null) return default;

            string id = rec.RecordingId ?? "";
            if (DiagnosticsState.storageBreakdownCache.TryGetValue(id, out var cached))
            {
                if ((currentUT - cached.cachedUT) < DiagnosticsState.StorageBreakdownCacheTtlSeconds)
                    return cached.breakdown;
            }

            var bd = ComputeStorageBreakdown(rec);
            DiagnosticsState.storageBreakdownCache[id] = (bd, currentUT);
            return bd;
        }

        // ------------------------------------------------------------------
        // ComputeSnapshot
        // ------------------------------------------------------------------

        /// <summary>
        /// Computes a full MetricSnapshot from current system state.
        /// Safe to call when systems are partially unavailable (tests, early startup).
        /// </summary>
        internal static MetricSnapshot ComputeSnapshot(double currentUT)
        {
            var snap = new MetricSnapshot();

            // --- Storage + Memory: iterate committed recordings ---
            var recordings = RecordingStore.CommittedRecordings;
            int recCount = recordings != null ? recordings.Count : 0;
            snap.recordingCount = recCount;

            var breakdowns = new StorageBreakdown[recCount];
            long totalStorage = 0;
            int totalPts = 0, totalEvts = 0, totalSegs = 0, totalSnaps = 0;

            for (int i = 0; i < recCount; i++)
            {
                var rec = recordings[i];
                if (rec == null) continue;

                // Storage breakdown (cached per-recording)
                breakdowns[i] = GetCachedStorageBreakdown(rec, currentUT);
                totalStorage += breakdowns[i].totalBytes;

                // Memory counts
                totalPts += rec.Points != null ? rec.Points.Count : 0;
                totalEvts += rec.PartEvents != null ? rec.PartEvents.Count : 0;
                totalSegs += rec.OrbitSegments != null ? rec.OrbitSegments.Count : 0;

                // Snapshot count: recordings with non-null VesselSnapshot AND GhostVisualSnapshot
                if (rec.VesselSnapshot != null && rec.GhostVisualSnapshot != null)
                    totalSnaps++;
            }

            snap.perRecording = breakdowns;
            snap.totalStorageBytes = totalStorage;

            snap.loadedTrajectoryPoints = totalPts;
            snap.loadedPartEvents = totalEvts;
            snap.loadedOrbitSegments = totalSegs;
            snap.loadedSnapshotCount = totalSnaps;

            // Memory estimation formula from design doc
            snap.estimatedMemoryBytes = (long)totalPts * 136L
                                      + (long)totalEvts * 88L
                                      + (long)totalSegs * 120L
                                      + (long)totalSnaps * 8192L;

            // --- Ghost state: read from DiagnosticsState (populated by engine in Phase 3) ---
            snap.activeGhostCount = DiagnosticsState.playbackBudget.ghostsProcessed;
            // Zone/softcap details will be filled in Phase 3 engine instrumentation.
            // For now, read whatever is in DiagnosticsState fields — they default to 0.

            // --- Timing: raw last-frame budgets ---
            snap.lastPlaybackBudget = DiagnosticsState.playbackBudget;
            snap.lastRecordingBudget = DiagnosticsState.recordingBudget;

            // --- Timing: rolling stats ---
            double wallTs = GetWallTimestamp();
            DiagnosticsState.playbackFrameHistory.ComputeStats(
                wallTs, 4.0,
                out snap.playbackAvgTotalMs,
                out snap.playbackPeakTotalMs,
                out snap.playbackWindowDurationSeconds);

            // --- Save/load timing ---
            snap.lastSaveTiming = DiagnosticsState.lastSaveTiming;
            snap.lastLoadTiming = DiagnosticsState.lastLoadTiming;

            // Cache the snapshot
            DiagnosticsState.cachedSnapshot = snap;
            DiagnosticsState.hasCachedSnapshot = true;
            DiagnosticsState.cachedSnapshotUT = currentUT;

            return snap;
        }

        // ------------------------------------------------------------------
        // GetOrComputeSnapshot
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns cached MetricSnapshot if within TTL, otherwise computes fresh.
        /// </summary>
        internal static MetricSnapshot GetOrComputeSnapshot(double currentUT)
        {
            if (DiagnosticsState.hasCachedSnapshot
                && (currentUT - DiagnosticsState.cachedSnapshotUT) < DiagnosticsState.SnapshotCacheTtlSeconds)
            {
                return DiagnosticsState.cachedSnapshot;
            }

            return ComputeSnapshot(currentUT);
        }

        // ------------------------------------------------------------------
        // FormatReport
        // ------------------------------------------------------------------

        /// <summary>
        /// Formats a MetricSnapshot into the multi-line diagnostics report.
        /// All numbers use InvariantCulture. Empty rolling buffer shows "N/A".
        /// </summary>
        internal static string FormatReport(MetricSnapshot snapshot)
        {
            var sb = new StringBuilder(2048);

            // Header
            sb.AppendLine("===== DIAGNOSTICS REPORT =====");

            // Storage summary
            sb.AppendFormat(Inv, "Storage: {0} total, {1} recordings",
                FormatBytes(snapshot.totalStorageBytes), snapshot.recordingCount);
            sb.AppendLine();

            // Per-recording lines
            if (snapshot.perRecording != null)
            {
                for (int i = 0; i < snapshot.perRecording.Length; i++)
                {
                    var r = snapshot.perRecording[i];
                    sb.AppendFormat(Inv,
                        "  rec[{0}] \"{1}\" \u2014 {2} ({3} pts, {4} evts, {5} segs) {6} {7}/s",
                        i,
                        r.vesselName ?? "",
                        FormatBytes(r.totalBytes),
                        r.pointCount,
                        r.partEventCount,
                        r.orbitSegmentCount,
                        FormatDuration(r.durationSeconds),
                        FormatBytes((long)r.bytesPerSecond));
                    sb.AppendLine();
                }
            }

            // Memory
            sb.AppendFormat(Inv,
                "Memory: ~{0} est ({1} pts, {2} evts, {3} segs, {4} snapshots)",
                FormatBytes(snapshot.estimatedMemoryBytes),
                snapshot.loadedTrajectoryPoints,
                snapshot.loadedPartEvents,
                snapshot.loadedOrbitSegments,
                snapshot.loadedSnapshotCount);
            sb.AppendLine();

            // Ghosts
            sb.AppendFormat(Inv,
                "Ghosts: {0} active (z1:{1} z2:{2}), {3} reduced, {4} simplified",
                snapshot.activeGhostCount,
                snapshot.zone1GhostCount,
                snapshot.zone2GhostCount,
                snapshot.softCapReducedCount,
                snapshot.softCapSimplifiedCount);
            sb.AppendLine();

            // Playback budget
            bool hasPlaybackData = DiagnosticsState.playbackFrameHistory != null
                                && !DiagnosticsState.playbackFrameHistory.IsEmpty;
            if (hasPlaybackData)
            {
                sb.AppendFormat(Inv,
                    "Playback budget: {0} ms avg, {1} ms peak ({2}s window), warp: {3}x",
                    snapshot.playbackAvgTotalMs.ToString("F1", Inv),
                    snapshot.playbackPeakTotalMs.ToString("F1", Inv),
                    snapshot.playbackWindowDurationSeconds.ToString("F1", Inv),
                    snapshot.lastPlaybackBudget.warpRate.ToString("F0", Inv));
            }
            else
            {
                sb.Append("Playback budget: N/A");
            }
            sb.AppendLine();

            // Recording budget
            bool recordingActive = DiagnosticsState.hasActiveGrowthRate;
            sb.AppendFormat(Inv,
                "Recording budget: {0} ms avg {1}",
                (snapshot.lastRecordingBudget.totalMicroseconds / 1000.0).ToString("F1", Inv),
                recordingActive ? "(active)" : "(inactive)");
            sb.AppendLine();

            // Save/Load
            sb.AppendFormat(Inv,
                "Save: {0} ms last ({1} dirty) | Load: {2} ms last ({3} total)",
                snapshot.lastSaveTiming.totalMilliseconds,
                snapshot.lastSaveTiming.dirtyRecordingsWritten,
                snapshot.lastLoadTiming.totalMilliseconds,
                snapshot.lastLoadTiming.recordingsProcessed);
            sb.AppendLine();

            // Health
            var h = DiagnosticsState.health;
            int totalLookups = h.waypointCacheHits + h.waypointCacheMisses;
            string hitRateStr;
            if (totalLookups > 0)
            {
                double hitRate = (h.waypointCacheHits / (double)totalLookups) * 100.0;
                hitRateStr = hitRate.ToString("F1", Inv) + "%";
            }
            else
            {
                hitRateStr = "N/A";
            }
            sb.AppendFormat(Inv,
                "Health: cache {0} hit ({1} miss of {2} lookups), spikes {3}, spawn fail {4}, builds {5}",
                hitRateStr,
                h.waypointCacheMisses,
                totalLookups,
                h.snapshotRefreshSpikes,
                h.spawnFailures,
                h.ghostBuildsThisSession);
            sb.AppendLine();

            // GC gen0
            int gcDelta = GC.CollectionCount(0) - h.gcGen0Baseline;
            sb.AppendFormat(Inv, "GC gen0: +{0} collections this session", gcDelta);
            sb.AppendLine();

            // Footer
            sb.Append("===== END REPORT =====");

            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // RunDiagnosticsReport
        // ------------------------------------------------------------------

        /// <summary>
        /// Computes full snapshot, formats report, logs each line to KSP.log, returns full string.
        /// </summary>
        internal static string RunDiagnosticsReport()
        {
            double currentUT = 0.0;
            try
            {
                // Try to read game UT if available
                var recordings = RecordingStore.CommittedRecordings;
                if (recordings != null && recordings.Count > 0)
                    currentUT = recordings[recordings.Count - 1].EndUT;
            }
            catch { /* not available outside KSP */ }

            var snapshot = ComputeSnapshot(currentUT);
            string report = FormatReport(snapshot);

            // Log each line individually so each gets the [Parsek][INFO][Diagnostics] prefix
            string[] lines = report.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!string.IsNullOrEmpty(lines[i]))
                    ParsekLog.Info("Diagnostics", lines[i]);
            }

            return report;
        }

        // ------------------------------------------------------------------
        // Budget threshold checks (testable static methods)
        // ------------------------------------------------------------------

        /// <summary>
        /// Playback budget warning threshold in milliseconds.
        /// At 60 FPS, a frame is 16.6ms — 8ms is about half the budget.
        /// </summary>
        internal const double PlaybackBudgetThresholdMs = 8.0;

        /// <summary>
        /// Recording budget warning threshold in milliseconds.
        /// </summary>
        internal const double RecordingBudgetThresholdMs = 4.0;

        /// <summary>
        /// Checks the playback budget and emits a rate-limited WARN if exceeded.
        /// Called by GhostPlaybackEngine after each frame. Pure static, testable.
        /// </summary>
        internal static void CheckPlaybackBudgetThreshold(long totalMicroseconds, int ghostsProcessed, float warpRate)
        {
            double totalMs = totalMicroseconds / 1000.0;
            if (totalMs > PlaybackBudgetThresholdMs)
            {
                ParsekLog.WarnRateLimited("Diagnostics", "playback-budget",
                    string.Format(Inv,
                        "Playback frame budget exceeded: {0}ms ({1} ghosts, warp: {2}x)",
                        totalMs.ToString("F1", Inv), ghostsProcessed, warpRate.ToString("F0", Inv)),
                    30.0);
            }
        }

        /// <summary>
        /// Checks the recording budget and emits a rate-limited WARN if exceeded.
        /// Called by PhysicsFramePatch after each physics frame. Pure static, testable.
        /// </summary>
        internal static void CheckRecordingBudgetThreshold(long totalMicroseconds, string vesselName)
        {
            double totalMs = totalMicroseconds / 1000.0;
            if (totalMs > RecordingBudgetThresholdMs)
            {
                ParsekLog.WarnRateLimited("Diagnostics", "recording-budget",
                    string.Format(Inv,
                        "Recording frame exceeded budget: {0}ms for vessel \"{1}\"",
                        totalMs.ToString("F2", Inv), vesselName ?? "?"),
                    30.0);
            }
        }

        // ------------------------------------------------------------------
        // EmitSceneLoadSnapshot
        // ------------------------------------------------------------------

        /// <summary>
        /// Emits a one-shot Verbose log line with memory snapshot and ghost count after scene load.
        /// Designed for once-per-scene-load usage — no rate limiting needed.
        /// Pure static method, testable without Unity.
        /// </summary>
        internal static void EmitSceneLoadSnapshot(int recordingCount, string sceneName)
        {
            var recordings = RecordingStore.CommittedRecordings;
            int totalPts = 0, totalEvts = 0, totalSegs = 0;
            if (recordings != null)
            {
                for (int i = 0; i < recordings.Count; i++)
                {
                    var rec = recordings[i];
                    if (rec == null) continue;
                    totalPts += rec.Points != null ? rec.Points.Count : 0;
                    totalEvts += rec.PartEvents != null ? rec.PartEvents.Count : 0;
                    totalSegs += rec.OrbitSegments != null ? rec.OrbitSegments.Count : 0;
                }
            }

            long estimatedMemory = (long)totalPts * 136L + (long)totalEvts * 88L + (long)totalSegs * 120L;
            int ghostCount = DiagnosticsState.playbackBudget.ghostsProcessed;

            ParsekLog.Verbose("Diagnostics",
                string.Format(Inv,
                    "Scene load complete ({0}): {1} recordings, ~{2} memory est ({3} pts, {4} evts, {5} segs), {6} ghosts",
                    sceneName ?? "?",
                    recordingCount,
                    FormatBytes(estimatedMemory),
                    totalPts, totalEvts, totalSegs,
                    ghostCount));
        }

        /// <summary>
        /// Reset test-only state. Call from test cleanup.
        /// </summary>
        internal static void ResetForTesting()
        {
            ClockSource = null;
        }
    }
}
