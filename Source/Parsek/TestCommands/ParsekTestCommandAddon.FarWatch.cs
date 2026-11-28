// [ERS-exempt] The FarWatch seam verb reads RecordingStore.CommittedRecordings to resolve the
// watched ghost by COMMITTED-LIST index (the index the playback engine keys its ghost slots by
// and FarWatchController.TryStart takes), the EnterWatchMode rationale; see
// scripts/ers-els-audit-allowlist.txt.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin applier for the <c>FarWatch</c> verb; the contract is on
    /// <see cref="TestCommandFarWatch"/>. Start and stop are two-phase: start answers once the
    /// controller reaches Following (ERROR <c>far-watch-ended:&lt;reason&gt;</c> when it fell
    /// back to Idle first), stop once it is Idle again. Status answers at once.
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // Re-armed at the start of every FarWatchImpl, so a stale value is never read.
        private FarWatchOp farWatchPendingOp;
        private bool farWatchPendingFollow;
        private int farWatchPendingIndex = -1;

        private void FarWatchImpl(ParsedCommand cmd)
        {
            farWatchPendingIndex = -1;
            string opArg = ArgOrNull(cmd, TestCommandFarWatch.OpKey);
            string indexArg = ArgOrNull(cmd, TestCommandFarWatch.IndexKey);
            string treeArg = ArgOrNull(cmd, TestCommandFarWatch.TreeKey);
            string modeArg = ArgOrNull(cmd, TestCommandFarWatch.ModeKey);
            string argReason = TestCommandFarWatch.TryParseArgs(opArg, indexArg, treeArg, modeArg,
                out TestCommandFarWatch.Args args);
            if (argReason != null)
            {
                RejectFarWatch(argReason, $"op={opArg ?? string.Empty} index={indexArg ?? string.Empty} "
                    + $"tree={treeArg ?? string.Empty} mode={modeArg ?? string.Empty}");
                return;
            }

            ParsekFlight flight = ParsekFlight.Instance;
            FarWatchController fw = flight != null ? flight.farWatch : null;
            if (fw == null)
            {
                ParsekLog.Warn(Tag, "farwatch error reason=" + TestCommandFarWatch.NoControllerReason);
                SetExecResult("ERROR", null, TestCommandFarWatch.NoControllerReason);
                return;
            }

            if (args.Op == FarWatchOp.Status)
            {
                List<KeyValuePair<string, string>> payload = BuildFarWatchPayload(fw);
                ParsekLog.Info(Tag, "farwatch status " + TestCommandFarWatch.FormatPayload(payload));
                SetExecResult("OK", payload, null);
                return;
            }

            if (args.Op == FarWatchOp.Stop)
            {
                if (!fw.IsRunning)
                {
                    RejectFarWatch(TestCommandFarWatch.NotRunningReason, "state=" + fw.State);
                    return;
                }
                ParsekLog.Info(Tag, "farwatch stop requested state=" + fw.State);
                farWatchPendingOp = FarWatchOp.Stop;
                fw.RequestEnd(FarWatchEndReason.SeamStop);
                SetExecResult(PendingVerdict, null, null);
                return;
            }

            if (fw.IsRunning || CameraDroneIdentity.Exists)
            {
                RejectFarWatch(TestCommandFarWatch.AlreadyRunningReason, "state=" + fw.State);
                return;
            }
            if (!TryResolveTreeHandleArg("farwatch", args.Tree, out string treeId))
                return;

            int index = args.Index;
            // [ERS-exempt] - committed-LIST indices, the same contract EnterWatchMode reads.
            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            int count = committed != null ? committed.Count : 0;
            if (index < 0)
            {
                RecordingTree scope = FindCommittedTreeById(treeId);
                if (scope == null)
                {
                    RejectFarWatch(TestCommandFarWatch.UnknownTreeReason, "tree=" + (treeId ?? string.Empty));
                    return;
                }
                var candidates = new List<TestCommandFarWatch.GhostCandidate>(count);
                for (int i = 0; i < count; i++)
                {
                    Recording rec = committed[i];
                    bool inScope = rec != null && !string.IsNullOrEmpty(rec.RecordingId)
                        && scope.Recordings != null && scope.Recordings.ContainsKey(rec.RecordingId);
                    candidates.Add(new TestCommandFarWatch.GhostCandidate
                    {
                        Index = i,
                        InScope = inScope,
                        HasActiveGhost = inScope && flight.HasActiveGhost(i),
                        OnShipBody = inScope && flight.IsGhostOnSameBody(i),
                    });
                }
                index = TestCommandFarWatch.ResolveTargetIndex(candidates, args.Follow);
                if (index < 0)
                {
                    RejectFarWatch(TestCommandFarWatch.NoGhostReason,
                        $"tree={treeId} committed={count} mode={(args.Follow ? "follow" : "hold")}");
                    return;
                }
            }
            else if (index >= count)
            {
                RejectFarWatch(TestCommandFarWatch.IndexInvalidReason,
                    $"index={index.ToString(CultureInfo.InvariantCulture)} committed={count.ToString(CultureInfo.InvariantCulture)}");
                return;
            }

            ParsekLog.Info(Tag, $"farwatch start index={index.ToString(CultureInfo.InvariantCulture)} "
                + $"mode={(args.Follow ? "follow" : "hold")} tree={treeId ?? string.Empty}");
            string refusal;
            if (!fw.TryStart(index, args.Follow, out refusal))
            {
                RejectFarWatch(refusal ?? "start-refused", "index=" + index.ToString(CultureInfo.InvariantCulture));
                return;
            }
            farWatchPendingOp = FarWatchOp.Start;
            farWatchPendingFollow = args.Follow;
            farWatchPendingIndex = index;
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteFarWatch(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandFarWatch.Verb);
            ParsekFlight flight = ParsekFlight.Instance;
            FarWatchController fw = flight != null ? flight.farWatch : null;
            FarWatchState state = fw != null ? fw.State : FarWatchState.Idle;
            FarWatchCompletionDecision decision = TestCommandFarWatch.DecideCompletion(farWatchPendingOp, state);
            if (decision == FarWatchCompletionDecision.KeepWaiting && elapsed < budget)
                return;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            FarWatchOp op = farWatchPendingOp;
            ClearTwoPhase();

            if (decision == FarWatchCompletionDecision.CompleteOk)
            {
                List<KeyValuePair<string, string>> payload = BuildFarWatchPayload(fw);
                ParsekLog.Info(Tag, $"farwatch {(op == FarWatchOp.Start ? "start" : "stop")} complete "
                    + TestCommandFarWatch.FormatPayload(payload)
                    + $" elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
                EmitExecutedTerminal(id, seq, verb, "OK", payload, null, dequeueHead: true);
                return;
            }

            if (decision == FarWatchCompletionDecision.CompleteEnded)
            {
                string ended = TestCommandFarWatch.EndedReason(fw != null ? fw.LastEndReason : FarWatchEndReason.None);
                ParsekLog.Error(Tag, "farwatch start error reason=" + ended
                    + $" index={farWatchPendingIndex.ToString(CultureInfo.InvariantCulture)}"
                    + $" mode={(farWatchPendingFollow ? "follow" : "hold")}"
                    + $" elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null, ended, dequeueHead: true);
                return;
            }

            string timeoutReason = TestCommandFarWatch.TimeoutReason;
            TestCommandDiagnostics.Timeout(id, verb, elapsed, timeoutReason);
            ParsekLog.Error(Tag, $"farwatch {(op == FarWatchOp.Start ? "start" : "stop")} timeout state={state} "
                + $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
            EmitExecutedTerminal(id, seq, verb, "ERROR", null, timeoutReason, dequeueHead: true);
        }

        private static List<KeyValuePair<string, string>> BuildFarWatchPayload(FarWatchController fw)
        {
            if (fw == null)
                return TestCommandFarWatch.BuildPayload(FarWatchState.Idle, 0, 0, -1, true, FarWatchEndReason.None);
            return TestCommandFarWatch.BuildPayload(fw.State, CameraDroneIdentity.DronePid, CameraDroneIdentity.ShipPid,
                fw.IsRunning ? fw.GhostIndex : -1, fw.IsFollowingGhost, fw.LastEndReason);
        }

        private void RejectFarWatch(string reason, string detail)
        {
            ParsekLog.Warn(Tag, $"farwatch rejected reason={reason} {detail}");
            SetExecResult("REJECTED", null, reason);
        }
    }
}
