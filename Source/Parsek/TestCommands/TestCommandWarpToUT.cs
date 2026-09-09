using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The two-phase <c>WarpToUT</c> completion outcome (RF-12 / phase 4). Unlike
    /// <see cref="JumpCompletionDecision"/> - whose jump lands the clock synchronously -
    /// a warp advances the clock over many frames while the vessel TRAVELS its
    /// trajectory, so the completion is a poll, not a settle.
    /// </summary>
    internal enum WarpCompletionDecision
    {
        /// <summary>The clock has not reached the target, or warp has not returned to
        /// 1x, or the settle window has not drained.</summary>
        StillWaiting,

        /// <summary>Target reached, warp back at rate index 0, settle drained: terminal
        /// OK.</summary>
        CompleteOk,

        /// <summary>Budget expired before the clock reached the target (a rate clamp the
        /// verb cannot lift - a stock altitude limit, a vessel under acceleration - makes
        /// a long span unreachable in the budget): terminal ERROR
        /// (msg=<c>warp-timeout</c>).</summary>
        WarpTimeout,
    }

    /// <summary>
    /// Pure decision + payload helpers for the forward-only, REAL-TIME-ADVANCING
    /// <c>WarpToUT</c> verb.
    ///
    /// <para><b>Why this is not a second spelling of TimeJump.</b> <c>TimeJump</c> is an
    /// EPOCH SHIFT: <c>ParsekFlight.TimeJumpTo</c> -&gt; <c>TimeJumpManager.ExecuteJump</c>
    /// stops warp and moves the clock instantly with frozen relative positions
    /// (SMA/ecc/inc unchanged, MNA-at-epoch shifted by the delta). The clock moves; the
    /// vessel does not travel. <c>WarpToUT</c> drives the stock rails rate ladder
    /// (<c>TimeWarp.SetRate</c>) so the world SIMULATES forward - the vessel flies its
    /// trajectory, re-enters, and can impact. That difference is the whole reason the
    /// verb exists: RF12-NO-SEAM-PATH-CONCLUDES-A-REFLY-IN-FLIGHT named the epoch shift
    /// as the reason no seam path could crash a re-fly, and this is the instrument that
    /// answers it. It is deliberately GENERAL - any lane that wants real elapsed game
    /// time (a decaying orbit, a descent, a burn coast) can drive it.</para>
    ///
    /// <para><b>The ladder is advisory, the read-back is truth.</b> Stock owns the real
    /// clamps (a body's rails altitude limit, a vessel under acceleration, an SOI
    /// transition guard) and applies them inside <c>TimeWarp.SetRate</c>. This class
    /// selects a rate the CALLER asked for, bounded by a ceiling and by the optional
    /// <c>maxRate</c> cap; the applier then reads <c>TimeWarp.CurrentRateIndex</c> back
    /// and logs both. A clamp is therefore NOT a refusal - it is a slower warp, and the
    /// verb's budget is what bounds it. Only a warp that cannot be driven at all
    /// (no <c>TimeWarp</c> controller, a stock input lock on TIMEWARP) is REJECTED.</para>
    ///
    /// <para><b>There are TWO ladders and the caller passes whichever is live.</b> Stock
    /// keeps <c>warpRates</c> (rails) and <c>physicsWarpRates</c> (physics warp,
    /// <c>TimeWarp.Modes.LOW</c>) as separate arrays with the SAME index space and very
    /// different values - rails index 3 is 50x, physics index 3 is 4x. RF-12W's reading
    /// run 2 logged <c>rateIndex=3 rate=4</c> inside the atmosphere, which is what
    /// surfaced the pair. This class takes the ladder as a parameter and never reads
    /// either array itself, so the mode question belongs entirely to the applier
    /// (<c>SafeWarpRates</c> / <c>SafeMaxRateIndexForActiveVessel</c>) and the selection
    /// math stays a pure function of whatever rungs it is handed.</para>
    ///
    /// <para><b>The ladder lowers itself.</b> <see cref="SelectRateIndex"/> requires at
    /// least <see cref="MinRealSecondsAtRate"/> REAL seconds of running at a rate before
    /// it will select it, so as the target nears the selection walks back down the ladder
    /// and reaches index 0 in the last few seconds. That is the mirror-direction
    /// guarantee (a verb that raises warp must lower it deterministically): the ladder
    /// lowers on approach, the applier force-sets rate 0 the instant the target is
    /// reached, AND the timeout / exception paths force rate 0 before their terminal, so
    /// no path leaves the game warped.</para>
    ///
    /// Kept pure so every cell is xUnit-covered without a live KSP scene.
    /// </summary>
    internal static class TestCommandWarpToUT
    {
        /// <summary>Tolerance (seconds) on "the clock reached the target". A warp lands
        /// the clock on a physics frame boundary, never exactly, so this absorbs one
        /// frame at 1x plus double representation.</summary>
        internal const double UtToleranceSeconds = 0.25;

        /// <summary>Poll frames the applier waits after the clock reached the target AND
        /// warp returned to 1x, so the un-warp settle (ghost spawn queue, the physics
        /// re-pack) drains before the terminal OK. Mirrors
        /// <c>TestCommandTimeJump.SettleFrames</c>.</summary>
        internal const int SettleFrames = 3;

        /// <summary>Generous absolute bound (seconds) on a resolved target UT, verbatim
        /// from <c>TestCommandTimeJump.MaxAbsTargetUt</c>: a target outside
        /// <c>[-MaxAbsTargetUt, +MaxAbsTargetUt]</c> (or a non-finite one) is an author
        /// fault and is REJECTED before it can reach <c>TimeWarp</c>.</summary>
        internal const double MaxAbsTargetUt = 1e12;

        /// <summary>
        /// The minimum REAL seconds a selected rate must be able to run for. Mirrors
        /// stock's own <c>WarpTo</c> default <c>minTimeWarping = 2.5</c>. It is what
        /// makes the ladder step DOWN on approach rather than overshooting: selecting
        /// rate R is only allowed while at least <c>R * 2.5</c> game-seconds remain, so
        /// the worst-case overshoot past the target is bounded by one poll frame at the
        /// last selected rate.
        /// </summary>
        internal const double MinRealSecondsAtRate = 2.5;

        /// <summary>The <c>maxRate</c> value meaning "no cap" (the arg was absent).</summary>
        internal const double UncappedMaxRate = 0.0;

        // ----- Refusal reasons (the response msg token, verbatim) -----

        /// <summary><c>ut</c> absent or unparseable under InvariantCulture.</summary>
        internal const string MissingTargetReason = "missing-warp-target";

        /// <summary>A parsed target that is non-finite or absurd in magnitude.</summary>
        internal const string TargetOutOfRangeReason = "target-out-of-range";

        /// <summary>The target is not strictly in the future.</summary>
        internal const string BackwardWarpReason = "backward-warp";

        /// <summary><c>maxRate</c> present but unparseable or below 1.</summary>
        internal const string MaxRateInvalidReason = "max-rate-invalid";

        /// <summary>No <c>TimeWarp</c> controller in the scene: nothing to drive.</summary>
        internal const string WarpUnavailableReason = "warp-unavailable";

        /// <summary>A stock input lock owns TIMEWARP (a modal dialog, a scene
        /// transition), so a driven rate change would be silently swallowed.</summary>
        internal const string WarpLockedReason = "warp-locked";

        /// <summary>The completion budget expired before the clock reached the target.</summary>
        internal const string WarpTimeoutReason = "warp-timeout";

        /// <summary>
        /// Pure forward-warp gate (strictly <c>target &gt; now</c>). A backward or zero
        /// target is a DRIVER refusal (the orchestrator asked to warp into the past),
        /// never a Parsek defect. Mirrors <c>TestCommandTimeJump.IsForwardJump</c>.
        /// </summary>
        internal static bool IsForwardWarp(double nowUT, double targetUT)
            => targetUT > nowUT;

        /// <summary>
        /// Resolve the ABSOLUTE target UT from <c>ut</c> (InvariantCulture float).
        /// Deliberately absolute-only, unlike <c>TimeJump</c>'s <c>ut</c>/<c>deltaSeconds</c>
        /// pair: a warp's own duration depends on the clamps stock applies, so a
        /// delta-relative target would land somewhere the spec author cannot name in a
        /// log contract. Absent / unparseable fails <see cref="MissingTargetReason"/>
        /// (a locale comma such as <c>600,0</c> fails: InvariantCulture only); non-finite
        /// or absurd fails <see cref="TargetOutOfRangeReason"/>. Returns
        /// <c>double.NaN</c> on failure.
        /// </summary>
        internal static double ResolveTargetUt(string utArg, out string error)
        {
            if (string.IsNullOrEmpty(utArg))
            {
                error = MissingTargetReason;
                return double.NaN;
            }

            if (!double.TryParse(utArg, NumberStyles.Float, CultureInfo.InvariantCulture, out double target))
            {
                error = MissingTargetReason;
                return double.NaN;
            }

            // net472 has no double.IsFinite, so test NaN / Infinity explicitly.
            if (double.IsNaN(target) || double.IsInfinity(target) || Math.Abs(target) > MaxAbsTargetUt)
            {
                error = TargetOutOfRangeReason;
                return double.NaN;
            }

            error = null;
            return target;
        }

        /// <summary>
        /// Resolve the OPTIONAL <c>maxRate</c> cap. Absent yields
        /// <see cref="UncappedMaxRate"/> (0, meaning "whatever the ladder and stock
        /// allow"). Present must parse under InvariantCulture and be at least 1 -
        /// anything else is <see cref="MaxRateInvalidReason"/>, fail-closed rather than
        /// ignored, so a mis-typed cap can never read as an uncapped warp. Returns
        /// <c>double.NaN</c> on failure.
        /// </summary>
        internal static double ResolveMaxRate(string maxRateArg, out string error)
        {
            if (string.IsNullOrEmpty(maxRateArg))
            {
                error = null;
                return UncappedMaxRate;
            }

            if (!double.TryParse(maxRateArg, NumberStyles.Float, CultureInfo.InvariantCulture, out double cap)
                || double.IsNaN(cap) || double.IsInfinity(cap) || cap < 1.0)
            {
                error = MaxRateInvalidReason;
                return double.NaN;
            }

            error = null;
            return cap;
        }

        /// <summary>
        /// The up-front feasibility gate, in evaluation order. Returns the refusal reason
        /// or <c>null</c> when the warp can be driven. Deliberately SHORT: a rate clamp
        /// (altitude limit, acceleration, SOI guard) is not listed, because stock applies
        /// those INSIDE SetRate and the honest consequence is a slower warp, not a
        /// refusal - see the class remarks.
        /// </summary>
        internal static string EvaluateFeasibility(bool warpControllerPresent, bool warpLocked)
        {
            if (!warpControllerPresent) return WarpUnavailableReason;
            if (warpLocked) return WarpLockedReason;
            return null;
        }

        /// <summary>
        /// True once the clock has reached (or passed) the target within tolerance. The
        /// latch is ONE-SIDED for <c>TestCommandTimeJump.DecideJumpCompletion</c>'s
        /// reason verbatim: the clock keeps advancing while warp winds down, so a
        /// two-sided window would go false the instant it overshot and never re-satisfy.
        /// </summary>
        internal static bool HasReached(double currentUT, double targetUT, double toleranceSeconds)
            => currentUT >= targetUT - toleranceSeconds;

        /// <summary>
        /// Select the rails rate INDEX to request this frame.
        ///
        /// <para>Highest index <c>i</c> in <c>[0, maxAllowedIndex]</c> such that
        /// <c>warpRates[i]</c> is within the optional cap AND at least
        /// <see cref="MinRealSecondsAtRate"/> real seconds of running remain at that rate
        /// (<c>warpRates[i] * MinRealSecondsAtRate &lt;= remainingSeconds</c>). Index 0
        /// (1x) is the floor and is returned when nothing qualifies, when the ladder is
        /// missing, or when the target is already reached
        /// (<c>remainingSeconds &lt;= 0</c>).</para>
        ///
        /// <para><paramref name="maxAllowedIndex"/> is the ceiling the caller read from
        /// stock (<c>TimeWarp.GetMaxRateForAltitude</c>, lifted for a landed vessel and
        /// for no-active-vessel); it is clamped into the ladder's own bounds here so a
        /// stale or out-of-range ceiling can never index past the array.
        /// <paramref name="maxRateCap"/> at or below <see cref="UncappedMaxRate"/> means
        /// uncapped.</para>
        /// </summary>
        internal static int SelectRateIndex(
            double remainingSeconds, IList<float> warpRates, int maxAllowedIndex, double maxRateCap)
        {
            if (warpRates == null || warpRates.Count == 0) return 0;
            if (double.IsNaN(remainingSeconds) || remainingSeconds <= 0.0) return 0;

            // ONLY the upper clamp, and its absence is what an out-of-range ceiling
            // would cost: without it a stale ceiling indexes past the array and throws.
            // There is deliberately NO lower clamp - a negative ceiling makes the
            // descending loop below run zero times and fall through to 0, which is the
            // same answer a clamp would produce, so a `if (ceiling < 0) ceiling = 0;`
            // would be a line no test could ever discriminate. (Mutation-tested by the
            // review panel, which found exactly that: the assertion pinning it passed
            // against code without it.)
            int ceiling = maxAllowedIndex;
            if (ceiling > warpRates.Count - 1) ceiling = warpRates.Count - 1;

            bool capped = maxRateCap > UncappedMaxRate;
            for (int i = ceiling; i > 0; i--)
            {
                double rate = warpRates[i];
                if (rate <= 0.0) continue;
                if (capped && rate > maxRateCap) continue;
                if (rate * MinRealSecondsAtRate <= remainingSeconds) return i;
            }
            return 0;
        }

        /// <summary>
        /// Decide the two-phase WarpToUT completion. Complete only when ALL THREE hold:
        /// the clock reached the target, warp is back at rate index 0, and the settle
        /// window drained. Requiring rate 0 in the SUCCESS condition (not merely in the
        /// applier's cleanup) is what makes "never leave the game warped" observable
        /// rather than assumed - an OK terminal is proof the rate came down. The budget
        /// expiry bounds a span the clamps make unreachable; the applier forces rate 0
        /// on that path too.
        /// </summary>
        internal static WarpCompletionDecision DecideWarpCompletion(
            double elapsedSeconds, double currentUT, double targetUT, double toleranceSeconds,
            int currentRateIndex, int settleFramesRemaining, double budgetSeconds)
        {
            if (HasReached(currentUT, targetUT, toleranceSeconds)
                && currentRateIndex <= 0
                && settleFramesRemaining <= 0)
                return WarpCompletionDecision.CompleteOk;
            if (elapsedSeconds >= budgetSeconds)
                return WarpCompletionDecision.WarpTimeout;
            return WarpCompletionDecision.StillWaiting;
        }

        /// <summary>
        /// Terminal completion payload. <c>ut</c> = the reached UT, <c>target</c> = the
        /// captured target, <c>delta</c> = <c>target - startUT</c> (the span actually
        /// simulated), <c>maxRate</c> = the highest rate the warp actually ran at (1 when
        /// every request was clamped to 1x, which is how a reader tells a real warp from
        /// a real-time wait). All InvariantCulture round-trip ("R") so no locale comma can
        /// leak into a UT.
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(
            double reachedUT, double targetUT, double startUT, double maxObservedRate)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("ut", reachedUT.ToString("R", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("target", targetUT.ToString("R", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("delta", (targetUT - startUT).ToString("R", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("maxRate", maxObservedRate.ToString("R", CultureInfo.InvariantCulture)),
            };
    }
}
