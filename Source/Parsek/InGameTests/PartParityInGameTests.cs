using System.Collections;
using System.Collections.Generic;
using Parsek.TestCommands;

namespace Parsek.InGameTests
{
    /// <summary>
    /// The Ctrl+Shift+T entry of the part parity sweep (design-part-ghost-parity.md 7, 11):
    /// one sweep step over a small default set (the first five stock parts) plus the eight
    /// controls, label <c>ingame</c>, through the same <see cref="PartParitySweepRunner.RunStep"/> the
    /// <c>PartParitySweep</c> seam verb drives. Files land in
    /// <c>&lt;KSP root&gt;/parsek-partparity/ingame/</c>; the directory is reset first, so
    /// every run starts clean. Single-run only: it spawns and removes vessels around the
    /// active one, which must be in a stable orbit.
    /// </summary>
    public class PartParityInGameTests
    {
        private readonly InGameTestRunner runner;
        public PartParityInGameTests(InGameTestRunner runner) { this.runner = runner; }

        internal const string Label = "ingame";

        /// <summary>The small default set: the first parts of the stable order whose models
        /// come from <c>Squad</c> (stock), so the set is the same on every install.</summary>
        internal const string DefaultSource = "Squad";
        internal const int DefaultLimit = 5;

        [InGameTest(Category = "PartParity", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            BatchSkipReason = "Isolated-run only - spawns and removes one-part vessels around the active vessel; needs a stable orbit.",
            Description = "Part ghost parity: the 8 controls + 5 stock parts photographed real vs ghost into parsek-partparity/ingame/")]
        public IEnumerator SweepSmallDefaultSet()
        {
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null)
                InGameAssert.Skip("no active vessel");
            if (active.situation != Vessel.Situations.ORBITING)
                InGameAssert.Skip("the sweep spawns co-orbital and needs an orbiting active vessel (situation=" + active.situation + ")");

            PartParitySweepStepResult reset = null;
            IEnumerator r = PartParitySweepRunner.RunStep(Args(PartParitySweepOp.Reset), res => reset = res);
            while (r.MoveNext()) yield return r.Current;
            InGameAssert.IsTrue(reset != null && reset.Ok, "reset step failed: " + Describe(reset));

            PartParitySweepStepResult step = null;
            IEnumerator run = PartParitySweepRunner.RunStep(Args(PartParitySweepOp.Run), x => step = x);
            while (run.MoveNext()) yield return run.Current;
            InGameAssert.IsTrue(step != null && step.Ok, "run step failed: " + Describe(step));
            string payload = TestCommandPartParitySweep.FormatPayload(step.Payload);
            ParsekLog.Info(PartParitySweepRunner.Tag, "ingame sweep: " + payload);
            InGameAssert.IsTrue(Value(step, "done") == "true", "the step did not finish inside its time box: " + payload);
            InGameAssert.IsTrue(Value(step, "controls") == TestCommandPartParitySweep.ControlsOk,
                "controls did not behave (" + payload + "); read controls[] in parsek-partparity/" + Label + "/manifest.json");
        }

        private static PartParitySweepArgs Args(PartParitySweepOp op)
        {
            return new PartParitySweepArgs
            {
                Label = Label,
                Op = op,
                Sources = new List<string> { DefaultSource },
                Limit = DefaultLimit,
                Controls = true,
                MaxSeconds = TestCommandPartParitySweep.MaxMaxSeconds,
            };
        }

        private static string Value(PartParitySweepStepResult res, string key)
        {
            if (res?.Payload == null) return null;
            foreach (var kv in res.Payload) if (kv.Key == key) return kv.Value;
            return null;
        }

        private static string Describe(PartParitySweepStepResult res)
            => res == null ? "<no result>" : "ok=" + res.Ok + " reason=" + (res.Reason ?? "<none>") + " detail=" + (res.Detail ?? "<none>");
    }
}
