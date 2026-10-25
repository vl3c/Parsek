using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Parsek.Patches;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure half of the automation-only <c>AlarmJump</c> seam verb: a stock Alarm Clock alarm
    /// bound to a named vessel fires its stock message dialog, and that dialog's Jump is
    /// pressed (GC-13, todo GC13-ALARM-JUMP-BYPASSES-SWITCH-RULES).
    ///
    /// <para><b>Stock ground truth (decompiled KSP 1.12.5).</b> An alarm is an
    /// <c>AlarmTypeBase</c> (<c>AlarmTypeRaw</c> is the plain timed one) holding only the
    /// vessel's pid (<c>vesselId</c>); <c>AlarmClockScenario.AddAlarm</c> lists it, and the
    /// scenario's update loop fires it through the internal <c>AlarmTypeBase.TriggerAlarm</c>,
    /// which spawns <c>AlarmClockMessageDialog.Spawn(alarm, ActionAlarm)</c> when the alarm
    /// shows a message. <c>Spawn</c> shows the Jump button only when the pid is a live vessel
    /// other than the active one; the button's listener (added in <c>Start</c>, a frame later)
    /// is <c>OnJump</c>. The dialog's Delete-and-close button is <c>OnDeleteOnClose</c>, which
    /// runs <c>ActionAlarm</c> (deleting the alarm) and destroys the dialog.</para>
    ///
    /// <para><b>What the verb drives.</b> It builds an <c>AlarmTypeRaw</c> bound to the target
    /// (an hour ahead, so the scenario's own loop cannot fire it a second time), adds it through
    /// <c>AddAlarm</c>, fires it through stock's own <c>TriggerAlarm</c> (so Parsek's
    /// <c>AlarmJumpButtonMarkPatch</c> Spawn postfix runs exactly as on a real fire), reads
    /// whether the Jump button is shown, then calls stock's <c>OnJump</c> (Harmony-patched, so
    /// <c>AlarmJumpSwitchPatch</c>'s Prefix runs as on a click) and closes the dialog through
    /// <c>OnDeleteOnClose</c>, leaving no alarm behind. <c>OnJump</c> is invoked directly rather
    /// than through the button's listener because the listener is added in <c>Start</c>, which
    /// has not run in the frame the dialog was spawned.</para>
    ///
    /// <para><b>Grammar.</b> <c>cmd=AlarmJump vessel=&lt;exact name&gt;|pid=&lt;persistentId&gt;
    /// [jump=shown|always|never]</c>, issued in FLIGHT. Exactly one of <c>vessel=</c> /
    /// <c>pid=</c>. A ghost map vessel is a legal target (an alarm set on a ghost's orbit node is
    /// one of the cases the fix refuses). <c>jump=shown</c> (the default) presses Jump only when
    /// the button is shown, as a player can; <c>jump=always</c> presses it even when hidden
    /// (the block behind the mark); <c>jump=never</c> only reads the button.</para>
    ///
    /// <para><b>Answer.</b> OK payload, in wire order: <c>targetPid= targetIsGhost=
    /// targetLoaded= alarmId= stockWouldShowJump= jumpShown= decision= jump= pressed= before=
    /// after= switched= sceneLoadRequested= alarmRemoved=</c>. <c>stockWouldShowJump</c> is
    /// stock's own Spawn predicate; <c>jumpShown</c> is the button's state after Parsek's
    /// postfix; <c>decision</c> is what <c>AlarmJumpSwitchPatch.DecideLive</c> answers for the
    /// target before the press; <c>sceneLoadRequested</c> is whether stock's
    /// <c>onGameSceneLoadRequested</c> fired during the press (a far switch reloads the
    /// scene, so <c>after</c> alone cannot see it). Line: <c>alarmjump result</c> followed by
    /// the payload.</para>
    /// </summary>
    internal static class TestCommandAlarmJump
    {
        internal const string Verb = "AlarmJump";
        internal const string VesselKey = "vessel";
        internal const string PidKey = "pid";
        internal const string JumpKey = "jump";

        internal const string PressShown = "shown";
        internal const string PressAlways = "always";
        internal const string PressNever = "never";

        internal const string TargetArgMissingReason = "alarmjump-target-arg-missing";
        internal const string TargetArgConflictReason = "alarmjump-target-arg-conflict";
        internal const string PidArgInvalidReason = "alarmjump-pid-arg-invalid";
        internal const string JumpArgInvalidReason = "alarmjump-jump-arg-invalid";
        internal const string NoAlarmClockReason = "alarmjump-no-alarm-clock";
        internal const string TargetNotFoundReason = "alarmjump-target-not-found";
        internal const string TargetAmbiguousReason = "alarmjump-target-ambiguous";

        internal const string AddFailedReason = "alarmjump-add-failed";
        internal const string TriggerFailedReason = "alarmjump-trigger-failed";
        internal const string NoDialogReason = "alarmjump-no-dialog";
        internal const string JumpThrewReason = "alarmjump-jump-threw";

        /// <summary>The REJECTED vocabulary, in gate order. hlib.ALARMJUMP_REASONS mirrors it
        /// (AlarmJumpSourceSyncTests).</summary>
        internal static readonly string[] Reasons =
        {
            "alarmjump-target-arg-missing",
            "alarmjump-target-arg-conflict",
            "alarmjump-pid-arg-invalid",
            "alarmjump-jump-arg-invalid",
            "alarmjump-no-alarm-clock",
            "alarmjump-target-not-found",
            "alarmjump-target-ambiguous",
        };

        /// <summary>The ERROR vocabulary (a stock member the verb drives failed).
        /// hlib.ALARMJUMP_ERROR_REASONS mirrors it.</summary>
        internal static readonly string[] ErrorReasons =
        {
            "alarmjump-add-failed",
            "alarmjump-trigger-failed",
            "alarmjump-no-dialog",
            "alarmjump-jump-threw",
        };

        /// <summary>The <c>decision=</c> values, one per
        /// <see cref="AlarmJumpSwitchPatch.JumpDecision"/> member in declaration order, then
        /// <c>inert</c> (the game-mode gate). hlib.ALARMJUMP_DECISIONS mirrors it.</summary>
        internal static readonly string[] Decisions =
        {
            "leave-to-stock",
            "refuse-ghost",
            "refuse-refly",
            "inert",
        };

        internal const string InertDecision = "inert";

        /// <summary>
        /// Parses the args. Returns null on success or the refusal reason (target first, then
        /// jump). Exactly one of <paramref name="rawVessel"/> / <paramref name="rawPid"/>;
        /// a pid is a positive base-10 uint. <c>jump=</c> is exact and lower-case; absent
        /// means <see cref="PressShown"/>.
        /// </summary>
        internal static string ParseArgs(string rawVessel, string rawPid, string rawPress,
            out string vesselName, out uint pid, out string press)
        {
            vesselName = null;
            pid = 0u;
            press = PressShown;
            bool hasVessel = !string.IsNullOrEmpty(rawVessel);
            bool hasPid = !string.IsNullOrEmpty(rawPid);
            if (!hasVessel && !hasPid)
                return TargetArgMissingReason;
            if (hasVessel && hasPid)
                return TargetArgConflictReason;
            if (hasPid)
            {
                if (!uint.TryParse(rawPid, NumberStyles.None, CultureInfo.InvariantCulture, out pid)
                    || pid == 0u)
                {
                    pid = 0u;
                    return PidArgInvalidReason;
                }
            }
            else
            {
                vesselName = rawVessel;
            }
            if (rawPress != null)
            {
                if (string.Equals(rawPress, PressShown, StringComparison.Ordinal)
                    || string.Equals(rawPress, PressAlways, StringComparison.Ordinal)
                    || string.Equals(rawPress, PressNever, StringComparison.Ordinal))
                    press = rawPress;
                else
                    return JumpArgInvalidReason;
            }
            return null;
        }

        /// <summary>The target-resolution refusal for a sweep that found
        /// <paramref name="matches"/> vessels (ghost map vessels included), or null for one.</summary>
        internal static string ClassifyMatches(int matches)
        {
            if (matches <= 0) return TargetNotFoundReason;
            if (matches > 1) return TargetAmbiguousReason;
            return null;
        }

        /// <summary>
        /// Stock's own Jump-button predicate (decompiled <c>AlarmClockMessageDialog.Spawn</c>):
        /// the alarm names a vessel, the pid is a live vessel, and it is not the active vessel
        /// (no active vessel counts as other).
        /// </summary>
        internal static bool StockWouldShowJump(uint alarmVesselId, bool pidIsLive, uint activePid)
        {
            if (alarmVesselId == 0u || !pidIsLive) return false;
            return activePid == 0u || activePid != alarmVesselId;
        }

        /// <summary>Whether the verb presses Jump for <paramref name="press"/> given the button state.</summary>
        internal static bool ShouldPress(string press, bool jumpShown)
        {
            if (string.Equals(press, PressAlways, StringComparison.Ordinal)) return true;
            if (string.Equals(press, PressNever, StringComparison.Ordinal)) return false;
            return jumpShown;
        }

        /// <summary>The wire token of a patch decision.</summary>
        internal static string DecisionToken(AlarmJumpSwitchPatch.JumpDecision decision)
        {
            int i = (int)decision;
            return i >= 0 && i < Decisions.Length - 1 ? Decisions[i] : "unknown";
        }

        internal static string PidOrNone(uint pid)
            => pid == 0u ? "none" : pid.ToString(CultureInfo.InvariantCulture);

        private static string B(bool b) => b ? "true" : "false";

        /// <summary>The OK payload, in wire order.</summary>
        internal static List<KeyValuePair<string, string>> BuildOkPayload(
            uint targetPid, bool targetIsGhost, bool targetLoaded, uint alarmId,
            bool stockWouldShowJump, bool jumpShown, string decision, string press, bool pressed,
            uint before, uint after, bool sceneLoadRequested, bool alarmRemoved)
        {
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("targetPid", PidOrNone(targetPid)),
                new KeyValuePair<string, string>("targetIsGhost", B(targetIsGhost)),
                new KeyValuePair<string, string>("targetLoaded", B(targetLoaded)),
                new KeyValuePair<string, string>("alarmId", alarmId.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("stockWouldShowJump", B(stockWouldShowJump)),
                new KeyValuePair<string, string>("jumpShown", B(jumpShown)),
                new KeyValuePair<string, string>("decision", string.IsNullOrEmpty(decision) ? "unknown" : decision),
                new KeyValuePair<string, string>("jump", string.IsNullOrEmpty(press) ? PressShown : press),
                new KeyValuePair<string, string>("pressed", B(pressed)),
                new KeyValuePair<string, string>("before", PidOrNone(before)),
                new KeyValuePair<string, string>("after", PidOrNone(after)),
                new KeyValuePair<string, string>("switched", B(before != after)),
                new KeyValuePair<string, string>("sceneLoadRequested", B(sceneLoadRequested)),
                new KeyValuePair<string, string>("alarmRemoved", B(alarmRemoved)),
            };
        }

        /// <summary>The grep-stable line logged on OK: <c>alarmjump result</c> and the payload.</summary>
        internal static string FormatResultLine(List<KeyValuePair<string, string>> payload)
        {
            var sb = new StringBuilder("alarmjump result");
            if (payload != null)
                for (int i = 0; i < payload.Count; i++)
                    sb.Append(' ').Append(payload[i].Key).Append('=').Append(payload[i].Value);
            return sb.ToString();
        }
    }
}
