using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>Where a <c>LaunchSiteProbe</c> presses Launch.</summary>
    internal enum LaunchSiteProbePath
    {
        None = 0,
        /// <summary>The VAB / SPH Launch button (<c>EditorLogic.launchVessel</c>): stock's
        /// pre-flight checks, whose <c>LaunchSiteClear</c> test raises the
        /// <c>TestWarning</c> dialog.</summary>
        Editor,
        /// <summary>The Space Center launch-site picker (<c>LaunchSiteFacility</c> ->
        /// <c>VesselSpawnDialog</c> Launch -> <c>LaunchSiteFacility.shipSelected</c>), whose
        /// own <c>LaunchSiteWarning</c> dialog offers Recover-and-launch.</summary>
        SpaceCenter,
    }

    /// <summary>Which dialog button a <c>LaunchSiteProbe</c> presses once it has read it.</summary>
    internal enum LaunchSiteProbePress
    {
        /// <summary>The dialog's abort / cancel button: nothing is recovered.</summary>
        Abort = 0,
        /// <summary>The proceed (Recover and launch) button when the dialog has one, through
        /// its own callback and whether it is greyed or not; the abort button otherwise.</summary>
        Proceed,
    }

    /// <summary>One parsed <c>LaunchSiteProbe</c> call.</summary>
    internal struct LaunchSiteProbeRequest
    {
        internal LaunchSiteProbePath Path;
        /// <summary>The Space Center facility whose picker opens (SpaceCenter only).</summary>
        internal EditorRouteFacility Facility;
        /// <summary>The craft the picker launches (SpaceCenter only).</summary>
        internal string Craft;
        internal LaunchSiteProbePress Press;
    }

    /// <summary>What one look while the probe waits for the launch-site dialog concludes.</summary>
    internal enum LaunchSiteProbeAwaitOutcome
    {
        NotYet,
        /// <summary>The launch-site dialog is up: read it and press.</summary>
        Dialog,
        /// <summary>Another pre-flight dialog came up instead (a different stock test failed).</summary>
        OtherDialog,
        /// <summary>The scene left before any dialog: the launch went ahead.</summary>
        Launched,
        TimedOut,
    }

    /// <summary>What one settle poll after the press concludes.</summary>
    internal enum LaunchSiteProbeSettleOutcome
    {
        NotYet,
        /// <summary>The dialog closed and the scene stayed for the settle floor.</summary>
        Stayed,
        /// <summary>The scene left: the press launched.</summary>
        Launched,
        TimedOut,
    }

    /// <summary>The roles of a launch-site dialog's buttons, by index (-1 = absent).</summary>
    internal struct LaunchSiteProbeButtons
    {
        internal bool ShapeOk;
        internal int AbortIndex;
        internal int ProceedIndex;
    }

    /// <summary>
    /// Pure half of the automation-only <c>LaunchSiteProbe</c> seam verb: it presses Launch on
    /// an OBSTRUCTED launch site through stock's own buttons, reads the launch-site-clear
    /// dialog stock raises, presses one of its buttons and reports what happened. It exists for
    /// lane LS-1 (todo GC9-LAUNCH-SITE-CLEAR-RECOVERS-CLAIMED-VESSEL): stock recovers every
    /// vessel at the site from that dialog's proceed button, and Parsek must not let it
    /// recover a vessel a committed future flight holds. <c>LaunchFromEditor</c> refuses an
    /// obstructed site (<c>launchfromeditor-site-obstructed</c>) because its proceed branch
    /// recovers; this verb is the one that walks into the dialog on purpose.
    ///
    /// <para><b>Stock ground truth (decompiled KSP 1.12.5).</b> Editor: <c>launchVessel</c>
    /// builds a <c>PreFlightCheck</c> whose last test is <c>LaunchSiteClear</c>; a failed test
    /// spawns <c>MultiOptionDialog("TestWarning", GetWarningDescription(), GetWarningTitle(),
    /// ...)</c> with a proceed button (<c>GetProceedOption()</c>, Recover and launch) and an
    /// abort button (<c>GetAbortOption()</c>), or ONLY the abort button when
    /// <c>GetProceedOption()</c> is null. Space Center: <c>LaunchSiteFacility.shipSelected</c>
    /// runs the same test and on a failure spawns <c>"LaunchSiteWarning"</c> with three buttons
    /// in a fixed order: go to the obstructing vessel (<c>ResumeFlightOnSite</c>), the proceed
    /// button (<c>ClearSite</c> then <c>launchChecks</c>), Cancel.</para>
    ///
    /// <para><b>Grammar.</b> In the editor: <c>LaunchSiteProbe [answer=abort|proceed]</c> on the
    /// craft already loaded, at the editor's selected site. At the Space Center:
    /// <c>LaunchSiteProbe facility=VAB|SPH craft=&lt;name&gt; [answer=abort|proceed]</c>: opens
    /// the facility's picker through the building's own entry, selects the craft row, presses
    /// the picker's own Launch button. <c>answer=proceed</c> presses the proceed button through
    /// its own callback even when it is greyed (a player's click cannot), so the click-blocks
    /// behind a greyed button are what the press meets; with no proceed button it presses
    /// abort. A clear site is refused before Launch is pressed, so the verb never launches
    /// a craft onto a clear site.</para>
    ///
    /// <para><b>Verdict.</b> OK once a press was made and the result settled, carrying the
    /// FACTS (buttons, interactable states, what was pressed, whether the scene left, the
    /// obstruction count before and after); the lane judges them in its log contract. ERROR
    /// only when the probe could not read the dialog it came for. REJECTED for the gates
    /// before anything was pressed. The vocabularies are mirrored by
    /// <c>hlib.LAUNCHSITEPROBE_*</c> (<c>LaunchSiteProbeSourceSyncTests</c>).</para>
    /// </summary>
    internal static class TestCommandLaunchSiteProbe
    {
        internal const string Verb = "LaunchSiteProbe";

        internal const string FacilityArg = "facility";
        internal const string CraftArg = "craft";
        internal const string AnswerArg = "answer";

        /// <summary>Press tokens, in <see cref="LaunchSiteProbePress"/> order.</summary>
        internal static readonly string[] AnswerTokens = { "abort", "proceed" };

        /// <summary>Stock's <c>PreFlightCheck</c> warning dialog name (every failed test).</summary>
        internal const string EditorDialogName = "TestWarning";

        /// <summary>Stock's Space Center launch-site warning dialog name.</summary>
        internal const string SpaceCenterDialogName = "LaunchSiteWarning";

        /// <summary>The control lock <c>EditorLogic.launchVessel</c> takes around the
        /// pre-flight checks; stock's abort removes it a frame later.</summary>
        internal const string EditorLaunchSequenceLockId = "EditorLogic_launchSequence";

        /// <summary>The control lock the Space Center picker and its dialog hold
        /// (<c>LaunchSiteFacility</c>'s KSC_ALL lock); stock's Cancel removes it.</summary>
        internal const string FacilityLockId = "launchSiteFacility";

        /// <summary>Seconds an <c>EDITOR_LAUNCH</c> lock is waited out before the press
        /// (stock holds it for short stretches; <c>LaunchFromEditor</c>'s window).</summary>
        internal const double LaunchLockWaitSeconds = 10.0;

        /// <summary>Seconds the dialog is waited for after Launch was pressed. Both dialogs are
        /// spawned inside the press's own call stack, so a miss is not a slow frame.</summary>
        internal const double DialogWaitSeconds = 10.0;

        /// <summary>Seconds the picker is given to list the requested craft (its list is built
        /// by a coroutine over the frames after it opens).</summary>
        internal const double CraftListWaitSeconds = 10.0;

        /// <summary>Frames a selected craft row is given before Launch is pressed (the row's
        /// selection fills the crew manifest the launch reads).</summary>
        internal const int SelectSettleFrames = 5;

        /// <summary>Frames AND seconds the scene must stay after the press before the probe
        /// reads the result: a press that launches leaves the scene within a few frames
        /// (stock's <c>goForLaunch</c> loads FLIGHT one frame after the proceed).</summary>
        internal const int MinSettleFrames = 30;
        internal const double MinSettleSeconds = 1.0;

        /// <summary>Character cap on the logged dialog title / description.</summary>
        internal const int MaxLoggedTextLength = 1200;

        // ---- REJECTED: the gates before anything was pressed ----
        internal const string AnswerArgInvalidReason = "launchsiteprobe-answer-arg-invalid";
        internal const string FacilityArgInvalidReason = "launchsiteprobe-facility-arg-invalid";
        internal const string CraftArgInvalidReason = "launchsiteprobe-craft-arg-invalid";
        internal const string WrongSceneReason = "launchsiteprobe-wrong-scene";
        internal const string ArgNotForSceneReason = "launchsiteprobe-arg-not-for-scene";
        internal const string CraftArgMissingReason = "launchsiteprobe-craft-arg-missing";
        internal const string NoShipReason = "launchsiteprobe-no-ship";
        internal const string LaunchLockedReason = "launchsiteprobe-launch-locked";
        internal const string SiteClearReason = "launchsiteprobe-site-clear";
        internal const string FacilityNotFoundReason = "launchsiteprobe-facility-not-found";
        internal const string PickerOpenReason = "launchsiteprobe-picker-open";
        internal const string CraftNotFoundReason = "launchsiteprobe-craft-not-found";

        // ---- ERROR: Launch was pressed and the dialog could not be read ----
        internal const string NoDialogReason = "launchsiteprobe-no-dialog";
        internal const string OtherDialogReason = "launchsiteprobe-other-dialog";
        internal const string DialogShapeReason = "launchsiteprobe-dialog-shape";
        internal const string NotSettledReason = "launchsiteprobe-not-settled";
        internal const string ThrewReason = "launchsiteprobe-threw";

        /// <summary>The REJECTED vocabulary, in gate order. hlib.LAUNCHSITEPROBE_REASONS mirrors it.</summary>
        internal static readonly string[] Reasons =
        {
            "launchsiteprobe-answer-arg-invalid",
            "launchsiteprobe-facility-arg-invalid",
            "launchsiteprobe-craft-arg-invalid",
            "launchsiteprobe-wrong-scene",
            "launchsiteprobe-arg-not-for-scene",
            "launchsiteprobe-craft-arg-missing",
            "launchsiteprobe-no-ship",
            "launchsiteprobe-launch-locked",
            "launchsiteprobe-site-clear",
            "launchsiteprobe-facility-not-found",
            "launchsiteprobe-picker-open",
            "launchsiteprobe-craft-not-found",
        };

        /// <summary>The ERROR vocabulary. hlib.LAUNCHSITEPROBE_ERROR_REASONS mirrors it.</summary>
        internal static readonly string[] ErrorReasons =
        {
            "launchsiteprobe-no-dialog",
            "launchsiteprobe-other-dialog",
            "launchsiteprobe-dialog-shape",
            "launchsiteprobe-not-settled",
            "launchsiteprobe-threw",
        };

        internal static string PathToken(LaunchSiteProbePath path)
        {
            switch (path)
            {
                case LaunchSiteProbePath.Editor: return "editor";
                case LaunchSiteProbePath.SpaceCenter: return "spacecenter";
                default: return "none";
            }
        }

        internal static string PressToken(LaunchSiteProbePress press)
        {
            int i = (int)press;
            return i >= 0 && i < AnswerTokens.Length ? AnswerTokens[i] : "abort";
        }

        /// <summary>
        /// Parses the args against the scene the call runs in. The editor reads only
        /// <c>answer=</c> (the craft is the one on the stage, the site the editor's selected
        /// one); the Space Center needs <c>craft=</c> and takes <c>facility=</c> (default VAB).
        /// Every other scene is refused.
        /// </summary>
        internal static bool TryParse(
            TestCommandScene scene, string rawFacility, string rawCraft, string rawPress,
            out LaunchSiteProbeRequest request, out string rejectReason, out string detail)
        {
            request = new LaunchSiteProbeRequest
            {
                Path = LaunchSiteProbePath.None,
                Facility = EditorRouteFacility.None,
                Craft = null,
                Press = LaunchSiteProbePress.Abort,
            };
            rejectReason = null;
            detail = null;

            if (rawPress != null)
            {
                int p = Array.IndexOf(AnswerTokens, rawPress);
                if (p < 0)
                {
                    rejectReason = AnswerArgInvalidReason;
                    detail = "answer=" + rawPress + " valid=" + string.Join(",", AnswerTokens);
                    return false;
                }
                request.Press = (LaunchSiteProbePress)p;
            }

            if (scene == TestCommandScene.Editor)
            {
                if (rawFacility != null || rawCraft != null)
                {
                    rejectReason = ArgNotForSceneReason;
                    detail = "scene=EDITOR"
                        + (rawFacility != null ? " facility=" + rawFacility : string.Empty)
                        + (rawCraft != null ? " craft=" + rawCraft : string.Empty)
                        + " - the editor launches the craft on its stage from its selected site";
                    return false;
                }
                request.Path = LaunchSiteProbePath.Editor;
                return true;
            }

            if (scene != TestCommandScene.SpaceCenter)
            {
                rejectReason = WrongSceneReason;
                detail = "scene=" + scene;
                return false;
            }

            request.Path = LaunchSiteProbePath.SpaceCenter;
            request.Facility = EditorRouteFacility.VAB;
            if (rawFacility != null)
            {
                int f = Array.IndexOf(TestCommandEditorRoute.FacilityTokens, rawFacility);
                if (f < 0)
                {
                    rejectReason = FacilityArgInvalidReason;
                    detail = "facility=" + rawFacility + " valid="
                        + string.Join(",", TestCommandEditorRoute.FacilityTokens);
                    return false;
                }
                request.Facility = (EditorRouteFacility)(f + 1);
            }
            if (rawCraft == null)
            {
                rejectReason = CraftArgMissingReason;
                detail = "the Space Center picker launches a named craft";
                return false;
            }
            if (rawCraft.Trim().Length == 0)
            {
                rejectReason = CraftArgInvalidReason;
                detail = "craft=" + rawCraft;
                return false;
            }
            request.Craft = rawCraft;
            return true;
        }

        /// <summary>
        /// The pre-press gate. Editor: a ship with parts, the Launch button not locked
        /// (a lock that is the only blocker is waited out by the applier first), a site
        /// with at least one obstruction. Space Center: the facility found, its picker not
        /// already open. Returns null to proceed. The Space Center's obstruction check runs
        /// after the picker opened (the picker names the site), see
        /// <see cref="DecideSiteGate"/>.
        /// </summary>
        internal static string DecideEditorGate(bool hasEditor, int shipParts, bool launchUnlocked,
            int obstructing)
        {
            if (!hasEditor || shipParts <= 0) return NoShipReason;
            if (!launchUnlocked) return LaunchLockedReason;
            return DecideSiteGate(obstructing);
        }

        /// <summary>True when the editor gate is shut ONLY by the Launch lock.</summary>
        internal static bool EditorLockIsOnlyBlocker(bool hasEditor, int shipParts, bool launchUnlocked,
            int obstructing)
            => !launchUnlocked && DecideEditorGate(hasEditor, shipParts, true, obstructing) == null;

        internal static string DecideSpaceCenterGate(bool facilityFound, bool pickerOpen)
        {
            if (!facilityFound) return FacilityNotFoundReason;
            if (pickerOpen) return PickerOpenReason;
            return null;
        }

        /// <summary>A clear site is refused: pressing Launch there launches.</summary>
        internal static string DecideSiteGate(int obstructing)
            => obstructing > 0 ? null : SiteClearReason;

        /// <summary>
        /// One look while waiting for the dialog. The scene leaving wins (the launch went
        /// ahead without a dialog); then the dialog the probe came for; then, in the editor,
        /// another failed pre-flight test's dialog (same name, another title); the budget last.
        /// </summary>
        internal static LaunchSiteProbeAwaitOutcome DecideAwait(
            bool sceneLeft, bool targetDialogUp, bool otherDialogUp, double waitedSeconds)
        {
            if (sceneLeft) return LaunchSiteProbeAwaitOutcome.Launched;
            if (targetDialogUp) return LaunchSiteProbeAwaitOutcome.Dialog;
            if (otherDialogUp) return LaunchSiteProbeAwaitOutcome.OtherDialog;
            return waitedSeconds >= DialogWaitSeconds
                ? LaunchSiteProbeAwaitOutcome.TimedOut
                : LaunchSiteProbeAwaitOutcome.NotYet;
        }

        /// <summary>
        /// One settle poll after the press: a scene that left is a launch; otherwise the
        /// dialog must be gone and the scene must have stayed for both floors. A stock lock
        /// still held is a reported fact, not a wait condition (a recovery the press caused
        /// can hold the picker's lock behind its own summary dialog).
        /// </summary>
        internal static LaunchSiteProbeSettleOutcome DecideSettle(
            bool sceneLeft, bool dialogOpen, int frames, double seconds, bool budgetExpired)
        {
            if (sceneLeft) return LaunchSiteProbeSettleOutcome.Launched;
            if (!dialogOpen && frames >= MinSettleFrames && seconds >= MinSettleSeconds)
                return LaunchSiteProbeSettleOutcome.Stayed;
            return budgetExpired ? LaunchSiteProbeSettleOutcome.TimedOut : LaunchSiteProbeSettleOutcome.NotYet;
        }

        /// <summary>
        /// The button roles. Editor (<c>PreFlightCheck</c>): one or two buttons; the abort
        /// is the one labelled <paramref name="abortLabel"/>, the proceed is the other.
        /// Space Center (<c>shipSelected</c>): exactly three, resume / proceed / cancel.
        /// Any other shape is not the dialog the decompile describes.
        /// </summary>
        internal static LaunchSiteProbeButtons ClassifyButtons(
            LaunchSiteProbePath path, IList<string> labels, string abortLabel)
        {
            var r = new LaunchSiteProbeButtons { ShapeOk = false, AbortIndex = -1, ProceedIndex = -1 };
            int n = labels != null ? labels.Count : 0;
            if (path == LaunchSiteProbePath.SpaceCenter)
            {
                if (n != 3) return r;
                r.ShapeOk = true;
                r.ProceedIndex = 1;
                r.AbortIndex = 2;
                return r;
            }
            if (path != LaunchSiteProbePath.Editor || n < 1 || n > 2) return r;
            for (int i = n - 1; i >= 0; i--)
            {
                if (string.Equals(labels[i], abortLabel, StringComparison.Ordinal))
                {
                    r.AbortIndex = i;
                    break;
                }
            }
            if (r.AbortIndex < 0) return r;
            for (int i = 0; i < n; i++)
            {
                if (i == r.AbortIndex) continue;
                r.ProceedIndex = i;
                break;
            }
            r.ShapeOk = true;
            return r;
        }

        /// <summary>The button index the press lands on, and the token it reports.</summary>
        internal static int ChooseButton(LaunchSiteProbePress press, LaunchSiteProbeButtons buttons,
            out string pressedToken)
        {
            if (press == LaunchSiteProbePress.Proceed && buttons.ProceedIndex >= 0)
            {
                pressedToken = "proceed";
                return buttons.ProceedIndex;
            }
            pressedToken = "abort";
            return buttons.AbortIndex;
        }

        /// <summary>One-line, length-capped dialog text: line breaks become a literal
        /// <c>\n</c>.</summary>
        internal static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "-";
            string s = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\\n");
            if (s.Length > MaxLoggedTextLength) s = s.Substring(0, MaxLoggedTextLength) + "...";
            return s;
        }

        internal static string FormatLabels(IList<string> labels)
        {
            var sb = new StringBuilder("[");
            int n = labels != null ? labels.Count : 0;
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(OneLine(labels[i]));
            }
            return sb.Append(']').ToString();
        }

        internal static string FormatInteractable(IList<bool> interactable)
        {
            int n = interactable != null ? interactable.Count : 0;
            if (n == 0) return "-";
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(interactable[i] ? '1' : '0');
            }
            return sb.ToString();
        }

        /// <summary><c>1</c> / <c>0</c> for the proceed button's interactable state, <c>-</c>
        /// when the dialog has no proceed button.</summary>
        internal static string ProceedInteractableToken(LaunchSiteProbeButtons buttons, IList<bool> interactable)
        {
            if (buttons.ProceedIndex < 0 || interactable == null || buttons.ProceedIndex >= interactable.Count)
                return "-";
            return interactable[buttons.ProceedIndex] ? "1" : "0";
        }

        // ---- log lines and payload ----

        internal static string FormatStartLine(LaunchSiteProbeRequest r, string scene, string site,
            int obstructing, string firstObstructor)
            => "launchsiteprobe start path=" + PathToken(r.Path)
               + " scene=" + Token(scene)
               + (r.Path == LaunchSiteProbePath.SpaceCenter
                   ? " facility=" + TestCommandEditorRoute.FacilityToken(r.Facility) + " craft=" + Token(r.Craft)
                   : string.Empty)
               + " answer=" + PressToken(r.Press)
               + " site=" + Token(site)
               + " obstructing=" + obstructing.ToString(CultureInfo.InvariantCulture)
               + " first=" + Token(firstObstructor);

        internal static string FormatDialogLine(LaunchSiteProbePath path, string dialogName, string title,
            IList<string> labels, IList<bool> interactable, LaunchSiteProbeButtons buttons, string description)
            => "launchsiteprobe dialog path=" + PathToken(path)
               + " name=" + Token(dialogName)
               + " buttons=" + (labels != null ? labels.Count : 0).ToString(CultureInfo.InvariantCulture)
               + " labels=" + FormatLabels(labels)
               + " interactable=" + FormatInteractable(interactable)
               + " proceedOffered=" + (buttons.ProceedIndex >= 0 ? "1" : "0")
               + " proceedInteractable=" + ProceedInteractableToken(buttons, interactable)
               + " title=" + OneLine(title)
               + " description=" + OneLine(description);

        internal static string FormatPressLine(LaunchSiteProbePath path, string pressedToken, string label,
            bool interactable, LaunchSiteProbePress requested)
            => "launchsiteprobe press path=" + PathToken(path)
               + " requested=" + PressToken(requested)
               + " pressed=" + Token(pressedToken)
               + " label=" + OneLine(label)
               + " interactable=" + (interactable ? "1" : "0")
               + " - through the button's own callback (OptionSelected)"
               + (interactable ? string.Empty : "; the button is greyed, so a player's click could not land: this press meets the click-blocks behind it");

        internal static string FormatCompleteLine(LaunchSiteProbePath path, string site, string dialogName,
            int buttonCount, bool proceedOffered, string proceedInteractable, string pressed, string outcome,
            int obstructingBefore, int obstructingAfter, string firstAfter, bool lockHeld, double elapsedSeconds)
            => "launchsiteprobe complete path=" + PathToken(path)
               + " site=" + Token(site)
               + " dialog=" + Token(dialogName)
               + " buttons=" + buttonCount.ToString(CultureInfo.InvariantCulture)
               + " proceedOffered=" + (proceedOffered ? "1" : "0")
               + " proceedInteractable=" + Token(proceedInteractable)
               + " pressed=" + Token(pressed)
               + " outcome=" + Token(outcome)
               + " obstructingBefore=" + obstructingBefore.ToString(CultureInfo.InvariantCulture)
               + " obstructingAfter=" + obstructingAfter.ToString(CultureInfo.InvariantCulture)
               + " firstAfter=" + Token(firstAfter)
               + " lockHeld=" + (lockHeld ? "1" : "0")
               + " elapsed=" + elapsedSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s";

        internal static List<KeyValuePair<string, string>> BuildPayload(LaunchSiteProbePath path, string site,
            string dialogName, int buttonCount, bool proceedOffered, string proceedInteractable, string pressed,
            string outcome, int obstructingBefore, int obstructingAfter)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("path", PathToken(path)),
                new KeyValuePair<string, string>("site", Token(site)),
                new KeyValuePair<string, string>("dialog", Token(dialogName)),
                new KeyValuePair<string, string>("buttons", buttonCount.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("proceedOffered", proceedOffered ? "1" : "0"),
                new KeyValuePair<string, string>("proceedInteractable", Token(proceedInteractable)),
                new KeyValuePair<string, string>("pressed", Token(pressed)),
                new KeyValuePair<string, string>("outcome", Token(outcome)),
                new KeyValuePair<string, string>("obstructingBefore", obstructingBefore.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("obstructingAfter", obstructingAfter.ToString(CultureInfo.InvariantCulture)),
            };

        /// <summary>A one-word token: blank reads <c>-</c>, spaces become underscores.</summary>
        internal static string Token(string s) => string.IsNullOrEmpty(s) ? "-" : s.Replace(' ', '_');
    }
}
