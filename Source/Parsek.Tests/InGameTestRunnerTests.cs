using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
        public void FilterSceneEligibleBatchCandidates_MarksRejectedTestsBeforeBatch()
        {
            var anyScene = new InGameTestInfo
            {
                Category = "A",
                Name = "Any",
                RequiredScene = InGameTestAttribute.AnyScene
            };
            var ksc = new InGameTestInfo
            {
                Category = "A",
                Name = "KSC",
                RequiredScene = GameScenes.SPACECENTER
            };
            var flight = new InGameTestInfo
            {
                Category = "A",
                Name = "Flight",
                RequiredScene = GameScenes.FLIGHT
            };

            int skipped;
            Dictionary<GameScenes, int> skippedByRequiredScene;
            var eligible = InGameTestRunner.FilterSceneEligibleBatchCandidates(
                new[] { anyScene, ksc, flight },
                GameScenes.FLIGHT,
                out skipped,
                out skippedByRequiredScene);

            Assert.Equal(new[] { "Any", "Flight" }, eligible.Select(t => t.Name).ToArray());
            Assert.Equal(1, skipped);
            Assert.Equal(1, skippedByRequiredScene[GameScenes.SPACECENTER]);
            Assert.Equal(TestStatus.Skipped, ksc.Status);
            Assert.Equal("Requires SPACECENTER scene", ksc.ErrorMessage);
            Assert.Equal(TestStatus.NotRun, anyScene.Status);
            Assert.Equal(TestStatus.NotRun, flight.Status);
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

        [Theory]
        [InlineData(Vessel.Situations.PRELAUNCH, true)]
        [InlineData(Vessel.Situations.LANDED, false)]
        [InlineData(Vessel.Situations.FLYING, false)]
        public void ShouldWaitForStockStageManager_OnlyForPrelaunch(
            Vessel.Situations situation,
            bool expected)
        {
            bool actual = InGameTestRunner.ShouldWaitForStockStageManager(situation);

            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(true, 1, false, false, true)]
        [InlineData(false, 1, false, false, false)]
        [InlineData(true, 0, false, false, false)]
        [InlineData(true, 1, true, false, false)]
        [InlineData(true, 1, false, true, false)]
        public void IsStageManagerReadyForActivateNextStage_RequiresStableStockState(
            bool hasInstance,
            int stageCount,
            bool rebuildIndexes,
            bool hasSortRoutine,
            bool expected)
        {
            bool actual = InGameTestRunner.IsStageManagerReadyForActivateNextStage(
                hasInstance,
                stageCount,
                rebuildIndexes,
                hasSortRoutine);

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void CreateBatchFlightParsekSaveSnapshotStaging_MissingSnapshot_DoesNotTouchCurrentDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "parsek-staging-test-" + Guid.NewGuid().ToString("N"));
            string currentDirectory = Path.Combine(root, "Parsek");
            string currentFile = Path.Combine(currentDirectory, "state.txt");
            try
            {
                Directory.CreateDirectory(currentDirectory);
                File.WriteAllText(currentFile, "live");

                Assert.ThrowsAny<Exception>(() =>
                    InGameTestRunner.CreateBatchFlightParsekSaveSnapshotStaging(
                        currentDirectory,
                        Path.Combine(root, "missing-snapshot")));

                Assert.True(Directory.Exists(currentDirectory));
                Assert.Equal("live", File.ReadAllText(currentFile));
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void CreateBatchFlightParsekSaveSnapshotStaging_Activate_ReplacesCurrentDirectoryOnlyWhenInvoked()
        {
            string root = Path.Combine(Path.GetTempPath(), "parsek-staging-test-" + Guid.NewGuid().ToString("N"));
            string currentDirectory = Path.Combine(root, "Parsek");
            string snapshotDirectory = Path.Combine(root, "ParsekSnapshot");
            string currentFile = Path.Combine(currentDirectory, "state.txt");
            string snapshotFile = Path.Combine(snapshotDirectory, "state.txt");
            try
            {
                Directory.CreateDirectory(currentDirectory);
                Directory.CreateDirectory(snapshotDirectory);
                File.WriteAllText(currentFile, "live");
                File.WriteAllText(snapshotFile, "baseline");

                using (var staged = InGameTestRunner.CreateBatchFlightParsekSaveSnapshotStaging(
                    currentDirectory,
                    snapshotDirectory))
                {
                    Assert.Equal("live", File.ReadAllText(currentFile));

                    staged.Activate();

                    Assert.True(Directory.Exists(currentDirectory));
                    Assert.Equal("baseline", File.ReadAllText(currentFile));
                }
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void ActivateStagedBatchFlightBaselineRestore_PreparesOnlyAfterActivation()
        {
            var order = new List<string>();

            InGameTestRunner.ActivateStagedBatchFlightBaselineRestore(
                () => order.Add("activate"),
                () => order.Add("prepare"));

            Assert.Equal(new[] { "activate", "prepare" }, order);
        }

        [Fact]
        public void ActivateStagedBatchFlightBaselineRestore_ActivationFailure_DoesNotPrepare()
        {
            bool prepared = false;

            Assert.Throws<InvalidOperationException>(() =>
                InGameTestRunner.ActivateStagedBatchFlightBaselineRestore(
                    () => throw new InvalidOperationException("boom"),
                    () => prepared = true));

            Assert.False(prepared);
        }

        [Fact]
        public void FormatCoroutineState_ReportsActiveAndIdleSlots()
        {
            string state = InGameTestRunner.FormatCoroutineState(
                isRunning: true,
                hasBatchCoroutine: true,
                hasInnerCoroutine: false);

            Assert.Equal("isRunning=True batch=active inner=null", state);
        }

        [Fact]
        public void FormatCoroutineState_ReportsIdleRunnerWhenNothingIsRunning()
        {
            string state = InGameTestRunner.FormatCoroutineState(
                isRunning: false,
                hasBatchCoroutine: false,
                hasInnerCoroutine: false);

            Assert.Equal("isRunning=False batch=null inner=null", state);
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

        [Fact]
        public void RunCoroutineSafely_CapturesNestedCoroutineFailure()
        {
            Exception failure = null;

            var safe = InGameTestRunner.RunCoroutineSafely(
                OuterCoroutineYieldingNestedFailure(),
                ex => failure = ex);

            ExhaustCoroutine(safe);

            var actual = Assert.IsType<InvalidOperationException>(failure);
            Assert.Equal("nested boom", actual.Message);
        }

        private static IEnumerator OuterCoroutineYieldingNestedFailure()
        {
            yield return NestedThrowingCoroutine();
        }

        private static IEnumerator NestedThrowingCoroutine()
        {
            yield return null;
            throw new InvalidOperationException("nested boom");
        }

        private static void ExhaustCoroutine(IEnumerator routine)
        {
            while (routine.MoveNext())
            {
                if (routine.Current is IEnumerator nested)
                    ExhaustCoroutine(nested);
            }
        }

        // Round-7 review regression: tests that self-skip via
        // InGameAssert.Skip from inside the test body (after starting a
        // recording, staging the vessel, or otherwise mutating the live
        // FLIGHT session) used to bypass the post-test batch FLIGHT
        // baseline restore because the runner's predicate gated on
        // `Status != Skipped`. Real example: ControlledChildBreakupSeed_LogsLiveResidualDecision
        // calls flight.StartRecording() and StageManager.ActivateNextStage()
        // before deciding via InGameAssert.Skip if no controlled-child
        // decision log appeared. With the gate, Run All + Isolated would
        // carry the staged vessel + active recording into the next test.
        //
        // Round-7 fix: post-test restore predicate ignores test status.
        // Restore runs after any restore-backed test that reached
        // RunOneTest (passed, failed, or self-skipped). Scene-eligibility
        // skips are filtered by RunBatch's `continue;` before RunOneTest
        // runs, so they never reach this predicate.

        [Fact]
        public void ShouldRestoreBatchFlightBaselineAfterTest_RestoreBackedPassedTest_ReturnsTrue()
        {
            var test = new InGameTestInfo
            {
                Name = "PassedTest",
                RestoreBatchFlightBaselineAfterExecution = true,
                Status = TestStatus.Passed,
            };

            Assert.True(InGameTestRunner.ShouldRestoreBatchFlightBaselineAfterTest(test));
        }

        [Fact]
        public void ShouldRestoreBatchFlightBaselineAfterTest_RestoreBackedFailedTest_ReturnsTrue()
        {
            var test = new InGameTestInfo
            {
                Name = "FailedTest",
                RestoreBatchFlightBaselineAfterExecution = true,
                Status = TestStatus.Failed,
            };

            Assert.True(InGameTestRunner.ShouldRestoreBatchFlightBaselineAfterTest(test));
        }

        [Fact]
        public void ShouldRestoreBatchFlightBaselineAfterTest_RestoreBackedSelfSkippedTest_ReturnsTrue()
        {
            // The contract pin: a restore-backed test that called
            // InGameAssert.Skip from inside its body (e.g.
            // ControlledChildBreakupSeed_LogsLiveResidualDecision after
            // StartRecording + ActivateNextStage) MUST trigger the post-test
            // restore. Pre-fix the runner gated on `Status != Skipped`
            // and skipped the restore here, leaving the staged vessel
            // and active recording for the next test.
            var test = new InGameTestInfo
            {
                Name = "SelfSkippedAfterStaging",
                RestoreBatchFlightBaselineAfterExecution = true,
                Status = TestStatus.Skipped,
            };

            Assert.True(InGameTestRunner.ShouldRestoreBatchFlightBaselineAfterTest(test));
        }

        [Fact]
        public void ShouldRestoreBatchFlightBaselineAfterTest_NotRestoreBacked_AnyStatus_ReturnsFalse()
        {
            // Non-restore-backed tests never trigger the restore path,
            // regardless of status. (The runner only calls this predicate
            // after a real test execution; non-batch / single-run-only
            // tests are excluded earlier by PrepareBatchExecution.)
            foreach (var status in new[]
            {
                TestStatus.Passed,
                TestStatus.Failed,
                TestStatus.Skipped,
                TestStatus.Running,
                TestStatus.NotRun,
            })
            {
                var test = new InGameTestInfo
                {
                    Name = "NonBatchTest",
                    RestoreBatchFlightBaselineAfterExecution = false,
                    Status = status,
                };
                Assert.False(InGameTestRunner.ShouldRestoreBatchFlightBaselineAfterTest(test),
                    $"non-restore-backed test should never trigger restore (status={status})");
            }
        }

        [Fact]
        public void ShouldRestoreBatchFlightBaselineAfterTest_NullTest_ReturnsFalse()
        {
            Assert.False(InGameTestRunner.ShouldRestoreBatchFlightBaselineAfterTest(null));
        }

        [Fact]
        public void ShouldForceCloseSpaceCenterFacilities_OnlyTrueInSpaceCenter()
        {
            Assert.True(InGameTestRunner.ShouldForceCloseSpaceCenterFacilities(GameScenes.SPACECENTER));

            foreach (var scene in new[]
            {
                GameScenes.FLIGHT,
                GameScenes.TRACKSTATION,
                GameScenes.EDITOR,
                GameScenes.MAINMENU,
                GameScenes.SETTINGS,
                GameScenes.CREDITS,
                GameScenes.LOADING,
            })
            {
                Assert.False(InGameTestRunner.ShouldForceCloseSpaceCenterFacilities(scene),
                    $"facility force-close must be a no-op outside the Space Center (scene={scene})");
            }
        }

        [Fact]
        public void FormatFacilityForceCloseSummary_IncludesCountAndReason()
        {
            string summary = InGameTestRunner.FormatFacilityForceCloseSummary(2, "post-test:Foo");
            Assert.Contains("closed 2", summary);
            Assert.Contains("reason=post-test:Foo", summary);
        }

        [Fact]
        public void FormatFacilityForceCloseSummary_NullReason_RendersPlaceholder()
        {
            string summary = InGameTestRunner.FormatFacilityForceCloseSummary(1, null);
            Assert.Contains("reason=(null)", summary);
        }

        [Fact]
        public void IsBatchBaselineRestoreSupportedScene_AllowsFlightAndTrackingStation()
        {
            Assert.True(InGameTestRunner.IsBatchBaselineRestoreSupportedScene(GameScenes.FLIGHT));
            Assert.True(InGameTestRunner.IsBatchBaselineRestoreSupportedScene(GameScenes.TRACKSTATION));

            foreach (var scene in new[]
            {
                GameScenes.SPACECENTER,
                GameScenes.EDITOR,
                GameScenes.MAINMENU,
                GameScenes.LOADING,
            })
            {
                Assert.False(InGameTestRunner.IsBatchBaselineRestoreSupportedScene(scene),
                    $"baseline restore is only supported in FLIGHT / TRACKSTATION (scene={scene})");
            }
        }

        [Fact]
        public void BatchBaselineUnavailableReasonForScene_FlightHappyPath_ReturnsNull()
        {
            Assert.Null(InGameTestRunner.BatchBaselineUnavailableReasonForScene(
                GameScenes.FLIGHT, hasCurrentGame: true, hasSaveFolder: true, hasActiveVessel: true));
        }

        [Fact]
        public void BatchBaselineUnavailableReasonForScene_TrackingStationHappyPath_NeedsNoActiveVessel()
        {
            // A Tracking Station baseline returns via LoadScene with no vessel
            // focus, so a missing active vessel must NOT block capture.
            Assert.Null(InGameTestRunner.BatchBaselineUnavailableReasonForScene(
                GameScenes.TRACKSTATION, hasCurrentGame: true, hasSaveFolder: true, hasActiveVessel: false));
        }

        [Fact]
        public void BatchBaselineUnavailableReasonForScene_FlightWithoutActiveVessel_IsBlocked()
        {
            string reason = InGameTestRunner.BatchBaselineUnavailableReasonForScene(
                GameScenes.FLIGHT, hasCurrentGame: true, hasSaveFolder: true, hasActiveVessel: false);
            Assert.False(string.IsNullOrEmpty(reason));
            Assert.Contains("active vessel", reason);
        }

        [Fact]
        public void BatchBaselineUnavailableReasonForScene_UnsupportedScene_IsBlocked()
        {
            string reason = InGameTestRunner.BatchBaselineUnavailableReasonForScene(
                GameScenes.SPACECENTER, hasCurrentGame: true, hasSaveFolder: true, hasActiveVessel: true);
            Assert.False(string.IsNullOrEmpty(reason));
            Assert.Contains("FLIGHT or Tracking Station", reason);
        }

        [Fact]
        public void BatchBaselineUnavailableReasonForScene_MissingGameOrSaveFolder_IsBlocked()
        {
            Assert.Contains("HighLogic.CurrentGame",
                InGameTestRunner.BatchBaselineUnavailableReasonForScene(
                    GameScenes.TRACKSTATION, hasCurrentGame: false, hasSaveFolder: true, hasActiveVessel: false));
            Assert.Contains("HighLogic.SaveFolder",
                InGameTestRunner.BatchBaselineUnavailableReasonForScene(
                    GameScenes.FLIGHT, hasCurrentGame: true, hasSaveFolder: false, hasActiveVessel: true));
        }

        [Fact]
        public void IsReloadedNonFlightSceneReady_SameSceneNotYetReloaded_IsNotReady()
        {
            // The prime restore reloads TRACKSTATION while already in it: scene
            // matches but the ParsekScenario has NOT been replaced yet, so the
            // wait must not report ready (the bug this guards against).
            Assert.False(InGameTestRunner.IsReloadedNonFlightSceneReady(
                GameScenes.TRACKSTATION, GameScenes.TRACKSTATION,
                currentScenarioInstanceId: 42, previousScenarioInstanceId: 42));
        }

        [Fact]
        public void IsReloadedNonFlightSceneReady_SceneMatchesAndScenarioReplaced_IsReady()
        {
            Assert.True(InGameTestRunner.IsReloadedNonFlightSceneReady(
                GameScenes.TRACKSTATION, GameScenes.TRACKSTATION,
                currentScenarioInstanceId: 99, previousScenarioInstanceId: 42));
        }

        [Fact]
        public void IsReloadedNonFlightSceneReady_NoPriorScenario_OnlyNeedsFreshInstance()
        {
            // previous id 0 == "no prior scenario": any non-zero current scenario
            // in the target scene counts as ready.
            Assert.True(InGameTestRunner.IsReloadedNonFlightSceneReady(
                GameScenes.TRACKSTATION, GameScenes.TRACKSTATION,
                currentScenarioInstanceId: 7, previousScenarioInstanceId: 0));
        }

        [Fact]
        public void IsReloadedNonFlightSceneReady_NoCurrentScenario_IsNotReady()
        {
            // Mid-reload the scenario may be momentarily absent (id 0).
            Assert.False(InGameTestRunner.IsReloadedNonFlightSceneReady(
                GameScenes.TRACKSTATION, GameScenes.TRACKSTATION,
                currentScenarioInstanceId: 0, previousScenarioInstanceId: 42));
        }

        [Fact]
        public void IsReloadedNonFlightSceneReady_WrongScene_IsNotReady()
        {
            // Even with a fresh scenario, a non-target loaded scene is not ready
            // (e.g. still passing through LOADING / FLIGHT).
            Assert.False(InGameTestRunner.IsReloadedNonFlightSceneReady(
                GameScenes.TRACKSTATION, GameScenes.FLIGHT,
                currentScenarioInstanceId: 99, previousScenarioInstanceId: 42));
        }

        [Fact]
        public void IsExceptionStorm_BelowThreshold_IsFalse()
        {
            // A healthy batch logs ~0 unhandled exceptions.
            Assert.False(InGameTestRunner.IsExceptionStorm(
                0, InGameTestRunner.BatchExceptionStormThreshold));
            Assert.False(InGameTestRunner.IsExceptionStorm(
                InGameTestRunner.BatchExceptionStormThreshold - 1,
                InGameTestRunner.BatchExceptionStormThreshold));
        }

        [Fact]
        public void IsExceptionStorm_AtOrAboveThreshold_IsTrue()
        {
            // The flood signature: thousands of unhandled exceptions in a batch.
            Assert.True(InGameTestRunner.IsExceptionStorm(
                InGameTestRunner.BatchExceptionStormThreshold,
                InGameTestRunner.BatchExceptionStormThreshold));
            Assert.True(InGameTestRunner.IsExceptionStorm(
                InGameTestRunner.BatchExceptionStormThreshold + 500_000,
                InGameTestRunner.BatchExceptionStormThreshold));
        }

        [Fact]
        public void IsExceptionStorm_NonPositiveThreshold_DisablesGuard()
        {
            // A non-positive threshold disables the guard (never a storm), so a
            // misconfiguration cannot spuriously abort every batch.
            Assert.False(InGameTestRunner.IsExceptionStorm(1_000_000, 0));
            Assert.False(InGameTestRunner.IsExceptionStorm(1_000_000, -1));
        }

        [Fact]
        public void ReloadStillFlooding_CleanSettleWindow_IsFalse()
        {
            // A clean baseline reload adds ~0 unhandled exceptions in the settle window.
            Assert.False(InGameTestRunner.ReloadStillFlooding(
                0, InGameTestRunner.BaselineReloadFloodExceptionThreshold));
            Assert.False(InGameTestRunner.ReloadStillFlooding(
                InGameTestRunner.BaselineReloadFloodExceptionThreshold - 1,
                InGameTestRunner.BaselineReloadFloodExceptionThreshold));
        }

        [Fact]
        public void ReloadStillFlooding_FloodingSettleWindow_IsTrue()
        {
            // A reload that tripped the camera race keeps flooding hundreds of exceptions per window.
            Assert.True(InGameTestRunner.ReloadStillFlooding(
                InGameTestRunner.BaselineReloadFloodExceptionThreshold,
                InGameTestRunner.BaselineReloadFloodExceptionThreshold));
            Assert.True(InGameTestRunner.ReloadStillFlooding(
                5000, InGameTestRunner.BaselineReloadFloodExceptionThreshold));
        }

        [Fact]
        public void ReloadStillFlooding_NonPositiveThreshold_DisablesCheck()
        {
            // A non-positive threshold disables the retry check (never flooding), so a
            // misconfiguration cannot spin retries on every reload.
            Assert.False(InGameTestRunner.ReloadStillFlooding(1_000_000, 0));
            Assert.False(InGameTestRunner.ReloadStillFlooding(1_000_000, -1));
        }

        [Fact]
        public void ShouldAttemptSpaceCenterBounce_FloodingAndNotYetAttempted_IsTrue()
        {
            // 2026-07-10 soft-freeze: the batch ends with the flood still active and no
            // bounce attempted yet -> the one-shot recovery must fire.
            Assert.True(InGameTestRunner.ShouldAttemptSpaceCenterBounce(
                alreadyAttemptedThisBatch: false,
                exceptionsInSettleWindow: InGameTestRunner.BaselineReloadFloodExceptionThreshold,
                threshold: InGameTestRunner.BaselineReloadFloodExceptionThreshold));
            Assert.True(InGameTestRunner.ShouldAttemptSpaceCenterBounce(
                alreadyAttemptedThisBatch: false,
                exceptionsInSettleWindow: 250_000,
                threshold: InGameTestRunner.BaselineReloadFloodExceptionThreshold));
        }

        [Fact]
        public void ShouldAttemptSpaceCenterBounce_AlreadyAttempted_IsFalseEvenWhileFlooding()
        {
            // EXACTLY ONE bounce per batch: retry loops are the disproven model
            // (2026-07-05: the corruption is process-permanent; 4 reload retries all
            // flooded). A still-flooding game after the one bounce gets the relaunch
            // alert, never a second bounce.
            Assert.False(InGameTestRunner.ShouldAttemptSpaceCenterBounce(
                alreadyAttemptedThisBatch: true,
                exceptionsInSettleWindow: 250_000,
                threshold: InGameTestRunner.BaselineReloadFloodExceptionThreshold));
        }

        [Fact]
        public void ShouldAttemptSpaceCenterBounce_CleanSettleWindow_IsFalse()
        {
            // A healthy batch end (settle window adds ~0 exceptions) never bounces.
            Assert.False(InGameTestRunner.ShouldAttemptSpaceCenterBounce(
                alreadyAttemptedThisBatch: false,
                exceptionsInSettleWindow: 0,
                threshold: InGameTestRunner.BaselineReloadFloodExceptionThreshold));
            Assert.False(InGameTestRunner.ShouldAttemptSpaceCenterBounce(
                alreadyAttemptedThisBatch: false,
                exceptionsInSettleWindow: InGameTestRunner.BaselineReloadFloodExceptionThreshold - 1,
                threshold: InGameTestRunner.BaselineReloadFloodExceptionThreshold));
        }

        [Fact]
        public void ShouldSampleBatchEndCorruption_InMemoryBaselineOrStorm_Samples()
        {
            // An in-memory-baseline batch (captured in FLIGHT or the Tracking Station)
            // performs scene reloads (each can trip stock Bug #4803), and a detected
            // storm is the corruption signature regardless of isolation mode - both
            // must run the batch-end settle-window check.
            Assert.True(InGameTestRunner.ShouldSampleBatchEndCorruption(
                hasInMemoryBaseline: true, stormDetected: false));
            Assert.True(InGameTestRunner.ShouldSampleBatchEndCorruption(
                hasInMemoryBaseline: false, stormDetected: true));
            Assert.True(InGameTestRunner.ShouldSampleBatchEndCorruption(
                hasInMemoryBaseline: true, stormDetected: true));
        }

        [Fact]
        public void ShouldSampleBatchEndCorruption_NoReloadsAndNoStorm_Skips()
        {
            // A plain disk-only batch (SPACECENTER / EDITOR / FLIGHT-no-vessel) with no
            // storm never reloads a scene; sampling would only add settle frames and
            // risk bouncing on an unrelated mod's error flood.
            Assert.False(InGameTestRunner.ShouldSampleBatchEndCorruption(
                hasInMemoryBaseline: false, stormDetected: false));
        }

        [Fact]
        public void ShouldAttemptSpaceCenterBounce_NonPositiveThreshold_DisablesRecovery()
        {
            // Inherits ReloadStillFlooding's semantics: a non-positive threshold
            // disables the check so a misconfiguration cannot bounce every batch.
            Assert.False(InGameTestRunner.ShouldAttemptSpaceCenterBounce(
                alreadyAttemptedThisBatch: false, exceptionsInSettleWindow: 1_000_000, threshold: 0));
            Assert.False(InGameTestRunner.ShouldAttemptSpaceCenterBounce(
                alreadyAttemptedThisBatch: false, exceptionsInSettleWindow: 1_000_000, threshold: -1));
        }

        [Fact]
        public void DescribeRestoreFailure_NullException_ReturnsPlaceholder()
        {
            Assert.Equal("(null exception)", InGameTestRunner.DescribeRestoreFailure(null));
        }

        [Fact]
        public void DescribeRestoreFailure_PlainException_NamesTypeAndMessage()
        {
            var detail = InGameTestRunner.DescribeRestoreFailure(
                new InvalidOperationException("stage manager missing"));

            Assert.Contains("System.InvalidOperationException", detail);
            Assert.Contains("stage manager missing", detail);
        }

        [Fact]
        public void DescribeRestoreFailure_NestedInnerException_IncludesEveryLayer()
        {
            var detail = InGameTestRunner.DescribeRestoreFailure(
                new InvalidOperationException(
                    "outer wrapper",
                    new IOException(
                        "middle io",
                        new NullReferenceException("innermost nre"))));

            Assert.Contains("System.InvalidOperationException", detail);
            Assert.Contains("outer wrapper", detail);
            Assert.Contains("System.IO.IOException", detail);
            Assert.Contains("middle io", detail);
            Assert.Contains("System.NullReferenceException", detail);
            Assert.Contains("innermost nre", detail);
        }

        [Fact]
        public void DescribeRestoreFailure_ThrownAndCaughtException_IncludesStackFrames()
        {
            // The whole point of the helper: a thrown exception's detail must name
            // the throwing statement, which ex.Message alone never did.
            Exception caught = null;
            try
            {
                ThrowDeliberateRestoreFailure();
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            var detail = InGameTestRunner.DescribeRestoreFailure(caught);

            Assert.Contains("System.NullReferenceException", detail);
            Assert.Contains("deliberate prime failure", detail);
            // Stack trace must name the throwing method and this test method.
            Assert.Contains(nameof(ThrowDeliberateRestoreFailure), detail);
            Assert.Contains(nameof(DescribeRestoreFailure_ThrownAndCaughtException_IncludesStackFrames), detail);
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void ThrowDeliberateRestoreFailure()
        {
            throw new NullReferenceException("deliberate prime failure");
        }
    }
}
