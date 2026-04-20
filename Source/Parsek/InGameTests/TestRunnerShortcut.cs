using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Global Ctrl+Shift+T shortcut for the in-game test runner.
    /// Active in all game scenes — handles scenes where Parsek has no main UI
    /// (tracking station buildings, editor, facility interiors).
    ///
    /// #269: Uses Instantly+DontDestroyOnLoad to survive scene transitions,
    /// enabling multi-scene coroutine tests (e.g., quickload-resume).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class TestRunnerShortcut : MonoBehaviour
    {
        private const string Tag = "TestRunner";

        private bool showWindow;
        private Rect windowRect;
        private Vector2 scrollPos;
        private InGameTestRunner runner;
        private bool windowHasInputLock;
        private const string InputLockId = "Parsek_TestRunnerGlobal";
        private readonly HashSet<string> expandedCategories = new HashSet<string>();
        private List<KeyValuePair<string, List<InGameTestInfo>>> cachedGroups;
        private bool wasRunning;
        private GUIStyle opaqueStyle;
        private GUIStyle zeroHeightLabelStyle;
        private GUIStyle wrappedErrorLabelStyle;
        private GUIStyle wrappedTooltipStyle;

        private bool shortcutHeld;
        private static TestRunnerShortcut instance;
        private const float DefaultWindowWidth = 440f;
        private const float DefaultWindowHeight = 500f;
        private const float ErrorIndent = 40f;
        private const float ErrorMaxWidth = 380f;

        /// <summary>
        /// Singleton accessor — non-null after Awake when DontDestroyOnLoad keeps the
        /// instance alive. Used by in-game tests to verify bridge survival (#269).
        /// </summary>
        internal static TestRunnerShortcut Instance => instance;

        void Awake()
        {
            if (instance != null)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);

            // #269: InputLockManager locks are scene-scoped — KSP clears them on
            // scene transition but our flag persists. Reset on scene change.
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChangeRequested);
        }

        void Start()
        {
            ParsekLog.Verbose(Tag, $"TestRunnerShortcut active in {HighLogic.LoadedScene}");
        }

        void Update()
        {
            // Input.GetKey is reliable across all KSP scenes (IMGUI Event is not)
            bool pressed = (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                && Input.GetKeyDown(KeyCode.T);

            if (pressed && !shortcutHeld)
            {
                showWindow = !showWindow;
                ParsekLog.Verbose(Tag,
                    $"Test runner toggled via shortcut: {(showWindow ? "open" : "closed")}");
            }
            shortcutHeld = pressed;
        }

        void OnGUI()
        {
            // #269: GUI.skin may not be initialized during LOADING scene
            if (HighLogic.LoadedScene == GameScenes.LOADING) return;

            if (!showWindow)
            {
                if (windowHasInputLock)
                {
                    InputLockManager.RemoveControlLock(InputLockId);
                    windowHasInputLock = false;
                }
                return;
            }

            if (runner == null)
            {
                runner = new InGameTestRunner(this);
                foreach (var t in runner.Tests)
                    expandedCategories.Add(t.Category);
                RebuildGroups();
            }

            if (windowRect.width < 1f)
                windowRect = new Rect(20, 60, DefaultWindowWidth, DefaultWindowHeight);

            if (opaqueStyle == null)
            {
                // Match ParsekUI's opaque window style: copy KSP skin, force alpha to 1
                opaqueStyle = new GUIStyle(GUI.skin.window);
                opaqueStyle.normal.background = MakeOpaqueCopy(opaqueStyle.normal.background);
                opaqueStyle.onNormal.background = MakeOpaqueCopy(opaqueStyle.onNormal.background);
                opaqueStyle.focused.background = MakeOpaqueCopy(opaqueStyle.focused.background);
                opaqueStyle.onFocused.background = MakeOpaqueCopy(opaqueStyle.onFocused.background);
                opaqueStyle.active.background = MakeOpaqueCopy(opaqueStyle.active.background);
                opaqueStyle.onActive.background = MakeOpaqueCopy(opaqueStyle.onActive.background);
                opaqueStyle.hover.background = MakeOpaqueCopy(opaqueStyle.hover.background);
                opaqueStyle.onHover.background = MakeOpaqueCopy(opaqueStyle.onHover.background);
            }

            windowRect = GUILayout.Window(
                "ParsekTestRunnerGlobal".GetHashCode(),
                windowRect,
                DrawWindow,
                "Parsek - Test Runner",
                opaqueStyle,
                GUILayout.Width(DefaultWindowWidth));

            if (windowRect.Contains(Event.current.mousePosition))
            {
                if (!windowHasInputLock)
                {
                    InputLockManager.SetControlLock(
                        ControlTypes.CAMERACONTROLS | ControlTypes.EDITOR_ICON_HOVER
                        | ControlTypes.EDITOR_ICON_PICK | ControlTypes.EDITOR_PAD_PICK_PLACE
                        | ControlTypes.EDITOR_PAD_PICK_COPY | ControlTypes.EDITOR_GIZMO_TOOLS
                        | ControlTypes.KSC_ALL,
                        InputLockId);
                    windowHasInputLock = true;
                }
            }
            else if (windowHasInputLock)
            {
                InputLockManager.RemoveControlLock(InputLockId);
                windowHasInputLock = false;
            }
        }

        void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
                GameEvents.onGameSceneLoadRequested.Remove(OnSceneChangeRequested);
            }
            if (windowHasInputLock)
                InputLockManager.RemoveControlLock(InputLockId);
        }

        /// <summary>
        /// #269: Reset scene-scoped state on scene transition.
        /// InputLockManager locks are cleared by KSP, so our tracking flag must match.
        /// opaqueStyle references GUI.skin textures that may not survive scene changes.
        /// </summary>
        private void OnSceneChangeRequested(GameScenes scene)
        {
            windowHasInputLock = false;
            opaqueStyle = null;
            zeroHeightLabelStyle = null;
            wrappedErrorLabelStyle = null;
            wrappedTooltipStyle = null;
        }

        private void DrawWindow(int windowID)
        {
            if (runner == null) { GUI.DragWindow(); return; }

            EnsureLayoutStyles();

            bool running = runner.IsRunning;
            if (cachedGroups == null || (wasRunning && !running))
                RebuildGroups();
            wasRunning = running;

            GUILayout.BeginHorizontal();
            GUI.enabled = !running;
            if (GUILayout.Button("Run All")) { runner.ResetResults(); runner.RunAll(); }
            if (GUILayout.Button(new GUIContent("Run All + Isolated",
                "Runs ordinary batch-safe tests plus [isolated] FLIGHT tests by capturing a temporary baseline save and quickloading it after each destructive test.")))
            {
                runner.ResetResults();
                runner.RunAllIncludingFlightRestore();
            }
            // The explicit Reset button wipes BOTH the live table and the per-scene
            // history used by the auto-export — clicking Reset must produce a fresh
            // report on the next run. The implicit pre-run ResetResults (above)
            // preserves per-scene history so KSC→Flight accumulation works.
            if (GUILayout.Button(new GUIContent("Reset",
                "Clears the table AND per-scene history used by the auto-exported results file.")))
            {
                runner.ClearAllSceneHistory();
            }
            GUI.enabled = running;
            if (GUILayout.Button("Cancel")) { runner.Cancel(); }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            string status = running ? "RUNNING" : "idle";
            GUILayout.Label(
                $"{status} | {runner.Passed} passed  {runner.Failed} failed  {runner.Skipped} skipped  ({runner.Tests.Count} total)",
                GUI.skin.box);
            GUILayout.Label($"Scene: {HighLogic.LoadedScene}");
            string batchModeNotice = BuildBatchModeNotice();
            if (!string.IsNullOrEmpty(batchModeNotice))
            {
                GUILayout.Label(batchModeNotice, GUI.skin.label);
            }

            scrollPos = GUILayout.BeginScrollView(scrollPos,
                GUILayout.MinHeight(200), GUILayout.MaxHeight(500));

            foreach (var group in cachedGroups)
            {
                var cat = group.Key;
                var tests = group.Value;
                bool expanded = expandedCategories.Contains(cat);
                int catPassed = 0, catFailed = 0;
                foreach (var t in tests)
                {
                    if (t.Status == TestStatus.Passed) catPassed++;
                    else if (t.Status == TestStatus.Failed) catFailed++;
                }

                GUILayout.BeginHorizontal();
                string arrow = expanded ? "\u25bc" : "\u25b6";
                string summary = catFailed > 0
                    ? $" ({catPassed}/{tests.Count}, {catFailed} failed)"
                    : $" ({catPassed}/{tests.Count})";
                if (GUILayout.Button($"{arrow} {cat}{summary}", GUI.skin.label))
                {
                    if (expanded) expandedCategories.Remove(cat);
                    else expandedCategories.Add(cat);
                }
                GUI.enabled = !running;
                if (GUILayout.Button("Run", GUILayout.Width(40)))
                {
                    runner.ResetCategory(cat);
                    runner.RunCategory(cat);
                }
                if (GUILayout.Button(new GUIContent("Run+",
                    "Runs this category plus any [isolated] FLIGHT tests by restoring a temporary baseline between destructive tests."),
                    GUILayout.Width(44)))
                {
                    runner.ResetCategory(cat);
                    runner.RunCategoryIncludingFlightRestore(cat);
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();

                if (!expanded) continue;

                foreach (var test in tests)
                {
                    bool eligible = test.RequiredScene == InGameTestAttribute.AnyScene
                        || test.RequiredScene == HighLogic.LoadedScene;

                    GUILayout.BeginHorizontal();
                    GUILayout.Space(16);
                    var prevColor = GUI.contentColor;
                    GUI.contentColor = GetStatusColor(test.Status);
                    GUILayout.Label(GetStatusIcon(test.Status), GUILayout.Width(20));
                    GUI.contentColor = prevColor;

                    if (!eligible) GUI.enabled = false;
                    string label = test.Method.Name;
                    if (test.RestoreBatchFlightBaselineAfterExecution)
                        label += " [isolated]";
                    else if (!test.AllowBatchExecution)
                        label += " [single]";
                    if (test.DurationMs > 0) label += $" ({test.DurationMs:F0}ms)";
                    GUILayout.Label(new GUIContent(label, BuildTestTooltip(test, eligible)), GUILayout.ExpandWidth(true));
                    GUI.enabled = true;

                    GUI.enabled = !running && eligible;
                    if (GUILayout.Button("\u25b6", GUILayout.Width(24)))
                    {
                        test.Status = TestStatus.NotRun;
                        test.ErrorMessage = null;
                        runner.RunSingle(test);
                    }
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();

                    // Always render error row — conditional begin/end causes
                    // Layout/Repaint control count mismatch when status changes mid-frame.
                    bool showError = test.Status == TestStatus.Failed && !string.IsNullOrEmpty(test.ErrorMessage);
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(ErrorIndent);
                    var prev = GUI.contentColor;
                    GUI.contentColor = Color.red;
                    GUILayout.Label(
                        showError ? test.ErrorMessage : string.Empty,
                        showError ? wrappedErrorLabelStyle : zeroHeightLabelStyle,
                        showError ? GUILayout.MaxWidth(ErrorMaxWidth) : GUILayout.Height(0f));
                    GUI.contentColor = prev;
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Export Results",
                "Auto-exported after every Run All / Run Category / row-play — click to re-write now.")))
                runner.ExportResultsFile();
            if (GUILayout.Button("Close")) showWindow = false;
            GUILayout.EndHorizontal();
            GUILayout.Label("Results file auto-updates after each run. Multi-scene runs accumulate.",
                GUI.skin.label);
            GUILayout.Label("Ctrl+Shift+T to toggle from any scene", GUI.skin.label);

            // Always render tooltip label — conditional rendering causes
            // Layout/Repaint control count mismatch (IMGUI exception).
            string tooltip = GUI.tooltip ?? "";
            GUILayout.Label(
                tooltip.Length > 0 ? tooltip : string.Empty,
                tooltip.Length > 0 ? wrappedTooltipStyle : zeroHeightLabelStyle,
                tooltip.Length > 0 ? GUILayout.ExpandWidth(true) : GUILayout.Height(0f));

            GUI.DragWindow();
        }

        private void RebuildGroups()
        {
            var groups = new Dictionary<string, List<InGameTestInfo>>();
            foreach (var t in runner.Tests)
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
            cachedGroups = sorted;
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

        private static string GetStatusIcon(TestStatus s)
        {
            switch (s)
            {
                case TestStatus.Passed:  return "\u2713";
                case TestStatus.Failed:  return "\u2717";
                case TestStatus.Running: return "\u25cb";
                case TestStatus.Skipped: return "\u2013";
                default:                 return "\u00b7";
            }
        }

        private static Color GetStatusColor(TestStatus s)
        {
            switch (s)
            {
                case TestStatus.Passed:  return Color.green;
                case TestStatus.Failed:  return Color.red;
                case TestStatus.Running: return Color.yellow;
                case TestStatus.Skipped: return Color.gray;
                default:                 return Color.white;
            }
        }

        private static Texture2D MakeOpaqueCopy(Texture2D source)
        {
            if (source == null) return null;
            RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            Texture2D copy = new Texture2D(source.width, source.height, TextureFormat.ARGB32, false);
            copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            Color[] pixels = copy.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
                pixels[i].a = 1f;
            copy.SetPixels(pixels);
            copy.Apply();
            copy.filterMode = source.filterMode;
            return copy;
        }

        private string BuildBatchModeNotice()
        {
            if (runner == null) return null;

            bool hasIsolated = false;
            bool hasManualOnly = false;

            foreach (var test in runner.Tests)
            {
                bool eligible = test.RequiredScene == InGameTestAttribute.AnyScene
                    || test.RequiredScene == HighLogic.LoadedScene;
                if (!eligible)
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

        private static string BuildTestTooltip(InGameTestInfo test, bool eligible)
        {
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
