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
            // Disposed on every exit (a cancelled test stops this coroutine), so the step's
            // cleanup runs.
            try { while (r.MoveNext()) yield return r.Current; }
            finally { PartParitySweepRunner.DisposeNested(r); }
            InGameAssert.IsTrue(reset != null && reset.Ok, "reset step failed: " + Describe(reset));

            PartParitySweepStepResult step = null;
            IEnumerator run = PartParitySweepRunner.RunStep(Args(PartParitySweepOp.Run), x => step = x);
            try { while (run.MoveNext()) yield return run.Current; }
            finally { PartParitySweepRunner.DisposeNested(run); }
            InGameAssert.IsTrue(step != null && step.Ok, "run step failed: " + Describe(step));
            string payload = TestCommandPartParitySweep.FormatPayload(step.Payload);
            ParsekLog.Info(PartParitySweepRunner.Tag, "ingame sweep: " + payload);
            InGameAssert.IsTrue(Value(step, "done") == "true", "the step did not finish inside its time box: " + payload);
            InGameAssert.IsTrue(Value(step, "controls") == TestCommandPartParitySweep.ControlsOk,
                "controls did not behave (" + payload + "); read controls[] in parsek-partparity/" + Label + "/manifest.json");
        }

        /// <summary>The state filter's acceptance parts (design D.10 S3): one of every orbit
        /// family, the stacked engine shroud and the top-node jet shroud.</summary>
        internal static readonly string[] StateAcceptanceParts =
        {
            "longAntenna", "solarPanels4", "GearSmall", "mk2CargoBayS", "telescopicLadder",
            "spotLight1", "mk1pod.v2", "airbrake1", "liquidEngine2", "turboFanSize2",
        };

        /// <summary>The label prefix of the state cell; each part runs under its own label
        /// (<c>ingame-state-&lt;n&gt;</c>), one step per label, since a filter names one glob.</summary>
        internal const string StateLabelPrefix = "ingame-state-";

        [InGameTest(Category = "PartParity", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            BatchSkipReason = "Isolated-run only - spawns, presses and removes one-part vessels around the active vessel; needs a stable orbit.",
            Description = "Part ghost parity, state filter: the acceptance parts at every orbit state (states=all) photographed on three ghosts into parsek-partparity/ingame-state-<n>/, then the FX (ingame-fx-<n>/) and motion (ingame-motion-<n>/) filters; from a pad host instead the burst (ingame-burst/) and dust (ingame-dust/) filters")]
        public IEnumerator SweepStateAcceptanceSet()
        {
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null)
                InGameAssert.Skip("no active vessel");
            if (PartParityAirContext.DecideAdmission(BurstArgs(PartParitySweepOp.Run), active, out string padDetail) == null)
            {
                // A pad host runs the burst and dust filters (design G.7): they need the pad, the
                // orbit filters an orbit, so one scene takes one half.
                ParsekLog.Info(PartParitySweepRunner.Tag, "ingame acceptance: pad host (" + padDetail + "), burst and dust filters only");
                IEnumerator pad = RunPadHostSteps();
                try { while (pad.MoveNext()) yield return pad.Current; }
                finally { PartParitySweepRunner.DisposeNested(pad); }
                yield break;
            }
            if (active.situation != Vessel.Situations.ORBITING)
                InGameAssert.Skip("the state sweep spawns co-orbital and needs an orbiting active vessel, or a pad host for the burst and dust filters (situation=" + active.situation + ")");

            for (int i = 0; i < StateAcceptanceParts.Length; i++)
            {
                string label = StateLabelPrefix + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                PartParitySweepArgs stateArgs = StateArgs(label, PartParitySweepOp.Reset);
                PartParitySweepStepResult reset = null;
                IEnumerator r = PartParitySweepRunner.RunStep(stateArgs, res => reset = res);
                try { while (r.MoveNext()) yield return r.Current; }
                finally { PartParitySweepRunner.DisposeNested(r); }
                InGameAssert.IsTrue(reset != null && reset.Ok, "state reset failed for " + label + ": " + Describe(reset));

                PartParitySweepStepResult step = null;
                IEnumerator run = PartParitySweepRunner.RunStep(StateArgs(label, PartParitySweepOp.Run, StateAcceptanceParts[i]),
                    x => step = x);
                try { while (run.MoveNext()) yield return run.Current; }
                finally { PartParitySweepRunner.DisposeNested(run); }
                InGameAssert.IsTrue(step != null && step.Ok, "state run failed for " + label + ": " + Describe(step));
                string payload = TestCommandPartParitySweep.FormatPayload(step.Payload);
                ParsekLog.Info(PartParitySweepRunner.Tag, "ingame state sweep " + StateAcceptanceParts[i] + ": " + payload);
                InGameAssert.IsTrue(Value(step, "done") == "true", "the state step did not finish inside its time box: " + payload);
                InGameAssert.IsTrue(Value(step, "setupFaults") == "0",
                    StateAcceptanceParts[i] + " has a state setup fault (" + payload + "); read parsek-partparity/" + label + "/manifest.json");
            }
            IEnumerator fx = RunFxSteps();
            try { while (fx.MoveNext()) yield return fx.Current; }
            finally { PartParitySweepRunner.DisposeNested(fx); }
            IEnumerator motion = RunMotionSteps();
            try { while (motion.MoveNext()) yield return motion.Current; }
            finally { PartParitySweepRunner.DisposeNested(motion); }
        }

        /// <summary>The motion filter's acceptance parts (design H.7 M3): one of every orbit
        /// motion kind (clip, servo, surface on its radial filler) and the families P6 judges
        /// only at their end state.</summary>
        internal static readonly string[] MotionAcceptanceParts =
        {
            "longAntenna", "solarPanels4", "GearSmall", "mk2CargoBayS", "telescopicLadder",
            "airbrake1", "hinge.01", "StandardCtrlSrf",
        };

        /// <summary>The label prefix of the motion steps; each part runs under its own label.</summary>
        internal const string MotionLabelPrefix = "ingame-motion-";

        /// <summary>One motion step's args: every <c>motion=</c> word on one part at the motion
        /// views and tile, the motion controls on the first label only.</summary>
        internal static PartParitySweepArgs MotionArgs(string label, PartParitySweepOp op, string filter = null)
        {
            return new PartParitySweepArgs
            {
                Label = label,
                Op = op,
                Filter = filter ?? TestCommandPartParitySweep.DefaultFilter,
                Motion = new List<string>(PartParityMotionRules.ArgWords),
                MotionAll = true,
                Views = new List<string>(PartParityMotionRules.DefaultViews),
                Tile = PartParityMotionRules.MotionTilePx,
                Controls = label == MotionLabelPrefix + "1",
                MaxSeconds = TestCommandPartParitySweep.MaxMaxSeconds,
            };
        }

        /// <summary>The motion half of the state acceptance cell (the plan's <c>motion=</c>
        /// filter, no new test): the acceptance parts through every motion family they have.</summary>
        private static IEnumerator RunMotionSteps()
        {
            for (int i = 0; i < MotionAcceptanceParts.Length; i++)
            {
                string label = MotionLabelPrefix + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                PartParitySweepStepResult reset = null;
                IEnumerator r = PartParitySweepRunner.RunStep(MotionArgs(label, PartParitySweepOp.Reset), res => reset = res);
                try { while (r.MoveNext()) yield return r.Current; }
                finally { PartParitySweepRunner.DisposeNested(r); }
                InGameAssert.IsTrue(reset != null && reset.Ok, "motion reset failed for " + label + ": " + Describe(reset));

                PartParitySweepStepResult step = null;
                IEnumerator run = PartParitySweepRunner.RunStep(MotionArgs(label, PartParitySweepOp.Run, MotionAcceptanceParts[i]), x => step = x);
                try { while (run.MoveNext()) yield return run.Current; }
                finally { PartParitySweepRunner.DisposeNested(run); }
                InGameAssert.IsTrue(step != null && step.Ok, "motion run failed for " + label + ": " + Describe(step));
                string payload = TestCommandPartParitySweep.FormatPayload(step.Payload);
                ParsekLog.Info(PartParitySweepRunner.Tag, "ingame motion sweep " + MotionAcceptanceParts[i] + ": " + payload);
                InGameAssert.IsTrue(Value(step, "done") == "true", "the motion step did not finish inside its time box: " + payload);
                InGameAssert.IsTrue(Value(step, "setupFaults") == "0",
                    MotionAcceptanceParts[i] + " has a motion setup fault (" + payload + "); read parsek-partparity/" + label + "/manifest.json");
                if (label == MotionLabelPrefix + "1")
                    InGameAssert.IsTrue(Value(step, "controls") == TestCommandPartParitySweep.ControlsOk,
                        "motion controls did not behave (" + payload + "); read parsek-partparity/" + label + "/manifest.json");
            }
        }

        /// <summary>The FX filter's acceptance parts (design E.9 F3): one of every FX family the
        /// orbit run plans; the RAPIER runs its closed cycle (its air mode is the air run's) and a
        /// point-light part is absent on both installs (E0 U9).</summary>
        internal static readonly string[] FxAcceptanceParts =
        {
            "liquidEngine2.v2", "solidBooster.sm.v2", "ionEngine", "RAPIER", "RCSBlock.v2",
            "vernierEngine", "standardNoseCone", "spotLight1",
        };

        /// <summary>The label prefix of the FX steps; each part runs under its own label.</summary>
        internal const string FxLabelPrefix = "ingame-fx-";

        /// <summary>One FX step's args: every <c>fx=</c> word on one part, the controls on the
        /// first label only.</summary>
        internal static PartParitySweepArgs FxArgs(string label, PartParitySweepOp op, string filter = null)
        {
            return new PartParitySweepArgs
            {
                Label = label,
                Op = op,
                Filter = filter ?? TestCommandPartParitySweep.DefaultFilter,
                Fx = new List<string>(PartParityFxRules.ArgWords),
                FxAll = true,
                Controls = label == FxLabelPrefix + "1",
                MaxSeconds = TestCommandPartParitySweep.MaxMaxSeconds,
            };
        }

        /// <summary>The FX half of the state acceptance cell (the plan's <c>fx=</c> filter, no new
        /// test): the acceptance parts through every FX family they have.</summary>
        private static IEnumerator RunFxSteps()
        {
            for (int i = 0; i < FxAcceptanceParts.Length; i++)
            {
                string label = FxLabelPrefix + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                PartParitySweepStepResult reset = null;
                IEnumerator r = PartParitySweepRunner.RunStep(FxArgs(label, PartParitySweepOp.Reset), res => reset = res);
                try { while (r.MoveNext()) yield return r.Current; }
                finally { PartParitySweepRunner.DisposeNested(r); }
                InGameAssert.IsTrue(reset != null && reset.Ok, "fx reset failed for " + label + ": " + Describe(reset));

                PartParitySweepStepResult step = null;
                IEnumerator run = PartParitySweepRunner.RunStep(FxArgs(label, PartParitySweepOp.Run, FxAcceptanceParts[i]), x => step = x);
                try { while (run.MoveNext()) yield return run.Current; }
                finally { PartParitySweepRunner.DisposeNested(run); }
                InGameAssert.IsTrue(step != null && step.Ok, "fx run failed for " + label + ": " + Describe(step));
                string payload = TestCommandPartParitySweep.FormatPayload(step.Payload);
                ParsekLog.Info(PartParitySweepRunner.Tag, "ingame fx sweep " + FxAcceptanceParts[i] + ": " + payload);
                InGameAssert.IsTrue(Value(step, "done") == "true", "the fx step did not finish inside its time box: " + payload);
                InGameAssert.IsTrue(Value(step, "setupFaults") == "0",
                    FxAcceptanceParts[i] + " has an fx setup fault (" + payload + "); read parsek-partparity/" + label + "/manifest.json");
                if (label == FxLabelPrefix + "1")
                    InGameAssert.IsTrue(Value(step, "controls") == TestCommandPartParitySweep.ControlsOk,
                        "fx controls did not behave (" + payload + "); read parsek-partparity/" + label + "/manifest.json");
            }
        }

        /// <summary>The label of the burst steps (every burst word over the install, contexts=air).</summary>
        internal const string BurstLabel = "ingame-burst";

        /// <summary>The label of the dust steps (fx=dust over the install, contexts=ground).</summary>
        internal const string DustLabel = "ingame-dust";

        /// <summary>How many run steps one pad label may take before the cell gives up: the burst
        /// run is one wave per case and the dust run four cases per wave, both resumed by label.</summary>
        internal const int MaxPadHostSteps = 40;

        /// <summary>One burst step's args: every burst word, the air context, the controls.</summary>
        internal static PartParitySweepArgs BurstArgs(PartParitySweepOp op)
        {
            return new PartParitySweepArgs
            {
                Label = BurstLabel,
                Op = op,
                Filter = TestCommandPartParitySweep.DefaultFilter,
                Fx = new List<string>(PartParityFxRules.BurstArgWords),
                Contexts = new List<string> { PartParityStateRules.ContextAir },
                Controls = true,
                MaxSeconds = TestCommandPartParitySweep.MaxMaxSeconds,
            };
        }

        /// <summary>One dust step's args: the dust word, the ground context, the controls.</summary>
        internal static PartParitySweepArgs DustArgs(PartParitySweepOp op)
        {
            return new PartParitySweepArgs
            {
                Label = DustLabel,
                Op = op,
                Filter = TestCommandPartParitySweep.DefaultFilter,
                Fx = new List<string>(PartParityFxRules.DustArgWords),
                Contexts = new List<string> { PartParityFxRules.ContextGround },
                Controls = true,
                MaxSeconds = TestCommandPartParitySweep.MaxMaxSeconds,
            };
        }

        /// <summary>The pad half of the acceptance cell (design G.7, the plan's <c>fx=</c> filter,
        /// no new test): every burst and dust case of the install, resumed step by step under one
        /// label each until the step reports done. Setup faults are logged, not asserted: a
        /// potential-0 puff is expected to read <c>burst-not-triggered</c>; read the manifest.</summary>
        private static IEnumerator RunPadHostSteps()
        {
            foreach (bool dust in new[] { false, true })
            {
                string label = dust ? DustLabel : BurstLabel;
                PartParitySweepStepResult reset = null;
                IEnumerator r = PartParitySweepRunner.RunStep(dust ? DustArgs(PartParitySweepOp.Reset) : BurstArgs(PartParitySweepOp.Reset),
                    res => reset = res);
                try { while (r.MoveNext()) yield return r.Current; }
                finally { PartParitySweepRunner.DisposeNested(r); }
                InGameAssert.IsTrue(reset != null && reset.Ok, label + " reset failed: " + Describe(reset));

                PartParitySweepStepResult step = null;
                for (int n = 0; n < MaxPadHostSteps; n++)
                {
                    step = null;
                    IEnumerator run = PartParitySweepRunner.RunStep(dust ? DustArgs(PartParitySweepOp.Run) : BurstArgs(PartParitySweepOp.Run),
                        x => step = x);
                    try { while (run.MoveNext()) yield return run.Current; }
                    finally { PartParitySweepRunner.DisposeNested(run); }
                    InGameAssert.IsTrue(step != null && step.Ok, label + " run failed: " + Describe(step));
                    ParsekLog.Info(PartParitySweepRunner.Tag, "ingame " + label + " step " +
                        (n + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ": " +
                        TestCommandPartParitySweep.FormatPayload(step.Payload));
                    InGameAssert.IsTrue(Value(step, "hostDamaged") != "true", label + " damaged the host: " + Describe(step));
                    if (Value(step, "done") == "true") break;
                }
                string payload = TestCommandPartParitySweep.FormatPayload(step.Payload);
                InGameAssert.IsTrue(Value(step, "done") == "true", label + " did not finish in " +
                    MaxPadHostSteps.ToString(System.Globalization.CultureInfo.InvariantCulture) + " steps: " + payload);
                InGameAssert.IsTrue(Value(step, "controls") == TestCommandPartParitySweep.ControlsOk,
                    label + " controls did not behave (" + payload + "); read parsek-partparity/" + label + "/manifest.json");
            }
        }

        /// <summary>One state step's args: every state at the orbit contexts, the controls on
        /// the first label only (they are the same for every label).</summary>
        private static PartParitySweepArgs StateArgs(string label, PartParitySweepOp op, string filter = null)
        {
            return new PartParitySweepArgs
            {
                Label = label,
                Op = op,
                Filter = filter ?? TestCommandPartParitySweep.DefaultFilter,
                States = new List<string>(PartParityStateRules.KnownStates),
                StatesAll = true,
                Controls = label == StateLabelPrefix + "1",
                MaxSeconds = TestCommandPartParitySweep.MaxMaxSeconds,
            };
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
