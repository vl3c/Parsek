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
        public TestStatus Status = TestStatus.NotRun;
        public string ErrorMessage;
        public float DurationMs;
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

            ExportResultsFile();
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
            ParsekLog.Warn(Tag, $"FAILED: {test.Name} - {message}");
        }

        private void RecordSkip(InGameTestInfo test, Stopwatch sw, string reason)
        {
            sw.Stop();
            test.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
            test.Status = TestStatus.Skipped;
            test.ErrorMessage = reason;
            ParsekLog.Verbose(Tag, $"SKIPPED: {test.Name} - {reason}");
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

        internal void ExportResultsFile()
        {
            var ic = CultureInfo.InvariantCulture;
            try
            {
                string kspRoot = KSPUtil.ApplicationRootPath ?? "";
                string path = Path.Combine(kspRoot, ResultsFileName);

                var lines = new List<string>();
                lines.Add($"Parsek In-Game Test Results");
                lines.Add($"Run at: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", ic)}");
                lines.Add($"Scene: {HighLogic.LoadedScene}");
                lines.Add($"Total: {allTests.Count}  Passed: {Passed}  Failed: {Failed}  Skipped: {Skipped}");
                lines.Add(new string('-', 80));

                // Failed tests first (most actionable)
                var failed = allTests.Where(t => t.Status == TestStatus.Failed).ToList();
                if (failed.Count > 0)
                {
                    lines.Add("");
                    lines.Add("FAILURES:");
                    foreach (var t in failed)
                    {
                        lines.Add($"  FAIL  {t.Name} ({t.DurationMs.ToString("F1", ic)}ms)");
                        lines.Add($"        {t.ErrorMessage}");
                    }
                }

                // Then all results
                lines.Add("");
                lines.Add("ALL RESULTS:");
                string currentCategory = null;
                foreach (var t in allTests)
                {
                    if (t.Category != currentCategory)
                    {
                        currentCategory = t.Category;
                        lines.Add($"  [{currentCategory}]");
                    }
                    string status = t.Status.ToString().ToUpperInvariant().PadRight(7);
                    string duration = t.Status == TestStatus.NotRun
                        ? ""
                        : $" ({t.DurationMs.ToString("F1", ic)}ms)";
                    lines.Add($"    {status} {t.Name}{duration}");
                    if ((t.Status == TestStatus.Failed || t.Status == TestStatus.Skipped)
                        && !string.IsNullOrEmpty(t.ErrorMessage))
                        lines.Add($"            {t.ErrorMessage}");
                }

                File.WriteAllText(path, string.Join("\n", lines) + "\n");
                ParsekLog.Info(Tag, $"Test results written to {path}");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"Failed to write test results file: {ex.Message}");
            }
        }
    }
}
