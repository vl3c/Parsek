using System;
using System.Globalization;
using System.IO;

namespace Parsek.TestCommands
{
    /// <summary>
    /// GUI-census partial: the thin applier for the TWO-PHASE <c>DumpGuiTree</c> verb.
    /// Every decision is delegated to the pure sibling
    /// <see cref="TestCommandDumpGuiTree"/>; this file makes the one call into
    /// <see cref="GuiTreeRecorder.ArmForNextRepaint"/>, polls the recorder until it
    /// reports THIS arm's dump written, and stats the file for the payload's byte count.
    ///
    /// <para>
    /// WHY THE ARM IS MADE FROM HERE AND NOWHERE ELSE. The recorder refuses to arm from
    /// inside an IMGUI pass, because arming installs Harmony patches on the very methods a
    /// running GUI pass is executing and a half-drawn pass would be captured truncated.
    /// The seam pump runs in <c>Update</c> (<c>ParsekTestCommandAddon.Update</c>), which
    /// Unity runs BEFORE the frame's <c>OnGUI</c>, so this call site is outside a pass by
    /// construction - and the very next Repaint is the one recorded. A refusal here would
    /// mean the dispatch itself had moved, which is why it is an ERROR naming the
    /// recorder's own reason rather than a REJECTED.
    /// </para>
    ///
    /// <para>
    /// WHY THERE IS NO PRE-DELETE. <c>CaptureScreenshot</c> deletes a colliding target
    /// because its poll reads a FILE and a stale one would settle immediately, reporting
    /// OK for an older image. This verb's completion signal is the recorder's own
    /// <c>LastCaptureWritten</c> + <c>LastWrittenPath</c> pair, which the arm clears before
    /// anything else, so a stale file cannot satisfy it. The recorder overwrites the path
    /// on flush; a re-run of a census re-uses its labels and that is intended.
    /// </para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // Two-phase state for the in-flight dump. Only ever read by
        // TryCompleteDumpGuiTree while completionVerb == "DumpGuiTree", so it needs no
        // reset beyond what ClearTwoPhase already does for the shared fields.
        private string guiTreeLabel;

        // The absolute path the ARM returned. The poll compares the recorder's
        // LastWrittenPath against it rather than trusting the bare written flag: a dump
        // written for some other arm (the in-game GuiTree cell, a second seam step that
        // somehow overlapped) must never settle this one.
        private string guiTreeExpectedPath;

        private void DumpGuiTreeImpl(ParsedCommand cmd)
        {
            // (1) Arg. The parse is pure and its rejects are terminal-with-no-side-effect,
            // so the recorder has not been touched when either fires.
            if (!TestCommandDumpGuiTree.TryParseLabel(
                    ArgOrNull(cmd, "label"), out string label, out string labelReject))
            {
                ParsekLog.Warn(Tag, $"dumpguitree rejected reason={labelReject} "
                    + $"label={ArgOrNull(cmd, "label") ?? string.Empty}");
                SetExecResult("REJECTED", null,
                    $"{labelReject} label={ArgOrNull(cmd, "label") ?? string.Empty}");
                return;
            }

            // (2) Arm the recorder for the next Repaint pass. It returns the path the dump
            // will be written to, or null when it refused.
            string path;
            try
            {
                path = GuiTreeRecorder.ArmForNextRepaint(label);
            }
            catch (Exception ex)
            {
                // Applying 17 Harmony patches is real codegen and can throw. Treated as a
                // recorder FAULT rather than as its own terminal: from a spec's point of
                // view the recorder failed to produce a tree, which is exactly what
                // gui-tree-faulted names, and the exception type is on the Error line.
                ParsekLog.Error(Tag, $"dumpguitree arm threw label={label}: "
                    + $"{ex.GetType().Name}: {ex.Message}");
                SetExecResult("ERROR", null, TestCommandDumpGuiTree.FaultedReason);
                return;
            }

            if (string.IsNullOrEmpty(path))
            {
                string reason = GuiTreeRecorder.LastArmRefusalReason ?? "unknown";
                ParsekLog.Error(Tag, $"dumpguitree arm refused label={label} "
                    + $"reason={reason}; the recorder refuses to arm from inside an IMGUI "
                    + "pass and the seam pump runs in Update, so this means the dispatch moved");
                SetExecResult("ERROR", null,
                    $"{TestCommandDumpGuiTree.ArmRefusedReason} reason={reason}");
                return;
            }

            guiTreeLabel = label;
            guiTreeExpectedPath = path;

            ParsekLog.Info(Tag, $"dumpguitree armed label={label} path="
                + TestCommandDumpGuiTree.RelativePathFor(label));
            SetExecResult(PendingVerdict, null, null);
        }

        // Bounded, observable completion (the LoadGame contract): hold the head until the
        // recorder reports this arm's dump written, then OK; convert to a terminal ERROR
        // once the recorder gives up, faults, or the verb's budget expires, so an arm that
        // never sees a Repaint cannot wedge the run.
        private void TryCompleteDumpGuiTree(double now)
        {
            bool written = GuiTreeRecorder.LastCaptureWritten
                && PathMatchesArmedDump(GuiTreeRecorder.LastWrittenPath, guiTreeExpectedPath);
            bool idle = !GuiTreeRecorder.HasPendingWork;
            int faults = GuiTreeRecorder.FaultsSinceArm;

            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds("DumpGuiTree");
            bool expired = DeferralBudget.ShouldTimeout(completionStartedAt, now, budget);

            GuiTreeDumpPollOutcome outcome = TestCommandDumpGuiTree.DecidePoll(
                written, idle, faults, expired);
            if (outcome == GuiTreeDumpPollOutcome.NotYet)
                return;

            string id = completionId;
            long seq = completionSeq;
            string verb = completionVerb;
            string label = guiTreeLabel;
            string path = guiTreeExpectedPath;
            ClearTwoPhase();

            CultureInfo ic = CultureInfo.InvariantCulture;
            string elapsedText = elapsed.ToString("F1", ic);

            if (outcome == GuiTreeDumpPollOutcome.Faulted)
            {
                // A dump MAY exist (a fault leaves an open capture for the pump to flush),
                // and it is left where it is - it is evidence about the fault. What is
                // refused is calling it a census product.
                ParsekLog.Error(Tag, $"dumpguitree faulted label={label} "
                    + $"faults={Int(faults)} written={Bool(GuiTreeRecorder.LastCaptureWritten)} "
                    + $"elapsed={elapsedText}s; a patch body threw during the captured "
                    + "frame, so any dump on disk describes a frame the recorder stopped "
                    + "following");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    TestCommandDumpGuiTree.FaultedReason, dequeueHead: true);
                return;
            }

            if (outcome == GuiTreeDumpPollOutcome.GaveUp
                || outcome == GuiTreeDumpPollOutcome.TimedOut)
            {
                // ONE wire token, two causes, and the log line separates them: `gaveUp`
                // is the recorder's own frame budget (armed-no-repaint - nothing drew IMGUI
                // through a patched funnel), while a plain budget expiry means it was still
                // working when the minute ran out. A flush that could not WRITE lands in
                // the first branch with no disarm reason, and logs its own Error.
                ParsekLog.Error(Tag, $"dumpguitree not written label={label} "
                    + $"gaveUp={Bool(outcome == GuiTreeDumpPollOutcome.GaveUp)} "
                    + $"recorderReason={GuiTreeRecorder.LastDisarmReason ?? "no-dump-written"} "
                    + $"elapsed={elapsedText}s budget={budget.ToString("F0", ic)}s "
                    + $"path={path}");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    TestCommandDumpGuiTree.TimeoutReason, dequeueHead: true);
                return;
            }

            long bytes = ReadDumpBytes(path);
            int windows = GuiTreeRecorder.LastTree != null ? GuiTreeRecorder.LastTree.WindowCount : 0;
            int nodes = GuiTreeRecorder.LastTree != null ? GuiTreeRecorder.LastTree.NodeCount : 0;
            int patched = 0;
            int hits = 0;
            for (int i = 0; i < GuiTreeFunnels.Count; i++)
            {
                if (GuiTreeFunnels.PatchedAtArm[i]) patched++;
                hits += GuiTreeFunnels.Hits[i];
            }

            ParsekLog.Info(Tag, $"dumpguitree ok label={label} path="
                + TestCommandDumpGuiTree.RelativePathFor(label)
                + $" bytes={bytes.ToString(ic)} windows={Int(windows)} nodes={Int(nodes)}"
                + $" patched={TestCommandDumpGuiTree.FormatPatched(patched, GuiTreeFunnels.Count)}"
                + $" hits={Int(hits)} elapsed={elapsedText}s");
            EmitExecutedTerminal(id, seq, verb, "OK",
                TestCommandDumpGuiTree.BuildPayload(
                    label, bytes, windows, nodes, patched, GuiTreeFunnels.Count, hits),
                null, dequeueHead: true);
        }

        /// <summary>
        /// Whether the recorder's written path is the one this arm was handed. Both come
        /// from the same <c>ResolveOutputPath</c> call in the same process, so an ordinal
        /// comparison is the right one; case is ignored because the volume is Windows and
        /// a case difference here would be a false negative, not a real mismatch.
        /// </summary>
        private static bool PathMatchesArmedDump(string written, string expected)
        {
            return !string.IsNullOrEmpty(written)
                && !string.IsNullOrEmpty(expected)
                && string.Equals(written, expected, StringComparison.OrdinalIgnoreCase);
        }

        // The payload's byte count. A stat failure is NOT a terminal: the recorder has
        // already said the write returned, so a file we cannot stat is a reporting gap and
        // not a missing dump. -1 says so without pretending the file is empty.
        private static long ReadDumpBytes(string path)
        {
            try
            {
                var info = new FileInfo(path ?? string.Empty);
                return info.Exists ? info.Length : -1L;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"dumpguitree stat failed path={path}: "
                    + $"{ex.GetType().Name}");
                return -1L;
            }
        }
    }
}
