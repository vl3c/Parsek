using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// R12 partial: the thin Unity applier for the two-phase <c>ExitToSpaceCenter</c>
    /// verb. Every decision it makes is factored into the pure
    /// <see cref="TestCommandExitToSpaceCenter"/>; this file only samples live state,
    /// performs the stock exit sequence, and polls the settled scene.
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // ----- ExitToSpaceCenter (R12, two-phase) -----
        //
        // The driven FLIGHT -> SPACECENTER exit. Its reason to exist is the pending-tree
        // AUTO-COMMIT: all four auto-commit routes are gated on
        // `HighLogic.LoadedScene != GameScenes.FLIGHT`, and until R12 no seam verb produced
        // that transition. When the active RECORDED vessel is destroyed, ParsekFlight
        // finalizes the tree, STASHES it as the pending tree and nulls activeTree, so
        // CommitTree fails its HasActiveTree guard and FlushAndQuit saves and quits from
        // inside FLIGHT - which is why the crew-loss run's final log line was
        // `saving 0 committed tree(s)`.
        //
        // TWO CONTRACTS ARE LOAD-BEARING HERE:
        //
        // (1) WEDGE GUARD FIRST, always. Parsek's own HighLogic.LoadScene prefix blocks a
        //     flight exit and spawns a `ControlTypes.All`-locking modal whenever a merge
        //     decision is outstanding, and NOTHING re-invokes LoadScene except that
        //     dialog's own postChoice. A seam-driven exit into that state does not fail -
        //     it WEDGES: the input lock stands, the FIFO head holds, and the step burns its
        //     whole budget. AnswerMergeDialog cannot rescue it either; it finds its popup
        //     through FindReFlyMergePopup, which is markerLive-gated to the re-fly dialog
        //     alone, so a plain whole-tree merge popup is structurally unmatchable (that
        //     exact combination already cost one deterministically-INVALID S4.1 run). So
        //     the verb evaluates the SAME live predicates the prefix will and refuses up
        //     front with a typed REJECTED naming the variant, and the supported v1 shape is
        //     `SetSetting autoMerge=true` earlier in the spec.
        //
        // (2) PERSIST BEFORE THE LoadScene. A raw HighLogic.LoadScene models a scene exit
        //     that NO stock UI route performs: stock saveAndExit saves before the prefix
        //     fires, and SceneExitInterceptor.SafeWritePersistent exists precisely to cover
        //     the stock routes that do not. AnswerMergeDialog learned this the expensive
        //     way - driving a raw LoadScene left the re-fly marker unpersisted, the
        //     destination scene read `Marker loaded: none`, and a FIXTURE-shaped divergence
        //     presented as a product defect. Same sequence here, same reason.
        private void ExitToSpaceCenterImpl(ParsedCommand cmd)
        {
            // Dispatch already gated FLIGHT (RequiresFlight), load-in-flight and
            // merge-journal-in-flight.
            ParsekFlight flight = ParsekFlight.Instance;
            ParsekScenario scenario = ParsekScenario.Instance;

            bool hasActiveTree = flight != null && flight.HasActiveTree;
            bool sessionArmed = scenario != null && scenario.ActiveSwitchSegmentSession != null;

            // The two live predicates the prefix itself calls, evaluated against the SAME
            // destination the verb is about to drive. Reusing the product's own decision
            // (rather than restating it) is what keeps the guard from drifting out of step
            // with the interceptor.
            SceneExitInterceptor.DialogVariant activeTreeVariant =
                SceneExitInterceptor.ShouldShowDialogBeforeSceneChangeLive(GameScenes.SPACECENTER, flight);
            SceneExitInterceptor.DialogVariant pendingTreeVariant =
                SceneExitInterceptor.ShouldShowPendingTreeDialogBeforeSceneChangeLive(GameScenes.SPACECENTER);

            ParsekLog.Info(Tag,
                $"exittospacecenter start scene={HighLogic.LoadedScene} autoMerge={Bool(ParsekScenario.IsAutoMerge)} " +
                $"hasActiveTree={Bool(hasActiveTree)} hasPendingTree={Bool(RecordingStore.HasPendingTree)} " +
                $"switchSegmentSession={Bool(sessionArmed)} activeTreeVariant={activeTreeVariant} pendingTreeVariant={pendingTreeVariant}");

            ExitGateDecision gate = TestCommandExitToSpaceCenter.DecideExitGate(
                hasActiveTree, sessionArmed, activeTreeVariant, pendingTreeVariant);

            if (gate != ExitGateDecision.Proceed)
            {
                string variant = TestCommandExitToSpaceCenter.VariantToken(
                    gate, activeTreeVariant, pendingTreeVariant);
                string msg = TestCommandExitToSpaceCenter.RefusalMsg(variant);
                ParsekLog.Warn(Tag,
                    $"exittospacecenter refused reason={TestCommandExitToSpaceCenter.DialogRequiredReason} " +
                    $"variant={variant} gate={gate} - a merge modal would block the exit and no seam verb can answer it " +
                    "(set autoMerge=true, or commit/discard the tree, before exiting)");
                SetExecResult("REJECTED", null, msg);
                return;
            }

            // Clean route: mirror the stock Space Center exit button. SafeWritePersistent
            // fires onSceneConfirmExit + ClearpersistentIdDictionaries + SaveGame exactly
            // as stock saveAndExit does; it returns false only for a MAINMENU save throw,
            // so a false here on a SPACECENTER destination is a Warn-and-continue that we
            // still surface in the log.
            bool persisted = SceneExitInterceptor.SafeWritePersistent(GameScenes.SPACECENTER);
            ParsekLog.Info(Tag,
                $"exittospacecenter driving scene-exit dest=SPACECENTER persisted={Bool(persisted)} " +
                "- the finalize/stash pipeline runs on the transition and the pending tree auto-commits on arrival");
            HighLogic.LoadScene(GameScenes.SPACECENTER);
            SetExecResult(PendingVerdict, null, null);
        }

        // Bounded two-phase completion. Same shape as TryCompleteLoadGame (it is the same
        // scene-settle question, just with the destination fixed): a settled SPACECENTER
        // with a game loaded is the success, a MAINMENU settle is the fast failure, and the
        // budget is the catch-all so a blocked transition terminates instead of holding the
        // FIFO head to the harness run budget.
        private void TryCompleteExitToSpaceCenter(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds("ExitToSpaceCenter");
            TestCommandScene scene = MapScene(HighLogic.LoadedScene);
            bool gameLoaded = HighLogic.CurrentGame != null;
            LoadCompletionDecision decision =
                TestCommandExitToSpaceCenter.DecideExitCompletion(elapsed, scene, gameLoaded, budget);

            if (decision == LoadCompletionDecision.StillWaiting)
                return;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            ClearTwoPhase();

            switch (decision)
            {
                case LoadCompletionDecision.CompleteOk:
                {
                    string sceneName = HighLogic.LoadedScene.ToString();
                    List<KeyValuePair<string, string>> payload =
                        TestCommandExitToSpaceCenter.BuildCompletePayload(sceneName);
                    ParsekLog.Info(Tag,
                        $"exittospacecenter complete scene={sceneName} game-loaded=true " +
                        $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
                    EmitExecutedTerminal(id, seq, verb, "OK", payload, null, dequeueHead: true);
                    break;
                }
                case LoadCompletionDecision.LoadFailedMenu:
                {
                    ParsekLog.Error(Tag,
                        $"exittospacecenter failed-returned-to-menu scene={scene} elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
                    EmitExecutedTerminal(id, seq, verb, "ERROR", null, "exit-failed-returned-to-menu", dequeueHead: true);
                    break;
                }
                case LoadCompletionDecision.LoadTimeout:
                {
                    TestCommandDiagnostics.Timeout(id, verb, elapsed, "exit-timeout");
                    ParsekLog.Error(Tag,
                        $"exittospacecenter timeout scene={scene} elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s " +
                        "- the transition never settled at SPACECENTER (a foreign LoadScene prefix, or a modal spawned after the gate passed)");
                    EmitExecutedTerminal(id, seq, verb, "ERROR", null, "exit-timeout", dequeueHead: true);
                    break;
                }
            }
        }
    }
}
