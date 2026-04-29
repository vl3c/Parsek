using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Settings window extracted from ParsekUI.
    /// Manages all Parsek settings: recording, looping, ghosts, diagnostics, sampling density, data management.
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

        private const float SpacingSmall = 3f;
        private const float SpacingLarge = 10f;
        private GUIStyle zeroHeightLabelStyle;
        private GUIStyle wrappedTooltipStyle;

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
                    280, 600);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI", $"Settings window initial position: x={settingsWindowRect.x.ToString("F0", ic)} y={settingsWindowRect.y.ToString("F0", ic)}");
            }

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;
            // Pass both Width+Height like every other Parsek window so the shared
            // opaqueWindowStyle padding renders identically (the previous height=10
            // reset + Width-only call caused the title-bar spacing to look off).
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                settingsWindowRect = ClickThruBlocker.GUILayoutWindow(
                    "ParsekSettings".GetHashCode(),
                    settingsWindowRect,
                    DrawSettingsWindow,
                    "Parsek - Settings",
                    opaqueWindowStyle,
                    GUILayout.Width(settingsWindowRect.width),
                    GUILayout.Height(settingsWindowRect.height)
                );
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
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

        internal bool IsMouseOverOpenWindow(Vector2 mousePosition)
        {
            return ParsekUI.IsPointerOverOpenWindow(
                showSettingsWindow,
                settingsWindowRect,
                mousePosition);
        }

        private void CommitAutoLoopEdit(ParsekSettings s)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            if (SettingsWindowPresentation.TryResolveAutoLoopEdit(
                settingsAutoLoopText,
                s.AutoLoopDisplayUnit,
                out SettingsWindowPresentation.AutoLoopEditResolution resolution))
            {
                // #381: defensively clamp to MinCycleDuration — matches per-recording UI.
                if (resolution.WasClamped)
                {
                    ParsekLog.Info("UI",
                        $"Auto-launch period clamped from {resolution.RequestedSeconds.ToString("F1", ic)}s to " +
                        $"{LoopTiming.MinCycleDuration.ToString("F1", ic)}s (MinCycleDuration)");
                }
                s.autoLoopIntervalSeconds = resolution.AppliedSeconds;
                ParsekLog.Info("UI",
                    $"Setting changed: autoLoopIntervalSeconds={s.autoLoopIntervalSeconds.ToString("F1", ic)}s");
            }
            else
            {
                ParsekLog.Warn("UI",
                    $"Auto-launch period edit rejected: invalid or negative input '{settingsAutoLoopText}' " +
                    $"for unit {ParsekUI.UnitLabel(s.AutoLoopDisplayUnit)}");
            }
            settingsAutoLoopEditing = false;
            settingsAutoLoopEditRect = default;
            GUIUtility.keyboardControl = 0;
        }

        private void DrawSettingsWindow(int windowID)
        {
            EnsureLayoutStyles();
            // Breathing room below the title bar — matches Timeline's visual spacing.
            GUILayout.Space(5);
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
                SettingsWindowPresentation.SettingsDefaults defaults =
                    SettingsWindowPresentation.BuildDefaults();
                s.autoRecordOnLaunch = defaults.AutoRecordOnLaunch;
                s.autoRecordOnEva = defaults.AutoRecordOnEva;
                s.autoRecordOnFirstModificationAfterSwitch =
                    defaults.AutoRecordOnFirstModificationAfterSwitch;
                s.autoMerge = defaults.AutoMerge;
                s.verboseLogging = defaults.VerboseLogging;
                s.writeReadableSidecarMirrors = defaults.WriteReadableSidecarMirrors;
                s.SamplingDensityLevel = defaults.SamplingDensityLevel;
                s.autoLoopIntervalSeconds = defaults.AutoLoopIntervalSeconds;
                s.AutoLoopDisplayUnit = defaults.AutoLoopDisplayUnit;
                s.showGhostsInTrackingStation = defaults.ShowGhostsInTrackingStation;
                ParsekSettingsPersistence.RecordReadableSidecarMirrors(s.writeReadableSidecarMirrors);
                ParsekSettingsPersistence.RecordShowGhostsInTrackingStation(s.showGhostsInTrackingStation);
                RecordingStore.ReconcileReadableSidecarMirrorsForKnownRecordings();
                settingsAutoLoopEditing = false;
                ParsekLog.Info("UI", "Settings reset to defaults");
            }
            if (GUILayout.Button("Close"))
            {
                showSettingsWindow = false;
                ParsekLog.Verbose("UI", "Settings window closed via button");
            }
            GUILayout.EndHorizontal();

            string tooltip = GUI.tooltip ?? "";
            GUILayout.Space(tooltip.Length > 0 ? SpacingSmall : 0f);
            GUILayout.Label(
                tooltip.Length > 0 ? tooltip : string.Empty,
                tooltip.Length > 0 ? wrappedTooltipStyle : zeroHeightLabelStyle,
                tooltip.Length > 0 ? GUILayout.ExpandWidth(true) : GUILayout.Height(0f));

            GUI.DragWindow();
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

            if (wrappedTooltipStyle == null)
            {
                wrappedTooltipStyle = new GUIStyle(GUI.skin.box)
                {
                    wordWrap = true
                };
            }
        }

        private void DrawRecordingSettings(ParsekSettings s)
        {
            GUILayout.Label("Recording", parentUI.GetSectionHeaderStyle());
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

            bool autoRecordOnFirstModificationAfterSwitch = GUILayout.Toggle(
                s.autoRecordOnFirstModificationAfterSwitch,
                new GUIContent(
                    " Auto-record on first modification after switch",
                    "Arm after switching to a real vessel and start recording on the first meaningful physical change"));
            if (autoRecordOnFirstModificationAfterSwitch != s.autoRecordOnFirstModificationAfterSwitch)
            {
                s.autoRecordOnFirstModificationAfterSwitch = autoRecordOnFirstModificationAfterSwitch;
                ParsekLog.Info("UI",
                    $"Setting changed: autoRecordOnFirstModificationAfterSwitch={s.autoRecordOnFirstModificationAfterSwitch}");
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
            GUILayout.Label("Looping", parentUI.GetSectionHeaderStyle());
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Auto-launch every",
                "Default launch-to-launch period (seconds) for recordings set to 'auto' unit. Overlap occurs naturally when the period is shorter than the recording duration."),
                GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
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
            GUILayout.Label("Ghosts", parentUI.GetSectionHeaderStyle());

            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Ghost audio",
                "Volume multiplier for ghost vessel audio (engines, RCS, events). 0% = muted."),
                GUILayout.Width(85));
            float newAudioVol = GUILayout.HorizontalSlider(s.ghostAudioVolume, 0f, 1f);
            GUILayout.Label(
                UnityEngine.Mathf.RoundToInt(newAudioVol * 100f).ToString() + "%",
                GUILayout.Width(35));
            GUILayout.EndHorizontal();
            if (UnityEngine.Mathf.Abs(newAudioVol - s.ghostAudioVolume) > 0.001f)
            {
                s.ghostAudioVolume = newAudioVol;
                ParsekLog.VerboseRateLimited("UI", "ghostAudioVolume",
                    $"Ghost audio volume set to {newAudioVol:F2}", 1.0);
            }

            GUILayout.Space(SpacingSmall);

            bool showGhostsTS = GUILayout.Toggle(s.showGhostsInTrackingStation,
                new GUIContent(" Show ghosts in Tracking Station",
                    "When off, Parsek ghosts are hidden from the tracking station vessel list and map view"));
            if (showGhostsTS != s.showGhostsInTrackingStation)
            {
                s.showGhostsInTrackingStation = showGhostsTS;
                // Persist to the external settings store so the value survives
                // rewind, quickload, and KSP restart — same treatment as
                // writeReadableSidecarMirrors. Without this,
                // the user's choice reverts to the GameParameters-default on the
                // next load.
                ParsekSettingsPersistence.RecordShowGhostsInTrackingStation(showGhostsTS);
                ParsekLog.Info("UI", $"Setting changed: showGhostsInTrackingStation={showGhostsTS}");
            }
        }

        private void DrawDiagnosticsSettings(ParsekSettings s)
        {
            GUILayout.Label("Diagnostics", parentUI.GetSectionHeaderStyle());
            bool verboseLogging = GUILayout.Toggle(s.verboseLogging, " Verbose logging (development default)");
            if (verboseLogging != s.verboseLogging)
            {
                s.verboseLogging = verboseLogging;
                ParsekLog.Info("UI", $"Setting changed: verboseLogging={s.verboseLogging}");
            }

            bool writeReadableSidecarMirrors = GUILayout.Toggle(s.writeReadableSidecarMirrors,
                new GUIContent(" Write readable sidecar mirrors",
                    "Also write human-readable .txt mirrors of .prec and snapshot sidecars for debugging and binary/text comparison"));
            if (writeReadableSidecarMirrors != s.writeReadableSidecarMirrors)
            {
                s.writeReadableSidecarMirrors = writeReadableSidecarMirrors;
                ParsekSettingsPersistence.RecordReadableSidecarMirrors(s.writeReadableSidecarMirrors);
                RecordingStore.ReconcileReadableSidecarMirrorsForKnownRecordings();
                ParsekLog.Info("UI", $"Setting changed: writeReadableSidecarMirrors={s.writeReadableSidecarMirrors}");
            }

            if (GUILayout.Button(new GUIContent("In-Game Test Runner",
                "Run runtime tests to verify ghost spawning, playback, and visuals.\nAlso available via Ctrl+Shift+T in any scene.")))
            {
                parentUI.ToggleTestRunner();
            }

            if (GUILayout.Button(new GUIContent("Run Diagnostics Report",
                "Compute full diagnostics snapshot and dump report to KSP.log")))
            {
                ParsekLog.Info("UI", "Run Diagnostics Report button clicked");
                DiagnosticsComputation.RunDiagnosticsReport();
            }

            // Phase 14 of Rewind-to-Staging (design §7.28) plus stable-leaf
            // §11.6: live rewind-point disk-usage and retained-RP buckets.
            // The helper caches for 10s so GUI redraw does not hammer the
            // filesystem or classifier.
            string rpDir = RewindPointDiskUsage.ResolveCurrentSaveDirectory();
            var rpSnap = RewindPointDiskUsage.GetSnapshot(rpDir);
            GUILayout.Label(new GUIContent(
                RewindPointDiskUsage.FormatLine(rpSnap),
                "Total size of rewind-point quicksaves under saves/<save>/Parsek/RewindPoints/. "
                + "Also shows live RP counts split by crashed, stable, and sealed-pending slots. "
                + "Refreshed every 10 seconds or when RP state changes."));
        }

        private void DrawSamplingSettings(ParsekSettings s)
        {
            GUILayout.Label("Recorder Sample Density", parentUI.GetSectionHeaderStyle());

            GUILayout.BeginHorizontal();
            foreach (SamplingDensity level in new[] { SamplingDensity.Low, SamplingDensity.Medium, SamplingDensity.High })
            {
                bool isSelected = s.SamplingDensityLevel == level;
                GUIStyle style = isSelected ? GUI.skin.box : GUI.skin.button;
                if (GUILayout.Button(new GUIContent(ParsekSettings.DensityLabel(level),
                    ParsekSettings.DensityTooltip(level)), style))
                {
                    if (!isSelected)
                    {
                        s.SamplingDensityLevel = level;
                        ParsekLog.Info("UI", $"Setting changed: samplingDensity={level}");
                    }
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(ParsekSettings.DensitySummary(s.SamplingDensityLevel),
                GUI.skin.label);
        }

        private void DrawDataManagementSettings(ParsekSettings s)
        {
            GUILayout.Label("Data Management", parentUI.GetSectionHeaderStyle());

            // [ERS-exempt] reason: the wipe-all button reports the raw count of
            // stored recordings (including NotCommitted / superseded) because the
            // wipe path clears the whole store via RecordingStore.ClearCommitted().
            // ERS would under-count and mislead the user.
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
