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
            Assert.Contains(lines, l => l.Contains("(none — run a batch first)"));
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
    }
}
