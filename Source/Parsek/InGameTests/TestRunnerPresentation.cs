using System.Collections.Generic;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Pure presentation helpers shared by the in-game test runner windows.
    /// Keeps IMGUI code focused on layout and button wiring.
    /// </summary>
    internal static class TestRunnerPresentation
    {
        internal static bool IsEligibleForScene(InGameTestInfo test, GameScenes loadedScene)
        {
            if (test == null)
                return false;

            return test.RequiredScene == InGameTestAttribute.AnyScene
                || test.RequiredScene == loadedScene;
        }

        internal static string BuildRunSummary(
            bool isRunning,
            int passed,
            int failed,
            int skipped,
            int total)
        {
            string status = isRunning ? "RUNNING" : "idle";
            return $"{status} | {passed} passed  {failed} failed  {skipped} skipped  ({total} total)";
        }

        internal static string BuildCategoryButtonLabel(
            string category,
            IReadOnlyList<InGameTestInfo> testsInCategory,
            bool expanded)
        {
            int passed = 0;
            int failed = 0;
            int total = testsInCategory != null ? testsInCategory.Count : 0;

            if (testsInCategory != null)
            {
                for (int i = 0; i < testsInCategory.Count; i++)
                {
                    var test = testsInCategory[i];
                    if (test == null)
                        continue;

                    if (test.Status == TestStatus.Passed)
                        passed++;
                    else if (test.Status == TestStatus.Failed)
                        failed++;
                }
            }

            string arrow = expanded ? "\u25bc" : "\u25b6";
            string label = string.IsNullOrEmpty(category) ? "General" : category;
            string summary = failed > 0
                ? $" ({passed}/{total}, {failed} failed)"
                : $" ({passed}/{total})";
            return $"{arrow} {label}{summary}";
        }

        internal static string BuildTestLabel(InGameTestInfo test)
        {
            if (test == null)
                return string.Empty;

            string label = !string.IsNullOrEmpty(test.Method?.Name)
                ? test.Method.Name
                : (test.Name ?? string.Empty);

            if (test.RestoreBatchFlightBaselineAfterExecution)
                label += " [isolated]";
            else if (!test.AllowBatchExecution)
                label += " [single]";

            if (test.DurationMs > 0f)
                label += $" ({test.DurationMs:F0}ms)";

            return label;
        }

        internal static string BuildBatchModeNotice(
            IReadOnlyList<InGameTestInfo> tests,
            GameScenes loadedScene)
        {
            if (tests == null)
                return null;

            bool hasIsolated = false;
            bool hasManualOnly = false;

            for (int i = 0; i < tests.Count; i++)
            {
                var test = tests[i];
                if (!IsEligibleForScene(test, loadedScene))
                    continue;

                if (test.RestoreBatchFlightBaselineAfterExecution)
                    hasIsolated = true;
                else if (!test.AllowBatchExecution)
                    hasManualOnly = true;
            }

            if (hasIsolated && hasManualOnly)
            {
                return "[isolated] tests can run through Run All + Isolated / Run+. [single] tests still require the row play button.";
            }

            if (hasIsolated)
                return "[isolated] tests can run through Run All + Isolated / Run+ in a disposable FLIGHT session.";

            if (hasManualOnly)
                return "[single] tests are skipped by Run All / Run category. Use the row play button for manual-only destructive checks.";

            return null;
        }

        internal static string BuildTestTooltip(InGameTestInfo test, bool eligible)
        {
            if (test == null)
                return string.Empty;

            var lines = new List<string>();

            if (!string.IsNullOrEmpty(test.Description))
                lines.Add(test.Description);

            string batchNote = InGameTestRunner.GetBatchExecutionNote(test);
            if (!string.IsNullOrEmpty(batchNote))
                lines.Add(batchNote);

            if (!eligible)
                lines.Add($"Requires {test.RequiredScene} scene");

            if ((test.Status == TestStatus.Failed || test.Status == TestStatus.Skipped)
                && !string.IsNullOrEmpty(test.ErrorMessage)
                && test.ErrorMessage != batchNote)
            {
                lines.Add(test.ErrorMessage);
            }

            return lines.Count > 0 ? string.Join("\n", lines.ToArray()) : string.Empty;
        }
    }
}
