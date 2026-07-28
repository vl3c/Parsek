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
        private bool settingsWindowHeightRemeasurePending;
        private bool tooltipShownLastDraw;

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
            //
            // The ONE exception is a pending re-measure (see RequestHeightRemeasure): for
            // that single Layout pass the Height option is dropped so GUILayout sizes the
            // window to whatever content the current UI mode draws, and the measured height
            // comes straight back in the returned rect. The passed rect keeps its old
            // height, so the window chrome is never drawn at a stale size - that, not the
            // Width-only call itself, was what made the old every-frame auto-size look off.
            //
            // Held back while the bottom tooltip box is showing: the mode toggle is the
            // control the mouse rests on right after the click, and measuring then would
            // latch a height that includes a tooltip which disappears the moment the pointer
            // moves - dead space again, just less of it. The request survives, so the fit
            // lands on the first tooltip-free frame.
            bool remeasuring = settingsWindowHeightRemeasurePending && !tooltipShownLastDraw;
            GUILayoutOption[] sizeOptions = remeasuring
                ? new[] { GUILayout.Width(settingsWindowRect.width) }
                : new[]
                {
                    GUILayout.Width(settingsWindowRect.width),
                    GUILayout.Height(settingsWindowRect.height)
                };
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                settingsWindowRect = ClickThruBlocker.GUILayoutWindow(
                    "ParsekSettings".GetHashCode(),
                    settingsWindowRect,
                    DrawSettingsWindow,
                    "Parsek - Settings",
                    opaqueWindowStyle,
                    sizeOptions
                );
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }

            // Consume on the LAYOUT pass only: layout options are ignored on every other
            // event type, so clearing the flag on (say) a Repaint would eat the request
            // without ever re-measuring. Unity sends Layout first each frame, and the mode
            // latch runs in Update, so the request is always honoured on the next frame.
            if (remeasuring && Event.current.type == EventType.Layout)
            {
                settingsWindowHeightRemeasurePending = false;
                var ric = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI",
                    $"Settings window height re-measured: h={settingsWindowRect.height.ToString("F0", ric)}");
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

        /// <summary>
        /// Queues a one-shot content re-measure of the window height. Called from
        /// <c>ParsekUI.OnUiComplexityModeApplied</c> in BOTH directions (design 7.2): Basic
        /// drops the Diagnostics + Sample Density sections and Advanced restores them, so the
        /// stored height - fixed, never player-resized, this window has no resize handle - no
        /// longer matches the content either way. Without this the window keeps its old size:
        /// dead space below the buttons in Basic, clipped content back in Advanced.
        ///
        /// <para>Only the HEIGHT is re-derived; x / y / width are untouched, so the window
        /// does not jump. Safe to call while the window is closed - the request simply waits
        /// for the next draw. Runs from the deferred mode latch (Update), never mid-OnGUI, so
        /// it cannot change an IMGUI control count inside a frame.</para>
        /// </summary>
        internal void RequestHeightRemeasure()
        {
            settingsWindowHeightRemeasurePending = true;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            ParsekLog.Verbose("UI",
                $"Settings window height re-measure requested: storedHeight={settingsWindowRect.height.ToString("F0", ic)} " +
                $"open={showSettingsWindow}");
        }

        /// <summary>
        /// Test seam for the pending re-measure flag. Settable so a headless test can drive
        /// both mode directions without an OnGUI pass to consume the request.
        /// </summary>
        internal bool HeightRemeasurePendingForTesting
        {
            get { return settingsWindowHeightRemeasurePending; }
            set { settingsWindowHeightRemeasurePending = value; }
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

            // Basic / Advanced gating (design 7.1). Read the FRAME-LATCHED mode ONCE per
            // draw pass, never the settings field: the Interface section below hosts the
            // mode toggle itself and draws BEFORE the gated sections, so a raw read would
            // change the control count between this frame's Layout and Repaint passes
            // (`ArgumentException: Getting control N's position in a group with only M
            // controls`). Each hidden section's trailing GUILayout.Space separator lives
            // INSIDE its gate, or Basic shows a double gap where the section used to be.
            // Interface / Recording / Looping / Ghosts / Stock UI / Data Management are
            // visible in both modes and stay unwrapped.
            UiComplexityMode complexity = ParsekUI.AppliedUiComplexityMode;

            DrawInterfaceSettings(s);
            GUILayout.Space(SpacingSmall);
            DrawRecordingSettings(s);
            GUILayout.Space(SpacingSmall);
            DrawLoopingSettings(s);
            GUILayout.Space(SpacingSmall);
            DrawGhostSettings(s);
            GUILayout.Space(SpacingSmall);
            DrawStockUiSettings(s);
            GUILayout.Space(SpacingSmall);

            // Developer instrumentation (verbose logging, the three tracing toggles, the
            // RewindPoints disk-usage readout, and the Settings-launched Test Runner).
            // Hiding the section takes that launcher with it; the SEPARATE global
            // Ctrl+Shift+T `ParsekTestRunnerGlobal` window and its shortcut are never gated
            // in either mode (design 6.3) - the automated-testing harness needs them and
            // never opens this window.
            if (UiSurfaceVisibility.IsVisible(UiSurface.SettingsSectionDiagnostics, complexity))
            {
                DrawDiagnosticsSettings(s);
                GUILayout.Space(SpacingSmall);
            }

            // Recorder fidelity tuning: a wrong value degrades recordings, and the Medium
            // default is correct for normal play (design section 4).
            if (UiSurfaceVisibility.IsVisible(UiSurface.SettingsSectionSampleDensity, complexity))
            {
                DrawSamplingSettings(s);
                GUILayout.Space(SpacingSmall);
            }

            DrawDataManagementSettings(s);

            GUILayout.Space(SpacingLarge);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Defaults"))
            {
                ParsekLog.Verbose("UI", "Settings Defaults button clicked");
                SettingsWindowPresentation.SettingsDefaults defaults =
                    SettingsWindowPresentation.BuildDefaults();
                bool priorShowCommittedFutureOverlays = s.showCommittedFutureOverlays;
                s.autoRecordOnLaunch = defaults.AutoRecordOnLaunch;
                s.autoRecordOnEva = defaults.AutoRecordOnEva;
                s.autoRecordOnFirstModificationAfterSwitch =
                    defaults.AutoRecordOnFirstModificationAfterSwitch;
                s.autoMerge = defaults.AutoMerge;
                s.verboseLogging = defaults.VerboseLogging;
                s.ghostRenderTracing = false;
                s.mapRenderTracing = false;
                s.ledgerTracing = false;
                s.writeReadableSidecarMirrors = defaults.WriteReadableSidecarMirrors;
                s.autoBackupExistingSaves = defaults.AutoBackupExistingSaves;
                s.showRouteLines = defaults.ShowRouteLines;
                s.SamplingDensityLevel = defaults.SamplingDensityLevel;
                s.autoLoopIntervalSeconds = defaults.AutoLoopIntervalSeconds;
                s.AutoLoopDisplayUnit = defaults.AutoLoopDisplayUnit;
                s.showCommittedFutureOverlays = defaults.ShowCommittedFutureOverlays;
                s.blockCommittedActions = defaults.BlockCommittedActions;
                ParsekSettingsPersistence.RecordReadableSidecarMirrors(s.writeReadableSidecarMirrors);
                ParsekSettingsPersistence.RecordAutoBackupExistingSaves(s.autoBackupExistingSaves);
                ParsekSettingsPersistence.RecordShowRouteLines(s.showRouteLines);
                ParsekSettingsPersistence.RecordShowCommittedFutureOverlays(s.showCommittedFutureOverlays);
                ParsekSettingsPersistence.RecordBlockCommittedActions(s.blockCommittedActions);
                ParsekSettingsPersistence.RecordGhostRenderTracing(s.ghostRenderTracing);
                ParsekSettingsPersistence.RecordMapRenderTracing(s.mapRenderTracing);
                ParsekSettingsPersistence.RecordLedgerTracing(s.ledgerTracing);
                // blockCommittedActions needs no controller refresh; click-block patches read it at call time.
                if (s.showCommittedFutureOverlays != priorShowCommittedFutureOverlays)
                    StockUiOverlayController.RefreshOpenScreensAfterSettingsChanged();
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
            // Read by the height re-measure gate in DrawIfOpen (next frame): a measurement
            // taken while this box is up would bake in a height that vanishes with the box.
            tooltipShownLastDraw = tooltip.Length > 0;
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

        /// <summary>
        /// Basic / Advanced UI complexity toggle (design 6.2). Drawn FIRST because it
        /// governs which of the sections below a player even sees once the phase 4-6
        /// gates land. Uses the two-option selected-is-a-box button row of
        /// <see cref="DrawSamplingSettings"/> rather than a checkbox: the two modes are
        /// peers, not an on/off of one of them.
        ///
        /// <para>The click routes through <see cref="ParsekUI.SetUiComplexityMode"/>, the
        /// single setter seam - never a direct write to the settings field.</para>
        /// </summary>
        private void DrawInterfaceSettings(ParsekSettings s)
        {
            GUILayout.Label("Interface", parentUI.GetSectionHeaderStyle());

            // Design 7.2 / edge case 11: Basic hides the Gloops window WITHOUT stopping the
            // recording (philosophy 1), which would strand a running recorder with no
            // reachable Stop / Discard control. Null-safe: in SPACECENTER parentUI.Flight is
            // null and this falls back to "not recording", which is sound - Gloops
            // live-recording state dies with the FLIGHT-scene ParsekFlight.
            //
            // Deliberately NOT logged: this runs every frame the Settings window is open.
            // ParsekUI.SetUiComplexityMode logs the refusal at Info if a click ever gets
            // through, and that seam - not this disable - is the load-bearing half.
            bool gloopsRecording = parentUI.Flight != null && parentUI.Flight.IsGloopsRecording;

            GUILayout.BeginHorizontal();
            foreach (UiComplexityMode mode in new[] { UiComplexityMode.Basic, UiComplexityMode.Advanced })
            {
                bool isSelected = s.UiComplexityModeLevel == mode;
                GUIStyle style = isSelected ? GUI.skin.box : GUI.skin.button;

                // GUI.enabled changes how the control renders and whether it reports a
                // click; it does NOT change the control COUNT, so this is safe to vary
                // between one frame's Layout and Repaint passes.
                bool prevEnabled = GUI.enabled;
                GUI.enabled = prevEnabled && !IsModeOptionDisabled(mode, gloopsRecording);
                bool clicked = GUILayout.Button(
                    new GUIContent(UiComplexityModeLabel(mode), UiComplexityModeTooltip(mode)), style);
                GUI.enabled = prevEnabled;

                if (clicked && !isSelected)
                    ParsekUI.SetUiComplexityMode(mode);
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(InterfaceSectionHint(gloopsRecording), GUI.skin.label);
        }

        /// <summary>
        /// Whether the given mode's option button is disabled (design 7.2, edge case 11).
        /// Only Basic is ever disabled, and only while a manual Gloops recording is running.
        /// Advanced is never disabled: it only reveals surfaces.
        /// </summary>
        internal static bool IsModeOptionDisabled(UiComplexityMode mode, bool gloopsRecording)
        {
            return mode == UiComplexityMode.Basic && gloopsRecording;
        }

        /// <summary>
        /// The Interface section's hint label. One label either way (never a second control),
        /// so the inline Gloops reason cannot change the IMGUI control count mid-frame.
        /// </summary>
        internal static string InterfaceSectionHint(bool gloopsRecording)
        {
            const string BaseHint = "Basic hides power-user windows. Advanced is the full UI.";
            return gloopsRecording
                ? BaseHint + " Stop the Gloops recording first."
                : BaseHint;
        }

        private static string UiComplexityModeLabel(UiComplexityMode mode)
            => mode == UiComplexityMode.Basic ? "Basic" : "Advanced";

        private static string UiComplexityModeTooltip(UiComplexityMode mode)
            => mode == UiComplexityMode.Basic
                ? "Show only the core loop: Timeline, Missions, Logistics, and Settings."
                : "Show every Parsek window and settings section.";

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
                new GUIContent(" Auto-merge recordings", "Commit recordings to the timeline automatically, with no confirmation dialog. When off, a confirmation dialog appears after each recording."));
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

            // Zero-drift A/B flag: how a looped mission that LANDS on another body (the Mun, or an
            // interplanetary destination such as Duna) aligns that landed-on body's rotation at each
            // faithful relaunch. The launch pad is always aligned exactly; this only trades the
            // relaunch cadence (same-parent) or the per-cycle arrival hold + destination-loiter trim
            // (interplanetary) against the approach-to-landing handoff seam on the destination body.
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Landing-body alignment",
                "For a looped mission that lands on another body (the Mun, or an interplanetary "
                + "destination such as Duna): how precisely that body's rotation lines up at each "
                + "relaunch. Off = launch as often as possible (largest landing-handoff seam); Loose = "
                + "a small seam; Precise = a pixel-perfect handoff (for an interplanetary landing the "
                + "deorbit is aligned each cycle by holding and re-timing the destination parking "
                + "loiter). The launch pad is always aligned exactly. Affects only looped inter-body "
                + "missions."),
                GUILayout.Width(150));
            if (GUILayout.Button(TransitedBodyRotationModeLabel(s.TransitedBodyRotationMode),
                    GUILayout.Width(120)))
            {
                s.TransitedBodyRotationMode = CycleTransitedBodyRotationMode(s.TransitedBodyRotationMode);
                ParsekLog.Info("UI",
                    $"Setting changed: transitedBodyRotationMode={s.TransitedBodyRotationMode}");
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>The cycle-button label for the landing-body alignment A/B mode. Pure.</summary>
        internal static string TransitedBodyRotationModeLabel(TransitedBodyRotationMode mode)
        {
            switch (mode)
            {
                case TransitedBodyRotationMode.Drop: return "Off (frequent)";
                case TransitedBodyRotationMode.Loose: return "Loose (~monthly)";
                default: return "Precise (rare)";
            }
        }

        /// <summary>Cycles the landing-body alignment A/B mode (Drop -&gt; Loose -&gt; Tight -&gt; Drop). Pure.</summary>
        internal static TransitedBodyRotationMode CycleTransitedBodyRotationMode(TransitedBodyRotationMode mode)
        {
            switch (mode)
            {
                case TransitedBodyRotationMode.Drop: return TransitedBodyRotationMode.Loose;
                case TransitedBodyRotationMode.Loose: return TransitedBodyRotationMode.Tight;
                default: return TransitedBodyRotationMode.Drop;
            }
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

            bool showRouteLines = GUILayout.Toggle(s.showRouteLines,
                new GUIContent(" Show supply route paths on map",
                    "Draw each committed same-body supply route's recorded launch-to-dock path as a line on the flight map and Tracking Station, so you can see where a route runs"));
            if (showRouteLines != s.showRouteLines)
            {
                s.showRouteLines = showRouteLines;
                ParsekSettingsPersistence.RecordShowRouteLines(showRouteLines);
                ParsekLog.Info("UI", $"Setting changed: showRouteLines={showRouteLines}");
            }
        }

        private void DrawStockUiSettings(ParsekSettings s)
        {
            GUILayout.Label("Stock UI", parentUI.GetSectionHeaderStyle());

            bool showCommittedFutureOverlays = GUILayout.Toggle(s.showCommittedFutureOverlays,
                new GUIContent(" Show committed-future overlays in stock UI",
                    "Show stock-screen markers for R&D, Astronaut Complex, and Mission Control actions already committed on the timeline"));
            if (showCommittedFutureOverlays != s.showCommittedFutureOverlays)
            {
                s.showCommittedFutureOverlays = showCommittedFutureOverlays;
                ParsekSettingsPersistence.RecordShowCommittedFutureOverlays(showCommittedFutureOverlays);
                ParsekLog.Info("UI", $"Setting changed: showCommittedFutureOverlays={showCommittedFutureOverlays}");
                StockUiOverlayController.RefreshOpenScreensAfterSettingsChanged();
            }

            bool blockCommittedActions = GUILayout.Toggle(s.blockCommittedActions,
                new GUIContent(" Block player actions that conflict with committed timeline",
                    "Prevent stock-screen clicks that would duplicate actions already committed by pending recordings"));
            if (blockCommittedActions != s.blockCommittedActions)
            {
                s.blockCommittedActions = blockCommittedActions;
                ParsekSettingsPersistence.RecordBlockCommittedActions(blockCommittedActions);
                // No overlay refresh here: this setting only gates click-block predicates.
                ParsekLog.Info("UI", $"Setting changed: blockCommittedActions={blockCommittedActions}");
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

            bool ghostRenderTracing = GUILayout.Toggle(s.ghostRenderTracing,
                new GUIContent(" Ghost render tracing (Warning: huge logs)",
                    "Write detailed per-ghost render placement diagnostics to KSP.log. Leave off unless investigating playback placement."));
            if (ghostRenderTracing != s.ghostRenderTracing)
            {
                s.ghostRenderTracing = ghostRenderTracing;
                ParsekSettingsPersistence.RecordGhostRenderTracing(s.ghostRenderTracing);
                ParsekLog.Info("UI", $"Setting changed: ghostRenderTracing={s.ghostRenderTracing}");
            }

            bool mapRenderTracing = GUILayout.Toggle(s.mapRenderTracing,
                new GUIContent(" Map/TS render tracing (Warning: huge logs)",
                    "Write detailed map and tracking-station ghost render diagnostics to KSP.log. Leave off unless investigating map/TS rendering. Per-frame detail also requires Verbose logging on."));
            if (mapRenderTracing != s.mapRenderTracing)
            {
                s.mapRenderTracing = mapRenderTracing;
                ParsekSettingsPersistence.RecordMapRenderTracing(s.mapRenderTracing);
                ParsekLog.Info("UI", $"Setting changed: mapRenderTracing={s.mapRenderTracing}");
            }

            bool ledgerTracing = GUILayout.Toggle(s.ledgerTracing,
                new GUIContent(" Ledger apply tracing (Warning: huge logs)",
                    "Write detailed ledger reconstruction diagnostics to KSP.log: a structural snapshot per recalc, per-identity change lines, and computed-vs-live read-back mismatch warnings. Leave off unless investigating ledger / career-state apply. Per-identity detail also requires Verbose logging on."));
            if (ledgerTracing != s.ledgerTracing)
            {
                s.ledgerTracing = ledgerTracing;
                ParsekSettingsPersistence.RecordLedgerTracing(s.ledgerTracing);
                ParsekLog.Info("UI", $"Setting changed: ledgerTracing={s.ledgerTracing}");
            }

            bool writeReadableSidecarMirrors = GUILayout.Toggle(s.writeReadableSidecarMirrors,
                new GUIContent(" Write readable sidecar mirrors (Warning: extra disk usage)",
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
                + "Also shows live RP counts split by crashed, stable, and concluded slots. "
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

            bool autoBackupExistingSaves = GUILayout.Toggle(s.autoBackupExistingSaves,
                new GUIContent(" Auto-backup existing saves before first use",
                    "The first time Parsek opens a save with no Parsek data yet, copy it to a separate timestamped 'pre-Parsek' entry in the Load menu, so you can return to your career as it was before installing Parsek. Runs once per save."));
            if (autoBackupExistingSaves != s.autoBackupExistingSaves)
            {
                s.autoBackupExistingSaves = autoBackupExistingSaves;
                ParsekSettingsPersistence.RecordAutoBackupExistingSaves(autoBackupExistingSaves);
                ParsekLog.Info("UI", $"Setting changed: autoBackupExistingSaves={autoBackupExistingSaves}");
            }

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
