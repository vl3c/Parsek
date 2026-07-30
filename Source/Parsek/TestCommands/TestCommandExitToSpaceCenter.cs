using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The pre-exit wedge-guard verdict for <c>ExitToSpaceCenter</c> (R12). A driven
    /// FLIGHT -> SPACECENTER exit is only safe when Parsek's own
    /// <c>HighLogic_LoadScene_Patch.Prefix</c> would let it THROUGH; every other outcome
    /// spawns a modal that no seam verb can answer, so the verb refuses BEFORE initiating
    /// rather than wedging the run.
    /// </summary>
    internal enum ExitGateDecision
    {
        /// <summary>The interceptor would pass the exit through silently: drive it.</summary>
        Proceed,

        /// <summary>An armed <c>SwitchSegmentSession</c> with no live active tree. The
        /// prefix's Bug-C branch shows a dialog on the SESSION'S tree UNCONDITIONALLY -
        /// autoMerge does not suppress it - so this is a wedge even under autoMerge.</summary>
        RefusedSwitchSegmentSession,

        /// <summary>The live-state decision matrix (active tree) would spawn a modal.</summary>
        RefusedActiveTreeDialog,

        /// <summary>The finalized-pending-tree branch would spawn a modal.</summary>
        RefusedPendingTreeDialog,
    }

    /// <summary>
    /// Pure decision + payload helpers for the two-phase <c>ExitToSpaceCenter</c> seam
    /// verb (R12). The verb exists because the pending-tree AUTO-COMMIT - the ledger half
    /// of the crew-loss chain (CL-1) - is gated on
    /// <c>HighLogic.LoadedScene != GameScenes.FLIGHT</c> on all four of its routes, and no
    /// seam verb produced that transition: a destroyed active recorded vessel stashes its
    /// tree as PENDING and nulls <c>activeTree</c>, so <c>CommitTree</c> fails its
    /// <c>HasActiveTree</c> guard and <c>FlushAndQuit</c> saves and quits from inside
    /// FLIGHT ("saving 0 committed tree(s)").
    ///
    /// <para>It is deliberately NOT a generic <c>LoadScene</c> verb. The narrow contract is
    /// the FLIGHT exit WITH its finalize / stash / auto-commit side effects; a generic
    /// scene loader would over-promise EDITOR / MAINMENU (MAINMENU always shows the merge
    /// dialog under an active tree, by design) and under-specify this gauntlet.</para>
    ///
    /// <para>Every decision the Unity applier makes is factored here so the whole gate is
    /// xUnit-covered without a live KSP scene; the applier only samples live state, calls
    /// <c>SceneExitInterceptor.SafeWritePersistent</c> + <c>HighLogic.LoadScene</c>, and
    /// polls the settled scene.</para>
    /// </summary>
    internal static class TestCommandExitToSpaceCenter
    {
        /// <summary>The stable <c>msg</c> prefix for every wedge refusal. Paired with a
        /// <c>variant=</c> token so a spec author reads WHICH modal would have spawned,
        /// not just that one would.</summary>
        internal const string DialogRequiredReason = "dialog-required";

        /// <summary>
        /// Decide whether a driven FLIGHT -> SPACECENTER exit is safe to initiate.
        ///
        /// <para>ORDER MIRRORS THE PREFIX (<c>SceneExitInterceptor</c>'s
        /// <c>HighLogic_LoadScene_Patch.Prefix</c>): the prefix takes its
        /// no-active-tree routing branch BEFORE the live decision matrix, and inside that
        /// branch an armed session with a resolvable tree wins over the pending-tree
        /// variant. Reproducing the order here (rather than OR-ing the two variants) is
        /// what makes the reported <c>variant=</c> token name the modal that would
        /// actually have spawned.</para>
        ///
        /// <para>DELIBERATELY CONSERVATIVE - this is a SUPERSET of the wedging states, and
        /// that is the point. Three prefix fast paths (the no-op switch-segment
        /// auto-discard, the no-op no-session committed-resume auto-discard, and the
        /// idle-on-pad auto-discard) can convert a would-be-modal into a silent
        /// pass-through, so this gate can refuse an exit the prefix would in fact have
        /// allowed. It stays conservative because all three fast paths both DETECT AND MUTATE:
        /// probing them from a refusal check would tear down a live tree as a side effect
        /// of asking a question. A false REJECTED is a typed, side-effect-free answer the
        /// harness classifies immediately; a wedge is a <c>ControlTypes.All</c> input lock
        /// held until the step budget expires, and <c>AnswerMergeDialog</c> cannot clear it
        /// (it is <c>markerLive</c>-gated to the re-fly popup alone). The v1 supported
        /// shape is therefore <c>SetSetting autoMerge=true</c> earlier in the spec, which
        /// makes the common active-tree case <see cref="ExitGateDecision.Proceed"/>.</para>
        /// </summary>
        /// <param name="hasActiveTree">The live <c>ParsekFlight.HasActiveTree</c>.</param>
        /// <param name="switchSegmentSessionArmed"><c>ParsekScenario.ActiveSwitchSegmentSession != null</c>.</param>
        /// <param name="activeTreeVariant">
        /// <c>SceneExitInterceptor.ShouldShowDialogBeforeSceneChangeLive(SPACECENTER, flight)</c>
        /// - the prefix's step-(4) live decision matrix.</param>
        /// <param name="pendingTreeVariant">
        /// <c>SceneExitInterceptor.ShouldShowPendingTreeDialogBeforeSceneChangeLive(SPACECENTER)</c>
        /// - the prefix's no-active-tree pending branch.</param>
        internal static ExitGateDecision DecideExitGate(
            bool hasActiveTree,
            bool switchSegmentSessionArmed,
            SceneExitInterceptor.DialogVariant activeTreeVariant,
            SceneExitInterceptor.DialogVariant pendingTreeVariant)
        {
            if (!hasActiveTree)
            {
                // Prefix Bug-C branch: an armed session routes the dialog to the SESSION'S
                // tree with no autoMerge escape hatch, so refuse on the session alone. (If
                // the session's tree fails to resolve the prefix falls back to the pending
                // branch - also a dialog - so refusing here is right on both sub-paths.)
                if (switchSegmentSessionArmed)
                    return ExitGateDecision.RefusedSwitchSegmentSession;
                if (pendingTreeVariant != SceneExitInterceptor.DialogVariant.None)
                    return ExitGateDecision.RefusedPendingTreeDialog;
                return ExitGateDecision.Proceed;
            }

            if (activeTreeVariant != SceneExitInterceptor.DialogVariant.None)
                return ExitGateDecision.RefusedActiveTreeDialog;

            return ExitGateDecision.Proceed;
        }

        /// <summary>
        /// The variant token a refusal reports. For the two dialog refusals it is the live
        /// <see cref="SceneExitInterceptor.DialogVariant"/> name; the Bug-C session branch
        /// has no <c>DialogVariant</c> of its own (the prefix spawns that dialog without
        /// consulting the matrix), so it reports the literal
        /// <c>SwitchSegmentSession</c>.
        /// </summary>
        internal static string VariantToken(
            ExitGateDecision decision,
            SceneExitInterceptor.DialogVariant activeTreeVariant,
            SceneExitInterceptor.DialogVariant pendingTreeVariant)
        {
            switch (decision)
            {
                case ExitGateDecision.RefusedSwitchSegmentSession:
                    return "SwitchSegmentSession";
                case ExitGateDecision.RefusedActiveTreeDialog:
                    return activeTreeVariant.ToString();
                case ExitGateDecision.RefusedPendingTreeDialog:
                    return pendingTreeVariant.ToString();
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// The terminal REJECTED <c>msg</c> for a wedge refusal:
        /// <c>dialog-required variant=&lt;token&gt;</c>. One stable reason prefix (so a
        /// spec can match the class of refusal) plus the discriminating variant (so the
        /// log says which modal), mirroring <c>SetSetting</c>'s
        /// <c>setting-not-whitelisted name=&lt;x&gt;</c> shape. Percent-encoding of the
        /// space and <c>=</c> happens in the response formatter, not here.
        /// </summary>
        internal static string RefusalMsg(string variantToken)
            => DialogRequiredReason + " variant=" + (variantToken ?? string.Empty);

        /// <summary>Terminal completion payload once SPACECENTER settles with a game
        /// loaded. Mirrors <c>LoadGame</c>'s <c>scene</c> key so one reader handles both
        /// scene-changing verbs.</summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(string sceneName)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("scene", sceneName ?? string.Empty),
            };

        /// <summary>
        /// Decide the two-phase completion. Identical in shape and ordering to
        /// <see cref="TestCommandLoadGame.DecideLoadCompletion"/> (settled destination with
        /// a game -> OK; a MAINMENU settle -> fast failure ahead of the budget; budget
        /// expiry -> timeout; anything else keeps holding the FIFO head), and delegates to
        /// it so the two verbs cannot drift.
        ///
        /// <para>MAINMENU is a real failure mode here, not a theoretical one:
        /// <c>SafeWritePersistent</c> hard-blocks a MAINMENU exit whose save threw, and a
        /// foreign mod prefix could redirect the transition. Reporting it as a distinct
        /// fast terminal beats burning the whole 120 s budget on it.</para>
        /// </summary>
        internal static LoadCompletionDecision DecideExitCompletion(
            double elapsedSeconds, TestCommandScene currentScene, bool currentGameNonNull,
            double budgetSeconds)
        {
            return TestCommandLoadGame.DecideLoadCompletion(
                elapsedSeconds, currentScene, currentGameNonNull, budgetSeconds,
                TestCommandScene.SpaceCenter);
        }
    }
}
