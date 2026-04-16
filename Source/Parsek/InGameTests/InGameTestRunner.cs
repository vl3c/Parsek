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

        private List<InGameTestInfo> allTests;
        private readonly List<GameObject> cleanupRegistry = new List<GameObject>();
        private MonoBehaviour coroutineHost;
        private bool isRunning;
        private Coroutine activeCoroutine;
        private Coroutine activeInnerCoroutine;

        // Results summary
        public int Passed { get; private set; }
        public int Failed { get; private set; }
        public int Skipped { get; private set; }
        public bool IsRunning => isRunning;

        public IReadOnlyList<InGameTestInfo> Tests => allTests;

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

        public void RunCategory(string category)
        {
            if (isRunning) return;
            PerformBetweenRunCleanup("run-category:" + (category ?? "(null)"));
            var eligible = PrepareBatchExecution(allTests
                .Where(t => t.Category == category && IsEligibleForScene(t)));
            RecountResults();
            activeCoroutine = coroutineHost.StartCoroutine(RunBatch(eligible));
        }

        public void RunSingle(InGameTestInfo test)
        {
            if (isRunning) return;
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
        /// 1. Delegate to ParsekFlight.Instance.DestroyAllTimelineGhosts() when available
        ///    — this does RemoveAllGhostVessels (vessel.Die + dict clear) followed by
        ///    engine.DestroyAllGhosts (primary + overlap GO destroy + engine dict clear).
        /// 2. Safety net: ResetBetweenTestRuns on GhostMapPresence, idempotent and
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
            if (activeCoroutine != null)
                coroutineHost.StopCoroutine(activeCoroutine);
            isRunning = false;
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

        internal static List<InGameTestInfo> OrderForBatchExecution(IEnumerable<InGameTestInfo> tests)
        {
            return tests
                .OrderBy(t => t.RunLast)
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

        internal static string GetBatchSkipReason(InGameTestInfo test)
        {
            if (test == null || test.AllowBatchExecution)
                return null;

            return string.IsNullOrEmpty(test.BatchSkipReason)
                ? DefaultBatchSkipReason
                : test.BatchSkipReason;
        }

        private IEnumerator RunBatch(List<InGameTestInfo> tests)
        {
            isRunning = true;
            ParsekLog.Info(Tag, $"Starting test run: {tests.Count} tests");

            foreach (var test in tests)
            {
                if (!IsEligibleForScene(test))
                {
                    test.Status = TestStatus.Skipped;
                    test.ErrorMessage = $"Requires {test.RequiredScene} scene";
                    RecountResults();
                    continue;
                }

                yield return coroutineHost.StartCoroutine(RunOneTest(test));
                RecountResults();
            }

            isRunning = false;
            int considered = allTests.Count(t => t.Status != TestStatus.NotRun);
            ParsekLog.Info(Tag,
                $"Test run complete: {Passed} passed, {Failed} failed, {Skipped} skipped (of {considered})");

            // Auto-export on every batch completion so the external report file
            // always reflects the latest state. Multi-scene history lives in
            // InGameTestInfo.ResultsByScene, which ResetResults preserves, so
            // the file accumulates across KSC / Flight / Tracking Station runs.
            ExportResultsFile(auto: true);
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
                        while (true)
                        {
                            bool hasNext;
                            try
                            {
                                hasNext = enumerator.MoveNext();
                            }
                            catch (Exception ex)
                            {
                                coroutineError = ex;
                                coroutineRunning = false;
                                yield break;
                            }
                            if (!hasNext)
                            {
                                coroutineRunning = false;
                                yield break;
                            }
                            yield return enumerator.Current;
                        }
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
            if (test == null) return;
            var scene = HighLogic.LoadedScene;
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
                lines.Add("Scenes with captured results: (none — run a batch first)");
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
