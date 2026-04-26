using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Computes diagnostics metrics and formats the human-readable diagnostics report.
    /// All methods are static. File-system access is on-demand only (never per-frame).
    /// All numeric formatting uses InvariantCulture.
    /// </summary>
    // [ERS-exempt — Phase 3] Diagnostics reports raw storage + memory footprint
    // across every stored recording (including NotCommitted / superseded); the
    // per-snapshot breakdowns array is keyed by raw index. ERS filtering would
    // hide real on-disk/in-memory cost from the diagnostics surface (admin/debug
    // per design §3.4 "raw-count display").
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
            string readablePrecPath = SafeResolvePath(RecordingPaths.BuildReadableTrajectoryMirrorRelativePath(rec.RecordingId));
            string readableVesselPath = SafeResolvePath(RecordingPaths.BuildReadableVesselSnapshotMirrorRelativePath(rec.RecordingId));
            string readableGhostPath = SafeResolvePath(RecordingPaths.BuildReadableGhostSnapshotMirrorRelativePath(rec.RecordingId));

            // .prec is always written. _vessel.craft and _ghost.craft are written
            // only when their in-memory snapshot is non-null. For tree continuation
            // recordings, ghost-only-merged debris, etc., the snapshot is legitimately
            // null and no file ever existed — querying with the warning enabled
            // would emit a false-positive "Missing sidecar file" warning per scan (#262).
            bd.trajectoryFileBytes = SafeGetFileSize(precPath, warnIfMissing: true);
            bd.vesselSnapshotBytes = SafeGetFileSize(vesselPath,
                warnIfMissing: ShouldExpectSidecarFile(rec, SidecarFileType.VesselSnapshot));
            bd.ghostSnapshotBytes = SafeGetFileSize(ghostPath,
                warnIfMissing: ShouldExpectSidecarFile(rec, SidecarFileType.GhostSnapshot));
            bd.readableMirrorBytes =
                SafeGetFileSize(readablePrecPath, warnIfMissing: false) +
                SafeGetFileSize(readableVesselPath, warnIfMissing: false) +
                SafeGetFileSize(readableGhostPath, warnIfMissing: false);
            bd.totalBytes = bd.trajectoryFileBytes + bd.vesselSnapshotBytes
                          + bd.ghostSnapshotBytes + bd.readableMirrorBytes;

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
        /// Sidecar file types written by RecordingStore.SaveRecordingFiles.
        /// Used by ShouldExpectSidecarFile to gate "missing file" warnings.
        /// </summary>
        internal enum SidecarFileType
        {
            Trajectory,
            VesselSnapshot,
            GhostSnapshot,
        }

        /// <summary>
        /// Pure predicate: should this recording's sidecar file exist on disk?
        /// Mirrors RecordingStore.SaveRecordingFiles' write conditions:
        /// - .prec is always written
        /// - _vessel.craft only when rec.VesselSnapshot != null
        /// - _ghost.craft only when ghost snapshot mode resolves to Separate
        /// Tree continuation recordings, ghost-only-merged debris, and chain
        /// mid-segments legitimately have null snapshots and no sidecar file.
        /// </summary>
        internal static bool ShouldExpectSidecarFile(Recording rec, SidecarFileType type)
        {
            if (rec == null) return false;
            switch (type)
            {
                case SidecarFileType.Trajectory:     return true;
                case SidecarFileType.VesselSnapshot: return rec.VesselSnapshot != null;
                case SidecarFileType.GhostSnapshot:
                    return RecordingStore.GetExpectedGhostSnapshotMode(rec) == GhostSnapshotMode.Separate;
                default: return false;
            }
        }

        /// <summary>
        /// Gets file size in bytes. Returns 0 if file doesn't exist or path is null.
        /// Emits a "Missing sidecar file" warning only when warnIfMissing is true —
        /// callers gate this for files that legitimately may not exist (#262).
        /// </summary>
        internal static long SafeGetFileSize(string path, bool warnIfMissing)
        {
            if (string.IsNullOrEmpty(path))
                return 0;

            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists)
                {
                    if (warnIfMissing)
                        ParsekLog.WarnRateLimited(
                            "Diagnostics",
                            "missing-sidecar|" + path,
                            $"Missing sidecar file during storage scan: {path}",
                            30.0);
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

            // --- Ghost state: derive live LOD counts from the engine when available ---
            var flight = ParsekFlight.Instance;
            var engine = flight?.Engine;
            GhostObservability ghostObs;
            if (engine != null)
            {
                ghostObs = engine.CaptureGhostObservability();
                PopulateGhostStateCounts(
                    ref snap,
                    engine.ghostStates,
                    engine.overlapGhosts,
                    flight.WatchedRecordingIndexForDiagnostics,
                    flight.WatchedLoopCycleIndexForDiagnostics,
                    ghostObs.activePrimaryGhostCount + ghostObs.activeOverlapGhostCount);
            }
            else
            {
                ghostObs = DiagnosticsState.playbackBudget.ghostObservability;
                snap.activeGhostCount = ghostObs.activePrimaryGhostCount + ghostObs.activeOverlapGhostCount;
                snap.activeOverlapGhostCount = ghostObs.activeOverlapGhostCount;
            }
            snap.zone1GhostCount = ghostObs.zone1GhostCount;
            snap.zone2GhostCount = ghostObs.zone2GhostCount;
            snap.softCapReducedCount = ghostObs.softCapReducedCount;
            snap.softCapSimplifiedCount = ghostObs.softCapSimplifiedCount;
            snap.ghostsWithEngineFx = ghostObs.ghostsWithEngineFx;
            snap.engineModuleCount = ghostObs.engineModuleCount;
            snap.engineParticleSystemCount = ghostObs.engineParticleSystemCount;
            snap.ghostsWithRcsFx = ghostObs.ghostsWithRcsFx;
            snap.rcsModuleCount = ghostObs.rcsModuleCount;
            snap.rcsParticleSystemCount = ghostObs.rcsParticleSystemCount;

            // --- Timing: raw last-frame budgets ---
            snap.lastPlaybackBudget = DiagnosticsState.playbackBudget;
            snap.lastRecordingBudget = DiagnosticsState.recordingBudget;

            // --- Timing: rolling stats ---
            double wallTs = GetWallTimestamp();
            DiagnosticsState.playbackFrameHistory.ComputeStats(
                wallTs, 4.0,
                out snap.playbackAvgTotalMs,
                out snap.playbackPeakTotalMs,
                out snap.playbackWindowDurationSeconds,
                out snap.playbackEntriesInWindow);

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
                "Ghosts: {0} active ({1} overlap), {2} full, {3} reduced, {4} hidden, {5} watched override",
                snapshot.activeGhostCount,
                snapshot.activeOverlapGhostCount,
                snapshot.fullGhostCount,
                snapshot.reducedGhostCount,
                snapshot.hiddenGhostCount,
                snapshot.watchedOverrideGhostCount);
            sb.AppendLine();

            sb.AppendFormat(Inv,
                "FX: engine {0} ghosts / {1} modules / {2} systems | RCS {3} ghosts / {4} modules / {5} systems",
                snapshot.ghostsWithEngineFx,
                snapshot.engineModuleCount,
                snapshot.engineParticleSystemCount,
                snapshot.ghostsWithRcsFx,
                snapshot.rcsModuleCount,
                snapshot.rcsParticleSystemCount);
            sb.AppendLine();

            // Playback budget — read from snapshot, not live buffer.
            // The snapshot may have been computed when the buffer had entries that were
            // all outside the 4 s rolling window; in that case avg/peak/duration are 0
            // and entriesInWindow == 0, which is the correct N/A signal (#261).
            bool hasPlaybackData = snapshot.playbackEntriesInWindow > 0;
            if (hasPlaybackData)
            {
                sb.AppendFormat(Inv,
                    "Playback budget: {0} ms avg, {1} ms peak ({2}s window), spawn {3} ms, destroy {4} ms, warp: {5}x",
                    snapshot.playbackAvgTotalMs.ToString("F1", Inv),
                    snapshot.playbackPeakTotalMs.ToString("F1", Inv),
                    snapshot.playbackWindowDurationSeconds.ToString("F1", Inv),
                    (snapshot.lastPlaybackBudget.spawnMicroseconds / 1000.0).ToString("F1", Inv),
                    (snapshot.lastPlaybackBudget.destroyMicroseconds / 1000.0).ToString("F1", Inv),
                    snapshot.lastPlaybackBudget.warpRate.ToString("F0", Inv));
            }
            else
            {
                sb.AppendFormat(Inv,
                    "Playback budget: N/A (spawn {0} ms, destroy {1} ms, warp: {2}x)",
                    (snapshot.lastPlaybackBudget.spawnMicroseconds / 1000.0).ToString("F1", Inv),
                    (snapshot.lastPlaybackBudget.destroyMicroseconds / 1000.0).ToString("F1", Inv),
                    snapshot.lastPlaybackBudget.warpRate.ToString("F0", Inv));
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
            // Bug #450 B3: `deferred` counts every spawn-or-rehydrate that entered
            // the lazy-build queue (one increment per visual build event, NOT per
            // unique trajectory). `buildsAvoided` = deferred - built is the number
            // of lazy-build EVENTS that didn't fire this session — each avoided
            // event is a real ~7 ms saving pre-B3 would have paid. Note that a
            // single recording that unloads and rehydrates N times contributes N to
            // deferred; if it only ever reaches atmosphere once, buildsAvoided =
            // N-1, which IS correct accounting (pre-B3 rebuilt the FX on every
            // spawn+rehydrate). The label is "buildsAvoided" rather than
            // "neverBuilt" to avoid implying unique-trajectory semantics.
            int reentryBuildsAvoided = h.reentryFxDeferredThisSession - h.reentryFxBuildsThisSession;
            if (reentryBuildsAvoided < 0) reentryBuildsAvoided = 0;
            sb.AppendFormat(Inv,
                "Health: cache {0} hit ({1} miss of {2} lookups), spikes {3}, spawn fail {4}, builds {5} destroys {6}, reentryFx built {7} skipped {8} deferred {9} buildsAvoided {10}",
                hitRateStr,
                h.waypointCacheMisses,
                totalLookups,
                h.snapshotRefreshSpikes,
                h.spawnFailures,
                h.ghostBuildsThisSession,
                h.ghostDestroysThisSession,
                h.reentryFxBuildsThisSession,
                h.reentryFxSkippedThisSession,
                h.reentryFxDeferredThisSession,
                reentryBuildsAvoided);
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

        /// <summary>
        /// Populates live ghost-state counts for the diagnostics report from the engine's
        /// active primary/overlap ghost dictionaries.
        /// </summary>
        internal static void PopulateGhostStateCounts(
            ref MetricSnapshot snap,
            IReadOnlyDictionary<int, GhostPlaybackState> primaryStates,
            IReadOnlyDictionary<int, List<GhostPlaybackState>> overlapStates,
            int watchedIndex,
            long watchedLoopCycleIndex,
            int fallbackActiveGhostCount)
        {
            if (primaryStates == null && overlapStates == null)
            {
                snap.activeGhostCount = fallbackActiveGhostCount;
                return;
            }

            if (primaryStates != null)
            {
                foreach (var kvp in primaryStates)
                {
                    if (kvp.Value == null) continue;
                    if (GhostPlaybackEngine.HasLoadedGhostVisuals(kvp.Value))
                        snap.activeGhostCount++;
                    AccumulateGhostTierCounts(
                        kvp.Key, kvp.Value, watchedIndex, watchedLoopCycleIndex,
                        ref snap.fullGhostCount,
                        ref snap.reducedGhostCount,
                        ref snap.hiddenGhostCount,
                        ref snap.watchedOverrideGhostCount);
                }
            }

            if (overlapStates != null)
            {
                foreach (var kvp in overlapStates)
                {
                    var overlaps = kvp.Value;
                    if (overlaps == null) continue;

                    for (int i = 0; i < overlaps.Count; i++)
                    {
                        var state = overlaps[i];
                        if (state == null) continue;

                        if (GhostPlaybackEngine.HasLoadedGhostVisuals(state))
                        {
                            snap.activeGhostCount++;
                            snap.activeOverlapGhostCount++;
                        }
                        AccumulateGhostTierCounts(
                            kvp.Key, state, watchedIndex, watchedLoopCycleIndex,
                            ref snap.fullGhostCount,
                            ref snap.reducedGhostCount,
                            ref snap.hiddenGhostCount,
                            ref snap.watchedOverrideGhostCount);
                    }
                }
            }
        }

        /// <summary>
        /// Classifies a single live ghost state into the shipped distance LOD buckets.
        /// Watched override is reported separately and counts as full fidelity.
        /// </summary>
        internal static void AccumulateGhostTierCounts(
            int recordingIndex,
            GhostPlaybackState state,
            int watchedIndex,
            long watchedLoopCycleIndex,
            ref int fullGhostCount,
            ref int reducedGhostCount,
            ref int hiddenGhostCount,
            ref int watchedOverrideGhostCount)
        {
            if (state == null) return;

            double renderDistance = GetEffectiveRenderDistance(state);

            bool watchedOverrideActive =
                GhostPlaybackLogic.IsProtectedGhost(
                    watchedIndex, watchedLoopCycleIndex,
                    recordingIndex, state.loopCycleIndex) &&
                renderDistance >= DistanceThresholds.PhysicsBubbleMeters;

            if (watchedOverrideActive)
            {
                watchedOverrideGhostCount++;
                fullGhostCount++;
                return;
            }

            if (state.simplified
                || state.currentZone == RenderingZone.Beyond
                || renderDistance >= DistanceThresholds.GhostFlight.LoopSimplifiedMeters)
            {
                hiddenGhostCount++;
                return;
            }

            if (state.distanceLodReduced
                || state.fidelityReduced
                || renderDistance >= DistanceThresholds.PhysicsBubbleMeters)
            {
                reducedGhostCount++;
                return;
            }

            fullGhostCount++;
        }

        private static double GetEffectiveRenderDistance(GhostPlaybackState state)
        {
            if (state == null)
                return double.MaxValue;

            if (!double.IsNaN(state.lastRenderDistance)
                && !double.IsInfinity(state.lastRenderDistance)
                && state.lastRenderDistance >= 0)
            {
                return state.lastRenderDistance;
            }

            return state.lastDistance;
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
        /// Bug #450: minimum single-spawn cost (in microseconds) required before the
        /// one-shot spawn-build breakdown WARN consumes its latch. Matches the 15 ms
        /// figure the #450 todo entry proposes as the threshold where a spawn alone
        /// exceeds the 8 ms frame budget enough to constitute the "bimodal" case a
        /// count cap cannot cover. Gating on `spawnMaxMicroseconds` (not
        /// `spawnMicroseconds` aggregate) means an incidental cheap prewarm or
        /// watch-mode spawn on an otherwise non-spawn hitch cannot burn the
        /// session's only #450 sample before the real regression fires.
        /// </summary>
        internal const long BuildBreakdownMinHeaviestSpawnMicroseconds = 15_000;

        /// <summary>
        /// Bug #460: minimum <c>mainLoopMicroseconds</c> required for the mainLoop
        /// breakdown one-shot WARN to consume its latch. Set to 10 ms so the gate
        /// sits above (a) the 8 ms budget threshold (otherwise every exceeded frame
        /// would qualify) and (b) the pre-B3 smoke's already-captured 8.55 ms mainLoop
        /// sample, but below the 17-25 ms range observed in the post-B3 playtest
        /// (so all three known spikes would have tripped).
        /// </summary>
        internal const long MainLoopBreakdownMinMainLoopMicroseconds = 10_000;

        /// <summary>
        /// Bug #460: maximum <c>spawnMicroseconds</c> allowed before the #460 latch
        /// declines to fire. A frame whose hitch includes any non-trivial spawn work
        /// (&gt;= 1 ms) belongs to #414's / #450's per-ghost territory even if
        /// <c>ghostsProcessed == 0</c> (e.g. a trajectory whose state was null at
        /// frame start spawned and was destroyed same-frame without ever incrementing
        /// the counter). Keeps the #460 latch reserved for spikes that no per-ghost
        /// instrumentation explains.
        /// </summary>
        internal const long MainLoopBreakdownMaxSpawnMicroseconds = 1_000;

        /// <summary>
        /// Bug #460: mirror of <see cref="MainLoopBreakdownMaxSpawnMicroseconds"/> for
        /// the destroy stopwatch. Symmetric suppression so a same-frame destroy on a
        /// ghost that never became active does not burn the #460 latch.
        /// </summary>
        internal const long MainLoopBreakdownMaxDestroyMicroseconds = 1_000;

        /// <summary>
        /// Bug #581: minimum per-phase contribution required for the hybrid latch
        /// to recognise a frame as a genuine mainLoop+spawn hybrid spike. Without
        /// this floor, the gate (which is just "below #450 spawn threshold AND
        /// below #460 mainLoop threshold") matches default-phase frames, deferred-
        /// event-dominated spikes, and zero-spawn / zero-mainLoop spikes — any of
        /// which would burn the one-shot latch on a non-hybrid frame and rob the
        /// session of the diagnostic snapshot the latch is supposed to capture.
        /// 1 ms (1 000 µs) is roughly an order of magnitude below either of the
        /// individual thresholds, so it admits the captured 2026-04-25 spike
        /// (mainLoop 7.51 ms + spawn 3.44 ms) and any plausible hybrid below the
        /// #450/#460 lines, while rejecting per-frame measurement noise.
        /// </summary>
        internal const long HybridBreakdownMinPhaseMicroseconds = 1_000;

        /// <summary>
        /// One-shot latch for the bug #414 per-phase breakdown log. Flipped true the first
        /// time a playback-budget-exceeded warning fires in the session; once latched, the
        /// breakdown is never re-emitted even on subsequent spikes. This keeps the steady-state
        /// cost at zero (no extra log lines, no allocations) while preserving the one
        /// diagnostic snapshot that localizes which phase blew the budget.
        /// </summary>
        private static bool s_playbackBreakdownOneShotFired;

        /// <summary>
        /// Bug #450: independent latch for the spawn-build sub-phase WARN. Separate from the
        /// #414 latch so the build breakdown fires on the next spike after #450 rollout even
        /// when the session's first budget-exceeded frame already consumed #414's latch.
        /// </summary>
        private static bool s_buildBreakdownOneShotFired;

        /// <summary>
        /// Bug #460: independent latch for the mainLoop-dominated zero-ghost breakdown WARN.
        /// Separate from the #414 / #450 latches so a mid-session rollout of #460 still
        /// captures the next qualifying spike even if the session's first budget-exceeded
        /// frame(s) already consumed either predecessor latch. Fires at most once per session,
        /// only when every gate in
        /// <see cref="CheckPlaybackBudgetThresholdWithBreakdown"/>'s #460 branch holds: budget
        /// exceeded, <c>ghostsProcessed == 0</c>, <c>spawn &amp; destroy &lt; 1 ms</c>,
        /// <c>mainLoop &gt;= 10 ms</c>, and mainLoop is strictly the largest non-spawn/destroy
        /// phase. The single WARN it emits is diagnostic plumbing — no user-observable
        /// behaviour changes.
        /// </summary>
        private static bool s_mainLoopBreakdownOneShotFired;

        /// <summary>
        /// Bug #581: independent latch for the hybrid-spike breakdown WARN. The
        /// 2026-04-25 marker-validator-fix playtest emitted a single
        /// budget-exceeded frame at 11.6 ms whose phase shape was
        /// <c>mainLoop=7.51ms spawn=3.44ms (built=1 max=3.44ms)</c> — partly
        /// mainLoop work + partly a single non-trivial spawn, but neither
        /// component large enough on its own to fit the existing #450 (gate:
        /// <c>spawnMaxMicroseconds &gt;= 15 ms</c>) or #460 (gate:
        /// <c>mainLoop &gt;= 10 ms</c> AND <c>spawn &lt; 1 ms</c>) sub-breakdowns.
        /// The session therefore captured the generic #414 breakdown but no
        /// sub-phase attribution that would let Phase B target a fix. This
        /// latch fires on the next budget-exceeded frame that falls in the
        /// gap between #450 and #460 — heaviest spawn under the 15 ms #450
        /// threshold AND mainLoop under the 10 ms #460 floor — so the next
        /// hybrid breach lands a sub-breakdown line that itemises the same
        /// fields as #460's breakdown plus the spawn ratios. Independent of
        /// the prior three latches so it can fire even after they are consumed.
        /// </summary>
        private static bool s_hybridBreakdownOneShotFired;

        /// <summary>
        /// Checks the playback budget and emits a rate-limited WARN if exceeded.
        /// Called by GhostPlaybackEngine after each frame. Pure static, testable.
        /// Kept for back-compat with existing call sites and tests that have no phase breakdown.
        /// </summary>
        internal static void CheckPlaybackBudgetThreshold(long totalMicroseconds, int ghostsProcessed, float warpRate)
        {
            CheckPlaybackBudgetThresholdWithBreakdown(
                totalMicroseconds, ghostsProcessed, warpRate, default);
        }

        /// <summary>
        /// Bug #414 instrumentation: same as <see cref="CheckPlaybackBudgetThreshold"/> but
        /// additionally emits a one-shot per-phase breakdown WARN the very first time the
        /// budget is exceeded in the session. The intent is NOT to fix the spike — it is to
        /// expose which sub-phase (main loop, spawn, destroy, explosion cleanup, deferred
        /// event invocations, observability capture) is actually responsible, so the root
        /// cause can be localized on the next playtest. After the first fire the latch is
        /// set and subsequent exceeded frames behave exactly like the legacy path.
        /// Pure static, testable.
        /// </summary>
        internal static void CheckPlaybackBudgetThresholdWithBreakdown(
            long totalMicroseconds, int ghostsProcessed, float warpRate, PlaybackBudgetPhases phases)
        {
            double totalMs = totalMicroseconds / 1000.0;
            if (totalMs <= PlaybackBudgetThresholdMs)
                return;

            ParsekLog.WarnRateLimited("Diagnostics", "playback-budget",
                string.Format(Inv,
                    "Playback frame budget exceeded: {0}ms ({1} ghosts, warp: {2}x)",
                    totalMs.ToString("F1", Inv), ghostsProcessed, warpRate.ToString("F0", Inv)),
                30.0);

            if (!s_playbackBreakdownOneShotFired)
            {
                // One-shot: emit the phase breakdown on the very first exceeded frame. The breakdown
                // is a plain WARN (not rate-limited) so it always lands next to the triggering
                // budget-exceeded line in KSP.log regardless of the rate limiter's state for any
                // future spikes. See bug #414 in docs/dev/todo-and-known-bugs.md.
                s_playbackBreakdownOneShotFired = true;
                ParsekLog.Warn("Diagnostics",
                    string.Format(Inv,
                        "Playback budget breakdown (one-shot, first exceeded frame): total={0}ms"
                        + " mainLoop={1}ms spawn={2}ms (built={13} throttled={14} max={15}ms)"
                        + " destroy={3}ms explosionCleanup={4}ms"
                        + " deferredCreated={5}ms ({6} evts) deferredCompleted={7}ms ({8} evts)"
                        + " observabilityCapture={9}ms trajectories={10} ghosts={11} warp={12}x",
                        totalMs.ToString("F1", Inv),
                        (phases.mainLoopMicroseconds / 1000.0).ToString("F2", Inv),
                        (phases.spawnMicroseconds / 1000.0).ToString("F2", Inv),
                        (phases.destroyMicroseconds / 1000.0).ToString("F2", Inv),
                        (phases.explosionCleanupMicroseconds / 1000.0).ToString("F2", Inv),
                        (phases.deferredCreatedEventsMicroseconds / 1000.0).ToString("F2", Inv),
                        phases.createdEventsFired,
                        (phases.deferredCompletedEventsMicroseconds / 1000.0).ToString("F2", Inv),
                        phases.completedEventsFired,
                        (phases.observabilityCaptureMicroseconds / 1000.0).ToString("F2", Inv),
                        phases.trajectoriesIterated,
                        ghostsProcessed,
                        warpRate.ToString("F0", Inv),
                        phases.spawnsAttempted,
                        phases.spawnsThrottled,
                        (phases.spawnMaxMicroseconds / 1000.0).ToString("F2", Inv)));
            }

            // Bug #450: emit the spawn-build sub-phase breakdown on its own one-shot latch.
            // Gate on `spawnMaxMicroseconds >= BuildBreakdownMinHeaviestSpawnMicroseconds`
            // (not just `spawnMicroseconds > 0`) so an incidental cheap prewarm or watch-
            // mode spawn on a frame whose hitch was driven by something else cannot burn
            // the session's only #450 sample before the real single-spawn regression fires.
            // 15 ms is the threshold the #450 todo entry proposes as the bimodal line —
            // above it, at least one spawn is, on its own, eating most of the frame budget,
            // which is exactly the case Phase A is meant to diagnose. The latch itself is
            // independent of #414's so Phase A collects data even when the session's first
            // spike already consumed the #414 latch before #450 rolled out.
            if (!s_buildBreakdownOneShotFired
                && phases.spawnMaxMicroseconds >= BuildBreakdownMinHeaviestSpawnMicroseconds)
            {
                s_buildBreakdownOneShotFired = true;
                ParsekLog.Warn("Diagnostics",
                    string.Format(Inv,
                        "Playback spawn build breakdown (one-shot): "
                        + "sum[snapshot={0}ms timeline={1}ms dicts={2}ms reentry={3}ms other={4}ms] "
                        + "heaviestSpawn[type={5} snapshot={6}ms timeline={7}ms dicts={8}ms reentry={9}ms other={10}ms total={11}ms]",
                        (phases.buildSnapshotResolveMicroseconds / 1000.0).ToString("F2", Inv),
                        (phases.buildTimelineFromSnapshotMicroseconds / 1000.0).ToString("F2", Inv),
                        (phases.buildDictionariesMicroseconds / 1000.0).ToString("F2", Inv),
                        (phases.buildReentryFxMicroseconds / 1000.0).ToString("F2", Inv),
                        (phases.buildOtherMicroseconds / 1000.0).ToString("F2", Inv),
                        phases.heaviestSpawnBuildType.ToLogToken(),
                        (phases.heaviestSpawnSnapshotResolveMicroseconds / 1000.0).ToString("F2", Inv),
                        (phases.heaviestSpawnTimelineFromSnapshotMicroseconds / 1000.0).ToString("F2", Inv),
                        (phases.heaviestSpawnDictionariesMicroseconds / 1000.0).ToString("F2", Inv),
                        (phases.heaviestSpawnReentryFxMicroseconds / 1000.0).ToString("F2", Inv),
                        (phases.heaviestSpawnOtherMicroseconds / 1000.0).ToString("F2", Inv),
                        ((phases.heaviestSpawnSnapshotResolveMicroseconds
                            + phases.heaviestSpawnTimelineFromSnapshotMicroseconds
                            + phases.heaviestSpawnDictionariesMicroseconds
                            + phases.heaviestSpawnReentryFxMicroseconds
                            + phases.heaviestSpawnOtherMicroseconds) / 1000.0).ToString("F2", Inv)));
            }

            // Bug #460: mainLoop-dominated zero-ghost breakdown. Independent of #414 and #450
            // latches. Fires on the next frame that is NOT explained by any per-ghost
            // instrumentation (neither ongoing rendering nor a same-frame spawn/destroy) and
            // whose mainLoop phase is strictly the largest non-spawn/destroy contributor.
            // See plan-460-mainloop-breakdown.md for the gating rationale (7 conditions,
            // each of which would otherwise let the latch burn on the wrong kind of spike).
            // Dominance check: mainLoop must exceed every OTHER non-spawn/destroy
            // bucket. Deferred events are summed as a single "deferred" phase
            // because a Phase B fix that targets deferred-event processing would
            // attack both halves together — a spike where mainLoop=11ms but
            // deferredCreated+deferredCompleted=14ms is really a deferred-events
            // spike, not a mainLoop one, even though no single deferred bucket
            // exceeds mainLoop. Observability capture and explosion cleanup stay
            // per-bucket because they are independent code paths.
            long deferredEventsTotalMicroseconds =
                phases.deferredCreatedEventsMicroseconds + phases.deferredCompletedEventsMicroseconds;
            if (!s_mainLoopBreakdownOneShotFired
                && ghostsProcessed == 0
                && phases.spawnMicroseconds < MainLoopBreakdownMaxSpawnMicroseconds
                && phases.destroyMicroseconds < MainLoopBreakdownMaxDestroyMicroseconds
                && phases.mainLoopMicroseconds >= MainLoopBreakdownMinMainLoopMicroseconds
                && phases.mainLoopMicroseconds > deferredEventsTotalMicroseconds
                && phases.mainLoopMicroseconds > phases.observabilityCaptureMicroseconds
                && phases.mainLoopMicroseconds > phases.explosionCleanupMicroseconds)
            {
                s_mainLoopBreakdownOneShotFired = true;

                // Per-dispatch means. Render "n/a" sentinels for zero-divisor cases so a
                // pathological frame that somehow spent 10+ ms in the main loop with zero
                // trajectories / overlap iterations surfaces visibly instead of reporting a
                // misleading zero. Integer divide is safe: denominators above zero are
                // checked explicitly.
                long dispatchCount = (long)phases.trajectoriesIterated
                    + (long)phases.overlapGhostIterationCount;
                string meanPerTrajStr = phases.trajectoriesIterated > 0
                    ? ((double)phases.mainLoopMicroseconds / phases.trajectoriesIterated)
                        .ToString("F2", Inv) + "us"
                    : "n/a";
                string meanPerDispatchStr = dispatchCount > 0
                    ? ((double)phases.mainLoopMicroseconds / dispatchCount)
                        .ToString("F2", Inv) + "us"
                    : "n/a";

                ParsekLog.Warn("Diagnostics",
                    string.Format(Inv,
                        "Playback mainLoop breakdown (one-shot, first mainLoop-dominated spike):"
                        + " total={0}ms mainLoop={1}ms trajectories={2} overlapIterations={3}"
                        + " meanPerTraj={4} meanPerDispatch={5}"
                        + " deferredCreated={6}ms ({7} evts) deferredCompleted={8}ms ({9} evts)"
                        + " observabilityCapture={10}ms explosionCleanup={11}ms"
                        + " spawn={12}ms destroy={13}ms ghosts={14} warp={15}x",
                        totalMs.ToString("F1", Inv),
                        (phases.mainLoopMicroseconds / 1000.0).ToString("F2", Inv),
                        phases.trajectoriesIterated,
                        phases.overlapGhostIterationCount,
                        meanPerTrajStr,
                        meanPerDispatchStr,
                        (phases.deferredCreatedEventsMicroseconds / 1000.0).ToString("F2", Inv),
                        phases.createdEventsFired,
                        (phases.deferredCompletedEventsMicroseconds / 1000.0).ToString("F2", Inv),
                        phases.completedEventsFired,
                        (phases.observabilityCaptureMicroseconds / 1000.0).ToString("F2", Inv),
                        (phases.explosionCleanupMicroseconds / 1000.0).ToString("F2", Inv),
                        (phases.spawnMicroseconds / 1000.0).ToString("F2", Inv),
                        (phases.destroyMicroseconds / 1000.0).ToString("F2", Inv),
                        ghostsProcessed,
                        warpRate.ToString("F0", Inv)));
            }

            // Bug #581: hybrid-spike breakdown. The 2026-04-25 playtest's only
            // budget-exceeded frame was total=11.6ms mainLoop=7.51ms
            // spawn=3.44ms (built=1 max=3.44ms). That falls in a diagnostic
            // gap between #450 (gate: spawnMax >= 15ms) and #460 (gate:
            // mainLoop >= 10ms AND spawn < 1ms): neither sub-bucket is large
            // enough to clear its own threshold, so without this latch the
            // session captures the generic #414 breakdown but no Phase-B
            // attribution. Fires when the spike is in that gap AND the
            // mainLoop+spawn pair has positive evidence of genuine hybrid
            // dominance — both phases must individually be ≥ 1 ms (rejecting
            // default / pre-init frames, observability-only spikes, and
            // zero-spawn or zero-mainLoop frames where one bucket is silent),
            // and their sum must exceed every other non-spawn/destroy bucket
            // (rejecting deferred-event, observability-capture, and explosion-
            // cleanup-dominated spikes that happen to leave both mainLoop and
            // spawn under threshold). Without these positive gates, the bare
            // "<#450 AND <#460" floor matches every above-budget frame whose
            // spike comes from any other bucket, and the one-shot latch can
            // be consumed before a real hybrid frame appears (review note on
            // PR #553). Independent of the prior three latches so a session
            // that already burned them on bigger spikes can still capture the
            // next hybrid breach.
            long deferredEventsTotalMicrosecondsForHybrid =
                phases.deferredCreatedEventsMicroseconds + phases.deferredCompletedEventsMicroseconds;
            long hybridPairMicroseconds =
                phases.mainLoopMicroseconds + phases.spawnMicroseconds;
            if (!s_hybridBreakdownOneShotFired
                && phases.spawnMaxMicroseconds < BuildBreakdownMinHeaviestSpawnMicroseconds
                && phases.mainLoopMicroseconds < MainLoopBreakdownMinMainLoopMicroseconds
                && phases.mainLoopMicroseconds >= HybridBreakdownMinPhaseMicroseconds
                && phases.spawnMicroseconds >= HybridBreakdownMinPhaseMicroseconds
                && hybridPairMicroseconds > deferredEventsTotalMicrosecondsForHybrid
                && hybridPairMicroseconds > phases.observabilityCaptureMicroseconds
                && hybridPairMicroseconds > phases.explosionCleanupMicroseconds)
            {
                s_hybridBreakdownOneShotFired = true;

                double mainLoopMs = phases.mainLoopMicroseconds / 1000.0;
                double spawnMs = phases.spawnMicroseconds / 1000.0;
                string mainLoopFractionStr = totalMs > 0
                    ? (100.0 * mainLoopMs / totalMs).ToString("F0", Inv) + "%"
                    : "n/a";
                string spawnFractionStr = totalMs > 0
                    ? (100.0 * spawnMs / totalMs).ToString("F0", Inv) + "%"
                    : "n/a";

                ParsekLog.Warn("Diagnostics",
                    string.Format(Inv,
                        "Playback hybrid breakdown (one-shot, first sub-#450/#460 hybrid spike):"
                        + " total={0}ms mainLoop={1}ms ({2}) spawn={3}ms ({4} built={5} max={6}ms)"
                        + " destroy={7}ms explosionCleanup={8}ms"
                        + " deferredCreated={9}ms ({10} evts) deferredCompleted={11}ms ({12} evts)"
                        + " observabilityCapture={13}ms trajectories={14} overlapIterations={15}"
                        + " ghosts={16} warp={17}x",
                        totalMs.ToString("F1", Inv),
                        mainLoopMs.ToString("F2", Inv),
                        mainLoopFractionStr,
                        spawnMs.ToString("F2", Inv),
                        spawnFractionStr,
                        phases.spawnsAttempted,
                        (phases.spawnMaxMicroseconds / 1000.0).ToString("F2", Inv),
                        (phases.destroyMicroseconds / 1000.0).ToString("F2", Inv),
                        (phases.explosionCleanupMicroseconds / 1000.0).ToString("F2", Inv),
                        (phases.deferredCreatedEventsMicroseconds / 1000.0).ToString("F2", Inv),
                        phases.createdEventsFired,
                        (phases.deferredCompletedEventsMicroseconds / 1000.0).ToString("F2", Inv),
                        phases.completedEventsFired,
                        (phases.observabilityCaptureMicroseconds / 1000.0).ToString("F2", Inv),
                        phases.trajectoriesIterated,
                        phases.overlapGhostIterationCount,
                        ghostsProcessed,
                        warpRate.ToString("F0", Inv)));
            }
        }


        /// <summary>
        /// Test-only: reset the bug #414, #450, #460, and #581 one-shot breakdown latches so each
        /// test starts clean. Use <see cref="SetBug414BreakdownLatchFiredForTesting"/> /
        /// <see cref="SetBug450BreakdownLatchFiredForTesting"/> /
        /// <see cref="SetBug460BreakdownLatchFiredForTesting"/> when a test needs to
        /// pre-consume a specific prior latch without touching the others.
        /// </summary>
        internal static void ResetPlaybackBreakdownOneShotForTesting()
        {
            s_playbackBreakdownOneShotFired = false;
            s_buildBreakdownOneShotFired = false;
            s_mainLoopBreakdownOneShotFired = false;
            s_hybridBreakdownOneShotFired = false;
        }

        /// <summary>
        /// Bug #450 test seam: flip the #414 breakdown latch without touching #450's latch,
        /// so a test can simulate the mid-session rollout case where the session's first
        /// budget-exceeded frame already consumed the #414 latch BEFORE Phase A's code
        /// loaded. Without this helper the "latch independence" test can only verify that
        /// both latches consume in lockstep, not that they are independent.
        /// </summary>
        internal static void SetBug414BreakdownLatchFiredForTesting()
        {
            s_playbackBreakdownOneShotFired = true;
        }

        /// <summary>
        /// Added by #460: test seam that pre-fires the #450 spawn-build-breakdown
        /// latch without touching the #414 or #460 latches, so a three-way independence
        /// test can verify the #460 branch does not share state with #450 either.
        /// Companion to <see cref="SetBug414BreakdownLatchFiredForTesting"/>.
        /// </summary>
        internal static void SetBug450BreakdownLatchFiredForTesting()
        {
            s_buildBreakdownOneShotFired = true;
        }

        /// <summary>
        /// Added by #581: test seam that pre-fires the #460 mainLoop-dominated
        /// breakdown latch without touching the #414, #450, or #581 latches, so the
        /// hybrid-spike independence test can verify the #581 branch fires even when
        /// #460 is already consumed. Companion to <see cref="SetBug450BreakdownLatchFiredForTesting"/>.
        /// </summary>
        internal static void SetBug460BreakdownLatchFiredForTesting()
        {
            s_mainLoopBreakdownOneShotFired = true;
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
            s_playbackBreakdownOneShotFired = false;
            s_buildBreakdownOneShotFired = false;
            s_mainLoopBreakdownOneShotFired = false;
            s_hybridBreakdownOneShotFired = false;
        }
    }
}
