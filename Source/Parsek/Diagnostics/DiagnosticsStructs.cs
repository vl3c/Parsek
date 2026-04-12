namespace Parsek
{
    /// <summary>
    /// Per-frame timing breakdown for playback or recording.
    /// Raw timing only — rolling stats are computed from RollingTimingBuffer at read time.
    /// </summary>
    internal struct FrameBudget
    {
        public long totalMicroseconds;
        public long spawnMicroseconds;
        public int ghostsProcessed;
        public float warpRate;
    }

    /// <summary>
    /// Timing for a single OnSave or OnLoad operation.
    /// </summary>
    internal struct SaveLoadTiming
    {
        public long totalMilliseconds;
        public int recordingsProcessed;
        public int dirtyRecordingsWritten;
    }

    /// <summary>
    /// Per-recording storage detail. Computed by reading file sizes on disk.
    /// </summary>
    internal struct StorageBreakdown
    {
        public string recordingId;
        public string vesselName;
        public long trajectoryFileBytes;
        public long vesselSnapshotBytes;
        public long ghostSnapshotBytes;
        public long totalBytes;
        public int pointCount;
        public int partEventCount;
        public int orbitSegmentCount;
        public double durationSeconds;
        public double bytesPerSecond;
    }

    /// <summary>
    /// Point-in-time snapshot of all observability metrics.
    /// Computed on demand, cached briefly in DiagnosticsState.
    /// </summary>
    internal struct MetricSnapshot
    {
        // Storage
        public long totalStorageBytes;
        public int recordingCount;
        public StorageBreakdown[] perRecording;

        // Memory
        public int loadedTrajectoryPoints;
        public int loadedPartEvents;
        public int loadedOrbitSegments;
        public int loadedSnapshotCount;
        public long estimatedMemoryBytes;

        // Ghost state
        public int activeGhostCount;
        public int activeOverlapGhostCount;
        public int fullGhostCount;
        public int reducedGhostCount;
        public int hiddenGhostCount;
        public int watchedOverrideGhostCount;

        // Timing — raw last-frame budgets
        public FrameBudget lastPlaybackBudget;
        public FrameBudget lastRecordingBudget;

        // Timing — rolling stats computed at snapshot time
        public double playbackAvgTotalMs;
        public double playbackPeakTotalMs;
        public double playbackWindowDurationSeconds;
        public int playbackEntriesInWindow;

        // Save/load
        public SaveLoadTiming lastSaveTiming;
        public SaveLoadTiming lastLoadTiming;
    }

    /// <summary>
    /// Live growth metrics during active recording.
    /// Updated per sampling event in FlightRecorder.
    /// </summary>
    internal struct RecordingGrowthRate
    {
        public double pointsPerSecond;
        public double eventsPerSecond;
        public long estimatedFinalBytes;
        public double elapsedSeconds;
        public int totalPoints;
        public int totalEvents;
    }

    /// <summary>
    /// Session-level counters for anomalies and operational health.
    /// Reset per scene load via DiagnosticsState.ResetSessionCounters().
    /// </summary>
    internal struct HealthCounters
    {
        public int waypointCacheHits;
        public int waypointCacheMisses;
        public int snapshotRefreshSpikes;
        public int spawnFailures;
        public int spawnRetries;
        public int ghostBuildsThisSession;
        public int ghostDestroysThisSession;
        public int gcGen0Baseline;

        public void Reset()
        {
            waypointCacheHits = 0;
            waypointCacheMisses = 0;
            snapshotRefreshSpikes = 0;
            spawnFailures = 0;
            spawnRetries = 0;
            ghostBuildsThisSession = 0;
            ghostDestroysThisSession = 0;
            gcGen0Baseline = System.GC.CollectionCount(0);
        }
    }
}
