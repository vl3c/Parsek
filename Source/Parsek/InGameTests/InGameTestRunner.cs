using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using UnityEngine;

namespace Parsek.InGameTests
{
    public enum TestStatus { NotRun, Running, Passed, Failed, Skipped }

    /// <summary>
    /// One completed result captured for a given scene. Lets a single
    /// <see cref="InGameTestInfo"/> preserve its outcome from multiple
    /// scene runs (e.g. KSC Run All then Flight Run All) so the export
    /// file can show both instead of the last-run overwriting the first.
    /// </summary>
    public struct SceneResult
    {
        public TestStatus Status;
        public string ErrorMessage;
        public float DurationMs;
        public DateTime TimestampUtc;
    }

    public class InGameTestInfo
    {
        public string Category;
        public string Name;
        public string Description;
        public GameScenes RequiredScene;
        public MethodInfo Method;
        public Type DeclaringType;
        public bool RunLast;
        public bool AllowBatchExecution = true;
        public bool RestoreBatchFlightBaselineAfterExecution;
        public string BatchSkipReason;

        /// <summary>
        /// Last-observed status across any scene. Kept for UI compatibility
        /// (the in-game TestRunner window colors rows from this). For the
        /// export report, prefer <see cref="ResultsByScene"/> so multi-scene
        /// history is preserved.
        /// </summary>
        public TestStatus Status = TestStatus.NotRun;
        public string ErrorMessage;
        public float DurationMs;

        /// <summary>
        /// Per-scene result history. Populated each time the test finishes in
        /// a given scene; the entry is overwritten if the same test is re-run
        /// in the same scene (scene + test uniquely identifies the slot).
        /// Empty until the first run. Never null — always a fresh dictionary
        /// per test so concurrent reads during test-runner cleanup are safe.
        /// </summary>
        public Dictionary<GameScenes, SceneResult> ResultsByScene =
            new Dictionary<GameScenes, SceneResult>();
    }

    /// <summary>
    /// Discovers and executes [InGameTest]-attributed methods at runtime inside KSP.
    /// Attach to a MonoBehaviour to run coroutine-based (multi-frame) tests.
    /// </summary>
    public class InGameTestRunner
    {
        private const string Tag = "TestRunner";
        internal const string DefaultBatchSkipReason =
            "Single-run only — excluded from Run All / Run category because it performs a destructive scene transition. Run it from the row play button in a disposable session.";
        internal const string DefaultBatchRestoreNote =
            "Included in Run All + Isolated / Run+ with automatic FLIGHT restore — the runner captures a temporary baseline save and quickloads it after this destructive test. Use a disposable session.";
        private const float BatchBaselineStableMatchSeconds = 0.5f;
        private static readonly FieldInfo StageManagerSortRoutineField =
            typeof(KSP.UI.Screens.StageManager).GetField("sortRoutine",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo StageManagerRebuildIndexesField =
            typeof(KSP.UI.Screens.StageManager).GetField("rebuildIndexes",
                BindingFlags.Instance | BindingFlags.NonPublic);

        internal enum BatchIsolationMode { None, InMemoryAndDisk, DiskOnly }

        /// <summary>Two-part disk-revert outcome (persistent.sfs + Parsek/ sidecars are
        /// independent best-effort operations; a mixed result is NOT a correct disk).</summary>
        private struct DiskRevertResult
        {
            public bool PersistentReverted;
            public bool SidecarsReverted;
            public bool FullyReverted => PersistentReverted && SidecarsReverted;
        }

        /// <summary>Tiny info DTO (slot name + scene) so the restore-degraded alert
        /// message builder is unit-testable without a live FlightBatchBaselineState
        /// (which is a private nested type).</summary>
        internal struct FlightBatchBaselineState_SlotInfo
        {
            public string SlotName;
            public GameScenes CapturedScene;
        }

        private sealed class FlightBatchBaselineState
        {
            public string SlotName;
            // Scene the baseline was captured in (FLIGHT or TRACKSTATION). The
            // restore returns to this scene: FLIGHT via StartAndFocusVessel,
            // non-FLIGHT via CommitNonFlightSceneLoad + LoadScene. A batch's
            // restore-backed tests are scene-filtered to this scene, so the
            // prime/post-test restore always lands the player back where the
            // batch began.
            public GameScenes CapturedScene;
            public string ParsekSaveSnapshotDirectory;
            // Batch-start backup of the campaign's persistent.sfs (a sibling
            // .bak so it never shows in KSP's load menu), restored at teardown.
            // Tests can write persistent.sfs directly (the scene-exit merge
            // tests) or trigger a KSP auto-save on a scene change; restoring the
            // batch-start bytes guarantees the campaign's main save is left
            // exactly as it was. Null when persistent.sfs was absent or the
            // backup could not be taken (the run then logs a warning).
            public string PersistentSaveBackupPath;
            public int CapturedFlightInstanceId;
            public Guid ActiveVesselId;
            public string ActiveVesselName;
            public Vessel.Situations ActiveVesselSituation;
            // P2 review fix: snapshot the live RecordingStore state at batch
            // capture time, BEFORE any test runs. This is the rollback
            // target for RestoreBatchFlightBaselineCore's fail-closed path.
            // Per-attempt snapshots from inside the restore call were
            // correct for the pre-prime path (RecordingStore is still in
            // the player's pre-batch state) but WRONG for the post-test
            // path (the just-executed test may have layered synthetic
            // mutations on top of the player's data; rolling back would
            // preserve those mutations rather than the batch baseline).
            // One snapshot at batch capture time, used for every rollback
            // during the batch, gives consistent recovery to the player's
            // pre-batch state regardless of which test triggered the
            // restore.
            public RecordingStoreTestSnapshot RecordingStoreSnapshot;
        }

        internal sealed class StagedBatchFlightParsekSaveSnapshot : IDisposable
        {
            private readonly string currentParsekSaveDirectory;
            private readonly string restoreStagingDirectory;
            private readonly string backupDirectory;
            private bool currentMovedToBackup;
            private bool activated;

            internal StagedBatchFlightParsekSaveSnapshot(
                string currentParsekSaveDirectory,
                string restoreStagingDirectory,
                string backupDirectory)
            {
                this.currentParsekSaveDirectory = currentParsekSaveDirectory;
                this.restoreStagingDirectory = restoreStagingDirectory;
                this.backupDirectory = backupDirectory;
            }

            internal void Activate()
            {
                if (activated)
                    return;

                bool stagedSnapshotMovedIntoPlace = false;
                try
                {
                    if (Directory.Exists(currentParsekSaveDirectory))
                    {
                        DeleteDirectoryRecursive(backupDirectory);
                        Directory.Move(currentParsekSaveDirectory, backupDirectory);
                        currentMovedToBackup = true;
                    }

                    Directory.Move(restoreStagingDirectory, currentParsekSaveDirectory);
                    stagedSnapshotMovedIntoPlace = true;
                    activated = true;

                    if (currentMovedToBackup)
                        DeleteDirectoryRecursive(backupDirectory);
                }
                catch
                {
                    if (stagedSnapshotMovedIntoPlace && Directory.Exists(currentParsekSaveDirectory))
                    {
                        TryDeleteDirectoryRecursive(currentParsekSaveDirectory);
                    }

                    if (currentMovedToBackup && Directory.Exists(backupDirectory))
                    {
                        if (Directory.Exists(currentParsekSaveDirectory))
                            TryDeleteDirectoryRecursive(currentParsekSaveDirectory);

                        Directory.Move(backupDirectory, currentParsekSaveDirectory);
                    }

                    throw;
                }
            }

            public void Dispose()
            {
                if (Directory.Exists(restoreStagingDirectory))
                    TryDeleteDirectoryRecursive(restoreStagingDirectory);

                if (Directory.Exists(backupDirectory))
                    TryDeleteDirectoryRecursive(backupDirectory);
            }
        }

        private List<InGameTestInfo> allTests;
        private readonly List<GameObject> cleanupRegistry = new List<GameObject>();
        private MonoBehaviour coroutineHost;
        private bool isRunning;
        private Coroutine activeCoroutine;
        private Coroutine activeTestCoroutine;
        private Coroutine activeInnerCoroutine;
        private FlightBatchBaselineState batchFlightBaseline;
        private bool batchFlightBaselinePrimed;
        private bool abortBatchAfterRestoreFailure;
        private bool preserveBatchFlightBaselineArtifacts;
        private string preservedBatchFlightBaselineReason;
        private BatchIsolationMode batchIsolationMode = BatchIsolationMode.None;
        private string diskOnlyPersistentBackupPath;   // DiskOnly mode (EDITOR / SPACECENTER / FLIGHT-no-vessel)
        private bool finalBatchRestoreDone;             // guards double final restore
        private bool stateDirtySinceLastRestore;        // a test body ran since the last successful baseline restore
        private Guid batchInstanceId;                   // monotonic-per-batch token, stamped on the marker (defensive same-process guard)

        // Unhandled-exception storm guard (flight-state corruption detector). A save-load into
        // a broken flight scene (e.g. the stock FlightCamera.SetModeImmediate NRE during
        // FlightDriver.Start) can leave FlightGlobals half-initialized so KSP AND every per-frame
        // mod (Waterfall / BetterTimeWarp / ...) NRE-flood every frame. Reloading the flight scene
        // for a baseline restore re-runs that broken stock bootstrap and sustains the flood, so
        // once a storm is detected the batch aborts via the existing disk-only-revert path instead
        // of another flight-scene reload. Monitored only for the duration of a batch.
        private bool batchExceptionMonitorActive;
        private int batchUnhandledExceptionCount;
        private bool batchExceptionStormDetected;
        // Once-per-batch guard for the post-corruption Space Center bounce recovery.
        // Reset at RunBatch entry; set when the batch-end flood check decides to
        // bounce. EXACTLY ONE bounce, never retried (2026-07-05: the corruption is
        // process-permanent for the FLIGHT scene; a reload-retry loop is the
        // disproven model - 4 retries all flooded and made a 469MB log).
        private bool spaceCenterBounceAttempted;

        // The selector that produced the current batch, threaded into the H3
        // BATCH_COMPLETE line's category= token (module M-A3, correction G4). RunBatch
        // has no selector of its own, so each public entry point sets this before it
        // starts the coroutine: "all" for the run-all paths, the category name for the
        // run-category paths, "single" for RunSingle. Read once in the batch-end
        // region; defaults to "all" so a batch that somehow started without setting it
        // still emits a non-stale token.
        private string currentBatchSelector = "all";

        // [M-A3 P4.1] Autorun handoff (design "wasAutorunBatch handoff mechanism").
        // H1 calls MarkNextBatchAutorun IMMEDIATELY before RunAll / RunCategory; RunBatch
        // latches the two pending flags into the per-batch wasAutorunBatch /
        // autorunExitArmedThisBatch at batch start and clears the pending flags, so the
        // mark applies to exactly the next batch and never leaks to a later
        // human-initiated one (edge 13). The per-batch pair is read in the batch-end
        // region by H2 (P5.1). A human clicking Run All never calls MarkNextBatchAutorun,
        // so its batch latches wasAutorunBatch=false and H2 never quits KSP under them.
        private bool pendingAutorunBatch;
        private bool pendingAutorunExit;
        private bool wasAutorunBatch;
        private bool autorunExitArmedThisBatch;

        // Results summary
        public int Passed { get; private set; }
        public int Failed { get; private set; }
        public int Skipped { get; private set; }
        public bool IsRunning => isRunning;

        public IReadOnlyList<InGameTestInfo> Tests => allTests;

        internal static string FormatCoroutineState(
            bool isRunning, bool hasBatchCoroutine, bool hasInnerCoroutine)
        {
            return $"isRunning={isRunning} " +
                   $"batch={(hasBatchCoroutine ? "active" : "null")} " +
                   $"inner={(hasInnerCoroutine ? "active" : "null")}";
        }

        internal string DescribeCoroutineState()
        {
            return FormatCoroutineState(
                isRunning,
                activeCoroutine != null,
                activeInnerCoroutine != null);
        }

        public InGameTestRunner(MonoBehaviour host)
        {
            coroutineHost = host;
            DiscoverTests();
        }

        /// <summary>
        /// Register a GameObject for cleanup after the current test.
        /// Call this from tests that create scene objects.
        /// </summary>
        public void TrackForCleanup(GameObject go)
        {
            if (go != null) cleanupRegistry.Add(go);
        }

        private void DiscoverTests()
        {
            allTests = new List<InGameTestInfo>();

            // Scan the executing assembly for [InGameTest] methods
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    var attr = method.GetCustomAttribute<InGameTestAttribute>();
                    if (attr == null) continue;

                    allTests.Add(new InGameTestInfo
                    {
                        Category = attr.Category ?? "General",
                        Name = $"{type.Name}.{method.Name}",
                        Description = attr.Description,
                        RequiredScene = attr.Scene,
                        Method = method,
                        DeclaringType = type,
                        RunLast = attr.RunLast,
                        AllowBatchExecution = attr.AllowBatchExecution,
                        RestoreBatchFlightBaselineAfterExecution = attr.RestoreBatchFlightBaselineAfterExecution,
                        BatchSkipReason = attr.BatchSkipReason
                    });
                }
            }

            allTests = OrderForBatchExecution(allTests);
            ParsekLog.Info(Tag, $"Discovered {allTests.Count} in-game tests");
        }

        /// <summary>
        /// [M-A3 hooks H1/H2] Marks the NEXT batch as an autorun batch (design
        /// "wasAutorunBatch handoff mechanism"). Called by TestRunnerShortcut's H1 fire
        /// path immediately before RunAll / RunCategory. <paramref name="exitAfterBatch"/>
        /// carries the parsed PARSEK_AUTORUN_EXIT arming so the batch-end H2 decision can
        /// quit without the runner re-reading the environment. The mark is latched into
        /// exactly the next batch at RunBatch start and cleared there, so a human clicking
        /// Run All (which never calls this) latches wasAutorunBatch=false and never quits
        /// KSP (edge 13). Inert when never called.
        /// </summary>
        internal void MarkNextBatchAutorun(bool exitAfterBatch)
        {
            pendingAutorunBatch = true;
            pendingAutorunExit = exitAfterBatch;
            ParsekLog.Verbose(Tag, $"next batch marked autorun (exitAfterBatch={exitAfterBatch})");
        }

        public void RunAll()
        {
            if (isRunning) return;
            currentBatchSelector = "all";
            ResetBatchIsolationState();
            PerformBetweenRunCleanup("run-all");
            var eligible = PrepareBatchExecution(FilterSceneEligibleBatchCandidates(allTests));
            RecountResults();
            CaptureBatchBaseline(ClassifyBatchIsolationMode(
                HighLogic.LoadedScene, HighLogic.CurrentGame != null,
                !string.IsNullOrEmpty(HighLogic.SaveFolder), FlightGlobals.ActiveVessel != null));
            activeCoroutine = coroutineHost.StartCoroutine(RunBatch(eligible));
        }

        public void RunAllIncludingFlightRestore()
        {
            if (isRunning) return;
            currentBatchSelector = "all";
            ResetBatchIsolationState();
            PerformBetweenRunCleanup("run-all+restore");
            var eligible = PrepareBatchExecutionIncludingFlightRestore(
                FilterSceneEligibleBatchCandidates(allTests));
            CaptureBatchBaseline(ClassifyBatchIsolationMode(
                HighLogic.LoadedScene, HighLogic.CurrentGame != null,
                !string.IsNullOrEmpty(HighLogic.SaveFolder), FlightGlobals.ActiveVessel != null));
            eligible = PrepareBatchFlightRestoreExecution(eligible);
            RecountResults();
            activeCoroutine = coroutineHost.StartCoroutine(RunBatch(eligible));
        }

        public void RunCategory(string category)
        {
            if (isRunning) return;
            currentBatchSelector = category ?? "(null)";
            ResetBatchIsolationState();
            PerformBetweenRunCleanup("run-category:" + (category ?? "(null)"));
            var eligible = PrepareBatchExecution(FilterSceneEligibleBatchCandidates(
                allTests.Where(t => t.Category == category)));
            RecountResults();
            CaptureBatchBaseline(ClassifyBatchIsolationMode(
                HighLogic.LoadedScene, HighLogic.CurrentGame != null,
                !string.IsNullOrEmpty(HighLogic.SaveFolder), FlightGlobals.ActiveVessel != null));
            activeCoroutine = coroutineHost.StartCoroutine(RunBatch(eligible));
        }

        public void RunCategoryIncludingFlightRestore(string category)
        {
            if (isRunning) return;
            currentBatchSelector = category ?? "(null)";
            ResetBatchIsolationState();
            PerformBetweenRunCleanup("run-category+restore:" + (category ?? "(null)"));
            var eligible = PrepareBatchExecutionIncludingFlightRestore(
                FilterSceneEligibleBatchCandidates(allTests.Where(t => t.Category == category)));
            CaptureBatchBaseline(ClassifyBatchIsolationMode(
                HighLogic.LoadedScene, HighLogic.CurrentGame != null,
                !string.IsNullOrEmpty(HighLogic.SaveFolder), FlightGlobals.ActiveVessel != null));
            eligible = PrepareBatchFlightRestoreExecution(eligible);
            RecountResults();
            activeCoroutine = coroutineHost.StartCoroutine(RunBatch(eligible));
        }

        public void RunSingle(InGameTestInfo test)
        {
            if (isRunning) return;
            currentBatchSelector = "single";
            ResetBatchIsolationState();
            var single = new List<InGameTestInfo> { test };
            CaptureBatchBaseline(ClassifyBatchIsolationMode(
                HighLogic.LoadedScene, HighLogic.CurrentGame != null,
                !string.IsNullOrEmpty(HighLogic.SaveFolder), FlightGlobals.ActiveVessel != null));
            // Mirror RunAllIncludingFlightRestore: a restore-after-run test must
            // NOT execute when its baseline is unavailable (capture threw and was
            // swallowed, or the scene classifies to disk-only / none), because it
            // would mutate live FLIGHT state with nothing to revert it. Without
            // this guard the row play button ran such a test on a null baseline
            // and the post-test restore silently no-oped. PrepareBatchFlightRestoreExecution
            // marks the test Skipped with the baseline-unavailable reason and
            // drops it from the batch; a non-restore test passes through unchanged.
            single = PrepareBatchFlightRestoreExecution(single);
            RecountResults();
            activeCoroutine = coroutineHost.StartCoroutine(RunBatch(single));
        }

        /// <summary>
        /// Between-run cleanup hook invoked before Run All / Run Category (#417/#418).
        /// Destroys any ghosts and ghost-map ProtoVessels left alive from a previous
        /// batch so the next pass starts from a known-empty state. Without this, the
        /// second Run All spawns fresh ghosts on top of the first run's residue,
        /// which compounds GhostCount (tripping GhostCountReasonable's 200-cap) and
        /// races the ghost-map vs. ProtoVessel registration order (tripping
        /// GhostPidsResolveToProtoVessels).
        ///
        /// Order (matches the rewind path in ParsekFlight.DestroyAllTimelineGhosts):
        /// 1. Exit watch mode first (skip camera restore) so stock camera state does not
        ///    retain a destroyed ghost transform across the next frame.
        /// 2. Delegate to ParsekFlight.Instance.DestroyAllTimelineGhosts() when available
        ///    — this does RemoveAllGhostVessels (vessel.Die + dict clear) followed by
        ///    engine.DestroyAllGhosts (primary + overlap GO destroy + engine dict clear).
        /// 3. Safety net: ResetBetweenTestRuns on GhostMapPresence, idempotent and
        ///    synchronous, catches any PID that lingered past step 1 (e.g. if Die()
        ///    throws, or if we are not in the flight scene and ParsekFlight.Instance is
        ///    null but ghost-map bookkeeping is somehow non-empty from a previous scene).
        ///
        /// Idempotent: calling with zero ghosts just emits verbose no-op logs.
        /// Exceptions from Die() or engine cleanup are swallowed so a single broken
        /// ghost cannot abort the test run.
        /// </summary>
        internal void PerformBetweenRunCleanup(string reason)
        {
            ParsekLog.Info(Tag,
                $"PerformBetweenRunCleanup: begin reason={reason} scene={HighLogic.LoadedScene}");

            int ghostsBefore = 0;
            int mapPidsBefore = GhostMapPresence.ghostMapVesselPids.Count;

            var flight = ParsekFlight.Instance;
            if (flight != null)
            {
                try
                {
                    flight.ExitWatchModeBeforeTimelineGhostCleanup(
                        $"PerformBetweenRunCleanup:{reason}");
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag,
                        $"PerformBetweenRunCleanup: watch exit before cleanup threw: {ex.Message}");
                }

                try
                {
                    ghostsBefore = flight.Engine?.GhostCount ?? 0;
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag,
                        $"PerformBetweenRunCleanup: failed to read pre-cleanup GhostCount: {ex.Message}");
                }

                try
                {
                    flight.DestroyAllTimelineGhosts();
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag,
                        $"PerformBetweenRunCleanup: DestroyAllTimelineGhosts threw: {ex.Message}");
                }
            }
            else
            {
                ParsekLog.Verbose(Tag,
                    "PerformBetweenRunCleanup: no ParsekFlight.Instance — skipping flight-scene ghost teardown");
            }

            // Safety net: clear any ghost-map bookkeeping that survived step 1.
            // Also covers the case where ParsekFlight.Instance was null but
            // GhostMapPresence dicts are non-empty from a previous scene.
            try
            {
                GhostMapPresence.ResetBetweenTestRuns(reason);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"PerformBetweenRunCleanup: ResetBetweenTestRuns threw: {ex.Message}");
            }

            int mapPidsAfter = GhostMapPresence.ghostMapVesselPids.Count;
            ParsekLog.Info(Tag,
                $"PerformBetweenRunCleanup: end reason={reason} " +
                $"ghostsBefore={ghostsBefore} mapPidsBefore={mapPidsBefore} mapPidsAfter={mapPidsAfter}");

            // Safety net: a prior run (or a user Cancel) may have left a stock
            // Space Center facility building open and the game paused. Force it
            // closed so the next batch starts from a clean Space Center.
            ForceCloseOpenSpaceCenterFacilities(reason);
        }

        /// <summary>
        /// Scene-debris sweep for the abortBatchAfterRestoreFailure batch ending.
        /// The abort break skips both the per-test scene reload and the final
        /// in-memory restore, which are the paths that normally clear test debris,
        /// so without this the live scene keeps leftover cleanupRegistry objects
        /// and timeline ghost visuals until the next Run* entry (2026-07-10: a
        /// green fallback ghost sphere was left riding the vessel). Destroys the
        /// tracked cleanupRegistry objects (the same loop RunCleanup uses) and
        /// delegates ghost teardown to the idempotent PerformBetweenRunCleanup
        /// (watch-mode exit + DestroyAllTimelineGhosts + ghost-map reset). Runs
        /// only from RunBatch's always-runs batch-end region, never mid-batch;
        /// every step is exception-safe so a broken object cannot abort teardown.
        ///
        /// The orphaned-ghost-mesh sweep runs BEFORE the live-engine teardown:
        /// while the live engine still owns its ghosts, OwnsGhostGameObject
        /// correctly skips them and only genuinely unowned orphans are swept
        /// (review of PR #1286: DestroyAllGhosts clears ghostStates/overlapGhosts
        /// immediately but Object.Destroy is end-of-frame-deferred, so a sweep
        /// AFTER the teardown would see the live engine's still-enumerable
        /// meshes as unowned, re-destroy them harmlessly, and pollute the
        /// orphanedGhostMeshes counter with false positives). A ghost visual
        /// spawned by a test-private GhostPlaybackEngine instance abandoned
        /// without DestroyAllGhosts is unowned regardless of ordering
        /// (2026-07-10 rerun2: the "Re-Fly Settle Anchor" sphere-fallback ghost
        /// stayed riding the vessel while DestroyAllGhosts reported 0 entries).
        /// The sweep scans root GameObjects for the engine's "Parsek_Timeline_"
        /// naming convention; scene-scan cost is irrelevant here: post-abort,
        /// one-shot, never per-frame.
        /// </summary>
        private void PerformPostAbortSceneCleanup(string reason)
        {
            int destroyed = 0;
            try
            {
                foreach (var go in cleanupRegistry)
                {
                    if (go != null)
                    {
                        UnityEngine.Object.Destroy(go);
                        destroyed++;
                    }
                }
                cleanupRegistry.Clear();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Post-abort scene cleanup: cleanupRegistry destroy threw: {ex.Message}");
            }

            // Sweep orphans FIRST (see the ordering note in the doc comment): the
            // live engine must still own its ghosts for the ownership skip to work.
            int orphanedGhostMeshes = 0;
            try
            {
                orphanedGhostMeshes = DestroyOrphanedGhostMeshes();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Post-abort scene cleanup: orphaned ghost mesh sweep threw: {ex.Message}");
            }

            try
            {
                PerformBetweenRunCleanup("post-abort:" + reason);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Post-abort scene cleanup: PerformBetweenRunCleanup threw: {ex.Message}");
            }

            ParsekLog.Info(Tag,
                $"Post-abort scene cleanup ({reason}): destroyed {destroyed} tracked object(s), " +
                $"ghosts cleared, orphanedGhostMeshes={orphanedGhostMeshes}");
        }

        /// <summary>
        /// Pure name predicate for the post-abort orphaned-ghost-mesh sweep. The
        /// playback engine names every ghost visual root (snapshot mesh AND sphere
        /// fallback) "Parsek_Timeline_{index}" (GhostPlaybackEngine spawn path), so
        /// a root GameObject with that prefix that no engine references is an
        /// abandoned ghost visual. Ordinal, prefix-only: test scaffolding objects
        /// ("ParsekTestGhost_...") and unrelated Parsek objects never match.
        /// </summary>
        internal static bool IsOrphanedGhostMeshName(string name)
        {
            return name != null
                && name.StartsWith("Parsek_Timeline_", StringComparison.Ordinal);
        }

        /// <summary>
        /// Destroys root GameObjects that carry the engine's ghost-mesh naming
        /// convention but are not referenced by the live engine. Post-abort
        /// one-shot only (scene root scan); returns the destroyed count for the
        /// cleanup summary line. Per-item Info logging is fine here: orphans are
        /// rare (one leaked sphere per incident, not a per-frame population).
        /// </summary>
        private int DestroyOrphanedGhostMeshes()
        {
            var engine = ParsekFlight.Instance?.Engine;
            int destroyedCount = 0;
            GameObject[] roots =
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            foreach (var go in roots)
            {
                if (go == null || !IsOrphanedGhostMeshName(go.name))
                    continue;
                if (engine != null && engine.OwnsGhostGameObject(go))
                    continue;
                ParsekLog.Info(Tag,
                    $"Post-abort orphan sweep: destroying unreferenced ghost mesh '{go.name}' " +
                    $"(active={go.activeSelf})");
                UnityEngine.Object.Destroy(go);
                destroyedCount++;
            }
            return destroyedCount;
        }

        /// <summary>
        /// Pure gate for the facility force-close safety net. Stock Space Center
        /// facility buildings (R&amp;D / Astronaut Complex / Mission Control /
        /// Administration) can only be open in the SPACECENTER scene, so the
        /// safety net is a no-op everywhere else; the cheap scene check keeps
        /// FLIGHT / TRACKSTATION cleanup from paying for FindObjectOfType scans.
        /// </summary>
        internal static bool ShouldForceCloseSpaceCenterFacilities(GameScenes scene)
        {
            return scene == GameScenes.SPACECENTER;
        }

        internal static string FormatFacilityForceCloseSummary(int closedCount, string reason)
        {
            return $"ForceCloseOpenSpaceCenterFacilities: closed {closedCount} " +
                $"open facility(ies) reason={reason ?? "(null)"}";
        }

        /// <summary>
        /// Safety net that force-closes any stock Space Center facility building
        /// (R&amp;D / Astronaut Complex / Mission Control; Administration is
        /// deliberately omitted -- see the call site) left open by a test. The
        /// Phase-5 StockUiOverlay tests open a facility
        /// canvas (which pauses the game) and close it in their own try/finally;
        /// the runner disposes the test iterator via <see cref="RunCoroutineSafely"/>
        /// so that finally fires even on a failed assertion. But a user
        /// <see cref="Cancel"/> stops the coroutine with Unity StopCoroutine,
        /// which does NOT run the iterator's finally, and a despawn event that
        /// no-ops in some game state has the same effect: the player is left
        /// stuck inside the building with the game paused and no automatic
        /// return to the Space Center (SPACECENTER batches capture no FLIGHT
        /// baseline to restore). Firing the stock despawn GameEvent is exactly
        /// what UISpaceCenter's exit button does -- UISpaceCenter listens for it
        /// to unpause and tear down the canvas.
        ///
        /// Each fire is gated on the facility actually being open
        /// (FindObjectOfType != null) so a despawn event is never fired when
        /// nothing is open (other listeners, including Parsek's own overlay
        /// controller, react to it). Idempotent and swallow-on-throw: one broken
        /// facility close cannot abort cleanup.
        /// </summary>
        internal void ForceCloseOpenSpaceCenterFacilities(string reason)
        {
            if (!ShouldForceCloseSpaceCenterFacilities(HighLogic.LoadedScene))
                return;

            int closed = 0;
            closed += TryForceCloseFacility<KSP.UI.Screens.RDController>("R&D", controller =>
            {
                KSP.UI.Screens.RDController.OnRDTreeDespawn.Fire(controller);
                GameEvents.onGUIRnDComplexDespawn.Fire();
            });
            closed += TryForceCloseFacility<KSP.UI.Screens.AstronautComplex>("Astronaut Complex",
                _ => GameEvents.onGUIAstronautComplexDespawn.Fire());
            closed += TryForceCloseFacility<KSP.UI.Screens.MissionControl>("Mission Control",
                _ => GameEvents.onGUIMissionControlDespawn.Fire());
            // Administration is intentionally excluded: no test opens it as a
            // real building (the StockUiOverlay tests open R&D / Astronaut
            // Complex / Mission Control), and the strategy-lifecycle canaries
            // create a HIDDEN, disabled Administration canvas that still sets
            // Administration.Instance and stays activeInHierarchy. Force-closing
            // by FindObjectOfType<Administration> would fire a spurious
            // onGUIAdministrationFacilityDespawn into that hidden canvas, which
            // there is no clean programmatic way to distinguish from a real open
            // building. Since nothing here leaves a real Administration open,
            // omitting it removes the only false-positive path with no loss.
            //
            // The FindObjectOfType detector for the three included facilities
            // (R&D / Astronaut Complex / Mission Control) is valid on the same
            // audited invariant, in reverse: no current test creates a HIDDEN
            // activeInHierarchy canvas for any of those three (only the
            // Administration strategy canaries do that), so a non-null
            // FindObjectOfType<T> means a really-open building. If a future
            // test ever leaves a hidden-active R&D/AC/MC canvas, revisit this.

            if (closed > 0)
                ParsekLog.Info(Tag, FormatFacilityForceCloseSummary(closed, reason));
            else
                ParsekLog.Verbose(Tag,
                    $"ForceCloseOpenSpaceCenterFacilities: no open facilities reason={reason ?? "(null)"}");
        }

        private static int TryForceCloseFacility<T>(string facilityName, Action<T> fireDespawn)
            where T : UnityEngine.Object
        {
            T open = UnityEngine.Object.FindObjectOfType<T>();
            if (open == null)
                return 0;

            try
            {
                ParsekLog.Info(Tag,
                    $"ForceCloseOpenSpaceCenterFacilities: closing open {facilityName} facility canvas");
                fireDespawn(open);
                return 1;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"ForceCloseOpenSpaceCenterFacilities: {facilityName} close threw: {ex.Message}");
                return 0;
            }
        }

        public void Cancel()
        {
            if (!isRunning) return;
            EndBatchExceptionMonitor(); // StopCoroutine below won't run RunBatch's normal-end teardown
            // Same StopCoroutine-skips-teardown reasoning: a cancel mid-reload would
            // otherwise leave the Bug #4803 camera re-pin handlers subscribed until
            // their TTL fail-safe. Idempotent; the cancel-restore below re-arms it
            // for its own reload commit.
            Helpers.FlightCameraReloadPin.Disarm("test-run-cancelled");
            if (activeInnerCoroutine != null)
                coroutineHost.StopCoroutine(activeInnerCoroutine);
            activeInnerCoroutine = null;
            if (activeTestCoroutine != null)
                coroutineHost.StopCoroutine(activeTestCoroutine);
            activeTestCoroutine = null;
            if (activeCoroutine != null)
                coroutineHost.StopCoroutine(activeCoroutine);
            activeCoroutine = null;

            // Unity StopCoroutine above abandons the test iterator WITHOUT
            // running its try/finally, so a cancel while a building-entry test
            // is mid-flight would leave the facility canvas open and the game
            // paused. Force any open facility closed so cancel always returns
            // to a usable Space Center. Gated to SPACECENTER, so the FLIGHT
            // baseline-restore-on-cancel path below is unaffected.
            ForceCloseOpenSpaceCenterFacilities("test-run-cancelled");

            if (batchFlightBaseline != null)
            {
                var baseline = batchFlightBaseline;
                int previousFlightInstanceId = ParsekFlight.Instance != null
                    ? ParsekFlight.Instance.GetInstanceID()
                    : baseline.CapturedFlightInstanceId;
                ParsekLog.Info(Tag,
                    $"Test run cancelled; restoring isolated batch baseline from slot '{baseline.SlotName}'");
                activeCoroutine = coroutineHost.StartCoroutine(
                    RestoreBatchBaselineAfterCancel(baseline, previousFlightInstanceId));
                return;
            }

            if (batchIsolationMode == BatchIsolationMode.DiskOnly)
            {
                // No in-memory restore by design; revert the on-disk persistent.sfs +
                // sweep, clear the durable marker, synchronously.
                TeardownDiskOnlyIsolation("cancel");
                ClearTestBatchMarker("cancel");
                batchIsolationMode = BatchIsolationMode.None;
                diskOnlyPersistentBackupPath = null;
                batchInstanceId = Guid.Empty;
                isRunning = false;
                abortBatchAfterRestoreFailure = false;
                ParsekLog.Info(Tag, "Test run cancelled (disk-only isolation reverted)");
                return;
            }

            isRunning = false;
            abortBatchAfterRestoreFailure = false;
            ParsekLog.Info(Tag, "Test run cancelled");
        }

        // Number of unhandled Unity exceptions during a batch that marks a corrupted flight
        // state (a healthy batch logs ~0 unhandled exceptions; a corrupted flight scene floods
        // thousands per second). Set well above any legitimate single-batch exception count.
        internal const int BatchExceptionStormThreshold = 1000;

        /// <summary>
        /// True when the unhandled-exception count logged during a batch has crossed the storm
        /// threshold — the signature of a corrupted flight state (a stock/mod NRE flooding every
        /// frame). Pure; a non-positive threshold disables the guard.
        /// </summary>
        internal static bool IsExceptionStorm(int unhandledExceptionCount, int threshold)
        {
            return threshold > 0 && unhandledExceptionCount >= threshold;
        }

        // Frames sampled after a reload to detect whether the FlightCamera NRE flood is active.
        private const int BaselineReloadHealthSettleFrames = 8;
        // Unhandled exceptions within the settle window above which the reload is treated as still
        // flooding. A clean reload adds ~0; a corrupted one floods hundreds even across a few frames.
        internal const int BaselineReloadFloodExceptionThreshold = 50;

        /// <summary>
        /// True when the unhandled-exception count sampled across a post-reload settle window shows the
        /// stock FlightCamera NRE flood (Bug #4803) is active (a clean reload adds ~0). Pure; a
        /// non-positive threshold disables the check.
        /// </summary>
        internal static bool ReloadStillFlooding(int exceptionsInSettleWindow, int threshold)
        {
            return threshold > 0 && exceptionsInSettleWindow >= threshold;
        }

        /// <summary>
        /// True when the one-shot post-batch Space Center bounce recovery should run:
        /// the batch-end settle window still shows the stock Bug #4803 NRE flood AND no
        /// bounce has been attempted this batch. EXACTLY ONE bounce per batch, never a
        /// retry loop: the corruption is process-permanent for the FLIGHT scene
        /// (confirmed in-game 2026-07-05; reload retries only multiply the flood) and a
        /// single LoadScene(SPACECENTER) is the only in-process recovery. Pure; the
        /// threshold semantics are <see cref="ReloadStillFlooding"/>'s (non-positive
        /// threshold disables the check).
        /// </summary>
        internal static bool ShouldAttemptSpaceCenterBounce(
            bool alreadyAttemptedThisBatch, int exceptionsInSettleWindow, int threshold)
        {
            return !alreadyAttemptedThisBatch
                && ReloadStillFlooding(exceptionsInSettleWindow, threshold);
        }

        /// <summary>
        /// True when the batch-end corruption check should sample the flood detector at
        /// all: only batches that could have corrupted the scene are eligible - a batch
        /// with an in-memory baseline (captured in FLIGHT or the Tracking Station)
        /// performs scene reloads (each one can trip stock Bug #4803), and a detected
        /// exception storm is the corruption signature regardless of mode. A plain
        /// disk-only batch (SPACECENTER / EDITOR / FLIGHT-no-vessel) with no storm never
        /// reloads a scene, so sampling would only add settle-window frames and risk
        /// bouncing on an unrelated mod's error flood. Accepted coarseness: this keys on
        /// "baseline captured", not "a reload actually happened", so an in-memory-
        /// baseline batch with zero restores still samples; blast radius is one Space
        /// Center visit. Pure.
        /// </summary>
        internal static bool ShouldSampleBatchEndCorruption(bool hasInMemoryBaseline, bool stormDetected)
        {
            return hasInMemoryBaseline || stormDetected;
        }

        // Subscribe the batch unhandled-exception counter and reset the count so each batch starts
        // clean. Unsubscribe-before-subscribe guarantees EXACTLY ONE handler even if a prior batch
        // leaked its subscription (RunBatch is a Unity coroutine; a synchronous throw in it would
        // skip the normal-end unsubscribe, and StopCoroutine on cancel does not run a finally) —
        // -= on an absent handler is a safe no-op, so no leaked handler can accumulate across batches.
        private void BeginBatchExceptionMonitor()
        {
            batchUnhandledExceptionCount = 0;
            batchExceptionStormDetected = false;
            Application.logMessageReceived -= OnBatchLogMessage;
            Application.logMessageReceived += OnBatchLogMessage;
            batchExceptionMonitorActive = true;
        }

        // Unsubscribe the counter (idempotent). Called from RunBatch's normal end AND from
        // Cancel(): Unity StopCoroutine abandons the RunBatch iterator without running any
        // finally, so the normal-end unsubscribe would otherwise leak the handler on cancel.
        private void EndBatchExceptionMonitor()
        {
            if (!batchExceptionMonitorActive)
                return;
            Application.logMessageReceived -= OnBatchLogMessage;
            batchExceptionMonitorActive = false;
        }

        // Main-thread Unity log callback: count unhandled exceptions (LogType.Exception) and hard
        // errors (LogType.Error). Cheap (an int increment); registered only during a batch.
        private void OnBatchLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Exception || type == LogType.Error)
                batchUnhandledExceptionCount++;
        }

        // Loud WARN + on-screen alert on a storm abort so the user knows the batch stopped because
        // the flight state is corrupted (NOT because a test is at fault) and that KSP must be
        // relaunched to recover.
        private void NotifyExceptionStormAbort(InGameTestInfo test, int exceptionCount)
        {
            string testName = test != null ? test.Name : "<test>";
            ParsekLog.Warn(Tag,
                $"NRE storm detected ({exceptionCount}+ unhandled exceptions) after {testName} — the flight state is "
                + "corrupted (a stock/mod NRE flooding every frame, NOT the test). Aborting the batch WITHOUT another "
                + "flight-scene reload and reverting the campaign save on disk; close and relaunch KSP to recover.");
            try
            {
                ScreenMessages.PostScreenMessage(
                    "[Parsek] Test batch aborted: flight-state NRE storm detected. Relaunch KSP to recover.",
                    12f, ScreenMessageStyle.UPPER_CENTER);
            }
            catch
            {
                // ScreenMessages can be unavailable mid-teardown; the WARN above is the durable record.
            }
        }

        // Detect an unhandled-exception storm and, on the first detection, flag the batch for the
        // disk-only abort path (abortBatchAfterRestoreFailure) so no further flight-scene reload
        // runs and the loop breaks. Returns true only on the transition into the storm state.
        private bool TryDetectExceptionStorm(InGameTestInfo test)
        {
            if (batchExceptionStormDetected
                || !IsExceptionStorm(batchUnhandledExceptionCount, BatchExceptionStormThreshold))
            {
                return false;
            }
            batchExceptionStormDetected = true;
            abortBatchAfterRestoreFailure = true;
            NotifyExceptionStormAbort(test, batchUnhandledExceptionCount);
            return true;
        }

        /// <summary>
        /// Clear the live (top-level) Status / ErrorMessage / DurationMs on every
        /// discovered test so the in-game table shows blank rows for the about-to-
        /// start batch. The per-scene <see cref="InGameTestInfo.ResultsByScene"/>
        /// history is deliberately preserved so a Run All in scene A followed by
        /// Run All in scene B accumulates BOTH scenes' outcomes in the export file.
        /// Use <see cref="ClearAllSceneHistory"/> for a genuine wipe.
        /// </summary>
        public void ResetResults()
        {
            foreach (var t in allTests)
            {
                t.Status = TestStatus.NotRun;
                t.ErrorMessage = null;
                t.DurationMs = 0;
            }
            Passed = 0;
            Failed = 0;
            Skipped = 0;
        }

        /// <summary>
        /// Scoped version of <see cref="ResetResults"/> — only touches tests in the
        /// given category. Also preserves per-scene history.
        /// </summary>
        public void ResetCategory(string category)
        {
            foreach (var t in allTests)
            {
                if (t.Category != category) continue;
                t.Status = TestStatus.NotRun;
                t.ErrorMessage = null;
                t.DurationMs = 0;
            }
            RecountResults();
        }

        /// <summary>
        /// Full history wipe — clears both the live top-level fields AND the
        /// per-scene <see cref="InGameTestInfo.ResultsByScene"/> dictionary.
        /// Use when the user explicitly wants a fresh session (e.g. a dedicated
        /// "Clear All History" button) or from test teardown.
        /// </summary>
        public void ClearAllSceneHistory()
        {
            foreach (var t in allTests)
            {
                t.Status = TestStatus.NotRun;
                t.ErrorMessage = null;
                t.DurationMs = 0;
                t.ResultsByScene.Clear();
            }
            Passed = 0;
            Failed = 0;
            Skipped = 0;
        }

        private bool IsEligibleForScene(InGameTestInfo test)
        {
            if (test.RequiredScene == InGameTestAttribute.AnyScene) return true;
            return test.RequiredScene == HighLogic.LoadedScene;
        }

        internal static string FormatSceneEligibilitySkipSummary(
            int skipped,
            GameScenes currentScene,
            IDictionary<GameScenes, int> skippedByRequiredScene)
        {
            var parts = new List<string>();
            if (skippedByRequiredScene != null)
            {
                foreach (var kvp in skippedByRequiredScene.OrderBy(kvp => kvp.Key.ToString()))
                    parts.Add(kvp.Key + ":" + kvp.Value.ToString(CultureInfo.InvariantCulture));
            }

            string byScene = parts.Count > 0 ? string.Join(",", parts) : "(none)";
            return string.Format(CultureInfo.InvariantCulture,
                "Scene eligibility skip summary: skipped={0} currentScene={1} byRequiredScene={2}",
                skipped,
                currentScene,
                byScene);
        }

        /// <summary>
        /// H3 BATCH_COMPLETE line (module M-A3, design "H3 line format"). The exact
        /// token set + order is the versioned orchestrator contract: an external
        /// nightly pipeline greps "BATCH_COMPLETE v1 " to read the batch tally
        /// without parsing the whole results file. Any change to the tokens or their
        /// meaning MUST bump v1 -> v2 and update the BAT-001 LogContract test.
        ///
        /// Pure and Unity-free: the caller passes HighLogic.LoadedScene.ToString() so
        /// this stays xUnit-testable. Values never contain spaces (category is a
        /// single token, scene is an enum name), keeping the line whitespace-split
        /// friendly for a trivial grep/awk.
        /// </summary>
        internal static string FormatBatchCompleteLine(
            int total, int passed, int failed, int skipped, string category, string scene)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "BATCH_COMPLETE v1 total={0} passed={1} failed={2} skipped={3} category={4} scene={5}",
                total, passed, failed, skipped, category, scene);
        }

        internal List<InGameTestInfo> FilterSceneEligibleBatchCandidates(
            IEnumerable<InGameTestInfo> tests)
        {
            int skipped;
            Dictionary<GameScenes, int> skippedByRequiredScene;
            var eligible = FilterSceneEligibleBatchCandidates(
                tests,
                HighLogic.LoadedScene,
                out skipped,
                out skippedByRequiredScene);
            if (skipped > 0)
            {
                ParsekLog.Info(Tag,
                    FormatSceneEligibilitySkipSummary(
                        skipped,
                        HighLogic.LoadedScene,
                        skippedByRequiredScene));
            }

            return eligible;
        }

        internal static List<InGameTestInfo> FilterSceneEligibleBatchCandidates(
            IEnumerable<InGameTestInfo> tests,
            GameScenes currentScene,
            out int skipped,
            out Dictionary<GameScenes, int> skippedByRequiredScene)
        {
            skipped = 0;
            skippedByRequiredScene = new Dictionary<GameScenes, int>();
            var eligible = new List<InGameTestInfo>();
            if (tests == null)
                return eligible;

            foreach (var test in tests)
            {
                if (test == null)
                    continue;

                if (test.RequiredScene == InGameTestAttribute.AnyScene
                    || test.RequiredScene == currentScene)
                {
                    eligible.Add(test);
                    continue;
                }

                test.Status = TestStatus.Skipped;
                test.ErrorMessage = $"Requires {test.RequiredScene} scene";
                test.DurationMs = 0f;
                skipped++;
                int count;
                skippedByRequiredScene.TryGetValue(test.RequiredScene, out count);
                skippedByRequiredScene[test.RequiredScene] = count + 1;
            }

            return eligible;
        }

        internal static List<InGameTestInfo> OrderForBatchExecution(IEnumerable<InGameTestInfo> tests)
        {
            return tests
                .OrderBy(t => t.RunLast)
                .ThenBy(t => t.RestoreBatchFlightBaselineAfterExecution)
                .ThenBy(t => t.Category, StringComparer.Ordinal)
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Decides whether to fire the post-test batch FLIGHT baseline
        /// restore after a restore-backed test has executed.
        ///
        /// Round-7 contract: restore runs unconditionally after any test
        /// that declared <see cref="InGameTestInfo.RestoreBatchFlightBaselineAfterExecution"/>=true
        /// and reached <c>RunOneTest</c>, regardless of the test's terminal
        /// <see cref="TestStatus"/>. Self-skips (<c>InGameAssert.Skip</c>
        /// thrown from inside the test body after the body has already
        /// started recording, staged the vessel, or otherwise mutated the
        /// flight scene) are NOT exempted -- the body may have left the
        /// session in any state before deciding to skip, so the next
        /// test must start from the captured baseline. Scene-eligibility
        /// skips are filtered out by <c>RunBatch</c>'s <c>continue;</c>
        /// branch BEFORE the test runs, so they never reach this
        /// predicate (and therefore never trigger an unnecessary restore).
        /// </summary>
        internal static bool ShouldRestoreBatchFlightBaselineAfterTest(InGameTestInfo test)
        {
            return test != null && test.RestoreBatchFlightBaselineAfterExecution;
        }

        /// <summary>
        /// Decides whether the guaranteed end-of-batch in-memory restore must run. It
        /// runs whenever an in-memory baseline exists AND state may have been mutated
        /// since the last successful restore. When the last per-test restore already
        /// returned the player to baseline and no test ran afterward, the final
        /// restore is a no-op (avoids a redundant scene reload; keeps FLIGHT/TS byte
        /// behavior identical to today's restore-backed batches). Robust to test
        /// ordering: the guarantee rests on stateDirtySinceLastRestore, NOT on
        /// restore-backed tests sorting last. A RunLast (or any) test that runs after
        /// the last per-test restore correctly re-arms the dirty flag and triggers
        /// one final restore.
        /// </summary>
        internal static bool ShouldRunFinalBatchRestore(
            bool hasInMemoryBaseline, bool stateDirtySinceLastRestore, bool restoreAlreadyFailedAndAborted)
        {
            if (restoreAlreadyFailedAndAborted) return false; // session untrusted; disk forced separately, don't reload
            return hasInMemoryBaseline && stateDirtySinceLastRestore;
        }

        internal static List<InGameTestInfo> PrepareBatchExecution(IEnumerable<InGameTestInfo> tests)
        {
            var ordered = OrderForBatchExecution(tests);
            var batch = new List<InGameTestInfo>(ordered.Count);
            int skippedForBatch = 0;

            foreach (var test in ordered)
            {
                if (test.AllowBatchExecution)
                {
                    batch.Add(test);
                    continue;
                }

                test.Status = TestStatus.Skipped;
                test.ErrorMessage = GetBatchSkipReason(test);
                test.DurationMs = 0f;
                skippedForBatch++;
            }

            if (skippedForBatch > 0)
                ParsekLog.Info(Tag, $"Batch execution skipped {skippedForBatch} single-run-only test(s)");

            return batch;
        }

        internal static List<InGameTestInfo> PrepareBatchExecutionIncludingFlightRestore(
            IEnumerable<InGameTestInfo> tests)
        {
            var ordered = OrderForBatchExecution(tests);
            var batch = new List<InGameTestInfo>(ordered.Count);
            int skippedForBatch = 0;

            foreach (var test in ordered)
            {
                if (test.AllowBatchExecution || test.RestoreBatchFlightBaselineAfterExecution)
                {
                    batch.Add(test);
                    continue;
                }

                test.Status = TestStatus.Skipped;
                test.ErrorMessage = GetBatchSkipReason(test);
                test.DurationMs = 0f;
                skippedForBatch++;
            }

            if (skippedForBatch > 0)
            {
                ParsekLog.Info(Tag,
                    $"Isolated batch execution skipped {skippedForBatch} manual-only test(s)");
            }

            return batch;
        }

        internal static string GetBatchSkipReason(InGameTestInfo test)
        {
            if (test == null || test.AllowBatchExecution)
                return null;

            return string.IsNullOrEmpty(test.BatchSkipReason)
                ? DefaultBatchSkipReason
                : test.BatchSkipReason;
        }

        internal static string GetBatchExecutionNote(InGameTestInfo test)
        {
            if (test == null)
                return null;

            if (test.RestoreBatchFlightBaselineAfterExecution)
                return DefaultBatchRestoreNote;

            return GetBatchSkipReason(test);
        }

        private List<InGameTestInfo> PrepareBatchFlightRestoreExecution(List<InGameTestInfo> ordered)
        {
            if (ordered == null || ordered.Count == 0)
                return ordered ?? new List<InGameTestInfo>();

            if (!ordered.Any(t => t.RestoreBatchFlightBaselineAfterExecution))
                return ordered;

            string baselineUnavailableReason = GetBatchFlightBaselineUnavailableReason();
            if (!string.IsNullOrEmpty(baselineUnavailableReason))
                return SkipBatchFlightRestoreTests(ordered, baselineUnavailableReason);

            try
            {
                if (batchFlightBaseline == null)
                    batchFlightBaseline = CaptureFlightBatchBaseline(); // fallback only if universal capture was None/failed
                batchFlightBaselinePrimed = false;
                int restoreCount = ordered.Count(t => t.RestoreBatchFlightBaselineAfterExecution);
                ParsekLog.Info(Tag,
                    $"Using batch baseline slot '{batchFlightBaseline.SlotName}' for {restoreCount} restore-after-run test(s)");
                return ordered;
            }
            catch (InGameTestSkippedException skipEx)
            {
                // Warn, not Error: a capture SKIP degrades gracefully (the
                // restore-backed tests are skipped and the batch continues),
                // unlike the prime / per-test restore failures that abort.
                ParsekLog.Warn(Tag,
                    "Batch baseline capture skip detail: " + DescribeRestoreFailure(skipEx));
                return SkipBatchFlightRestoreTests(ordered, skipEx.Message);
            }
            catch (InGameTestFailedException failEx)
            {
                ParsekLog.Error(Tag,
                    "Batch baseline capture failure detail: " + DescribeRestoreFailure(failEx));
                return SkipBatchFlightRestoreTests(ordered, failEx.Message);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag,
                    "Batch baseline capture failure detail: " + DescribeRestoreFailure(ex));
                return SkipBatchFlightRestoreTests(ordered,
                    $"Automatic FLIGHT batch restore unavailable: {ex.Message}");
            }
        }

        /// <summary>
        /// Scenes the automatic batch baseline (quicksave + quickload) restore
        /// supports. FLIGHT is the original path; TRACKSTATION lets the Tracking
        /// Station "Fly" canary isolate (it captures a TS baseline and returns
        /// to it after the test transitions into FLIGHT). Other scenes have no
        /// quicksave-restorable baseline contract here.
        /// </summary>
        internal static bool IsBatchBaselineRestoreSupportedScene(GameScenes scene)
        {
            return scene == GameScenes.FLIGHT || scene == GameScenes.TRACKSTATION;
        }

        /// <summary>
        /// Pure availability gate for the automatic batch baseline restore.
        /// Returns null when a baseline can be captured/restored for the given
        /// scene + live-state flags, or the reason string otherwise. Only FLIGHT
        /// requires a focusable active vessel (its restore re-focuses one via
        /// StartAndFocusVessel); a Tracking Station baseline returns via
        /// LoadScene and needs no active vessel.
        /// </summary>
        internal static string BatchBaselineUnavailableReasonForScene(
            GameScenes scene, bool hasCurrentGame, bool hasSaveFolder, bool hasActiveVessel)
        {
            if (!IsBatchBaselineRestoreSupportedScene(scene))
                return "Automatic batch restore requires running from the FLIGHT or Tracking Station scene.";
            if (!hasCurrentGame)
                return "Automatic batch restore requires HighLogic.CurrentGame.";
            if (!hasSaveFolder)
                return "Automatic batch restore requires HighLogic.SaveFolder.";
            if (scene == GameScenes.FLIGHT && !hasActiveVessel)
                return "Automatic FLIGHT batch restore requires an active vessel.";

            return null;
        }

        private static string GetBatchFlightBaselineUnavailableReason()
        {
            return BatchBaselineUnavailableReasonForScene(
                HighLogic.LoadedScene,
                HighLogic.CurrentGame != null,
                !string.IsNullOrEmpty(HighLogic.SaveFolder),
                FlightGlobals.ActiveVessel != null);
        }

        /// <summary>
        /// Pure classifier for how a batch isolates the player's campaign, given the
        /// scene + live-state flags.
        ///   FLIGHT (with active vessel) / TRACKSTATION -> InMemoryAndDisk: full
        ///     in-memory revert (baseline quicksave + final quickload) + on-disk revert.
        ///     These two are the only scenes whose in-memory restore path is exercised
        ///     today (FLIGHT via StartAndFocusVessel, TRACKSTATION via the existing
        ///     non-flight commit canary).
        ///   FLIGHT (no active vessel) / SPACECENTER / EDITOR -> DiskOnly: persistent.sfs
        ///     safety .bak only, NO in-memory reload. SPACECENTER's in-memory reload via
        ///     CommitNonFlightSceneLoad/Game.Start() is structurally available but UNPROVEN
        ///     in this codebase, so it ships DiskOnly until the in-game validation gate
        ///     (RuntimeTests SpaceCenterBatchIsolationInMemoryRestore) passes; EDITOR's
        ///     in-memory reload mid-edit is deliberately out of scope; FLIGHT-no-vessel has
        ///     no focusable vessel for StartAndFocusVessel.
        ///   MAINMENU / no loaded game / no save folder -> None (save-mutating tests
        ///     already skip there).
        /// </summary>
        internal static BatchIsolationMode ClassifyBatchIsolationMode(
            GameScenes scene, bool hasCurrentGame, bool hasSaveFolder, bool hasActiveVessel)
        {
            if (!hasCurrentGame || !hasSaveFolder)
                return BatchIsolationMode.None;
            switch (scene)
            {
                case GameScenes.FLIGHT:
                    return hasActiveVessel
                        ? BatchIsolationMode.InMemoryAndDisk
                        : BatchIsolationMode.DiskOnly;
                case GameScenes.TRACKSTATION:
                    return BatchIsolationMode.InMemoryAndDisk;
                case GameScenes.SPACECENTER:
                case GameScenes.EDITOR:
                    return BatchIsolationMode.DiskOnly;
                default:
                    return BatchIsolationMode.None;
            }
        }

        /// <summary>
        /// Universal batch-start campaign isolation. InMemoryAndDisk captures the full
        /// baseline (quicksave slot + Parsek sidecar snapshot + persistent.sfs .bak).
        /// DiskOnly takes ONLY the persistent.sfs safety .bak (no quicksave, no
        /// in-memory reload). Writes the crash marker (Piece 4) on every non-None
        /// mode, in its own try so a marker-write failure (e.g. autosave disabled)
        /// disables ONLY crash reconcile, never the in-memory or disk revert. A
        /// capture failure downgrades to None + warn (the batch still runs, without
        /// auto-revert).
        /// </summary>
        private void CaptureBatchBaseline(BatchIsolationMode mode)
        {
            batchIsolationMode = mode;
            if (mode == BatchIsolationMode.None)
                return;

            batchInstanceId = Guid.NewGuid();
            try
            {
                if (mode == BatchIsolationMode.InMemoryAndDisk)
                {
                    batchFlightBaseline = CaptureFlightBatchBaseline(); // existing; slot + snapshot + clean .bak
                }
                else // DiskOnly
                {
                    diskOnlyPersistentBackupPath =
                        BackupCampaignPersistentSave(CreateBatchFlightBaselineSlotName());
                }
                ParsekLog.Info(Tag, "Batch isolation captured: mode=" + mode
                    + " slot='" + (batchFlightBaseline?.SlotName ?? "(disk-only)") + "'"
                    + " persistentBackup='" + (batchFlightBaseline?.PersistentSaveBackupPath
                        ?? diskOnlyPersistentBackupPath ?? "(none)") + "'"
                    + " batchInstanceId=" + batchInstanceId.ToString("N"));
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "Batch isolation capture FAILED (mode=" + mode
                    + "); the campaign will NOT be auto-reverted this run: " + ex.Message);
                ParsekLog.Error(Tag,
                    "Batch isolation capture failure detail (mode=" + mode + "): "
                    + DescribeRestoreFailure(ex));
                batchFlightBaseline = null;
                TryDeletePersistentSaveBackup(diskOnlyPersistentBackupPath);
                diskOnlyPersistentBackupPath = null;
                batchIsolationMode = BatchIsolationMode.None;
                return;
            }

            // Marker write is SEPARATE and best-effort: a failure here disables ONLY
            // crash reconcile, not the in-memory / disk revert captured above.
            try
            {
                WriteTestBatchMarker(mode); // Piece 4
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    "Test batch crash marker write FAILED; crash reconcile disabled this run "
                    + "(in-memory + disk revert remain active): " + ex.Message);
            }
        }

        private void ResetBatchIsolationState()
        {
            batchFlightBaseline = null;
            batchFlightBaselinePrimed = false;
            abortBatchAfterRestoreFailure = false;
            preserveBatchFlightBaselineArtifacts = false;
            preservedBatchFlightBaselineReason = null;
            batchIsolationMode = BatchIsolationMode.None;
            diskOnlyPersistentBackupPath = null;
            finalBatchRestoreDone = false;
            stateDirtySinceLastRestore = false;
            batchInstanceId = Guid.Empty;
        }

        private void WriteTestBatchMarker(BatchIsolationMode mode)
        {
            string backupPath = batchFlightBaseline?.PersistentSaveBackupPath ?? diskOnlyPersistentBackupPath;
            if (string.IsNullOrEmpty(backupPath))
            {
                ParsekLog.Warn(Tag,
                    "WriteTestBatchMarker: no persistent backup path; crash reconcile disabled this run");
                return;
            }
            var scenario = UnityEngine.Object.FindObjectOfType<ParsekScenario>();
            if (scenario == null)
            {
                ParsekLog.Warn(Tag,
                    "WriteTestBatchMarker: no ParsekScenario; crash reconcile disabled this run");
                return;
            }
            scenario.ActiveTestBatchMarker = new TestBatchMarker
            {
                ProcessSessionId = ParsekProcess.ProcessSessionId.ToString("N"),
                BatchInstanceId = batchInstanceId.ToString("N"),
                PersistentBackupPath = backupPath,
                // The on-disk Parsek/ sidecar snapshot (<slot>-parsek), where the
                // LEDGER (Parsek/GameState/events.pgse) lives. Present only in
                // InMemoryAndDisk mode (DiskOnly takes no snapshot); the crash
                // finisher reverts the live sidecar dir from this before its
                // deferred reload so the reloaded ledger comes from the clean
                // snapshot, not the test-mutated live sidecars. It is a durable
                // directory under saves/<save>/ that survives a process kill.
                ParsekSnapshotDir = batchFlightBaseline?.ParsekSaveSnapshotDirectory,
                SaveFolder = HighLogic.SaveFolder,
                CapturedScene = HighLogic.LoadedScene.ToString(),
                StartedRealTime = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };
            // Make the marker durable in persistent.sfs so a crash leaves it on disk.
            // Fire-and-forget: a no-op save (CanAutoSave disabled) leaves the in-memory
            // marker set but no durable copy; crash reconcile is simply unavailable this
            // run (the in-memory + disk-.bak revert paths are unaffected).
            Helpers.QuickloadResumeHelpers.TriggerCampaignPersistentSave();
            ParsekLog.Info(Tag, $"Test batch crash marker written (mode={mode}, backup='{backupPath}')");
        }

        private void ClearTestBatchMarker(string reason)
        {
            var scenario = UnityEngine.Object.FindObjectOfType<ParsekScenario>();
            if (scenario != null && scenario.ActiveTestBatchMarker != null)
                scenario.ActiveTestBatchMarker = null;
            // The DISK marker is removed authoritatively by the success-path
            // persistent.sfs revert from the clean .bak (CleanupBatchFlightBaselineSave
            // / TeardownDiskOnlyIsolation), which ran just before this call. This only
            // drops the in-memory copy so a later in-process OnSave does not re-persist it.
            ParsekLog.Info(Tag, $"Test batch crash marker cleared ({reason})");
        }

        private static FlightBatchBaselineState CaptureFlightBatchBaseline()
        {
            GameScenes capturedScene = HighLogic.LoadedScene;
            Vessel vessel = FlightGlobals.ActiveVessel;
            // FLIGHT restore re-focuses an active vessel (StartAndFocusVessel),
            // so it must exist at capture time. A non-FLIGHT (Tracking Station)
            // baseline returns via LoadScene with no vessel focus, so a null
            // active vessel there is fine.
            if (capturedScene == GameScenes.FLIGHT)
                InGameAssert.IsNotNull(vessel,
                    "Automatic FLIGHT batch restore requires a live active vessel to capture baseline.");

            string slotName = CreateBatchFlightBaselineSlotName();
            string parsekSnapshotDirectory = null;
            string persistentBackupPath = null;

            try
            {
                Helpers.QuickloadResumeHelpers.TriggerQuicksave(slotName);
                parsekSnapshotDirectory = CaptureBatchFlightParsekSaveSnapshot(slotName);
                persistentBackupPath = BackupCampaignPersistentSave(slotName);

                return new FlightBatchBaselineState
                {
                    SlotName = slotName,
                    CapturedScene = capturedScene,
                    ParsekSaveSnapshotDirectory = parsekSnapshotDirectory,
                    PersistentSaveBackupPath = persistentBackupPath,
                    CapturedFlightInstanceId = ParsekFlight.Instance != null
                        ? ParsekFlight.Instance.GetInstanceID()
                        : 0,
                    ActiveVesselId = vessel != null ? vessel.id : Guid.Empty,
                    ActiveVesselName = vessel != null ? vessel.vesselName : null,
                    ActiveVesselSituation = vessel != null
                        ? vessel.situation
                        : Vessel.Situations.PRELAUNCH,
                    // P2 review fix: capture the live RecordingStore state
                    // at batch start, before any test mutates it. This is
                    // the rollback target for RestoreBatchFlightBaselineCore's
                    // fail-closed path. Cheap: shallow copies of the four
                    // list/slot references plus the auto-assigned-groups
                    // dict.
                    RecordingStoreSnapshot = RecordingStoreTestSnapshot.Capture()
                };
            }
            catch
            {
                Helpers.QuickloadResumeHelpers.TryDeleteSaveSlot(slotName);
                if (!string.IsNullOrEmpty(parsekSnapshotDirectory))
                    TryDeleteDirectoryRecursive(parsekSnapshotDirectory);
                TryDeletePersistentSaveBackup(persistentBackupPath);
                throw;
            }
        }

        private const string CampaignPersistentSaveFileName = "persistent.sfs";

        /// <summary>
        /// Backs up the campaign's persistent.sfs at batch start so the teardown
        /// can restore it byte-for-byte. Tests promoted into the isolated batch
        /// write persistent.sfs directly (the scene-exit merge tests) or trigger
        /// a KSP scene-change auto-save; without this the campaign's main save
        /// would be left in the test's mutated state even though the baseline
        /// reload reverts the in-memory game. The backup is a sibling
        /// "&lt;slot&gt;-persistent.bak" -- a non-.sfs name so KSP never lists it
        /// as a loadable save. Defensive: never throws (a failed backup just
        /// disables the revert for the run, with a warning); returns null then.
        /// </summary>
        private static string BackupCampaignPersistentSave(string baselineSlotName)
        {
            try
            {
                string persistentPath = RecordingPaths.ResolveSaveScopedPath(CampaignPersistentSaveFileName);
                if (string.IsNullOrEmpty(persistentPath) || !File.Exists(persistentPath))
                {
                    ParsekLog.Verbose(Tag,
                        "Batch baseline: no campaign persistent.sfs present to back up");
                    return null;
                }

                string backupPath = RecordingPaths.ResolveSaveScopedPath(
                    baselineSlotName + "-persistent.bak");
                if (string.IsNullOrEmpty(backupPath))
                {
                    ParsekLog.Warn(Tag,
                        "Batch baseline: could not resolve a persistent.sfs backup path; "
                        + "campaign persistent.sfs will NOT be auto-reverted this run");
                    return null;
                }

                byte[] bytes = File.ReadAllBytes(persistentPath);
                FileIOUtils.SafeWriteBytes(bytes, backupPath, Tag);
                ParsekLog.Info(Tag,
                    $"Batch baseline: backed up campaign persistent.sfs ({bytes.Length} bytes) "
                    + $"to '{Path.GetFileName(backupPath)}'");
                return backupPath;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Batch baseline: failed to back up campaign persistent.sfs: {ex.Message}. "
                    + "Tests that write persistent.sfs will NOT be auto-reverted this run.");
                return null;
            }
        }

        /// <summary>
        /// Restores the campaign's persistent.sfs from the batch-start backup
        /// via the atomic safe-write (tmp + rename) path. Returns true when the
        /// backup is consumable (restored, or nothing to restore); false only
        /// when a backup existed but the restore failed, so the caller keeps the
        /// .bak for manual recovery. Never throws.
        /// </summary>
        private static bool RestoreCampaignPersistentSave(string backupPath)
        {
            if (string.IsNullOrEmpty(backupPath))
                return true;
            try
            {
                if (!File.Exists(backupPath))
                {
                    ParsekLog.Warn(Tag,
                        $"Batch baseline: persistent.sfs backup missing at '{backupPath}'; nothing to restore");
                    return true;
                }

                string persistentPath = RecordingPaths.ResolveSaveScopedPath(CampaignPersistentSaveFileName);
                if (string.IsNullOrEmpty(persistentPath))
                {
                    ParsekLog.Warn(Tag,
                        "Batch baseline: could not resolve persistent.sfs path to restore; "
                        + "backup preserved for manual recovery");
                    return false;
                }

                byte[] bytes = File.ReadAllBytes(backupPath);
                FileIOUtils.SafeWriteBytes(bytes, persistentPath, Tag);
                ParsekLog.Info(Tag,
                    $"Batch baseline: restored campaign persistent.sfs ({bytes.Length} bytes) "
                    + "from batch-start backup");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Batch baseline: failed to restore campaign persistent.sfs from '{backupPath}': "
                    + $"{ex.Message}. Backup preserved for manual recovery.");
                return false;
            }
        }

        private static void TryDeletePersistentSaveBackup(string backupPath)
        {
            if (string.IsNullOrEmpty(backupPath))
                return;
            try
            {
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                    ParsekLog.Verbose(Tag,
                        $"Batch baseline: deleted persistent.sfs backup '{Path.GetFileName(backupPath)}'");
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Batch baseline: failed to delete persistent.sfs backup '{backupPath}': {ex.Message}");
            }
        }

        private static string CreateBatchFlightBaselineSlotName()
        {
            return "parsek-test-batch-baseline-"
                + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)
                + "-"
                + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private static string CaptureBatchFlightParsekSaveSnapshot(string slotName)
        {
            string currentParsekSaveDirectory = RecordingPaths.ResolveSaveScopedPath("Parsek");
            InGameAssert.IsTrue(!string.IsNullOrEmpty(currentParsekSaveDirectory),
                "Automatic FLIGHT batch restore requires a resolvable save-scoped Parsek directory.");

            string snapshotDirectory = Path.Combine(
                Path.GetDirectoryName(currentParsekSaveDirectory) ?? string.Empty,
                slotName + "-parsek");
            InGameAssert.IsTrue(!string.IsNullOrEmpty(snapshotDirectory),
                "Automatic FLIGHT batch restore failed: snapshot directory was null/empty.");

            try
            {
                DeleteDirectoryRecursive(snapshotDirectory);
                if (Directory.Exists(currentParsekSaveDirectory))
                {
                    CopyDirectoryRecursive(currentParsekSaveDirectory, snapshotDirectory);
                }
                else
                {
                    Directory.CreateDirectory(snapshotDirectory);
                }

                return snapshotDirectory;
            }
            catch
            {
                TryDeleteDirectoryRecursive(snapshotDirectory);
                throw;
            }
        }

        private static List<InGameTestInfo> SkipBatchFlightRestoreTests(
            List<InGameTestInfo> ordered, string reason)
        {
            var filtered = new List<InGameTestInfo>(ordered.Count);
            int skipped = 0;

            foreach (var test in ordered)
            {
                if (!test.RestoreBatchFlightBaselineAfterExecution)
                {
                    filtered.Add(test);
                    continue;
                }

                test.Status = TestStatus.Skipped;
                test.ErrorMessage = reason;
                test.DurationMs = 0f;
                skipped++;
            }

            if (skipped > 0)
            {
                ParsekLog.Warn(Tag,
                    $"Batch execution skipped {skipped} restore-after-run test(s): {reason}");
            }

            return filtered;
        }

        private IEnumerator RunBatch(List<InGameTestInfo> tests)
        {
            isRunning = true;
            // [M-A3 P4.1] Latch the pending autorun mark into THIS batch and clear the
            // pending flag so it applies to exactly this batch, never a later
            // human-initiated one (edge 13). Read in the batch-end region by H2 (P5.1).
            wasAutorunBatch = pendingAutorunBatch;
            autorunExitArmedThisBatch = pendingAutorunExit;
            pendingAutorunBatch = false;
            pendingAutorunExit = false;
            BeginBatchExceptionMonitor();
            spaceCenterBounceAttempted = false; // once-per-batch recovery guard
            ParsekLog.Info(Tag, $"Starting test run: {tests.Count} tests");
            int sceneEligibilitySkipped = 0;
            var sceneEligibilitySkipsByRequiredScene = new Dictionary<GameScenes, int>();

            foreach (var test in tests)
            {
                // Storm pre-check (before the prime/run below): if a flood has crossed the threshold
                // since the previous test (e.g. exceptions accumulated during a baseline restore's
                // frames), stop here so the next test neither runs on a corpse nor reloads the flight
                // scene (each reload re-runs the broken stock bootstrap and sustains the flood). Pairs
                // with the post-check after each test. NOTE: a flood already active before the batch's
                // FIRST test is caught by that test's POST-check, not here — the per-batch counter
                // starts at 0 and needs a few frames to cross the threshold.
                if (TryDetectExceptionStorm(test))
                {
                    RecountResults();
                    break;
                }

                if (test.RestoreBatchFlightBaselineAfterExecution
                    && batchFlightBaseline != null
                    && !batchFlightBaselinePrimed
                    && !batchExceptionStormDetected)
                {
                    yield return coroutineHost.StartCoroutine(
                        PrimeBatchFlightBaselineBeforeFirstRestoreBackedTest(test));
                    RecountResults();
                    if (abortBatchAfterRestoreFailure)
                        break;
                }

                if (!IsEligibleForScene(test))
                {
                    test.Status = TestStatus.Skipped;
                    test.ErrorMessage = $"Requires {test.RequiredScene} scene";
                    sceneEligibilitySkipped++;
                    int count;
                    sceneEligibilitySkipsByRequiredScene.TryGetValue(test.RequiredScene, out count);
                    sceneEligibilitySkipsByRequiredScene[test.RequiredScene] = count + 1;
                    ParsekLog.Verbose(Tag,
                        $"Scene eligibility skipped: {test.Name} requires={test.RequiredScene} current={HighLogic.LoadedScene}");
                    RecountResults();
                    continue;
                }

                stateDirtySinceLastRestore = true;
                activeTestCoroutine = coroutineHost.StartCoroutine(RunOneTest(test));
                yield return activeTestCoroutine;
                activeTestCoroutine = null;

                // Storm post-check: the test body itself may have tipped the game into the flood.
                // Detecting it here skips this test's flight-scene reload (each reload re-runs the
                // broken stock bootstrap and sustains the flood) and routes the batch to the
                // existing disk-only-revert abort path (ShouldRunFinalBatchRestore returns false
                // when abortBatchAfterRestoreFailure is set).
                TryDetectExceptionStorm(test);

                if (!batchExceptionStormDetected && ShouldRestoreBatchFlightBaselineAfterTest(test))
                {
                    yield return coroutineHost.StartCoroutine(
                        RestoreBatchFlightBaselineAfterExecution(test));
                }
                RecountResults();
                if (abortBatchAfterRestoreFailure)
                    break;
            }

            // Piece 2: guaranteed final in-memory revert before cleanup.
            if (ShouldRunFinalBatchRestore(
                    batchFlightBaseline != null, stateDirtySinceLastRestore, abortBatchAfterRestoreFailure))
            {
                yield return coroutineHost.StartCoroutine(RunFinalBatchRestore("batch-complete-final-restore"));
            }
            else if (abortBatchAfterRestoreFailure && batchFlightBaseline != null)
            {
                // Piece 3: per-test restore already failed + aborted the batch. The
                // final in-memory restore is skipped (untrusted session), but the
                // DISK save must still be correct.
                ForceRevertCampaignDiskToBaseline(batchFlightBaseline, "abort-after-restore-failure");
            }

            // Post-corruption one-shot recovery check (stock Bug #4803). The per-restore
            // reload guard trips the storm-abort at prime/per-test restores, but BOTH
            // batch endings can still leave the game NRE-flooding with no recovery: the
            // batch's FINAL restore corrupting the scene (2026-07-10: the last isolated
            // test's restore tripped the late-switch camera destruction and the operator
            // got a soft-frozen game), and the storm-abort path itself (it only reverts
            // the disk and tells the operator to relaunch). Sample the flood detector one
            // more time over a settle window while the exception monitor is still active;
            // the actual bounce is dispatched at the very END of RunBatch, after the disk
            // teardown + results export, so the batch's disk state is fully settled
            // before the scene change (there are no further yields in between, so the
            // decision cannot go stale).
            bool bounceToSpaceCenter = false;
            int batchEndSettleDelta = 0;
            if (ShouldSampleBatchEndCorruption(batchFlightBaseline != null, batchExceptionStormDetected))
            {
                int batchEndSettleStart = batchUnhandledExceptionCount;
                for (int f = 0; f < BaselineReloadHealthSettleFrames; f++)
                    yield return null;
                batchEndSettleDelta = batchUnhandledExceptionCount - batchEndSettleStart;
                bounceToSpaceCenter = ShouldAttemptSpaceCenterBounce(
                    spaceCenterBounceAttempted, batchEndSettleDelta, BaselineReloadFloodExceptionThreshold);
                if (bounceToSpaceCenter)
                    spaceCenterBounceAttempted = true;
                ParsekLog.Verbose(Tag,
                    $"Batch-end corruption check: +{batchEndSettleDelta} exc/{BaselineReloadHealthSettleFrames}f -> "
                    + (bounceToSpaceCenter ? "FLOODING (one-shot Space Center bounce armed)" : "healthy (no bounce)"));
            }
            else
            {
                ParsekLog.Verbose(Tag,
                    "Batch-end corruption check skipped: no FLIGHT/TS baseline and no exception storm "
                    + "(this batch performed no scene reloads)");
            }

            isRunning = false;
            EndBatchExceptionMonitor();
            // Mirror of EndBatchExceptionMonitor's placement: a Cancel() StopCoroutine
            // skips this normal-end teardown, so Cancel() disarms too. Idempotent; on a
            // healthy batch the pin already disarmed at the reload's onLevelWasLoaded.
            Helpers.FlightCameraReloadPin.Disarm("batch-complete");

            // Post-abort scene cleanup (2026-07-10 verify run): the
            // abortBatchAfterRestoreFailure break skips both the per-test scene
            // reload (whose post-reload PerformBetweenRunCleanup is the normal
            // ghost sweep) and the final in-memory restore, so the live scene
            // kept every prior test's debris (tracked cleanupRegistry objects +
            // timeline ghost visuals; a green fallback ghost sphere was observed
            // riding the vessel). Sweep it here in the always-runs batch-end
            // region, BEFORE the flag resets below. Gated on the abort flag so
            // the normal path stays byte-identical; exception-safe throughout.
            if (abortBatchAfterRestoreFailure)
                PerformPostAbortSceneCleanup("abort-after-restore-failure");

            // Piece 4 + 5: teardown by mode. InMemoryAndDisk reverts persistent.sfs
            // from the clean .bak + sweeps slot/loadmeta/bak/snapshot; DiskOnly
            // reverts + sweeps the .bak.
            if (batchIsolationMode == BatchIsolationMode.DiskOnly)
                TeardownDiskOnlyIsolation("batch-complete");
            else
                CleanupBatchFlightBaselineSave();

            ClearTestBatchMarker("batch-complete");

            batchFlightBaseline = null;
            batchFlightBaselinePrimed = false;
            abortBatchAfterRestoreFailure = false;
            preserveBatchFlightBaselineArtifacts = false;
            preservedBatchFlightBaselineReason = null;
            batchIsolationMode = BatchIsolationMode.None;
            diskOnlyPersistentBackupPath = null;
            finalBatchRestoreDone = false;
            stateDirtySinceLastRestore = false;
            batchInstanceId = Guid.Empty;
            activeTestCoroutine = null;
            if (sceneEligibilitySkipped > 0)
            {
                ParsekLog.Info(Tag,
                    FormatSceneEligibilitySkipSummary(
                        sceneEligibilitySkipped,
                        HighLogic.LoadedScene,
                        sceneEligibilitySkipsByRequiredScene));
            }
            int considered = allTests.Count(t => t.Status != TestStatus.NotRun);
            ParsekLog.Info(Tag,
                $"Test run complete: {Passed} passed, {Failed} failed, {Skipped} skipped (of {considered})");

            // Auto-export on every batch completion so the external report file
            // always reflects the latest state. Multi-scene history lives in
            // InGameTestInfo.ResultsByScene, which ResetResults preserves, so
            // the file accumulates across KSC / Flight / Tracking Station runs.
            ExportResultsFile();

            // H3 (module M-A3): emit the grep-stable BATCH_COMPLETE marker once per
            // batch, immediately after export, so an external orchestrator can read
            // the tally from the log without parsing the results file. total wires to
            // `considered` (Status != NotRun, the same quantity the "Test run
            // complete" summary logs), matching the per-scene accumulation. Placed
            // here (after teardown + export, before the optional bounce) so the last
            // durable batch record carries the machine-readable outcome.
            ParsekLog.Info(Tag, FormatBatchCompleteLine(
                considered, Passed, Failed, Skipped,
                currentBatchSelector, HighLogic.LoadedScene.ToString()));

            // Last: the one-shot Space Center bounce recovery decided above. Dispatched
            // only after teardown + export so the disk revert and the results file are
            // complete before the scene changes (the RunBatch host is the DDOL
            // TestRunnerShortcut, but a scene-scoped host would lose the coroutine at
            // LoadScene, so nothing may follow this call).
            if (bounceToSpaceCenter)
                AttemptSpaceCenterBounceRecovery(batchEndSettleDelta);
        }

        // One-shot post-corruption recovery: the 2026-07-05 in-game sweeps proved the
        // stock Bug #4803 corruption is PERMANENT for the FLIGHT scene within a process
        // (reload retries only multiply the flood) and that a single Space Center bounce
        // (LoadScene(SPACECENTER)) is the only in-process recovery. Dispatch exactly one
        // bounce so the operator lands in a usable Space Center instead of a soft-frozen
        // game. NEVER retried: a failed dispatch falls back to the relaunch alert.
        private void AttemptSpaceCenterBounceRecovery(int settleDelta)
        {
            ParsekLog.Error(Tag,
                $"Batch ended with the flight state still NRE-flooding (+{settleDelta} exc/"
                + $"{BaselineReloadHealthSettleFrames}f - stock Bug #4803 camera corruption). Attempting the "
                + "one-shot Space Center bounce recovery via HighLogic.LoadScene(SPACECENTER); no retries. "
                + "If the Space Center does not come up usable, relaunch KSP.");
            try
            {
                ScreenMessages.PostScreenMessage(
                    "[Parsek] Flight state corrupted after the test batch: bouncing to Space Center to recover.",
                    12f, ScreenMessageStyle.UPPER_CENTER);
            }
            catch
            {
                // ScreenMessages can be unavailable mid-corruption; the ERROR above is the durable record.
            }
            try
            {
                HighLogic.LoadScene(GameScenes.SPACECENTER);
                ParsekLog.Info(Tag,
                    "Space Center bounce dispatched (one-shot; batch disk teardown + results export completed beforehand)");
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag,
                    $"Space Center bounce dispatch FAILED ({ex.GetType().Name}: {ex.Message}); relaunch KSP to recover.");
            }
        }

        // Corruption backstop around RestoreBatchFlightBaselineCore. A FLIGHT->FLIGHT baseline reload
        // can trip stock Bug #4803 (the persistent FlightCamera is destroyed on the reload and fetch
        // orphaned to null -> FlightGlobals half-initialized -> a per-frame NRE flood that bricks the
        // session). The core's EnsureFlightCameraSurvivesReload PREVENTS it; this wrapper is the
        // backstop: it samples a short settle window after the reload and, if the flood signature is
        // present, trips the storm-abort IMMEDIATELY at the reload site. It does NOT retry - the
        // corruption is PERSISTENT (confirmed in-game 2026-07-05: re-reloading never recovers it, only
        // a Space Center bounce or a relaunch does), so re-reloading would just flood the log further.
        // Tripping here (rather than waiting for the RunBatch storm check) matters because that check
        // never runs for the LAST restore-backed test (the foreach ends with no next iteration and this
        // wrapper returns "success"), so without it the batch would end "clean" with the game corrupted
        // and no disk-revert / relaunch alert. Genuine core failures (skip/fail/other) are re-raised
        // UNCHANGED so the caller's existing abort/recovery handling is untouched. Relies on the batch
        // exception monitor being active (it is on the prime + per-test restore paths that call this).
        private IEnumerator RestoreBatchFlightBaselineCoreWithReloadGuard(
            FlightBatchBaselineState baseline, int previousFlightInstanceId, string cleanupReason)
        {
            Exception coreFailure = null;
            yield return coroutineHost.StartCoroutine(RunCoroutineSafely(
                RestoreBatchFlightBaselineCore(baseline, previousFlightInstanceId, cleanupReason),
                ex => coreFailure = ex));

            // Capture-point logging: HERE the exception still carries the ORIGINAL
            // stack (set at the throw inside the core, captured by RunCoroutineSafely's
            // MoveNext catch). Log the full detail immediately so the diagnostics are
            // immune to any downstream stack-eating rethrow - the 2026-07-10 rerun
            // (logs/2026-07-10_2258_rerun2-fast) captured only this wrapper's MoveNext
            // frame because the bare `throw coreFailure;` at the bottom of this method
            // reset the stack to the rethrow site before any catch site logged it.
            if (coreFailure != null)
            {
                ParsekLog.Error(Tag,
                    "Restore core failure detail (captured at reload-guard, original stack): "
                    + DescribeRestoreFailure(coreFailure));
            }

            // Settle window: a clean reload adds ~0 unhandled exceptions across these frames; a
            // corrupted one floods hundreds. The post-settle rate (not a cumulative-since-reload count)
            // is the reliable signal.
            int settleStart = batchUnhandledExceptionCount;
            for (int f = 0; f < BaselineReloadHealthSettleFrames; f++)
                yield return null;
            int settleDelta = batchUnhandledExceptionCount - settleStart;

            if (ReloadStillFlooding(settleDelta, BaselineReloadFloodExceptionThreshold))
            {
                // Corruption slipped past the prevention. It cannot be recovered by re-reloading
                // (persistent), so trip the storm-abort DIRECTLY here (disk-only revert + relaunch
                // alert) rather than rely on a later RunBatch check that never runs for the last test.
                ParsekLog.Warn(Tag,
                    $"Baseline reload flooding (+{settleDelta} exc/{BaselineReloadHealthSettleFrames}f) after " +
                    $"{cleanupReason} — the FlightCamera was destroyed on reload (stock Bug #4803) and cannot be " +
                    "recovered by re-reloading; aborting the batch. Relaunch KSP (or bounce via the Space Center).");
                if (!batchExceptionStormDetected)
                {
                    batchExceptionStormDetected = true;
                    abortBatchAfterRestoreFailure = true;
                    NotifyExceptionStormAbort(null, batchUnhandledExceptionCount);
                }
            }

            if (coreFailure != null)
            {
                // Stack-preserving rethrow: a bare `throw coreFailure;` RESETS the
                // exception's stack trace to this rethrow site, wiping the core's
                // frames (proven by the 2026-07-10 rerun log, where the captured
                // stack showed only this method's MoveNext). EDI.Throw rethrows the
                // SAME exception object with the original stack preserved and the
                // rethrow site appended, so the caller's existing skip/fail/other
                // handling is unchanged.
                ExceptionDispatchInfo.Capture(coreFailure).Throw();
                // Unreachable: EDI.Throw always throws, but the compiler cannot see
                // that, so keep the canonical bare rethrow as the fallback shape.
                throw coreFailure;
            }
        }

        private IEnumerator RestoreBatchFlightBaselineAfterExecution(InGameTestInfo test)
        {
            if (test == null || !test.RestoreBatchFlightBaselineAfterExecution || batchFlightBaseline == null)
                yield break;

            int previousFlightInstanceId = ParsekFlight.Instance != null
                ? ParsekFlight.Instance.GetInstanceID()
                : batchFlightBaseline.CapturedFlightInstanceId;

            ParsekLog.Info(Tag,
                $"Restoring batch FLIGHT baseline after {test.Name} from slot '{batchFlightBaseline.SlotName}'");

            Exception restoreFailure = null;
            yield return coroutineHost.StartCoroutine(RunCoroutineSafely(
                RestoreBatchFlightBaselineCoreWithReloadGuard(
                    batchFlightBaseline,
                    previousFlightInstanceId,
                    "post-batch-restore:" + test.Name),
                ex => restoreFailure = ex));

            if (restoreFailure != null)
            {
                // Full detail (type + message + inner exceptions + stack trace) to the
                // log so the throwing statement is named; the result row keeps the
                // short message below.
                ParsekLog.Error(Tag,
                    $"Batch baseline restore failure detail after {test.Name}: "
                    + DescribeRestoreFailure(restoreFailure));
            }

            if (restoreFailure is InGameTestSkippedException skipEx)
            {
                FailAndAbortBatchAfterRestore(test,
                    $"Automatic FLIGHT batch restore skipped after {test.Name}: {skipEx.Message}");
            }
            else if (restoreFailure is InGameTestFailedException failEx)
            {
                FailAndAbortBatchAfterRestore(test,
                    $"Automatic FLIGHT batch restore failed after {test.Name}: {failEx.Message}");
            }
            else if (restoreFailure != null)
            {
                FailAndAbortBatchAfterRestore(test,
                    $"Automatic FLIGHT batch restore failed after {test.Name}: {restoreFailure.Message}");
            }
            else
            {
                batchFlightBaselinePrimed = true;
                stateDirtySinceLastRestore = false;
            }
        }

        private IEnumerator PrimeBatchFlightBaselineBeforeFirstRestoreBackedTest(InGameTestInfo test)
        {
            if (test == null || !test.RestoreBatchFlightBaselineAfterExecution
                || batchFlightBaseline == null || batchFlightBaselinePrimed)
            {
                yield break;
            }

            int previousFlightInstanceId = ParsekFlight.Instance != null
                ? ParsekFlight.Instance.GetInstanceID()
                : batchFlightBaseline.CapturedFlightInstanceId;

            ParsekLog.Info(Tag,
                $"Priming batch FLIGHT baseline before {test.Name} from slot '{batchFlightBaseline.SlotName}'");

            Exception restoreFailure = null;
            yield return coroutineHost.StartCoroutine(RunCoroutineSafely(
                RestoreBatchFlightBaselineCoreWithReloadGuard(
                    batchFlightBaseline,
                    previousFlightInstanceId,
                    "pre-batch-restore:" + test.Name),
                ex => restoreFailure = ex));

            if (restoreFailure != null)
            {
                // Full detail (type + message + inner exceptions + stack trace) to the
                // log so the throwing statement is named; the result row keeps the
                // short message below.
                ParsekLog.Error(Tag,
                    $"Batch baseline prime failure detail before {test.Name}: "
                    + DescribeRestoreFailure(restoreFailure));
            }

            if (restoreFailure is InGameTestSkippedException skipEx)
            {
                FailAndAbortBatchAfterRestore(test,
                    $"Automatic FLIGHT batch baseline prime skipped before {test.Name}: {skipEx.Message}");
            }
            else if (restoreFailure is InGameTestFailedException failEx)
            {
                FailAndAbortBatchAfterRestore(test,
                    $"Automatic FLIGHT batch baseline prime failed before {test.Name}: {failEx.Message}");
            }
            else if (restoreFailure != null)
            {
                FailAndAbortBatchAfterRestore(test,
                    $"Automatic FLIGHT batch baseline prime failed before {test.Name}: {restoreFailure.Message}");
            }
            else
            {
                batchFlightBaselinePrimed = true;
                stateDirtySinceLastRestore = false;
            }
        }

        private IEnumerator RunFinalBatchRestore(string reason)
        {
            int previousFlightInstanceId = ParsekFlight.Instance != null
                ? ParsekFlight.Instance.GetInstanceID()
                : batchFlightBaseline.CapturedFlightInstanceId;
            ParsekLog.Info(Tag,
                $"Final batch baseline restore ({reason}) from slot '{batchFlightBaseline.SlotName}' scene={batchFlightBaseline.CapturedScene}");
            yield return coroutineHost.StartCoroutine(
                RestoreBatchBaselineWithRecovery(batchFlightBaseline, previousFlightInstanceId, reason));
            finalBatchRestoreDone = true;
            stateDirtySinceLastRestore = false;
        }

        /// <summary>
        /// Recover-on-failure wrapper around <see cref="RestoreBatchFlightBaselineCore"/>:
        /// attempts the in-memory restore, retries the load once on a non-skip
        /// failure, and on a persistent failure forces the on-disk persistent.sfs +
        /// Parsek/ sidecars back to baseline so the DISK save is always correct. A
        /// mixed/partial disk revert is the true last resort (preserve artifacts +
        /// loud alert).
        /// </summary>
        private IEnumerator RestoreBatchBaselineWithRecovery(
            FlightBatchBaselineState baseline, int previousFlightInstanceId, string reason)
        {
            // Attempt 1.
            Exception failure = null;
            yield return coroutineHost.StartCoroutine(RunCoroutineSafely(
                RestoreBatchFlightBaselineCore(baseline, previousFlightInstanceId, reason + ":attempt1"),
                ex => failure = ex));

            // Skips are NOT retried: a skip means the slot/snapshot was structurally
            // absent (missing file, invalid activeVessel index), which a retry cannot fix.
            if (failure != null && !(failure is InGameTestSkippedException))
            {
                ParsekLog.Warn(Tag,
                    $"Batch baseline restore attempt 1 failed ({failure.Message}); retrying load once");
                ParsekLog.Error(Tag,
                    $"Batch baseline restore attempt 1 failure detail ({reason}): "
                    + DescribeRestoreFailure(failure));
                Exception retryFailure = null;
                yield return coroutineHost.StartCoroutine(RunCoroutineSafely(
                    RestoreBatchFlightBaselineCore(baseline, previousFlightInstanceId, reason + ":attempt2"),
                    ex => retryFailure = ex));
                failure = retryFailure;
            }

            if (failure == null)
            {
                // Success path: RestoreBatchFlightBaselineCore already swapped the
                // on-disk Parsek/ snapshot AND the imminent CleanupBatchFlightBaselineSave
                // reverts persistent.sfs from the clean .bak. No extra disk work here
                // (keeps the success path byte-identical to today).
                ParsekLog.Info(Tag, $"Batch baseline restore succeeded ({reason})");
                yield break;
            }

            // In-memory load failed (or skipped). Force the on-disk persistent.sfs +
            // Parsek/ sidecars back to baseline so the DISK save is always correct.
            ParsekLog.Error(Tag,
                $"Batch baseline restore failure detail ({reason}): "
                + DescribeRestoreFailure(failure));
            DiskRevertResult disk = ForceRevertCampaignDiskToBaseline(baseline, reason + ":recover");

            if (disk.FullyReverted)
            {
                ParsekLog.Warn(Tag,
                    $"Batch baseline IN-MEMORY restore failed ({failure.Message}) but the on-disk "
                    + "campaign save was fully reverted to baseline; load it (F9 / re-enter the save) to recover.");
                AlertRestoreDegraded(baseline, failure, diskFullyReverted: true);
                // Disk is correct; let teardown sweep the artifacts normally.
            }
            else
            {
                // True last resort: in-memory failed AND the disk revert was
                // partial/failed (persistent.sfs and Parsek/ may now disagree).
                // Preserve every artifact and raise a loud in-game alert.
                // preserveBatchFlightBaselineArtifacts short-circuits the teardown
                // sweep so the .bak + snapshot survive for manual recovery.
                preserveBatchFlightBaselineArtifacts = true;
                preservedBatchFlightBaselineReason =
                    $"restore failed ({failure.Message}); disk revert partial "
                    + $"(persistent={disk.PersistentReverted}, sidecars={disk.SidecarsReverted})";
                ParsekLog.Warn(Tag,
                    "Batch baseline restore FAILED and disk revert is PARTIAL/FAILED "
                    + $"(persistent={disk.PersistentReverted}, sidecars={disk.SidecarsReverted}); "
                    + "artifacts preserved for manual recovery.");
                AlertRestoreDegraded(baseline, failure, diskFullyReverted: false);
            }
        }

        private static DiskRevertResult ForceRevertCampaignDiskToBaseline(
            FlightBatchBaselineState baseline, string reason)
        {
            bool persistentOk = RestoreCampaignPersistentSave(baseline?.PersistentSaveBackupPath);
            bool sidecarsOk = RestoreParsekSidecarsFromSnapshot(baseline?.ParsekSaveSnapshotDirectory);
            ParsekLog.Info(Tag,
                $"Force disk revert ({reason}): persistent={persistentOk} sidecars={sidecarsOk}");
            return new DiskRevertResult { PersistentReverted = persistentOk, SidecarsReverted = sidecarsOk };
        }

        private static bool RestoreParsekSidecarsFromSnapshot(string snapshotDirectory)
        {
            if (string.IsNullOrEmpty(snapshotDirectory) || !Directory.Exists(snapshotDirectory))
                return true; // nothing to restore
            try
            {
                string currentParsek = RecordingPaths.ResolveSaveScopedPath("Parsek");
                if (string.IsNullOrEmpty(currentParsek))
                    return false;
                using (var staged = CreateBatchFlightParsekSaveSnapshotStaging(currentParsek, snapshotDirectory))
                {
                    staged.Activate(); // copies from the surviving snapshot dir; staged-swap is idempotent
                }
                ParsekLog.Info(Tag, "Force disk revert: Parsek/ sidecars reverted to batch-start snapshot");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"Force disk revert: Parsek/ sidecar revert failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Builds the loud last-resort restore-degraded alert message. Factored for
        /// xUnit: takes a tiny info DTO (slot + scene) so it is testable without a
        /// live FlightBatchBaselineState.
        /// </summary>
        internal static string BuildRestoreDegradedAlertMessage(
            FlightBatchBaselineState_SlotInfo info, string failureMessage, bool diskFullyReverted)
        {
            string head = "Parsek test isolation: automatic in-memory restore failed after a test run.";
            string body = diskFullyReverted
                ? "Your on-disk campaign save WAS reverted to its pre-test state. "
                  + "Load it (F9 quickload, or re-enter the save from the main menu) to recover the in-memory game."
                : "Your on-disk campaign save could NOT be fully reverted. Test artifacts have been "
                  + "preserved for manual recovery (see KSP.log [TestRunner] lines).";
            return head + "\n\n" + body + "\n\nSlot: " + (info.SlotName ?? "(unknown)")
                + " | scene: " + info.CapturedScene + " | detail: " + (failureMessage ?? "(none)");
        }

        private void AlertRestoreDegraded(
            FlightBatchBaselineState baseline, Exception failure, bool diskFullyReverted)
        {
            try
            {
                var info = new FlightBatchBaselineState_SlotInfo
                {
                    SlotName = baseline?.SlotName,
                    CapturedScene = baseline != null ? baseline.CapturedScene : HighLogic.LoadedScene,
                };
                string message = BuildRestoreDegradedAlertMessage(
                    info, failure?.Message, diskFullyReverted);
                PopupDialog.SpawnPopupDialog(
                    new Vector2(0.5f, 0.5f),
                    new Vector2(0.5f, 0.5f),
                    "ParsekTestRestoreDegraded",
                    "Parsek Test Isolation",
                    message,
                    "OK",
                    true,
                    HighLogic.UISkin);
            }
            catch (Exception ex)
            {
                // A failed popup must never throw out of teardown.
                ParsekLog.Warn(Tag, $"AlertRestoreDegraded: popup spawn threw: {ex.Message}");
            }
        }

        /// <summary>
        /// DiskOnly teardown (EDITOR / SPACECENTER / FLIGHT-no-vessel): revert the
        /// on-disk persistent.sfs from the batch-start .bak + delete the .bak. No
        /// in-memory reload by design. Preserves the .bak when artifacts are flagged
        /// for manual recovery.
        /// </summary>
        private void TeardownDiskOnlyIsolation(string reason)
        {
            if (batchIsolationMode != BatchIsolationMode.DiskOnly)
                return;
            if (preserveBatchFlightBaselineArtifacts)
            {
                ParsekLog.Warn(Tag,
                    $"Preserving disk-only persistent backup '{diskOnlyPersistentBackupPath}' ({reason})");
                return;
            }
            bool consumable = RestoreCampaignPersistentSave(diskOnlyPersistentBackupPath); // revert disk to baseline
            if (consumable)
                TryDeletePersistentSaveBackup(diskOnlyPersistentBackupPath);
            else
                ParsekLog.Warn(Tag,
                    $"Keeping disk-only persistent backup '{diskOnlyPersistentBackupPath}' after failed revert");
            ParsekLog.Info(Tag, $"Disk-only batch isolation torn down ({reason})");
        }

        private IEnumerator RestoreBatchBaselineAfterCancel(
            FlightBatchBaselineState baseline, int previousFlightInstanceId)
        {
            yield return coroutineHost.StartCoroutine(
                RestoreBatchBaselineWithRecovery(baseline, previousFlightInstanceId, "cancelled-batch-restore"));

            // The restore's commit re-armed the Bug #4803 re-pin window; on the normal
            // path onLevelWasLoaded already disarmed it, but a failed/timed-out reload
            // leaves it armed. Idempotent teardown mirror of RunBatch's normal end.
            Helpers.FlightCameraReloadPin.Disarm("cancelled-batch-restore-complete");
            CleanupBatchFlightBaselineSave();
            ClearTestBatchMarker("cancel");
            batchFlightBaseline = null;
            batchFlightBaselinePrimed = false;
            abortBatchAfterRestoreFailure = false;
            preserveBatchFlightBaselineArtifacts = false;
            preservedBatchFlightBaselineReason = null;
            batchIsolationMode = BatchIsolationMode.None;
            diskOnlyPersistentBackupPath = null;
            finalBatchRestoreDone = false;
            stateDirtySinceLastRestore = false;
            batchInstanceId = Guid.Empty;
            activeCoroutine = null;
            activeTestCoroutine = null;
            activeInnerCoroutine = null;
            isRunning = false;
        }

        // Bug #4803 PREVENTION - the actual fix for the FLIGHT-batch freeze. Decompilation-confirmed
        // mechanism: FlightCamera survives a FLIGHT->FLIGHT reload only because FlightCamera.Start()
        // parents its transform under a DontDestroyOnLoad "pivot"; each reload's fresh FlightCamera
        // self-destructs via the fetch!=null duplicate guard, so the SAME camera persists and fetch
        // stays valid. Stock FlightCamera.OnSceneSwitch re-parents the pivot under the DDOL
        // PSystemSetup root right before unload, which is why ~50 normal reloads work. But an EVA
        // leaves the pivot parented under a TRANSIENT EVA-kerbal vessel; when that vessel is
        // Vessel.Die()'d (Destroy(gameObject) + children) while it is still the camera target,
        // FlightCamera.OnTargetDestroyed refuses to re-home the pivot (it only acts when the dead
        // target is NOT the active vessel), so the DDOL pivot + the FlightCamera under it are destroyed
        // with the vessel and FlightCamera.OnDestroy sets fetch=null. The corruption is PERMANENT (the
        // next MakeActive NREs on the null fetch and no FLIGHT->FLIGHT reload re-establishes it).
        //
        // fetch is still VALID here (the camera dies during the reload's UNLOAD, which happens later),
        // so re-home the pivot onto a SURVIVING vessel now: force Flight camera mode (exit any IVA left
        // by an EVA test) and re-target the live active vessel. Then the reload's stock OnSceneSwitch
        // rescue re-parents the pivot under DDOL PSystemSetup against a live target - exactly the clean
        // case - and the persistent FlightCamera survives the reload with fetch intact. Best-effort +
        // fully guarded: any failure just proceeds (the reload-guard + storm-abort backstops apply).
        private void EnsureFlightCameraSurvivesReload(string cleanupReason)
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
                return;
            if (FlightCamera.fetch == null)
            {
                // Already destroyed (corruption already happened, or no camera yet) - nothing to
                // re-home. Proceed; the reload-guard settle-check + storm-abort will catch the flood.
                ParsekLog.Warn(Tag,
                    $"Pre-reload camera guard ({cleanupReason}): FlightCamera.fetch is already null - " +
                    "cannot re-home; the reload's camera bring-up may fail (stock Bug #4803).");
                return;
            }
            Vessel active = FlightGlobals.ActiveVessel;
            try
            {
                if (CameraManager.Instance != null)
                    CameraManager.Instance.SetCameraFlight();
                if (active != null)
                    FlightCamera.fetch.SetTargetVessel(active);
                ParsekLog.Info(Tag,
                    $"Pre-reload camera guard ({cleanupReason}): forced Flight mode + re-homed the FlightCamera pivot " +
                    $"onto '{(active != null ? active.vesselName : "<null>")}' so it survives the reload (stock Bug #4803 prevention).");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Pre-reload camera guard ({cleanupReason}) threw: {ex.GetType().Name}: {ex.Message} - proceeding (backstops apply).");
            }
        }

        private IEnumerator RestoreBatchFlightBaselineCore(
            FlightBatchBaselineState baseline, int previousFlightInstanceId, string cleanupReason)
        {
            // Bug #4803 prevention: re-home the FlightCamera onto a surviving vessel BEFORE anything
            // else (well before LoadAndValidateGameForQuickload / the scene unload), so the stock
            // OnSceneSwitch rescue keeps the persistent camera alive across the reload. No-op unless a
            // prior EVA test left the camera targeted at a transient vessel. See the method for the
            // full decompiled mechanism. Synchronous + guarded, so it never blocks or throws here.
            EnsureFlightCameraSurvivesReload(cleanupReason);

            // Fail-closed live-data recovery for the batch FLIGHT baseline
            // restore flow. The sequence below splits validation into a
            // truly-non-destructive XML structural pre-check and a
            // potentially-destructive Game-object load, and puts the
            // wipe between them so that the most common failure modes
            // leave the player's combined disk + in-memory state
            // internally consistent.
            //
            // Sequence:
            //   1a. Structural pre-validation via
            //       ValidateQuicksaveStructure. Calls ConfigNode.Load
            //       (XML parse only) and verifies file existence,
            //       FLIGHTSTATE node, VESSEL children, and
            //       activeVesselIdx range. Does NOT call
            //       GamePersistence.LoadGame, so no FlightGlobals
            //       persistent-id dictionaries are mutated. Truly
            //       non-destructive.
            //   2.  Stage the Parsek/ snapshot dir on-disk (file copy +
            //       atomic Directory.Move swap, with attempted rollback
            //       inside Activate's catch). Now the on-disk Parsek/
            //       reflects BATCH-START sidecars; live in-memory state
            //       is still pre-call. FlightGlobals untouched.
            //   1b. Realise the validated .sfs into a Game object via
            //       LoadAndValidateGameForQuickload. This DOES mutate
            //       FlightGlobals (LoadGame clears its persistent-id
            //       dictionaries before returning, per stock KSP
            //       decompilation 2026-05-10). The dictionaries are
            //       rebuilt by OnLoad at step 4. If LoadGame fails
            //       despite step 1a passing -- very rare, requires the
            //       .sfs to be structurally well-formed but Game-object
            //       realisation to still fail -- FlightGlobals stays
            //       cleared until manual user reload. Documented
            //       residual.
            //   3.  Run the prep wipe (PrepareForIsolatedBatchFlightBaselineRestore).
            //       Wipes RecordingStore + 6 other Parsek save-scoped
            //       stores. After this, in-memory is "empty" and on-disk
            //       Parsek/ is BATCH-START -- the imminent OnLoad will
            //       reconcile the two.
            //   4.  Commit the scene change via CommitValidatedGameLoad.
            //   5.  Wait for FLIGHT-ready / batch vessel / stock stage
            //       manager. OnLoad fires during the scene change and
            //       rebuilds every wiped store from the loaded game.
            //
            // Failure-mode coverage:
            //   - Step 1a throws (structural validation fails): no
            //     destructive work done. All seven save-scoped stores
            //     intact. Disk untouched. FlightGlobals untouched.
            //     Snapshot rollback NOT armed (wipePerformed=false), so
            //     the finally is a no-op -- in-memory state stays at the
            //     test's current post-test mutation, which is what we
            //     want when no wipe ran.
            //   - Step 2 throws (Activate's own try/catch attempts disk
            //     rollback): in-memory still untouched. Disk in best-
            //     effort baseline-or-rolled-back state. FlightGlobals
            //     untouched. Snapshot rollback NOT armed; finally is a
            //     no-op. Note: a successful Activate followed by step 1b
            //     failure leaves on-disk Parsek/ at BATCH-START while
            //     in-memory stays at TEST-CURRENT -- a known disk/memory
            //     desync, but rolling RecordingStore back here would only
            //     partially fix it (other 6 stores still desync) and
            //     would corrupt the test's forward progress in the more
            //     common "validation never destructively touched memory"
            //     case. We accept disk/memory desync as a residual; manual
            //     F9 recovers.
            //   - Step 1b throws (LoadGame fails despite step 1a
            //     passing): on-disk Parsek/ swapped to BATCH-START,
            //     FlightGlobals cleared. Snapshot rollback NOT armed;
            //     in-memory RecordingStore + other 6 stores stay at
            //     TEST-CURRENT. User has on-disk baseline + cleared
            //     FlightGlobals + memory at TEST-CURRENT. Documented
            //     residual; manual F9 recovers.
            //   - Step 3 throws (one of the eight Reset functions
            //     misbehaves): partial wipe in memory, on-disk Parsek/
            //     at BATCH-START. wipePerformed is set by the helper's
            //     onWipeStart callback IMMEDIATELY before the first
            //     RecordingStore reset, so any throw at-or-after that
            //     boundary -- including a partial failure where
            //     RecordingStore was wiped but a later reset
            //     (GroupHierarchyStore, CrewReservationManager, ...)
            //     threw -- arms the snapshot rollback. The rollback
            //     restores RecordingStore.committedRecordings/Trees/
            //     pendingTree/state/AutoAssignedStandaloneGroups to
            //     BATCH-START -- consistent with on-disk. A throw in the
            //     helper's flag-flip prelude (before onWipeStart fires)
            //     leaves wipePerformed=false; in-memory state is intact
            //     in that case so no rollback is needed.
            //     RecordingStoreTestSnapshot does NOT capture the
            //     additional fields that ResetForTestingInternal clears
            //     (RewindContext state, PendingCleanupPids,
            //     suppressNextTreeSceneExitCommit, PendingScienceSubjects,
            //     etc.); those stay at their reset values. Documented
            //     residual; manual reload recovers.
            //   - Step 4 throws (StartAndFocusVessel rare synchronous
            //     throw): full wipe done (wipePerformed=true), no scene
            //     change queued. Snapshot rollback restores its 5
            //     captured fields. Other 6 Parsek stores wiped + extra
            //     RecordingStore fields beyond the snapshot scope stay
            //     reset. Documented residual.
            //   - Step 5 wait timeouts: scene change was queued, full
            //     wipe ran (wipePerformed=true). OnLoad either fired
            //     (every store rebuilt from disk -- best case) or
            //     LoadScene itself failed (extremely rare; the partial
            //     RecordingStore snapshot rollback fires).
            //
            // The RecordingStore-specific snapshot rollback covers the
            // post-wipe residual edge cases above; the explicit residuals
            // (pre-wipe disk/memory desync on steps 2/1b, snapshot-
            // incomplete fields on steps 3-5) are documented but not
            // snapshotted -- a manual F9 quickload after a failed
            // restore recovers every category. Pair with the
            // iterator-disposal fix in RunCoroutineSafely -- without
            // that, this finally never runs when a nested wait throws,
            // because RunCoroutineSafely abandons the parent routine
            // without disposing it.
            //
            // wipePerformed gates the rollback so a "no destructive
            // work" failure does NOT rewrite in-memory recordings to
            // batch-start: before the wipe, RecordingStore still holds
            // whatever the test left it in (post-test mutations); after
            // the wipe, RecordingStore is empty and rolling back to
            // BATCH-START is the right move. The flag is set by
            // PrepareForIsolatedBatchFlightBaselineRestore's onWipeStart
            // callback at the actual destructive boundary, so a partial
            // helper failure (RecordingStore wiped, later reset throws)
            // still arms the rollback correctly.
            RecordingStoreTestSnapshot preWipeSnapshot = baseline?.RecordingStoreSnapshot;
            bool restoreCommitted = false;
            bool wipePerformed = false;
            bool isFlightBaseline = baseline.CapturedScene == GameScenes.FLIGHT;
            try
            {
                // Step 1a: structural pre-validation via ConfigNode.Load.
                // Truly non-destructive -- parses the .sfs as XML, does
                // NOT call GamePersistence.LoadGame (which would clear
                // FlightGlobals.PersistentLoaded dictionaries as a side
                // effect). Catches the bulk of failure modes (file
                // missing/empty, malformed XML, no FLIGHTSTATE node,
                // no VESSEL nodes, invalid activeVessel index) without
                // mutating any KSP or Parsek state. A non-FLIGHT (Tracking
                // Station) baseline returns via LoadScene with no vessel
                // focus, so an out-of-range activeVessel index is tolerated
                // there (a TS save can carry activeVessel = -1).
                Helpers.QuickloadResumeHelpers.ValidateQuicksaveStructure(
                    baseline.SlotName, requireValidActiveVessel: isFlightBaseline);

                using (var stagedSnapshot = CreateBatchFlightParsekSaveSnapshotStaging(baseline))
                {
                    // Step 2: stage the Parsek/ snapshot dir on disk.
                    // ActivateStagedBatchFlightBaselineRestore's helper
                    // does a current→backup move, then staging→current
                    // move, with attempted rollback inside Activate's
                    // catch on partial failure. Dispose only deletes
                    // residual temp dirs; it cannot roll back a
                    // committed swap, which is fine because step 1a
                    // already passed and step 3's wipe will follow.
                    ActivateStagedBatchFlightBaselineRestore(
                        stagedSnapshot.Activate,
                        prepareForRestore: null);
                }

                // Step 1b: realise the validated slot into a Game object
                // via GamePersistence.LoadGame. This call DOES mutate
                // FlightGlobals (clears persistent-id dicts) as a side
                // effect of stock KSP's LoadGame internals -- the
                // dictionaries are rebuilt by OnLoad on the imminent
                // scene change. If LoadGame fails despite step 1a's
                // structural pre-check passing (very rare -- requires
                // FLIGHTSTATE structurally well-formed but Game-object
                // realisation still failing), FlightGlobals stays
                // cleared until the user manually reloads. Documented
                // residual.
                // FLIGHT realises via the activeVessel-validated load (its
                // commit focuses that vessel); a non-FLIGHT baseline realises
                // via the vessel-tolerant load and returns through LoadScene.
                Helpers.QuickloadResumeHelpers.ValidatedGameLoad validatedLoad = default;
                Game nonFlightGame = null;
                if (isFlightBaseline)
                    validatedLoad = Helpers.QuickloadResumeHelpers.LoadAndValidateGameForQuickload(baseline.SlotName);
                else
                    nonFlightGame = Helpers.QuickloadResumeHelpers.LoadGameForSceneRestore(baseline.SlotName);

                // Step 3: prep wipe runs only after validation succeeded
                // AND the on-disk Parsek/ has been swapped to BATCH-START.
                // PrepareForIsolatedBatchFlightBaselineRestore invokes the
                // onWipeStart callback IMMEDIATELY before its first
                // destructive store reset (RecordingStore.
                // ResetForBatchFlightBaselineRestoreBypassingGuard). Setting
                // wipePerformed=true inside that callback arms the snapshot
                // rollback in the finally below at the actual destructive
                // boundary -- not after the helper returns. The eight
                // Reset calls inside the helper are not atomic, so if a
                // later reset (GroupHierarchyStore, CrewReservationManager,
                // ...) throws after RecordingStore has been wiped, the
                // finally must still fire the rollback to recover live
                // RecordingStore data.
                {
                    var currentScenario = UnityEngine.Object.FindObjectOfType(typeof(ParsekScenario))
                        as ParsekScenario;
                    ParsekScenario.PrepareForIsolatedBatchFlightBaselineRestore(
                        currentScenario != null
                            ? (Action)currentScenario.UnsubscribeStateRecorderForIsolatedBatchFlightBaselineRestore
                            : null,
                        onWipeStart: () => wipePerformed = true);
                }

                // Step 4 + 5: commit the scene change and wait for it to land.
                // OnLoad (including ParsekScenario, which rebuilds the wiped
                // Parsek save-scoped stores from the on-disk BATCH-START
                // snapshot) fires during the transition in either scene.
                if (isFlightBaseline)
                {
                    // FLIGHT: StartAndFocusVessel re-focuses the captured
                    // active vessel; wait for FLIGHT-ready + that vessel +
                    // (for a staged baseline) the stock stage manager.
                    // The commit arms the Bug #4803 late-switch camera re-pin
                    // window (FlightCameraReloadPin) which onVesselChange-driven
                    // switches hit; the end-of-frame pin below covers same-frame
                    // switches through code paths that do NOT fire onVesselChange
                    // (2026-07-10: the late "Hudmy Kerman" switch's exact caller
                    // was never identified, so the guard must not depend on it).
                    Helpers.QuickloadResumeHelpers.CommitValidatedGameLoad(validatedLoad);
                    yield return new WaitForEndOfFrame();
                    Helpers.FlightCameraReloadPin.PinNowIfArmed(cleanupReason + ":post-commit");
                    yield return Helpers.QuickloadResumeHelpers.WaitForFlightReady(
                        previousFlightInstanceId, timeoutSeconds: 15f);
                    yield return WaitForBatchBaselineVessel(baseline, timeoutSeconds: 10f);
                    PerformBetweenRunCleanup(cleanupReason);
                    if (ShouldWaitForStockStageManager(baseline.ActiveVesselSituation))
                        yield return WaitForStockStageManagerReady(timeoutSeconds: 10f);
                }
                else
                {
                    // Non-FLIGHT (Tracking Station): return via LoadScene; no
                    // vessel focus / stage manager to wait on. The test that
                    // ran transitioned into FLIGHT (stock "Fly"), so this
                    // returns the player to the captured Tracking Station.
                    //
                    // Capture the live ParsekScenario instance id BEFORE the
                    // reload so the wait can require a freshly-rebuilt scenario.
                    // The prime restore reloads TRACKSTATION while already in
                    // TRACKSTATION, so a bare "LoadedScene == target" wait would
                    // return on frame 1 (before the reload tears the scene
                    // down) and let the canary run against a half-loaded scene.
                    int previousScenarioInstanceId = CurrentParsekScenarioInstanceId();
                    Helpers.QuickloadResumeHelpers.CommitNonFlightSceneLoad(
                        nonFlightGame, baseline.SlotName, baseline.CapturedScene);
                    // Same Bug #4803 end-of-frame pin as the FLIGHT branch: the commit
                    // armed the re-pin window; cover same-frame switches that bypass
                    // onVesselChange before the old scene unloads.
                    yield return new WaitForEndOfFrame();
                    Helpers.FlightCameraReloadPin.PinNowIfArmed(cleanupReason + ":post-commit");
                    yield return WaitForBatchBaselineNonFlightScene(
                        baseline.CapturedScene, previousScenarioInstanceId, timeoutSeconds: 15f);
                    PerformBetweenRunCleanup(cleanupReason);
                }
                restoreCommitted = true;
            }
            finally
            {
                if (!restoreCommitted && wipePerformed && preWipeSnapshot != null)
                {
                    preWipeSnapshot.Restore();
                    ParsekLog.Warn(Tag,
                        "Batch FLIGHT baseline restore did not complete after the prep wipe; "
                        + "rolled RecordingStore back to batch-start snapshot to recover live data "
                        + "(committedRecordings=" + preWipeSnapshot.CommittedRecordingCount
                        + ", committedTrees=" + preWipeSnapshot.CommittedTreeCount
                        + ", hasPendingTree=" + preWipeSnapshot.HasPendingTree
                        + "). slot='" + (baseline?.SlotName ?? "(null)") + "'");
                }
                else if (!restoreCommitted && !wipePerformed)
                {
                    // Failure occurred before the prep wipe (validation,
                    // disk swap, or LoadGame). RecordingStore in memory
                    // still holds whatever the test left it in -- rolling
                    // back to BATCH-START would corrupt forward progress.
                    // Disk and other Parsek stores may be partially
                    // touched (Activate's disk swap, LoadGame clearing
                    // FlightGlobals dicts) but the in-memory RecordingStore
                    // is intact. See failure-mode comment block above.
                    ParsekLog.Warn(Tag,
                        "Batch FLIGHT baseline restore did not complete and never crossed the prep-wipe "
                        + "destructive boundary; leaving RecordingStore at its current in-memory state "
                        + "(snapshot rollback NOT armed). slot='"
                        + (baseline?.SlotName ?? "(null)") + "'");
                }
            }
        }

        internal static void ActivateStagedBatchFlightBaselineRestore(
            Action activateSnapshot,
            Action prepareForRestore)
        {
            InGameAssert.IsNotNull(activateSnapshot,
                "Automatic FLIGHT batch restore requires a staged snapshot activation callback.");
            activateSnapshot();
            prepareForRestore?.Invoke();
        }

        private void CleanupBatchFlightBaselineSave()
        {
            if (batchFlightBaseline == null || string.IsNullOrEmpty(batchFlightBaseline.SlotName))
                return;

            if (preserveBatchFlightBaselineArtifacts)
            {
                ParsekLog.Warn(Tag,
                    $"Disk reverted; in-memory artifacts preserved for recovery: slot='{batchFlightBaseline.SlotName}', snapshot='{batchFlightBaseline.ParsekSaveSnapshotDirectory}', persistentBackup='{batchFlightBaseline.PersistentSaveBackupPath}', reason='{preservedBatchFlightBaselineReason ?? "restore failure"}'");
                return;
            }

            // Restore the campaign's persistent.sfs to its batch-start bytes
            // BEFORE deleting the backup. A test may have written persistent.sfs
            // directly or via a scene-change auto-save; this leaves the player's
            // main career save exactly as it was. Only delete the backup if the
            // restore is consumable (succeeded or nothing to restore); a failed
            // restore keeps the .bak so the player can recover manually.
            bool persistentRestoreConsumable =
                RestoreCampaignPersistentSave(batchFlightBaseline.PersistentSaveBackupPath);

            Helpers.QuickloadResumeHelpers.TryDeleteSaveSlot(batchFlightBaseline.SlotName);
            TryDeleteDirectoryRecursive(batchFlightBaseline.ParsekSaveSnapshotDirectory);
            if (persistentRestoreConsumable)
                TryDeletePersistentSaveBackup(batchFlightBaseline.PersistentSaveBackupPath);
            else
                ParsekLog.Warn(Tag,
                    $"Batch baseline: keeping persistent.sfs backup '{batchFlightBaseline.PersistentSaveBackupPath}' "
                    + "after a failed restore so the campaign save can be recovered manually");
        }

        private static StagedBatchFlightParsekSaveSnapshot CreateBatchFlightParsekSaveSnapshotStaging(
            FlightBatchBaselineState baseline)
        {
            string currentParsekSaveDirectory = RecordingPaths.ResolveSaveScopedPath("Parsek");
            InGameAssert.IsTrue(!string.IsNullOrEmpty(currentParsekSaveDirectory),
                "Automatic FLIGHT batch restore requires a resolvable save-scoped Parsek directory.");
            InGameAssert.IsNotNull(baseline,
                "Automatic FLIGHT batch restore requires captured baseline metadata.");
            return CreateBatchFlightParsekSaveSnapshotStaging(
                currentParsekSaveDirectory,
                baseline.ParsekSaveSnapshotDirectory);
        }

        internal static StagedBatchFlightParsekSaveSnapshot CreateBatchFlightParsekSaveSnapshotStaging(
            string currentParsekSaveDirectory,
            string snapshotDirectory)
        {
            InGameAssert.IsTrue(!string.IsNullOrEmpty(currentParsekSaveDirectory),
                "Automatic FLIGHT batch restore requires a resolvable save-scoped Parsek directory.");
            InGameAssert.IsTrue(!string.IsNullOrEmpty(snapshotDirectory),
                "Automatic FLIGHT batch restore requires a captured Parsek save snapshot directory.");
            if (!Directory.Exists(snapshotDirectory))
            {
                InGameAssert.Skip(
                    $"Automatic FLIGHT batch restore skipped: Parsek save snapshot '{snapshotDirectory}' was missing");
            }

            string parentDirectory = Path.GetDirectoryName(currentParsekSaveDirectory);
            InGameAssert.IsTrue(!string.IsNullOrEmpty(parentDirectory),
                "Automatic FLIGHT batch restore requires a Parsek save directory parent.");
            Directory.CreateDirectory(parentDirectory);

            string restoreStagingDirectory = CreateBatchFlightTempSiblingDirectory(
                currentParsekSaveDirectory, "restore");
            string backupDirectory = CreateBatchFlightTempSiblingDirectory(
                currentParsekSaveDirectory, "backup");

            try
            {
                CopyDirectoryRecursive(snapshotDirectory, restoreStagingDirectory);
            }
            catch
            {
                if (Directory.Exists(restoreStagingDirectory))
                    TryDeleteDirectoryRecursive(restoreStagingDirectory);

                throw;
            }

            return new StagedBatchFlightParsekSaveSnapshot(
                currentParsekSaveDirectory,
                restoreStagingDirectory,
                backupDirectory);
        }

        private static string CreateBatchFlightTempSiblingDirectory(string currentParsekSaveDirectory, string suffix)
        {
            string parentDirectory = Path.GetDirectoryName(currentParsekSaveDirectory) ?? string.Empty;
            string leafName = Path.GetFileName(currentParsekSaveDirectory);
            return Path.Combine(parentDirectory,
                leafName + "-" + suffix + "-" + Guid.NewGuid().ToString("N"));
        }

        private static void CopyDirectoryRecursive(string sourceDirectory, string destinationDirectory)
        {
            InGameAssert.IsTrue(Directory.Exists(sourceDirectory),
                $"Automatic FLIGHT batch restore failed: source directory '{sourceDirectory}' did not exist");

            Directory.CreateDirectory(destinationDirectory);

            foreach (string directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relative = directory.Substring(sourceDirectory.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
            }

            foreach (string file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(sourceDirectory.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string destinationFile = Path.Combine(destinationDirectory, relative);
                string destinationParent = Path.GetDirectoryName(destinationFile);
                if (!string.IsNullOrEmpty(destinationParent))
                    Directory.CreateDirectory(destinationParent);
                File.Copy(file, destinationFile, overwrite: true);
            }
        }

        private static void TryDeleteDirectoryRecursive(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath))
                return;

            try
            {
                DeleteDirectoryRecursive(directoryPath);
            }
            catch (IOException ex)
            {
                ParsekLog.Warn(Tag,
                    $"Best-effort cleanup skipped directory '{directoryPath}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                ParsekLog.Warn(Tag,
                    $"Best-effort cleanup skipped directory '{directoryPath}': {ex.Message}");
            }
        }

        private static void DeleteDirectoryRecursive(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath))
                return;

            foreach (string file in Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(directoryPath, recursive: true);
        }

        private static int CurrentParsekScenarioInstanceId()
        {
            var scenario = UnityEngine.Object.FindObjectOfType<ParsekScenario>();
            return scenario != null ? scenario.GetInstanceID() : 0;
        }

        /// <summary>
        /// Ready predicate for a NON-FLIGHT baseline scene return. Mirrors
        /// <see cref="Helpers.QuickloadResumeHelpers.IsReloadedFlightReady"/>:
        /// requires BOTH the target scene loaded AND a freshly-rebuilt
        /// ParsekScenario, so a same-scene reload (the TRACKSTATION prime restore
        /// reloading TRACKSTATION) is not reported ready on frame 1 before the
        /// reload has actually torn down and recreated the scene. Instance id 0
        /// is the "no scenario" sentinel.
        /// </summary>
        internal static bool IsReloadedNonFlightSceneReady(
            GameScenes targetScene, GameScenes loadedScene,
            int currentScenarioInstanceId, int previousScenarioInstanceId)
        {
            bool replacedScenario = currentScenarioInstanceId != 0
                && (previousScenarioInstanceId == 0
                    || currentScenarioInstanceId != previousScenarioInstanceId);
            return loadedScene == targetScene && replacedScenario;
        }

        private IEnumerator WaitForBatchBaselineNonFlightScene(
            GameScenes scene, int previousScenarioInstanceId, float timeoutSeconds)
        {
            // Wall-clock: a non-flight scene load can run while the game is
            // paused (timeScale 0), which would freeze Time.time.
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (IsReloadedNonFlightSceneReady(
                        scene, HighLogic.LoadedScene,
                        CurrentParsekScenarioInstanceId(), previousScenarioInstanceId))
                    yield break;
                yield return null;
            }
            InGameAssert.IsTrue(false,
                $"WaitForBatchBaselineNonFlightScene timed out after {timeoutSeconds:F0}s "
                + $"(wanted={scene}, current={HighLogic.LoadedScene}, "
                + $"awaiting a rebuilt ParsekScenario != id={previousScenarioInstanceId})");
        }

        private static IEnumerator WaitForBatchBaselineVessel(
            FlightBatchBaselineState baseline, float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            float stableMatchStarted = -1f;
            while (Time.time < deadline)
            {
                Vessel vessel = FlightGlobals.ActiveVessel;
                bool matchesBaseline = HighLogic.LoadedScene == GameScenes.FLIGHT
                    && FlightGlobals.ready
                    && FlightInputHandler.state != null
                    && vessel != null
                    && vessel.id == baseline.ActiveVesselId
                    && DoesVesselMatchBatchBaselineSituation(vessel, baseline.ActiveVesselSituation);
                if (matchesBaseline)
                {
                    if (stableMatchStarted < 0f)
                    {
                        stableMatchStarted = Time.unscaledTime;
                    }
                    else if ((Time.unscaledTime - stableMatchStarted) >= BatchBaselineStableMatchSeconds)
                    {
                        yield break;
                    }
                }
                else
                {
                    stableMatchStarted = -1f;
                }

                yield return null;
            }

            Vessel timedOutVessel = FlightGlobals.ActiveVessel;
            string actualName = timedOutVessel != null ? timedOutVessel.vesselName : "null";
            string actualId = timedOutVessel != null ? timedOutVessel.id.ToString() : "null";
            string actualSituation = timedOutVessel != null
                ? timedOutVessel.situation.ToString()
                : "null";
            InGameAssert.Fail(
                $"WaitForBatchBaselineVessel timed out after {timeoutSeconds:F0}s " +
                $"(expectedVessel='{baseline.ActiveVesselName}'/{baseline.ActiveVesselId}, " +
                $"expectedSituation={baseline.ActiveVesselSituation}, " +
                $"actualVessel='{actualName}'/{actualId}, actualSituation={actualSituation}, " +
                $"scene={HighLogic.LoadedScene}, flightReady={FlightGlobals.ready}, " +
                $"controlsReady={(FlightInputHandler.state != null)}, " +
                $"stableWindow={BatchBaselineStableMatchSeconds:F1}s)");
        }

        private static bool DoesVesselMatchBatchBaselineSituation(
            Vessel vessel, Vessel.Situations expectedSituation)
        {
            if (vessel == null)
                return false;
            if (vessel.situation == expectedSituation)
                return true;

            return expectedSituation == Vessel.Situations.PRELAUNCH
                && vessel.LandedOrSplashed;
        }

        internal static bool ShouldWaitForStockStageManager(Vessel.Situations expectedSituation)
        {
            return expectedSituation == Vessel.Situations.PRELAUNCH;
        }

        internal static bool IsStageManagerReadyForActivateNextStage(
            bool hasInstance,
            int stageCount,
            bool rebuildIndexes,
            bool hasSortRoutine)
        {
            return hasInstance
                && stageCount > 0
                && !rebuildIndexes
                && !hasSortRoutine;
        }

        internal static IEnumerator WaitForStockStageManagerReady(float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            float stableMatchStarted = -1f;
            while (Time.time < deadline)
            {
                var stageManager = KSP.UI.Screens.StageManager.Instance;
                bool hasInstance = stageManager != null;
                int stageCount = hasInstance ? KSP.UI.Screens.StageManager.StageCount : 0;
                bool rebuildIndexes = hasInstance
                    && StageManagerRebuildIndexesField?.GetValue(stageManager) is bool rebuildLoop
                    && rebuildLoop;
                bool hasSortRoutine = hasInstance
                    && StageManagerSortRoutineField?.GetValue(stageManager) is Coroutine;
                bool ready = IsStageManagerReadyForActivateNextStage(
                    hasInstance, stageCount, rebuildIndexes, hasSortRoutine);
                if (ready)
                {
                    if (stableMatchStarted < 0f)
                    {
                        stableMatchStarted = Time.unscaledTime;
                    }
                    else if ((Time.unscaledTime - stableMatchStarted) >= BatchBaselineStableMatchSeconds)
                    {
                        yield break;
                    }
                }
                else
                {
                    stableMatchStarted = -1f;
                }

                yield return null;
            }

            var timedOutStageManager = KSP.UI.Screens.StageManager.Instance;
            bool timedOutHasInstance = timedOutStageManager != null;
            int timedOutStageCount = timedOutHasInstance ? KSP.UI.Screens.StageManager.StageCount : 0;
            bool timedOutRebuildIndexes = timedOutHasInstance
                && StageManagerRebuildIndexesField?.GetValue(timedOutStageManager) is bool rebuild
                && rebuild;
            bool timedOutHasSortRoutine = timedOutHasInstance
                && StageManagerSortRoutineField?.GetValue(timedOutStageManager) is Coroutine;
            InGameAssert.Fail(
                $"WaitForStockStageManagerReady timed out after {timeoutSeconds:F0}s " +
                $"(hasInstance={timedOutHasInstance}, stageCount={timedOutStageCount}, " +
                $"rebuildIndexes={timedOutRebuildIndexes}, hasSortRoutine={timedOutHasSortRoutine}, " +
                $"stableWindow={BatchBaselineStableMatchSeconds:F1}s)");
        }

        internal static IEnumerator RunCoroutineSafely(
            IEnumerator routine, Action<Exception> onFailure)
        {
            if (routine == null)
                yield break;

            // P1 review fix: every yield-break path below abandons `routine`.
            // C# iterator state machines do not run try/finally blocks just
            // because the consumer stops iterating -- only an explicit
            // Dispose() unwinds the suspended state. RestoreBatchFlightBaselineCore
            // relies on its outer try/finally to roll RecordingStore back when
            // a nested wait throws (TriggerQuickload skip, WaitForFlightReady
            // timeout); without disposing the parent iterator here, that
            // finally never runs and the player's live in-memory data stays
            // wiped. Wrap the body so EVERY exit path disposes `routine`.
            try
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = routine.MoveNext();
                    }
                    catch (Exception ex)
                    {
                        onFailure?.Invoke(ex);
                        yield break;
                    }

                    if (!hasNext)
                        yield break;

                    if (routine.Current is IEnumerator nestedRoutine)
                    {
                        Exception nestedFailure = null;
                        yield return RunCoroutineSafely(nestedRoutine, ex => nestedFailure = ex);
                        if (nestedFailure != null)
                        {
                            onFailure?.Invoke(nestedFailure);
                            yield break;
                        }

                        continue;
                    }

                    yield return routine.Current;
                }
            }
            finally
            {
                // Dispose is idempotent on compiler-generated iterator state
                // machines; safe to call after natural completion (`!hasNext`),
                // after the synchronous-throw path, and after the
                // nested-failure path. The cast handles non-generic
                // IEnumerator that may or may not implement IDisposable
                // (compiler-generated iterators always do).
                (routine as IDisposable)?.Dispose();
            }
        }

        /// <summary>
        /// Full-detail formatter for batch-baseline restore / prime failures.
        /// The 2026-07-10 verify run's prime NRE was undiagnosable because every
        /// catch site logged only <c>ex.Message</c> ("Object reference not set...")
        /// with no stack trace, so the throwing statement was unrecoverable.
        /// <see cref="Exception.ToString"/> includes the exception type, message,
        /// every inner exception, and the stack trace of the throw site; use this
        /// at EVERY restore/prime failure report so the next occurrence names the
        /// statement. Pure and null-tolerant. The short <c>ex.Message</c> stays in
        /// the test-result row; this full detail goes to the log only.
        /// </summary>
        internal static string DescribeRestoreFailure(Exception ex)
        {
            if (ex == null)
                return "(null exception)";
            try
            {
                return ex.ToString();
            }
            catch (Exception formatEx)
            {
                // A pathological ToString override must never mask the original
                // failure; fall back to the parts that cannot throw.
                return ex.GetType().FullName + ": " + ex.Message
                    + " (full detail unavailable: ToString threw " + formatEx.GetType().Name + ")";
            }
        }

        private void FailAndAbortBatchAfterRestore(InGameTestInfo test, string restoreMessage)
        {
            string combinedMessage = string.IsNullOrEmpty(test.ErrorMessage)
                ? restoreMessage
                : test.ErrorMessage + " | " + restoreMessage;
            test.Status = TestStatus.Failed;
            test.ErrorMessage = combinedMessage;
            RecordSceneResult(
                test,
                test.RequiredScene == InGameTestAttribute.AnyScene ? GameScenes.FLIGHT : test.RequiredScene,
                TestStatus.Failed,
                combinedMessage,
                test.DurationMs);
            abortBatchAfterRestoreFailure = true;
            preserveBatchFlightBaselineArtifacts = true;
            preservedBatchFlightBaselineReason = combinedMessage;
            ParsekLog.Warn(Tag, $"FAILED: {test.Name} - {combinedMessage}");
            ParsekLog.Warn(Tag,
                $"Aborting batch after restore failure in {test.Name}; the current session is no longer trusted");
        }

        private void RecountResults()
        {
            int passed = 0, failed = 0, skipped = 0;
            foreach (var t in allTests)
            {
                switch (t.Status)
                {
                    case TestStatus.Passed:  passed++;  break;
                    case TestStatus.Failed:  failed++;  break;
                    case TestStatus.Skipped: skipped++; break;
                }
            }
            Passed = passed;
            Failed = failed;
            Skipped = skipped;
        }

        private IEnumerator RunOneTest(InGameTestInfo test)
        {
            test.Status = TestStatus.Running;
            test.ErrorMessage = null;
            ParsekLog.Verbose(Tag, $"Running: {test.Name}");

            object instance = null;
            var sw = Stopwatch.StartNew();
            bool needsCoroutineWait = false;
            bool coroutineRunning = false;
            Exception coroutineError = null;

            // Phase 1: synchronous setup + invocation (in try/catch)
            try
            {
                if (!test.Method.IsStatic)
                    instance = CreateTestInstance(test.DeclaringType);

                RunLifecycleMethod<InGameSetupAttribute>(instance, test.DeclaringType);

                object result;
                if (test.Method.IsStatic)
                    result = test.Method.Invoke(null, null);
                else
                    result = test.Method.Invoke(instance, null);

                if (result is IEnumerator enumerator)
                {
                    needsCoroutineWait = true;
                    coroutineRunning = true;

                    IEnumerator SafeEnumerator()
                    {
                        Exception nestedFailure = null;
                        yield return RunCoroutineSafely(enumerator, ex => nestedFailure = ex);
                        coroutineError = nestedFailure;
                        coroutineRunning = false;
                    }

                    activeInnerCoroutine = coroutineHost.StartCoroutine(SafeEnumerator());
                }
            }
            catch (TargetInvocationException tie) when (tie.InnerException is InGameTestSkippedException skipEx)
            {
                RecordSkip(test, sw, skipEx.Message);
                RunCleanup(instance, test);
                yield break;
            }
            catch (InGameTestSkippedException skipEx)
            {
                RecordSkip(test, sw, skipEx.Message);
                RunCleanup(instance, test);
                yield break;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                RecordFailure(test, sw, tie.InnerException.Message);
                RunCleanup(instance, test);
                yield break;
            }
            catch (Exception ex)
            {
                RecordFailure(test, sw, ex.Message);
                RunCleanup(instance, test);
                yield break;
            }

            // Phase 2: yield loop for coroutine tests (outside try/catch)
            if (needsCoroutineWait)
            {
                while (coroutineRunning)
                    yield return null;
            }

            // Phase 3: record result
            sw.Stop();
            test.DurationMs = (float)sw.Elapsed.TotalMilliseconds;

            if (coroutineError != null)
            {
                // Unwrap TargetInvocationException if needed
                var inner = coroutineError is TargetInvocationException tie2 && tie2.InnerException != null
                    ? tie2.InnerException
                    : coroutineError;

                if (inner is InGameTestSkippedException)
                    RecordSkip(test, sw, inner.Message);
                else
                    RecordFailure(test, sw, inner.Message);
            }
            else
            {
                test.Status = TestStatus.Passed;
                test.ErrorMessage = null;
                RecordSceneResult(test, TestStatus.Passed, null, test.DurationMs);
                ParsekLog.Verbose(Tag, $"PASSED: {test.Name} ({test.DurationMs:F1}ms)");
            }

            RunCleanup(instance, test);
        }

        private void RecordFailure(InGameTestInfo test, Stopwatch sw, string message)
        {
            sw.Stop();
            test.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
            test.Status = TestStatus.Failed;
            test.ErrorMessage = message;
            RecordSceneResult(test, TestStatus.Failed, message, test.DurationMs);
            ParsekLog.Warn(Tag, $"FAILED: {test.Name} - {message}");
        }

        private void RecordSkip(InGameTestInfo test, Stopwatch sw, string reason)
        {
            sw.Stop();
            test.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
            test.Status = TestStatus.Skipped;
            test.ErrorMessage = reason;
            RecordSceneResult(test, TestStatus.Skipped, reason, test.DurationMs);
            ParsekLog.Verbose(Tag, $"SKIPPED: {test.Name} - {reason}");
        }

        /// <summary>
        /// Stamp the current scene's outcome for this test. Called once per
        /// terminal transition (Passed / Failed / Skipped-by-the-test itself).
        /// The eligibility-skip branch in <see cref="RunBatch"/> deliberately
        /// does NOT call this — recording "not applicable in this scene" rows
        /// would clutter the per-scene export with noise that adds no signal.
        /// </summary>
        private static void RecordSceneResult(InGameTestInfo test, TestStatus status,
            string errorMessage, float durationMs)
        {
            RecordSceneResult(test, HighLogic.LoadedScene, status, errorMessage, durationMs);
        }

        private static void RecordSceneResult(InGameTestInfo test, GameScenes scene, TestStatus status,
            string errorMessage, float durationMs)
        {
            if (test == null) return;
            test.ResultsByScene[scene] = new SceneResult
            {
                Status = status,
                ErrorMessage = errorMessage,
                DurationMs = durationMs,
                TimestampUtc = DateTime.UtcNow,
            };
        }

        private void RunCleanup(object instance, InGameTestInfo test)
        {
            activeInnerCoroutine = null;

            try
            {
                RunLifecycleMethod<InGameTeardownAttribute>(instance, test.DeclaringType);
            }
            catch (Exception teardownEx)
            {
                ParsekLog.Warn(Tag, $"Teardown error in {test.Name}: {teardownEx.Message}");
            }

            foreach (var go in cleanupRegistry)
            {
                if (go != null) UnityEngine.Object.Destroy(go);
            }
            cleanupRegistry.Clear();

            // Safety net: if a building-entry test failed to close its facility
            // (its own try/finally is the primary close; this catches a despawn
            // event that no-op'd or a body that opened a building outside a
            // finally), force it closed so the NEXT test starts from a clean,
            // unpaused Space Center instead of running blind behind an open
            // facility canvas. No-op outside SPACECENTER.
            ForceCloseOpenSpaceCenterFacilities("post-test:" + (test?.Name ?? "(unknown)"));
        }

        private object CreateTestInstance(Type type)
        {
            // Try constructor that takes InGameTestRunner (for cleanup registration)
            var ctorWithRunner = type.GetConstructor(new[] { typeof(InGameTestRunner) });
            if (ctorWithRunner != null)
                return ctorWithRunner.Invoke(new object[] { this });

            // Fall back to parameterless constructor
            return Activator.CreateInstance(type);
        }

        private void RunLifecycleMethod<TAttr>(object instance, Type type) where TAttr : Attribute
        {
            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                if (method.GetCustomAttribute<TAttr>() == null) continue;

                if (method.IsStatic)
                    method.Invoke(null, null);
                else if (instance != null)
                    method.Invoke(instance, null);
            }
        }

        private const string ResultsFileName = "parsek-test-results.txt";

        /// <summary>
        /// Writes the results report to the KSP root. Called automatically at
        /// the end of every batch run (see <see cref="RunBatch"/>), so the file
        /// always reflects the latest run; there is no manual export button.
        /// </summary>
        private void ExportResultsFile()
        {
            try
            {
                string kspRoot = KSPUtil.ApplicationRootPath ?? "";
                string path = Path.Combine(kspRoot, ResultsFileName);
                var lines = FormatResultsReport(allTests, DateTime.Now,
                    exportScene: HighLogic.LoadedScene);
                File.WriteAllText(path, string.Join("\n", lines) + "\n");
                ParsekLog.Info(Tag,
                    $"Test results written to {path} (auto after run)");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"Failed to write test results file: {ex.Message}");
            }
        }

        /// <summary>
        /// Pure formatter for the results file. Walks every test's
        /// <see cref="InGameTestInfo.ResultsByScene"/> so a KSC Run All followed
        /// by a Flight Run All yields both scenes' outcomes in the export
        /// (previously the later run overwrote the earlier). Each scene block
        /// lists its own per-test failures and full results; a trailing
        /// "UNION" block aggregates the last-observed status per test so a
        /// reader who only wants "current-session health" still gets one.
        /// </summary>
        internal static List<string> FormatResultsReport(
            IList<InGameTestInfo> tests, DateTime exportedAt, GameScenes exportScene)
        {
            var ic = CultureInfo.InvariantCulture;
            var lines = new List<string>();
            if (tests == null) tests = new List<InGameTestInfo>();

            // Gather all scenes that have produced any per-test result, in the
            // order they were first captured (determinism: stable across exports).
            var scenesWithResults = new List<GameScenes>();
            var scenesSeen = new HashSet<GameScenes>();
            foreach (var t in tests)
            {
                if (t?.ResultsByScene == null) continue;
                foreach (var kv in t.ResultsByScene)
                {
                    if (scenesSeen.Add(kv.Key))
                        scenesWithResults.Add(kv.Key);
                }
            }
            scenesWithResults.Sort((a, b) => string.CompareOrdinal(a.ToString(), b.ToString()));

            lines.Add("Parsek In-Game Test Results");
            lines.Add($"Exported at: {exportedAt.ToString("yyyy-MM-dd HH:mm:ss", ic)}");
            lines.Add($"Export scene (current): {exportScene}");
            lines.Add($"Total discovered tests: {tests.Count}");

            // Per-scene header: last-run timestamp + pass/fail/skip tally for that scene only.
            if (scenesWithResults.Count == 0)
            {
                lines.Add("Scenes with captured results: (none - run a batch first)");
            }
            else
            {
                lines.Add($"Scenes with captured results: {scenesWithResults.Count}");
                foreach (var scene in scenesWithResults)
                {
                    int p = 0, f = 0, s = 0;
                    DateTime lastRun = DateTime.MinValue;
                    int captured = 0;
                    foreach (var t in tests)
                    {
                        SceneResult r;
                        if (t?.ResultsByScene == null ||
                            !t.ResultsByScene.TryGetValue(scene, out r))
                            continue;
                        captured++;
                        if (r.TimestampUtc > lastRun) lastRun = r.TimestampUtc;
                        switch (r.Status)
                        {
                            case TestStatus.Passed:  p++; break;
                            case TestStatus.Failed:  f++; break;
                            case TestStatus.Skipped: s++; break;
                        }
                    }
                    string ts = lastRun == DateTime.MinValue
                        ? "—"
                        : lastRun.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", ic);
                    lines.Add($"  {scene,-14} last run {ts}   " +
                              $"captured={captured}  Passed={p}  Failed={f}  Skipped={s}");
                }
            }

            lines.Add(new string('-', 80));

            // Per-scene FAILURES block — actionable at a glance, labeled by scene
            // so a reader can tell which mode the failure came from.
            bool anyFailures = false;
            foreach (var scene in scenesWithResults)
            {
                var sceneFailures = new List<InGameTestInfo>();
                foreach (var t in tests)
                {
                    SceneResult r;
                    if (t?.ResultsByScene != null &&
                        t.ResultsByScene.TryGetValue(scene, out r) &&
                        r.Status == TestStatus.Failed)
                    {
                        sceneFailures.Add(t);
                    }
                }
                if (sceneFailures.Count == 0) continue;
                if (!anyFailures)
                {
                    lines.Add("");
                    lines.Add("FAILURES (grouped by scene):");
                    anyFailures = true;
                }
                lines.Add($"  [{scene}]");
                foreach (var t in sceneFailures)
                {
                    var r = t.ResultsByScene[scene];
                    lines.Add($"    FAIL  {t.Name} ({r.DurationMs.ToString("F1", ic)}ms)");
                    if (!string.IsNullOrEmpty(r.ErrorMessage))
                        lines.Add($"          {r.ErrorMessage}");
                }
            }

            // Per-test block — each test gets one row per scene it has been run in.
            // Tests that have never run in any scene still appear, with a "(never run)"
            // line, so the report is a complete catalog rather than only-run-tests.
            lines.Add("");
            lines.Add("ALL RESULTS (one row per scene, per test):");
            string currentCategory = null;
            foreach (var t in tests)
            {
                if (t == null) continue;
                if (t.Category != currentCategory)
                {
                    currentCategory = t.Category;
                    lines.Add($"  [{currentCategory}]");
                }
                lines.Add($"    {t.Name}");
                if (t.ResultsByScene == null || t.ResultsByScene.Count == 0)
                {
                    lines.Add("      (never run)");
                    continue;
                }
                // Stable ordering so diffs between exports are readable.
                var sceneKeys = new List<GameScenes>(t.ResultsByScene.Keys);
                sceneKeys.Sort((a, b) => string.CompareOrdinal(a.ToString(), b.ToString()));
                foreach (var scene in sceneKeys)
                {
                    var r = t.ResultsByScene[scene];
                    string status = r.Status.ToString().ToUpperInvariant().PadRight(7);
                    string duration = r.DurationMs > 0f
                        ? $" ({r.DurationMs.ToString("F1", ic)}ms)"
                        : "";
                    string err = (r.Status == TestStatus.Failed || r.Status == TestStatus.Skipped)
                        && !string.IsNullOrEmpty(r.ErrorMessage)
                        ? $" — {r.ErrorMessage}"
                        : "";
                    lines.Add($"      {scene,-14} {status}{duration}{err}");
                }
                // List scenes eligible-but-not-run so the report is honest about
                // coverage. Only meaningful for AnyScene tests — scene-tied tests
                // that simply weren't visited don't need the "(not run in X)" line
                // spammed for every scene the user hasn't loaded.
                if (t.RequiredScene == InGameTestAttribute.AnyScene
                    && scenesWithResults.Count > 1)
                {
                    foreach (var scene in scenesWithResults)
                    {
                        if (!t.ResultsByScene.ContainsKey(scene))
                            lines.Add($"      {scene,-14} (not run in this scene)");
                    }
                }
            }

            return lines;
        }
    }
}
