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
                UiMockClear();
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

        private void UiMockClear()
        {
            string stateId = GuiMockSession.StateId;
            string window = GuiMockSession.Window;
            int held = GuiMockSession.IsLive
                ? Time.frameCount - GuiMockSession.AppliedFrame
                : -1;

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

            List<string> witnesses = GuiMockWitness.Expected(payload, state.Tab);

            Action restore;
            try
            {
                restore = InstallMock(ui, spec, state, payload);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(GuiMockSession.LogTag,
                    "mock install threw state=" + state.Id + " window=" + spec.Name
                    + ": " + ex.GetType().Name + ": " + ex.Message);
                // The install is transactional by construction: each arm captures its
                // previous values BEFORE writing, so a throw part-way leaves whatever it
                // had already written. Clear through the session machinery when one was
                // created; there is none here, so force the window shut instead.
                ForceCloseMockedWindow(spec.Name);
                SetExecResult("ERROR", null, TestCommandUiAction.ThrewReason);
                return;
            }

            if (!GuiMockSession.Begin(state.Id, spec.Name, Time.frameCount,
                                      DateTime.UtcNow.ToString(
                                          "yyyy-MM-ddTHH:mm:ssZ",
                                          System.Globalization.CultureInfo.InvariantCulture),
                                      restore))
            {
                // Unreachable: the live check above already refused. Restore rather than
                // leak the install if it ever became reachable.
                restore();
                SetExecResult("REJECTED", null, TestCommandUiMock.RefusedSessionLiveReason);
                return;
            }

            string armPath;
            try
            {
                armPath = GuiTreeRecorder.ArmForNextRepaint(
                    UiMockCaptureLabel, writeToDisk: false);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(GuiMockSession.LogTag,
                    "mock arm threw state=" + state.Id + ": "
                    + ex.GetType().Name + ": " + ex.Message);
                GuiTreeRecorder.Disarm(GuiTreeRecorder.ArmThrewDisarmReason);
                GuiMockSession.Clear("arm-threw", 0);
                SetExecResult("ERROR", null, TestCommandUiMock.NotAppliedReason);
                return;
            }
            if (string.IsNullOrEmpty(armPath))
            {
                string reason = GuiTreeRecorder.LastArmRefusalReason ?? "unknown";
                ParsekLog.Error(GuiMockSession.LogTag,
                    "mock arm refused state=" + state.Id + " reason=" + reason);
                GuiMockSession.Clear("arm-refused", 0);
                SetExecResult("ERROR", null,
                    TestCommandUiMock.NotAppliedReason + " reason=" + reason);
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
                + " (awaiting one capture)");
            SetExecResult(PendingVerdict, null, null);
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

            var drawn = new List<string>();
            CollectTreeTexts(GuiTreeRecorder.LastTree, drawn);

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

        /// <summary>Every non-empty text in a captured tree, depth-first. The witness
        /// predicate matches by CONTAINS over this set (see
        /// <c>TestCommandUiMock.WitnessesDrawn</c>).</summary>
        private static void CollectTreeTexts(GuiTreeResult tree, List<string> into)
        {
            if (tree == null) return;
            for (int i = 0; i < tree.Roots.Count; i++)
                CollectTreeTexts(tree.Roots[i], into);
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
        private static Action InstallMock(ParsekUI ui, UiWindowSpec spec,
                                          GuiMockState state, GuiMockPayload payload)
        {
            UiWindowHandle handle = ResolveWindowHandle(ui, spec.Name);

            // Chrome first, and the same for every window: open flag, tab, rect. Captured
            // and restored here rather than per arm so a new window's arm cannot forget
            // one of the three.
            bool prevOpen = handle.GetOpen();
            Rect prevRect = handle.GetRect();
            int prevTab = handle.GetTab != null ? handle.GetTab() : -1;

            Action restoreChrome = () =>
            {
                if (prevTab >= 0 && handle.SetTab != null) handle.SetTab(prevTab);
                handle.SetRect(prevRect);
                handle.SetOpen(prevOpen);
            };

            Action restoreData;
            switch (spec.Name)
            {
                case GuiMockSession.KerbalsWindow:
                    restoreData = InstallKerbalsMock(ui, state, payload);
                    break;
                case GuiMockSession.CareerWindow:
                    restoreData = InstallCareerMock(ui, payload);
                    break;
                case GuiMockSession.StructureWindow:
                    restoreData = InstallStructureMock(ui, payload);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(spec), spec.Name);
            }

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
                restoreData();
                restoreChrome();
            };
        }

        private static Action InstallKerbalsMock(ParsekUI ui, GuiMockState state,
                                                 GuiMockPayload payload)
        {
            KerbalsWindowUI w = ui.GetKerbalsUI();
            KerbalsWindowUI.KerbalsViewModel? prev = w.CachedViewModelForTesting;
            var expandedByUs = new List<string>();

            w.CachedViewModelForTesting = payload.Kerbals;
            // The chain view and the plain-bucket fold are a SEPARATE transient set from
            // the view model, so a state whose point is an expanded chain has to drive it
            // - through the window's own expand setter, which is the same member
            // `op=expand` writes.
            for (int i = 0; i < state.ExpandKeys.Length; i++)
            {
                if (w.SetRosterExpandedForTesting(state.ExpandKeys[i], true))
                    expandedByUs.Add(state.ExpandKeys[i]);
            }

            return () =>
            {
                for (int i = 0; i < expandedByUs.Count; i++)
                    w.SetRosterExpandedForTesting(expandedByUs[i], false);
                w.CachedViewModelForTesting = prev;
            };
        }

        private static Action InstallCareerMock(ParsekUI ui, GuiMockPayload payload)
        {
            CareerStateWindowUI w = ui.GetCareerStateUI();
            CareerStateWindowUI.CareerStateViewModel? prev = w.CachedVMForTesting;
            w.CachedVMForTesting = payload.Career;
            return () => { w.CachedVMForTesting = prev; };
        }

        private static Action InstallStructureMock(ParsekUI ui, GuiMockPayload payload)
        {
            StructureListWindowUI w = ui.GetStructureListUI();
            StructureListWindowUI.GalleryTargetSnapshot prev = w.CaptureGalleryTarget();
            w.OpenWithGallerySteps(payload.Structure.RouteMode, payload.Structure.Title,
                                   payload.Structure.Steps);
            return () => { w.RestoreGalleryTarget(prev); };
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
