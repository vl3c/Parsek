// Lane A addon-partial: TimeJump / KscAction executor wiring.
// =====================================================================================
// (File name is historical - it began as compile stubs before the sibling Lane B pure
// files landed in this worktree; it now holds the REAL addon executor bodies. The
// coordinator may rename it to ParsekTestCommandAddon.TimeJumpKscAction.cs.)
//
// The addon (ParsekTestCommandAddon) OWNS the four verb bodies (design's "four new verb
// bodies on ParsekTestCommandAddon"). InvokeRewind / AnswerMergeDialog live in the main
// addon file (Lane A). TimeJump / KscAction live here and delegate every decision to the
// sibling Lane B PURE surfaces (TestCommandTimeJump / TestCommandKscAction) and the
// ParsekFlight.TimeJumpTo epoch-shift wrapper, so the pure logic stays xUnit-covered and
// this file only samples live KSP state and stashes the verdict via SetExecResult / the
// PENDING sentinel.
// =====================================================================================
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.TestCommands
{
    public partial class ParsekTestCommandAddon
    {
        // TimeJump two-phase state (M-C1): the captured absolute target + start UT (echoed in
        // the terminal payload) and the settle-frame countdown that lets the engine playback
        // loop drain the spawn queue before the terminal OK.
        private double jumpTargetUt;
        private double jumpStartUt;
        private int jumpSettleFramesRemaining;

        // ----- TimeJump (two-phase, forward-only, epoch-shift) -----
        // Resolves the forward target (exactly one of ut / deltaSeconds via
        // TestCommandTimeJump.ResolveTargetUt), refuses backward / zero jumps, then drives
        // the real ParsekFlight.TimeJumpTo -> TimeJumpManager.ExecuteJump epoch-shift and
        // holds the FIFO head until the clock lands + the settle window drains. Dispatch
        // already guaranteed FLIGHT.
        private void TimeJumpImpl(ParsedCommand cmd)
        {
            string utArg = ArgOrNull(cmd, "ut");
            string deltaArg = ArgOrNull(cmd, "deltaSeconds");
            double nowUt = SafeUniversalTime();

            double target = TestCommandTimeJump.ResolveTargetUt(nowUt, utArg, deltaArg, out string error);
            if (error != null)
            {
                ParsekLog.Warn(Tag, "timejump refused reason=missing-jump-target");
                SetExecResult("REJECTED", null, "missing-jump-target");
                return;
            }
            if (!TestCommandTimeJump.IsForwardJump(nowUt, target))
            {
                ParsekLog.Warn(Tag, $"timejump refused reason=backward-jump now={nowUt.ToString("R", CultureInfo.InvariantCulture)} target={target.ToString("R", CultureInfo.InvariantCulture)}");
                SetExecResult("REJECTED", null, "backward-jump");
                return;
            }

            ParsekLog.Info(Tag,
                $"timejump start target={target.ToString("R", CultureInfo.InvariantCulture)} delta={(target - nowUt).ToString("R", CultureInfo.InvariantCulture)}s");

            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null)
            {
                ParsekLog.Warn(Tag, "timejump no-flight-instance");
                SetExecResult("ERROR", null, "no-flight-instance");
                return;
            }

            bool jumped = flight.TimeJumpTo(target);
            if (!jumped)
            {
                ParsekLog.Warn(Tag, "timejump refused (TimeJumpTo declined)");
                SetExecResult("ERROR", null, "jump-refused");
                return;
            }

            jumpTargetUt = target;
            jumpStartUt = nowUt;
            jumpSettleFramesRemaining = TestCommandTimeJump.SettleFrames;
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteTimeJump(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds("TimeJump");
            if (jumpSettleFramesRemaining > 0)
                jumpSettleFramesRemaining--;

            double currentUt = SafeUniversalTime();
            JumpCompletionDecision decision = TestCommandTimeJump.DecideJumpCompletion(
                elapsed, currentUt, jumpTargetUt, TestCommandTimeJump.UtToleranceSeconds,
                jumpSettleFramesRemaining, budget);
            if (decision == JumpCompletionDecision.StillWaiting)
                return;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            double target = jumpTargetUt; double start = jumpStartUt;
            ClearTwoPhase();

            if (decision == JumpCompletionDecision.CompleteOk)
            {
                List<KeyValuePair<string, string>> payload =
                    TestCommandTimeJump.BuildCompletePayload(currentUt, target, start);
                ParsekLog.Info(Tag,
                    $"timejump complete reachedUT={currentUt.ToString("R", CultureInfo.InvariantCulture)}");
                EmitExecutedTerminal(id, seq, verb, "OK", payload, null, dequeueHead: true);
            }
            else // JumpTimeout
            {
                TestCommandDiagnostics.Timeout(id, verb, elapsed, "jump-timeout");
                ParsekLog.Error(Tag,
                    $"timejump timeout elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null, "jump-timeout", dequeueHead: true);
            }
        }

        // ----- KscAction (sync, real stock action, ledger observes organically) -----
        // Fully delegated to the sibling Lane B TestCommandKscAction.Execute, which applies
        // the pure Decide gate, invokes the real stock API, and CONFIRMS the effect before
        // OK (a guard-blocked call is REJECTED blocked-committed, never a false OK). Dispatch
        // already applied the career / SPACECENTER sub-gate.
        private void KscActionImpl(ParsedCommand cmd)
        {
            string action = ArgOrNull(cmd, "action");
            string node = ArgOrNull(cmd, "node");
            string facility = ArgOrNull(cmd, "facility");
            string kerbal = ArgOrNull(cmd, "kerbal");

            TestCommandKscAction.KscActionExecOutcome outcome =
                TestCommandKscAction.Execute(action, node, facility, kerbal);
            SetExecResult(outcome.Verdict, outcome.Payload, outcome.Msg);
        }

        // FLIGHT-safe Planetarium read (dispatch guaranteed FLIGHT, but stay null-safe).
        private static double SafeUniversalTime()
        {
            if (HighLogic.CurrentGame == null) return 0.0;
            try { return Planetarium.GetUniversalTime(); }
            catch (System.Exception) { return 0.0; }
        }
    }
}
