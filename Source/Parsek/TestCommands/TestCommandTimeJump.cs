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
        /// <summary>UT has not yet reached the target, or the settle window has not
        /// elapsed.</summary>
        StillWaiting,

        /// <summary>UT reached (or passed) the target AND settle frames drained: terminal
        /// OK.</summary>
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

        /// <summary>Generous absolute bound (seconds) on a resolved target UT. ~31,700 years
        /// past the epoch is far beyond any legitimate jump; a target outside
        /// <c>[-MaxAbsTargetUt, +MaxAbsTargetUt]</c> (or a non-finite one) is an author fault
        /// and is REJECTED so an absurd value (a 1e308 UT, a NaN, an overflow from
        /// <c>now + delta</c>) can never reach <c>ExecuteJump</c>.</summary>
        internal const double MaxAbsTargetUt = 1e12;

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
        /// <c>600,0</c> fails: InvariantCulture only). A parsed-but-non-finite or absurdly
        /// large target (NaN / Infinity, an overflow from <c>now + delta</c>, or a magnitude
        /// beyond <see cref="MaxAbsTargetUt"/>) fails with
        /// <paramref name="error"/> = <c>"target-out-of-range"</c> so it can never reach
        /// <c>ExecuteJump</c>. Returns the resolved absolute target UT on success
        /// (<paramref name="error"/> null), or <c>double.NaN</c> on failure.
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

            double target;
            if (hasUt)
            {
                if (!double.TryParse(utArg, NumberStyles.Float, CultureInfo.InvariantCulture, out double ut))
                {
                    error = "missing-jump-target";
                    return double.NaN;
                }
                target = ut;
            }
            else
            {
                if (!double.TryParse(deltaArg, NumberStyles.Float, CultureInfo.InvariantCulture, out double delta))
                {
                    error = "missing-jump-target";
                    return double.NaN;
                }
                target = nowUT + delta;
            }

            // Finiteness + sane-magnitude guard (net472: no double.IsFinite, so test NaN /
            // Infinity explicitly). Rejects a non-finite target (NaN / Infinity, or an
            // overflow from now + delta) and an absurd magnitude before it can reach the
            // real epoch-shift path.
            if (double.IsNaN(target) || double.IsInfinity(target) || Math.Abs(target) > MaxAbsTargetUt)
            {
                error = "target-out-of-range";
                return double.NaN;
            }

            error = null;
            return target;
        }

        /// <summary>
        /// Decide the two-phase TimeJump completion. The clock is reached ONE-SIDEDLY:
        /// <paramref name="currentUT"/> has reached <c>targetUT - toleranceSeconds</c> or
        /// beyond. The one-sided latch is load-bearing: the epoch-shift jump does NOT pause
        /// the game, so the clock keeps advancing at the live rate and by the time the
        /// settle window drains <paramref name="currentUT"/> is already tens of milliseconds
        /// PAST the target and receding. A two-sided <c>Abs(current - target) &lt;= tol</c>
        /// test would therefore go false the instant the clock overshot and never re-satisfy,
        /// so every live jump fell through to the budget and ERRORed jump-timeout despite
        /// landing exactly. Reaching or passing the target latches reached true, and the jump
        /// is complete only once the settle window has also drained
        /// (<paramref name="settleFramesRemaining"/> &lt;= 0). The budget expiry is a
        /// catch-all that bounds a pathological non-landing jump (the clock never reaches
        /// the target at all). Kept pure so every cell is xUnit-covered without a live KSP
        /// scene. The exactly-on-target-minus-tolerance case is inclusive (reached).
        /// </summary>
        internal static JumpCompletionDecision DecideJumpCompletion(
            double elapsedSeconds, double currentUT, double targetUT,
            double toleranceSeconds, int settleFramesRemaining, double budgetSeconds)
        {
            bool reached = currentUT >= targetUT - toleranceSeconds;
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
