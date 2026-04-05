using System.Collections.Generic;
using ClickThroughFix;
using Parsek.InGameTests;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Test runner window extracted from ParsekUI.
    /// Displays in-game tests by category with run/reset/cancel controls.
    /// </summary>
    internal class TestRunnerUI
    {
        private readonly ParsekUI parentUI;

        private bool showTestRunnerWindow;
        private Rect testRunnerWindowRect;
        private Vector2 testRunnerScrollPos;
        private bool testRunnerWindowHasInputLock;
        private const string TestRunnerInputLockId = "Parsek_TestRunnerWindow";
        private Rect lastTestRunnerWindowRect;
        private InGameTestRunner testRunner;
        private readonly HashSet<string> expandedTestCategories = new HashSet<string>();
        private List<KeyValuePair<string, List<InGameTestInfo>>> cachedTestGroups;
        private bool testRunnerWasRunning;

        private const float SpacingSmall = 3f;

        public bool IsOpen
        {
            get { return showTestRunnerWindow; }
            set { showTestRunnerWindow = value; }
        }

        internal TestRunnerUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
        }

        public void DrawIfOpen(Rect mainWindowRect, MonoBehaviour host)
        {
            if (!showTestRunnerWindow)
            {
                ReleaseInputLock();
                return;
            }

            // Lazy-init the test runner
            if (testRunner == null)
            {
                testRunner = new InGameTestRunner(host);
                foreach (var t in testRunner.Tests)
                    expandedTestCategories.Add(t.Category);
                RebuildTestGroupCache();
            }

            if (testRunnerWindowRect.width < 1f)
            {
                testRunnerWindowRect = new Rect(
                    mainWindowRect.x + mainWindowRect.width + 10,
                    mainWindowRect.y,
                    380, 500);
            }

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            testRunnerWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekTestRunner".GetHashCode(),
                testRunnerWindowRect,
                DrawTestRunnerWindow,
                "Parsek \u2014 Test Runner",
                opaqueWindowStyle,
                GUILayout.Width(380)
            );
            parentUI.LogWindowPosition("TestRunner", ref lastTestRunnerWindowRect, testRunnerWindowRect);

            if (testRunnerWindowRect.Contains(Event.current.mousePosition))
            {
                if (!testRunnerWindowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, TestRunnerInputLockId);
                    testRunnerWindowHasInputLock = true;
                }
            }
            else
            {
                ReleaseInputLock();
            }
        }

        private void ReleaseInputLock()
        {
            if (!testRunnerWindowHasInputLock) return;
            InputLockManager.RemoveControlLock(TestRunnerInputLockId);
            testRunnerWindowHasInputLock = false;
        }

        private void RebuildTestGroupCache()
        {
            var groups = new Dictionary<string, List<InGameTestInfo>>();
            foreach (var t in testRunner.Tests)
            {
                if (!groups.TryGetValue(t.Category, out var list))
                {
                    list = new List<InGameTestInfo>();
                    groups[t.Category] = list;
                }
                list.Add(t);
            }
            var sorted = new List<KeyValuePair<string, List<InGameTestInfo>>>(groups);
            sorted.Sort((a, b) => string.Compare(a.Key, b.Key, System.StringComparison.Ordinal));
            cachedTestGroups = sorted;
        }

        private void DrawTestCategoryList()
        {
            foreach (var group in cachedTestGroups)
            {
                var category = group.Key;
                var testsInCategory = group.Value;

                bool expanded = expandedTestCategories.Contains(category);
                int catPassed = 0, catFailed = 0;
                foreach (var t in testsInCategory)
                {
                    if (t.Status == TestStatus.Passed) catPassed++;
                    else if (t.Status == TestStatus.Failed) catFailed++;
                }

                // Category header
                GUILayout.BeginHorizontal();
                string arrow = expanded ? "\u25bc" : "\u25b6";
                string catSummary = catFailed > 0
                    ? $" ({catPassed}/{testsInCategory.Count}, {catFailed} failed)"
                    : $" ({catPassed}/{testsInCategory.Count})";
                if (GUILayout.Button($"{arrow} {category}{catSummary}", GUI.skin.label))
                {
                    if (expanded) expandedTestCategories.Remove(category);
                    else expandedTestCategories.Add(category);
                }
                // Run category button
                GUI.enabled = !testRunner.IsRunning;
                if (GUILayout.Button("Run", GUILayout.Width(40)))
                {
                    testRunner.ResetCategory(category);
                    testRunner.RunCategory(category);
                    ParsekLog.Verbose("UI", $"Test runner: Run category '{category}'");
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();

                if (!expanded) continue;

                // Individual tests
                foreach (var test in testsInCategory)
                {
                    bool eligible = test.RequiredScene == InGameTestAttribute.AnyScene
                        || test.RequiredScene == HighLogic.LoadedScene;

                    GUILayout.BeginHorizontal();
                    GUILayout.Space(16);

                    // Status indicator
                    string icon = GetTestStatusIcon(test.Status);
                    var prevColor = GUI.contentColor;
                    GUI.contentColor = GetTestStatusColor(test.Status);
                    GUILayout.Label(icon, GUILayout.Width(20));
                    GUI.contentColor = prevColor;

                    // Test name (dimmed if wrong scene)
                    if (!eligible) GUI.enabled = false;
                    string testLabel = test.Name;
                    if (test.Method.DeclaringType != null)
                        testLabel = test.Method.Name; // short name within category
                    if (test.DurationMs > 0)
                        testLabel += $" ({test.DurationMs:F0}ms)";
                    GUILayout.Label(new GUIContent(testLabel,
                        test.Description ?? (eligible ? "" : $"Requires {test.RequiredScene} scene")));
                    GUI.enabled = true;

                    // Run single button
                    GUI.enabled = !testRunner.IsRunning && eligible;
                    if (GUILayout.Button("\u25b6", GUILayout.Width(24)))
                    {
                        test.Status = TestStatus.NotRun;
                        test.ErrorMessage = null;
                        testRunner.RunSingle(test);
                    }
                    GUI.enabled = true;

                    GUILayout.EndHorizontal();

                    // Error message if failed
                    if (test.Status == TestStatus.Failed && !string.IsNullOrEmpty(test.ErrorMessage))
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Space(40);
                        var prevCol = GUI.contentColor;
                        GUI.contentColor = Color.red;
                        GUILayout.Label(test.ErrorMessage, GUILayout.MaxWidth(320));
                        GUI.contentColor = prevCol;
                        GUILayout.EndHorizontal();
                    }
                }
            }
        }

        private void DrawTestRunnerWindow(int windowID)
        {
            if (testRunner == null)
            {
                GUILayout.Label("Test runner not initialized.");
                GUI.DragWindow();
                return;
            }

            // --- Controls bar ---
            GUILayout.BeginHorizontal();
            GUI.enabled = !testRunner.IsRunning;
            if (GUILayout.Button("Run All"))
            {
                testRunner.ResetResults();
                testRunner.RunAll();
                ParsekLog.Info("UI", "Test runner: Run All clicked");
            }
            if (GUILayout.Button("Reset"))
            {
                testRunner.ResetResults();
                ParsekLog.Verbose("UI", "Test runner: Reset clicked");
            }
            GUI.enabled = testRunner.IsRunning;
            if (GUILayout.Button("Cancel"))
            {
                testRunner.Cancel();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // --- Summary ---
            GUILayout.Space(SpacingSmall);
            int total = testRunner.Tests.Count;
            string status = testRunner.IsRunning ? "RUNNING" : "idle";
            GUILayout.Label(
                $"{status} | {testRunner.Passed} passed  {testRunner.Failed} failed  {testRunner.Skipped} skipped  ({total} total)",
                GUI.skin.box);

            // --- Scene filter note ---
            GUILayout.Label($"Scene: {HighLogic.LoadedScene}", GUI.skin.label);

            // --- Rebuild cached groups when run state changes ---
            bool running = testRunner.IsRunning;
            if (cachedTestGroups == null || (testRunnerWasRunning && !running))
                RebuildTestGroupCache();
            testRunnerWasRunning = running;

            // --- Test list ---
            GUILayout.Space(SpacingSmall);
            testRunnerScrollPos = GUILayout.BeginScrollView(testRunnerScrollPos,
                GUILayout.MinHeight(200), GUILayout.MaxHeight(500));

            DrawTestCategoryList();

            GUILayout.EndScrollView();

            // --- Bottom bar ---
            GUILayout.Space(SpacingSmall);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Export Results"))
            {
                testRunner.ExportResultsFile();
                ParsekLog.Info("UI", "Test runner: manually exported results file");
            }
            if (GUILayout.Button("Close"))
            {
                showTestRunnerWindow = false;
                ParsekLog.Verbose("UI", "Test runner window closed");
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Ctrl+Shift+T to toggle from any scene", GUI.skin.label);

            // Tooltip
            if (!string.IsNullOrEmpty(GUI.tooltip))
            {
                GUILayout.Space(SpacingSmall);
                GUILayout.Label(GUI.tooltip, GUI.skin.box);
            }

            GUI.DragWindow();
        }

        private static string GetTestStatusIcon(TestStatus status)
        {
            switch (status)
            {
                case TestStatus.Passed:  return "\u2713"; // checkmark
                case TestStatus.Failed:  return "\u2717"; // X
                case TestStatus.Running: return "\u25cb"; // circle
                case TestStatus.Skipped: return "\u2013"; // dash
                default:                 return "\u00b7"; // dot
            }
        }

        private static Color GetTestStatusColor(TestStatus status)
        {
            switch (status)
            {
                case TestStatus.Passed:  return Color.green;
                case TestStatus.Failed:  return Color.red;
                case TestStatus.Running: return Color.yellow;
                case TestStatus.Skipped: return Color.gray;
                default:                 return Color.white;
            }
        }
    }
}
