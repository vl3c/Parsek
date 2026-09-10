using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// GUI-census partial: the thin Unity applier for the SINGLE-PHASE <c>UiAction</c> verb.
    /// Every decision - the op / window / tab / mode / rect vocabulary, the scene
    /// availability rule, the read-back tolerance and every payload shape - is delegated to
    /// the pure sibling <see cref="TestCommandUiAction"/>. This file only reaches live
    /// <c>ParsekUI</c> state and reads it back.
    ///
    /// <para>
    /// WHY IT DRIVES INTERNAL STATE RATHER THAN SYNTHESISING CLICKS. Each window's open flag
    /// is a plain field behind a property (<c>IsOpen</c>) that the main window's own buttons
    /// write; the tab selectors are equally plain ints. Faking a click would need an IMGUI
    /// event injected into a specific window's layout at a specific rect, which is neither
    /// reproducible nor a better proof of anything - the button handler's whole body IS the
    /// field write. So the seam writes the same fields and READS THEM BACK, and adds no
    /// player-facing surface: no new window, no new button, nothing reachable in a normal
    /// game.
    /// </para>
    ///
    /// <para>
    /// THE ONE EXCEPTION IS THE COMPLEXITY MODE, which is deliberately NOT a field write:
    /// it goes through the production <c>ParsekUI.SetUiComplexityMode</c> so the whole
    /// documented chain runs (persist, then the Advanced -> Basic gated-window close set,
    /// then the Missions tab clamp). That setter only QUEUES the draw-visible value, so the
    /// applier then calls <c>ApplyPendingUiComplexityModeIfAny</c> - which is contractually
    /// an <c>Update</c>-only call, and the seam pump runs in <c>Update</c>. That is what
    /// keeps the op single-phase without a second owner of the latch.
    /// </para>
    ///
    /// <para>
    /// WHY THE MAIN WINDOW IS IN THE TABLE AT ALL. Every sub-window draw in both hosts sits
    /// inside the host's <c>showUI</c> gate, so with the toolbar never clicked an unattended
    /// run draws NOTHING - a sub-window with <c>IsOpen = true</c> and the main window hidden
    /// is invisible. <c>UiAction op=open window=main</c> is therefore the first step of any
    /// census, and it is the only op that touches the scene host rather than
    /// <c>ParsekUI</c>.
    /// </para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private void UiActionImpl(ParsedCommand cmd)
        {
            // (1) The op. A missing / unknown op is terminal-with-no-side-effect.
            if (!TestCommandUiAction.TryParseOp(
                    ArgOrNull(cmd, "op"), out UiActionOp op, out string opReject))
            {
                string raw = ArgOrNull(cmd, "op") ?? string.Empty;
                ParsekLog.Warn(Tag, $"uiaction rejected reason={opReject} op={raw}");
                SetExecResult("REJECTED", null,
                    opReject == TestCommandUiAction.OpArgInvalidReason
                        ? $"{opReject} op={raw} valid={TestCommandUiAction.ValidOpNames}"
                        : opReject);
                return;
            }

            // (2) The host. A PRE-CALL gate, so REJECTED (the map-view-unavailable class):
            // the Tracking Station and the editor host no Parsek UI at all, and the
            // dispatcher has already waited for a loaded game, so waiting longer could not
            // turn this into a success.
            ParsekUI ui = ParsekUI.ActiveInstance;
            TestCommandScene scene = MapScene(HighLogic.LoadedScene);
            if (ui == null)
            {
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiAction.HostUnavailableReason
                    + $" op={TestCommandUiAction.OpToken(op)} "
                    + $"scene={TestCommandUiAction.SceneToken(scene)}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiAction.HostUnavailableReason} "
                    + $"scene={TestCommandUiAction.SceneToken(scene)}");
                return;
            }

            try
            {
                // Routed through the PURE predicate rather than a switch over the same
                // set, so this dispatch and the harness's own per-op arg validation
                // (hlib.UIACTION_OPS_NEEDING_WINDOW) cannot disagree about which ops name
                // a window. The three branches are exhaustive over the five parseable ops
                // plus describe; UiActionOp.None is unreachable (TryParseOp returned).
                if (TestCommandUiAction.OpNeedsWindow(op))
                {
                    UiActionWindowOp(cmd, ui, scene, op);
                    return;
                }
                if (op == UiActionOp.Complexity)
                {
                    UiActionComplexity(cmd);
                    return;
                }
                UiActionDescribe(ui, scene);
                return;
            }
            catch (Exception ex)
            {
                // The read-back cannot describe a throw, and the pump's own containment
                // would report a bare `exception=` with no verb context. Keep the named
                // token (the SimulateStockSwitchClick switch-threw discipline).
                ParsekLog.Error(Tag, $"uiaction op={TestCommandUiAction.OpToken(op)} threw "
                    + $"{ex.GetType().Name}: {ex.Message}");
                SetExecResult("ERROR", null, TestCommandUiAction.ThrewReason);
            }
        }

        // ----- op=open / close / tab / rect (everything that names a window) -----

        private void UiActionWindowOp(ParsedCommand cmd, ParsekUI ui,
                                      TestCommandScene scene, UiActionOp op)
        {
            if (!TestCommandUiAction.TryResolveWindow(
                    ArgOrNull(cmd, "window"), out UiWindowSpec spec, out string winReject))
            {
                string raw = ArgOrNull(cmd, "window") ?? string.Empty;
                ParsekLog.Warn(Tag, $"uiaction rejected reason={winReject} window={raw}");
                SetExecResult("REJECTED", null,
                    winReject == TestCommandUiAction.WindowUnknownReason
                        ? $"{winReject} window={raw} valid={TestCommandUiAction.ValidWindowNames}"
                        : winReject);
                return;
            }

            if (!TestCommandUiAction.IsAvailableInScene(spec, scene))
            {
                // Named, not silent: an "opened" flight-only window at the Space Center
                // would produce a capture of the scene WITHOUT it, which reads as a render
                // defect rather than as a spec that asked for the wrong scene.
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiAction.WindowNotInSceneReason
                    + $" window={spec.Name} scene={TestCommandUiAction.SceneToken(scene)}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiAction.WindowNotInSceneReason} window={spec.Name} "
                    + $"scene={TestCommandUiAction.SceneToken(scene)}");
                return;
            }

            switch (op)
            {
                case UiActionOp.Open:
                case UiActionOp.Close:
                    UiActionToggle(ui, spec, op);
                    return;
                case UiActionOp.Tab:
                    UiActionTab(cmd, ui, spec);
                    return;
                default:
                    UiActionRectOp(cmd, ui, spec);
                    return;
            }
        }

        private void UiActionToggle(ParsekUI ui, UiWindowSpec spec, UiActionOp op)
        {
            bool want = op == UiActionOp.Open;
            bool before = ReadWindowOpen(ui, spec.Name);
            bool already = before == want;
            if (!already)
                WriteWindowOpen(ui, spec.Name, want);
            bool after = ReadWindowOpen(ui, spec.Name);

            if (after != want)
            {
                // Only reachable if a window's own setter declines (a self-closing window
                // out of its scene). ERROR, not REJECTED: we acted and the game did not
                // follow - the map-not-entered line.
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiAction.WindowNotToggledReason
                    + $" window={spec.Name} want={Bool(want)} after={Bool(after)}");
                SetExecResult("ERROR", null,
                    $"{TestCommandUiAction.WindowNotToggledReason} window={spec.Name}");
                return;
            }

            ParsekLog.Info(Tag, $"uiaction {TestCommandUiAction.OpToken(op)} "
                + $"window={spec.Name} open={Bool(after)} already={Bool(already)}");
            SetExecResult("OK",
                TestCommandUiAction.BuildTogglePayload(op, spec.Name, after, already), null);
        }

        private void UiActionTab(ParsedCommand cmd, ParsekUI ui, UiWindowSpec spec)
        {
            if (!TestCommandUiAction.TryResolveTab(
                    spec, ArgOrNull(cmd, "tab"), out int index, out string tabReject))
            {
                string raw = ArgOrNull(cmd, "tab") ?? string.Empty;
                ParsekLog.Warn(Tag, $"uiaction rejected reason={tabReject} "
                    + $"window={spec.Name} tab={raw}");
                SetExecResult("REJECTED", null,
                    tabReject == TestCommandUiAction.TabUnknownReason
                        ? $"{tabReject} window={spec.Name} tab={raw} "
                          + $"valid={TestCommandUiAction.TabNamesOf(spec)}"
                        : $"{tabReject} window={spec.Name}");
                return;
            }

            int before = ReadWindowTab(ui, spec.Name);
            bool already = before == index;
            if (!already)
                WriteWindowTab(ui, spec.Name, index);
            int after = ReadWindowTab(ui, spec.Name);

            if (after != index)
            {
                // The one live cause: Basic mode clamps the Missions window's tab back to
                // index 0 (RecordingsTableUI.ApplyTabClamp), so `tab=recordings` cannot
                // hold there. Reported rather than silently accepted - a census that
                // believes it photographed the Recordings tab in Basic photographed
                // Missions.
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiAction.TabNotAppliedReason
                    + $" window={spec.Name} want={Int(index)} after={Int(after)}");
                SetExecResult("ERROR", null,
                    $"{TestCommandUiAction.TabNotAppliedReason} window={spec.Name} "
                    + $"tab={TestCommandUiAction.TabTokenAt(spec, index) ?? string.Empty}");
                return;
            }

            string token = TestCommandUiAction.TabTokenAt(spec, index);
            ParsekLog.Info(Tag, $"uiaction tab window={spec.Name} tab={token} "
                + $"index={Int(index)} already={Bool(already)}");
            SetExecResult("OK",
                TestCommandUiAction.BuildTabPayload(spec.Name, token, index, already), null);
        }

        private void UiActionRectOp(ParsedCommand cmd, ParsekUI ui, UiWindowSpec spec)
        {
            if (!TestCommandUiAction.TryParseRect(
                    ArgOrNull(cmd, "x"), ArgOrNull(cmd, "y"),
                    ArgOrNull(cmd, "w"), ArgOrNull(cmd, "h"),
                    out UiActionRect want, out string rectReject))
            {
                ParsekLog.Warn(Tag, $"uiaction rejected reason={rectReject} "
                    + $"window={spec.Name}");
                SetExecResult("REJECTED", null, $"{rectReject} window={spec.Name}");
                return;
            }

            WriteWindowRect(ui, spec.Name, want);
            UiActionRect after = ReadWindowRect(ui, spec.Name);
            bool hostControlled = TestCommandUiAction.SizeIsHostControlled(spec.Name);

            if (!TestCommandUiAction.RectAppliedWithinTolerance(want, after, hostControlled))
            {
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiAction.RectNotAppliedReason
                    + $" window={spec.Name} want={TestCommandUiAction.FormatRect(want)} "
                    + $"after={TestCommandUiAction.FormatRect(after)}");
                SetExecResult("ERROR", null,
                    $"{TestCommandUiAction.RectNotAppliedReason} window={spec.Name}");
                return;
            }

            ParsekLog.Info(Tag, $"uiaction rect window={spec.Name} "
                + $"rect={TestCommandUiAction.FormatRect(after)}");
            SetExecResult("OK",
                TestCommandUiAction.BuildRectPayload(spec.Name, after), null);
        }

        // ----- op=complexity -----

        private void UiActionComplexity(ParsedCommand cmd)
        {
            if (!TestCommandUiAction.TryParseMode(
                    ArgOrNull(cmd, "mode"), out bool basic, out string modeReject))
            {
                string raw = ArgOrNull(cmd, "mode") ?? string.Empty;
                ParsekLog.Warn(Tag, $"uiaction rejected reason={modeReject} mode={raw}");
                SetExecResult("REJECTED", null,
                    modeReject == TestCommandUiAction.ModeArgInvalidReason
                        ? $"{modeReject} mode={raw} valid="
                          + $"{TestCommandUiAction.BasicModeToken},"
                          + $"{TestCommandUiAction.AdvancedModeToken}"
                        : modeReject);
                return;
            }

            UiComplexityMode want = basic ? UiComplexityMode.Basic : UiComplexityMode.Advanced;
            bool already = ParsekUI.AppliedUiComplexityMode == want;

            // PRE-CALL: the one production refusal, named rather than inferred from a failed
            // read-back (the EnterWatchMode discipline). Advanced is never refused.
            if (!already && ParsekUI.WouldRefuseModeChange(want))
            {
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiAction.ComplexityRefusedRecordingReason
                    + $" mode={TestCommandUiAction.ModeToken(basic)}");
                SetExecResult("REJECTED", null,
                    TestCommandUiAction.ComplexityRefusedRecordingReason);
                return;
            }

            if (!already)
            {
                // The production setter (persist + queue), then the production latch. The
                // latch is Update-only by contract and the pump runs in Update.
                ParsekUI.SetUiComplexityMode(want);
                ParsekUI.ApplyPendingUiComplexityModeIfAny();
            }

            UiComplexityMode after = ParsekUI.AppliedUiComplexityMode;
            if (after != want)
            {
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiAction.ComplexityNotAppliedReason
                    + $" want={TestCommandUiAction.ModeToken(basic)} after={after}");
                SetExecResult("ERROR", null,
                    TestCommandUiAction.ComplexityNotAppliedReason);
                return;
            }

            ParsekLog.Info(Tag, $"uiaction complexity mode="
                + $"{TestCommandUiAction.ModeToken(basic)} already={Bool(already)}");
            SetExecResult("OK",
                TestCommandUiAction.BuildComplexityPayload(basic, already), null);
        }

        // ----- op=describe -----

        private void UiActionDescribe(ParsekUI ui, TestCommandScene scene)
        {
            var rows = new List<UiWindowState>(TestCommandUiAction.Windows.Count);
            foreach (UiWindowSpec spec in TestCommandUiAction.Windows)
            {
                bool available = TestCommandUiAction.IsAvailableInScene(spec, scene);
                var row = new UiWindowState { Name = spec.Name, Available = available };
                if (available)
                {
                    row.Open = ReadWindowOpen(ui, spec.Name);
                    UiActionRect rect = ReadWindowRect(ui, spec.Name);
                    // A rect field is all-zero until the window's first draw seeds its
                    // default from the main window's position. Reporting that zero as a
                    // position would send a reader hunting an off-screen window, so an
                    // unmeasured rect is reported ABSENT (rect=-) instead. Width is the
                    // discriminator because every seed site tests `width < 1`.
                    row.RectKnown = rect.W >= 1f;
                    row.Rect = rect;
                    if (spec.Tabs != null && spec.Tabs.Length > 0)
                        row.Tab = TestCommandUiAction.TabTokenAt(
                            spec, ReadWindowTab(ui, spec.Name));
                }
                rows.Add(row);
            }

            bool basic = ParsekUI.AppliedUiComplexityMode == UiComplexityMode.Basic;
            int openCount = 0;
            foreach (UiWindowState row in rows) if (row.Open) openCount++;
            ParsekLog.Info(Tag, $"uiaction describe scene="
                + $"{TestCommandUiAction.SceneToken(scene)} "
                + $"complexity={TestCommandUiAction.ModeToken(basic)} "
                + $"windows={Int(rows.Count)} open={Int(openCount)}");
            SetExecResult("OK", TestCommandUiAction.BuildDescribePayload(
                TestCommandUiAction.SceneToken(scene), basic, rows), null);
        }

        // ----- live-state plumbing -----
        //
        // One switch per axis over the canonical window name. Deliberately a switch rather
        // than a delegate table on the pure side: the pure half must stay free of
        // UnityEngine / ParsekUI references (it is the half xUnit exercises without KSP), so
        // the mapping from a wire token to a live field can only live here. Every arm is one
        // line, and a name the table has but this switch does not would fail loudly at the
        // default rather than silently reading false.

        private static bool ReadWindowOpen(ParsekUI ui, string window)
        {
            switch (window)
            {
                case TestCommandUiAction.MainWindow: return ReadHostShowUi();
                case TestCommandUiAction.MissionsWindow: return ui.GetRecordingsTableUI().IsOpen;
                case TestCommandUiAction.TimelineWindow: return ui.GetTimelineUI().IsOpen;
                case TestCommandUiAction.KerbalsWindow: return ui.GetKerbalsUI().IsOpen;
                case TestCommandUiAction.CareerWindow: return ui.GetCareerStateUI().IsOpen;
                case TestCommandUiAction.LogisticsWindow: return ui.GetLogisticsUI().IsOpen;
                case TestCommandUiAction.StructureWindow: return ui.GetStructureListUI().IsOpen;
                case TestCommandUiAction.SettingsWindow: return ui.GetSettingsWindowUI().IsOpen;
                case TestCommandUiAction.SpawnControlWindow: return ui.GetSpawnControlUI().IsOpen;
                case TestCommandUiAction.GloopsWindow: return ui.GetGloopsUI().IsOpen;
                case TestCommandUiAction.TestRunnerWindow: return ui.GetTestRunnerUI().IsOpen;
                default: throw new ArgumentOutOfRangeException(nameof(window), window);
            }
        }

        private static void WriteWindowOpen(ParsekUI ui, string window, bool open)
        {
            switch (window)
            {
                case TestCommandUiAction.MainWindow: WriteHostShowUi(open); return;
                case TestCommandUiAction.MissionsWindow: ui.GetRecordingsTableUI().IsOpen = open; return;
                case TestCommandUiAction.TimelineWindow: ui.GetTimelineUI().IsOpen = open; return;
                case TestCommandUiAction.KerbalsWindow: ui.GetKerbalsUI().IsOpen = open; return;
                case TestCommandUiAction.CareerWindow: ui.GetCareerStateUI().IsOpen = open; return;
                case TestCommandUiAction.LogisticsWindow: ui.GetLogisticsUI().IsOpen = open; return;
                case TestCommandUiAction.StructureWindow: ui.GetStructureListUI().IsOpen = open; return;
                case TestCommandUiAction.SettingsWindow: ui.GetSettingsWindowUI().IsOpen = open; return;
                case TestCommandUiAction.SpawnControlWindow: ui.GetSpawnControlUI().IsOpen = open; return;
                case TestCommandUiAction.GloopsWindow: ui.GetGloopsUI().IsOpen = open; return;
                case TestCommandUiAction.TestRunnerWindow: ui.GetTestRunnerUI().IsOpen = open; return;
                default: throw new ArgumentOutOfRangeException(nameof(window), window);
            }
        }

        private static int ReadWindowTab(ParsekUI ui, string window)
        {
            switch (window)
            {
                case TestCommandUiAction.MissionsWindow:
                    return ui.GetRecordingsTableUI().SelectedTabForTesting;
                case TestCommandUiAction.TimelineWindow:
                    return ui.GetTimelineUI().TierFilterModeIndexForTesting;
                case TestCommandUiAction.KerbalsWindow:
                    return ui.GetKerbalsUI().SelectedTabForTesting;
                case TestCommandUiAction.CareerWindow:
                    return ui.GetCareerStateUI().SelectedTabForTesting;
                default:
                    // Unreachable through the verb: TryResolveTab answers
                    // WindowHasNoTabsReason for every window absent from this switch, and
                    // describe only asks for a tab when the table declares one.
                    throw new ArgumentOutOfRangeException(nameof(window), window);
            }
        }

        private static void WriteWindowTab(ParsekUI ui, string window, int index)
        {
            switch (window)
            {
                case TestCommandUiAction.MissionsWindow:
                    ui.GetRecordingsTableUI().SelectedTabForTesting = index; return;
                case TestCommandUiAction.TimelineWindow:
                    ui.GetTimelineUI().TierFilterModeIndexForTesting = index; return;
                case TestCommandUiAction.KerbalsWindow:
                    ui.GetKerbalsUI().SelectedTabForTesting = index; return;
                case TestCommandUiAction.CareerWindow:
                    ui.GetCareerStateUI().SelectedTabForTesting = index; return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(window), window);
            }
        }

        private static UiActionRect ReadWindowRect(ParsekUI ui, string window)
        {
            switch (window)
            {
                case TestCommandUiAction.MainWindow: return ToRect(ReadHostMainRect());
                case TestCommandUiAction.MissionsWindow: return ToRect(ui.GetRecordingsTableUI().WindowRectForTesting);
                case TestCommandUiAction.TimelineWindow: return ToRect(ui.GetTimelineUI().WindowRectForTesting);
                case TestCommandUiAction.KerbalsWindow: return ToRect(ui.GetKerbalsUI().WindowRectForTesting);
                case TestCommandUiAction.CareerWindow: return ToRect(ui.GetCareerStateUI().WindowRectForTesting);
                case TestCommandUiAction.LogisticsWindow: return ToRect(ui.GetLogisticsUI().WindowRectForTesting);
                case TestCommandUiAction.StructureWindow: return ToRect(ui.GetStructureListUI().WindowRectForTesting);
                case TestCommandUiAction.SettingsWindow: return ToRect(ui.GetSettingsWindowUI().WindowRectForTesting);
                case TestCommandUiAction.SpawnControlWindow: return ToRect(ui.GetSpawnControlUI().WindowRectForTesting);
                case TestCommandUiAction.GloopsWindow: return ToRect(ui.GetGloopsUI().WindowRectForTesting);
                case TestCommandUiAction.TestRunnerWindow: return ToRect(ui.GetTestRunnerUI().WindowRectForTesting);
                default: throw new ArgumentOutOfRangeException(nameof(window), window);
            }
        }

        private static void WriteWindowRect(ParsekUI ui, string window, UiActionRect r)
        {
            Rect rect = new Rect(r.X, r.Y, r.W, r.H);
            switch (window)
            {
                case TestCommandUiAction.MainWindow: WriteHostMainRect(rect); return;
                case TestCommandUiAction.MissionsWindow: ui.GetRecordingsTableUI().WindowRectForTesting = rect; return;
                case TestCommandUiAction.TimelineWindow: ui.GetTimelineUI().WindowRectForTesting = rect; return;
                case TestCommandUiAction.KerbalsWindow: ui.GetKerbalsUI().WindowRectForTesting = rect; return;
                case TestCommandUiAction.CareerWindow: ui.GetCareerStateUI().WindowRectForTesting = rect; return;
                case TestCommandUiAction.LogisticsWindow: ui.GetLogisticsUI().WindowRectForTesting = rect; return;
                case TestCommandUiAction.StructureWindow: ui.GetStructureListUI().WindowRectForTesting = rect; return;
                case TestCommandUiAction.SettingsWindow: ui.GetSettingsWindowUI().WindowRectForTesting = rect; return;
                case TestCommandUiAction.SpawnControlWindow: ui.GetSpawnControlUI().WindowRectForTesting = rect; return;
                case TestCommandUiAction.GloopsWindow: ui.GetGloopsUI().WindowRectForTesting = rect; return;
                case TestCommandUiAction.TestRunnerWindow: ui.GetTestRunnerUI().WindowRectForTesting = rect; return;
                default: throw new ArgumentOutOfRangeException(nameof(window), window);
            }
        }

        private static UiActionRect ToRect(Rect r)
            => new UiActionRect { X = r.x, Y = r.y, W = r.width, H = r.height };

        // ONE KNOWN COSMETIC SIDE EFFECT, recorded rather than fixed: writing the host's
        // showUI flag directly does NOT move the toolbar button, which the player's own
        // path keeps in step (the ON/OFF callbacks set the flag, and ParsekUI.CloseMainWindow
        // calls toolbarControl.SetFalse()). So a driven open leaves the toolbar icon reading
        // "off" over an open window. It is invisible in a capture of the Parsek window
        // itself, it changes nothing about what draws, and reaching into ToolbarControl from
        // the seam would add a second owner of a two-way binding for a screenshot's benefit.
        //
        // The main window belongs to the SCENE HOST, not to ParsekUI, and the two hosts are
        // different MonoBehaviours. ParsekFlight publishes a static Instance; ParsekKSC does
        // not, so it is found by type. FindObjectOfType is acceptable here for the reason it
        // is not in a per-frame path: a UiAction step runs at most a few times per run, and
        // adding a static Instance + lifecycle to ParsekKSC would be a bigger change (and a
        // new stale-reference risk across scene loads) than this caller justifies.
        private static bool ReadHostShowUi()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight != null) return flight.ShowUIForTesting;
            ParsekKSC ksc = UnityEngine.Object.FindObjectOfType<ParsekKSC>();
            return ksc != null && ksc.ShowUIForTesting;
        }

        private static void WriteHostShowUi(bool show)
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight != null) { flight.ShowUIForTesting = show; return; }
            ParsekKSC ksc = UnityEngine.Object.FindObjectOfType<ParsekKSC>();
            if (ksc != null) ksc.ShowUIForTesting = show;
        }

        private static Rect ReadHostMainRect()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight != null) return flight.MainWindowRectForTesting;
            ParsekKSC ksc = UnityEngine.Object.FindObjectOfType<ParsekKSC>();
            return ksc != null ? ksc.MainWindowRectForTesting : default(Rect);
        }

        private static void WriteHostMainRect(Rect rect)
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight != null) { flight.MainWindowRectForTesting = rect; return; }
            ParsekKSC ksc = UnityEngine.Object.FindObjectOfType<ParsekKSC>();
            if (ksc != null) ksc.MainWindowRectForTesting = rect;
        }
    }
}
