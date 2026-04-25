using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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

        private sealed class FlightBatchBaselineState
        {
            public string SlotName;
            public string ParsekSaveSnapshotDirectory;
            public int CapturedFlightInstanceId;
            public Guid ActiveVesselId;
            public string ActiveVesselName;
            public Vessel.Situations ActiveVesselSituation;
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

        public void RunAll()
        {
            if (isRunning) return;
            PerformBetweenRunCleanup("run-all");
            var eligible = PrepareBatchExecution(allTests.Where(t => IsEligibleForScene(t)));
            RecountResults();
            activeCoroutine = coroutineHost.StartCoroutine(RunBatch(eligible));
        }

        public void RunAllIncludingFlightRestore()
        {
            if (isRunning) return;
            batchFlightBaseline = null;
            batchFlightBaselinePrimed = false;
            abortBatchAfterRestoreFailure = false;
            preserveBatchFlightBaselineArtifacts = false;
            preservedBatchFlightBaselineReason = null;
            PerformBetweenRunCleanup("run-all+restore");
            var eligible = PrepareBatchExecutionIncludingFlightRestore(
                allTests.Where(t => IsEligibleForScene(t)));
            eligible = PrepareBatchFlightRestoreExecution(eligible);
            RecountResults();
            activeCoroutine = coroutineHost.StartCoroutine(RunBatch(eligible));
        }

        public void RunCategory(string category)
        {
            if (isRunning) return;
            PerformBetweenRunCleanup("run-category:" + (category ?? "(null)"));
            var eligible = PrepareBatchExecution(allTests
                .Where(t => t.Category == category && IsEligibleForScene(t)));
            RecountResults();
            activeCoroutine = coroutineHost.StartCoroutine(RunBatch(eligible));
        }

        public void RunCategoryIncludingFlightRestore(string category)
        {
            if (isRunning) return;
            batchFlightBaseline = null;
            batchFlightBaselinePrimed = false;
            abortBatchAfterRestoreFailure = false;
            preserveBatchFlightBaselineArtifacts = false;
            preservedBatchFlightBaselineReason = null;
            PerformBetweenRunCleanup("run-category+restore:" + (category ?? "(null)"));
            var eligible = PrepareBatchExecutionIncludingFlightRestore(allTests
                .Where(t => t.Category == category && IsEligibleForScene(t)));
            eligible = PrepareBatchFlightRestoreExecution(eligible);
            RecountResults();
            activeCoroutine = coroutineHost.StartCoroutine(RunBatch(eligible));
        }

        public void RunSingle(InGameTestInfo test)
        {
            if (isRunning) return;
            batchFlightBaseline = null;
            batchFlightBaselinePrimed = false;
            abortBatchAfterRestoreFailure = false;
            preserveBatchFlightBaselineArtifacts = false;
            preservedBatchFlightBaselineReason = null;
            activeCoroutine = coroutineHost.StartCoroutine(RunBatch(new List<InGameTestInfo> { test }));
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
        }

        public void Cancel()
        {
            if (!isRunning) return;
            if (activeInnerCoroutine != null)
                coroutineHost.StopCoroutine(activeInnerCoroutine);
            activeInnerCoroutine = null;
            if (activeTestCoroutine != null)
                coroutineHost.StopCoroutine(activeTestCoroutine);
            activeTestCoroutine = null;
            if (activeCoroutine != null)
                coroutineHost.StopCoroutine(activeCoroutine);
            activeCoroutine = null;

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

            isRunning = false;
            abortBatchAfterRestoreFailure = false;
            ParsekLog.Info(Tag, "Test run cancelled");
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

        internal static List<InGameTestInfo> OrderForBatchExecution(IEnumerable<InGameTestInfo> tests)
        {
            return tests
                .OrderBy(t => t.RunLast)
                .ThenBy(t => t.RestoreBatchFlightBaselineAfterExecution)
                .ThenBy(t => t.Category, StringComparer.Ordinal)
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .ToList();
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
                batchFlightBaseline = CaptureFlightBatchBaseline();
                batchFlightBaselinePrimed = false;
                int restoreCount = ordered.Count(t => t.RestoreBatchFlightBaselineAfterExecution);
                ParsekLog.Info(Tag,
                    $"Captured batch FLIGHT baseline slot '{batchFlightBaseline.SlotName}' for {restoreCount} restore-after-run test(s)");
                return ordered;
            }
            catch (InGameTestSkippedException skipEx)
            {
                return SkipBatchFlightRestoreTests(ordered, skipEx.Message);
            }
            catch (InGameTestFailedException failEx)
            {
                return SkipBatchFlightRestoreTests(ordered, failEx.Message);
            }
            catch (Exception ex)
            {
                return SkipBatchFlightRestoreTests(ordered,
                    $"Automatic FLIGHT batch restore unavailable: {ex.Message}");
            }
        }

        private static string GetBatchFlightBaselineUnavailableReason()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
                return "Automatic FLIGHT batch restore requires running from the FLIGHT scene.";
            if (HighLogic.CurrentGame == null)
                return "Automatic FLIGHT batch restore requires HighLogic.CurrentGame.";
            if (string.IsNullOrEmpty(HighLogic.SaveFolder))
                return "Automatic FLIGHT batch restore requires HighLogic.SaveFolder.";
            if (FlightGlobals.ActiveVessel == null)
                return "Automatic FLIGHT batch restore requires an active vessel.";

            return null;
        }

        private static FlightBatchBaselineState CaptureFlightBatchBaseline()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            InGameAssert.IsNotNull(vessel,
                "Automatic FLIGHT batch restore requires a live active vessel to capture baseline.");

            string slotName = CreateBatchFlightBaselineSlotName();
            string parsekSnapshotDirectory = null;

            try
            {
                Helpers.QuickloadResumeHelpers.TriggerQuicksave(slotName);
                parsekSnapshotDirectory = CaptureBatchFlightParsekSaveSnapshot(slotName);

                return new FlightBatchBaselineState
                {
                    SlotName = slotName,
                    ParsekSaveSnapshotDirectory = parsekSnapshotDirectory,
                    CapturedFlightInstanceId = ParsekFlight.Instance != null
                        ? ParsekFlight.Instance.GetInstanceID()
                        : 0,
                    ActiveVesselId = vessel.id,
                    ActiveVesselName = vessel.vesselName,
                    ActiveVesselSituation = vessel.situation
                };
            }
            catch
            {
                Helpers.QuickloadResumeHelpers.TryDeleteSaveSlot(slotName);
                if (!string.IsNullOrEmpty(parsekSnapshotDirectory))
                    TryDeleteDirectoryRecursive(parsekSnapshotDirectory);
                throw;
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
            ParsekLog.Info(Tag, $"Starting test run: {tests.Count} tests");
            int sceneEligibilitySkipped = 0;
            var sceneEligibilitySkipsByRequiredScene = new Dictionary<GameScenes, int>();

            foreach (var test in tests)
            {
                if (test.RestoreBatchFlightBaselineAfterExecution
                    && batchFlightBaseline != null
                    && !batchFlightBaselinePrimed)
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

                activeTestCoroutine = coroutineHost.StartCoroutine(RunOneTest(test));
                yield return activeTestCoroutine;
                activeTestCoroutine = null;
                if (test.RestoreBatchFlightBaselineAfterExecution
                    && test.Status != TestStatus.Skipped)
                {
                    yield return coroutineHost.StartCoroutine(
                        RestoreBatchFlightBaselineAfterExecution(test));
                }
                RecountResults();
                if (abortBatchAfterRestoreFailure)
                    break;
            }

            isRunning = false;
            CleanupBatchFlightBaselineSave();
            batchFlightBaseline = null;
            batchFlightBaselinePrimed = false;
            abortBatchAfterRestoreFailure = false;
            preserveBatchFlightBaselineArtifacts = false;
            preservedBatchFlightBaselineReason = null;
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
            ExportResultsFile(auto: true);
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
                RestoreBatchFlightBaselineCore(
                    batchFlightBaseline,
                    previousFlightInstanceId,
                    "post-batch-restore:" + test.Name),
                ex => restoreFailure = ex));

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
                RestoreBatchFlightBaselineCore(
                    batchFlightBaseline,
                    previousFlightInstanceId,
                    "pre-batch-restore:" + test.Name),
                ex => restoreFailure = ex));

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
            }
        }

        private IEnumerator RestoreBatchBaselineAfterCancel(
            FlightBatchBaselineState baseline, int previousFlightInstanceId)
        {
            Exception restoreFailure = null;
            yield return coroutineHost.StartCoroutine(
                RunCoroutineSafely(
                    RestoreBatchFlightBaselineCore(
                        baseline,
                        previousFlightInstanceId,
                        "cancelled-batch-restore"),
                    ex => restoreFailure = ex));
            if (restoreFailure != null)
            {
                ParsekLog.Warn(Tag,
                    $"Cancelled run failed to restore isolated batch baseline: {restoreFailure.Message}");
                preserveBatchFlightBaselineArtifacts = true;
                preservedBatchFlightBaselineReason =
                    "cancelled batch restore failed: " + restoreFailure.Message;
            }

            CleanupBatchFlightBaselineSave();
            batchFlightBaseline = null;
            batchFlightBaselinePrimed = false;
            abortBatchAfterRestoreFailure = false;
            preserveBatchFlightBaselineArtifacts = false;
            preservedBatchFlightBaselineReason = null;
            activeCoroutine = null;
            activeTestCoroutine = null;
            activeInnerCoroutine = null;
            isRunning = false;
        }

        private IEnumerator RestoreBatchFlightBaselineCore(
            FlightBatchBaselineState baseline, int previousFlightInstanceId, string cleanupReason)
        {
            using (var stagedSnapshot = CreateBatchFlightParsekSaveSnapshotStaging(baseline))
            {
                var currentScenario = UnityEngine.Object.FindObjectOfType(typeof(ParsekScenario))
                    as ParsekScenario;
                ActivateStagedBatchFlightBaselineRestore(
                    stagedSnapshot.Activate,
                    () => ParsekScenario.PrepareForIsolatedBatchFlightBaselineRestore(
                        currentScenario != null
                            ? (Action)currentScenario.UnsubscribeStateRecorderForIsolatedBatchFlightBaselineRestore
                            : null));
            }
            Helpers.QuickloadResumeHelpers.TriggerQuickload(baseline.SlotName);
            yield return Helpers.QuickloadResumeHelpers.WaitForFlightReady(
                previousFlightInstanceId, timeoutSeconds: 15f);
            yield return WaitForBatchBaselineVessel(baseline, timeoutSeconds: 10f);
            PerformBetweenRunCleanup(cleanupReason);
            if (ShouldWaitForStockStageManager(baseline.ActiveVesselSituation))
                yield return WaitForStockStageManagerReady(timeoutSeconds: 10f);
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
                    $"Preserving isolated batch baseline artifacts for recovery: slot='{batchFlightBaseline.SlotName}', snapshot='{batchFlightBaseline.ParsekSaveSnapshotDirectory}', reason='{preservedBatchFlightBaselineReason ?? "restore failure"}'");
                return;
            }

            Helpers.QuickloadResumeHelpers.TryDeleteSaveSlot(batchFlightBaseline.SlotName);
            TryDeleteDirectoryRecursive(batchFlightBaseline.ParsekSaveSnapshotDirectory);
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
        /// Public entry point (manual Export button).
        /// </summary>
        internal void ExportResultsFile()
        {
            ExportResultsFile(auto: false);
        }

        private void ExportResultsFile(bool auto)
        {
            try
            {
                string kspRoot = KSPUtil.ApplicationRootPath ?? "";
                string path = Path.Combine(kspRoot, ResultsFileName);
                var lines = FormatResultsReport(allTests, DateTime.Now,
                    exportScene: HighLogic.LoadedScene);
                File.WriteAllText(path, string.Join("\n", lines) + "\n");
                ParsekLog.Info(Tag,
                    $"Test results written to {path} ({(auto ? "auto after run" : "manual export")})");
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
