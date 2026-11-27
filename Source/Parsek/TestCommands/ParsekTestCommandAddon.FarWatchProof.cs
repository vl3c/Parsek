using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin applier for the THROWAWAY two-phase <c>FarWatchProof</c> verb (never merged;
    /// docs/dev/design-far-watch.md section 18.1). It parses the args, starts one op on
    /// <see cref="FarWatchProofDriver"/> and answers OK with the op's <c>result=</c> once the
    /// driver reports it done. A failed precondition is a <c>result=</c> value, not a refusal,
    /// so one missed step never stops the rest of the run from being measured.
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private string farWatchProofOp;

        private void FarWatchProofImpl(ParsedCommand cmd)
        {
            farWatchProofOp = null;
            string reason = TestCommandFarWatchProof.TryParseArgs(cmd.Args, out TestCommandFarWatchProof.Args args);
            if (reason != null)
            {
                ParsekLog.Warn(Tag, "farwatchproof rejected reason=" + reason);
                SetExecResult("REJECTED", null, reason);
                return;
            }
            FarWatchProofDriver driver = FarWatchProofDriver.Ensure();
            string busy = driver.Begin(args);
            if (busy != null)
            {
                SetExecResult("OK", new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("op", TestCommandFarWatchProof.OpToken(args.Op)),
                    new KeyValuePair<string, string>("result", busy),
                }, null);
                return;
            }
            farWatchProofOp = TestCommandFarWatchProof.OpToken(args.Op);
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteFarWatchProof(double now)
        {
            FarWatchProofDriver driver = FarWatchProofDriver.Instance;
            if (driver != null && !driver.IsOpDone)
            {
                if (now - completionStartedAt < TestCommandFarWatchProof.BudgetSeconds - 10.0) return;
                driver.Abort("budget");
            }
            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            var payload = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("op", farWatchProofOp ?? "-"),
                new KeyValuePair<string, string>("result", driver != null ? driver.OpResult : "driver-gone"),
            };
            if (driver != null && driver.OpPayload != null) payload.AddRange(driver.OpPayload);
            ClearTwoPhase();
            EmitExecutedTerminal(id, seq, verb, "OK", payload, null, dequeueHead: true);
        }

        private void ResetFarWatchProofState()
        {
            if (farWatchProofOp != null && FarWatchProofDriver.Instance != null)
                FarWatchProofDriver.Instance.Abort("two-phase-clear");
            farWatchProofOp = null;
        }
    }
}
