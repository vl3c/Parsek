using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The two-phase <c>TimeJump</c> completion outcome (M-C1). A forward epoch-shift
    /// jump lands the clock synchronously, but a short settle window lets the engine
    /// playback loop drain the spawn queue (chain tips crossed during the jump) before
    /// the terminal response claims completion.
    /// </summary>
    internal enum JumpCompletionDecision
    {
        /// <summary>UT not yet within tolerance, or the settle window has not elapsed.</summary>
        StillWaiting,

        /// <summary>UT within tolerance AND settle frames drained: terminal OK.</summary>
        CompleteOk,

        /// <summary>Budget expired without reaching the target (bounds a pathological
        /// SetUniversalTime failure; should not happen for a synchronous jump): terminal
        /// ERROR (msg=jump-timeout).</summary>
        JumpTimeout,
    }

    /// <summary>
    /// Pure decision + payload helpers for the forward-only, epoch-shift <c>TimeJump</c>
    /// verb (M-C1). The verb resolves a forward target UT (exactly one of <c>ut</c> or
    /// <c>deltaSeconds</c>), refuses backward / zero jumps mirroring
    /// <see cref="Parsek.TimeJumpManager.IsValidJump"/>, and drives the real
    /// <c>ParsekFlight.TimeJumpTo</c> -&gt; <c>TimeJumpManager.ExecuteJump</c> epoch-shift
    /// path (frozen relative positions, SMA/ecc/inc unchanged, MNA-at-epoch shifted by the
    /// delta, earlier chain tips auto-spawned). The completion decider confirms EXACTLY the
    /// clock reached the target within tolerance and the settle window elapsed; it does NOT
    /// confirm any specific vessel spawn (a following <c>RecordingState</c> step or the
    /// verifier chain judges the consequences). Kept pure so every cell is xUnit-covered
    /// without a live KSP scene.
    /// </summary>
    internal static class TestCommandTimeJump
    {
        /// <summary>Fixed epsilon that absorbs float representation of the landed clock
        /// (<c>Planetarium.SetUniversalTime</c> lands the clock exactly; this only covers
        /// double-vs-double representation).</summary>
        internal const double UtToleranceSeconds = 1e-3;

        /// <summary>A short settle window (in poll frames) so the engine playback loop can
        /// drain the spawn queue and let newly spawned end-of-recording ghosts settle
        /// before the terminal OK.</summary>
        internal const int SettleFrames = 3;

        /// <summary>
        /// Pure forward-jump gate, mirroring <see cref="Parsek.TimeJumpManager.IsValidJump"/>
        /// (strictly <c>target &gt; current</c>). A backward or zero jump is a DRIVER
        /// refusal (the orchestrator asked for a non-forward jump), never a Parsek defect.
        /// </summary>
        internal static bool IsForwardJump(double nowUT, double targetUT)
            => targetUT > nowUT;

        /// <summary>
        /// Resolve the forward target UT from EXACTLY one of <c>ut</c> (absolute) or
        /// <c>deltaSeconds</c> (positive delta), both InvariantCulture floats. Both absent,
        /// both present, or an unparseable value all fail with
        /// <paramref name="error"/> = <c>"missing-jump-target"</c> (a locale comma such as
        /// <c>600,0</c> fails: InvariantCulture only). Returns the resolved absolute target
        /// UT on success (<paramref name="error"/> null), or <c>double.NaN</c> on failure.
        /// </summary>
        internal static double ResolveTargetUt(double nowUT, string utArg, string deltaArg, out string error)
        {
            bool hasUt = !string.IsNullOrEmpty(utArg);
            bool hasDelta = !string.IsNullOrEmpty(deltaArg);

            if (hasUt == hasDelta)
            {
                // Both absent or both present: ambiguous / missing target.
                error = "missing-jump-target";
                return double.NaN;
            }

            if (hasUt)
            {
                if (!double.TryParse(utArg, NumberStyles.Float, CultureInfo.InvariantCulture, out double ut))
                {
                    error = "missing-jump-target";
                    return double.NaN;
                }
                error = null;
                return ut;
            }

            if (!double.TryParse(deltaArg, NumberStyles.Float, CultureInfo.InvariantCulture, out double delta))
            {
                error = "missing-jump-target";
                return double.NaN;
            }

            error = null;
            return nowUT + delta;
        }

        /// <summary>
        /// Decide the two-phase TimeJump completion. The clock is reached when the current
        /// UT is within <paramref name="toleranceSeconds"/> of the captured target; the
        /// jump is complete only once the settle window has also drained
        /// (<paramref name="settleFramesRemaining"/> &lt;= 0). The budget expiry is a
        /// catch-all that bounds a pathological non-landing jump. Kept pure so every cell
        /// is xUnit-covered without a live KSP scene. The exactly-on-tolerance case is
        /// inclusive (CompleteOk).
        /// </summary>
        internal static JumpCompletionDecision DecideJumpCompletion(
            double elapsedSeconds, double currentUT, double targetUT,
            double toleranceSeconds, int settleFramesRemaining, double budgetSeconds)
        {
            bool reached = Math.Abs(currentUT - targetUT) <= toleranceSeconds;
            if (reached && settleFramesRemaining <= 0)
                return JumpCompletionDecision.CompleteOk;
            if (elapsedSeconds >= budgetSeconds)
                return JumpCompletionDecision.JumpTimeout;
            return JumpCompletionDecision.StillWaiting;
        }

        /// <summary>
        /// Terminal completion payload once the clock reached the target and settled.
        /// <c>ut</c> = the reached UT, <c>target</c> = the captured target, <c>delta</c> =
        /// <c>target - startUT</c> (the actual jump magnitude). All InvariantCulture
        /// round-trip ("R") so no locale comma can leak into a UT.
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(
            double reachedUT, double targetUT, double startUT)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("ut", reachedUT.ToString("R", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("target", targetUT.ToString("R", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("delta", (targetUT - startUT).ToString("R", CultureInfo.InvariantCulture)),
            };
    }
}
