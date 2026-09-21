using System;
using System.Collections.Generic;
using Parsek.InGameTests;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// GUI-census partial: <c>UiAction op=run window=testrunner|testrunnerglobal
    /// category=&lt;name&gt;</c> - run one in-game test category through THAT RUNNER
    /// WINDOW'S OWN runner.
    ///
    /// <para><b>WHY IT IS NOT THE <c>RunTests</c> VERB.</b> Both test-runner windows
    /// construct their own <c>InGameTestRunner</c>, and each runner does its OWN reflection
    /// discovery: the <c>InGameTestInfo</c> objects a window's table draws are that
    /// runner's instances, and <c>Status</c> lives on them. The <c>RunTests</c> verb drives
    /// a THIRD runner (the addon's <c>ownedRunner</c>), so a batch through it leaves both
    /// windows' tables reading "not run" - a census capture labelled "results" over a table
    /// of dots. This op exists so that picture is honest, and it is the ONLY reason it
    /// exists: it runs the category header "Run" button's own body
    /// (<c>ResetCategory</c> then <c>RunCategory</c>) against the runner the window
    /// already owns, adding no player-facing surface and no fourth runner.</para>
    ///
    /// <para><b>ONE CATEGORY, NEVER "ALL".</b> <c>category=</c> is REQUIRED
    /// (<c>TestCommandUiState.TryParseRun</c>). The full batch is minutes of tests, half of
    /// which mutate the save, and a census step must not be able to ask for that by leaving
    /// an arg out. A category matching zero tests in that runner's discovery is REJECTED
    /// rather than dispatched, because an empty batch reports <c>total=0</c>, leaves the
    /// table untouched, and photographs exactly like a batch that never started.</para>
    ///
    /// <para><b>THE BATCH BASELINE IS THE RUNNER'S OWN.</b> <c>RunCategory</c> captures a
    /// clean <c>persistent.sfs</c> baseline before the batch and reverts from it at
    /// teardown (<c>InGameTestRunner.CaptureBatchBaseline</c>), exactly as it does under a
    /// player's click and under the <c>RunTests</c> verb. Nothing here duplicates or
    /// bypasses that; a lane that wants the window state it arranged to survive the run
    /// takes its other captures BEFORE this step, which is the GUI-1 ordering rule for the
    /// same reason.</para>
    ///
    /// <para><b><c>await=false</c> AND WHAT IT COSTS.</b> Absent, <c>await=</c> is TRUE and
    /// this op is exactly what it was: two-phase, polling until the window's runner goes
    /// idle, reporting that category's tally. <c>await=false</c> terminates OK as soon as
    /// the batch is confirmed dispatched and leaves it running, which is the ONLY way a
    /// census photographs the table mid-batch - under <c>await=true</c> no capture can be
    /// ordered before the batch ends, because the op holds the FIFO head until it does. It
    /// also unblocks a category longer than the 60 s default deferral budget this op rides,
    /// which otherwise ends ERROR <c>run-not-finished</c> on a perfectly healthy batch.
    /// <para>The cost is stated rather than hidden: NO TALLY IS REPORTED, and not because
    /// it was omitted for tidiness - mid-batch the numbers would be wrong in a way a lane
    /// could gate on (see <c>TestCommandUiState.BuildRunStartedPayload</c>). A lane that
    /// wants both a running picture and an outcome runs the category twice, or reads the
    /// runner's own <c>BATCH_COMPLETE</c> line from the collected log.</para>
    /// <para><c>finished=true</c> is a legitimate answer: a category whose every cell is
    /// scene-ineligible completes synchronously inside <c>RunCategory</c>, because
    /// <c>RunBatch</c> has no unconditional yield before it clears <c>isRunning</c>.</para>
    /// </para>
    ///
    /// <para><b>THE BATCH GATE IS RELAXED, NARROWLY.</b> An <c>await=false</c> OK would buy
    /// nothing on its own: <c>ParsekTestCommandAddon.Update</c> returns before the pump
    /// while <c>IsBatchRunning()</c>, and that predicate reads the very runner this op
    /// drives - so the following <c>CaptureScreenshot</c> / <c>DumpGuiTree</c> would sit
    /// unexecuted until the batch ended and photograph the post-batch table. While a batch
    /// THIS ARM started is running, the gate therefore admits the verbs that cannot perturb
    /// it (<c>TestCommandUiState.IsBatchGateRelaxableVerb</c>, derived from
    /// <c>TestCommandVerbs.IsStateMutatingVerb</c>) and holds every other verb exactly as
    /// before. Nothing changes when no such batch is running: the relaxation is keyed on a
    /// flag only this arm sets.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // ----- arm -----

        private void UiActionRunOp(ParsedCommand cmd, ParsekUI ui, UiWindowSpec spec)
        {
            if (!TestCommandUiState.TryParseRun(
                    spec.Name, ArgOrNull(cmd, TestCommandUiState.CategoryArg),
                    out string category, out string reject))
            {
                string detail = reject == TestCommandUiState.RunUnsupportedWindowReason
                    ? $"{reject} window={spec.Name} "
                      + $"valid={TestCommandUiState.RunnableWindowNames}"
                    : $"{reject} window={spec.Name}";
                ParsekLog.Warn(Tag, $"uiaction rejected reason={reject} "
                    + $"window={spec.Name}");
                SetExecResult("REJECTED", null, detail);
                return;
            }

            // Parsed BEFORE the runner is touched, so a typo'd await= can never dispatch a
            // batch it then mis-reports the completion policy of.
            if (!TestCommandUiState.TryParseAwait(
                    ArgOrNull(cmd, TestCommandUiState.AwaitArg),
                    out bool awaitBatch, out string awaitReject))
            {
                string raw = ArgOrNull(cmd, TestCommandUiState.AwaitArg) ?? string.Empty;
                ParsekLog.Warn(Tag, $"uiaction rejected reason={awaitReject} "
                    + $"window={spec.Name} await={raw}");
                SetExecResult("REJECTED", null,
                    $"{awaitReject} window={spec.Name} await={raw} "
                    + $"valid={TestCommandUiState.StateTrueToken},"
                    + TestCommandUiState.StateFalseToken);
                return;
            }

            InGameTestRunner runner = ResolveWindowRunner(ui, spec.Name);
            if (runner == null)
            {
                // The window has never drawn, so its runner does not exist: both windows
                // create it lazily on the first draw. REJECTED with the remedy named,
                // rather than constructing a runner here - a seam-constructed runner would
                // be a SECOND runner for that window, and its results would not be the
                // ones the window's own table draws.
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiState.RunRunnerNotReadyReason
                    + $" window={spec.Name} (open the window first; its runner is built on "
                    + "the first drawn frame)");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiState.RunRunnerNotReadyReason} window={spec.Name}");
                return;
            }

            if (runner.IsRunning || IsBatchRunning())
            {
                // RunCategory returns SILENTLY while a batch runs, so dispatching here
                // would report a batch that never started.
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiState.RunAlreadyRunningReason
                    + $" window={spec.Name} category={category} "
                    + $"windowRunner={Bool(runner.IsRunning)}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiState.RunAlreadyRunningReason} window={spec.Name}");
                return;
            }

            int discovered = CountTestsInCategory(runner, category);
            if (discovered == 0)
            {
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiState.RunCategoryUnknownReason
                    + $" window={spec.Name} category={category} discovered=0");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiState.RunCategoryUnknownReason} window={spec.Name} "
                    + $"category={category}");
                return;
            }

            // The category header button's body, in its order: reset that category's rows
            // (so the table shows THIS run rather than a previous one's statuses), then
            // run it. NOT the isolated entry point: an `[isolated]` FLIGHT test restores a
            // quicksaved baseline between cells, which is not something a census step
            // should reach by default, and no lane has asked for it.
            runner.ResetCategory(category);
            runner.RunCategory(category);

            if (!awaitBatch)
            {
                // RunCategory returns after StartCoroutine, so IsRunning read HERE is the
                // honest answer to "did it survive the first pass": false means the whole
                // category completed synchronously (every cell scene-ineligible), which is
                // an OK with finished=true, never an ERROR.
                bool stillRunning = runner.IsRunning;
                if (stillRunning)
                {
                    // The ONLY writer of the flag. Set only when a batch is genuinely
                    // live, so a synchronous completion leaves the gate untouched. The
                    // RUNNER is recorded beside it: the clear checks that reference rather
                    // than "is any batch running", so the relaxation cannot survive onto a
                    // batch this seam did not start.
                    detachedBatchArmed = true;
                    detachedBatchRunner = runner;
                }
                ParsekLog.Info(Tag, $"uiaction run started window={spec.Name} "
                    + $"category={category} discovered={Int(discovered)} "
                    + $"await=false running={Bool(stillRunning)} "
                    + $"batchGateRelaxed={Bool(stillRunning)} (no tally is reported: the "
                    + "batch is still running and its counts would be mid-flight)");
                SetExecResult("OK",
                    TestCommandUiState.BuildRunStartedPayload(
                        spec.Name, category, discovered, stillRunning),
                    null);
                return;
            }

            uiActionPending = new UiActionPending
            {
                Op = UiActionOp.Run,
                Window = spec.Name,
                StartFrame = Time.frameCount,
                RunCategory = category,
                RunTests = discovered,
            };
            ParsekLog.Info(Tag, $"uiaction run initiated window={spec.Name} "
                + $"category={category} discovered={Int(discovered)} "
                + "(awaiting the batch)");
            SetExecResult(PendingVerdict, null, null);
        }

        // ----- settle -----

        /// <summary>
        /// Polls the batch this op dispatched. The signal is the WINDOW'S runner going
        /// idle, not a frame count: the batch runs for as long as its tests take, and the
        /// numbers the window's summary line draws are only final once it stops.
        /// </summary>
        private void TryCompleteUiActionRun(double now)
        {
            UiActionPending pending = uiActionPending;
            ParsekUI ui = ParsekUI.ActiveInstance;
            InGameTestRunner runner = ResolveWindowRunner(ui, pending.Window);

            // A null runner here means the window object went away mid-batch (a scene
            // teardown for the Settings-launched one). Treated as FINISHED rather than as
            // "still running", so the op terminates on its own reason instead of burning
            // the budget: the zeros below then say plainly that nothing was measured.
            bool running = runner != null && runner.IsRunning;
            double budget = DeferralBudget.BudgetSeconds("UiAction");
            bool expired = DeferralBudget.ShouldTimeout(completionStartedAt, now, budget);
            if (running && !expired) return;

            string id = completionId;
            long seq = completionSeq;
            string verb = completionVerb;
            ClearTwoPhase();

            if (running)
            {
                // Not a refusal: the batch was dispatched and is simply slower than this
                // verb's bound. Named so it cannot be read as "the runner said no".
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiState.RunNotFinishedReason
                    + $" window={pending.Window} category={pending.RunCategory} "
                    + $"budget={budget.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    $"{TestCommandUiState.RunNotFinishedReason} window={pending.Window} "
                    + $"category={pending.RunCategory}",
                    dequeueHead: true);
                return;
            }

            // THE CATEGORY'S OWN ROWS, not the runner's whole-discovery counters: those
            // are recomputed over every discovered test (InGameTestRunner.RecountResults)
            // while ResetCategory clears only the named category, so a second op=run step
            // on the same window would report the first step's rows as well - and a lane
            // gating on `failed=0` would be reading the wrong batch.
            UiRunTally tally = TestCommandUiState.TallyCategory(
                runner != null ? runner.Tests : null, pending.RunCategory);
            ParsekLog.Info(Tag, $"uiaction run ok window={pending.Window} "
                + $"category={pending.RunCategory} discovered={Int(pending.RunTests)} "
                + $"total={Int(tally.Total)} passed={Int(tally.Passed)} "
                + $"failed={Int(tally.Failed)} skipped={Int(tally.Skipped)}");
            EmitExecutedTerminal(id, seq, verb, "OK",
                TestCommandUiState.BuildRunPayload(
                    pending.Window, pending.RunCategory,
                    tally.Total, tally.Passed, tally.Failed, tally.Skipped),
                null, dequeueHead: true);
        }

        // ----- live-state plumbing -----

        /// <summary>
        /// The <c>InGameTestRunner</c> one runner WINDOW owns, or null before that window's
        /// first draw. ONE resolver for both windows, the <c>UiWindowHandle</c> rule: a
        /// mis-wired row would run a category through the other window's runner and
        /// photograph a table that did not move.
        /// </summary>
        private static InGameTestRunner ResolveWindowRunner(ParsekUI ui, string window)
        {
            if (window == TestCommandUiAction.TestRunnerWindow)
            {
                TestRunnerUI w = ui != null ? ui.GetTestRunnerUI() : null;
                return w != null ? w.RunnerForTesting : null;
            }
            if (window == TestCommandUiAction.TestRunnerGlobalWindow)
            {
                TestRunnerShortcut s = TestRunnerShortcut.Instance;
                return s != null ? s.RunnerForTesting : null;
            }
            return null;
        }

        /// <summary>
        /// How many of that runner's discovered tests carry this category. The pre-dispatch
        /// anti-vacuity check, and the mirror of <c>WarnIfCategoryMatchesNoTests</c> on the
        /// <c>RunTests</c> side - except that this op REFUSES where that one warns, because
        /// its product is a photograph rather than a tally.
        /// </summary>
        private static int CountTestsInCategory(InGameTestRunner runner, string category)
        {
            if (runner == null) return 0;
            IReadOnlyList<InGameTestInfo> tests = runner.Tests;
            int count = 0;
            for (int i = 0; i < tests.Count; i++)
            {
                if (string.Equals(tests[i].Category, category, StringComparison.Ordinal))
                    count++;
            }
            return count;
        }
    }
}
