// STUB: replaced by the capture engine's file at merge.
using System;
using System.Collections;
using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>The outcome of one <c>PartParitySweep</c> step, handed to the applier's
    /// callback: <see cref="Ok"/> with the OK <see cref="Payload"/>
    /// (<c>TestCommandPartParitySweep.BuildPayload</c>), or a failure
    /// <see cref="Reason"/> token with an optional <see cref="Detail"/>.</summary>
    internal sealed class PartParitySweepStepResult
    {
        public bool Ok;
        public string Reason;
        public string Detail;
        public List<KeyValuePair<string, string>> Payload;
    }

    /// <summary>
    /// The capture engine's entry point for one sweep step (design-part-ghost-parity.md
    /// section 4). This file is a stub: <see cref="RunStep"/> answers
    /// <c>not-implemented</c> at once. The capture engine's own file replaces it.
    /// </summary>
    internal static class PartParitySweepRunner
    {
        internal static IEnumerator RunStep(PartParitySweepArgs args, Action<PartParitySweepStepResult> onDone)
        {
            onDone?.Invoke(new PartParitySweepStepResult
            {
                Ok = false,
                Reason = "not-implemented",
            });
            yield break;
        }
    }
}
