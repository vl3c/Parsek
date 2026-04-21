using System.Collections.Generic;
using System.Reflection;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the pure presentation rules shared by the IMGUI test-runner windows.
    /// </summary>
    public class TestRunnerPresentationTests
    {
        private static void SampleFixtureMethod()
        {
        }

        [Fact]
        public void IsEligibleForScene_AnyScene_ReturnsTrue()
        {
            var test = new InGameTestInfo
            {
                RequiredScene = InGameTestAttribute.AnyScene
            };

            Assert.True(TestRunnerPresentation.IsEligibleForScene(test, GameScenes.FLIGHT));
        }

        [Fact]
        public void IsEligibleForScene_SpecificMismatch_ReturnsFalse()
        {
            var test = new InGameTestInfo
            {
                RequiredScene = GameScenes.FLIGHT
            };

            Assert.False(TestRunnerPresentation.IsEligibleForScene(test, GameScenes.SPACECENTER));
        }

        [Fact]
        public void BuildRunSummary_FormatsRunningStateAndCounts()
        {
            string summary = TestRunnerPresentation.BuildRunSummary(
                isRunning: true,
                passed: 3,
                failed: 1,
                skipped: 2,
                total: 6);

            Assert.Equal("RUNNING | 3 passed  1 failed  2 skipped  (6 total)", summary);
        }

        [Fact]
        public void BuildCategoryButtonLabel_IncludesArrowAndFailedCount()
        {
            var tests = new List<InGameTestInfo>
            {
                new InGameTestInfo { Status = TestStatus.Passed },
                new InGameTestInfo { Status = TestStatus.Failed },
                new InGameTestInfo { Status = TestStatus.Skipped },
            };

            string label = TestRunnerPresentation.BuildCategoryButtonLabel(
                "Flight",
                tests,
                expanded: true);

            Assert.Equal("\u25bc Flight (1/3, 1 failed)", label);
        }

        [Fact]
        public void BuildTestLabel_PrefersShortMethodNameAndFormatsIsolatedDuration()
        {
            MethodInfo method = typeof(TestRunnerPresentationTests).GetMethod(
                nameof(SampleFixtureMethod),
                BindingFlags.NonPublic | BindingFlags.Static);
            var test = new InGameTestInfo
            {
                Name = "Fixture.SampleFixtureMethod",
                Method = method,
                AllowBatchExecution = false,
                RestoreBatchFlightBaselineAfterExecution = true,
                DurationMs = 12.6f
            };

            string label = TestRunnerPresentation.BuildTestLabel(test);

            Assert.Equal("SampleFixtureMethod [isolated] (13ms)", label);
        }

        [Fact]
        public void BuildBatchModeNotice_FiltersOutIneligibleTests()
        {
            var tests = new List<InGameTestInfo>
            {
                new InGameTestInfo
                {
                    Name = "FlightOnly",
                    RequiredScene = GameScenes.FLIGHT,
                    AllowBatchExecution = false,
                    RestoreBatchFlightBaselineAfterExecution = true
                },
                new InGameTestInfo
                {
                    Name = "KscManual",
                    RequiredScene = GameScenes.SPACECENTER,
                    AllowBatchExecution = false
                },
            };

            string notice = TestRunnerPresentation.BuildBatchModeNotice(
                tests,
                GameScenes.SPACECENTER);

            Assert.Equal(
                "[single] tests are skipped by Run All / Run category. Use the row play button for manual-only destructive checks.",
                notice);
        }

        [Fact]
        public void BuildBatchModeNotice_WhenBothKindsAreEligible_ShowsCombinedNotice()
        {
            var tests = new List<InGameTestInfo>
            {
                new InGameTestInfo
                {
                    RequiredScene = InGameTestAttribute.AnyScene,
                    AllowBatchExecution = false,
                    RestoreBatchFlightBaselineAfterExecution = true
                },
                new InGameTestInfo
                {
                    RequiredScene = InGameTestAttribute.AnyScene,
                    AllowBatchExecution = false
                },
            };

            string notice = TestRunnerPresentation.BuildBatchModeNotice(
                tests,
                GameScenes.FLIGHT);

            Assert.Equal(
                "[isolated] tests can run through Run All + Isolated / Run+. [single] tests still require the row play button.",
                notice);
        }

        [Fact]
        public void BuildTestTooltip_IncludesDescriptionBatchNoteSceneAndFailure()
        {
            var test = new InGameTestInfo
            {
                Description = "Moves the vessel through a scene transition.",
                RequiredScene = GameScenes.FLIGHT,
                AllowBatchExecution = false,
                Status = TestStatus.Failed,
                ErrorMessage = "Observed staging mismatch."
            };

            string tooltip = TestRunnerPresentation.BuildTestTooltip(test, eligible: false);

            Assert.Contains("Moves the vessel through a scene transition.", tooltip);
            Assert.Contains(InGameTestRunner.DefaultBatchSkipReason, tooltip);
            Assert.Contains("Requires FLIGHT scene", tooltip);
            Assert.Contains("Observed staging mismatch.", tooltip);
        }

        [Fact]
        public void BuildTestTooltip_DoesNotRepeatBatchNoteAsFailure()
        {
            var test = new InGameTestInfo
            {
                RestoreBatchFlightBaselineAfterExecution = true,
                Status = TestStatus.Failed,
                ErrorMessage = InGameTestRunner.DefaultBatchRestoreNote
            };

            string tooltip = TestRunnerPresentation.BuildTestTooltip(test, eligible: true);

            Assert.Equal(InGameTestRunner.DefaultBatchRestoreNote, tooltip);
        }
    }
}
