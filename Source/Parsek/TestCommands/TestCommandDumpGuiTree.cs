using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>What one poll of the armed GUI-tree capture concludes.</summary>
    internal enum GuiTreeDumpPollOutcome
    {
        /// <summary>The recorder is still armed or still holding a captured frame:
        /// poll again next frame.</summary>
        NotYet,

        /// <summary>The dump for THIS arm was assembled and written: terminal OK.</summary>
        Settled,

        /// <summary>A patch body threw during the captured frame. The recorder counted
        /// and swallowed it (an exception must never escape into OnGUI), so a dump may
        /// still exist - but it describes a frame the recorder stopped following, and a
        /// partial tree read as a census product would be read as a Parsek UI defect.
        /// Terminal ERROR.</summary>
        Faulted,

        /// <summary>The recorder went idle without writing this arm's dump: it gave itself
        /// up (<c>armed-no-repaint</c> - nothing drew IMGUI through a patched funnel
        /// within its own frame budget) or the flush could not write the file. Terminal
        /// ERROR, same token as the budget expiry: both mean "no dump".</summary>
        GaveUp,

        /// <summary>The verb's deferral budget expired with the recorder still working.
        /// Terminal ERROR.</summary>
        TimedOut,
    }

    /// <summary>
    /// Pure decision / payload half of the ADDITIVE, automation-only
    /// <c>DumpGuiTree label=&lt;name&gt;</c> seam verb. The applier
    /// (<c>ParsekTestCommandAddon.DumpGuiTree.cs</c>) owns the one call into the recorder
    /// (<c>GuiTreeRecorder.ArmForNextRepaint</c>) and the file stat; the arg parse, the
    /// poll decision and the payload shape live here so a spec's <c>expect</c> has xUnit
    /// cells behind it.
    ///
    /// <para><b>WHY IT EXISTS.</b> <c>CaptureScreenshot</c> gave a GUI review PIXELS. A
    /// screenshot says what a window looked like and nothing about what it CONTAINED: an
    /// IMGUI window has no retained widget tree, no GameObject hierarchy and no
    /// accessibility surface, so which rows a filter left visible, which button was
    /// greyed out, what nests inside what and which control published which tooltip are
    /// all unreadable from an image. <c>GuiTreeRecorder</c> records exactly that for one
    /// Repaint pass and writes it as JSON beside the screenshots
    /// (<c>docs/dev/design-gui-tree-dump.md</c>); this verb is the only way an unattended
    /// run can arm it, because the recorder's API is <c>internal</c> and no player input
    /// reaches it.</para>
    ///
    /// <para><b>WHY IT IS TWO-PHASE.</b> Arming does not capture: the recorder waits for
    /// the NEXT IMGUI Repaint pass, records it, and then assembles + serialises + writes
    /// the dump from the following <c>LateUpdate</c>. A single-phase OK would therefore
    /// claim a file that does not exist yet, and the NEXT seam step (another dump, a
    /// window close, a scene exit) would race the write - and worse, would change the very
    /// UI the pending capture is about to record. Holding the FIFO head until the recorder
    /// reports the dump written is what makes a following step ORDERED AFTER the capture.
    /// Same shape as <c>CaptureScreenshot</c>: two-phase, but riding the 60 s DEFAULT
    /// budget rather than joining <c>DEFERRED_SEAM_VERBS</c>, because the wait is a frame
    /// or two and a dump that has not landed within a minute is broken rather than
    /// slow.</para>
    ///
    /// <para><b>WHY THERE IS NO TWO-SAMPLE SIZE RULE.</b> <c>CaptureScreenshot</c> needs
    /// one because UNITY writes the PNG asynchronously into the path the poll reads. Here
    /// the writer is Parsek's own <c>GuiTreeRecorder.FlushCapture</c>, whose
    /// <c>File.WriteAllText</c> has RETURNED before <c>LastCaptureWritten</c> is set - so
    /// the flag is already the stronger statement, and polling a size for stability would
    /// only add a frame of latency to a file that cannot be partial.</para>
    ///
    /// <para><b>WHY THE ARM HAPPENS IN Update.</b> The recorder REFUSES to arm from
    /// inside an IMGUI pass (<c>GuiTreeRecorder.ClassifyArmRefusal</c>): arming installs
    /// Harmony patches on the very methods the running call stack is executing, and a
    /// half-drawn pass would give a truncated capture. The seam pump runs in
    /// <c>Update</c>, so the refusal is unreachable BY CONSTRUCTION - which is exactly
    /// why a refusal that ever does fire is an <c>ERROR</c> naming the recorder's own
    /// reason rather than a <c>REJECTED</c>: it would mean the pump moved.</para>
    /// </summary>
    internal static class TestCommandDumpGuiTree
    {
        /// <summary>No <c>label=</c> arg. REQUIRED and never defaulted, for
        /// <c>CaptureScreenshot</c>'s reason exactly: the label is the dump's filename, an
        /// invented one would collide across steps and silently overwrite an earlier dump,
        /// and a census would come back one file short with nothing saying which.</summary>
        internal const string LabelArgMissingReason =
            TestCommandCaptureScreenshot.LabelArgMissingReason;

        /// <summary>The <c>label=</c> value is not filename-safe. The SAME rule and the
        /// SAME wire token as <c>CaptureScreenshot</c>, deliberately: a census pairs one
        /// dump with one PNG under one label, so a label either verb would refuse must be
        /// refused by both, with the same message.</summary>
        internal const string LabelArgInvalidReason =
            TestCommandCaptureScreenshot.LabelArgInvalidReason;

        /// <summary>POST-CALL terminal: the recorder refused to arm. It returns null and
        /// names the reason (today the only one is <c>inside-gui-pass</c>); the reason
        /// rides on the message so the diagnosis does not need the log. ERROR rather than
        /// REJECTED because the seam pump runs in <c>Update</c> and the refusal is
        /// therefore unreachable by construction - if it fires, something about the
        /// dispatch moved.</summary>
        internal const string ArmRefusedReason = "gui-tree-arm-refused";

        /// <summary>POST-CALL terminal: no dump. Either the recorder gave itself up (its
        /// own <c>armed-no-repaint</c> frame budget expired because nothing drew IMGUI
        /// through a patched funnel) or the verb's deferral budget ran out first. ONE
        /// token for both, because the actionable fact is identical - the file a later
        /// step wanted is not there - and the log line carries which of the two it
        /// was.</summary>
        internal const string TimeoutReason = "gui-tree-timeout";

        /// <summary>POST-CALL terminal: a patch body threw during the captured frame. Kept
        /// apart from the timeout for the reason <c>SimulateStockSwitchClick</c> keeps
        /// <c>switch-threw</c> apart from <c>switch-refused-by-stock</c>: a fault inside an
        /// interception of a UnityEngine IMGUI funnel is a different investigation from
        /// "nothing drew", and it is the one outcome that can leave a dump on disk that
        /// must NOT be read as a census product.</summary>
        internal const string FaultedReason = "gui-tree-faulted";

        /// <summary>The extension every dump is written with, mirroring
        /// <c>GuiTreeRecorder.OutputSuffix</c>. Not a second definition of the same fact:
        /// the recorder OWNS the path, this constant only spells the payload's relative
        /// form, and a unit cell pins the two against each other.</summary>
        internal const string DumpSuffix = ".gui.json";

        /// <summary>
        /// True when <paramref name="label"/> is safe to use as a bare filename stem.
        /// DELEGATES to <see cref="TestCommandCaptureScreenshot.IsValidLabel"/> rather
        /// than re-stating the rule: the two verbs are used in pairs under ONE label, so a
        /// second copy of the predicate could only ever drift into a label one verb
        /// accepts and the other refuses.
        ///
        /// <para>The rule is also strictly TIGHTER than
        /// <c>GuiTreeRecorder.SanitizeLabel</c>, which replaces anything outside
        /// <c>[A-Za-z0-9._-]</c> with an underscore and trims. So a label that passes here
        /// survives sanitisation UNCHANGED, and the path this verb reports is the path the
        /// recorder writes - pinned in the mirror direction by
        /// <c>TestCommandDumpGuiTreeTests</c>.</para>
        /// </summary>
        internal static bool IsValidLabel(string label)
            => TestCommandCaptureScreenshot.IsValidLabel(label);

        /// <summary>
        /// Parses the required <c>label=</c> arg. <paramref name="rejectReason"/> carries
        /// <see cref="LabelArgMissingReason"/> for an absent arg and
        /// <see cref="LabelArgInvalidReason"/> for a present-but-unsafe one.
        /// </summary>
        internal static bool TryParseLabel(string raw, out string label, out string rejectReason)
        {
            label = null;
            if (raw == null)
            {
                rejectReason = LabelArgMissingReason;
                return false;
            }
            if (!IsValidLabel(raw))
            {
                rejectReason = LabelArgInvalidReason;
                return false;
            }
            label = raw;
            rejectReason = null;
            return true;
        }

        /// <summary>The bare filename a label is written as:
        /// <c>&lt;label&gt;.gui.json</c>.</summary>
        internal static string FileNameFor(string label)
            => (label ?? string.Empty) + DumpSuffix;

        /// <summary>
        /// The path the payload reports: <c>Screenshots/&lt;label&gt;.gui.json</c>, always
        /// with forward slashes and always RELATIVE to the KSP root.
        ///
        /// <para>The DIRECTORY is not a choice and it is not a screenshot pun: it is the
        /// one directory <c>run.py</c>'s artifact leg harvests, and
        /// <c>hlib.ARTIFACT_SHOTS_SUFFIXES</c> carries <c>.gui.json</c> precisely so a
        /// dump travels with the run's images into <c>results/&lt;runId&gt;_shots/</c>.
        /// Relative for <c>CaptureScreenshot</c>'s reason: the absolute path names the
        /// operator's provisioned instance, so a spec that pinned it would pin a
        /// machine.</para>
        /// </summary>
        internal static string RelativePathFor(string label)
            => TestCommandCaptureScreenshot.ScreenshotsDirName + "/" + FileNameFor(label);

        /// <summary>
        /// One poll of the armed capture.
        ///
        /// <para>ORDER MATTERS, twice.</para>
        ///
        /// <para>(1) FAULTED is decided FIRST, ahead of a written dump. A patch-body
        /// exception disarms the recorder mid-frame, and if the capture had already
        /// opened, the pump still flushes what it had - so a faulted arm CAN leave a file
        /// on disk. Reporting OK over it would put a partial control tree in a census
        /// folder, where the missing subtree reads as a Parsek UI defect rather than as a
        /// recorder fault. The dump is left in place (it is still evidence about the
        /// fault); it is the VERDICT that refuses to call it a product.</para>
        ///
        /// <para>(2) SETTLED is decided BEFORE the budget, the
        /// <c>CaptureScreenshot.DecidePoll</c> rule: a dump that landed on the very frame
        /// the budget expired is a success, and reporting it as a timeout would red a run
        /// over a file sitting in the artifact folder.</para>
        /// </summary>
        /// <param name="writtenForThisArm">Whether the recorder reports a dump written AND
        /// its path is the one THIS arm was given. The path half is load-bearing: a stale
        /// <c>LastCaptureWritten</c> from an earlier capture must never settle this
        /// one.</param>
        /// <param name="recorderIdle">Whether the recorder has no pending work left - not
        /// armed, no captured frame waiting to flush. With no dump written that means it
        /// gave itself up or its flush could not write the file; either way waiting
        /// longer cannot change the answer.</param>
        /// <param name="faultsSinceArm">Patch-body exceptions counted since this arm
        /// (the recorder clears the counter on every arm).</param>
        /// <param name="budgetExpired">Whether the verb's deferral budget has run out.</param>
        internal static GuiTreeDumpPollOutcome DecidePoll(
            bool writtenForThisArm, bool recorderIdle, int faultsSinceArm, bool budgetExpired)
        {
            if (faultsSinceArm > 0)
                return GuiTreeDumpPollOutcome.Faulted;
            if (writtenForThisArm)
                return GuiTreeDumpPollOutcome.Settled;
            if (recorderIdle)
                return GuiTreeDumpPollOutcome.GaveUp;
            if (budgetExpired)
                return GuiTreeDumpPollOutcome.TimedOut;
            return GuiTreeDumpPollOutcome.NotYet;
        }

        /// <summary>The <c>patched=</c> token: <c>&lt;patched&gt;/&lt;funnels&gt;</c>.
        ///
        /// <para>Its own field rather than two, because the reading only means anything as
        /// a PAIR: <c>17</c> alone says nothing without the denominator the build shipped,
        /// and a spec pins the whole token (<c>patched=17/17</c>) as the one statement
        /// that every one of the interceptions the dump's structure depends on was
        /// actually installed for this arm. <c>patched</c> is the recorder's ARM-TIME
        /// <c>Harmony.GetPatchInfo</c> reading, owner-scoped to the recorder's own Harmony
        /// id - a flush-time reading would report false for everything, because the
        /// capture removes its own patches.</para></summary>
        internal static string FormatPatched(int patched, int funnels)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return patched.ToString(ic) + "/" + funnels.ToString(ic);
        }

        /// <summary>
        /// OK payload: <c>label=&lt;label&gt; path=Screenshots/&lt;label&gt;.gui.json
        /// bytes=&lt;n&gt; windows=&lt;n&gt; nodes=&lt;n&gt; patched=&lt;ok&gt;/&lt;of&gt;
        /// hits=&lt;sum&gt;</c>.
        ///
        /// <para>The last four are THE CAPTURE'S OWN READING, and they are on the wire
        /// rather than only in the file because a spec can only regex what reaches the
        /// response line and the log. <c>windows</c> / <c>nodes</c> say the capture has
        /// content (a dump of an empty frame is a valid file and a useless product);
        /// <c>patched</c> says the interceptions were installed; <c>hits</c> is the sum of
        /// the per-funnel body-run counters and says they FIRED. The three answer three
        /// different failures - nothing drew, nothing was patched, everything was inlined -
        /// which is why they are three tokens and not one.</para>
        ///
        /// <para><c>hits</c> is deliberately left for a reader rather than pinned by the
        /// census lanes: it is a property of whatever the frame happened to contain, so a
        /// literal would pin a window's row count.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildPayload(
            string label, long bytes, int windows, int nodes, int patched, int funnels, int hits)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("label", label ?? string.Empty),
                new KeyValuePair<string, string>("path", RelativePathFor(label)),
                new KeyValuePair<string, string>("bytes", bytes.ToString(ic)),
                new KeyValuePair<string, string>("windows", windows.ToString(ic)),
                new KeyValuePair<string, string>("nodes", nodes.ToString(ic)),
                new KeyValuePair<string, string>("patched", FormatPatched(patched, funnels)),
                new KeyValuePair<string, string>("hits", hits.ToString(ic)),
            };
        }
    }
}
