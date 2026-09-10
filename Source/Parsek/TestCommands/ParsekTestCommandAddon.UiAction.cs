using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// One driveable window's live state, reached through the ONE resolver below rather
    /// than through a per-axis switch.
    ///
    /// <para>WHY A HANDLE AND NOT FOUR SWITCHES. The first draft had four parallel switches
    /// over the same eleven window names - read/write open, read/write tab, read/write rect
    /// - so a mapping error (the kerbals arm reaching the career window's field) lived in
    /// ONE of eight arms while the other seven stayed right, and the whole xUnit suite
    /// passed either way: the pure half never touches a live window, so nothing could
    /// witness it. Collapsing them into one row per window makes that class of error a
    /// single site, which is the only defence a headless suite can offer here.</para>
    ///
    /// <para><see cref="GetTab"/> / <see cref="SetTab"/> are NULL for a window with no tab
    /// selector, which is the same fact <c>TestCommandUiAction.TryResolveTab</c> reports as
    /// <c>window-has-no-tabs</c>. The null is never dereferenced through the verb (that
    /// REJECTED fires first) and describe asks only where the table declares tabs.</para>
    /// </summary>
    internal struct UiWindowHandle
    {
        internal Func<bool> GetOpen;
        internal Action<bool> SetOpen;
        internal Func<Rect> GetRect;
        internal Action<Rect> SetRect;
        internal Func<int> GetTab;
        internal Action<int> SetTab;
    }

    /// <summary>
    /// GUI-census partial: the thin Unity applier for the <c>UiAction</c> verb. Every
    /// decision - the op / window / tab / mode / rect vocabulary, the scene availability
    /// rule, the settle rule, the read-back tolerance and every payload shape - is
    /// delegated to the pure sibling <see cref="TestCommandUiAction"/>. This file only
    /// reaches live <c>ParsekUI</c> state and reads it back.
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
    /// TWO OPS HOLD THE HEAD FOR A FRAME. <c>open</c> and <c>rect</c> are TWO-PHASE
    /// (<c>TestCommandUiAction.OpIsTwoPhase</c>) because their read-back is only a
    /// statement about the game AFTER an OnGUI pass has run: a window can force-close
    /// itself on its first draw, and a <c>GUILayout</c> window's rect is resolved during
    /// the draw. Everything else is a synchronous write-then-read.
    /// </para>
    ///
    /// <para>
    /// THE COMPLEXITY MODE is deliberately NOT a field write: it goes through the
    /// production <c>ParsekUI.SetUiComplexityMode</c> so the whole documented chain runs
    /// (persist, then the Advanced -&gt; Basic gated-window close set, then the Missions tab
    /// clamp). That setter only QUEUES the draw-visible value, so the applier then calls
    /// <c>ApplyPendingUiComplexityModeIfAny</c> - which is contractually an <c>Update</c>-only
    /// call, and the seam pump runs in <c>Update</c>. That is what keeps the op
    /// single-phase without a second owner of the latch.
    /// </para>
    ///
    /// <para>
    /// WHY THE MAIN WINDOW IS IN THE TABLE AT ALL. Every sub-window draw in both hosts sits
    /// inside the host's <c>showUI</c> gate, so with the toolbar never clicked an unattended
    /// run draws NOTHING - a sub-window with <c>IsOpen = true</c> and the main window hidden
    /// is invisible. <c>UiAction op=open window=main</c> is therefore the first step of any
    /// census, and it is the only window whose handle reaches the scene host rather than
    /// <c>ParsekUI</c>.
    /// </para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // Two-phase state for an in-flight `open` / `rect`. Re-armed wholesale at the start
        // of every two-phase arm, so a stale value can never be read across commands (the
        // TimeJump field contract).
        private UiActionOp uiActionOp;
        private string uiActionWindow;
        private UiActionRect uiActionCommandedRect;
        private bool uiActionAlready;
        private bool uiActionSizeHostControlled;
        private int uiActionStartFrame;

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

            UiWindowHandle handle = ResolveWindowHandle(ui, spec.Name);
            switch (op)
            {
                case UiActionOp.Open:
                case UiActionOp.Close:
                    UiActionToggle(handle, spec, op);
                    return;
                case UiActionOp.Tab:
                    UiActionTab(cmd, handle, spec);
                    return;
                default:
                    UiActionRectOp(cmd, handle, spec);
                    return;
            }
        }

        private void UiActionToggle(UiWindowHandle handle, UiWindowSpec spec, UiActionOp op)
        {
            bool want = op == UiActionOp.Open;
            bool before = handle.GetOpen();
            bool already = before == want;
            if (!already)
                handle.SetOpen(want);
            bool after = handle.GetOpen();

            if (after != want)
            {
                // Only reachable if a window's own setter declines the value outright, with
                // no draw in between. ERROR, not REJECTED: we acted and the game did not
                // follow - the map-not-entered line.
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiAction.WindowNotToggledReason
                    + $" window={spec.Name} want={Bool(want)} after={Bool(after)}");
                SetExecResult("ERROR", null,
                    $"{TestCommandUiAction.WindowNotToggledReason} window={spec.Name}");
                return;
            }

            if (!TestCommandUiAction.OpIsTwoPhase(op))
            {
                // `close` terminates here. The mirror direction was checked before this
                // asymmetry was accepted: a drawing window can LOWER its own flag
                // (SpawnControlUI.DrawIfOpen), and nothing in the mod RAISES one from a
                // draw path - so there is no self-opening window a settled close read-back
                // could catch, and holding the head for it would buy a frame of nothing.
                ParsekLog.Info(Tag, $"uiaction {TestCommandUiAction.OpToken(op)} "
                    + $"window={spec.Name} open={Bool(after)} already={Bool(already)}");
                SetExecResult("OK",
                    TestCommandUiAction.BuildTogglePayload(op, spec.Name, after, already),
                    null);
                return;
            }

            ArmUiActionSettle(op, spec, already, hostControlled: false);
            ParsekLog.Info(Tag, $"uiaction open initiated window={spec.Name} "
                + $"already={Bool(already)} (awaiting one drawn frame)");
            SetExecResult(PendingVerdict, null, null);
        }

        private void UiActionTab(ParsedCommand cmd, UiWindowHandle handle, UiWindowSpec spec)
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

            int before = handle.GetTab();
            bool already = before == index;
            if (!already)
                handle.SetTab(index);
            int after = handle.GetTab();

            if (after != index)
            {
                // The one live cause: Basic mode clamps the Missions window's tab back to
                // index 0 (RecordingsTableUI.ApplyTabClamp), so `tab=recordings` cannot
                // hold there. Reported rather than silently accepted - a census that
                // believes it photographed the Recordings tab in Basic photographed
                // Missions. Single-phase is right here: that clamp runs from the
                // complexity LATCH (which the applier drives in Update), not from a draw.
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

        private void UiActionRectOp(ParsedCommand cmd, UiWindowHandle handle,
                                    UiWindowSpec spec)
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

            handle.SetRect(new Rect(want.X, want.Y, want.W, want.H));

            // TWO-PHASE, and this is the whole reason: a GUILayout window's rect is
            // resolved during the DRAW (GUILayout.Window returns the resolved rect and the
            // window class assigns it back), so reading it back in the same Update compares
            // the field with the value just written to it - which is what the first draft
            // did, leaving RectAppliedWithinTolerance unable to fire on any input.
            uiActionCommandedRect = want;
            ArmUiActionSettle(UiActionOp.Rect, spec, already: false,
                hostControlled: TestCommandUiAction.SizeIsHostControlled(spec.Name));
            ParsekLog.Info(Tag, $"uiaction rect initiated window={spec.Name} "
                + $"want={TestCommandUiAction.FormatRect(want)} (awaiting one drawn frame)");
            SetExecResult(PendingVerdict, null, null);
        }

        private void ArmUiActionSettle(UiActionOp op, UiWindowSpec spec, bool already,
                                       bool hostControlled)
        {
            uiActionOp = op;
            uiActionWindow = spec.Name;
            uiActionAlready = already;
            uiActionSizeHostControlled = hostControlled;
            uiActionStartFrame = Time.frameCount;
        }

        // Bounded, observable completion (the LoadGame contract): wait for ONE drawn frame,
        // then read the live state back. Unity runs Update (where this pump lives) before
        // OnGUI, so a single advanced frame guarantees a full IMGUI pass has happened -
        // the pass in which a self-closing window closes itself and in which GUILayout
        // resolves a window rect.
        private void TryCompleteUiAction(double now)
        {
            int framesElapsed = Time.frameCount - uiActionStartFrame;
            double budget = DeferralBudget.BudgetSeconds("UiAction");
            bool expired = DeferralBudget.ShouldTimeout(completionStartedAt, now, budget);
            UiActionSettleOutcome outcome =
                TestCommandUiAction.DecideSettlePoll(framesElapsed, expired);
            if (outcome == UiActionSettleOutcome.NotYet)
                return;

            string id = completionId;
            long seq = completionSeq;
            string verb = completionVerb;
            UiActionOp op = uiActionOp;
            string window = uiActionWindow;
            bool already = uiActionAlready;
            UiActionRect commanded = uiActionCommandedRect;
            bool hostControlled = uiActionSizeHostControlled;
            ClearTwoPhase();

            if (outcome == UiActionSettleOutcome.TimedOut)
            {
                // Not a refusal: the budget ran out before the game drew a single frame,
                // which means the renderer stopped or the pump never reached another safe
                // point. Named separately so it cannot be read as "the window said no".
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiAction.NotSettledReason
                    + $" op={TestCommandUiAction.OpToken(op)} window={window} "
                    + $"frames={Int(framesElapsed)}");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    $"{TestCommandUiAction.NotSettledReason} window={window}",
                    dequeueHead: true);
                return;
            }

            ParsekUI ui = ParsekUI.ActiveInstance;
            if (ui == null)
            {
                // The scene lost its host between the write and the settle. Treat it as the
                // same pre-call class the execute path answers, rather than dereferencing.
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiAction.NotSettledReason
                    + $" op={TestCommandUiAction.OpToken(op)} window={window} "
                    + "(ui host gone between write and settle)");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    $"{TestCommandUiAction.NotSettledReason} window={window}",
                    dequeueHead: true);
                return;
            }

            UiWindowHandle handle = ResolveWindowHandle(ui, window);

            if (op == UiActionOp.Open)
            {
                bool after = handle.GetOpen();
                if (!after)
                {
                    // The flag WAS raised (the execute path verified that) and a window
                    // that drew itself put it back down - SpawnControlUI.DrawIfOpen's
                    // zero-candidates auto-close being the live case. Without this the
                    // census would report OK and then photograph a scene with no window.
                    ParsekLog.Error(Tag, "uiaction error reason="
                        + TestCommandUiAction.WindowSelfClosedReason
                        + $" window={window} frames={Int(framesElapsed)}");
                    EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                        $"{TestCommandUiAction.WindowSelfClosedReason} window={window}",
                        dequeueHead: true);
                    return;
                }
                ParsekLog.Info(Tag, $"uiaction open window={window} open=true "
                    + $"already={Bool(already)} frames={Int(framesElapsed)}");
                EmitExecutedTerminal(id, seq, verb, "OK",
                    TestCommandUiAction.BuildTogglePayload(
                        UiActionOp.Open, window, true, already),
                    null, dequeueHead: true);
                return;
            }

            UiActionRect settled = ToRect(handle.GetRect());
            if (!TestCommandUiAction.RectAppliedWithinTolerance(
                    commanded, settled, hostControlled))
            {
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiAction.RectNotAppliedReason
                    + $" window={window} want={TestCommandUiAction.FormatRect(commanded)} "
                    + $"after={TestCommandUiAction.FormatRect(settled)}");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    $"{TestCommandUiAction.RectNotAppliedReason} window={window}",
                    dequeueHead: true);
                return;
            }

            ParsekLog.Info(Tag, $"uiaction rect window={window} "
                + $"rect={TestCommandUiAction.FormatRect(settled)} "
                + $"frames={Int(framesElapsed)}");
            EmitExecutedTerminal(id, seq, verb, "OK",
                TestCommandUiAction.BuildRectPayload(window, settled), null,
                dequeueHead: true);
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
            // BOTH the latch AND the persisted setting have to agree before this is a
            // no-op, because SetUiComplexityMode no-ops on the SETTING: with a save that
            // persisted Basic under a fail-open Advanced latch, checking only the latch
            // called a setter that queued nothing and then ERRORed complexity-not-applied
            // on the mode the save actually carried.
            bool already = TestCommandUiAction.IsComplexityAlreadySatisfied(
                ParsekUI.AppliedUiComplexityMode, ParsekUI.PersistedUiComplexityMode, want);

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
                // The production setter (persist + queue when the SETTING differs), then
                // the production re-queue for the case it does not (setting already right,
                // latch drifted), then the production latch. All three are ParsekUI's own;
                // the seam adds no fourth writer of the pending value.
                ParsekUI.SetUiComplexityMode(want);
                ParsekUI.TryRequeuePersistedUiComplexityMode();
                ParsekUI.ApplyPendingUiComplexityModeIfAny();
            }

            UiComplexityMode after = ParsekUI.AppliedUiComplexityMode;
            if (after != want)
            {
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiAction.ComplexityNotAppliedReason
                    + $" want={TestCommandUiAction.ModeToken(basic)} after={after} "
                    + $"persisted={ParsekUI.PersistedUiComplexityMode}");
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
                    UiWindowHandle handle = ResolveWindowHandle(ui, spec.Name);
                    row.Open = handle.GetOpen();
                    UiActionRect rect = ToRect(handle.GetRect());
                    // A rect field is all-zero until the window's first draw seeds its
                    // default from the main window's position. Reporting that zero as a
                    // position would send a reader hunting an off-screen window, so an
                    // unmeasured rect is reported ABSENT (rect=-) instead. Width is the
                    // discriminator because every seed site tests `width < 1`.
                    row.RectKnown = rect.W >= 1f;
                    row.Rect = rect;
                    if (spec.Tabs != null && spec.Tabs.Length > 0)
                        row.Tab = TestCommandUiAction.TabTokenAt(spec, handle.GetTab());
                }
                rows.Add(row);
            }

            bool basic = ParsekUI.AppliedUiComplexityMode == UiComplexityMode.Basic;
            int openCount = 0;
            foreach (UiWindowState row in rows) if (row.Open) openCount++;
            // `openWindows=` is on the LOG line and not only in the payload, because the
            // payload never reaches KSP.log: a census spec's [expectations.logContracts]
            // can only assert what is written here. Paired with `open=<n>` it is an EXACT
            // claim - `open=2 openWindows=main,missions` cannot match a scene with a third
            // window open - which is the in-run check a describe-after-open step buys.
            ParsekLog.Info(Tag, $"uiaction describe scene="
                + $"{TestCommandUiAction.SceneToken(scene)} "
                + $"complexity={TestCommandUiAction.ModeToken(basic)} "
                + $"windows={Int(rows.Count)} open={Int(openCount)} "
                + $"openWindows={TestCommandUiAction.FormatOpenWindowList(rows)}");
            SetExecResult("OK", TestCommandUiAction.BuildDescribePayload(
                TestCommandUiAction.SceneToken(scene), basic, rows), null);
        }

        // ----- live-state plumbing: ONE resolver, one row per window -----
        //
        // It cannot live on the pure side: that half must stay free of UnityEngine /
        // ParsekUI references, which is what lets xUnit exercise it without KSP. So the
        // mapping from a wire token to live fields lives here - but as ONE row per window
        // rather than as four parallel switches, so a mis-wired window is a single site.
        // A name the table has but this resolver does not fails loudly at the default
        // rather than silently reading false.

        private static UiWindowHandle ResolveWindowHandle(ParsekUI ui, string window)
        {
            switch (window)
            {
                case TestCommandUiAction.MainWindow:
                    // The only row that reaches the SCENE HOST rather than ParsekUI - see
                    // the host accessors at the bottom of this file.
                    return Handle(ReadHostShowUi, WriteHostShowUi,
                                  ReadHostMainRect, WriteHostMainRect);
                case TestCommandUiAction.MissionsWindow:
                {
                    RecordingsTableUI w = ui.GetRecordingsTableUI();
                    return Handle(() => w.IsOpen, v => w.IsOpen = v,
                                  () => w.WindowRectForTesting, r => w.WindowRectForTesting = r,
                                  () => w.SelectedTabForTesting, i => w.SelectedTabForTesting = i);
                }
                case TestCommandUiAction.TimelineWindow:
                {
                    TimelineWindowUI w = ui.GetTimelineUI();
                    return Handle(() => w.IsOpen, v => w.IsOpen = v,
                                  () => w.WindowRectForTesting, r => w.WindowRectForTesting = r,
                                  () => w.TierFilterModeIndexForTesting,
                                  i => w.TierFilterModeIndexForTesting = i);
                }
                case TestCommandUiAction.KerbalsWindow:
                {
                    KerbalsWindowUI w = ui.GetKerbalsUI();
                    return Handle(() => w.IsOpen, v => w.IsOpen = v,
                                  () => w.WindowRectForTesting, r => w.WindowRectForTesting = r,
                                  () => w.SelectedTabForTesting, i => w.SelectedTabForTesting = i);
                }
                case TestCommandUiAction.CareerWindow:
                {
                    CareerStateWindowUI w = ui.GetCareerStateUI();
                    return Handle(() => w.IsOpen, v => w.IsOpen = v,
                                  () => w.WindowRectForTesting, r => w.WindowRectForTesting = r,
                                  () => w.SelectedTabForTesting, i => w.SelectedTabForTesting = i);
                }
                case TestCommandUiAction.LogisticsWindow:
                {
                    LogisticsWindowUI w = ui.GetLogisticsUI();
                    return Handle(() => w.IsOpen, v => w.IsOpen = v,
                                  () => w.WindowRectForTesting, r => w.WindowRectForTesting = r);
                }
                case TestCommandUiAction.StructureWindow:
                {
                    StructureListWindowUI w = ui.GetStructureListUI();
                    return Handle(() => w.IsOpen, v => w.IsOpen = v,
                                  () => w.WindowRectForTesting, r => w.WindowRectForTesting = r);
                }
                case TestCommandUiAction.SettingsWindow:
                {
                    SettingsWindowUI w = ui.GetSettingsWindowUI();
                    return Handle(() => w.IsOpen, v => w.IsOpen = v,
                                  () => w.WindowRectForTesting, r => w.WindowRectForTesting = r);
                }
                case TestCommandUiAction.SpawnControlWindow:
                {
                    SpawnControlUI w = ui.GetSpawnControlUI();
                    return Handle(() => w.IsOpen, v => w.IsOpen = v,
                                  () => w.WindowRectForTesting, r => w.WindowRectForTesting = r);
                }
                case TestCommandUiAction.GloopsWindow:
                {
                    GloopsRecorderUI w = ui.GetGloopsUI();
                    return Handle(() => w.IsOpen, v => w.IsOpen = v,
                                  () => w.WindowRectForTesting, r => w.WindowRectForTesting = r);
                }
                case TestCommandUiAction.TestRunnerWindow:
                {
                    TestRunnerUI w = ui.GetTestRunnerUI();
                    return Handle(() => w.IsOpen, v => w.IsOpen = v,
                                  () => w.WindowRectForTesting, r => w.WindowRectForTesting = r);
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(window), window);
            }
        }

        private static UiWindowHandle Handle(Func<bool> getOpen, Action<bool> setOpen,
                                             Func<Rect> getRect, Action<Rect> setRect,
                                             Func<int> getTab = null,
                                             Action<int> setTab = null)
            => new UiWindowHandle
            {
                GetOpen = getOpen,
                SetOpen = setOpen,
                GetRect = getRect,
                SetRect = setRect,
                GetTab = getTab,
                SetTab = setTab,
            };

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
