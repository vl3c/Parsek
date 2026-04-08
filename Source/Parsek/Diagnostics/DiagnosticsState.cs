using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Central holder for all observability state. Static class, survives scene changes.
    /// Metrics are read-only observations — they never change behavior.
    /// No serialization; diagnostics state is ephemeral.
    /// </summary>
    internal static class DiagnosticsState
    {
        // Live frame budgets (updated every frame by Stopwatch instrumentation)
        internal static FrameBudget playbackBudget;
        internal static FrameBudget recordingBudget;

        // Rolling average history (pre-allocated ring buffer, time-based window)
        internal static RollingTimingBuffer playbackFrameHistory = new RollingTimingBuffer();

        // Growth rate (only valid during active recording)
        internal static RecordingGrowthRate activeGrowthRate;
        internal static bool hasActiveGrowthRate;

        // Health counters (reset per scene load)
        internal static HealthCounters health;

        // Snapshot cache (computed on demand, cached briefly)
        internal static MetricSnapshot cachedSnapshot;
        internal static bool hasCachedSnapshot;
        internal static double cachedSnapshotUT;
        internal const double SnapshotCacheTtlSeconds = 2.0;

        // Per-recording storage cache (computed on hover, cached per-recording)
        internal static Dictionary<string, (StorageBreakdown breakdown, double cachedUT)> storageBreakdownCache
            = new Dictionary<string, (StorageBreakdown, double)>();
        internal const double StorageBreakdownCacheTtlSeconds = 5.0;

        // Average bytes per trajectory point — bootstrapped from real data when available
        internal static double avgBytesPerPoint = 85.0;

        /// <summary>
        /// Reset session-scoped counters. Called on scene load.
        /// Resets health counters, rolling buffer, and invalidates snapshot cache.
        /// Captures GC gen0 baseline.
        /// </summary>
        internal static void ResetSessionCounters()
        {
            health.Reset();
            playbackFrameHistory.Reset();
            playbackBudget = default;
            recordingBudget = default;
            InvalidateSnapshotCache();
        }

        /// <summary>
        /// Invalidate the cached MetricSnapshot, forcing recomputation on next request.
        /// </summary>
        internal static void InvalidateSnapshotCache()
        {
            hasCachedSnapshot = false;
            cachedSnapshotUT = 0.0;
        }

        /// <summary>
        /// Reset all state for unit testing. Prevents cross-test contamination.
        /// </summary>
        internal static void ResetForTesting()
        {
            playbackBudget = default;
            recordingBudget = default;
            playbackFrameHistory.Reset();
            activeGrowthRate = default;
            hasActiveGrowthRate = false;
            health = default;
            hasCachedSnapshot = false;
            cachedSnapshotUT = 0.0;
            cachedSnapshot = default;
            storageBreakdownCache.Clear();
            avgBytesPerPoint = 85.0;
        }
    }
}
