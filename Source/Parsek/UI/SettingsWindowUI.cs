using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Settings window extracted from ParsekUI.
    /// Manages all Parsek settings: recording, looping, ghosts, diagnostics, sampling, data management.
    /// </summary>
    internal class SettingsWindowUI
    {
        private readonly ParsekUI parentUI;

        private bool showSettingsWindow;
        private Rect settingsWindowRect;
        private bool settingsWindowHasInputLock;
        private const string SettingsInputLockId = "Parsek_SettingsWindow";
        private Rect lastSettingsWindowRect;

        // Auto-loop editing
        private string settingsAutoLoopText = "";
        private bool settingsAutoLoopEditing;
        private Rect settingsAutoLoopEditRect;

        // Camera cutoff editing
        private bool settingsCameraCutoffEditing;
        private string settingsCameraCutoffText = "";
        private Rect settingsCameraCutoffEditRect;

        private const float SpacingSmall = 3f;
        private const float SpacingLarge = 10f;

        public bool IsOpen
        {
            get { return showSettingsWindow; }
            set { showSettingsWindow = value; }
        }

        internal SettingsWindowUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
        }

        public void DrawIfOpen(Rect mainWindowRect)
        {
            if (!showSettingsWindow)
            {
                ReleaseInputLock();
                return;
            }

            if (settingsWindowRect.width < 1f)
            {
                settingsWindowRect = new Rect(
                    mainWindowRect.x + mainWindowRect.width + 10,
                    mainWindowRect.y,
                    280, 10);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI", $"Settings window initial position: x={settingsWindowRect.x.ToString("F0", ic)} y={settingsWindowRect.y.ToString("F0", ic)}");
            }

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            // Reset height each frame so GUILayout auto-sizes to content
            settingsWindowRect.height = 10;
            settingsWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekSettings".GetHashCode(),
                settingsWindowRect,
                DrawSettingsWindow,
                "Parsek \u2014 Settings",
                opaqueWindowStyle,
                GUILayout.Width(280)
            );
            parentUI.LogWindowPosition("Settings", ref lastSettingsWindowRect, settingsWindowRect);

            if (settingsWindowRect.Contains(Event.current.mousePosition))
            {
                if (!settingsWindowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, SettingsInputLockId);
                    settingsWindowHasInputLock = true;
                }
            }
            else
            {
                ReleaseInputLock();
            }
        }

        internal void ReleaseInputLock()
        {
            if (!settingsWindowHasInputLock) return;
            InputLockManager.RemoveControlLock(SettingsInputLockId);
            settingsWindowHasInputLock = false;
        }

        private void CommitAutoLoopEdit(ParsekSettings s)
        {
            double parsed;
            if (ParsekUI.TryParseLoopInput(settingsAutoLoopText, s.AutoLoopDisplayUnit, out parsed) && parsed >= 0)
            {
                s.autoLoopIntervalSeconds = (float)ParsekUI.ConvertToSeconds(parsed, s.AutoLoopDisplayUnit);
                ParsekLog.Info("UI",
                    $"Setting changed: autoLoopIntervalSeconds={s.autoLoopIntervalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}s");
            }
            else
            {
                ParsekLog.Warn("UI",
                    $"Auto-loop settings edit rejected: invalid or negative input '{settingsAutoLoopText}' " +
                    $"for unit {ParsekUI.UnitLabel(s.AutoLoopDisplayUnit)}");
            }
            settingsAutoLoopEditing = false;
            settingsAutoLoopEditRect = default;
            GUIUtility.keyboardControl = 0;
        }

        private void CommitCameraCutoffEdit(ParsekSettings s)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            if (float.TryParse(settingsCameraCutoffText, System.Globalization.NumberStyles.Float,
                    ic, out float parsed) && parsed >= 10f && parsed <= 10000f)
            {
                s.ghostCameraCutoffKm = parsed;
                ParsekLog.Info("UI",
                    $"Setting changed: ghostCameraCutoffKm={s.ghostCameraCutoffKm.ToString("F0", ic)}");
            }
            settingsCameraCutoffEditing = false;
            settingsCameraCutoffEditRect = default;
            GUIUtility.keyboardControl = 0;
        }

        private void DrawSettingsWindow(int windowID)
        {
            var s = ParsekSettings.Current;
            if (s == null)
            {
                GUILayout.Label("Settings unavailable (no active game).");
                if (GUILayout.Button("Close"))
                    showSettingsWindow = false;
                GUI.DragWindow();
                return;
            }

            // Click outside active settings edit field → commit
            if (Event.current.type == EventType.MouseDown)
            {
                if (settingsAutoLoopEditing && settingsAutoLoopEditRect.width > 0
                    && !settingsAutoLoopEditRect.Contains(Event.current.mousePosition))
                    CommitAutoLoopEdit(s);
                if (settingsCameraCutoffEditing && settingsCameraCutoffEditRect.width > 0
                    && !settingsCameraCutoffEditRect.Contains(Event.current.mousePosition))
                    CommitCameraCutoffEdit(s);
            }

            DrawRecordingSettings(s);
            GUILayout.Space(SpacingSmall);
            DrawLoopingSettings(s);
            GUILayout.Space(SpacingSmall);
            DrawGhostSettings(s);
            GUILayout.Space(SpacingSmall);
            DrawDiagnosticsSettings(s);
            GUILayout.Space(SpacingSmall);
            DrawSamplingSettings(s);
            GUILayout.Space(SpacingSmall);
            DrawDataManagementSettings(s);

            GUILayout.Space(SpacingLarge);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Defaults"))
            {
                ParsekLog.Verbose("UI", "Settings Defaults button clicked");
                s.autoRecordOnLaunch = true;
                s.autoRecordOnEva = true;
                s.autoMerge = false;
                s.verboseLogging = true;
                s.maxSampleInterval = 3.0f;
                s.velocityDirThreshold = 2.0f;
                s.speedChangeThreshold = 5.0f;
                s.autoLoopIntervalSeconds = 10.0f;
                s.autoLoopTimeUnit = 0;
                s.ghostCapEnabled = false;
                s.ghostCapZone1Reduce = 8;
                s.ghostCapZone1Despawn = 15;
                s.ghostCapZone2Simplify = 20;
                s.ghostCameraCutoffKm = 300f;
                GhostSoftCapManager.Enabled = false;
                GhostSoftCapManager.ApplySettings(8, 15, 20);
                settingsAutoLoopEditing = false;
                settingsCameraCutoffEditing = false;
                ParsekLog.Info("UI", "Settings reset to defaults");
            }
            if (GUILayout.Button("Close"))
            {
                showSettingsWindow = false;
                ParsekLog.Verbose("UI", "Settings window closed via button");
            }
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(GUI.tooltip))
            {
                GUILayout.Space(SpacingSmall);
                GUILayout.Label(GUI.tooltip, GUI.skin.box);
            }

            GUI.DragWindow();
        }

        private void DrawRecordingSettings(ParsekSettings s)
        {
            GUILayout.Label("Recording", GUI.skin.box);
            bool autoRecordOnLaunch = GUILayout.Toggle(s.autoRecordOnLaunch,
                new GUIContent(" Auto-record on launch", "Start recording when a vessel leaves the pad or runway"));
            if (autoRecordOnLaunch != s.autoRecordOnLaunch)
            {
                s.autoRecordOnLaunch = autoRecordOnLaunch;
                ParsekLog.Info("UI", $"Setting changed: autoRecordOnLaunch={s.autoRecordOnLaunch}");
            }

            bool autoRecordOnEva = GUILayout.Toggle(s.autoRecordOnEva,
                new GUIContent(" Auto-record on EVA", "Start recording when a kerbal goes EVA from the pad"));
            if (autoRecordOnEva != s.autoRecordOnEva)
            {
                s.autoRecordOnEva = autoRecordOnEva;
                ParsekLog.Info("UI", $"Setting changed: autoRecordOnEva={s.autoRecordOnEva}");
            }

            bool autoMerge = GUILayout.Toggle(s.autoMerge,
                new GUIContent(" Auto-merge recordings", "When off, a confirmation dialog appears after each recording"));
            if (autoMerge != s.autoMerge)
            {
                s.autoMerge = autoMerge;
                ParsekLog.Info("UI", $"Setting changed: autoMerge={s.autoMerge}");
            }
        }

        private void DrawLoopingSettings(ParsekSettings s)
        {
            GUILayout.Label("Looping", GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Auto-loop every",
                "Default loop interval for recordings set to 'auto' unit"), GUILayout.Width(100));
            {
                if (!settingsAutoLoopEditing)
                {
                    double displayVal = ParsekUI.ConvertFromSeconds(s.autoLoopIntervalSeconds, s.AutoLoopDisplayUnit);
                    string displayText = ParsekUI.FormatLoopValue(displayVal, s.AutoLoopDisplayUnit);
                    GUI.SetNextControlName("AutoLoopEdit");
                    string newText = GUILayout.TextField(displayText, GUILayout.Width(45));
                    if (GUI.GetNameOfFocusedControl() == "AutoLoopEdit")
                    {
                        settingsAutoLoopText = newText;
                        settingsAutoLoopEditing = true;
                        settingsAutoLoopEditRect = GUILayoutUtility.GetLastRect();
                        ParsekLog.Verbose("UI",
                            $"Auto-loop settings edit started: value='{newText}' unit={ParsekUI.UnitLabel(s.AutoLoopDisplayUnit)}");
                    }
                }
                else
                {
                    bool submitAutoLoop = Event.current.type == EventType.KeyDown &&
                        (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

                    GUI.SetNextControlName("AutoLoopEdit");
                    string newText = GUILayout.TextField(settingsAutoLoopText, GUILayout.Width(45));
                    settingsAutoLoopEditRect = GUILayoutUtility.GetLastRect();
                    if (newText != settingsAutoLoopText)
                        settingsAutoLoopText = newText;

                    if (submitAutoLoop)
                    {
                        CommitAutoLoopEdit(s);
                        Event.current.Use();
                    }
                }

                if (GUILayout.Button(ParsekUI.UnitLabel(s.AutoLoopDisplayUnit), GUILayout.Width(40)))
                {
                    if (settingsAutoLoopEditing)
                        CommitAutoLoopEdit(s);
                    s.AutoLoopDisplayUnit = ParsekUI.CycleDisplayUnit(s.AutoLoopDisplayUnit);
                    ParsekLog.Info("UI", $"Setting changed: autoLoopDisplayUnit={s.AutoLoopDisplayUnit}");
                }
            }
            GUILayout.EndHorizontal();
        }

        private void DrawGhostSettings(ParsekSettings s)
        {
            GUILayout.Label("Ghosts", GUI.skin.box);

            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Camera cutoff",
                "Watch mode auto-exits when ghost exceeds this distance from the active vessel"),
                GUILayout.Width(85));
            {
                if (!settingsCameraCutoffEditing)
                {
                    string displayText = s.ghostCameraCutoffKm.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                    GUI.SetNextControlName("CameraCutoffEdit");
                    string newText = GUILayout.TextField(displayText, GUILayout.Width(45));
                    if (GUI.GetNameOfFocusedControl() == "CameraCutoffEdit")
                    {
                        settingsCameraCutoffText = newText;
                        settingsCameraCutoffEditing = true;
                        settingsCameraCutoffEditRect = GUILayoutUtility.GetLastRect();
                    }
                }
                else
                {
                    bool submitCutoff = Event.current.type == EventType.KeyDown &&
                        (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

                    GUI.SetNextControlName("CameraCutoffEdit");
                    string newText = GUILayout.TextField(settingsCameraCutoffText, GUILayout.Width(45));
                    settingsCameraCutoffEditRect = GUILayoutUtility.GetLastRect();
                    if (newText != settingsCameraCutoffText)
                        settingsCameraCutoffText = newText;

                    if (submitCutoff)
                    {
                        CommitCameraCutoffEdit(s);
                        Event.current.Use();
                    }
                }
            }
            GUILayout.Label("km");
            GUILayout.EndHorizontal();

            GUILayout.Space(SpacingSmall);

            bool enabled = GUILayout.Toggle(s.ghostCapEnabled,
                new GUIContent(" Enable soft caps",
                    "Limit ghost count per distance zone to reduce rendering load"));
            if (enabled != s.ghostCapEnabled)
            {
                s.ghostCapEnabled = enabled;
                GhostSoftCapManager.Enabled = enabled;
                ParsekLog.Info("UI", $"Ghost soft caps {(enabled ? "enabled" : "disabled")}");
            }

            // Always draw slider controls even when caps are disabled — IMGUI requires
            // identical control counts in Layout and Repaint passes. The toggle click changes
            // ghostCapEnabled between passes, causing a mismatch if controls are conditionally
            // skipped. GUI.enabled grays out sliders without affecting layout. (Bug #217)
            bool prevEnabled = GUI.enabled;
            GUI.enabled = s.ghostCapEnabled;

            DrawGhostCapSlider("Zone 1 reduce", "Nearby ghosts above this count get reduced fidelity",
                ref s.ghostCapZone1Reduce, 2, 30, "ghostCap.zone1Reduce", s);
            DrawGhostCapSlider("Zone 1 despawn", "Nearby ghosts above this count get despawned (lowest priority first)",
                ref s.ghostCapZone1Despawn, 5, 50, "ghostCap.zone1Despawn", s);
            DrawGhostCapSlider("Zone 2 simplify", "Distant ghosts above this count get simplified to orbit lines",
                ref s.ghostCapZone2Simplify, 5, 60, "ghostCap.zone2Simplify", s);

            GUI.enabled = prevEnabled;

            if (s.ghostCapEnabled && s.ghostCapZone1Reduce >= s.ghostCapZone1Despawn)
            {
                s.ghostCapZone1Reduce = System.Math.Max(2, s.ghostCapZone1Despawn - 1);
                GhostSoftCapManager.ApplySettings(
                    s.ghostCapZone1Reduce, s.ghostCapZone1Despawn, s.ghostCapZone2Simplify);
                ParsekLog.Info("UI",
                    $"Clamped ghostCapZone1Reduce={s.ghostCapZone1Reduce} to stay below " +
                    $"ghostCapZone1Despawn={s.ghostCapZone1Despawn}");
            }
        }

        private void DrawDiagnosticsSettings(ParsekSettings s)
        {
            GUILayout.Label("Diagnostics", GUI.skin.box);
            bool verboseLogging = GUILayout.Toggle(s.verboseLogging, " Verbose logging (development default)");
            if (verboseLogging != s.verboseLogging)
            {
                s.verboseLogging = verboseLogging;
                ParsekLog.Info("UI", $"Setting changed: verboseLogging={s.verboseLogging}");
            }

            if (GUILayout.Button(new GUIContent("In-Game Test Runner",
                "Run runtime tests to verify ghost spawning, playback, and visuals.\nAlso available via Ctrl+Shift+T in any scene.")))
            {
                parentUI.ToggleTestRunner();
            }
        }

        private void DrawSamplingSettings(ParsekSettings s)
        {
            GUILayout.Label("Sampling", GUI.skin.box);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Max interval: {s.maxSampleInterval:F1}s", GUILayout.Width(140));
            float maxSampleInterval = GUILayout.HorizontalSlider(s.maxSampleInterval, 1f, 10f);
            if (Mathf.Abs(maxSampleInterval - s.maxSampleInterval) > 0.0001f)
            {
                s.maxSampleInterval = maxSampleInterval;
                ParsekLog.VerboseRateLimited("UI", "sampling.maxSampleInterval",
                    $"Setting changed: maxSampleInterval={s.maxSampleInterval:F1}s", 1.0);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Direction: {s.velocityDirThreshold:F1}\u00b0", GUILayout.Width(140));
            float velocityDirThreshold = GUILayout.HorizontalSlider(s.velocityDirThreshold, 0.5f, 10f);
            if (Mathf.Abs(velocityDirThreshold - s.velocityDirThreshold) > 0.0001f)
            {
                s.velocityDirThreshold = velocityDirThreshold;
                ParsekLog.VerboseRateLimited("UI", "sampling.velocityDirThreshold",
                    $"Setting changed: velocityDirThreshold={s.velocityDirThreshold:F1}", 1.0);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Speed: {s.speedChangeThreshold:F0}%", GUILayout.Width(140));
            float speedChangeThreshold = GUILayout.HorizontalSlider(s.speedChangeThreshold, 1f, 20f);
            if (Mathf.Abs(speedChangeThreshold - s.speedChangeThreshold) > 0.0001f)
            {
                s.speedChangeThreshold = speedChangeThreshold;
                ParsekLog.VerboseRateLimited("UI", "sampling.speedChangeThreshold",
                    $"Setting changed: speedChangeThreshold={s.speedChangeThreshold:F0}%", 1.0);
            }
            GUILayout.EndHorizontal();
        }

        private void DrawGhostCapSlider(string label, string tooltip, ref int value,
            int min, int max, string logKey, ParsekSettings s)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(
                new GUIContent($"{label}: {value}", tooltip),
                GUILayout.Width(140));
            int newValue = Mathf.RoundToInt(
                GUILayout.HorizontalSlider(value, min, max));
            if (newValue != value)
            {
                value = newValue;
                GhostSoftCapManager.ApplySettings(
                    s.ghostCapZone1Reduce, s.ghostCapZone1Despawn, s.ghostCapZone2Simplify);
                ParsekLog.VerboseRateLimited("UI", logKey,
                    $"Setting changed: {logKey}={value}", 1.0);
            }
            GUILayout.EndHorizontal();
        }

        private void DrawDataManagementSettings(ParsekSettings s)
        {
            GUILayout.Label("Data Management", GUI.skin.box);

            int committedCount = RecordingStore.CommittedRecordings.Count;
            int milestoneCount = MilestoneStore.Milestones.Count;

            GUI.enabled = committedCount > 0;
            if (GUILayout.Button($"Wipe All Recordings ({committedCount})"))
                parentUI.ShowWipeRecordingsConfirmation(committedCount);
            GUI.enabled = true;

            GUI.enabled = milestoneCount > 0;
            if (GUILayout.Button($"Wipe All Game Actions ({milestoneCount})"))
                parentUI.ShowWipeActionsConfirmation(milestoneCount);
            GUI.enabled = true;
        }
    }
}
