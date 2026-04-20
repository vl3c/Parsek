using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    public class InGameTestRunnerTests
    {
        [Fact]
        public void OrderForBatchExecution_PlacesRunLastTestsAfterNormalTests()
        {
            var ordered = InGameTestRunner.OrderForBatchExecution(new[]
            {
                new InGameTestInfo { Category = "B", Name = "NormalB" },
                new InGameTestInfo { Category = "A", Name = "RunLastA", RunLast = true },
                new InGameTestInfo { Category = "A", Name = "NormalA" },
                new InGameTestInfo { Category = "A", Name = "RunLastB", RunLast = true },
            });

            Assert.Equal(
                new[] { "NormalA", "NormalB", "RunLastA", "RunLastB" },
                ordered.Select(t => t.Name).ToArray());
        }

        [Fact]
        public void OrderForBatchExecution_KeepsAlphabeticalOrderWithinEachPriorityBucket()
        {
            var ordered = InGameTestRunner.OrderForBatchExecution(new[]
            {
                new InGameTestInfo { Category = "B", Name = "Beta" },
                new InGameTestInfo { Category = "A", Name = "Alpha" },
                new InGameTestInfo { Category = "B", Name = "Gamma", RunLast = true },
                new InGameTestInfo { Category = "A", Name = "Delta", RunLast = true },
            });

            Assert.Equal(
                new[] { "Alpha", "Beta", "Delta", "Gamma" },
                ordered.Select(t => t.Name).ToArray());
        }

        [Fact]
        public void PrepareBatchExecution_SkipsSingleRunOnlyTestsWithExplicitReason()
        {
            var batchSafe = new InGameTestInfo { Category = "A", Name = "BatchSafe" };
            var singleOnly = new InGameTestInfo
            {
                Category = "A",
                Name = "SceneTransition",
                AllowBatchExecution = false,
                BatchSkipReason = "single-run only"
            };

            var batch = InGameTestRunner.PrepareBatchExecution(new[] { singleOnly, batchSafe });

            Assert.Equal(new[] { "BatchSafe" }, batch.Select(t => t.Name).ToArray());
            Assert.Equal(TestStatus.Skipped, singleOnly.Status);
            Assert.Equal("single-run only", singleOnly.ErrorMessage);
            Assert.Equal(0f, singleOnly.DurationMs);
        }

        [Fact]
        public void PrepareBatchExecution_UsesDefaultReasonWhenSingleRunOnlyTestHasNoCustomReason()
        {
            var singleOnly = new InGameTestInfo
            {
                Category = "A",
                Name = "SceneTransition",
                AllowBatchExecution = false
            };

            var batch = InGameTestRunner.PrepareBatchExecution(new[] { singleOnly });

            Assert.Empty(batch);
            Assert.Equal(TestStatus.Skipped, singleOnly.Status);
            Assert.Equal(InGameTestRunner.DefaultBatchSkipReason, singleOnly.ErrorMessage);
        }

        [Fact]
        public void OrderForBatchExecution_PlacesRestoreTestsAfterSharedSessionTests()
        {
            var ordered = InGameTestRunner.OrderForBatchExecution(new[]
            {
                new InGameTestInfo { Category = "A", Name = "RestoreA", RestoreBatchFlightBaselineAfterExecution = true },
                new InGameTestInfo { Category = "A", Name = "SharedA" },
                new InGameTestInfo { Category = "A", Name = "RestoreB", RestoreBatchFlightBaselineAfterExecution = true },
            });

            Assert.Equal(
                new[] { "SharedA", "RestoreA", "RestoreB" },
                ordered.Select(t => t.Name).ToArray());
        }

        [Fact]
        public void PrepareBatchExecutionIncludingFlightRestore_IncludesRestoreTestsButSkipsManualOnly()
        {
            var batchSafe = new InGameTestInfo { Category = "A", Name = "BatchSafe" };
            var isolated = new InGameTestInfo
            {
                Category = "A",
                Name = "Isolated",
                AllowBatchExecution = false,
                RestoreBatchFlightBaselineAfterExecution = true,
                BatchSkipReason = "run with restore"
            };
            var manualOnly = new InGameTestInfo
            {
                Category = "A",
                Name = "ManualOnly",
                AllowBatchExecution = false,
                BatchSkipReason = "manual only"
            };

            var batch = InGameTestRunner.PrepareBatchExecutionIncludingFlightRestore(
                new[] { isolated, batchSafe, manualOnly });

            Assert.Equal(new[] { "BatchSafe", "Isolated" }, batch.Select(t => t.Name).ToArray());
            Assert.Equal(TestStatus.Skipped, manualOnly.Status);
            Assert.Equal("manual only", manualOnly.ErrorMessage);
            Assert.Equal(TestStatus.NotRun, isolated.Status);
        }

        [Fact]
        public void GetBatchExecutionNote_ReturnsRestoreNoteForIsolatedTests()
        {
            var isolated = new InGameTestInfo
            {
                RestoreBatchFlightBaselineAfterExecution = true
            };

            string note = InGameTestRunner.GetBatchExecutionNote(isolated);

            Assert.Equal(InGameTestRunner.DefaultBatchRestoreNote, note);
        }

        // ---- FormatResultsReport: multi-scene accumulation (per-scene history) ----

        private static InGameTestInfo MakeTest(string category, string name,
            GameScenes requiredScene = InGameTestAttribute.AnyScene)
        {
            return new InGameTestInfo
            {
                Category = category,
                Name = name,
                RequiredScene = requiredScene,
            };
        }

        private static void StampResult(InGameTestInfo t, GameScenes scene,
            TestStatus status, string err = null, float durationMs = 1.0f)
        {
            t.ResultsByScene[scene] = new SceneResult
            {
                Status = status,
                ErrorMessage = err,
                DurationMs = durationMs,
                TimestampUtc = DateTime.UtcNow,
            };
        }

        [Fact]
        public void FormatResultsReport_EmptyTests_ProducesHeaderWithNoScenesLine()
        {
            var lines = InGameTestRunner.FormatResultsReport(
                new List<InGameTestInfo>(),
                new DateTime(2026, 4, 17, 10, 0, 0),
                GameScenes.SPACECENTER);

            Assert.Contains("Parsek In-Game Test Results", string.Join("\n", lines));
            Assert.Contains(lines, l => l.Contains("(none - run a batch first)"));
        }

        [Fact]
        public void FormatResultsReport_SingleSceneRun_HasPerSceneHeader()
        {
            var t = MakeTest("Cat", "Alpha");
            StampResult(t, GameScenes.SPACECENTER, TestStatus.Passed);

            var lines = InGameTestRunner.FormatResultsReport(
                new[] { t },
                new DateTime(2026, 4, 17, 10, 0, 0),
                GameScenes.SPACECENTER);

            string joined = string.Join("\n", lines);
            Assert.Contains("Scenes with captured results: 1", joined);
            Assert.Contains("SPACECENTER", joined);
            Assert.Contains("Passed=1", joined);
            Assert.Contains("Failed=0", joined);
        }

        [Fact]
        public void FormatResultsReport_TwoScenesSameTest_PreservesBothResults()
        {
            // The regression gate: a test that Passed in KSC and Failed in Flight
            // must show BOTH outcomes in the export (this was the pre-fix behavior
            // where the later run overwrote the earlier one).
            var t = MakeTest("Cat", "AnySceneTest");
            StampResult(t, GameScenes.SPACECENTER, TestStatus.Passed, durationMs: 5.2f);
            StampResult(t, GameScenes.FLIGHT, TestStatus.Failed,
                err: "Expected 42, got 41", durationMs: 125.3f);

            var lines = InGameTestRunner.FormatResultsReport(
                new[] { t },
                new DateTime(2026, 4, 17, 10, 0, 0),
                GameScenes.FLIGHT);
            string joined = string.Join("\n", lines);

            Assert.Contains("SPACECENTER", joined);
            Assert.Contains("FLIGHT", joined);
            Assert.Contains("PASS", joined);
            Assert.Contains("FAIL", joined);
            Assert.Contains("Expected 42, got 41", joined);
            Assert.Contains("5.2ms", joined);
            Assert.Contains("125.3ms", joined);
        }

        [Fact]
        public void FormatResultsReport_FailuresGroupedByScene()
        {
            var a = MakeTest("Cat", "A");
            var b = MakeTest("Cat", "B");
            StampResult(a, GameScenes.SPACECENTER, TestStatus.Failed, err: "ksc-only fail");
            StampResult(b, GameScenes.FLIGHT, TestStatus.Failed, err: "flight-only fail");
            StampResult(b, GameScenes.SPACECENTER, TestStatus.Passed);

            var lines = InGameTestRunner.FormatResultsReport(
                new[] { a, b },
                new DateTime(2026, 4, 17, 10, 0, 0),
                GameScenes.FLIGHT);
            string joined = string.Join("\n", lines);

            // The FAILURES header and both scene-labeled subheadings appear.
            Assert.Contains("FAILURES (grouped by scene):", joined);
            Assert.Contains("[SPACECENTER]", joined);
            Assert.Contains("[FLIGHT]", joined);
            Assert.Contains("ksc-only fail", joined);
            Assert.Contains("flight-only fail", joined);
        }

        [Fact]
        public void FormatResultsReport_NeverRunTest_AppearsAsNeverRun()
        {
            var t = MakeTest("Cat", "Untouched");

            var lines = InGameTestRunner.FormatResultsReport(
                new[] { t },
                new DateTime(2026, 4, 17, 10, 0, 0),
                GameScenes.FLIGHT);
            string joined = string.Join("\n", lines);

            Assert.Contains("Untouched", joined);
            Assert.Contains("(never run)", joined);
        }

        [Fact]
        public void FormatResultsReport_AnySceneTest_ListsMissingScenesExplicitly()
        {
            // AnyScene test that only ran in one of two captured scenes — the
            // export should honestly report "not run in FLIGHT" rather than
            // silently hide the gap.
            var any = MakeTest("Cat", "Any", InGameTestAttribute.AnyScene);
            var flightOnly = MakeTest("Cat", "FlightOnly", GameScenes.FLIGHT);

            StampResult(any, GameScenes.SPACECENTER, TestStatus.Passed);
            StampResult(flightOnly, GameScenes.FLIGHT, TestStatus.Passed);

            var lines = InGameTestRunner.FormatResultsReport(
                new[] { any, flightOnly },
                new DateTime(2026, 4, 17, 10, 0, 0),
                GameScenes.FLIGHT);
            string joined = string.Join("\n", lines);

            // The AnyScene test's gap is listed; the scene-tied test's non-KSC
            // scene is not spammed with "not run" rows.
            Assert.Contains("(not run in this scene)", joined);
            int notRunRows = joined.Split(new[] { "(not run in this scene)" },
                StringSplitOptions.None).Length - 1;
            Assert.Equal(1, notRunRows);
        }

        [Fact]
        public void FormatResultsReport_RerunInSameScene_OverwritesThatSceneSlot()
        {
            // Running the same test twice in the same scene should produce one
            // row for that scene with the latest outcome — the per-scene slot
            // is keyed on scene, so a second stamp overwrites the first. This
            // matches the user intent of "one result per scene, latest wins".
            var t = MakeTest("Cat", "Alpha");
            StampResult(t, GameScenes.SPACECENTER, TestStatus.Failed, err: "first run");
            StampResult(t, GameScenes.SPACECENTER, TestStatus.Passed);

            var lines = InGameTestRunner.FormatResultsReport(
                new[] { t },
                new DateTime(2026, 4, 17, 10, 0, 0),
                GameScenes.SPACECENTER);
            string joined = string.Join("\n", lines);

            Assert.Contains("PASS", joined);
            Assert.DoesNotContain("first run", joined);
            Assert.DoesNotContain("FAIL", joined);
        }

        // ---- Reset semantics (PR #328 P2-B) ----
        // The two Reset controls now have distinct meaning:
        // - Implicit pre-run ResetResults / ResetCategory: clears live Status fields
        //   so the table shows progression, but preserves per-scene history so the
        //   auto-export keeps accumulating KSC→Flight outcomes.
        // - Explicit ClearAllSceneHistory (wired to the "Reset" button): full wipe,
        //   including per-scene history. Next auto-export produces a clean report.
        //
        // Unit-testing the live runner directly requires a MonoBehaviour host, but
        // we can pin the state-machine contract on an InGameTestInfo directly.

        [Fact]
        public void InGameTestInfo_PerSceneHistory_SurvivesTopLevelStatusReset()
        {
            // Simulate the implicit pre-run reset: caller clears top-level Status
            // but should NOT touch ResultsByScene — that's what keeps the
            // cross-scene accumulation working.
            var t = MakeTest("Cat", "Alpha");
            StampResult(t, GameScenes.SPACECENTER, TestStatus.Passed, durationMs: 5.2f);
            t.Status = TestStatus.Passed;
            t.DurationMs = 5.2f;

            // Mimic ResetResults' effect on this one test.
            t.Status = TestStatus.NotRun;
            t.ErrorMessage = null;
            t.DurationMs = 0;

            Assert.Single(t.ResultsByScene);
            Assert.True(t.ResultsByScene.ContainsKey(GameScenes.SPACECENTER));
            Assert.Equal(TestStatus.Passed, t.ResultsByScene[GameScenes.SPACECENTER].Status);
        }

        [Fact]
        public void InGameTestInfo_PerSceneHistory_ClearedByFullWipe()
        {
            // Simulate ClearAllSceneHistory: both top-level AND the scene dict get
            // cleared. This is what the explicit Reset button triggers so the next
            // auto-export produces a clean file.
            var t = MakeTest("Cat", "Alpha");
            StampResult(t, GameScenes.SPACECENTER, TestStatus.Failed, err: "old");
            StampResult(t, GameScenes.FLIGHT, TestStatus.Passed);

            t.Status = TestStatus.NotRun;
            t.ErrorMessage = null;
            t.DurationMs = 0;
            t.ResultsByScene.Clear();

            Assert.Empty(t.ResultsByScene);
            Assert.Equal(TestStatus.NotRun, t.Status);

            // And the formatter treats the now-empty test as "never run".
            var lines = InGameTestRunner.FormatResultsReport(
                new[] { t },
                new DateTime(2026, 4, 17, 10, 0, 0),
                GameScenes.FLIGHT);
            Assert.Contains("(never run)", string.Join("\n", lines));
        }
    }
}
