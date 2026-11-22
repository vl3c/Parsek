using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>What one poll of a running <c>PartParitySweep</c> step concludes.</summary>
    internal enum PartParitySweepPollOutcome
    {
        /// <summary>The step's coroutine has not reported yet: poll again next frame.</summary>
        NotYet,

        /// <summary>The step reported OK: terminal OK with its payload.</summary>
        Done,

        /// <summary>The step reported a failure, or ended without reporting: terminal ERROR
        /// naming the runner's reason.</summary>
        Failed,

        /// <summary>The verb's deferral budget expired before the step reported: terminal
        /// ERROR.</summary>
        TimedOut,
    }

    /// <summary>
    /// Pure completion half of the <c>PartParitySweep</c> seam verb (design-part-ghost-parity.md
    /// section 7): the poll decision and the ERROR vocabulary the applier
    /// (<c>ParsekTestCommandAddon.PartParitySweep.cs</c>) answers with. The arg grammar, the
    /// REJECTED vocabulary, the host admission and the OK payload stay on
    /// <see cref="TestCommandPartParitySweep"/>.
    ///
    /// <para>Every terminal here is ERROR, never REJECTED: by the time the step reports, the
    /// runner may already have spawned and removed vessels, so a failure is a post-act
    /// terminal (the seam's verdict-family rule), not a refusal with no side effect.</para>
    /// </summary>
    internal static class PartParitySweepCompletion
    {
        /// <summary>ERROR: the runner reported a failure (or ended without reporting). The
        /// runner's own reason rides on the message (<c>reason=&lt;token&gt;</c>).</summary>
        internal const string StepFailedReason = "partparity-step-failed";

        /// <summary>ERROR: the deferral budget ran out before the runner reported. The runner
        /// time-boxes a step to <c>maxSeconds</c> (at most 480 s), so this means the engine
        /// hung, not that the step was slow.</summary>
        internal const string TimeoutReason = "partparity-timeout";

        /// <summary>The runner reason on <see cref="StepFailedReason"/>'s message when the
        /// runner's coroutine ended without calling its callback.</summary>
        internal const string NoResultFailure = "no-result";

        /// <summary>The ERROR vocabulary, in poll order.</summary>
        internal static readonly string[] ErrorReasons = { StepFailedReason, TimeoutReason };

        /// <summary>
        /// One poll. A reported result wins over an expired budget (a step that reported on
        /// the frame its budget ran out finished), and a coroutine that ended without a
        /// result is a failure, never a wait to the budget.
        /// </summary>
        internal static PartParitySweepPollOutcome DecidePoll(
            bool resultArrived, bool resultOk, bool routineEnded, bool budgetExpired)
        {
            if (resultArrived)
                return resultOk ? PartParitySweepPollOutcome.Done : PartParitySweepPollOutcome.Failed;
            if (routineEnded)
                return PartParitySweepPollOutcome.Failed;
            return budgetExpired ? PartParitySweepPollOutcome.TimedOut : PartParitySweepPollOutcome.NotYet;
        }

        /// <summary>The ERROR message for a failed step:
        /// <c>partparity-step-failed reason=&lt;token&gt;[ detail=&lt;detail&gt;]</c>. A blank
        /// reason reads <see cref="NoResultFailure"/>; whitespace in either part becomes
        /// <c>_</c> so the message stays one token per field.</summary>
        internal static string FormatStepFailedMessage(string reason, string detail)
        {
            string r = string.IsNullOrEmpty(reason) ? NoResultFailure : OneToken(reason);
            string msg = StepFailedReason + " reason=" + r;
            if (!string.IsNullOrEmpty(detail))
                msg += " detail=" + OneToken(detail);
            return msg;
        }

        /// <summary>The OK payload the applier answers with: the runner's own payload, or an
        /// empty list when it handed none.</summary>
        internal static List<KeyValuePair<string, string>> OkPayload(
            List<KeyValuePair<string, string>> payload)
            => payload ?? new List<KeyValuePair<string, string>>();

        /// <summary>The applier's Info line once a step answered OK.</summary>
        internal static string FormatOkLine(string label, string payloadLine, double elapsedSeconds)
            => "partparitysweep complete label=" + (label ?? string.Empty)
               + " elapsed=" + elapsedSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s "
               + (payloadLine ?? string.Empty);

        private static string OneToken(string s)
        {
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (char.IsWhiteSpace(chars[i])) chars[i] = '_';
            return new string(chars);
        }
    }
}
