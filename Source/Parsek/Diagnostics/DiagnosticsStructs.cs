namespace Parsek
{
    /// <summary>
    /// Last-frame ghost/FX counts captured from GhostPlaybackEngine.
    /// Pure observability data — never read by gameplay logic.
    /// </summary>
    internal struct GhostObservability
    {
        public int activePrimaryGhostCount;
        public int activeOverlapGhostCount;
        public int zone1GhostCount;
        public int zone2GhostCount;
        public int softCapReducedCount;
        public int softCapSimplifiedCount;
        public int ghostsWithEngineFx;
        public int engineModuleCount;
        public int engineParticleSystemCount;
        public int ghostsWithRcsFx;
        public int rcsModuleCount;
        public int rcsParticleSystemCount;
    }

    /// <summary>
    /// Per-frame timing breakdown for playback or recording.
    /// Raw timing only — rolling stats are computed from RollingTimingBuffer at read time.
    /// </summary>
    internal struct FrameBudget
    {
        public long totalMicroseconds;
        public long spawnMicroseconds;
        public long destroyMicroseconds;
        public int ghostsProcessed;
        public float warpRate;
        public GhostObservability ghostObservability;
    }

    /// <summary>
    /// One-shot per-phase breakdown of a single UpdatePlayback frame, captured only
    /// when the first playback-budget-exceeded warning fires (see bug #414). Used by
    /// <see cref="DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown"/>
    /// to pinpoint which sub-phase is actually responsible for a spike. All fields
    /// are microseconds. The struct is `default` on frames that stayed within budget.
    /// </summary>
    internal struct PlaybackBudgetPhases
    {
        /// <summary>Main per-trajectory dispatch loop, minus any spawn/destroy time accumulated inside.</summary>
        public long mainLoopMicroseconds;
        /// <summary>SpawnGhost time (already accumulated via the existing spawnStopwatch across the frame).</summary>
        public long spawnMicroseconds;
        /// <summary>DestroyGhost time (already accumulated via the existing destroyStopwatch across the frame).</summary>
        public long destroyMicroseconds;
        /// <summary>Post-loop stale-explosion cleanup sweep.</summary>
        public long explosionCleanupMicroseconds;
        /// <summary>Deferred OnGhostCreated invocations to the policy/host.</summary>
        public long deferredCreatedEventsMicroseconds;
        /// <summary>Deferred OnPlaybackCompleted invocations to the policy/host.</summary>
        public long deferredCompletedEventsMicroseconds;
        /// <summary>Post-loop observability capture (CaptureGhostObservability).</summary>
        public long observabilityCaptureMicroseconds;
        /// <summary>Number of trajectories iterated in the main loop (dispatched, not necessarily rendered).</summary>
        public int trajectoriesIterated;
        /// <summary>Number of deferred OnGhostCreated events fired this frame.</summary>
        public int createdEventsFired;
        /// <summary>Number of deferred OnPlaybackCompleted events fired this frame.</summary>
        public int completedEventsFired;
        /// <summary>Bug #414: count of throttle-eligible call sites that actually ran BuildGhostVisualsWithMetrics this frame.</summary>
        public int spawnsAttempted;
        /// <summary>Bug #414: count of throttle-eligible call sites that were deferred to a later frame because the spawn cap was exhausted.</summary>
        public int spawnsThrottled;
        /// <summary>Bug #414: largest single BuildGhostVisualsWithMetrics cost in microseconds this frame. Tells us whether any individual ghost blows the budget.</summary>
        public long spawnMaxMicroseconds;
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
        public long readableMirrorBytes;
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
        public int zone1GhostCount;
        public int zone2GhostCount;
        public int softCapReducedCount;
        public int softCapSimplifiedCount;
        public int ghostsWithEngineFx;
        public int engineModuleCount;
        public int engineParticleSystemCount;
        public int ghostsWithRcsFx;
        public int rcsModuleCount;
        public int rcsParticleSystemCount;

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
        public int reentryFxBuildsThisSession;
        public int reentryFxSkippedThisSession;
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
            reentryFxBuildsThisSession = 0;
            reentryFxSkippedThisSession = 0;
            gcGen0Baseline = System.GC.CollectionCount(0);
        }
    }
}
