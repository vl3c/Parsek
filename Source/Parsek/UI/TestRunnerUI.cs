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
        private GUIStyle zeroHeightLabelStyle;
        private GUIStyle wrappedErrorLabelStyle;
        private GUIStyle wrappedTooltipStyle;

        private const float SpacingSmall = 3f;
        private const float DefaultWindowWidth = 440f;
        private const float DefaultWindowHeight = 500f;
        private const float ErrorIndent = 40f;
        private const float ErrorMaxWidth = 380f;

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
                    DefaultWindowWidth, DefaultWindowHeight);
            }

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            testRunnerWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekTestRunner".GetHashCode(),
                testRunnerWindowRect,
                DrawTestRunnerWindow,
                "Parsek \u2014 Test Runner",
                opaqueWindowStyle,
                GUILayout.Width(DefaultWindowWidth)
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
            EnsureLayoutStyles();

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
                    if (!test.AllowBatchExecution)
                        testLabel += " [single]";
                    if (test.DurationMs > 0)
                        testLabel += $" ({test.DurationMs:F0}ms)";
                    GUILayout.Label(
                        new GUIContent(testLabel,
                            BuildTestTooltip(test, eligible)),
                        GUILayout.ExpandWidth(true));
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

                    // Always render error row — conditional begin/end causes
                    // Layout/Repaint control count mismatch when status changes mid-frame.
                    bool showError = test.Status == TestStatus.Failed && !string.IsNullOrEmpty(test.ErrorMessage);
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(ErrorIndent);
                    var prevCol = GUI.contentColor;
                    GUI.contentColor = Color.red;
                    GUILayout.Label(
                        showError ? test.ErrorMessage : string.Empty,
                        showError ? wrappedErrorLabelStyle : zeroHeightLabelStyle,
                        showError ? GUILayout.MaxWidth(ErrorMaxWidth) : GUILayout.Height(0f));
                    GUI.contentColor = prevCol;
                    GUILayout.EndHorizontal();
                }
            }
        }

        private void EnsureLayoutStyles()
        {
            if (zeroHeightLabelStyle == null)
            {
                zeroHeightLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    fixedHeight = 0f,
                    stretchHeight = false,
                    wordWrap = false
                };
                zeroHeightLabelStyle.margin = new RectOffset(0, 0, 0, 0);
                zeroHeightLabelStyle.padding = new RectOffset(0, 0, 0, 0);
            }

            if (wrappedErrorLabelStyle == null)
            {
                wrappedErrorLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    wordWrap = true
                };
                wrappedErrorLabelStyle.margin = new RectOffset(0, 0, 0, 0);
            }

            if (wrappedTooltipStyle == null)
            {
                wrappedTooltipStyle = new GUIStyle(GUI.skin.box)
                {
                    wordWrap = true
                };
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

            EnsureLayoutStyles();

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
            if (HasSingleRunOnlyTestsForCurrentScene())
            {
                GUILayout.Label(
                    "Single-run tests are skipped by Run All / Run category. Use the row ▶ button for destructive scene-transition checks.",
                    GUI.skin.label);
            }

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

            // Always render tooltip label — conditional rendering causes
            // Layout/Repaint control count mismatch (IMGUI exception).
            string tooltip = GUI.tooltip ?? "";
            GUILayout.Space(SpacingSmall);
            GUILayout.Label(
                tooltip.Length > 0 ? tooltip : string.Empty,
                tooltip.Length > 0 ? wrappedTooltipStyle : zeroHeightLabelStyle,
                tooltip.Length > 0 ? GUILayout.ExpandWidth(true) : GUILayout.Height(0f));

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

        private bool HasSingleRunOnlyTestsForCurrentScene()
        {
            if (testRunner == null) return false;

            foreach (var test in testRunner.Tests)
            {
                bool eligible = test.RequiredScene == InGameTestAttribute.AnyScene
                    || test.RequiredScene == HighLogic.LoadedScene;
                if (eligible && !test.AllowBatchExecution)
                    return true;
            }

            return false;
        }

        private static string BuildTestTooltip(InGameTestInfo test, bool eligible)
        {
            var lines = new List<string>();

            if (!string.IsNullOrEmpty(test.Description))
                lines.Add(test.Description);

            string batchNote = InGameTestRunner.GetBatchSkipReason(test);
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
