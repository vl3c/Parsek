using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// GUI-census partial: the applier for <c>UiAction op=find</c>. The match ladder, the
    /// arg parses and the payload live in the pure sibling
    /// <see cref="TestCommandUiFind"/>; this file arms the GUI-tree recorder, waits for the
    /// capture, and flattens ONE window's subtree into the pure candidate shape.
    ///
    /// <para>
    /// WHY IT CAPTURES A TREE INSTEAD OF READING LAYOUT DIRECTLY. A control's rect exists
    /// only inside the IMGUI pass that drew it - <c>GUILayout</c> resolves rects during the
    /// pass and keeps nothing addressable afterwards. The GUI-tree recorder already
    /// intercepts exactly that pass and already converts every rect to screen space, so the
    /// honest way to answer "where is the Close button" is to record one frame and read it.
    /// The capture is armed with <c>writeToDisk: false</c>: a census calls find several
    /// times per lane and each dump would otherwise land a JSON beside the ones a reviewer
    /// is actually reading.
    /// </para>
    ///
    /// <para>
    /// WHY THE POLL IS NOT THE SHARED ONE-FRAME SETTLE. The recorder flushes from
    /// <c>LateUpdate</c> when the frame NUMBER has changed, so a tree armed in frame N's
    /// Update is assembled during frame N+1's LateUpdate - one frame after the shared settle
    /// would have declared victory and read a null <c>LastTree</c>. So this op reuses
    /// <c>TestCommandDumpGuiTree.DecidePoll</c>, the decision that already models exactly
    /// this wait, with <see cref="GuiTreeRecorder.CaptureSeq"/> moving as the written
    /// signal (the written-path pair deliberately does not move for an in-memory arm).
    /// </para>
    ///
    /// <para>
    /// WHY THE WINDOW IS MATCHED BY ID. Every window node the recorder emits carries the
    /// <c>windowId</c> Unity was handed, and each Parsek window class now publishes the key
    /// that id is hashed from (<c>WindowIdKey</c>). Matching on the TITLE would break on the
    /// Structure window, whose title is built from its target; matching on the RECT would
    /// race the one-frame skew between a window's rect field and the rect
    /// <c>GUILayout</c> resolved. The id is exact and cannot drift, because the seam reads
    /// the same const the draw call does.
    /// </para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        /// <summary>
        /// The label the in-memory find capture is armed under. It never reaches a file, so
        /// it exists only to name the capture in <c>KSP.log</c>'s <c>[GuiTree]</c> line -
        /// which is why it is a fixed, obviously-internal string rather than something a
        /// spec supplies: a reader scanning the log can tell a find's capture from a
        /// census dump at a glance.
        /// </summary>
        private const string UiFindCaptureLabel = "seam-find";

        // ----- execute -----

        private void UiActionFindOp(ParsedCommand cmd, UiWindowHandle handle,
                                    UiWindowSpec spec)
        {
            if (!TestCommandUiFind.TryParseText(
                    ArgOrNull(cmd, TestCommandUiFind.TextArg), out string text,
                    out string textReject))
            {
                ParsekLog.Warn(Tag, $"uiaction rejected reason={textReject} "
                    + $"window={spec.Name}");
                SetExecResult("REJECTED", null, $"{textReject} window={spec.Name}");
                return;
            }
            if (!TestCommandUiFind.TryParseCtrl(
                    ArgOrNull(cmd, TestCommandUiFind.CtrlArg), out string ctrl,
                    out string ctrlReject))
            {
                string raw = ArgOrNull(cmd, TestCommandUiFind.CtrlArg) ?? string.Empty;
                ParsekLog.Warn(Tag, $"uiaction rejected reason={ctrlReject} ctrl={raw}");
                SetExecResult("REJECTED", null,
                    $"{ctrlReject} ctrl={raw} valid={TestCommandUiFind.ValidCtrlNames}");
                return;
            }
            if (!TestCommandUiFind.TryParseIndex(
                    ArgOrNull(cmd, TestCommandUiFind.IndexArg), out int index,
                    out string indexReject))
            {
                string raw = ArgOrNull(cmd, TestCommandUiFind.IndexArg) ?? string.Empty;
                ParsekLog.Warn(Tag, $"uiaction rejected reason={indexReject} index={raw}");
                SetExecResult("REJECTED", null, $"{indexReject} index={raw}");
                return;
            }

            int windowId = handle.GetWindowId != null ? handle.GetWindowId() : 0;

            string path;
            try
            {
                path = GuiTreeRecorder.ArmForNextRepaint(
                    UiFindCaptureLabel, writeToDisk: false);
            }
            catch (Exception ex)
            {
                // Arming applies Harmony patches, which is real codegen and can throw. The
                // recorder disarms itself on that path; this is the belt to those braces,
                // idempotent by construction (the DumpGuiTree applier's own contract).
                ParsekLog.Error(Tag, $"uiaction find arm threw window={spec.Name}: "
                    + $"{ex.GetType().Name}: {ex.Message}");
                GuiTreeRecorder.Disarm(GuiTreeRecorder.ArmThrewDisarmReason);
                SetExecResult("ERROR", null, TestCommandUiFind.CaptureFailedReason);
                return;
            }
            if (string.IsNullOrEmpty(path))
            {
                string reason = GuiTreeRecorder.LastArmRefusalReason ?? "unknown";
                ParsekLog.Error(Tag, $"uiaction find arm refused window={spec.Name} "
                    + $"reason={reason}");
                SetExecResult("ERROR", null,
                    $"{TestCommandUiFind.CaptureFailedReason} reason={reason}");
                return;
            }

            uiActionPending = new UiActionPending
            {
                Op = UiActionOp.Find,
                Window = spec.Name,
                StartFrame = Time.frameCount,
                FindText = text,
                FindCtrl = ctrl,
                FindIndex = index,
                FindWindowId = windowId,
                FindCaptureSeqAtArm = GuiTreeRecorder.CaptureSeq,
            };
            ParsekLog.Info(Tag, $"uiaction find initiated window={spec.Name} "
                + $"windowId={Int(windowId)} text={text} "
                + $"ctrl={ctrl ?? "any"} index={Int(index)} (awaiting one capture)");
            SetExecResult(PendingVerdict, null, null);
        }

        // ----- settle -----

        private void TryCompleteUiActionFind(double now)
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
            ClearTwoPhase();

            if (outcome != GuiTreeDumpPollOutcome.Settled)
            {
                // ONE wire reason, three causes, separated on the log line: a patch body
                // threw during the captured frame (Faulted), nothing drew IMGUI through a
                // patched funnel so the recorder gave its arm up (GaveUp), or the verb's
                // budget ran out while it was still working (TimedOut).
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiFind.CaptureFailedReason
                    + $" window={pending.Window} outcome={outcome} faults={Int(faults)} "
                    + $"recorderReason={GuiTreeRecorder.LastDisarmReason ?? "none"}");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    $"{TestCommandUiFind.CaptureFailedReason} outcome={outcome}",
                    dequeueHead: true);
                return;
            }

            GuiTreeResult tree = GuiTreeRecorder.LastTree;
            GuiTreeNode windowNode = FindWindowNode(tree, pending.FindWindowId);
            if (windowNode == null)
            {
                // The capture succeeded and this window is not in it. Distinct from
                // control-not-found on purpose: the fix is to open the window (and the main
                // one, whose showUI gates every sub-window draw), not to fix the text.
                int windows = tree != null ? tree.WindowCount : 0;
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiFind.WindowNotDrawnReason
                    + $" window={pending.Window} windowId={Int(pending.FindWindowId)} "
                    + $"windowsInCapture={Int(windows)}");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    $"{TestCommandUiFind.WindowNotDrawnReason} window={pending.Window} "
                    + $"windowsInCapture={Int(windows)}",
                    dequeueHead: true);
                return;
            }

            var candidates = new List<UiFindCandidate>();
            Flatten(windowNode, candidates);

            if (!TestCommandUiFind.TryPick(
                    candidates, pending.FindText, pending.FindCtrl, pending.FindIndex,
                    out UiFindCandidate match, out UiFindMatchMode mode, out int rungCount,
                    out int searched, out string pickReject))
            {
                ParsekLog.Error(Tag, $"uiaction error reason={pickReject} "
                    + $"window={pending.Window} text={pending.FindText} "
                    + $"ctrl={pending.FindCtrl ?? "any"} index={Int(pending.FindIndex)} "
                    + $"matches={Int(rungCount)} searched={Int(searched)}");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    $"{pickReject} window={pending.Window} text={pending.FindText} "
                    + $"matches={Int(rungCount)} searched={Int(searched)}",
                    dequeueHead: true);
                return;
            }

            float cx, cy;
            TestCommandUiFind.CentreOf(match, out cx, out cy);
            ParsekLog.Info(Tag, $"uiaction find window={pending.Window} "
                + $"text={match.Text} ctrl={match.Kind} "
                + $"match={TestCommandUiFind.MatchToken(mode)} matches={Int(rungCount)} "
                + $"rect={Fmt(match.X)},{Fmt(match.Y)},{Fmt(match.W)},{Fmt(match.H)} "
                + $"centre={Fmt(cx)},{Fmt(cy)} searched={Int(searched)}");
            EmitExecutedTerminal(id, seq, verb, "OK",
                TestCommandUiFind.BuildPayload(pending.Window, match, mode, rungCount),
                null, dequeueHead: true);
        }

        // ----- tree walking -----

        /// <summary>
        /// The window node carrying <paramref name="windowId"/>, searched over the whole
        /// tree rather than over the roots alone: Parsek draws no nested window today, but
        /// a window node's depth is the recorder's business and not this op's.
        /// </summary>
        private static GuiTreeNode FindWindowNode(GuiTreeResult tree, int windowId)
        {
            if (tree == null) return null;
            for (int i = 0; i < tree.Roots.Count; i++)
            {
                GuiTreeNode hit = FindWindowNode(tree.Roots[i], windowId);
                if (hit != null) return hit;
            }
            return null;
        }

        private static GuiTreeNode FindWindowNode(GuiTreeNode node, int windowId)
        {
            if (node == null) return null;
            if (node.Kind == GuiNodeKind.Window && node.WindowId.HasValue
                && node.WindowId.Value == windowId)
            {
                return node;
            }
            for (int i = 0; i < node.Children.Count; i++)
            {
                GuiTreeNode hit = FindWindowNode(node.Children[i], windowId);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>
        /// Flattens a subtree into the pure candidate shape, DEPTH-FIRST in draw order,
        /// which is what makes <c>index=</c> mean "the nth one down the window".
        ///
        /// <para>Nodes with no text are skipped: a spec addresses a control by the words a
        /// reviewer can see, and a text-less rect (a layout group, a spacer, a box) is not
        /// addressable that way. Degenerate rects are skipped too - a zero-area control has
        /// no centre a cursor could land on, so answering with one would produce a pointer
        /// step that hovers whatever is behind it.</para>
        ///
        /// <para>The window node itself IS included, so <c>text=</c> can address the title
        /// bar - which is the only way to aim a pointer at a window's drag strip.</para>
        /// </summary>
        private static void Flatten(GuiTreeNode node, List<UiFindCandidate> into)
        {
            if (node == null) return;
            if (!string.IsNullOrEmpty(node.Text) && !node.Rect.IsDegenerate)
            {
                into.Add(new UiFindCandidate
                {
                    Text = node.Text,
                    Kind = GuiTreeAssembler.KindName(node.Kind),
                    X = node.Rect.X,
                    Y = node.Rect.Y,
                    W = node.Rect.W,
                    H = node.Rect.H,
                });
            }
            for (int i = 0; i < node.Children.Count; i++)
                Flatten(node.Children[i], into);
        }
    }
}
