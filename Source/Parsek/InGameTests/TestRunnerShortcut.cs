using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
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
        private bool isResizingWindow;
        private const string InputLockId = "Parsek_TestRunnerGlobal";
        private readonly HashSet<string> expandedCategories = new HashSet<string>();
        private List<KeyValuePair<string, List<InGameTestInfo>>> cachedGroups;
        private bool wasRunning;
        private GUIStyle opaqueStyle;
        private GUIStyle zeroHeightLabelStyle;
        private GUIStyle wrappedErrorLabelStyle;
        private GUIStyle wrappedTooltipStyle;
        private GameScenes opaqueStyleScene;
        private bool hasOpaqueStyleScene;

        private bool shortcutHeld;

        // [M-A3 hook H1] Autorun state. autorunConfig is parsed ONCE in Awake (launch-time
        // contract; a mid-process env mutation cannot change behavior, edge 14). All of
        // Update's autorun work early-returns on the cached !autorunConfig.Enabled bool, so
        // an unarmed process (normal play, normal dotnet test) does no per-frame work.
        private AutorunConfig autorunConfig;
        private bool autorunParsed;
        // Consecutive qualifying frames toward the settle target; reset to 0 whenever any
        // scene-settle condition regresses (design "Scene-settle definition", item 5).
        private int autorunSettleFrames;
        // Single-fire per scene-entry; reset on onGameSceneLoadRequested so a scene the
        // orchestrator scripts re-arms, while autorunConsumedForProcess (never reset) keeps
        // a FLIGHT->FLIGHT isolation reload from restarting the selector (edge 8).
        private bool autorunFiredThisScene;
        private bool autorunConsumedForProcess;
        // A multi-category driver coroutine owns the run while true (single-fire is already
        // consumed, but this stops Update from re-entering the fire path).
        private bool autorunMultiDriving;
        // Settle target: ~0.5 s at 60 fps for stock one-frame-late init (camera, UI,
        // ScenarioModule OnLoad) to finish before the batch captures its baseline.
        private const int AutorunSettleTarget = 30;
        // No-game-scene timeout warn (edge 7): one-shot after a bounded wait so the log
        // explains an eventual orchestrator-timeout kill when no save ever loaded.
        private const float AutorunNoSceneWarnSeconds = 60f;
        private float autorunArmedRealtime;
        private bool autorunNoSceneWarned;
        internal const string EnvTestsVar = "PARSEK_AUTORUN_TESTS";
        internal const string EnvExitVar = "PARSEK_AUTORUN_EXIT";

        private static TestRunnerShortcut instance;
        private const float DefaultWindowWidth = 440f;
        private const float DefaultWindowHeight = 600f;
        private const float MinWindowWidth = 320f;
        // Default height is also the minimum: the window opens at this height
        // and will not shrink below it (matches the Logistics window).
        private const float MinWindowHeight = DefaultWindowHeight;
        private const float ErrorIndent = 40f;
        private const float ErrorMaxWidth = 380f;

        /// <summary>
        /// Singleton accessor — non-null after Awake when DontDestroyOnLoad keeps the
        /// instance alive. Used by in-game tests to verify bridge survival (#269).
        /// </summary>
        internal static TestRunnerShortcut Instance => instance;

        /// <summary>
        /// The interactive (Ctrl+Shift+T) test runner this shortcut owns, exposed so the
        /// ParsekTestCommands addon's safe-point gate can OR its batch state into
        /// <c>IsBatchRunning</c>: a command must never execute mid-batch even when the batch
        /// was started from this UI rather than the addon's own <c>RunTests</c> runner. Null
        /// until the window is first opened (the runner is lazily created in <c>OnGUI</c>).
        /// </summary>
        internal static InGameTestRunner ActiveRunnerForGating => instance != null ? instance.runner : null;

        internal bool HasOpaqueStyleForTesting => opaqueStyle != null;
        internal bool HasAllOpaqueStateBackgroundsForTesting => AreAllOpaqueStyleBackgroundsPresent(opaqueStyle);
        internal Texture2D OpaqueWindowBackgroundForTesting => opaqueStyle?.normal.background;

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

            ParseAutorunConfigOnce();
        }

        /// <summary>
        /// [M-A3 hook H1] Parse the two autorun env vars ONCE at addon Awake into the
        /// cached <see cref="autorunConfig"/> (design "Read-once caching", edge 14). Emit
        /// the startup selector line that records the exact env contract the process
        /// launched with, plus any misconfiguration warnings (edge 2 zero-categories, edge
        /// 9 exit-without-tests). Never re-reads the environment.
        /// </summary>
        private void ParseAutorunConfigOnce()
        {
            string testsVar = Environment.GetEnvironmentVariable(EnvTestsVar);
            string exitVar = Environment.GetEnvironmentVariable(EnvExitVar);
            autorunConfig = AutorunHooks.Parse(testsVar, exitVar);
            autorunParsed = true;
            autorunArmedRealtime = Time.realtimeSinceStartup;

            ParsekLog.Info(Tag,
                $"autorun selector parsed: enabled={autorunConfig.Enabled} "
                + $"selector='{autorunConfig.RawSelector ?? "(unset)"}' "
                + $"categories=[{string.Join(",", autorunConfig.Categories)}] "
                + $"exit={autorunConfig.ExitArmed}");
            foreach (string warning in autorunConfig.Warnings)
                ParsekLog.Warn(Tag, warning);
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

            UpdateAutorun();
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

            EnsureRunner();

            if (windowRect.width < 1f)
                windowRect = new Rect(20, 60, DefaultWindowWidth, DefaultWindowHeight);

            if (!EnsureOpaqueStyle(GUI.skin))
                return;

            ParsekUI.HandleResizeDrag(ref windowRect, ref isResizingWindow,
                MinWindowWidth, MinWindowHeight, "TestRunner global window");

            windowRect = RunWindowWithNormalizedGuiColors(() =>
                GUILayout.Window(
                    "ParsekTestRunnerGlobal".GetHashCode(),
                    windowRect,
                    DrawWindow,
                    "Parsek - Test Runner",
                    opaqueStyle,
                    GUILayout.Width(windowRect.width),
                    GUILayout.Height(windowRect.height)));

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
            ClearOpaqueStyle();
            if (windowHasInputLock)
                InputLockManager.RemoveControlLock(InputLockId);
        }

        /// <summary>
        /// #269: Reset scene-scoped state on scene transition.
        /// InputLockManager locks are cleared by KSP, so our tracking flag must match.
        /// Opaque window textures are copied into owned Texture2D instances, so drop
        /// and destroy them explicitly before the next scene rebuild.
        /// </summary>
        private void OnSceneChangeRequested(GameScenes scene)
        {
            ResetSceneScopedWindowState();

            // [M-A3 hook H1] Re-arm per scene: a new scene entry clears the fired-this-
            // scene flag and the settle counter so H1 can fire in a scene the orchestrator
            // scripts. The process-level autorunConsumedForProcess latch is NOT reset here,
            // so a FLIGHT->FLIGHT isolation reload re-arms but never restarts the selector
            // (edge 8; the !runner.IsRunning + consumed guards in the fire gate enforce it).
            autorunFiredThisScene = false;
            autorunSettleFrames = 0;
        }

        private void ResetSceneScopedWindowState()
        {
            windowHasInputLock = false;
            ClearOpaqueStyle();
            zeroHeightLabelStyle = null;
            wrappedErrorLabelStyle = null;
            wrappedTooltipStyle = null;
        }

        internal bool TryEnsureOpaqueStyleForTesting(GUISkin skin)
        {
            return EnsureOpaqueStyle(skin);
        }

        internal T RunWindowWithNormalizedGuiColorsForTesting<T>(System.Func<T> callback)
        {
            return RunWindowWithNormalizedGuiColors(callback);
        }

        private static T RunWindowWithNormalizedGuiColors<T>(System.Func<T> callback)
        {
            return ParsekUI.RunWithNormalizedWindowGuiColors(callback);
        }

        private bool EnsureOpaqueStyle(GUISkin skin)
        {
            if (opaqueStyle != null)
            {
                if (hasOpaqueStyleScene
                    && opaqueStyleScene == HighLogic.LoadedScene
                    && AreAllOpaqueStyleBackgroundsPresent(opaqueStyle))
                    return true;

                ParsekLog.VerboseRateLimited(Tag, "opaque-style-cache-stale",
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Test runner: dropping cached opaque style before rebuild (built={0}, current={1})",
                        opaqueStyleScene, HighLogic.LoadedScene), 1f);
                ResetSceneScopedWindowState();
            }

            if (!IsOpaqueStyleSkinReady(skin))
            {
                ParsekLog.VerboseRateLimited(Tag, "opaque-style-skin-not-ready",
                    "Test runner: skin not ready, deferring opaque style rebuild", 1f);
                return false;
            }

            opaqueStyle = BuildOpaqueStyle(skin.window);
            opaqueStyleScene = HighLogic.LoadedScene;
            hasOpaqueStyleScene = true;
            return true;
        }

        private void ClearOpaqueStyle()
        {
            if (opaqueStyle == null)
                return;

            var destroyedBackgrounds = new HashSet<int>();
            DestroyOpaqueBackground(opaqueStyle.normal.background, destroyedBackgrounds);
            DestroyOpaqueBackground(opaqueStyle.onNormal.background, destroyedBackgrounds);
            DestroyOpaqueBackground(opaqueStyle.focused.background, destroyedBackgrounds);
            DestroyOpaqueBackground(opaqueStyle.onFocused.background, destroyedBackgrounds);
            DestroyOpaqueBackground(opaqueStyle.active.background, destroyedBackgrounds);
            DestroyOpaqueBackground(opaqueStyle.onActive.background, destroyedBackgrounds);
            DestroyOpaqueBackground(opaqueStyle.hover.background, destroyedBackgrounds);
            DestroyOpaqueBackground(opaqueStyle.onHover.background, destroyedBackgrounds);
            opaqueStyle = null;
            hasOpaqueStyleScene = false;
        }

        private static bool IsOpaqueStyleSkinReady(GUISkin skin)
        {
            return skin != null
                && skin.window != null
                && skin.window.normal.background != null;
        }

        private static GUIStyle BuildOpaqueStyle(GUIStyle sourceStyle)
        {
            return ParsekUI.BuildOpaqueWindowStyleFromSource(sourceStyle);
        }

        private static bool AreAllOpaqueStyleBackgroundsPresent(GUIStyle style)
        {
            return style != null
                && style.normal.background != null
                && style.onNormal.background != null
                && style.focused.background != null
                && style.onFocused.background != null
                && style.active.background != null
                && style.onActive.background != null
                && style.hover.background != null
                && style.onHover.background != null;
        }

        private static void DestroyOpaqueBackground(
            Texture2D background,
            HashSet<int> destroyedBackgrounds)
        {
            if (background == null)
                return;

            int id = background.GetInstanceID();
            if (!destroyedBackgrounds.Add(id))
                return;

            UnityEngine.Object.Destroy(background);
        }
        /// <summary>
        /// [M-A3 correction G2] The single lazy runner factory, extracted from the
        /// former OnGUI-inline construction so the H1 autorun fire path
        /// (<see cref="Update"/>) and the interactive window-open path share ONE runner
        /// lifecycle. Idempotent: returns immediately once the runner exists, so calling
        /// it every settled frame is cheap. Without this, autorun (no window ever opened)
        /// would either fire against a null runner or construct a second, isolation-blind
        /// runner instance.
        /// </summary>
        private void EnsureRunner()
        {
            if (runner != null) return;
            runner = new InGameTestRunner(this);
            foreach (var t in runner.Tests)
                expandedCategories.Add(t.Category);
            RebuildGroups();
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

            GUILayout.Label(
                TestRunnerPresentation.BuildRunSummary(
                    running,
                    runner.Passed,
                    runner.Failed,
                    runner.Skipped,
                    runner.Tests.Count),
                GUI.skin.box);
            GUILayout.Label($"Scene: {HighLogic.LoadedScene}");
            string batchModeNotice = TestRunnerPresentation.BuildBatchModeNotice(
                runner.Tests,
                HighLogic.LoadedScene);
            if (!string.IsNullOrEmpty(batchModeNotice))
            {
                GUILayout.Label(batchModeNotice, GUI.skin.label);
            }

            // Bare ExpandHeight (no MinHeight) so the scroll view stretches to
            // fill the fixed-height window, pinning the Close button + info
            // labels to the bottom. A MinHeight option clears stretchHeight and
            // leaves the footer floating with dead space below it. Matches the
            // Kerbals / Real Spawn Control / Logistics windows.
            scrollPos = GUILayout.BeginScrollView(scrollPos,
                GUILayout.ExpandHeight(true));

            foreach (var group in cachedGroups)
            {
                var cat = group.Key;
                var tests = group.Value;
                bool expanded = expandedCategories.Contains(cat);

                GUILayout.BeginHorizontal();
                string categoryLabel = TestRunnerPresentation.BuildCategoryButtonLabel(
                    cat,
                    tests,
                    expanded);
                if (GUILayout.Button(categoryLabel, GUI.skin.label))
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
                    bool eligible = TestRunnerPresentation.IsEligibleForScene(
                        test,
                        HighLogic.LoadedScene);

                    GUILayout.BeginHorizontal();
                    GUILayout.Space(16);
                    var prevColor = GUI.contentColor;
                    GUI.contentColor = GetStatusColor(test.Status);
                    GUILayout.Label(GetStatusIcon(test.Status), GUILayout.Width(20));
                    GUI.contentColor = prevColor;

                    // Manual-only [single] scenarios are tinted blue so they
                    // are easy to find (dimmed when the scene is ineligible).
                    // Readable light blue: Color.blue is too dark on KSP's dark window.
                    if (!eligible) GUI.enabled = false;
                    string label = TestRunnerPresentation.BuildTestLabel(test);
                    var prevLabelColor = GUI.contentColor;
                    if (TestRunnerPresentation.IsManualOnly(test))
                        GUI.contentColor = new Color(0.45f, 0.65f, 1f);
                    GUILayout.Label(
                        new GUIContent(label, TestRunnerPresentation.BuildTestTooltip(test, eligible)),
                        GUILayout.ExpandWidth(true));
                    GUI.contentColor = prevLabelColor;
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

            // Results auto-export after every run, so there is no manual export
            // button (the file always reflects the latest run). Full-width Close
            // (matches the Logistics / Kerbals / Settings windows).
            if (GUILayout.Button("Close")) showWindow = false;
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

            ParsekUI.DrawResizeHandle(windowRect, ref isResizingWindow, "TestRunner global window");

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
                wrappedTooltipStyle = new GUIStyle(GUI.skin.label)
                {
                    wordWrap = true
                };
                wrappedTooltipStyle.margin = new RectOffset(0, 0, 0, 0);
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

        // ----- [M-A3 hook H1] Autorun arm / settle / fire -----

        /// <summary>
        /// Per-frame autorun poll (design "H1 - Autorun batch trigger"). Cheap and inert
        /// when the process is not armed: the first line early-returns on the cached
        /// !autorunConfig.Enabled bool. When armed it counts scene-settle frames, logs the
        /// stuck condition while waiting, and fires the runner exactly once per process
        /// through the shared EnsureRunner factory once the scene settles and the
        /// single-fire gate is clear.
        /// </summary>
        private void UpdateAutorun()
        {
            if (!autorunParsed || !autorunConfig.Enabled) return; // inert when unarmed
            if (autorunConsumedForProcess || autorunMultiDriving) return; // selector already run/running
            if (autorunFiredThisScene) return; // fired this scene entry; wait for re-arm

            GameScenes scene = HighLogic.LoadedScene;
            bool gameNonNull = HighLogic.CurrentGame != null;
            bool saveLoaded = !string.IsNullOrEmpty(HighLogic.SaveFolder);
            bool flightReady = FlightGlobals.ready;
            Vessel av = FlightGlobals.ActiveVessel;
            bool vesselNonNull = av != null;
            bool vesselPacked = av != null && av.packed;

            // Crash-reconcile gate (G3): hold fire until reconcile fully clears, so the
            // autorun baseline is captured against the reverted save, not a half-reverted
            // one (edge 6). reconcilePending feeds SceneSettleDecision as the negation.
            bool reconcilePending = !AutorunHooks.ReconcileGateClear(
                ParsekScenario.CrashReconcileInProgress, MarkerWouldReconcile());

            // Reuse the pure predicate for the BASE conditions (everything but the frame
            // count) by passing a satisfied frame count; the real frame count drives the
            // actual fire below. This keeps the settle-counter advance/reset in lock-step
            // with the fire gate instead of duplicating the condition logic.
            bool baseQualifies = AutorunHooks.SceneSettleDecision(
                scene, gameNonNull, saveLoaded, flightReady, vesselNonNull, vesselPacked,
                settleFrames: AutorunSettleTarget, settleTarget: AutorunSettleTarget,
                reconcilePending);
            if (baseQualifies) autorunSettleFrames++;
            else autorunSettleFrames = 0;

            MaybeWarnNoScene(gameNonNull, saveLoaded);

            ParsekLog.VerboseRateLimited(Tag, "autorun-settle",
                $"autorun armed, waiting for settle: scene={scene} game={gameNonNull} "
                + $"save={saveLoaded} flightReady={flightReady} vessel={vesselNonNull} "
                + $"packed={vesselPacked} settleFrames={autorunSettleFrames}/{AutorunSettleTarget} "
                + $"reconcilePending={reconcilePending}", 1f);

            if (!baseQualifies || autorunSettleFrames < AutorunSettleTarget) return;

            // Scene settled. Build the runner through the shared lazy factory (autorun
            // opens no window, so the runner is otherwise null here) then check the
            // single-fire gate (edge 5, 8; G1 command-seam runner check).
            EnsureRunner();
            bool commandRunnerRunning = ParsekTestCommandAddon.CommandRunnerIsRunningForGating;
            if (!AutorunHooks.AutorunFireGate(
                    runner.IsRunning, commandRunnerRunning,
                    autorunConsumedForProcess, autorunFiredThisScene))
            {
                ParsekLog.Verbose(Tag,
                    $"autorun fire blocked: runnerRunning={runner.IsRunning} "
                    + $"commandRunner={commandRunnerRunning} consumed={autorunConsumedForProcess} "
                    + $"firedThisScene={autorunFiredThisScene}");
                return;
            }

            FireAutorun();
        }

        /// <summary>
        /// Invokes the runner for the parsed selector (design "H1 - Fire"). Sets
        /// autorunFiredThisScene up front so a re-entry this frame cannot double-fire, then
        /// dispatches: "all" -> RunAll; a single category -> RunCategory; multiple
        /// categories -> the sequential driver coroutine. All non-driver paths mark the
        /// process latch so a per-scene re-arm never restarts the selector (edge 8).
        /// </summary>
        private void FireAutorun()
        {
            autorunFiredThisScene = true;
            bool exitArmed = autorunConfig.ExitArmed;

            if (autorunConfig.IsAll)
            {
                int eligible = runner.Tests.Count;
                ParsekLog.Info(Tag,
                    $"autorun FIRING: selector=all scene={HighLogic.LoadedScene} eligibleCount={eligible}");
                runner.MarkNextBatchAutorun(exitArmed);
                runner.RunAll();
                autorunConsumedForProcess = true;
                return;
            }

            if (autorunConfig.Categories.Count == 1)
            {
                string cat = autorunConfig.Categories[0];
                int discovered = runner.Tests.Count(t => t.Category == cat);
                if (discovered == 0)
                    ParsekLog.Warn(Tag, $"autorun category '{cat}' matched 0 discovered tests");
                ParsekLog.Info(Tag,
                    $"autorun FIRING: selector={cat} scene={HighLogic.LoadedScene} eligibleCount={discovered}");
                runner.MarkNextBatchAutorun(exitArmed);
                runner.RunCategory(cat);
                autorunConsumedForProcess = true;
                return;
            }

            // Multi-category (Count > 1): the sequential driver owns the run.
            autorunConsumedForProcess = true;
            autorunMultiDriving = true;
            StartCoroutine(AutorunMultiCategoryDriver(autorunConfig.Categories, exitArmed));
        }

        /// <summary>
        /// Runs a multi-category selector by issuing RunCategory per token SEQUENTIALLY
        /// (design "H1 - Multi-category selector"), waiting for !runner.IsRunning between
        /// tokens so each category captures + tears down its own campaign-isolation
        /// baseline independently. Each token emits its own BATCH_COMPLETE line (via the
        /// runner's H3); the driver aggregates the per-category union counts and emits a
        /// final category=multi:&lt;count&gt; summary line. Per-token batches are never
        /// exit-armed (that would quit KSP mid-run); the aggregate exit is wired by H2 in
        /// P5.1.
        /// </summary>
        private IEnumerator AutorunMultiCategoryDriver(IReadOnlyList<string> categories, bool exitArmed)
        {
            ParsekLog.Info(Tag,
                $"autorun multi-category: running {categories.Count} tokens sequentially: "
                + $"[{string.Join(",", categories)}]");

            int total = 0, passed = 0, failed = 0, skipped = 0, batches = 0;
            foreach (string cat in categories)
            {
                while (runner.IsRunning) yield return null;

                int discovered = runner.Tests.Count(t => t.Category == cat);
                if (discovered == 0)
                    ParsekLog.Warn(Tag, $"autorun category '{cat}' matched 0 discovered tests");
                ParsekLog.Info(Tag,
                    $"autorun FIRING: selector={cat} scene={HighLogic.LoadedScene} eligibleCount={discovered}");

                // Per-token batches never carry the exit arm (H2 must not quit mid-run).
                runner.MarkNextBatchAutorun(false);
                runner.RunCategory(cat);

                while (runner.IsRunning) yield return null;

                // Accumulate this category's union counts from its tests' final statuses.
                var catTests = runner.Tests.Where(t => t.Category == cat).ToList();
                passed += catTests.Count(t => t.Status == TestStatus.Passed);
                failed += catTests.Count(t => t.Status == TestStatus.Failed);
                skipped += catTests.Count(t => t.Status == TestStatus.Skipped);
                total += catTests.Count(t => t.Status != TestStatus.NotRun);
                batches++;
            }

            ParsekLog.Info(Tag, $"autorun multi-category complete: {batches} batches");
            ParsekLog.Info(Tag, InGameTestRunner.FormatBatchCompleteLine(
                total, passed, failed, skipped, $"multi:{batches}",
                HighLogic.LoadedScene.ToString()));

            autorunMultiDriving = false;

            // [M-A3 hook H2] Multi-category exit is driven HERE (not by per-token H2) so all
            // per-token BATCH_COMPLETE lines + the aggregate line are durable BEFORE the
            // quit. Per-token batches are marked non-exit above, so the runner-side H2 never
            // fires mid-run; the driver owns the single aggregate exit via the shared quit
            // seam so ordering (aggregate line before quit) holds.
            if (exitArmed)
                InGameTestRunner.PerformAutorunExit(
                    InGameTestRunner.QuitCallbackForTesting, HighLogic.LoadedScene.ToString());
        }

        /// <summary>
        /// Resolves whether the live TestBatchMarker would trigger a recovery reconcile on
        /// load (design "H1 - Interaction with crash-reconcile"; feeds ReconcileGateClear).
        /// Reads the same pure ShouldReconcileOnLoad decision OnLoad uses, so H1 holds fire
        /// while a killed prior batch's marker still demands a revert. False (gate open)
        /// when there is no scenario or no marker.
        /// </summary>
        private bool MarkerWouldReconcile()
        {
            ParsekScenario scenario = ParsekScenario.Instance;
            TestBatchMarker marker = scenario != null ? scenario.ActiveTestBatchMarker : null;
            if (marker == null) return false;
            string reason;
            return TestBatchMarker.ShouldReconcileOnLoad(
                marker, ParsekProcess.ProcessSessionId.ToString("N"),
                HighLogic.SaveFolder, out reason);
        }

        /// <summary>
        /// One-shot WARN after a bounded wait when the process is armed but no game scene
        /// with a loaded save ever settled (edge 7): the orchestrator's boot flag failed to
        /// auto-load a save, so H1 will never fire and the orchestrator's timeout will
        /// eventually reap the process. Logging it explains the eventual kill. The timer
        /// resets whenever a save is present.
        /// </summary>
        private void MaybeWarnNoScene(bool gameNonNull, bool saveLoaded)
        {
            if (gameNonNull && saveLoaded)
            {
                autorunNoSceneWarned = false;
                autorunArmedRealtime = Time.realtimeSinceStartup;
                return;
            }
            if (autorunNoSceneWarned) return;
            if (Time.realtimeSinceStartup - autorunArmedRealtime >= AutorunNoSceneWarnSeconds)
            {
                autorunNoSceneWarned = true;
                ParsekLog.Warn(Tag,
                    $"autorun armed but no game scene settled within {AutorunNoSceneWarnSeconds}s; "
                    + "still waiting for orchestrator save load");
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
