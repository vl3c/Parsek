using System;
using System.Collections.Generic;
using Parsek.UI.Gallery;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// GUI-state-gallery partial: the applier for <c>UiAction op=mock</c>. The vocabulary,
    /// the arg parses, every refusal token, the label derivation and the witness predicate
    /// live in the pure sibling <see cref="TestCommandUiMock"/>; the catalogue lives in
    /// <c>Source/Parsek/UI/Gallery/</c>. This file installs ONE window's injection member,
    /// arms a GUI-tree capture, and asserts the mocked model was DRAWN.
    ///
    /// <para><b>ONE RESOLVER ROW PER WINDOW, for the reason
    /// <c>ResolveWindowHandle</c>'s own header gives.</b> The first draft of that resolver
    /// was four parallel switches over the same window names, so a mapping error - the
    /// kerbals arm reaching the career window's field - lived in ONE of eight arms while
    /// the other seven stayed right and the whole xUnit suite passed either way. The mock
    /// arms below are the same shape: one row per window carrying its install AND its
    /// restore, built together so they cannot describe different members.</para>
    ///
    /// <para><b>THE READ-BACK IS THE CAPTURE.</b> Reading back the field just written is
    /// the vacuous read-back <c>op=rect</c> had to be fixed for. Only the DRAW witnesses a
    /// data swap, so the settle arms
    /// <c>GuiTreeRecorder.ArmForNextRepaint(label, writeToDisk: false)</c> - the
    /// <c>op=find</c> mechanism - and asserts the payload's own derived witness strings
    /// appear in the tree the frame produced.</para>
    ///
    /// <para><b>NOTHING HERE CAN REACH A SAVE.</b> Every member it writes is a UI-layer
    /// field (<c>CachedViewModelForTesting</c>, <c>CachedVMForTesting</c>, the Structure
    /// window's own target snapshot) and the session is a static on an automation-only
    /// type. <c>ParsekScenario.OnSave</c> reads none of them, so a save taken mid-mock
    /// writes the real game - by construction, not by a finally block. The grep gate
    /// <c>scripts/grep-audit-gui-mock-writeset.ps1</c> keeps that true.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        /// <summary>
        /// The label the in-memory apply capture is armed under. It never reaches a file,
        /// so it exists only to name the capture in <c>KSP.log</c>'s <c>[GuiTree]</c>
        /// line - a fixed, obviously-internal string for the reason
        /// <c>UiFindCaptureLabel</c> is one: a reader scanning the log can tell a mock
        /// settle's capture from a census dump at a glance.
        /// </summary>
        private const string UiMockCaptureLabel = "seam-mock";

        /// <summary>One window's mock install, built as ONE row so the install and the
        /// restore cannot describe different members.</summary>
        private struct UiMockArm
        {
            /// <summary>Installs the payload and returns the closure that puts every
            /// touched member back.</summary>
            internal Func<GuiMockPayload, GuiMockState, Action> Install;
        }

        // ----- execute -----

        private void UiActionMockOp(ParsedCommand cmd, ParsekUI ui, TestCommandScene scene)
        {
            TestCommandUiMock.MockIntent intent;
            string intentReject;
            if (!TestCommandUiMock.TryResolveIntent(
                    ArgOrNull(cmd, TestCommandUiMock.MockStateArg),
                    ArgOrNull(cmd, TestCommandUiMock.DescribeArg),
                    out intent, out intentReject))
            {
                ParsekLog.Warn(GuiMockSession.LogTag,
                    "mock rejected reason=" + intentReject
                    + " " + TestCommandUiMock.MockStateArg + "="
                    + (ArgOrNull(cmd, TestCommandUiMock.MockStateArg) ?? "-")
                    + " " + TestCommandUiMock.DescribeArg + "="
                    + (ArgOrNull(cmd, TestCommandUiMock.DescribeArg) ?? "-"));
                SetExecResult("REJECTED", null,
                    intentReject == TestCommandUiMock.ArgMissingReason
                        ? intentReject + " valid=" + TestCommandUiMock.ValidRefusalNames
                        : intentReject);
                return;
            }

            if (intent == TestCommandUiMock.MockIntent.Describe)
            {
                UiMockDescribe();
                return;
            }
            if (intent == TestCommandUiMock.MockIntent.Clear)
            {
                UiMockClear(ArgOrNull(cmd, "window"));
                return;
            }

            UiMockApply(cmd, ui, scene);
        }

        // ----- describe -----

        private void UiMockDescribe()
        {
            IReadOnlyList<GuiMockState> states = GuiMockCatalogue.All;
            var ids = new List<string>(states.Count);
            for (int i = 0; i < states.Count; i++) ids.Add(states[i].Id);
            string windows = GuiMockCatalogue.WindowsWithStates();

            ParsekLog.Info(GuiMockSession.LogTag,
                "mock describe catalogue=" + GuiMockSession.CatalogueId
                + " states=" + Int(ids.Count)
                + " windows=" + windows
                + " supported=" + GuiMockCatalogue.SupportedWindowNames
                + " live=" + (GuiMockSession.StateId ?? "-"));
            SetExecResult("OK", TestCommandUiMock.BuildDescribePayload(
                GuiMockSession.CatalogueId, ids, windows,
                GuiMockCatalogue.SupportedWindowNames, GuiMockSession.StateId), null);
        }

        // ----- clear (the paired op) -----

        private void UiMockClear(string rawWindow)
        {
            // The clear READS `window=`, which the first version ignored: hlib requires
            // the arg on every non-describe form and design 18.2 says the seam does too,
            // so ignoring it made the doc, the validator and the code disagree - and a
            // clear naming the WRONG window still tore down the live scope.
            UiWindowSpec spec;
            string winReject;
            if (!TestCommandUiAction.TryResolveWindow(rawWindow, out spec, out winReject))
            {
                ParsekLog.Warn(GuiMockSession.LogTag,
                    "mock rejected reason=" + winReject + " op=clear window="
                    + (rawWindow ?? "-"));
                SetExecResult("REJECTED", null,
                    winReject == TestCommandUiAction.WindowUnknownReason
                        ? winReject + " window=" + (rawWindow ?? string.Empty)
                          + " valid=" + TestCommandUiAction.ValidWindowNames
                        : winReject);
                return;
            }

            string stateId = GuiMockSession.StateId;
            string window = GuiMockSession.Window;

            if (window != null
                && !string.Equals(window, spec.Name, StringComparison.Ordinal))
            {
                // A clear that names another window is a lane bug, and tearing the scope
                // down anyway would hide it: the lane would believe it cleared one window
                // and have cleared a different one.
                ParsekLog.Warn(GuiMockSession.LogTag,
                    "mock rejected reason=" + TestCommandUiMock.StateWindowMismatchReason
                    + " op=clear window=" + spec.Name + " liveWindow=" + window
                    + " state=" + (stateId ?? "-"));
                SetExecResult("REJECTED", null,
                    TestCommandUiMock.StateWindowMismatchReason + " window=" + spec.Name
                    + " liveWindow=" + window
                    + " (the live scope belongs to another window; clear THAT one)");
                return;
            }
            int held = GuiMockSession.IsLive
                ? Time.frameCount - GuiMockSession.AppliedFrame
                : -1;

            // A scope that LOST its model mid-flight is reported on the clear too, so a
            // lane that took a capture without polling the apply still learns the picture
            // it got was not the state it asked for.
            string broken = GuiMockSession.BrokenReason;

            Exception failure;
            bool cleared = GuiMockSession.Clear("seam-clear", held, out failure);
            if (failure != null)
            {
                // Restore threw: the session is already dropped, so the next apply starts
                // clean. Force the window shut, which is the one state a half-restored
                // window can be put into without reading whatever it now holds.
                ForceCloseMockedWindow(window);
                SetExecResult("ERROR", null,
                    TestCommandUiMock.RestoreFailedReason + " window=" + (window ?? "-")
                    + " state=" + (stateId ?? "-"));
                return;
            }

            if (broken != null)
            {
                ParsekLog.Error(GuiMockSession.LogTag,
                    "mock clear reason=" + TestCommandUiMock.ScopeBrokenReason
                    + " state=" + (stateId ?? "-") + " window=" + (window ?? "-")
                    + " brokenReason=" + broken);
                SetExecResult("ERROR", null,
                    TestCommandUiMock.ScopeBrokenReason + " state=" + (stateId ?? "-")
                    + " reason=" + broken);
                return;
            }

            // Clearing with nothing live is an OK with cleared=false, not a refusal (the
            // `already=` rule): a lane's teardown step must stay safe to run after a state
            // that was never applied, or one refused apply cascades into every later step.
            ParsekLog.Info(GuiMockSession.LogTag,
                "mock clear cleared=" + Bool(cleared)
                + " window=" + (window ?? "-") + " state=" + (stateId ?? "-"));
            SetExecResult("OK",
                TestCommandUiMock.BuildClearPayload(cleared, window, stateId), null);
        }

        // ----- apply -----

        private void UiMockApply(ParsedCommand cmd, ParsekUI ui, TestCommandScene scene)
        {
            string stateId = ArgOrNull(cmd, TestCommandUiMock.MockStateArg);
            string rawWindow = ArgOrNull(cmd, "window");

            // The window arg is required for apply and clear and NOT for describe, which
            // is why op=mock is outside OpNeedsWindow - so this check lives here.
            UiWindowSpec spec;
            string winReject;
            if (!TestCommandUiAction.TryResolveWindow(rawWindow, out spec, out winReject))
            {
                ParsekLog.Warn(GuiMockSession.LogTag,
                    "mock rejected reason=" + winReject + " window=" + (rawWindow ?? "-"));
                SetExecResult("REJECTED", null,
                    winReject == TestCommandUiAction.WindowUnknownReason
                        ? winReject + " window=" + (rawWindow ?? string.Empty)
                          + " valid=" + TestCommandUiAction.ValidWindowNames
                        : winReject);
                return;
            }

            if (!GuiMockCatalogue.IsSupportedWindow(spec.Name))
            {
                ParsekLog.Warn(GuiMockSession.LogTag,
                    "mock rejected reason=" + TestCommandUiMock.WindowUnsupportedReason
                    + " window=" + spec.Name
                    + " supported=" + GuiMockCatalogue.SupportedWindowNames);
                SetExecResult("REJECTED", null,
                    TestCommandUiMock.WindowUnsupportedReason + " window=" + spec.Name
                    + " supported=" + GuiMockCatalogue.SupportedWindowNames);
                return;
            }

            if (!TestCommandUiAction.IsAvailableInScene(spec, scene))
            {
                ParsekLog.Warn(GuiMockSession.LogTag,
                    "mock rejected reason=" + TestCommandUiAction.WindowNotInSceneReason
                    + " window=" + spec.Name
                    + " scene=" + TestCommandUiAction.SceneToken(scene));
                SetExecResult("REJECTED", null,
                    TestCommandUiAction.WindowNotInSceneReason + " window=" + spec.Name
                    + " scene=" + TestCommandUiAction.SceneToken(scene));
                return;
            }

            GuiMockState state = GuiMockCatalogue.ById(stateId);
            if (state == null)
            {
                int known = GuiMockCatalogue.ForWindow(spec.Name).Count;
                ParsekLog.Warn(GuiMockSession.LogTag,
                    "mock rejected reason=" + TestCommandUiMock.StateUnknownReason
                    + " " + TestCommandUiMock.MockStateArg + "=" + (stateId ?? "-")
                    + " window=" + spec.Name + " ids=" + Int(known));
                SetExecResult("REJECTED", null,
                    TestCommandUiMock.StateUnknownReason + " " + TestCommandUiMock.MockStateArg
                    + "=" + (stateId ?? string.Empty) + " window=" + spec.Name
                    + " ids=" + Int(known));
                return;
            }

            if (!string.Equals(state.Window, spec.Name, StringComparison.Ordinal))
            {
                ParsekLog.Warn(GuiMockSession.LogTag,
                    "mock rejected reason=" + TestCommandUiMock.StateWindowMismatchReason
                    + " " + TestCommandUiMock.MockStateArg + "=" + state.Id
                    + " stateWindow=" + state.Window + " window=" + spec.Name);
                SetExecResult("REJECTED", null,
                    TestCommandUiMock.StateWindowMismatchReason + " state=" + state.Id
                    + " stateWindow=" + state.Window + " window=" + spec.Name);
                return;
            }

            if (state.Scene.HasValue && state.Scene.Value != scene)
            {
                ParsekLog.Warn(GuiMockSession.LogTag,
                    "mock rejected reason=" + TestCommandUiMock.RefusedSceneReason
                    + " state=" + state.Id
                    + " want=" + TestCommandUiAction.SceneToken(state.Scene.Value)
                    + " scene=" + TestCommandUiAction.SceneToken(scene));
                SetExecResult("REJECTED", null,
                    TestCommandUiMock.RefusedSceneReason + " state=" + state.Id
                    + " want=" + TestCommandUiAction.SceneToken(state.Scene.Value));
                return;
            }

            // Mirrors complexity-refused-gloops-recording, reached through the SAME
            // production predicate op=complexity uses (ParsekUI.WouldRefuseModeChange
            // consults ParsekUI.IsGloopsRecordingNow): a recording is the one live
            // condition under which nothing automation-only should swap a window's data
            // out from under the recorder.
            if (ParsekUI.WouldRefuseModeChange(UiComplexityMode.Basic))
            {
                ParsekLog.Warn(GuiMockSession.LogTag,
                    "mock rejected reason=" + TestCommandUiMock.RefusedRecordingReason
                    + " state=" + state.Id);
                SetExecResult("REJECTED", null, TestCommandUiMock.RefusedRecordingReason);
                return;
            }

            // The complexity mode decides whether this window can be ON SCREEN at all.
            // Basic hides the Kerbals and Career launchers and the mode switch
            // force-closes both, so a Basic apply would photograph a window no player can
            // open. Checked against the PRODUCTION predicate rather than a per-state pin.
            UiComplexityMode appliedMode = ParsekUI.AppliedUiComplexityMode;
            if (!GuiMockCatalogue.IsMockableInMode(spec.Name, appliedMode))
            {
                ParsekLog.Warn(GuiMockSession.LogTag,
                    "mock rejected reason=" + TestCommandUiMock.RefusedModeReason
                    + " state=" + state.Id + " window=" + spec.Name
                    + " mode=" + TestCommandUiAction.ModeToken(
                        appliedMode == UiComplexityMode.Basic));
                SetExecResult("REJECTED", null,
                    TestCommandUiMock.RefusedModeReason + " window=" + spec.Name
                    + " mode=" + TestCommandUiAction.ModeToken(
                        appliedMode == UiComplexityMode.Basic)
                    + " (this window's launcher is hidden in that mode, so no player can "
                    + "have it on screen; set op=complexity mode=advanced first)");
                return;
            }

            if (GuiMockSession.IsLive)
            {
                ParsekLog.Warn(GuiMockSession.LogTag,
                    "mock rejected reason=" + TestCommandUiMock.RefusedSessionLiveReason
                    + " state=" + state.Id + " live=" + GuiMockSession.StateId
                    + " liveWindow=" + GuiMockSession.Window);
                SetExecResult("REJECTED", null,
                    TestCommandUiMock.RefusedSessionLiveReason
                    + " live=" + GuiMockSession.StateId);
                return;
            }

            GuiMockPayload payload;
            try
            {
                payload = state.Build();
            }
            catch (Exception ex)
            {
                // A throwing builder creates NO session, so there is nothing to restore.
                ParsekLog.Error(GuiMockSession.LogTag,
                    "mock build threw state=" + state.Id + " window=" + spec.Name
                    + ": " + ex.GetType().Name + ": " + ex.Message);
                SetExecResult("ERROR", null, TestCommandUiAction.ThrewReason);
                return;
            }

            List<string> witnesses = GuiMockWitness.Expected(payload, state.Tab,
                                                             state.Covers);
            if (witnesses.Count == 0)
            {
                // A state whose payload produced nothing drawable would make its own
                // read-back vacuous. The catalogue unit suite refuses such a state, so
                // this is the belt to those braces rather than a reachable path.
                ParsekLog.Error(GuiMockSession.LogTag,
                    "mock rejected reason=" + TestCommandUiMock.NotAppliedReason
                    + " state=" + state.Id + " (no derivable witness)");
                SetExecResult("ERROR", null,
                    TestCommandUiMock.NotAppliedReason + " state=" + state.Id
                    + " (no derivable witness)");
                return;
            }

            // ---- PHASE 1: chrome only, then capture the window as it is WITHOUT the
            // mock. Every witness must be ABSENT from that baseline, which is what turns
            // "this string is plausible" into "only the mock put it there" - a witness the
            // real window already draws is no witness at all, and the review found seven
            // states in that shape. The chrome (open / rect / tab) is applied FIRST so the
            // baseline is of the same window at the same size and tab: a baseline taken
            // over a closed window would be empty and prove nothing.
            Action restoreChrome;
            try
            {
                restoreChrome = InstallMockChrome(ui, spec, state);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(GuiMockSession.LogTag,
                    "mock chrome install threw state=" + state.Id
                    + " window=" + spec.Name + ": "
                    + ex.GetType().Name + ": " + ex.Message);
                ForceCloseMockedWindow(spec.Name);
                SetExecResult("ERROR", null, TestCommandUiAction.ThrewReason);
                return;
            }

            string armReason;
            if (!TryArmMockCapture(out armReason))
            {
                restoreChrome();
                SetExecResult("ERROR", null,
                    TestCommandUiMock.NotAppliedReason + " reason=" + armReason);
                return;
            }

            bool basic = ParsekUI.AppliedUiComplexityMode == UiComplexityMode.Basic;
            uiActionPending = new UiActionPending
            {
                Op = UiActionOp.Mock,
                Window = spec.Name,
                StartFrame = Time.frameCount,
                MockStateId = state.Id,
                MockWitnesses = witnesses,
                MockBasicMode = basic,
                MockCovers = state.Covers != null ? state.Covers.Length : 0,
                MockLabel = TestCommandUiMock.DeriveLabel(
                    TestCommandUiMock.DefaultLabelPrefix, state.Id, basic),
                FindCaptureSeqAtArm = GuiTreeRecorder.CaptureSeq,
                MockAwaitingBaseline = true,
                MockPayload = payload,
                MockRestoreChrome = restoreChrome,
            };

            ParsekLog.Info(GuiMockSession.LogTag,
                "mock apply state=" + state.Id + " window=" + spec.Name
                + " tab=" + (state.Tab ?? "-")
                + " mode=" + TestCommandUiAction.ModeToken(basic)
                + " scene=" + TestCommandUiAction.SceneToken(scene)
                + " covers=" + Int(state.Covers != null ? state.Covers.Length : 0)
                + " witness=" + Int(witnesses.Count)
                + " frame=" + Int(Time.frameCount)
                + " note=" + (state.Note ?? "-")
                + " (awaiting the pre-apply baseline capture)");
            SetExecResult(PendingVerdict, null, null);
        }

        /// <summary>
        /// Arms one in-memory GUI-tree capture, reporting the recorder's own refusal
        /// reason rather than a bare bool. Shared by both phases so the arm's three
        /// failure shapes (a throwing Harmony apply, a refused arm, a disarmed recorder)
        /// are handled once.
        /// </summary>
        private bool TryArmMockCapture(out string reason)
        {
            reason = null;
            string path;
            try
            {
                path = GuiTreeRecorder.ArmForNextRepaint(
                    UiMockCaptureLabel, writeToDisk: false);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(GuiMockSession.LogTag,
                    "mock arm threw: " + ex.GetType().Name + ": " + ex.Message);
                GuiTreeRecorder.Disarm(GuiTreeRecorder.ArmThrewDisarmReason);
                reason = "arm-threw";
                return false;
            }
            if (string.IsNullOrEmpty(path))
            {
                reason = GuiTreeRecorder.LastArmRefusalReason ?? "arm-refused";
                ParsekLog.Error(GuiMockSession.LogTag,
                    "mock arm refused reason=" + reason);
                return false;
            }
            return true;
        }

        // ----- settle: the DRAW-PRODUCED read-back -----

        private void TryCompleteUiActionMock(double now)
        {
            bool captured =
                GuiTreeRecorder.CaptureSeq != uiActionPending.FindCaptureSeqAtArm;
            bool idle = !GuiTreeRecorder.HasPendingWork;
            int faults = GuiTreeRecorder.FaultsSinceArm;
            double budget = DeferralBudget.BudgetSeconds("UiAction");
            bool expired = DeferralBudget.ShouldTimeout(completionStartedAt, now, budget);

            GuiTreeDumpPollOutcome outcome = TestCommandDumpGuiTree.DecidePoll(
                captured, idle, faults, expired);
            if (outcome == GuiTreeDumpPollOutcome.NotYet)
                return;

            string id = completionId;
            long seq = completionSeq;
            string verb = completionVerb;
            UiActionPending pending = uiActionPending;
            int heldFrames = Time.frameCount - pending.StartFrame;

            // PHASE 1 -> PHASE 2. The baseline capture has landed: read the window as it
            // is WITHOUT the mock, install the data, and arm again. Everything else in
            // this method is phase 2, so the phase flag is consumed here and the pending
            // struct is re-armed wholesale (the struct-assignment rule).
            if (pending.MockAwaitingBaseline)
            {
                if (outcome != GuiTreeDumpPollOutcome.Settled)
                {
                    ClearTwoPhase();
                    ParsekLog.Error(GuiMockSession.LogTag,
                        "mock baseline capture failed reason="
                        + TestCommandUiMock.NotAppliedReason
                        + " state=" + (pending.MockStateId ?? "-")
                        + " window=" + (pending.Window ?? "-")
                        + " outcome=" + outcome);
                    if (pending.MockRestoreChrome != null) pending.MockRestoreChrome();
                    EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                        TestCommandUiMock.NotAppliedReason + " outcome=" + outcome
                        + " phase=baseline",
                        dequeueHead: true);
                    return;
                }

                var baseline = new List<string>();
                CollectTreeTexts(GuiTreeRecorder.LastTree, baseline,
                                 WindowIdForMock(pending.Window));

                Action restoreData;
                try
                {
                    restoreData = InstallMockData(ParsekUI.ActiveInstance, pending.Window,
                                                  pending.MockStateId, pending.MockPayload);
                }
                catch (Exception ex)
                {
                    ClearTwoPhase();
                    ParsekLog.Error(GuiMockSession.LogTag,
                        "mock data install threw state=" + (pending.MockStateId ?? "-")
                        + " window=" + (pending.Window ?? "-") + ": "
                        + ex.GetType().Name + ": " + ex.Message);
                    if (pending.MockRestoreChrome != null) pending.MockRestoreChrome();
                    ForceCloseMockedWindow(pending.Window);
                    EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                        TestCommandUiAction.ThrewReason, dequeueHead: true);
                    return;
                }

                Action restoreChrome = pending.MockRestoreChrome;
                if (!GuiMockSession.Begin(
                        pending.MockStateId, pending.Window, Time.frameCount,
                        DateTime.UtcNow.ToString(
                            "yyyy-MM-ddTHH:mm:ssZ",
                            System.Globalization.CultureInfo.InvariantCulture),
                        () => { restoreData(); if (restoreChrome != null) restoreChrome(); }))
                {
                    ClearTwoPhase();
                    // Unreachable: the execute path refused a live scope. Unwind rather
                    // than leak the install if it ever became reachable.
                    restoreData();
                    if (restoreChrome != null) restoreChrome();
                    EmitExecutedTerminal(id, seq, verb, "REJECTED", null,
                        TestCommandUiMock.RefusedSessionLiveReason, dequeueHead: true);
                    return;
                }

                string armReason;
                if (!TryArmMockCapture(out armReason))
                {
                    ClearTwoPhase();
                    GuiMockSession.Clear("arm-failed", 0);
                    EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                        TestCommandUiMock.NotAppliedReason + " reason=" + armReason,
                        dequeueHead: true);
                    return;
                }

                ParsekLog.Info(GuiMockSession.LogTag,
                    "mock baseline state=" + (pending.MockStateId ?? "-")
                    + " window=" + (pending.Window ?? "-")
                    + " baselineNodes=" + Int(baseline.Count)
                    + " (installed; awaiting the applied capture)");

                pending.MockAwaitingBaseline = false;
                pending.MockBaseline = baseline;
                pending.MockPayload = null;
                pending.MockRestoreChrome = null;
                pending.StartFrame = Time.frameCount;
                pending.FindCaptureSeqAtArm = GuiTreeRecorder.CaptureSeq;
                uiActionPending = pending;
                return;
            }

            ClearTwoPhase();

            if (outcome != GuiTreeDumpPollOutcome.Settled)
            {
                // ONE wire reason, three causes, separated on the log line: a patch body
                // threw during the captured frame, nothing drew IMGUI through a patched
                // funnel, or the budget ran out. Restore before answering - the mock must
                // not outlive its own failure.
                ParsekLog.Error(GuiMockSession.LogTag,
                    "mock not applied reason=" + TestCommandUiMock.NotAppliedReason
                    + " state=" + (pending.MockStateId ?? "-")
                    + " window=" + (pending.Window ?? "-")
                    + " outcome=" + outcome + " faults=" + Int(faults)
                    + " recorderReason=" + (GuiTreeRecorder.LastDisarmReason ?? "none"));
                GuiMockSession.Clear("settle-" + outcome, heldFrames);
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    TestCommandUiMock.NotAppliedReason + " outcome=" + outcome,
                    dequeueHead: true);
                return;
            }

            // A scope observed to have LOST its model cannot report OK whatever the tree
            // says: the window drew something, and it was not this state. Checked ahead of
            // the witness walk because the reason is more specific - it names a missing
            // suppression site rather than a bad state or a bad lane.
            if (GuiMockSession.IsBroken)
            {
                string brokenReason = GuiMockSession.BrokenReason;
                ParsekLog.Error(GuiMockSession.LogTag,
                    "mock scope broken reason=" + TestCommandUiMock.ScopeBrokenReason
                    + " state=" + (pending.MockStateId ?? "-")
                    + " window=" + (pending.Window ?? "-")
                    + " brokenReason=" + (brokenReason ?? "-")
                    + " frames=" + Int(heldFrames));
                GuiMockSession.Clear("scope-broken", heldFrames);
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    TestCommandUiMock.ScopeBrokenReason
                    + " window=" + (pending.Window ?? string.Empty)
                    + " state=" + (pending.MockStateId ?? string.Empty)
                    + " reason=" + (brokenReason ?? string.Empty),
                    dequeueHead: true);
                return;
            }

            var drawn = new List<string>();
            CollectTreeTexts(GuiTreeRecorder.LastTree, drawn, WindowIdForMock(pending.Window));

            string missing;
            if (!TestCommandUiMock.WitnessesDrawn(pending.MockWitnesses, drawn, out missing))
            {
                // The capture succeeded and the mocked model is not in it. This is the
                // whole point of the op's settle: the window drew SOMETHING, and it was
                // not the model this op installed.
                ParsekLog.Error(GuiMockSession.LogTag,
                    "mock not applied reason=" + TestCommandUiMock.NotAppliedReason
                    + " state=" + (pending.MockStateId ?? "-")
                    + " window=" + (pending.Window ?? "-")
                    + " missing=" + (missing ?? "-")
                    + " witness=" + Int(pending.MockWitnesses != null
                                        ? pending.MockWitnesses.Count : 0)
                    + " drawnNodes=" + Int(drawn.Count)
                    + " frames=" + Int(heldFrames));
                GuiMockSession.Clear("not-applied", heldFrames);
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    TestCommandUiMock.NotAppliedReason
                    + " window=" + (pending.Window ?? string.Empty)
                    + " state=" + (pending.MockStateId ?? string.Empty)
                    + " missing=" + (missing ?? string.Empty),
                    dequeueHead: true);
                return;
            }

            // EVERY witness must be ABSENT from the pre-apply baseline of the SAME
            // window. Without this the read-back only proves a plausible string was on
            // screen; with it, the string can only have come from the mock. It is also the
            // one check that catches a witness the real window happens to draw - which is
            // a CATALOGUE fault (a witness that says nothing about the state), so the
            // reason names the state rather than the lane.
            string shared;
            if (!TestCommandUiMock.WitnessesAbsentFromBaseline(
                    pending.MockWitnesses, pending.MockBaseline, out shared))
            {
                ParsekLog.Error(GuiMockSession.LogTag,
                    "mock not applied reason=" + TestCommandUiMock.NotAppliedReason
                    + " state=" + (pending.MockStateId ?? "-")
                    + " window=" + (pending.Window ?? "-")
                    + " witnessAlreadyDrawn=" + (shared ?? "-")
                    + " baselineNodes=" + Int(pending.MockBaseline != null
                                              ? pending.MockBaseline.Count : 0)
                    + " (the unmocked window already drew this string, so it witnesses "
                    + "nothing)");
                GuiMockSession.Clear("witness-in-baseline", heldFrames);
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    TestCommandUiMock.NotAppliedReason
                    + " window=" + (pending.Window ?? string.Empty)
                    + " state=" + (pending.MockStateId ?? string.Empty)
                    + " witnessAlreadyDrawn=" + (shared ?? string.Empty),
                    dequeueHead: true);
                return;
            }

            ParsekLog.Info(GuiMockSession.LogTag,
                "mock applied state=" + (pending.MockStateId ?? "-")
                + " nodes=" + Int(drawn.Count) + " readback=ok"
                + " witness=" + Int(pending.MockWitnesses != null
                                    ? pending.MockWitnesses.Count : 0)
                + " frames=" + Int(heldFrames));
            EmitExecutedTerminal(id, seq, verb, "OK",
                TestCommandUiMock.BuildApplyPayload(
                    pending.Window, pending.MockStateId, pending.MockCovers,
                    pending.MockWitnesses != null ? pending.MockWitnesses.Count : 0,
                    pending.MockLabel, pending.MockBasicMode, pending.StartFrame),
                null, dequeueHead: true);
        }

        /// <summary>
        /// Every non-empty text drawn INSIDE the target window, depth-first. The witness
        /// predicate matches by CONTAINS over this set (see
        /// <c>TestCommandUiMock.WitnessesDrawn</c>).
        ///
        /// <para><b>WINDOW-SCOPED, and the scope is the point.</b> A whole-tree walk would
        /// accept a witness some OTHER window happened to draw - the main window's
        /// launcher labels, the Timeline's own rows, a tooltip strip - which turns the
        /// read-back into "was this string anywhere on screen" rather than "did THIS
        /// window draw the mocked model". The window is matched by the same
        /// <c>windowId</c> <c>op=find</c> uses (each window class publishes the key its id
        /// is hashed from), which is exact and cannot drift.</para>
        ///
        /// <para>A window ABSENT from the capture yields an empty set, so the witness walk
        /// answers <c>mock-not-applied</c> - which is the honest reading of "the frame did
        /// not draw this window".</para>
        /// </summary>
        private static void CollectTreeTexts(GuiTreeResult tree, List<string> into,
                                             int windowId)
        {
            if (tree == null) return;
            GuiTreeNode windowNode = FindWindowNode(tree, windowId);
            if (windowNode == null) return;
            CollectTreeTexts(windowNode, into);
        }

        /// <summary>The IMGUI window id of one mockable window, resolved through the same
        /// handle row the rest of the seam uses. Zero when the host is gone, which makes
        /// the scoped walk find nothing and the settle answer not-applied.</summary>
        private static int WindowIdForMock(string window)
        {
            ParsekUI ui = ParsekUI.ActiveInstance;
            if (ui == null || string.IsNullOrEmpty(window)) return 0;
            try
            {
                UiWindowHandle handle = ResolveWindowHandle(ui, window);
                return handle.GetWindowId != null ? handle.GetWindowId() : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static void CollectTreeTexts(GuiTreeNode node, List<string> into)
        {
            if (node == null) return;
            if (!string.IsNullOrEmpty(node.Text)) into.Add(node.Text);
            if (!string.IsNullOrEmpty(node.TextValue)) into.Add(node.TextValue);
            for (int i = 0; i < node.Children.Count; i++)
                CollectTreeTexts(node.Children[i], into);
        }

        // ----- the per-window install rows -----

        /// <summary>
        /// Installs the payload into ONE window and returns the closure that puts every
        /// touched member back.
        ///
        /// <para>Each arm captures its previous values BEFORE writing anything, so the
        /// returned closure is a complete inverse of what it did. A window name the
        /// catalogue supports but this resolver does not throws loudly at the default
        /// rather than silently installing nothing and answering
        /// <c>mock-not-applied</c>.</para>
        /// </summary>
        private static Action InstallMockChrome(ParsekUI ui, UiWindowSpec spec,
                                                GuiMockState state)
        {
            UiWindowHandle handle = ResolveWindowHandle(ui, spec.Name);

            // The same three for every window: open flag, tab, rect. Captured here rather
            // than per arm so a new window's arm cannot forget one of them - and applied
            // BEFORE the data, so the pre-apply baseline capture is of the same window at
            // the same size and tab as the mocked one.
            bool prevOpen = handle.GetOpen();
            Rect prevRect = handle.GetRect();
            int prevTab = handle.GetTab != null ? handle.GetTab() : -1;

            if (state.RectW > 0 && state.RectH > 0)
                handle.SetRect(new Rect(prevRect.x, prevRect.y, state.RectW, state.RectH));
            if (state.Tab != null && handle.SetTab != null)
            {
                int tabIndex;
                string tabReject;
                if (TestCommandUiAction.TryResolveTab(spec, state.Tab, out tabIndex,
                                                      out tabReject))
                {
                    handle.SetTab(tabIndex);
                }
            }
            handle.SetOpen(true);

            return () =>
            {
                if (prevTab >= 0 && handle.SetTab != null) handle.SetTab(prevTab);
                handle.SetRect(prevRect);
                handle.SetOpen(prevOpen);
            };
        }

        /// <summary>
        /// Installs ONE window's mocked data and returns the closure that puts the member
        /// back. Every arm restores to the value that makes the window REBUILD its real
        /// model rather than to the pre-mock object: the invalidations suppressed during
        /// the scope were DEFERRED, not dropped, and putting a stale view model back would
        /// let a later real capture in the same boot photograph it.
        /// </summary>
        private static Action InstallMockData(ParsekUI ui, string window, string stateId,
                                              GuiMockPayload payload)
        {
            if (ui == null) throw new InvalidOperationException(
                "no live ParsekUI to install a mock into (window=" + window + ")");
            GuiMockState state = GuiMockCatalogue.ById(stateId);

            // TRANSACTIONAL. Each arm pushes an undo onto this stack as it writes, so a
            // throw part-way through unwinds what it had already done instead of leaving
            // half an install standing with no restore closure to put it back. The Kerbals
            // arm is the one that needs it - it writes a view model AND then N expand keys.
            var undo = new List<Action>();
            try
            {
                switch (window)
                {
                    case GuiMockSession.KerbalsWindow:
                        InstallKerbalsMock(ui, state, payload, undo);
                        break;
                    case GuiMockSession.CareerWindow:
                        InstallCareerMock(ui, payload, undo);
                        break;
                    case GuiMockSession.StructureWindow:
                        InstallStructureMock(ui, payload, undo);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(window), window);
                }
            }
            catch (Exception)
            {
                Unwind(undo);
                throw;
            }
            return () => Unwind(undo);
        }

        /// <summary>Runs an undo stack in REVERSE, swallowing nothing but continuing past
        /// a throw so one bad undo cannot orphan the rest.</summary>
        private static void Unwind(List<Action> undo)
        {
            for (int i = undo.Count - 1; i >= 0; i--)
            {
                try
                {
                    undo[i]();
                }
                catch (Exception ex)
                {
                    ParsekLog.Error(GuiMockSession.LogTag,
                        "mock undo step threw: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private static void InstallKerbalsMock(ParsekUI ui, GuiMockState state,
                                               GuiMockPayload payload, List<Action> undo)
        {
            KerbalsWindowUI w = ui.GetKerbalsUI();

            w.CachedViewModelForTesting = payload.Kerbals;
            // NULL on restore, not the pre-mock view model. The invalidations suppressed
            // during the scope were DEFERRED rather than dropped - the comment on
            // OnLiveCrewStateChanged says exactly that - so putting the old object back
            // would leave a stale roster a later REAL capture in the same boot could
            // photograph. Null is what the draw's own
            // `if (cachedVM == null) cachedVM = GatherViewModel()` rebuilds from.
            undo.Add(() => { w.CachedViewModelForTesting = null; });

            // The chain view and the plain-bucket fold are a SEPARATE transient set from
            // the view model, so a state whose point is an expanded chain has to drive it
            // - through the window's own expand setter, which is the same member
            // `op=expand` writes. One undo per key, pushed as it is written, so a throw
            // half-way through unwinds exactly what happened.
            for (int i = 0; i < state.ExpandKeys.Length; i++)
            {
                string key = state.ExpandKeys[i];
                if (!w.SetRosterExpandedForTesting(key, true)) continue;
                undo.Add(() => w.SetRosterExpandedForTesting(key, false));
            }
        }

        private static void InstallCareerMock(ParsekUI ui, GuiMockPayload payload,
                                              List<Action> undo)
        {
            CareerStateWindowUI w = ui.GetCareerStateUI();
            w.CachedVMForTesting = payload.Career;
            // NULL for the Kerbals reason: a suppressed ledger invalidation was deferred,
            // not dropped, and the draw always rebuilds a null cache (which is now the
            // FIRST thing ShouldRebuildCachedVM checks).
            undo.Add(() => { w.CachedVMForTesting = null; });
        }

        private static void InstallStructureMock(ParsekUI ui, GuiMockPayload payload,
                                                 List<Action> undo)
        {
            StructureListWindowUI w = ui.GetStructureListUI();
            // This window has no rebuild-on-null path - `steps` is written only by an
            // OpenFor* call - so its restore puts the captured target BACK rather than
            // nulling it. That asymmetry with the other two arms is the window's, not the
            // gallery's.
            StructureListWindowUI.GalleryTargetSnapshot prev = w.CaptureGalleryTarget();
            w.OpenWithGallerySteps(payload.Structure.RouteMode, payload.Structure.Title,
                                   payload.Structure.Steps);
            undo.Add(() => w.RestoreGalleryTarget(prev));
        }

        /// <summary>
        /// Shuts a window whose restore threw. It is the one state a half-restored window
        /// can be put into without reading whatever it now holds, and it leaves the next
        /// apply starting from a clean surface.
        /// </summary>
        private static void ForceCloseMockedWindow(string window)
        {
            if (string.IsNullOrEmpty(window)) return;
            try
            {
                ParsekUI ui = ParsekUI.ActiveInstance;
                if (ui == null) return;
                UiWindowHandle handle = ResolveWindowHandle(ui, window);
                handle.SetOpen(false);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(GuiMockSession.LogTag,
                    "mock force-close threw window=" + window + ": "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Clears any live mock scope. Called UNCONDITIONALLY from every exit path -
        /// scene change, level loaded, FlushAndQuit - which is the
        /// <c>ReleaseRaisedDialogInputLock</c> discipline: release on EVERY exit rather
        /// than on the happy one.
        /// </summary>
        internal static void ClearGuiMockSessionOnExit(string reason)
        {
            if (!GuiMockSession.IsLive) return;
            string window = GuiMockSession.Window;
            string stateId = GuiMockSession.StateId;
            ParsekLog.Warn(GuiMockSession.LogTag,
                "mock cleared by " + reason + " state=" + (stateId ?? "-")
                + " window=" + (window ?? "-"));
            Exception failure;
            GuiMockSession.Clear(reason, -1, out failure);
            if (failure != null) ForceCloseMockedWindow(window);
        }
    }
}
