using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Global Ctrl+Shift+T shortcut for the in-game test runner.
    /// Active in all game scenes — handles scenes where Parsek has no main UI
    /// (tracking station buildings, editor, facility interiors).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
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

        private bool shortcutHeld;
        private static TestRunnerShortcut instance;

        void Start()
        {
            // Singleton — destroy duplicates (EveryScene can spawn multiples in editor)
            if (instance != null && instance != this)
            {
                Destroy(this);
                return;
            }
            instance = this;
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
                windowRect = new Rect(20, 60, 380, 500);

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
                "Parsek \u2014 Test Runner",
                opaqueStyle,
                GUILayout.Width(380));

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
            if (instance == this) instance = null;
            if (windowHasInputLock)
                InputLockManager.RemoveControlLock(InputLockId);
        }

        private void DrawWindow(int windowID)
        {
            if (runner == null) { GUI.DragWindow(); return; }

            bool running = runner.IsRunning;
            if (cachedGroups == null || (wasRunning && !running))
                RebuildGroups();
            wasRunning = running;

            GUILayout.BeginHorizontal();
            GUI.enabled = !running;
            if (GUILayout.Button("Run All")) { runner.ResetResults(); runner.RunAll(); }
            if (GUILayout.Button("Reset")) { runner.ResetResults(); }
            GUI.enabled = running;
            if (GUILayout.Button("Cancel")) { runner.Cancel(); }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            string status = running ? "RUNNING" : "idle";
            GUILayout.Label(
                $"{status} | {runner.Passed} passed  {runner.Failed} failed  {runner.Skipped} skipped  ({runner.Tests.Count} total)",
                GUI.skin.box);
            GUILayout.Label($"Scene: {HighLogic.LoadedScene}");

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
                    if (test.DurationMs > 0) label += $" ({test.DurationMs:F0}ms)";
                    GUILayout.Label(new GUIContent(label, test.Description ?? ""));
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

                    if (test.Status == TestStatus.Failed && !string.IsNullOrEmpty(test.ErrorMessage))
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Space(40);
                        var prev = GUI.contentColor;
                        GUI.contentColor = Color.red;
                        GUILayout.Label(test.ErrorMessage, GUILayout.MaxWidth(320));
                        GUI.contentColor = prev;
                        GUILayout.EndHorizontal();
                    }
                }
            }

            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Export Results")) runner.ExportResultsFile();
            if (GUILayout.Button("Close")) showWindow = false;
            GUILayout.EndHorizontal();
            GUILayout.Label("Ctrl+Shift+T to toggle from any scene", GUI.skin.label);

            if (!string.IsNullOrEmpty(GUI.tooltip))
                GUILayout.Label(GUI.tooltip, GUI.skin.box);

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
    }
}
