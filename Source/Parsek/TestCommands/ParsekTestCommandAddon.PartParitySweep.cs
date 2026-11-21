using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Part ghost parity partial: the thin applier for the TWO-PHASE <c>PartParitySweep</c>
    /// verb (docs/dev/design-part-ghost-parity.md section 7). The arg parse, the refusals, the
    /// host admission and the OK payload are the pure <see cref="TestCommandPartParitySweep"/>;
    /// the poll decision and the ERROR vocabulary are the pure
    /// <see cref="PartParitySweepCompletion"/>; the capture itself is
    /// <see cref="PartParitySweepRunner.RunStep"/>. This file only parses, admits, starts the
    /// runner's coroutine and answers what it reports.
    ///
    /// <para>WHY A COROUTINE ON THIS ADDON. One step spawns, settles, renders and removes
    /// several waves of vessels over minutes of real time, frame by frame; the head is held
    /// for the whole step so no other seam verb runs under it. The addon is
    /// <c>DontDestroyOnLoad</c>, so the coroutine is never torn down under the runner.</para>
    ///
    /// <para>NO MUTABLE STATICS: the step's state lives in these instance fields and on the
    /// runner's coroutine. The arm id fences a superseded coroutine (an earlier step that
    /// timed out) from settling a later one.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private int partParityArmId;
        private string partParityLabel;
        private Coroutine partParityRoutine;
        private PartParitySweepStepResult partParityResult;
        private bool partParityRoutineEnded;

        private void PartParitySweepImpl(ParsedCommand cmd)
        {
            if (!TestCommandPartParitySweep.TryParseArgs(
                    cmd.Args, out PartParitySweepArgs args, out string reason, out string detail))
            {
                ParsekLog.Warn(Tag, $"partparitysweep rejected reason={reason} {detail}");
                SetExecResult("REJECTED", null, $"{reason} {detail}");
                return;
            }

            Vessel active = FlightGlobals.ActiveVessel;
            bool orbiting = active != null && active.situation == Vessel.Situations.ORBITING;
            string admission = TestCommandPartParitySweep.DecideHostAdmission(active != null, orbiting);
            if (admission != null)
            {
                string situation = active != null ? active.situation.ToString() : "(none)";
                ParsekLog.Warn(Tag, $"partparitysweep rejected reason={admission} label={args.Label} "
                    + $"situation={situation}");
                SetExecResult("REJECTED", null, $"{admission} situation={situation}");
                return;
            }

            partParityArmId++;
            partParityLabel = args.Label;
            partParityResult = null;
            partParityRoutineEnded = false;

            CultureInfo ic = CultureInfo.InvariantCulture;
            ParsekLog.Info(Tag, $"partparitysweep armed label={args.Label} op={args.Op} "
                + $"filter={args.Filter} limit={args.Limit.ToString(ic)} "
                + $"shard={args.ShardK.ToString(ic)}/{args.ShardN.ToString(ic)} "
                + $"controls={(args.Controls ? "true" : "false")} "
                + $"maxSeconds={args.MaxSeconds.ToString(ic)} host={active.vesselName} "
                + $"dir={TestCommandPartParitySweep.RelativeLabelDir(args.Label)}");
            partParityRoutine = StartCoroutine(PartParitySweepRoutine(partParityArmId, args));
            SetExecResult(PendingVerdict, null, null);
        }

        // Drives the runner's enumerator by hand so a throw inside it ends the step as a
        // failure at once rather than a silent stop the budget would only find 540 s later.
        private IEnumerator PartParitySweepRoutine(int armId, PartParitySweepArgs args)
        {
            IEnumerator inner;
            try
            {
                inner = PartParitySweepRunner.RunStep(args, result => OnPartParityStepDone(armId, result));
            }
            catch (Exception ex)
            {
                OnPartParityStepThrew(armId, ex);
                yield break;
            }

            while (true)
            {
                object current;
                try
                {
                    if (inner == null || !inner.MoveNext())
                        break;
                    current = inner.Current;
                }
                catch (Exception ex)
                {
                    OnPartParityStepThrew(armId, ex);
                    yield break;
                }
                yield return current;
            }

            if (armId == partParityArmId)
                partParityRoutineEnded = true;
        }

        private void OnPartParityStepDone(int armId, PartParitySweepStepResult result)
        {
            if (armId != partParityArmId || partParityResult != null)
                return;
            partParityResult = result ?? new PartParitySweepStepResult
            {
                Ok = false,
                Reason = PartParitySweepCompletion.NoResultFailure,
            };
        }

        private void OnPartParityStepThrew(int armId, Exception ex)
        {
            ParsekLog.Error(Tag, $"partparitysweep step threw label={partParityLabel}: "
                + $"{ex.GetType().Name}: {ex.Message}");
            if (armId != partParityArmId)
                return;
            if (partParityResult == null)
            {
                partParityResult = new PartParitySweepStepResult
                {
                    Ok = false,
                    Reason = "threw-" + ex.GetType().Name,
                };
            }
            partParityRoutineEnded = true;
        }

        private void TryCompletePartParitySweep(double now)
        {
            double budget = DeferralBudget.BudgetSeconds(TestCommandPartParitySweep.VerbName);
            bool expired = DeferralBudget.ShouldTimeout(completionStartedAt, now, budget);
            PartParitySweepStepResult result = partParityResult;
            PartParitySweepPollOutcome outcome = PartParitySweepCompletion.DecidePoll(
                result != null, result != null && result.Ok, partParityRoutineEnded, expired);
            if (outcome == PartParitySweepPollOutcome.NotYet)
                return;

            string id = completionId;
            long seq = completionSeq;
            string verb = completionVerb;
            string label = partParityLabel;
            ClearTwoPhase();

            CultureInfo ic = CultureInfo.InvariantCulture;
            double elapsed = now - completionStartedAt;
            string elapsedText = elapsed.ToString("F1", ic);

            if (outcome == PartParitySweepPollOutcome.TimedOut)
            {
                // Fence and stop the runner: a late report must not settle a later step.
                partParityArmId++;
                if (partParityRoutine != null)
                    StopCoroutine(partParityRoutine);
                partParityRoutine = null;
                TestCommandDiagnostics.Timeout(id, verb, elapsed, PartParitySweepCompletion.TimeoutReason);
                ParsekLog.Error(Tag, $"partparitysweep timed out label={label} "
                    + $"elapsed={elapsedText}s budget={budget.ToString("F0", ic)}s");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    PartParitySweepCompletion.TimeoutReason, dequeueHead: true);
                return;
            }

            partParityRoutine = null;
            if (outcome == PartParitySweepPollOutcome.Failed)
            {
                string reason = result != null ? result.Reason : null;
                string detail = result != null ? result.Detail : null;
                string msg = PartParitySweepCompletion.FormatStepFailedMessage(reason, detail);
                ParsekLog.Error(Tag, $"partparitysweep failed label={label} elapsed={elapsedText}s {msg}");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null, msg, dequeueHead: true);
                return;
            }

            List<KeyValuePair<string, string>> payload = PartParitySweepCompletion.OkPayload(result.Payload);
            ParsekLog.Info(Tag, PartParitySweepCompletion.FormatOkLine(
                label, TestCommandPartParitySweep.FormatPayload(payload), elapsed));
            EmitExecutedTerminal(id, seq, verb, "OK", payload, null, dequeueHead: true);
        }
    }
}
