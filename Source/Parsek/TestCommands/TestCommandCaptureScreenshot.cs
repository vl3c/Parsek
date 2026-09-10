using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>What one poll of the capture's target file concludes.</summary>
    internal enum ScreenshotPollOutcome
    {
        /// <summary>The file is absent, empty, or still growing: poll again next frame.</summary>
        NotYet,

        /// <summary>The file exists, is non-empty, and reported the SAME size on two
        /// consecutive polls: the capture is complete and the terminal may be emitted.</summary>
        Settled,

        /// <summary>The budget ran out before the file settled: terminal ERROR.</summary>
        TimedOut,
    }

    /// <summary>
    /// Pure decision / payload half of the ADDITIVE, automation-only
    /// <c>CaptureScreenshot label=&lt;name&gt; [superSize=&lt;1-4&gt;]</c> seam verb. The applier
    /// (<c>ParsekTestCommandAddon.CaptureScreenshot.cs</c>) owns the one Unity call
    /// (<c>UnityEngine.ScreenCapture.CaptureScreenshot</c>) and the file stats; the arg parse, the poll
    /// decision and the payload shape live here so a spec's <c>expect</c> has xUnit cells
    /// behind it.
    ///
    /// <para><b>WHY IT EXISTS.</b> Every harness run already harvests the instance's
    /// <c>Screenshots/</c> directory into <c>results/&lt;runId&gt;_shots/</c>
    /// (<c>hlib.select_run_screenshots</c>, cap 64 files) and the V3 contact sheet already
    /// renders whatever it finds there - its empty-state text literally reads "no
    /// screenshots captured (the V4 capture verbs feed this)". Nothing ever fed it: the only
    /// screenshot path in the game is the player's F1 key, which no unattended run presses.
    /// So the whole picture half of "numbers next to pictures" has been dead since it
    /// shipped, and a GUI review has had no evidence surface at all.</para>
    ///
    /// <para><b>WHY IT IS TWO-PHASE.</b> <c>ScreenCapture.CaptureScreenshot</c> returns
    /// immediately and Unity writes the PNG asynchronously at the end of a later frame, so
    /// a single-phase OK would claim a file that may not exist yet - and the NEXT seam step
    /// (another capture, a window close, a scene exit) would then race the write. Holding
    /// the FIFO head until the file exists and its size stops changing is what makes a
    /// following step ORDERED AFTER the capture. Same shape as <c>EnterWatchMode</c>: it is
    /// two-phase but rides the 60 s default budget rather than joining
    /// <c>DEFERRED_SEAM_VERBS</c>, because a capture that has not landed within a minute is
    /// broken rather than slow.</para>
    ///
    /// <para><b>WHY TWO AGREEING SIZE SAMPLES.</b> A first sighting can be a partially
    /// written file (Unity encodes the PNG into the same path it is polled from). "Exists and
    /// non-empty" is therefore NOT proof of a complete image, and a truncated PNG in a
    /// contact sheet reads as a Parsek render defect rather than as a harvest artifact. The
    /// second, equal sample is the cheapest available proof that the writer is done, and it
    /// costs one extra frame.</para>
    ///
    /// <para><b>WHY NO SCENE PRECONDITION.</b> <c>AnyScene</c>, the
    /// <c>ExportRenderManifest</c> row: a screenshot is meaningful in every settled scene
    /// (that is the point of a census that walks KSC and FLIGHT), and the dispatcher's
    /// safe-point gate already refuses to run during LOADING, a scene transition or the
    /// settle window - which is exactly when a capture would photograph a black frame.</para>
    /// </summary>
    internal static class TestCommandCaptureScreenshot
    {
        /// <summary>No <c>label=</c> arg. REQUIRED and never defaulted: an invented name
        /// would collide across steps and silently overwrite an earlier capture, so a
        /// census would come back one image short with nothing saying which.</summary>
        internal const string LabelArgMissingReason = "label-arg-missing";

        /// <summary>The <c>label=</c> value is not filename-safe. Fail-closed and
        /// CASE-SENSITIVE, the <c>LoadGame scene=</c> rule - the label becomes a filename in
        /// the harvested artifact directory, so a traversal or a shell-hostile character
        /// must never reach the filesystem.</summary>
        internal const string LabelArgInvalidReason = "label-arg-invalid";

        /// <summary>The <c>superSize=</c> value did not parse as an integer in
        /// <c>[1, MaxSuperSize]</c>. REJECTED rather than clamped: a typo that silently
        /// captured at 1x would put an unreadable image in a review folder and read as a
        /// resolution problem.</summary>
        internal const string SuperSizeArgInvalidReason = "supersize-arg-invalid";

        /// <summary>PRE-CALL gate: the target directory could not be resolved or created
        /// (an empty <c>KSPUtil.ApplicationRootPath</c>, or an I/O failure creating
        /// <c>Screenshots/</c>). REJECTED, the <c>map-view-unavailable</c> class - stock was
        /// never called and nothing was written.</summary>
        internal const string DirUnavailableReason = "screenshot-dir-unavailable";

        // There is deliberately NO `screenshot-api-unavailable` reason. The applier calls
        // `UnityEngine.ScreenCapture.CaptureScreenshot` directly against a compile-time
        // reference (`UnityEngine.ScreenCaptureModule` in Parsek.csproj), so a missing API
        // is a BUILD failure and can never be a run-time state - which is strictly better
        // than the reflective form this replaced, where the same fault could only surface
        // as a typed REJECTED after a whole KSP boot.

        /// <summary>POST-CALL terminal: the budget expired with no settled file. ERROR, the
        /// <c>map-not-entered</c> line - we acted and the engine did not produce the
        /// artifact.</summary>
        internal const string NotWrittenReason = "screenshot-not-written";

        /// <summary>POST-CALL terminal for the one thing a poll cannot describe: the Unity
        /// call or the pre-delete THREW. Kept as its own token for the reason
        /// <c>SimulateStockSwitchClick</c> keeps <c>switch-threw</c> apart from
        /// <c>switch-refused-by-stock</c>: a throw is a different investigation.</summary>
        internal const string ThrewReason = "screenshot-threw";

        /// <summary>The directory (relative to the KSP root) every capture is written into.
        /// It is NOT a choice: <c>run.py</c>'s artifact leg harvests
        /// <c>&lt;instance&gt;/Screenshots/</c> and nothing else, so a capture written
        /// anywhere else would exist on the operator's disk and never reach a result
        /// folder.</summary>
        internal const string ScreenshotsDirName = "Screenshots";

        /// <summary>The only extension written. PNG because it is lossless (a JPEG-blurred
        /// IMGUI label is unreadable at 1280x720) and because it is in
        /// <c>hlib.ARTIFACT_SCREENSHOT_EXTENSIONS</c>, so the harvest picks it up.</summary>
        internal const string ScreenshotExtension = ".png";

        /// <summary>Upper bound on <c>superSize=</c>. 4 is Unity's practical ceiling for a
        /// framebuffer multiple on a 1280x720 window (5120x2880) and well past anything a
        /// review needs.</summary>
        internal const int MaxSuperSize = 4;

        /// <summary>Default <c>superSize</c>. ONE, deliberately: Unity's supersize path
        /// re-renders through the cameras at the multiplied resolution, and screen-space
        /// IMGUI is not guaranteed to survive that - which would silently drop the very
        /// windows a GUI census exists to photograph. The arg is offered for a scene shot
        /// that wants the detail; the census itself stays at 1 until a run proves
        /// otherwise.</summary>
        internal const int DefaultSuperSize = 1;

        /// <summary>Longest accepted label. Bounded because the label is a filename on a
        /// Windows volume whose full path already carries the results-dir prefix.</summary>
        internal const int MaxLabelLength = 96;

        /// <summary>
        /// True when <paramref name="label"/> is safe to use as a bare filename stem.
        ///
        /// <para>Deliberately the SAME rule as the harness's own filename-safe id
        /// (<c>hlib._ID_RE</c>, <c>^[A-Za-z0-9][A-Za-z0-9._-]*$</c>): must start
        /// alphanumeric, then alphanumerics / dot / dash / underscore only. That excludes
        /// every path separator, every drive-letter colon, <c>..</c>, whitespace (which the
        /// wire codec would percent-encode and a shell would then re-split), and every
        /// non-ASCII byte. A label the harness would reject as an id must not become a file
        /// the harness then harvests.</para>
        /// </summary>
        internal static bool IsValidLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return false;
            if (label.Length > MaxLabelLength) return false;
            char first = label[0];
            bool firstOk = (first >= 'a' && first <= 'z')
                || (first >= 'A' && first <= 'Z')
                || (first >= '0' && first <= '9');
            if (!firstOk) return false;
            for (int i = 1; i < label.Length; i++)
            {
                char c = label[i];
                bool ok = (c >= 'a' && c <= 'z')
                    || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9')
                    || c == '.' || c == '-' || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>
        /// Parses the required <c>label=</c> arg. <paramref name="rejectReason"/> carries
        /// <see cref="LabelArgMissingReason"/> for an absent arg and
        /// <see cref="LabelArgInvalidReason"/> for a present-but-unsafe one; the two are
        /// distinct so a spec author can tell a forgotten arg from a bad one.
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

        /// <summary>
        /// Parses the OPTIONAL <c>superSize=</c> arg. An absent arg yields
        /// <see cref="DefaultSuperSize"/> and succeeds; anything present that is not an
        /// integer in <c>[1, MaxSuperSize]</c> is a REJECTED.
        /// </summary>
        internal static bool TryParseSuperSize(string raw, out int superSize, out string rejectReason)
        {
            superSize = DefaultSuperSize;
            rejectReason = null;
            if (raw == null)
                return true;
            int parsed;
            // NumberStyles.None, not Integer: Integer allows leading / trailing
            // whitespace and a sign, so " 2" would parse. Fail-closed, and the wire codec
            // would percent-encode a space anyway - a value that needed decoding to look
            // like a number is a spec fault, not a value.
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)
                || parsed < 1 || parsed > MaxSuperSize)
            {
                rejectReason = SuperSizeArgInvalidReason;
                return false;
            }
            superSize = parsed;
            return true;
        }

        /// <summary>The bare filename a label is written as: <c>&lt;label&gt;.png</c>.</summary>
        internal static string FileNameFor(string label)
            => (label ?? string.Empty) + ScreenshotExtension;

        /// <summary>
        /// The path the payload reports: <c>Screenshots/&lt;label&gt;.png</c>, always with
        /// forward slashes and always RELATIVE to the KSP root.
        ///
        /// <para>Relative on purpose. The absolute path names the operator's provisioned
        /// instance directory, which differs between the dev install and the automation one
        /// and between machines - so a spec that ever pinned it would pin a machine. The
        /// relative form is the same string on every host AND is exactly where the harvest
        /// looks.</para>
        /// </summary>
        internal static string RelativePathFor(string label)
            => ScreenshotsDirName + "/" + FileNameFor(label);

        /// <summary>
        /// One poll of the target file.
        ///
        /// <para>ORDER MATTERS: settled is decided BEFORE the budget. A capture that landed
        /// on the very frame the budget expired is a success, and reporting it as
        /// <see cref="NotWrittenReason"/> would red a run over a file that is sitting in the
        /// artifact folder.</para>
        /// </summary>
        /// <param name="exists">Whether the target file exists right now.</param>
        /// <param name="bytes">Its size, or a negative value when it does not exist.</param>
        /// <param name="previousBytes">The size the PREVIOUS poll saw, or a negative
        /// sentinel on the first poll. A first poll can therefore never settle, which is
        /// the two-agreeing-samples rule.</param>
        /// <param name="budgetExpired">Whether the verb's deferral budget has run out.</param>
        internal static ScreenshotPollOutcome DecidePoll(
            bool exists, long bytes, long previousBytes, bool budgetExpired)
        {
            if (exists && bytes > 0 && bytes == previousBytes)
                return ScreenshotPollOutcome.Settled;
            if (budgetExpired)
                return ScreenshotPollOutcome.TimedOut;
            return ScreenshotPollOutcome.NotYet;
        }

        /// <summary>
        /// OK payload: <c>label=&lt;label&gt; path=Screenshots/&lt;label&gt;.png
        /// bytes=&lt;n&gt; superSize=&lt;n&gt; overwrote=&lt;bool&gt;</c>.
        ///
        /// <para><c>overwrote</c> is ALWAYS present (both values), the
        /// <c>EnterMapView alreadyOpen</c> rule: a census reading its own results needs to
        /// know that an earlier image with that label was replaced, and an absent key would
        /// make "no collision" indistinguishable from an older seam build. A collision is
        /// deliberately NOT a refusal - re-running a census over a hand-staged instance is
        /// normal, and a REJECTED there would make the second run useless.</para>
        ///
        /// <para><c>bytes</c> is the SETTLED size, so a reader can tell a real capture from
        /// a 0-byte stub without opening the folder.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildPayload(
            string label, long bytes, int superSize, bool overwrote)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("label", label ?? string.Empty),
                new KeyValuePair<string, string>("path", RelativePathFor(label)),
                new KeyValuePair<string, string>("bytes", bytes.ToString(ic)),
                new KeyValuePair<string, string>("superSize", superSize.ToString(ic)),
                new KeyValuePair<string, string>("overwrote", overwrote ? "true" : "false"),
            };
        }
    }
}
