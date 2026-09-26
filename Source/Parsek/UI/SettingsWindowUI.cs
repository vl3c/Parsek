using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Settings window extracted from ParsekUI.
    /// Manages all Parsek settings: interface mode, ghosts, looping period, sampling density,
    /// diagnostics (the Recording and Stock UI sections were retired by the 2026-08-27
    /// settings simplification; Data Management and its wipe buttons by the 2026-09-26
    /// no-player-deletion ruling).
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
        // Armed on the pass that releases the height, cleared when the fitted height lands
        // (or when the wait below runs out). Only drives logging - never layout.
        private SettingsWindowPresentation.HeightFitLogState heightFitLog;
        /// <summary>
        /// Draw passes to wait for a released height to come back before reporting the fit
        /// as a no-change. GUILayout applies it within the same frame, so this is generous;
        /// it exists so a fit that genuinely changes nothing still logs exactly once.
        /// </summary>
        private const int HeightFitLogPassBudget = 12;

        // Pressed-toggle style for the Basic / Advanced and Low / Medium / High option rows
        // (the Timeline / Kerbals / Career idiom): the selected option draws pressed, and
        // every option keeps one fixed width. Built lazily inside OnGUI (GUI.skin is only
        // valid there).
        private GUIStyle optionToggleStyle;

        // A ghost-audio slider change not yet written to settings.cfg. Flushed once the
        // drag ends (SettingsWindowPresentation.ShouldFlushGhostAudioVolume), so dragging
        // does not write the file every frame.
        private bool ghostAudioVolumePersistPending;

        // Auto-loop editing
        private string settingsAutoLoopText = "";
        private bool settingsAutoLoopEditing;
        private Rect settingsAutoLoopEditRect;

        /// <summary>
        /// The key this window's IMGUI window id is hashed from. Named once and used at
        /// BOTH the <c>ClickThruBlocker.GUILayoutWindow</c> call below and
        /// <c>UiWindowHandle.GetWindowId</c> (wired by
        /// <c>ParsekTestCommandAddon.ResolveWindowHandle</c>), which the <c>UiAction op=find</c>
        /// seam uses to scope a captured GUI tree to THIS window's subtree. Two copies of
        /// the literal would let the seam search the wrong window's children and answer a
        /// plausible rect for a control in another window.
        /// </summary>
        internal const string WindowIdKey = "ParsekSettings";

        // Hover texts. Each fits the 280 px window's two-line help strip (71 characters,
        // TooltipEchoBudgetTests); the ones passed straight to a GUIContent are also
        // scanned there, the rest are pinned by SettingsWindowTextTests.
        internal const string BasicModeTooltip =
            "Core windows only: Timeline, Missions, Logistics, Kerbals, Settings.";
        internal const string AdvancedModeTooltip =
            "Show every Parsek window and settings section.";
        internal const string AutoLaunchTooltip =
            "Loop period of missions whose period is set to auto. Shorter = overlap.";
        internal const string VerboseLoggingLabel = " Verbose logging";
        internal const string VerboseLoggingTooltip =
            "Detailed Parsek lines in KSP.log. Keep on if you report bugs.";
        internal const string ReadableMirrorsLabel = " Write readable .txt recording copies";
        internal const string ReadableMirrorsTooltip =
            "Also write .txt copies of recordings for bug reports. Uses extra disk.";

        private const float SpacingSmall = 3f;
        private const float SpacingLarge = 10f;

        // Bottom "hovered control help text" strip. See TooltipEchoBox for why it is a
        // permanently visible box of constant height.
        private readonly TooltipEchoBox tooltipEcho = new TooltipEchoBox(SpacingSmall);

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
                FlushGhostAudioVolumeIfPending(0);
                ReleaseInputLock();
                // A pending fit REPORT cannot outlive the window: no pass runs while closed,
                // so reopening later would otherwise announce a fit for a long-dead switch.
                // The fit REQUEST deliberately survives (see RequestHeightRemeasure).
                heightFitLog = default(SettingsWindowPresentation.HeightFitLogState);
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
                // The 600 above is a guess, not a measurement, and GUILayout only fixes it
                // in one direction: a window resolves to Max(passedHeight, contentMin), so
                // content TALLER than 600 auto-grows, while content SHORTER leaves dead
                // space at the bottom. Since the 2026-08-27 settings simplification shrank
                // the window, BOTH modes fit under the 600 seed, so every first open needs
                // the measured fit (before it, only Basic did - Advanced masked the bug).
                // Request the same height fit a mode switch gets, so the first open lands
                // on the measured height in either mode. Requesting here is equivalent to
                // the Update-latch path: this branch runs once, at the top of the first
                // event pass (a Layout - Unity sends Layout first), so the fit is consumed
                // exactly as if it had been requested before the frame.
                RequestHeightRemeasure();
            }

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;
            // Pass both Width+Height like every other Parsek window so the shared
            // opaqueWindowStyle padding renders identically (the previous height=10
            // reset + Width-only call caused the title-bar spacing to look off).
            //
            // The ONE exception is a pending re-measure (see RequestHeightRemeasure), which
            // both DROPS the Height option and hands GUILayout a zero-height RECT for that
            // single Layout pass. The rect is the load-bearing half: a GUILayout window
            // resolves to Max(passedHeight, contentMin), so dropping the Height option on its
            // own leaves an over-tall window exactly as tall as it was - grow-only, and true
            // of every GUILayout window rather than of this one's content (the full chain is
            // on SettingsWindowPresentation.BuildHeightFitLayoutRect). That is exactly the
            // reported bug: Advanced -> Basic kept the taller Advanced height, while
            // Basic -> Advanced only appeared to work because it is the direction that grows.
            //
            // No tooltip exclusion: the bottom help strip is permanently present at a
            // constant two-line height, so it contributes the same pixels to every
            // measurement whether or not the pointer is resting on a tooltipped control.
            // (It used to be measured only while showing, which is why the fit had to be
            // held back until a tooltip-free frame.)
            bool remeasuring = settingsWindowHeightRemeasurePending;
            // Only the Layout pass computes a size; releasing the height on any other event
            // would hand the chrome a collapsed rect for nothing.
            bool heightFitPass = remeasuring && Event.current.type == EventType.Layout;
            GUILayoutOption[] sizeOptions = remeasuring
                ? new[] { GUILayout.Width(settingsWindowRect.width) }
                : new[]
                {
                    GUILayout.Width(settingsWindowRect.width),
                    GUILayout.Height(settingsWindowRect.height)
                };
            Rect requestedRect = SettingsWindowPresentation.BuildHeightFitLayoutRect(
                settingsWindowRect, heightFitPass);
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            Rect drawnRect;
            try
            {
                drawnRect = ClickThruBlocker.GUILayoutWindow(
                    WindowIdKey.GetHashCode(),
                    requestedRect,
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

            // A fit pass returns the zero-height rect it was given (GUILayout applies the
            // fitted size after the call), so keep the current height rather than storing a
            // collapsed one - the fitted height arrives on a later pass's return.
            settingsWindowRect = SettingsWindowPresentation.KeepStoredHeightAcrossFitPass(
                drawnRect, settingsWindowRect.height, heightFitPass);

            // Consume on the LAYOUT pass only: layout options are ignored on every other
            // event type, so clearing the flag on (say) a Repaint would eat the request
            // without ever re-measuring. Unity sends Layout first each frame, and the mode
            // latch runs in Update, so the request is always honoured on the next frame.
            var ric = System.Globalization.CultureInfo.InvariantCulture;
            if (heightFitPass)
                settingsWindowHeightRemeasurePending = false;

            // Report the fit where it actually lands, never on the pass that asked for it.
            SettingsWindowPresentation.HeightFitLogStep fitStep =
                SettingsWindowPresentation.AdvanceHeightFitLog(
                    heightFitLog, heightFitPass, settingsWindowRect.height, HeightFitLogPassBudget);
            heightFitLog = fitStep.State;

            if (heightFitPass)
            {
                ParsekLog.Verbose("UI",
                    $"Settings window height released for content fit: " +
                    $"h={heightFitLog.HeightAtFitPass.ToString("F0", ric)} " +
                    $"mode={ParsekUI.AppliedUiComplexityMode}");
            }

            switch (fitStep.Outcome)
            {
                case SettingsWindowPresentation.HeightFitLogOutcome.Applied:
                    ParsekLog.Verbose("UI",
                        $"Settings window height fit applied: " +
                        $"h={settingsWindowRect.height.ToString("F0", ric)} " +
                        $"was={heightFitLog.HeightAtFitPass.ToString("F0", ric)} " +
                        $"mode={ParsekUI.AppliedUiComplexityMode}");
                    break;
                case SettingsWindowPresentation.HeightFitLogOutcome.NoChange:
                    ParsekLog.Verbose("UI",
                        $"Settings window height fit left the height unchanged: " +
                        $"h={settingsWindowRect.height.ToString("F0", ric)} " +
                        $"mode={ParsekUI.AppliedUiComplexityMode}");
                    break;
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
        /// for the next draw. Two callers: the deferred mode latch (Update, outside OnGUI)
        /// and the first-open rect init in <see cref="DrawIfOpen"/> - the latter runs once at
        /// the top of the window's first Layout pass, before any size option is built, so
        /// both are consumed with identical frame semantics and neither can change an IMGUI
        /// control count inside a frame (the fit alters window size options only).</para>
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

        /// <summary>
        /// Test seam for the live window rect. The in-game height-fit gate reads its
        /// height across a mode switch - the only place the real GUILayout clamp runs.
        /// </summary>
        internal Rect WindowRectForTesting
        {
            get { return settingsWindowRect; }
            // Settable like every sibling window's accessor, for the automation-only
            // UiAction op=rect seam op. Note this window's HEIGHT is advisory: a
            // pending height re-measure deliberately hands GUILayout a zero-height
            // rect for one Layout pass, so a commanded height is a floor and not a
            // pin (which is why the seam's read-back checks height as a floor).
            set { settingsWindowRect = value; }
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
            EndAutoLoopEdit();
        }

        /// <summary>
        /// Ends the auto-launch-period inline edit WITHOUT committing: clears the buffer's
        /// focus flag, forgets the field rect the click-away test reads, and releases the
        /// keyboard focus that field held. <see cref="CommitAutoLoopEdit"/> is this plus the
        /// commit, so both exits leave identical state.
        /// </summary>
        private void EndAutoLoopEdit()
        {
            settingsAutoLoopEditing = false;
            settingsAutoLoopEditRect = default;
            GUIUtility.keyboardControl = 0;
        }

        private void DrawSettingsWindow(int windowID)
        {
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

            // Basic / Advanced gating (design 7.1). Read the FRAME-LATCHED mode ONCE per
            // draw pass, never the settings field: the Interface section below hosts the
            // mode toggle itself and draws BEFORE the gated sections, so a raw read would
            // change the control count between this frame's Layout and Repaint passes
            // (`ArgumentException: Getting control N's position in a group with only M
            // controls`). Read here rather than at the first gated section because the
            // auto-loop edit-state check below needs it too.
            UiComplexityMode complexity = ParsekUI.AppliedUiComplexityMode;

            // Click outside active settings edit field → commit.
            //
            // Basic draws no Looping section, so the auto-launch field is not on screen and
            // settingsAutoLoopEditRect is one Advanced last drew: an edit left open across the
            // switch is DROPPED rather than committed against that stale rect. Dropping it here
            // is what keeps the mode visibility-only (design section 4.5 / philosophy 1): leave
            // the edit armed and the click-away branch below stays live with no field to click
            // away FROM, so the next MouseDown anywhere in this window would commit the stale
            // buffer to autoLoopIntervalSeconds - a loop-setting write performed in Basic. Every
            // other exit (this click-away, Enter, the unit button, Defaults) needs the section
            // to draw, so this is the only teardown that can still run. Mirrors the loop-period
            // drop in MissionsWindowUI.DrawMissionsTabContent. Runs on every event type: it
            // writes private fields only, never a control.
            if (!UiSurfaceVisibility.IsVisible(UiSurface.SettingsSectionLooping, complexity))
            {
                if (settingsAutoLoopEditing)
                {
                    ParsekLog.Verbose("UI",
                        "Auto-launch period edit dropped uncommitted: " +
                        "Basic UI mode draws no Looping section");
                    EndAutoLoopEdit();
                }
            }
            else if (Event.current.type == EventType.MouseDown)
            {
                if (settingsAutoLoopEditing && settingsAutoLoopEditRect.width > 0
                    && !settingsAutoLoopEditRect.Contains(Event.current.mousePosition))
                    CommitAutoLoopEdit(s);
            }

            // Each hidden section's trailing GUILayout.Space separator lives INSIDE its gate,
            // or Basic shows a double gap where the section used to be. Interface / Ghosts
            // are visible in both modes and stay unwrapped. (`complexity` is
            // latched above, before the edit-state check.) The former Recording and Stock UI
            // sections were retired in the 2026-08-27 settings simplification: auto-record
            // and auto-merge are hardwired ON (fields survive for the harness command seam),
            // and the committed-future overlays + committed-action click blocks are always
            // active.
            // Order: Interface, Ghosts, Looping, Recorder Sample Density, Diagnostics - the
            // player-facing sections first, developer instrumentation last. Basic draws
            // Interface and Ghosts only. There is no Data Management section: recordings
            // are never player-deletable (a deletion breaks the timeline and the ledger),
            // and the per-row Archive checkbox is the only way to stop seeing one.
            EnsureOptionToggleStyle();
            DrawInterfaceSettings(s);
            GUILayout.Space(SpacingSmall);

            DrawGhostSettings(s);
            GUILayout.Space(SpacingSmall);

            // Manual-loop authoring, global half (design section 4.5): the auto-launch period
            // that IS the period of any Auto-unit mission. Hidden with the Missions tab's
            // per-mission loop controls - one decision, so it does not straddle two windows.
            // Route DELIVERY is unaffected (a route-backing mission carries its own Sec-unit
            // DispatchInterval, authored in Logistics, which Basic keeps).
            if (UiSurfaceVisibility.IsVisible(UiSurface.SettingsSectionLooping, complexity))
            {
                DrawLoopingSettings(s);
                GUILayout.Space(SpacingSmall);
            }

            // Recorder fidelity tuning: a wrong value degrades recordings, and the Medium
            // default is correct for normal play (design section 4).
            if (UiSurfaceVisibility.IsVisible(UiSurface.SettingsSectionSampleDensity, complexity))
            {
                DrawSamplingSettings(s);
                GUILayout.Space(SpacingSmall);
            }

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

            // Bottom "hovered control help text" strip (shared house helper). Fixed
            // two-line height, always present, drawn directly above the button row -
            // the house ordering every Parsek window uses, so the Close button is
            // always the last thing in the window and never swaps places with the box.
            tooltipEcho.Draw();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(
                "Defaults", SettingsWindowPresentation.DefaultsButtonTooltip)))
            {
                ParsekLog.Verbose("UI", "Settings Defaults button clicked");
                SettingsWindowPresentation.SettingsDefaults defaults =
                    SettingsWindowPresentation.BuildDefaults();
                s.verboseLogging = defaults.VerboseLogging;
                s.ghostRenderTracing = false;
                s.mapRenderTracing = false;
                s.ledgerTracing = false;
                s.writeReadableSidecarMirrors = defaults.WriteReadableSidecarMirrors;
                s.showRouteLines = defaults.ShowRouteLines;
                s.SamplingDensityLevel = defaults.SamplingDensityLevel;
                s.autoLoopIntervalSeconds = defaults.AutoLoopIntervalSeconds;
                s.AutoLoopDisplayUnit = defaults.AutoLoopDisplayUnit;
                // The Ghosts slider is drawn in both modes, so Defaults resets it too
                // (finding P8). uiComplexityMode stays where the player left it - see
                // SettingsDefaults' remarks and the button tooltip above.
                s.ghostAudioVolume = defaults.GhostAudioVolume;
                ghostAudioVolumePersistPending = false;
                ParsekSettingsPersistence.RecordVerboseLogging(s.verboseLogging);
                ParsekSettingsPersistence.RecordSamplingDensity(s.samplingDensity);
                ParsekSettingsPersistence.RecordGhostAudioVolume(s.ghostAudioVolume);
                ParsekSettingsPersistence.RecordReadableSidecarMirrors(s.writeReadableSidecarMirrors);
                ParsekSettingsPersistence.RecordShowRouteLines(s.showRouteLines);
                ParsekSettingsPersistence.RecordGhostRenderTracing(s.ghostRenderTracing);
                ParsekSettingsPersistence.RecordMapRenderTracing(s.mapRenderTracing);
                ParsekSettingsPersistence.RecordLedgerTracing(s.ledgerTracing);
                RecordingStore.ReconcileReadableSidecarMirrorsForKnownRecordings();
                // Defaults rewrites autoLoopIntervalSeconds, so any in-progress edit of it is
                // stale: end it through the shared teardown (rect + keyboard focus too), not by
                // clearing the flag alone.
                EndAutoLoopEdit();
                ParsekLog.Info("UI", "Settings reset to defaults");
            }
            if (GUILayout.Button("Close"))
            {
                showSettingsWindow = false;
                ParsekLog.Verbose("UI", "Settings window closed via button");
            }
            GUILayout.EndHorizontal();

            GUI.DragWindow();
        }

        /// <summary>
        /// Basic / Advanced UI complexity toggle (design 6.2). Drawn FIRST because it
        /// governs which of the sections below a player even sees. A two-option
        /// pressed-toggle row (shared with <see cref="DrawSamplingSettings"/>) rather than a
        /// checkbox: the two modes are peers, not an on/off of one of them.
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

            float cellWidth = SettingsWindowPresentation.OptionCellWidth(settingsWindowRect.width, 2);
            GUILayout.BeginHorizontal();
            foreach (UiComplexityMode mode in new[] { UiComplexityMode.Basic, UiComplexityMode.Advanced })
            {
                bool isSelected = s.UiComplexityModeLevel == mode;

                // GUI.enabled changes how the control renders and whether it reports a
                // click; it does NOT change the control COUNT, so this is safe to vary
                // between one frame's Layout and Repaint passes.
                bool prevEnabled = GUI.enabled;
                GUI.enabled = prevEnabled && !IsModeOptionDisabled(mode, gloopsRecording);
                bool pressed = GUILayout.Toggle(isSelected,
                    new GUIContent(UiComplexityModeLabel(mode), UiComplexityModeTooltip(mode)),
                    optionToggleStyle, GUILayout.Width(cellWidth));
                GUI.enabled = prevEnabled;

                if (pressed && !isSelected)
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

        internal static string UiComplexityModeTooltip(UiComplexityMode mode)
            => mode == UiComplexityMode.Basic ? BasicModeTooltip : AdvancedModeTooltip;

        /// <summary>
        /// Builds the pressed-toggle style once: a button whose ON state draws with the
        /// skin's pressed background and white text, matching the Timeline, Kerbals and
        /// Career windows. Replaces the old selected-is-a-box look, which read as greyed
        /// out and changed the row's widths on every switch.
        /// </summary>
        private void EnsureOptionToggleStyle()
        {
            if (optionToggleStyle != null) return;
            optionToggleStyle = new GUIStyle(GUI.skin.button)
            {
                margin = new RectOffset(
                    SettingsWindowPresentation.OptionToggleMarginPx,
                    SettingsWindowPresentation.OptionToggleMarginPx,
                    GUI.skin.button.margin.top, GUI.skin.button.margin.bottom)
            };
            optionToggleStyle.onNormal.background = GUI.skin.button.active.background;
            optionToggleStyle.onHover.background = GUI.skin.button.active.background;
            optionToggleStyle.onNormal.textColor = Color.white;
            optionToggleStyle.onHover.textColor = Color.white;
        }

        private void DrawLoopingSettings(ParsekSettings s)
        {
            GUILayout.Label("Looping", parentUI.GetSectionHeaderStyle());
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Auto-launch every", AutoLaunchTooltip),
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
                "Volume for ghost audio: engines, RCS, events. 0% = muted."),
                GUILayout.Width(85));
            float newAudioVol = GUILayout.HorizontalSlider(s.ghostAudioVolume, 0f, 1f);
            GUILayout.Label(
                UnityEngine.Mathf.RoundToInt(newAudioVol * 100f).ToString() + "%",
                GUILayout.Width(35));
            GUILayout.EndHorizontal();
            if (UnityEngine.Mathf.Abs(newAudioVol - s.ghostAudioVolume) > 0.001f)
            {
                s.ghostAudioVolume = newAudioVol;
                ghostAudioVolumePersistPending = true;
                ParsekLog.VerboseRateLimited("UI", "ghostAudioVolume",
                    $"Ghost audio volume set to {newAudioVol:F2}", 1.0);
            }
            FlushGhostAudioVolumeIfPending(GUIUtility.hotControl);

            bool showRouteLines = GUILayout.Toggle(s.showRouteLines,
                new GUIContent(" Show supply route paths on map",
                    "Draw supply routes' recorded paths on the map and Tracking Station."));
            if (showRouteLines != s.showRouteLines)
            {
                s.showRouteLines = showRouteLines;
                ParsekSettingsPersistence.RecordShowRouteLines(showRouteLines);
                ParsekLog.Info("UI", $"Setting changed: showRouteLines={showRouteLines}");
            }
        }

        /// <summary>
        /// Writes a finished ghost-audio slider change to settings.cfg, once, after the drag
        /// ends (or when the window closes). Logs the persisted value at Info.
        /// </summary>
        private void FlushGhostAudioVolumeIfPending(int hotControl)
        {
            if (!SettingsWindowPresentation.ShouldFlushGhostAudioVolume(
                    ghostAudioVolumePersistPending, hotControl))
                return;
            ghostAudioVolumePersistPending = false;
            ParsekSettings s = ParsekSettings.Current;
            if (s == null)
            {
                ParsekLog.Verbose("UI", "Ghost audio volume persist skipped: no active game settings");
                return;
            }
            ParsekSettingsPersistence.RecordGhostAudioVolume(s.ghostAudioVolume);
            ParsekLog.Info("UI",
                $"Setting changed: ghostAudioVolume={s.ghostAudioVolume.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        private void DrawDiagnosticsSettings(ParsekSettings s)
        {
            GUILayout.Label("Diagnostics", parentUI.GetSectionHeaderStyle());
            bool verboseLogging = GUILayout.Toggle(s.verboseLogging,
                new GUIContent(VerboseLoggingLabel, VerboseLoggingTooltip));
            if (verboseLogging != s.verboseLogging)
            {
                s.verboseLogging = verboseLogging;
                ParsekSettingsPersistence.RecordVerboseLogging(s.verboseLogging);
                ParsekLog.Info("UI", $"Setting changed: verboseLogging={s.verboseLogging}");
            }

            bool ghostRenderTracing = GUILayout.Toggle(s.ghostRenderTracing,
                new GUIContent(" Ghost render tracing (Warning: huge logs)",
                    "Log per-ghost render placement to KSP.log. Leave off unless debugging."));
            if (ghostRenderTracing != s.ghostRenderTracing)
            {
                s.ghostRenderTracing = ghostRenderTracing;
                ParsekSettingsPersistence.RecordGhostRenderTracing(s.ghostRenderTracing);
                ParsekLog.Info("UI", $"Setting changed: ghostRenderTracing={s.ghostRenderTracing}");
            }

            bool mapRenderTracing = GUILayout.Toggle(s.mapRenderTracing,
                new GUIContent(" Map/TS render tracing (Warning: huge logs)",
                    "Log map and Tracking Station ghost rendering to KSP.log. Leave off."));
            if (mapRenderTracing != s.mapRenderTracing)
            {
                s.mapRenderTracing = mapRenderTracing;
                ParsekSettingsPersistence.RecordMapRenderTracing(s.mapRenderTracing);
                ParsekLog.Info("UI", $"Setting changed: mapRenderTracing={s.mapRenderTracing}");
            }

            bool ledgerTracing = GUILayout.Toggle(s.ledgerTracing,
                new GUIContent(" Ledger apply tracing (Warning: huge logs)",
                    "Log ledger reconstruction and apply detail to KSP.log. Leave off."));
            if (ledgerTracing != s.ledgerTracing)
            {
                s.ledgerTracing = ledgerTracing;
                ParsekSettingsPersistence.RecordLedgerTracing(s.ledgerTracing);
                ParsekLog.Info("UI", $"Setting changed: ledgerTracing={s.ledgerTracing}");
            }

            bool writeReadableSidecarMirrors = GUILayout.Toggle(s.writeReadableSidecarMirrors,
                new GUIContent(ReadableMirrorsLabel, ReadableMirrorsTooltip));
            if (writeReadableSidecarMirrors != s.writeReadableSidecarMirrors)
            {
                s.writeReadableSidecarMirrors = writeReadableSidecarMirrors;
                ParsekSettingsPersistence.RecordReadableSidecarMirrors(s.writeReadableSidecarMirrors);
                RecordingStore.ReconcileReadableSidecarMirrorsForKnownRecordings();
                ParsekLog.Info("UI", $"Setting changed: writeReadableSidecarMirrors={s.writeReadableSidecarMirrors}");
            }

            // "Also Ctrl+Shift+T" was wrong: the shortcut opens the SEPARATE global runner
            // window (InGameTests/TestRunnerShortcut), not this one (finding P16).
            if (GUILayout.Button(new GUIContent("In-Game Test Runner",
                "Run runtime tests for ghosts and playback. Ctrl+Shift+T opens its own.")))
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
                RewindPointDiskUsage.FormatTooltip(rpSnap)));
        }

        private void DrawSamplingSettings(ParsekSettings s)
        {
            GUILayout.Label("Recorder Sample Density", parentUI.GetSectionHeaderStyle());

            float cellWidth = SettingsWindowPresentation.OptionCellWidth(settingsWindowRect.width, 3);
            GUILayout.BeginHorizontal();
            foreach (SamplingDensity level in new[] { SamplingDensity.Low, SamplingDensity.Medium, SamplingDensity.High })
            {
                bool isSelected = s.SamplingDensityLevel == level;
                bool pressed = GUILayout.Toggle(isSelected,
                    new GUIContent(ParsekSettings.DensityLabel(level), ParsekSettings.DensityTooltip(level)),
                    optionToggleStyle, GUILayout.Width(cellWidth));
                if (pressed && !isSelected)
                {
                    s.SamplingDensityLevel = level;
                    ParsekSettingsPersistence.RecordSamplingDensity(s.samplingDensity);
                    ParsekLog.Info("UI", $"Setting changed: samplingDensity={level}");
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(ParsekSettings.DensitySummary(s.SamplingDensityLevel),
                GUI.skin.label);
        }
    }
}
