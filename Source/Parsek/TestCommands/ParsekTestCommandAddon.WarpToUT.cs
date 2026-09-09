// ParsekTestCommandAddon partial: the WarpToUT executor body (RF-12 / re-fly phase 4).
// =====================================================================================
// Lane A (thin Unity applier) for the REAL rails warp. Every decision - target resolve,
// the forward gate, the feasibility gate, the rate-ladder selection, the completion
// verdict, the payload - is delegated to the pure sibling TestCommandWarpToUT; this file
// only samples live KSP state (Planetarium clock, TimeWarp rates, the active vessel's
// altitude) and applies TimeWarp.SetRate.
//
// THE CONTRAST THAT MADE THIS VERB NECESSARY: TimeJump (the sibling partial) is an EPOCH
// SHIFT - the clock moves, the vessel does not travel. This verb advances the clock by
// SIMULATING, so a descending vessel actually re-enters and impacts. RF-12's reading run
// measured the gap and filed it as RF12-NO-SEAM-PATH-CONCLUDES-A-REFLY-IN-FLIGHT.
//
// THE WARP IS ALWAYS LOWERED. Three paths reach a terminal - CompleteOk, WarpTimeout, and
// an exception - and all three force rate index 0 before emitting. The success condition
// itself requires rate 0 (TestCommandWarpToUT.DecideWarpCompletion), so an OK terminal is
// PROOF the rate came down rather than an assumption about it.
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    public partial class ParsekTestCommandAddon
    {
        // WarpToUT two-phase state: the captured absolute target + start UT (echoed in the
        // terminal payload), the optional rate cap, the highest rate actually observed
        // (so the payload can distinguish a real warp from a fully-clamped real-time
        // wait), the last rate index this verb REQUESTED (so the applier logs only on a
        // change rather than every poll frame), and the post-landing settle countdown.
        private double warpTargetUt;
        private double warpStartUt;
        private double warpMaxRateCap;
        private double warpMaxObservedRate;
        private int warpLastRequestedIndex;
        private int warpSettleFramesRemaining;

        // ----- WarpToUT (two-phase, forward-only, REAL rails warp) -----
        // Resolves the absolute forward target (ut=), the optional maxRate= cap, refuses a
        // backward / malformed target and an undriveable warp, then walks the stock rails
        // rate ladder toward the target and holds the FIFO head until the clock lands AND
        // warp is back at 1x. Dispatch already guaranteed FLIGHT.
        private void WarpToUTImpl(ParsedCommand cmd)
        {
            string utArg = ArgOrNull(cmd, "ut");
            string maxRateArg = ArgOrNull(cmd, "maxRate");
            double nowUt = SafeUniversalTime();

            double target = TestCommandWarpToUT.ResolveTargetUt(utArg, out string targetError);
            if (targetError != null)
            {
                ParsekLog.Warn(Tag, $"warptout refused reason={targetError} ut={utArg ?? "<none>"}");
                SetExecResult("REJECTED", null, targetError);
                return;
            }

            double cap = TestCommandWarpToUT.ResolveMaxRate(maxRateArg, out string capError);
            if (capError != null)
            {
                ParsekLog.Warn(Tag, $"warptout refused reason={capError} maxRate={maxRateArg ?? "<none>"}");
                SetExecResult("REJECTED", null, capError);
                return;
            }

            if (!TestCommandWarpToUT.IsForwardWarp(nowUt, target))
            {
                ParsekLog.Warn(Tag,
                    $"warptout refused reason={TestCommandWarpToUT.BackwardWarpReason} "
                    + $"now={Inv(nowUt)} ut={Inv(target)}");
                SetExecResult("REJECTED", null, TestCommandWarpToUT.BackwardWarpReason);
                return;
            }

            string feasibility = TestCommandWarpToUT.EvaluateFeasibility(
                SafeWarpControllerPresent(), SafeTimeWarpLocked());
            if (feasibility != null)
            {
                ParsekLog.Warn(Tag, $"warptout refused reason={feasibility} ut={Inv(target)}");
                SetExecResult("REJECTED", null, feasibility);
                return;
            }

            warpTargetUt = target;
            warpStartUt = nowUt;
            warpMaxRateCap = cap;
            warpMaxObservedRate = SafeCurrentWarpRate();
            warpLastRequestedIndex = -1;
            warpSettleFramesRemaining = TestCommandWarpToUT.SettleFrames;

            ParsekLog.Info(Tag,
                $"warptout start ut={Inv(target)} delta={Inv(target - nowUt)}s "
                + $"maxRate={Inv(cap)} rate={Inv(warpMaxObservedRate)} "
                + $"ceilingIndex={SafeMaxRateIndexForActiveVessel().ToString(CultureInfo.InvariantCulture)}");

            // ARM THE TWO-PHASE FIRST, THEN RAISE THE WARP. The order is load-bearing and
            // it is the fourth exit the file header's "always lowered" claim has to cover:
            // if the first ladder step throws BEFORE the pending verdict is set, the pump's
            // ExecuteHead catch emits a terminal ERROR, `completionVerb` is never
            // "WarpToUT", TryCompleteWarpToUT never runs - and the game is left warped with
            // nothing left to lower it. Setting the result first means any throw here is
            // caught by TryCompleteTwoPhase's handler instead, which routes through this
            // verb's own catch and force-lowers. The step still happens on THIS frame, so
            // the warp starts no later than it did before.
            SetExecResult(PendingVerdict, null, null);
            ApplyWarpLadderStep(nowUt);
        }

        private void TryCompleteWarpToUT(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds("WarpToUT");
            double currentUt;
            int currentRateIndex;

            try
            {
                currentUt = SafeUniversalTime();
                currentRateIndex = SafeCurrentWarpRateIndex();

                double rate = SafeCurrentWarpRate();
                if (rate > warpMaxObservedRate) warpMaxObservedRate = rate;

                if (TestCommandWarpToUT.HasReached(
                        currentUt, warpTargetUt, TestCommandWarpToUT.UtToleranceSeconds))
                {
                    // Reached: force the rate down (the ladder already walks itself down on
                    // approach, but a clamp change or a stock auto-drop can leave a residue)
                    // and only then drain the settle window.
                    if (currentRateIndex > 0)
                    {
                        ForceWarpToRealTime("target-reached");
                        currentRateIndex = SafeCurrentWarpRateIndex();
                    }
                    else if (warpSettleFramesRemaining > 0)
                    {
                        warpSettleFramesRemaining--;
                    }
                }
                else
                {
                    ApplyWarpLadderStep(currentUt);
                    // A STANDING condition whose producer runs on a poll, which is exactly
                    // what InfoRateLimited is for (CLAUDE.md's logging rules). The ladder
                    // line above prints only when the REQUESTED rung CHANGES, so a warp
                    // that stock has clamped to 1x for minutes - the atmospheric-descent
                    // case this verb exists to drive - would otherwise be completely
                    // silent between `warptout start` and its terminal, and a reader could
                    // not tell a working slow warp from a wedged one. The rate KEY is the
                    // index sampled at the TOP of this poll frame (before the ladder step
                    // below may move it), so a changed clamp prints on the next frame
                    // rather than this one, and `rate=` can be one step fresher than
                    // `rateIndex=` on the line that reports the change.
                    ParsekLog.InfoRateLimited(Tag,
                        "warptout-progress-" + currentRateIndex.ToString(CultureInfo.InvariantCulture),
                        $"warptout progress ut={Inv(warpTargetUt)} reachedUT={Inv(currentUt)} "
                        + $"remaining={Inv(warpTargetUt - currentUt)}s "
                        + $"rate={Inv(SafeCurrentWarpRate())} "
                        + $"rateIndex={currentRateIndex.ToString(CultureInfo.InvariantCulture)} "
                        + $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
                }
            }
            catch (Exception)
            {
                // Never leave the game warped because a sample threw. The outer
                // TryCompleteTwoPhase catch turns this into an ERROR terminal.
                ForceWarpToRealTime("completion-threw");
                throw;
            }

            WarpCompletionDecision decision = TestCommandWarpToUT.DecideWarpCompletion(
                elapsed, currentUt, warpTargetUt, TestCommandWarpToUT.UtToleranceSeconds,
                currentRateIndex, warpSettleFramesRemaining, budget);
            if (decision == WarpCompletionDecision.StillWaiting)
                return;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            double target = warpTargetUt; double start = warpStartUt;
            double maxObserved = warpMaxObservedRate;
            ClearTwoPhase();

            if (decision == WarpCompletionDecision.CompleteOk)
            {
                List<KeyValuePair<string, string>> payload =
                    TestCommandWarpToUT.BuildCompletePayload(currentUt, target, start, maxObserved);
                ParsekLog.Info(Tag,
                    $"warptout complete reachedUT={Inv(currentUt)} ut={Inv(target)} "
                    + $"rate={Inv(SafeCurrentWarpRate())} maxRate={Inv(maxObserved)} "
                    + $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
                EmitExecutedTerminal(id, seq, verb, "OK", payload, null, dequeueHead: true);
            }
            else // WarpTimeout
            {
                // Lower the warp BEFORE the terminal: a run that classifies INVALID off this
                // ERROR must not leave a warped game behind for whatever runs next.
                ForceWarpToRealTime(TestCommandWarpToUT.WarpTimeoutReason);
                TestCommandDiagnostics.Timeout(id, verb, elapsed, TestCommandWarpToUT.WarpTimeoutReason);
                ParsekLog.Error(Tag,
                    $"warptout timeout reason={TestCommandWarpToUT.WarpTimeoutReason} "
                    + $"ut={Inv(target)} reachedUT={Inv(currentUt)} maxRate={Inv(maxObserved)} "
                    + $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    TestCommandWarpToUT.WarpTimeoutReason, dequeueHead: true);
            }
        }

        /// <summary>
        /// One ladder step: pick the rate index the pure selector wants for the remaining
        /// span, request it from stock when it differs from the last request, then READ
        /// BACK what stock actually applied. Stock owns the real clamps (altitude limit,
        /// acceleration, SOI guard) and applies them inside SetRate, so a requested !=
        /// applied pair is logged as a clamp rather than treated as a failure.
        /// </summary>
        private void ApplyWarpLadderStep(double currentUt)
        {
            double remaining = warpTargetUt - currentUt;
            int ceiling = SafeMaxRateIndexForActiveVessel();
            int desired = TestCommandWarpToUT.SelectRateIndex(
                remaining, SafeWarpRates(), ceiling, warpMaxRateCap);

            // DE-DUP ON BOTH THE REQUEST AND WHAT STOCK IS ACTUALLY AT. De-duping on the
            // request alone WEDGES: once stock clamps a rung away (under acceleration, a
            // near SOI transition, an altitude drop), nothing re-asserts until `desired`
            // itself moves, and `desired` only moves when the ceiling changes or the
            // remaining span crosses a rung boundary. On a long span with `desired` pinned
            // high that can be the WHOLE budget, turning a warp stock would have allowed a
            // minute later into a `warp-timeout` ERROR. The green log shows the drift is
            // real: after `requested=3` stock moved the live index to 2 and then 1 with no
            // re-request line in between.
            if (desired == warpLastRequestedIndex && SafeCurrentWarpRateIndex() == desired)
                return;
            warpLastRequestedIndex = desired;

            SafeSetWarpRate(desired, instant: desired == 0);
            int applied = SafeCurrentWarpRateIndex();
            double appliedRate = SafeCurrentWarpRate();
            if (appliedRate > warpMaxObservedRate) warpMaxObservedRate = appliedRate;

            // `mode=` is not decoration: the two ladders share an index space with wholly
            // different values, so a `rate=` that disagrees with the rung is only readable
            // once the line says which array answered.
            ParsekLog.Info(Tag,
                $"warptout rate requested={desired.ToString(CultureInfo.InvariantCulture)} "
                + $"applied={applied.ToString(CultureInfo.InvariantCulture)} "
                + $"rate={Inv(appliedRate)} mode={SafeWarpModeName()} "
                + $"ceilingIndex={ceiling.ToString(CultureInfo.InvariantCulture)} "
                + $"clamped={(applied < desired ? "true" : "false")} "
                + $"remaining={Inv(remaining)}s ut={Inv(warpTargetUt)}");
        }

        /// <summary>Force rate index 0 instantly and log the reason. Idempotent.</summary>
        private void ForceWarpToRealTime(string reason)
        {
            int before = SafeCurrentWarpRateIndex();
            if (before <= 0) return;
            SafeSetWarpRate(0, instant: true);
            warpLastRequestedIndex = 0;
            ParsekLog.Info(Tag,
                $"warptout dewarp reason={reason} "
                + $"fromIndex={before.ToString(CultureInfo.InvariantCulture)} "
                + $"rate={Inv(SafeCurrentWarpRate())}");
        }

        // ----- Null-safe live-state samples (dispatch guaranteed FLIGHT, but stay safe) -----

        private static string Inv(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>The live warp MODE name (`HIGH` rails / `LOW` physics), or `unknown`
        /// when it cannot be read. Logged beside every rate change so a reader can tell
        /// which of the two index spaces a rung belongs to.</summary>
        private static string SafeWarpModeName()
        {
            try { return TimeWarp.WarpMode.ToString(); }
            catch (Exception) { return "unknown"; }
        }

        private static bool SafeWarpControllerPresent()
        {
            try { return TimeWarp.fetch != null; }
            catch (Exception) { return false; }
        }

        private static bool SafeTimeWarpLocked()
        {
            try { return InputLockManager.IsLocked(ControlTypes.TIMEWARP); }
            catch (Exception) { return false; }
        }

        private static float SafeCurrentWarpRate()
        {
            try { return TimeWarp.CurrentRate; }
            catch (Exception) { return 1f; }
        }

        /// <summary>
        /// The live rails/physics rate INDEX, or 0 when there is no warp controller.
        ///
        /// <para>The explicit null-fetch guard is load-bearing and IL-verified:
        /// <c>TimeWarp::get_CurrentRateIndex</c> returns <b>1</b> on its <c>!fetch</c>
        /// branch (someone copied <c>CurrentRate</c>'s <c>1f</c> literal), so a scene
        /// teardown while a WarpToUT is PENDING would leave this reporting 1 forever.
        /// Two consequences, both bad: <see cref="TestCommandWarpToUT.DecideWarpCompletion"/>
        /// could never return CompleteOk (it requires index 0), so the verb would hold the
        /// FIFO head to its whole budget and then ERROR; and
        /// <see cref="ForceWarpToRealTime"/>'s early-out would never trip, so its Info line
        /// would fire EVERY poll frame against a stock call that is a no-op with a null
        /// fetch - tens of thousands of lines, which is exactly what the batch-counting
        /// convention exists to prevent.</para>
        /// </summary>
        private static int SafeCurrentWarpRateIndex()
        {
            try
            {
                if (TimeWarp.fetch == null) return 0;
                return TimeWarp.CurrentRateIndex;
            }
            catch (Exception) { return 0; }
        }

        private static void SafeSetWarpRate(int rateIndex, bool instant)
        {
            try { TimeWarp.SetRate(rateIndex, instant); }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"warptout SetRate({rateIndex.ToString(CultureInfo.InvariantCulture)}, "
                    + $"instant={(instant ? "true" : "false")}) failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The rate ladder stock is ACTUALLY on, which is not always the rails one.
        ///
        /// <para>MEASURED on RF-12W's reading run 2: the log read
        /// <c>rateIndex=3 rate=4</c> while a re-flown craft was inside the atmosphere.
        /// Rails index 3 is 50x, so a mismatch that large is not a lerp - stock had put
        /// the game in PHYSICS warp (<c>TimeWarp.Modes.LOW</c>), where index 3 is 4x on
        /// the separate <c>physicsWarpRates</c> array. Selecting against
        /// <c>warpRates</c> in that state is not WRONG in outcome (the applier reads back
        /// what stock applied, the log reports the real rate, and the ladder still steps
        /// down by index), but it models the wrong ladder: the selector would believe a
        /// rung buys 50x when it buys 4x, and would step down at the wrong distance from
        /// the target. Reading the mode each frame fixes the model.</para>
        ///
        /// <para>The mode is re-read per call rather than captured, because stock
        /// switches between the two ladders on its own as the vessel climbs out of the
        /// atmosphere (<c>maxModeSwitchRate_index</c>).</para>
        /// </summary>
        private static IList<float> SafeWarpRates()
        {
            try
            {
                TimeWarp tw = TimeWarp.fetch;
                if (tw == null) return null;
                return TimeWarp.WarpMode == TimeWarp.Modes.LOW
                    ? tw.physicsWarpRates
                    : tw.warpRates;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// The rate-index ceiling for the current warp mode.
        ///
        /// <para>In PHYSICS warp (<c>TimeWarp.Modes.LOW</c>) the ceiling is stock's own
        /// <c>maxPhysicsRate_index</c>: the altitude limits are a RAILS concept and do not
        /// apply to the physics ladder, so reading <c>GetMaxRateForAltitude</c> there
        /// would answer against the wrong array (see <see cref="SafeWarpRates"/> for the
        /// measurement that surfaced the pair).</para>
        ///
        /// <para>In RAILS warp it is what stock publishes for the ACTIVE vessel's current
        /// altitude. A landed / splashed vessel has no altitude ceiling (stock allows the
        /// full ladder on the pad), and with NO active vessel there is no per-vessel clamp
        /// to read, so both lift the ceiling to the top of the ladder and let stock's own
        /// SetRate be the authority - the read-back in ApplyWarpLadderStep is what records
        /// the truth.</para>
        /// </summary>
        private static int SafeMaxRateIndexForActiveVessel()
        {
            try
            {
                TimeWarp tw = TimeWarp.fetch;
                if (tw == null) return 0;

                if (TimeWarp.WarpMode == TimeWarp.Modes.LOW)
                {
                    // THE WHOLE PHYSICS LADDER, and `maxPhysicsRate_index` is deliberately
                    // NOT consulted. It reads like the physics ceiling and is not: every
                    // stock use site indexes the RAILS array with it - `MaxPhysicsRate =>
                    // fetch.warpRates[fetch.maxPhysicsRate_index]`, `setRate`'s
                    // `if (Mode == Modes.HIGH) { if (rateIdx > maxPhysicsRate_index) ... }`,
                    // `if (curr_rate > warpRates[maxPhysicsRate_index])`, and all four
                    // clamp screen-messages. Stock's actual LOW ceiling is the tail of
                    // `setRate`: `Mathf.Clamp(num, 0, physicsWarpRates.Length - 1)`.
                    //
                    // Using it here was a REGRESSION with a measurement behind it: on
                    // RF-12W's `2026-09-09_2157` log the atmospheric descent ran at physics
                    // 4x (`rate=4 rateIndex=3` sustained, terminal `maxRate=4`); after the
                    // first cut of this branch, `2026-09-09_2224` sat at 1x for 40 s of the
                    // same descent. `maxPhysicsRate_index` is a HIGH-mode gate, so reading
                    // it here answered 0 and pinned the ladder to real time.
                    if (tw.physicsWarpRates == null || tw.physicsWarpRates.Length == 0) return 0;
                    return tw.physicsWarpRates.Length - 1;
                }

                if (tw.warpRates == null || tw.warpRates.Length == 0) return 0;
                int top = tw.warpRates.Length - 1;

                Vessel v = FlightGlobals.ActiveVessel;
                if (v == null) return top;
                if (v.LandedOrSplashed) return top;
                if (v.mainBody == null) return top;
                return tw.GetMaxRateForAltitude(v.altitude, v.mainBody);
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
