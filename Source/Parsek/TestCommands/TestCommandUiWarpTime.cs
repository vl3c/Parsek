using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>The four committed date fields of the Timeline warp row.</summary>
    internal struct UiWarpDate
    {
        internal int Year;
        internal int Day;
        internal int Hour;
        internal int Minute;
    }

    /// <summary>
    /// Pure half of <c>UiAction op=warp window=timeline year= day= hour= minute=
    /// confirm=warp|cancel</c>: press the Timeline window's "Warp to time" button and answer
    /// its confirm dialog. The applier (<c>ParsekTestCommandAddon.UiWarp.cs</c>) owns every
    /// live touch; the arg grammar, the refusal vocabulary, the post-confirm verdict and the
    /// payload live here so they are xUnit-covered without KSP.
    ///
    /// <para><b>THE BUTTON'S OWN BODY.</b> The four args are committed the way a player's
    /// Enter commits each box (<see cref="WarpToTimeMath.TryParseField"/>, the same floors),
    /// then <c>TimelineWindowUI.TryPressWarpToTimeForTesting</c> resolves the button state
    /// through the draw's own resolver and, only when it would be drawn enabled, runs the
    /// click body the drawn button runs (<c>WarpToTimeController.RequestWarp</c>). The
    /// confirm dialog that click spawns is then answered through the chosen
    /// <c>DialogGUIButton</c>'s own callback, the same one a click on it runs.</para>
    ///
    /// <para><b>WHY <c>confirm=</c> IS REQUIRED.</b> The click alone only raises a modal; the
    /// warp is the confirm button's. A default would either leave a modal standing over every
    /// later step or warp a lane that only meant to photograph the dialog, so the step names
    /// which button it presses. This is a mutating confirm reachable on purpose (the
    /// <c>op=dismiss</c> census policy keeps it census-safe; this op exists to warp).</para>
    ///
    /// <para><b>ONE-PHASE.</b> Every outcome is decided synchronously inside the confirm
    /// callback: a Space Center forward warp jumps the clock in the call, a rewind arms the
    /// pending forward warp and requests the Space Center load, and a flight warp arms the
    /// deferred Space Center warp. The scene load that follows a rewind holds the NEXT step
    /// at the dispatcher's safe point (<c>not-safe-point</c>) until it settles.</para>
    /// </summary>
    internal static class TestCommandUiWarpTime
    {
        // ----- arg keys -----

        internal const string YearArg = "year";
        internal const string DayArg = "day";
        internal const string HourArg = "hour";
        internal const string MinuteArg = "minute";
        internal const string ConfirmArg = "confirm";

        /// <summary>Every arg the Timeline form reads beyond <c>op</c> / <c>window</c>,
        /// in the order the payload echoes them.</summary>
        internal static readonly string[] TimelineArgKeys =
            { YearArg, DayArg, HourArg, MinuteArg, ConfirmArg };

        // ----- confirm tokens -----

        internal const string ConfirmWarpToken = "warp";
        internal const string ConfirmCancelToken = "cancel";

        // ----- refusal / error reasons -----

        /// <summary>One of the four date args is absent. All four are required: a
        /// half-entered date would warp to whatever the boxes last held.</summary>
        internal const string DateArgMissingReason = "warp-date-arg-missing";

        /// <summary>A date arg is not an integer (the box's own parse refuses it).</summary>
        internal const string DateArgInvalidReason = "warp-date-arg-invalid";

        /// <summary>No <c>confirm=</c> arg.</summary>
        internal const string ConfirmArgMissingReason = "warp-confirm-arg-missing";

        /// <summary>A <c>confirm=</c> value outside {warp, cancel}.</summary>
        internal const string ConfirmArgInvalidReason = "warp-confirm-arg-invalid";

        /// <summary><c>op=warp window=spawncontrol</c> named a Timeline-only arg. The row
        /// button there takes no date and raises no confirm, so the arg would be silently
        /// ignored.</summary>
        internal const string ArgUnsupportedReason = "warp-arg-unsupported";

        /// <summary>POST-CLICK: the enabled button was pressed and no confirm dialog stood
        /// afterwards. ERROR - the click body ran and the dialog it always spawns for an
        /// actionable plan did not appear.</summary>
        internal const string ConfirmNotShownReason = "warp-confirm-not-shown";

        /// <summary>POST-CLICK: the confirm dialog stood without the chosen button. ERROR,
        /// and the dialog is answered with Cancel when that one exists, so no modal is left
        /// over the following steps.</summary>
        internal const string ConfirmButtonMissingReason = "warp-confirm-button-missing";

        /// <summary>POST-CONFIRM, a Space Center backward warp: the confirm ran and no
        /// pending forward warp was left armed, which is what the controller does when the
        /// rewind refused to launch (its own Warn line names why).</summary>
        internal const string RewindNotLaunchedReason = "warp-rewind-not-launched";

        /// <summary>POST-CONFIRM, a Space Center forward warp: the clock did not reach the
        /// target (the jump's own validity guard refused).</summary>
        internal const string ForwardNotAppliedReason = "warp-forward-not-applied";

        /// <summary>POST-CONFIRM, in flight: no deferred Space Center warp was armed.</summary>
        internal const string NotDeferredReason = "warp-not-deferred";

        // ----- parses -----

        /// <summary>
        /// Parses the four date args. Each is REQUIRED and goes through the warp box's own
        /// parse (<see cref="WarpToTimeMath.TryParseField"/>), so a value the box would floor
        /// (Year 0 -&gt; 1, a negative hour -&gt; 0) is floored here too and the payload
        /// echoes the committed value.
        /// </summary>
        internal static bool TryParseDate(string rawYear, string rawDay, string rawHour,
            string rawMinute, out UiWarpDate date, out string rejectReason)
        {
            date = default(UiWarpDate);
            if (rawYear == null || rawDay == null || rawHour == null || rawMinute == null)
            {
                rejectReason = DateArgMissingReason;
                return false;
            }
            if (!WarpToTimeMath.TryParseField(WarpToTimeMath.WarpFieldKind.Year, rawYear, out int y)
                || !WarpToTimeMath.TryParseField(WarpToTimeMath.WarpFieldKind.Day, rawDay, out int d)
                || !WarpToTimeMath.TryParseField(WarpToTimeMath.WarpFieldKind.Hour, rawHour, out int h)
                || !WarpToTimeMath.TryParseField(WarpToTimeMath.WarpFieldKind.Minute, rawMinute, out int m))
            {
                rejectReason = DateArgInvalidReason;
                return false;
            }
            date = new UiWarpDate { Year = y, Day = d, Hour = h, Minute = m };
            rejectReason = null;
            return true;
        }

        /// <summary>Parses the required <c>confirm=</c> arg (case-sensitive).</summary>
        internal static bool TryParseConfirm(string raw, out bool confirmWarp, out string rejectReason)
        {
            confirmWarp = false;
            if (raw == null)
            {
                rejectReason = ConfirmArgMissingReason;
                return false;
            }
            if (raw == ConfirmWarpToken) { confirmWarp = true; rejectReason = null; return true; }
            if (raw == ConfirmCancelToken) { rejectReason = null; return true; }
            rejectReason = ConfirmArgInvalidReason;
            return false;
        }

        /// <summary>The first Timeline-only arg present on a <c>spawncontrol</c> warp step,
        /// or null when there is none.</summary>
        internal static string FirstTimelineOnlyArg(IDictionary<string, string> args)
        {
            if (args == null) return null;
            for (int i = 0; i < TimelineArgKeys.Length; i++)
                if (args.ContainsKey(TimelineArgKeys[i]))
                    return TimelineArgKeys[i];
            return null;
        }

        // ----- the post-confirm verdict -----

        /// <summary>
        /// Whether the answered confirm did what its plan promises, read from the state the
        /// controller leaves behind synchronously. Null means OK.
        /// <list type="bullet">
        /// <item><c>cancel</c>: always OK (the Cancel callback only logs).</item>
        /// <item>in flight: the whole warp defers to the Space Center, so a deferred warp
        /// must be armed (<see cref="NotDeferredReason"/>).</item>
        /// <item>Space Center ForwardOnly: the clock must have reached the target within
        /// <see cref="WarpToTimeMath.AtTargetEpsilonSeconds"/>
        /// (<see cref="ForwardNotAppliedReason"/>).</item>
        /// <item>Space Center RewindThenForward: the pending forward warp must still be
        /// armed, which the controller keeps only when the rewind requested its load
        /// (<see cref="RewindNotLaunchedReason"/>).</item>
        /// </list>
        /// </summary>
        internal static string DecideConfirmOutcome(
            WarpToTimeMath.WarpPlanKind plan, bool inFlight, bool confirmWarp,
            bool pendingAfter, bool deferredKscAfter, double utAfter, double targetUT)
        {
            if (!confirmWarp)
                return null;
            if (inFlight)
                return deferredKscAfter ? null : NotDeferredReason;
            if (plan == WarpToTimeMath.WarpPlanKind.ForwardOnly)
                return utAfter >= targetUT - WarpToTimeMath.AtTargetEpsilonSeconds
                    ? null : ForwardNotAppliedReason;
            if (plan == WarpToTimeMath.WarpPlanKind.RewindThenForward)
                return pendingAfter ? null : RewindNotLaunchedReason;
            // AtTarget / Unreachable never reach a confirm: the button is drawn greyed.
            return null;
        }

        // ----- tokens and payload -----

        internal static string PlanToken(WarpToTimeMath.WarpPlanKind plan)
        {
            switch (plan)
            {
                case WarpToTimeMath.WarpPlanKind.ForwardOnly: return "forward";
                case WarpToTimeMath.WarpPlanKind.RewindThenForward: return "rewind";
                case WarpToTimeMath.WarpPlanKind.AtTarget: return "at-target";
                default: return "unreachable";
            }
        }

        /// <summary>The <c>rewindKind=</c> tokens. The enum-to-token map lives on the
        /// applier (<c>ParsekTestCommandAddon.TimelineWarpRewindKindToken</c>): this pure half
        /// names no controller type, so it stays out of the controller dependency knot.</summary>
        internal const string RewindKindNoneToken = "none";
        internal const string RewindKindLaunchToken = "launch";
        internal const string RewindKindCareerStartToken = "career-start";

        /// <summary>The op / window tokens the payload echoes. Spelled here rather than read
        /// off <see cref="TestCommandUiAction"/> for the same knot reason; an xUnit cell pins
        /// them equal to <c>TestCommandUiAction.WarpOpToken</c> / <c>TimelineWindow</c>.</summary>
        internal const string OpToken = "warp";
        internal const string WindowToken = "timeline";

        /// <summary>
        /// OK payload: <c>op=warp window=timeline year= day= hour= minute= targetUT= plan=
        /// rewindKind= confirm= pending= deferredKsc=</c>. <c>plan</c> and <c>rewindKind</c>
        /// are what the button resolved at the press; <c>pending</c> / <c>deferredKsc</c>
        /// are the warp requests standing after the confirm.
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildPayload(
            UiWarpDate date, double targetUT, WarpToTimeMath.WarpPlanKind plan,
            string rewindKindToken, bool confirmWarp,
            bool pendingAfter, bool deferredKscAfter)
        {
            var ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                Kv("op", OpToken),
                Kv("window", WindowToken),
                Kv(YearArg, date.Year.ToString(ic)),
                Kv(DayArg, date.Day.ToString(ic)),
                Kv(HourArg, date.Hour.ToString(ic)),
                Kv(MinuteArg, date.Minute.ToString(ic)),
                Kv("targetUT", targetUT.ToString("F1", ic)),
                Kv("plan", PlanToken(plan)),
                Kv("rewindKind", rewindKindToken),
                Kv(ConfirmArg, confirmWarp ? ConfirmWarpToken : ConfirmCancelToken),
                Kv("pending", pendingAfter ? "true" : "false"),
                Kv("deferredKsc", deferredKscAfter ? "true" : "false"),
            };
        }

        private static KeyValuePair<string, string> Kv(string k, string v)
            => new KeyValuePair<string, string>(k, v ?? string.Empty);
    }
}
