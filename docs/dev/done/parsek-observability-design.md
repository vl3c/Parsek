# Design: Recording Observability

## Problem

Parsek accumulates recordings over a career — trajectory points, part events, orbit segments, vessel snapshots, ghost meshes. As the player progresses through dozens of missions, the system's disk footprint, memory usage, and per-frame playback cost grow without any visibility. There is no way to answer basic questions: "How much disk space is Parsek using? Which recording is the largest? Is playback costing me framerate? How fast is this recording growing?"

Before any optimization work (Phase 11.5's second half), we need instrumentation. You can't optimize what you can't measure. This phase adds observability — metrics collection, in-game diagnostics, and structured logging — without changing any existing behavior.

## Terminology

- **Metric** — a named numeric value collected at runtime (e.g., `playback.frame_ms`, `storage.total_bytes`)
- **Gauge** — a metric that reflects current state (ghost count, memory estimate). Sampled on demand.
- **Counter** — a metric that accumulates over time (spawn count, cache misses). Reset per scene load.
- **Diagnostics report** — a full snapshot of all metrics, rendered in the in-game test runner window (Ctrl+Shift+T) alongside test results, and simultaneously dumped to KSP.log.

## Mental Model

Observability is structured around five domains, each answering a different question:

```
┌─────────────────────────────────────────────────────────┐
│                    OBSERVABILITY                         │
│                                                         │
│  ┌───────────┐  ┌───────────┐  ┌────────────────────┐  │
│  │  STORAGE   │  │  MEMORY   │  │  PLAYBACK BUDGET   │  │
│  │            │  │           │  │                     │  │
│  │ How much   │  │ How much  │  │ How much frame     │  │
│  │ disk space │  │ RAM right │  │ time does ghost     │  │
│  │ per save?  │  │ now?      │  │ playback consume?   │  │
│  └───────────┘  └───────────┘  └────────────────────┘  │
│                                                         │
│  ┌────────────────────┐  ┌────────────────────────┐     │
│  │  RECORDING BUDGET   │  │  SAVE/LOAD BUDGET      │     │
│  │                     │  │                         │     │
│  │ How much frame      │  │ How long do save/load   │     │
│  │ time does active    │  │ operations take?        │     │
│  │ recording consume?  │  │                         │     │
│  └────────────────────┘  └────────────────────────┘     │
│                                                         │
│  Surfaced via:                                          │
│  - Diagnostics report (in-game test runner window)      │
│  - KSP.log (structured, rate-limited, no spam)          │
│  - Per-recording tooltip (Recordings Manager)           │
└─────────────────────────────────────────────────────────┘
```

Metrics are **read-only observations**. They never change behavior, never gate decisions, never alter playback. They exist purely to make the system's resource consumption visible.

## Data Model

### MetricSnapshot (struct)

Holds a point-in-time reading of all metrics. Computed on demand (not accumulated per-frame).

```
MetricSnapshot
  // Storage (computed by scanning file system)
  long    totalStorageBytes          // all Parsek files in save
  int     recordingCount             // committed recordings
  StorageBreakdown[] perRecording    // per-recording detail

  // Memory (computed from in-memory state)
  int     loadedTrajectoryPoints     // sum across all recordings
  int     loadedPartEvents           // sum across all recordings
  int     loadedOrbitSegments        // sum across all recordings
  int     loadedSnapshotCount        // vessel + ghost snapshots in memory
  long    estimatedMemoryBytes       // rough estimate from counts × per-item size
  // Estimation formula (bytes):
  //   points × 136   (76 struct + 8 list ref + ~50 string heap; bodyName may be interned)
  //   events × 88    (32 struct + 8 list ref + ~48 string heap for partName)
  //   segments × 120 (108 struct + 8 list ref; bodyName likely interned across segment)
  //   snapshots × 8192  (ConfigNode tree, rough average)

  // Ghost state (computed by iterating GhostPlaybackEngine.ghostStates values)
  // Zone counts derived by reading each GhostPlaybackState.currentZone field.
  // Do NOT read cachedZone1Ghosts/cachedZone2Ghosts — those are transient soft-cap evaluation lists.
  int     activeGhostCount           // ghostStates.Count
  int     activeOverlapGhostCount    // sum of overlapGhosts list counts
  int     zone1GhostCount            // count of ghosts where currentZone == Physics
  int     zone2GhostCount            // count of ghosts where currentZone == Visual
  int     softCapReducedCount        // ghosts where fidelityReduced == true (0 if soft caps disabled)
  int     softCapSimplifiedCount     // ghosts where simplified == true (0 if soft caps disabled)
  bool    softCapsEnabled            // GhostSoftCapManager.Enabled — report shows "disabled" when false

  // Timing (from FrameBudget)
  FrameBudget lastPlaybackBudget     // most recent playback frame timing
  FrameBudget lastRecordingBudget    // most recent recording frame timing
  SaveLoadTiming lastSaveTiming      // most recent OnSave timing
  SaveLoadTiming lastLoadTiming      // most recent OnLoad timing
```

### StorageBreakdown (struct)

Per-recording storage detail. Computed by reading file sizes on disk.

```
StorageBreakdown
  string  recordingId
  string  vesselName
  long    trajectoryFileBytes        // .prec
  long    vesselSnapshotBytes        // _vessel.craft
  long    ghostSnapshotBytes         // _ghost.craft
  long    totalBytes                 // sum of above
  int     pointCount                 // from recording metadata
  int     partEventCount
  int     orbitSegmentCount
  double  durationSeconds            // EndUT - StartUT
  double  bytesPerSecond             // totalBytes / durationSeconds
```

### FrameBudget (struct)

Per-frame timing breakdown. Updated every frame via Stopwatch instrumentation.

```
FrameBudget
  // Raw timing from most recent frame (microseconds)
  long    totalMicroseconds          // wall time for entire update pass
  long    positioningMicroseconds    // interpolation + world placement
  long    partEventMicroseconds      // part event scanning + application
  long    fxUpdateMicroseconds       // engine/RCS/reentry particle updates
  long    zoneEvalMicroseconds       // zone distance checks + soft cap eval
  long    spawnMicroseconds          // ghost build cost in THIS frame (0 if no spawns this frame)
  int     ghostsProcessed            // how many ghosts were updated this frame
  float   warpRate                   // TimeWarp.CurrentRate at measurement time

  // Rolling statistics (time-based, last 4 seconds)
  double  avgTotalMs                 // rolling average of totalMicroseconds
  double  peakTotalMs                // max over rolling window
  double  windowDurationSeconds      // actual time span of the window
```

### SaveLoadTiming (struct)

Timing for save/load operations. Updated on each OnSave/OnLoad.

```
SaveLoadTiming
  long    totalMilliseconds          // wall time for entire operation
  long    serializationMs            // ConfigNode serialization/parsing
  long    fileIoMs                   // disk read/write
  // Gap between (serializationMs + fileIoMs) and totalMilliseconds is expected —
  // it's validation, logging, bookkeeping, and other overhead. Not separately tracked.
  int     recordingsProcessed        // how many recordings touched
  int     dirtyRecordingsWritten     // how many had FilesDirty=true (save only)
```

### RecordingGrowthRate (struct)

Live growth metrics during active recording. Updated per sampling event in FlightRecorder.

```
RecordingGrowthRate
  double  pointsPerSecond            // points added / elapsed time
  double  eventsPerSecond            // part events / elapsed time
  long    estimatedFinalBytes        // projected file size at current rate
  double  elapsedSeconds             // recording duration so far
  int     totalPoints                // points so far
  int     totalEvents                // events so far
```

### HealthCounters (static counters, reset per scene load)

Session-level counters for anomalies and operational health.

```
HealthCounters (static)
  int     waypointCacheHits          // FindWaypointIndex: exact cached index OR cachedIndex+1 hit
  int     waypointCacheMisses        // FindWaypointIndex: binary search fallback (neither cached nor next)
  int     snapshotRefreshSpikes      // snapshot refreshes > 25ms
  int     spawnFailures              // ghost spawn blocked (collision, etc.)
  int     spawnRetries               // ghost spawn retry attempts
  int     softCapActivations         // times soft cap threshold was reached
  int     softCapDespawns            // cumulative ghosts despawned by soft cap
  int     ghostBuildsThisSession     // total ghost mesh builds
  int     ghostDestroysThisSession   // total ghost mesh destroys
  int     gcGen0Baseline             // GC.CollectionCount(0) captured at scene load
  // Report computes delta: GC.CollectionCount(0) - gcGen0Baseline (derived, not incremented)
```

### DiagnosticsState (singleton)

Central holder for all observability state. Lives as a static class, survives scene changes.

```
DiagnosticsState (static class)
  // Live frame budgets (updated every frame)
  FrameBudget       playbackBudget
  FrameBudget       recordingBudget

  // Rolling averages (pre-allocated ring buffer, time-based 4-second window)
  // Fixed-size array of (wallTimestamp, microseconds) pairs, pre-allocated at init.
  // Size: 1024 entries (enough for 4s at 240fps). Head/tail indices wrap via modulo.
  // On append: write at tail, advance tail. On read: skip entries older than 4s from head.
  // Zero allocation after init — no List/Queue growth, no GC pressure.
  RollingTimingBuffer playbackFrameHistory

  // Growth rate (only during active recording)
  RecordingGrowthRate activeGrowthRate
  bool              hasActiveGrowthRate     // false when not recording

  // Health counters
  HealthCounters    health

  // Snapshot (computed on demand, cached briefly)
  MetricSnapshot    cachedSnapshot
  bool              hasCachedSnapshot       // avoids Nullable boxing of struct with array fields
  double            cachedSnapshotUT        // UT when snapshot was last computed
  double            snapshotCacheTtlSeconds = 2.0

  // Methods
  MetricSnapshot    ComputeSnapshot()       // full computation, caches result
  MetricSnapshot    GetOrComputeSnapshot()  // returns cached if fresh
  void              ResetSessionCounters()  // called on scene load
  string            FormatReport()          // human-readable full dump
```

No serialization. Diagnostics state is ephemeral — it exists only during a game session. Nothing persists to save files.

## Behavior

### Metric Collection

**Storage metrics** are computed on demand (not continuously), triggered by:
1. Running the diagnostics report (via test runner or Settings button)
2. Hovering over a recording's tooltip in the Recordings Manager

The computation iterates `RecordingStore.CommittedRecordings` for recording IDs (in-memory, fast), then stats the corresponding sidecar files on disk via `RecordingPaths`. No directory scanning — the ID list is authoritative. This is I/O but infrequent — only when the developer explicitly requests diagnostics.

**Memory metrics** are computed on demand from in-memory state:
- Iterate `RecordingStore.CommittedRecordings`, sum point/event/segment counts
- Read `GhostPlaybackEngine.ghostStates.Count` and zone counts
- Estimate memory: `points × 136 + events × 88 + segments × 120 + snapshots × 8192` (see estimation formula in MetricSnapshot)

These are cheap in-memory traversals but still on-demand, not per-frame.

**Playback frame budget** is collected every frame inside `GhostPlaybackEngine.UpdatePlayback()`:
1. Start outer Stopwatch before the ghost loop
2. Start/stop inner Stopwatches around each subsection (positioning, part events, FX, zone eval)
3. After the loop: write timing into `DiagnosticsState.playbackBudget`
4. Append (timestamp, total) to time-based rolling buffer
5. Evict entries older than 4 seconds, recompute rolling average/peak

The Stopwatch overhead is ~50ns per Start/Stop call. With 6 measurement pairs per frame (1 outer + 5 inner subsections = 12 Start/Stop calls), that's ~600ns — negligible vs. the ~1-5ms playback cost being measured.

**Recording frame budget** is collected every physics frame inside the Harmony postfix (`PhysicsFramePatch`):
1. Start outer Stopwatch before sampling logic
2. Stop after all polling + point recording completes
3. Write timing into `DiagnosticsState.recordingBudget`

Only one Stopwatch (outer total) for recording — the inner polling breakdown is not individually timed in v1. If the total is unexpectedly high, individual Check* methods can be timed in a targeted investigation.

**Recording growth rate** is updated each time a point is recorded:
1. Increment point count
2. If events were emitted this frame, increment event count
3. Recompute rates: `pointsPerSecond = totalPoints / elapsedSeconds`
4. Estimate final size: `estimatedFinalBytes = (totalPoints × avgBytesPerPoint) + (totalEvents × avgBytesPerEvent)`
5. `avgBytesPerPoint` is computed once at recording start (not per-frame — no I/O in the hot path). If committed recordings exist, it's `totalPrecFileSize / totalPointCount` across all recordings. Falls back to 85 bytes if no committed recordings exist yet (first flight). Cached in `DiagnosticsState` and recomputed only when a new recording is committed.

**Save/Load timing** wraps the existing OnSave/OnLoad with Stopwatch:
1. Start Stopwatch at OnSave/OnLoad entry
2. Track inner timing around file I/O vs. ConfigNode operations
3. Store result in `DiagnosticsState.lastSaveTiming` / `lastLoadTiming`

**Health counters** are incremented at the point of occurrence:
- `FindWaypointIndex`: three paths exist — (a) exact cached index hit, (b) cachedIndex+1 hit (sequential playback), (c) binary search fallback. Paths (a) and (b) both increment `cacheHits`; path (c) increments `cacheMisses`. The counter increment is a single `++` at each return site — no allocation, no branching beyond what already exists.
- `RefreshBackupSnapshot`: increment `snapshotRefreshSpikes` when elapsed > threshold
- Ghost spawn logic: increment `spawnFailures` / `spawnRetries` on those code paths
- Soft cap evaluation: increment `softCapActivations` when threshold exceeded
- Ghost build/destroy: increment counters in `BuildGhostFromSnapshot` / destroy paths

All counters are simple `Interlocked.Increment` or plain `++` (single-threaded Unity). Reset on scene load via `DiagnosticsState.ResetSessionCounters()`.

### UI: Diagnostics Report (In-Game Test Runner Window)

The diagnostics report is surfaced through the existing in-game test runner window (Ctrl+Shift+T), alongside test results. A new "Run Diagnostics" button (or automatic inclusion when running all tests) computes the full snapshot and renders it as a results section in the test runner output. The report is simultaneously dumped to KSP.log.

This reuses the existing infrastructure — the test runner already handles result rendering, scrolling, and export to `parsek-test-results.txt`. Diagnostics entries appear as a "Diagnostics" category in the results, formatted identically to test results (pass/fail/info lines).

The Settings > Diagnostics section gains only one new button: **[Run Diagnostics Report]**, which opens the test runner window with the diagnostics category pre-selected.

### UI: Per-Recording Tooltip (Recordings Manager)

Hovering over a recording in the Recordings Manager shows a tooltip with storage details:

```
Mun Landing
Duration: 12m 34s | Points: 8,432 | Events: 47
Storage: 2.1 MB (trajectory: 1.8 MB, vessel: 180 KB, ghost: 120 KB)
Efficiency: 2.8 KB/s of flight time
```

The tooltip data is computed lazily (on hover) by calling `DiagnosticsState.ComputeStorageBreakdown(recordingId)`. File sizes are cached per-recording for 5 seconds (separate from the full MetricSnapshot cache at 2s TTL) to avoid repeated stat calls while the mouse moves within the same row.

### Diagnostics Report Format

The report is rendered in the test runner window AND written to KSP.log via `ParsekLog.Info`:

```
[Parsek][INFO][Diagnostics] ===== DIAGNOSTICS REPORT =====
[Parsek][INFO][Diagnostics] Storage: 12.4 MB total, 23 recordings
[Parsek][INFO][Diagnostics]   rec[0] "Mun Landing" — 2.1 MB (8432 pts, 47 evts, 2 segs) 12m34s 2.8 KB/s
[Parsek][INFO][Diagnostics]   rec[1] "KSC Hopper" — 0.3 MB (1204 pts, 12 evts, 0 segs) 2m01s 2.5 KB/s
[Parsek][INFO][Diagnostics]   ...
[Parsek][INFO][Diagnostics] Memory: ~6.8 MB est (47200 pts, 312 evts, 18 segs, 46 snapshots)
[Parsek][INFO][Diagnostics] Ghosts: 8 active (z1:3 z2:5), 0 reduced, 0 simplified
[Parsek][INFO][Diagnostics] Playback budget: 1.8 ms avg, 3.2 ms peak (4.0s window), warp: 1x
[Parsek][INFO][Diagnostics]   Position: 0.6 ms | Events: 0.1 ms | FX: 0.8 ms | Zone: 0.3 ms
[Parsek][INFO][Diagnostics] Recording budget: 0.4 ms avg (active)
[Parsek][INFO][Diagnostics] Save: 120 ms last (3 dirty) | Load: 340 ms last (23 total)
[Parsek][INFO][Diagnostics] Health: cache 99.2% hit (12 miss of 1500 lookups), spikes 2, spawn fail 0, builds 14
[Parsek][INFO][Diagnostics] GC gen0: +12 collections this session
[Parsek][INFO][Diagnostics] ===== END REPORT =====
```

This is the primary tool for bug reports and performance investigations. Open the test runner (Ctrl+Shift+T), run diagnostics, and the report appears alongside test results. The same data goes to KSP.log and the exported `parsek-test-results.txt` file.

### Automatic Logging (KSP.log)

For ongoing/live observability, KSP.log is the right channel. The key constraint is **no log spam** — every automatic log line must be either one-shot (fires once) or aggressively rate-limited.

| Trigger | What's logged | Level | Rate limiting |
|---------|--------------|-------|---------------|
| Scene load complete | Memory snapshot + ghost count | Verbose | Once per scene |
| OnSave complete | Save timing + dirty count | Verbose | Once per save |
| OnLoad complete | Load timing + recording count | Verbose | Once per load |
| Recording start | Initial state (point count = 0, vessel part count) | Verbose | Once |
| Recording stop | Final growth rate, total points/events, estimated file size | Verbose | Once |
| Soft cap activation | Which threshold, how many ghosts affected | Verbose | VerboseRateLimited, 30s |
| Playback budget > 8ms | Total ms + breakdown + ghost count + warp rate | Warn | VerboseRateLimited, 30s |
| Recording budget > 4ms | Total ms + vessel name + part count | Warn | VerboseRateLimited, 30s |

**No per-frame logging.** Frame budgets accumulate silently into rolling stats. Only threshold breaches emit log lines, and those are rate-limited to at most once per 30 seconds. Health counters are never individually logged — they appear only in the on-demand diagnostics report.

**API addition required:** `ParsekLog.WarnRateLimited(subsystem, key, message, minIntervalSeconds)` — same rate-limiting logic as `VerboseRateLimited` but emits at Warn level (unconditional, not gated by `IsVerboseEnabled`). Needed for budget threshold warnings that should be visible even with verbose logging disabled.

The warning thresholds (8ms playback, 4ms recording) are chosen to flag potential framerate impact. At 60 FPS, a frame is 16.6ms. Spending half the frame budget on Parsek is worth a warning. These thresholds are constants, not settings — they're for developer diagnostics, not player configuration.

All formatted numbers use `InvariantCulture` (the project has a known locale bug with comma-decimal systems — see MEMORY.md).

## Edge Cases

### E1. No recordings exist
- Storage: "0 bytes, 0 recordings"
- Memory: "0 points loaded"
- Frame budget: playback shows 0.0ms (no ghosts to process)
- Growth rate: hidden (no active recording)
- No errors, no warnings. Clean display of zeros.

### E2. Very large recording (100K+ points)
- Storage breakdown computes correctly (just larger numbers)
- Memory estimate reflects the large point count
- File size scan is still fast (single stat call per file)
- Per-recording tooltip shows the large numbers without truncation
- Handled: display uses `FormatBytes()` helper (KB/MB/GB auto-scaling)

### E3. Many recordings (50+)
- Storage scan iterates all files — could take 50-100ms on SSD, up to 1-2s on cold HDD (200 stat calls × 5-10ms)
- Mitigated: scan is on-demand + cached for 2 seconds. First-open latency is acceptable since the user explicitly requested the report.
- Diagnostics report logs all 50 recordings (acceptable for a log dump)
- If the scan takes >500ms, log a warning: `[Parsek][WARN][Diagnostics] Storage scan took {elapsed}ms — consider SSD for faster diagnostics`

### E4. Sidecar file missing (deleted externally)
- `StorageBreakdown` reports 0 bytes for the missing file
- Total still sums correctly (other files counted)
- Log warning: "Missing sidecar file: {path}"
- No crash, no error dialog. Graceful degradation.

### E5. Recording in progress during diagnostics
- Storage metrics exclude the in-progress recording (not yet committed, no sidecar files)
- Memory metrics include it (points are in memory)
- Growth rate section shows the in-progress recording's stats
- Frame budget includes the recording cost

### E6. Scene transitions
- `HealthCounters` reset on scene load (they're session-scoped)
- `FrameBudget` rolling history resets on scene load
- `cachedSnapshot` invalidated on scene load
- `activeGrowthRate` cleared when recording stops

### E7. Stopwatch overhead in hot path
- Total Stopwatch overhead: ~600ns per frame (6 measurement pairs × 2 calls × ~50ns each)
- Frame budget being measured: ~1-5ms
- Overhead is 0.012-0.06% of measured value — negligible
- Stopwatch is always running (no toggle). The cost of checking a toggle would be similar to the Stopwatch itself.

### E8. Thread safety
- All metrics are written from Unity's main thread (physics and Update callbacks)
- No concurrent writes. Plain field assignment is sufficient.
- No locks, no Interlocked. Single-threaded Unity model.

### E9. Diagnostics during time warp
- At high warp (1000x+), playback budget will be low (ghosts suppressed) but recording growth rate may be very high
- The warp rate is captured in FrameBudget, so the report shows context: "Playback: 0.3 ms avg, warp: 1000x"
- This is technically correct behavior — warp suppression is working. No special handling needed.

### E10. First frame after scene load
- Rolling history is empty. `avgTotalMs` returns 0.0, `peakTotalMs` returns 0.0.
- Report labels this as "N/A" if the rolling buffer has zero entries, to avoid showing misleading zeros when ghosts are clearly active.
- After one frame, real data populates.

### E11. Diagnostics report during high ghost count
- Storage scan is cached, not recomputed per frame
- Memory/ghost counts are direct field reads from DiagnosticsState (no computation on draw)
- Frame budget reads the last-computed rolling average
- UI draw cost is minimal (text labels only, no graphs)

## What Doesn't Change

- **Recording behavior** — no changes to adaptive sampling, part polling, event generation, or snapshot refresh. We're measuring, not modifying.
- **Playback behavior** — no changes to ghost positioning, FX, zone evaluation, or soft caps. Stopwatches wrap existing code without altering it.
- **Save/load format** — no new fields serialized. Diagnostics state is ephemeral.
- **Existing logging** — all existing log lines remain. New observability logging is additive.
- **Settings persistence** — no new persistent settings. The diagnostics report is on-demand only.
- **Performance** — Stopwatch overhead is ~600ns/frame. No allocations in the hot path (pre-allocated ring buffer for rolling stats). Storage scan is on-demand only.

## Out of Scope

This design covers **observability only** — measurement, reporting, and diagnostics. The following are explicitly deferred to Phase 11.5's optimization half, after measurement data is collected:

- Shorter key names in .prec serialization
- Compact numeric encoding
- Vessel snapshot deduplication
- Part event name deduplication
- Trajectory point thinning
- Optional gzip compression for sidecar files
- Lazy loading of trajectory data
- LOD culling for distant ghost meshes (T6)
- Ghost mesh unloading outside active time range (T7)
- Particle system pooling for engine/RCS FX (T8)

The observability phase produces the data that tells us which of these optimizations actually matter.

## Backward Compatibility

No compatibility concerns. This feature:
- Adds no new serialized fields
- Changes no save file format
- Changes no recording format
- Adds no new ConfigNode keys
- Is purely additive runtime instrumentation

A save created without observability loads identically. A save created with observability loads identically on a version without it (no persisted state to miss).

## Diagnostic Logging

### Collection layer
- **Stopwatch wrapping**: when a Stopwatch measurement completes, log the raw value only if it exceeds a threshold (8ms playback, 4ms recording, 100ms save/load). Below threshold: silent accumulation into rolling stats. Above: `[Parsek][WARN][Diagnostics] Playback frame budget exceeded: {total}ms ({ghostCount} ghosts) — position:{pos}ms events:{evt}ms fx:{fx}ms zone:{zone}ms`
- **Health counter increments**: each counter increment is NOT individually logged (too noisy). Counters appear in the periodic report and on-demand dump.
- **Storage scan**: `[Parsek][VERBOSE][Diagnostics] Storage scan: {totalMB} MB across {count} recordings ({elapsedMs}ms)` — logged once per scan.

### UI layer
- **Report requested**: `[Parsek][INFO][Diagnostics] Full diagnostics report requested` followed by the multi-line report

### Decision points
- **Cache hit/miss**: when `GetOrComputeSnapshot()` returns cached vs. recomputes: `[Parsek][VERBOSE][Diagnostics] Snapshot cache {hit|miss}, age={age}s` (rate-limited, 5s)
- **Warning threshold**: when playback or recording exceeds budget: warns with full breakdown (see above)
- **Missing file during scan**: `[Parsek][WARN][Diagnostics] Missing sidecar file during storage scan: {path}` — per-file, not rate-limited (infrequent event)

## Test Plan

### Unit tests (DiagnosticsStateTests.cs)

**Rolling average computation**
- Input: sequence of frame timings [1000, 2000, 3000, 1500, 2500]
- Expected: avg = 2000, peak = 3000
- Fails if: rolling buffer indexing is wrong, or peak doesn't track maximum

**Storage breakdown computation**
- Input: mock file sizes via injected path resolver
- Expected: correct per-recording totals, correct bytes-per-second calculation
- Fails if: file size summation is wrong, or duration=0 causes divide-by-zero

**Memory estimation**
- Input: recordings with known point/event/segment counts
- Expected: estimate = `points × 136 + events × 88 + segments × 120 + snapshots × 8192`
- Fails if: multipliers are wrong or counts are misread

**Health counter reset**
- Input: increment several counters, then call `ResetSessionCounters()`
- Expected: all counters back to 0
- Fails if: a new counter is added but not included in reset

**Growth rate calculation**
- Input: 100 points over 10 seconds, 5 events
- Expected: 10.0 pts/s, 0.5 evts/s
- Fails if: rate computation uses wrong divisor

**Growth rate at recording start (zero elapsed)**
- Input: 1 point at elapsedSeconds = 0.0
- Expected: pointsPerSecond = 0.0, eventsPerSecond = 0.0 (not NaN, not Infinity)
- Fails if: division by zero not guarded at recording start

**Snapshot cache TTL**
- Input: compute snapshot, wait 1s (< TTL), call GetOrCompute
- Expected: returns cached (no recompute)
- Input: compute snapshot, wait 3s (> TTL), call GetOrCompute
- Expected: recomputes
- Fails if: cache TTL comparison is wrong direction (< vs >)

**Format report**
- Input: MetricSnapshot with known values
- Expected: formatted string contains "12.4 MB", "23 recordings", correct per-recording lines
- Fails if: formatting is wrong (wrong units, missing values, locale issues)

### Log assertion tests (ObservabilityLoggingTests.cs)

**Budget warning threshold**
- Input: set playback budget to 10ms (> 8ms threshold)
- Expected: log contains `[WARN][Diagnostics] Playback frame budget exceeded`
- Fails if: threshold check is wrong direction, or message not emitted

**Budget below threshold — no warning**
- Input: set playback budget to 3ms (< 8ms threshold)
- Expected: log does NOT contain `[WARN]`
- Fails if: threshold check triggers false positive

**Diagnostics report format**
- Input: call FormatReport() with known state, capture via TestSink
- Expected: log contains `===== DIAGNOSTICS REPORT =====`, all sections present, numbers match input
- Fails if: report format changes or sections are missing

**Storage scan logging**
- Input: trigger storage scan
- Expected: log contains `[VERBOSE][Diagnostics] Storage scan:`
- Fails if: scan completes without logging

### Integration tests (using RecordingBuilder)

**Storage breakdown from synthetic recording**
- Input: RecordingBuilder with 1000 points, 20 events, 2 orbit segments, written to temp directory as v3 sidecar files
- Expected: StorageBreakdown.totalBytes matches sum of actual file sizes on disk
- Fails if: file path resolution is wrong, or size counting misses a file type

**Memory estimate from loaded recordings**
- Input: build 3 synthetic recordings, load into RecordingStore
- Expected: MetricSnapshot.loadedTrajectoryPoints = sum of all three recordings' point counts
- Fails if: iteration over CommittedRecordings misses recordings or double-counts

### Edge case tests

**Missing sidecar file (E4)**
- Input: recording with valid metadata but deleted .prec file
- Expected: StorageBreakdown.trajectoryFileBytes = 0, totalBytes = sum of remaining files, warning logged
- Fails if: missing file causes exception instead of graceful 0

**First frame / empty rolling buffer (E10)**
- Input: call FormatReport() with empty rolling history
- Expected: playback budget line shows "N/A" not "0.0 ms"
- Fails if: empty buffer treated as zero instead of absent

**Duration zero recording (divide-by-zero guard)**
- Input: recording with StartUT == EndUT
- Expected: bytesPerSecond = 0.0 (not NaN, not Infinity)
- Fails if: division by zero not guarded

### In-game tests (RuntimeTests.cs)

**Diagnostics report produces output**
- Run diagnostics report via test runner
- Assert result contains "DIAGNOSTICS REPORT", "Storage:", "Memory:", "Playback budget:"
- Fails if: report generation crashes or produces empty output

**Diagnostics report data matches live state**
- Run diagnostics report in flight with active ghosts
- Assert ghost count in report matches GhostPlaybackEngine.ghostStates.Count
- Fails if: report reads stale or wrong data source

**Recording growth rate updates during flight**
- Start recording, wait 3 seconds, read growth rate
- Assert pointsPerSecond > 0
- Fails if: growth rate not connected to FlightRecorder's point emission

**Storage breakdown resolves real sidecar files**
- With committed recordings, run storage breakdown
- Assert at least one recording has trajectoryFileBytes > 0
- Fails if: file path resolution is broken in live KSP environment
