using System;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace Parsek.TestCommands
{
    /// <summary>
    /// GUI-census partial: the thin Unity applier for the TWO-PHASE
    /// <c>CaptureScreenshot</c> verb. Every decision is delegated to the pure sibling
    /// <see cref="TestCommandCaptureScreenshot"/>; this file resolves the target path,
    /// makes the one <c>ScreenCapture.CaptureScreenshot</c> call, and polls the file until
    /// its size settles.
    ///
    /// <para>
    /// WHY THE TARGET DIRECTORY IS NOT A CHOICE. <c>run.py</c>'s always-collect leg copies
    /// run-window files out of <c>&lt;instance&gt;/Screenshots/</c> and nowhere else
    /// (<c>hlib.select_run_screenshots</c>, 64-file / 256 MB caps), and the V3 contact sheet
    /// renders whatever landed there. A capture written anywhere else would exist only on
    /// the operator's disk and never reach a result folder - so the path is derived from
    /// <c>KSPUtil.ApplicationRootPath</c> + <c>Screenshots</c>, and the directory is created
    /// when absent (a fresh provisioned instance may not have one until the first F1).
    /// </para>
    ///
    /// <para>
    /// WHY THE PRE-DELETE. A re-run of the same census against the same instance re-uses
    /// the labels, so the target may already exist from an earlier run. Polling would then
    /// see a settled file IMMEDIATELY and report OK for the OLD image. Deleting first makes
    /// the poll's "exists and stable" a statement about THIS capture. The collision is
    /// reported (<c>overwrote=true</c>) rather than refused: re-running a census is normal.
    /// </para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // Two-phase state for the in-flight capture. Only ever read by
        // TryCompleteCaptureScreenshot while completionVerb == "CaptureScreenshot", so it
        // needs no reset beyond what ClearTwoPhase already does for the shared fields.
        private string captureLabel;
        private string captureAbsolutePath;
        private int captureSuperSize;
        private bool captureOverwrote;

        // The size the PREVIOUS poll observed, or -1 before the first poll. The
        // two-agreeing-samples rule lives on this field: a first sighting can be a
        // partially written PNG (see the pure half's header).
        private long capturePreviousBytes;

        // Cached `UnityEngine.ScreenCapture.CaptureScreenshot(string, int)`. Resolved once
        // and then reused; `resolvedOnce` distinguishes "not looked up yet" from "looked up
        // and absent", so a missing API costs one reflection pass rather than one per call.
        private static MethodInfo cachedCaptureMethod;
        private static bool captureMethodResolved;

        /// <summary>
        /// Resolves the screenshot API REFLECTIVELY rather than through a compile-time
        /// reference, and the reason is a build constraint rather than a style preference.
        ///
        /// <para><c>UnityEngine.ScreenCapture</c> lives in
        /// <c>UnityEngine.ScreenCaptureModule.dll</c>, which <c>Parsek.csproj</c> does not
        /// reference - it names only the ten Unity modules the mod already needed. Adding an
        /// eleventh would be fine on a Windows dev box, but the CLOUD / CI build resolves its
        /// reference DLLs from the PRIVATE <c>vl3c/ksp-refs</c> repo rather than from a KSP
        /// install, and a reference that repo does not carry is a hard compile error on the
        /// required <c>tests</c> check - which nothing in this worktree can fix. Reflection
        /// keeps the build's dependency surface exactly as it was.</para>
        ///
        /// <para>Returns null when the type or the overload cannot be found, which the caller
        /// answers as <see cref="TestCommandCaptureScreenshot.ApiUnavailableReason"/>. It is
        /// a REJECTED and never a defer: a missing engine API cannot appear by waiting.</para>
        /// </summary>
        private static MethodInfo ResolveCaptureMethod()
        {
            if (captureMethodResolved) return cachedCaptureMethod;
            captureMethodResolved = true;
            try
            {
                // Assembly-qualified first (the Unity 2019 layout KSP 1.12.5 ships), then the
                // bare name so a differently-split runtime still resolves.
                Type type = Type.GetType(
                        "UnityEngine.ScreenCapture, UnityEngine.ScreenCaptureModule")
                    ?? Type.GetType("UnityEngine.ScreenCapture, UnityEngine")
                    ?? Type.GetType("UnityEngine.ScreenCapture");
                if (type != null)
                {
                    cachedCaptureMethod = type.GetMethod(
                        "CaptureScreenshot",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(string), typeof(int) },
                        null);
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "capturescreenshot api resolve failed: "
                    + $"{ex.GetType().Name}: {ex.Message}");
                cachedCaptureMethod = null;
            }
            if (cachedCaptureMethod == null)
                ParsekLog.Warn(Tag, "capturescreenshot api unavailable: "
                    + "UnityEngine.ScreenCapture.CaptureScreenshot(string,int) not found");
            return cachedCaptureMethod;
        }

        private void CaptureScreenshotImpl(ParsedCommand cmd)
        {
            // (1) Args. Both parses are pure and both rejects are terminal-with-no-side-
            // effect, so nothing has been written when either fires.
            if (!TestCommandCaptureScreenshot.TryParseLabel(
                    ArgOrNull(cmd, "label"), out string label, out string labelReject))
            {
                ParsekLog.Warn(Tag, $"capturescreenshot rejected reason={labelReject} "
                    + $"label={ArgOrNull(cmd, "label") ?? string.Empty}");
                SetExecResult("REJECTED", null,
                    $"{labelReject} label={ArgOrNull(cmd, "label") ?? string.Empty}");
                return;
            }
            if (!TestCommandCaptureScreenshot.TryParseSuperSize(
                    ArgOrNull(cmd, "superSize"), out int superSize, out string sizeReject))
            {
                ParsekLog.Warn(Tag, $"capturescreenshot rejected reason={sizeReject} "
                    + $"superSize={ArgOrNull(cmd, "superSize") ?? string.Empty}");
                SetExecResult("REJECTED", null,
                    $"{sizeReject} superSize={ArgOrNull(cmd, "superSize") ?? string.Empty}");
                return;
            }

            // (2) Resolve + create the harvested directory. A PRE-CALL gate, so REJECTED
            // (the map-view-unavailable class): nothing was written and waiting could not
            // fix an unresolvable root path.
            string root = KSPUtil.ApplicationRootPath ?? string.Empty;
            if (string.IsNullOrEmpty(root))
            {
                ParsekLog.Warn(Tag, "capturescreenshot rejected reason="
                    + TestCommandCaptureScreenshot.DirUnavailableReason + " root=empty");
                SetExecResult("REJECTED", null,
                    TestCommandCaptureScreenshot.DirUnavailableReason);
                return;
            }
            string dir = Path.Combine(root, TestCommandCaptureScreenshot.ScreenshotsDirName);
            try
            {
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag, $"capturescreenshot dir create failed dir={dir}: "
                    + $"{ex.GetType().Name}: {ex.Message}");
                SetExecResult("REJECTED", null,
                    TestCommandCaptureScreenshot.DirUnavailableReason);
                return;
            }

            string path = Path.Combine(
                dir, TestCommandCaptureScreenshot.FileNameFor(label));

            // (3) Pre-delete a colliding file so the poll below describes THIS capture.
            bool overwrote = false;
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    overwrote = true;
                }
            }
            catch (Exception ex)
            {
                // A locked target is the one thing the poll cannot describe: Unity's write
                // would fail and the stale file would then read as a settled success.
                ParsekLog.Error(Tag, $"capturescreenshot pre-delete threw path={path}: "
                    + $"{ex.GetType().Name}: {ex.Message}");
                SetExecResult("ERROR", null, TestCommandCaptureScreenshot.ThrewReason);
                return;
            }

            // (4) The one engine call, through reflection (see ResolveCaptureMethod for
            // why). It returns immediately; Unity encodes the PNG at the end of a later
            // frame, which is why this verb is two-phase.
            MethodInfo capture = ResolveCaptureMethod();
            if (capture == null)
            {
                ParsekLog.Warn(Tag, "capturescreenshot rejected reason="
                    + TestCommandCaptureScreenshot.ApiUnavailableReason);
                SetExecResult("REJECTED", null,
                    TestCommandCaptureScreenshot.ApiUnavailableReason);
                return;
            }
            try
            {
                capture.Invoke(null, new object[] { path, superSize });
            }
            catch (Exception ex)
            {
                // A reflected invoke wraps the real fault, so unwrap it for the log - the
                // token stays the same either way.
                Exception inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                ParsekLog.Error(Tag, $"capturescreenshot threw path={path}: "
                    + $"{inner.GetType().Name}: {inner.Message}");
                SetExecResult("ERROR", null, TestCommandCaptureScreenshot.ThrewReason);
                return;
            }

            captureLabel = label;
            captureAbsolutePath = path;
            captureSuperSize = superSize;
            captureOverwrote = overwrote;
            capturePreviousBytes = -1L;

            ParsekLog.Info(Tag, $"capturescreenshot initiated label={label} "
                + $"superSize={Int(superSize)} overwrote={Bool(overwrote)} path="
                + TestCommandCaptureScreenshot.RelativePathFor(label));
            SetExecResult(PendingVerdict, null, null);
        }

        // Bounded, observable completion (the LoadGame contract): poll the target until it
        // exists with a stable non-zero size, then OK; convert to ERROR once the verb's
        // budget expires so a capture the engine never wrote cannot wedge the run.
        private void TryCompleteCaptureScreenshot(double now)
        {
            long bytes = -1L;
            bool exists = false;
            try
            {
                var info = new FileInfo(captureAbsolutePath ?? string.Empty);
                exists = info.Exists;
                if (exists) bytes = info.Length;
            }
            catch (Exception ex)
            {
                // A transient stat failure while Unity holds the file for writing is a
                // NOT-YET, not a terminal: the next poll re-reads it. Logged rate-limited
                // because a locked file would otherwise print once per frame.
                ParsekLog.VerboseRateLimited(Tag, "capturescreenshot-stat",
                    $"capturescreenshot stat failed path={captureAbsolutePath}: "
                    + $"{ex.GetType().Name}");
                exists = false;
                bytes = -1L;
            }

            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds("CaptureScreenshot");
            bool expired = DeferralBudget.ShouldTimeout(completionStartedAt, now, budget);

            ScreenshotPollOutcome outcome = TestCommandCaptureScreenshot.DecidePoll(
                exists, bytes, capturePreviousBytes, expired);
            capturePreviousBytes = exists ? bytes : -1L;

            if (outcome == ScreenshotPollOutcome.NotYet)
                return;

            string id = completionId;
            long seq = completionSeq;
            string verb = completionVerb;
            string label = captureLabel;
            int superSize = captureSuperSize;
            bool overwrote = captureOverwrote;
            ClearTwoPhase();

            if (outcome == ScreenshotPollOutcome.TimedOut)
            {
                ParsekLog.Error(Tag, $"capturescreenshot not written label={label} "
                    + $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s "
                    + $"budget={budget.ToString("F0", CultureInfo.InvariantCulture)}s "
                    + $"exists={Bool(exists)} bytes={bytes.ToString(CultureInfo.InvariantCulture)}");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    TestCommandCaptureScreenshot.NotWrittenReason, dequeueHead: true);
                return;
            }

            ParsekLog.Info(Tag, $"capturescreenshot ok label={label} "
                + $"bytes={bytes.ToString(CultureInfo.InvariantCulture)} "
                + $"superSize={Int(superSize)} overwrote={Bool(overwrote)} "
                + $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
            EmitExecutedTerminal(id, seq, verb, "OK",
                TestCommandCaptureScreenshot.BuildPayload(label, bytes, superSize, overwrote),
                null, dequeueHead: true);
        }
    }
}
