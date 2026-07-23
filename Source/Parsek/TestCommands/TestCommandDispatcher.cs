using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure mirror of the KSP <c>GameScenes</c> the dispatcher cares about. The
    /// future addon maps <c>HighLogic.LoadedScene</c> to this so the dispatch core
    /// stays Unity-free (no Assembly-CSharp coupling). Only the scenes the
    /// precondition table distinguishes are enumerated; everything else is
    /// <see cref="Other"/>.
    /// </summary>
    internal enum TestCommandScene
    {
        Loading,
        MainMenu,
        SpaceCenter,
        Editor,
        Flight,
        TrackingStation,
        Other,
    }

    /// <summary>The pump's dispatch outcome for the head command.</summary>
    internal enum DispatchDecision
    {
        /// <summary>Run the verb handler now.</summary>
        Execute,

        /// <summary>Not ready: leave at head and retry next frame (until its budget expires).</summary>
        Defer,

        /// <summary>Terminal REJECTED (no side effect): write the verdict and advance.</summary>
        Reject,

        /// <summary>Crash recovery: journal at CLAIMED for this id; write INTERRUPTED and advance.</summary>
        Interrupted,
    }

    /// <summary>Pure result of <see cref="TestCommandDispatcher.DecideDispatch"/>.</summary>
    internal struct DispatchResult
    {
        public DispatchDecision Decision;

        /// <summary>Defer or reject reason (null for Execute / Interrupted).</summary>
        public string Reason;

        internal static DispatchResult Execute() =>
            new DispatchResult { Decision = DispatchDecision.Execute };

        internal static DispatchResult Defer(string reason) =>
            new DispatchResult { Decision = DispatchDecision.Defer, Reason = reason };

        internal static DispatchResult Reject(string reason) =>
            new DispatchResult { Decision = DispatchDecision.Reject, Reason = reason };

        internal static DispatchResult Interrupted() =>
            new DispatchResult { Decision = DispatchDecision.Interrupted };
    }

    /// <summary>
    /// Immutable snapshot of the Unity/KSP state the dispatcher reads, sampled once
    /// per poll by the addon and passed to the pure
    /// <see cref="TestCommandDispatcher.DecideDispatch"/>. Populating this is the
    /// addon's job (P4.4); the struct itself carries no Unity types.
    /// </summary>
    internal struct DispatchState
    {
        public TestCommandScene Scene;
        public bool GameLoaded;
        public bool SettingsPresent;
        public bool Recording;
        public bool HasTree;
        public bool Transitioning;
        public int SettleCounter;
        public bool BatchRunning;
        public bool LoadInFlight;

        // ----- M-C1 seam-verb bits (design-autotest-seam-verbs-c1.md, Data Model) -----

        /// <summary>A live "ParsekMerge" popup exists AND it is the re-fly merge dialog
        /// (kind-scoped: a live popup AND <c>ActiveReFlySessionMarker != null</c>). The
        /// bounded-wait signal for <c>AnswerMergeDialog</c>. NOT the raw MergeDialogPending
        /// flag, which three non-re-fly spawn sites also set.</summary>
        public bool ReFlyMergeDialogPresent;

        /// <summary>Mirrors <c>ParsekScenario.Instance.ActiveReFlySessionMarker != null</c>.
        /// <c>AnswerMergeDialog</c> uses it to decide whether it may DRIVE the re-fly
        /// conclusion scene-exit when no dialog is up yet.</summary>
        public bool ActiveReFlyMarker;

        /// <summary>Mirrors <c>ParsekScenario.Instance.ActiveMergeJournal != null</c>: a
        /// re-fly merge is mid-finalize. <c>InvokeRewind</c> must refuse rather than race
        /// the MergeJournalOrchestrator finisher.</summary>
        public bool MergeJournalInFlight;

        /// <summary>Career singletons live (CAREER mode + the relevant Funding / RnD /
        /// roster singleton present). The <c>KscAction</c> readiness bit for the three
        /// CAREER-only sub-actions (hire-kerbal / dismiss-kerbal / upgrade-facility): their
        /// funds legs need <c>Funding.Instance</c>, which is null outside CAREER.</summary>
        public bool CareerPresent;

        /// <summary>R&amp;D is live for a research spend: <c>(Mode == CAREER ||
        /// Mode == SCIENCE_SANDBOX) &amp;&amp; ResearchAndDevelopment.Instance != null</c>.
        /// The sub-action-scoped readiness bit for <c>research-node</c> ONLY (M-B3 OQ1): node
        /// research is live in Science mode even though <c>Funding.Instance</c> is null there,
        /// so research-node admits on <c>CareerPresent || RnDPresent</c> while the other three
        /// sub-actions stay CAREER-only. Do NOT widen the shared <see cref="CareerPresent"/>
        /// top-level bit; this is a per-sub-action widen for research-node alone.</summary>
        public bool RnDPresent;

        /// <summary>The run is in the SPACECENTER scene, where the
        /// <c>SpaceCenterBuilding</c> instances exist. Gates <c>upgrade-facility</c> only.</summary>
        public bool AtSpaceCenter;

        // ----- M-C2 EVA seam-verb bits (design-autotest-eva-missions.md, Data Model) -----

        /// <summary><c>FlightGlobals.ActiveVessel?.isEVA == true</c>. The readiness bit for
        /// <c>PlantFlag</c> / <c>EvaBoard</c>: both defer <c>not-eva</c> while the preceding
        /// EvaExit's auto-switch is still settling.</summary>
        public bool ActiveVesselIsEva;

        /// <summary><c>ParsekFlight.Instance.StructuralSplitPending</c> (a deferred split /
        /// branch in progress). <c>EvaExit</c> defers <c>split-pending</c> on it so the
        /// <c>OnCrewOnEva</c> skip path cannot silently swallow the branch.</summary>
        public bool StructuralSplitPending;

        /// <summary><c>FlightEVA.fetch != null</c> (the FLIGHT-scene singleton). <c>EvaExit</c>
        /// defers <c>flighteva-not-ready</c> while it is absent (scene still settling).</summary>
        public bool FlightEvaPresent;

        /// <summary>The replayed journal phase for THIS command id (None if fresh).</summary>
        public JournalPhase JournalPhase;
    }

    /// <summary>
    /// The Unity-side contract the pump invokes when a verb is dispatched to Execute.
    /// One method per v1 verb. Implemented by the addon (P4.4+); a fake stands in for
    /// pure tests. The dispatcher itself never calls this - it only decides.
    /// </summary>
    internal interface ITestCommandExecutor
    {
        void SetSetting(ParsedCommand cmd);
        void StartRecording(ParsedCommand cmd);
        void StopRecording(ParsedCommand cmd);
        void CommitTree(ParsedCommand cmd);
        void DiscardTree(ParsedCommand cmd);
        void RecordingState(ParsedCommand cmd);
        void RunTests(ParsedCommand cmd);
        void LoadGame(ParsedCommand cmd);
        void MissionMark(ParsedCommand cmd);
        void FlushAndQuit(ParsedCommand cmd);

        // ----- M-C1 seam verbs (batch 1) -----
        void InvokeRewind(ParsedCommand cmd);
        void AnswerMergeDialog(ParsedCommand cmd);
        void TimeJump(ParsedCommand cmd);
        void KscAction(ParsedCommand cmd);

        // ----- M-C1.1 follow-up -----
        void SaveGame(ParsedCommand cmd);

        // ----- M-C2 EVA seam verbs -----
        void EvaExit(ParsedCommand cmd);
        void EvaBoard(ParsedCommand cmd);
        void PlantFlag(ParsedCommand cmd);
    }

    /// <summary>The scene/state a verb requires before it may execute.</summary>
    internal enum VerbSceneRequirement
    {
        /// <summary>Executable in any settled, non-LOADING scene (read-only or menu-safe verbs).</summary>
        AnyScene,

        /// <summary>Requires FLIGHT (recorder/tree verbs); else Defer(not-in-flight).</summary>
        RequiresFlight,

        /// <summary>Requires a loaded game (ParsekSettings.Current != null); else Defer(game-not-loaded).</summary>
        RequiresGameLoaded,
    }

    /// <summary>
    /// Pure per-frame dispatch decision for the ParsekTestCommands seam (M-A2). Owns
    /// the per-verb precondition table and <c>DecideDispatch</c>; the pump (addon)
    /// samples <see cref="DispatchState"/> from Unity and acts on the returned
    /// <see cref="DispatchResult"/>.
    /// </summary>
    internal static class TestCommandDispatcher
    {
        // Per-verb scene/state precondition. LoadGame's recording-active /
        // load-in-flight guards and the global batch-running / safe-point gates are
        // applied in DecideDispatch on top of this table.
        private static readonly Dictionary<string, VerbSceneRequirement> Preconditions =
            new Dictionary<string, VerbSceneRequirement>
            {
                ["SetSetting"] = VerbSceneRequirement.RequiresGameLoaded,
                ["StartRecording"] = VerbSceneRequirement.RequiresFlight,
                ["StopRecording"] = VerbSceneRequirement.RequiresFlight,
                ["CommitTree"] = VerbSceneRequirement.RequiresFlight,
                ["DiscardTree"] = VerbSceneRequirement.RequiresFlight,
                ["RecordingState"] = VerbSceneRequirement.AnyScene,
                ["RunTests"] = VerbSceneRequirement.AnyScene,
                ["LoadGame"] = VerbSceneRequirement.AnyScene,
                ["MissionMark"] = VerbSceneRequirement.AnyScene,
                ["FlushAndQuit"] = VerbSceneRequirement.AnyScene,
                // M-C1 seam verbs (batch 1). InvokeRewind / TimeJump run from FLIGHT;
                // AnswerMergeDialog straddles FLIGHT (pre-transition dialog) and non-FLIGHT
                // (post-transition dialog), so AnyScene; KscAction is AnyScene with a
                // per-sub-action career / SPACECENTER sub-gate applied in DecideDispatch.
                ["InvokeRewind"] = VerbSceneRequirement.RequiresFlight,
                ["AnswerMergeDialog"] = VerbSceneRequirement.AnyScene,
                ["TimeJump"] = VerbSceneRequirement.RequiresFlight,
                ["KscAction"] = VerbSceneRequirement.AnyScene,
                // M-C1.1 follow-up. SaveGame is an in-process persist of the current game
                // state; AnyScene with an in-executor no-game refusal (the design's
                // AnyScene-with-a-game precondition: refuse at MAINMENU / no CurrentGame).
                ["SaveGame"] = VerbSceneRequirement.AnyScene,
                // M-C2 EVA seam verbs. All three run from FLIGHT (spawn / plant / board on the
                // active vessel). The finer preconditions (kerbal aboard, proximity, the stable
                // flag lock) are executor-side refusals; the EVA-specific dispatch guards
                // (split-pending / flighteva-not-ready / not-eva) are applied in DecideDispatch.
                ["EvaExit"] = VerbSceneRequirement.RequiresFlight,
                ["EvaBoard"] = VerbSceneRequirement.RequiresFlight,
                ["PlantFlag"] = VerbSceneRequirement.RequiresFlight,
            };

        /// <summary>
        /// Pure per-frame dispatch decision for the head command. Evaluation order
        /// (per the design's dispatch matrix): (1) journal-phase crash recovery for
        /// this id, (2) parse / verb-class rejects (no side effect), (3) the
        /// scene/state gate (safe point, batch running, per-verb scene precondition),
        /// (4) the LoadGame recording-active / load-in-flight guards. The pump only
        /// ever calls this on the strict-FIFO head command.
        /// </summary>
        internal static DispatchResult DecideDispatch(ParsedCommand parsed, DispatchState state)
        {
            // 1. Journal-phase crash recovery for this id. A leftover CLAIMED means we
            // claimed it then crashed mid-execution: do NOT re-execute. (EXECUTED /
            // DONE ids are filtered by the processed-set upstream and never reach live
            // dispatch; a fresh None id proceeds.)
            if (state.JournalPhase == JournalPhase.Claimed)
                return DispatchResult.Interrupted();

            // 2. Parse / verb-class rejects (terminal REJECTED, no side effect).
            if (!parsed.ParseOk)
                return DispatchResult.Reject(parsed.ParseError); // malformed / missing-id / missing-cmd

            TestCommandVerbClass verbClass = TestCommandVerbs.Classify(parsed.Verb);
            if (verbClass == TestCommandVerbClass.Reserved)
                return DispatchResult.Reject("not-implemented-v1");
            if (verbClass == TestCommandVerbClass.Unknown)
                return DispatchResult.Reject("unknown-command");

            // 3. Scene / state gate.
            // Safe point: never dispatch during LOADING, a scene transition, or the settle window.
            if (state.Scene == TestCommandScene.Loading || state.Transitioning || state.SettleCounter > 0)
                return DispatchResult.Defer("not-safe-point");
            // No command executes while an in-game test batch runs.
            if (state.BatchRunning)
                return DispatchResult.Defer("batch-running");

            VerbSceneRequirement req = RequirementFor(parsed.Verb);
            if (req == VerbSceneRequirement.RequiresFlight && state.Scene != TestCommandScene.Flight)
                return DispatchResult.Defer("not-in-flight");
            if (req == VerbSceneRequirement.RequiresGameLoaded && !state.SettingsPresent)
                return DispatchResult.Defer("game-not-loaded");

            // 4. Per-verb extra guards (mirroring how the LoadGame guards live here).
            switch (parsed.Verb)
            {
                case "LoadGame":
                    // Never silently discard an in-flight recording, and never overlap loads.
                    if (state.Recording)
                        return DispatchResult.Reject("recording-active");
                    if (state.LoadInFlight)
                        return DispatchResult.Reject("load-in-flight");
                    break;

                case "InvokeRewind":
                    // A re-fly reloads the scene and mutates the save. Refuse when a re-fly
                    // merge journal is mid-finalize (racing the crash-recovery finisher), when
                    // a LoadGame is already mid-flight, or when a recorder is live (the reload
                    // would silently discard it; commit / discard first).
                    if (state.MergeJournalInFlight)
                        return DispatchResult.Reject("merge-journal-in-flight");
                    if (state.LoadInFlight)
                        return DispatchResult.Reject("load-in-flight");
                    if (state.Recording)
                        return DispatchResult.Reject("recording-active");
                    break;

                case "AnswerMergeDialog":
                    // Bounded wait: defer while there is nothing to answer AND no re-fly
                    // attempt to conclude. As soon as a live re-fly merge popup exists OR a
                    // re-fly session marker is present, execute (the verb drives the
                    // conclusion scene-exit itself when only the marker is present).
                    if (!state.ReFlyMergeDialogPresent && !state.ActiveReFlyMarker)
                        return DispatchResult.Defer("no-refly-dialog");
                    break;

                case "KscAction":
                {
                    // AnyScene, but with a per-sub-action readiness sub-gate. The readiness bit
                    // splits by sub-action (M-B3 OQ1): research-node admits in CAREER OR
                    // SCIENCE_SANDBOX (R&D and node research are live in Science mode even
                    // though Funding is null), so it reads CareerPresent || RnDPresent; the
                    // other three (hire-kerbal / dismiss-kerbal / upgrade-facility) stay
                    // CAREER-only because their funds legs need Funding.Instance, which Science
                    // mode lacks. This widens the per-sub-action gate for research-node ALONE;
                    // the shared CareerPresent top-level bit is unchanged. upgrade-facility
                    // additionally needs the SPACECENTER scene (the funds debit lives on a
                    // SPACECENTER-scene SpaceCenterBuilding instance, not a headless singleton).
                    string action = Arg(parsed, "action");
                    bool ready = action == "research-node"
                        ? (state.CareerPresent || state.RnDPresent)
                        : state.CareerPresent;
                    if (!ready)
                        return DispatchResult.Defer("career-not-ready");
                    if (action == "upgrade-facility" && !state.AtSpaceCenter)
                        return DispatchResult.Defer("not-at-space-center");
                    break;
                }

                case "EvaExit":
                    // A LoadGame scene reload mid-EVA would silently discard the spawn, so
                    // refuse (fail-fast, like InvokeRewind). Then defer while a Parsek split /
                    // branch is pending (the OnCrewOnEva skip path would silently swallow the
                    // branch) or while the FlightEVA singleton has not settled in yet.
                    if (state.LoadInFlight)
                        return DispatchResult.Reject("load-in-flight");
                    if (state.StructuralSplitPending)
                        return DispatchResult.Defer("split-pending");
                    if (!state.FlightEvaPresent)
                        return DispatchResult.Defer("flighteva-not-ready");
                    break;

                case "PlantFlag":
                case "EvaBoard":
                    // Both act on the EVA kerbal, so defer while the preceding EvaExit's
                    // auto-switch is still settling (the active vessel is not yet the EVA one).
                    if (!state.ActiveVesselIsEva)
                        return DispatchResult.Defer("not-eva");
                    break;
            }

            // 5. Ready to execute.
            return DispatchResult.Execute();
        }

        /// <summary>Pure read of a percent-decoded command arg (null when absent), used
        /// by the KscAction sub-action sub-gate.</summary>
        private static string Arg(ParsedCommand parsed, string key)
            => parsed.Args != null && parsed.Args.TryGetValue(key, out string v) ? v : null;

        /// <summary>The scene/state requirement for an implemented verb.</summary>
        internal static VerbSceneRequirement RequirementFor(string verb)
        {
            return Preconditions.TryGetValue(verb, out VerbSceneRequirement req)
                ? req
                : VerbSceneRequirement.AnyScene;
        }

        /// <summary>Read-only view of the precondition table (for coverage tests).</summary>
        internal static IReadOnlyDictionary<string, VerbSceneRequirement> PreconditionTable => Preconditions;
    }

    /// <summary>
    /// Pure per-command deferral-budget model. A command that sits at the head of the
    /// queue in Defer longer than its budget is converted to a TIMEOUT terminal (with
    /// the last defer reason as msg) and the pump advances, so a never-satisfiable
    /// command never wedges the run. Budgets are wall-clock seconds measured from when
    /// the command first reached the head and began deferring.
    /// </summary>
    internal static class DeferralBudget
    {
        /// <summary>Ordinary scene-settle / game-loaded waits.</summary>
        internal const double DefaultSeconds = 60.0;

        /// <summary>A cold LoadGame + scene settle can take minutes on a large save.</summary>
        internal const double LoadGameSeconds = 300.0;

        /// <summary>StartRecording may wait for FLIGHT with an unpacked active vessel;
        /// sized to the scene-arrival wait rather than the fixed default. Chosen value
        /// (the design specifies "scene-wait budget" without a number); revisit when the
        /// scenario spec pins it.</summary>
        internal const double StartRecordingSceneWaitSeconds = 180.0;

        /// <summary>Fallback RunTests budget when the scenario spec supplies none. A full
        /// in-game batch can run minutes; chosen value pending the scenario's declared
        /// runtime budget (the authoritative source when provided).</summary>
        internal const double RunTestsFallbackSeconds = 600.0;

        /// <summary>InvokeRewind copies a quicksave, reloads the scene, and runs
        /// ConsumePostLoad; sized like LoadGame (M-C1, ~300 s).</summary>
        internal const double InvokeRewindSeconds = 300.0;

        /// <summary>AnswerMergeDialog may DRIVE the conclusion scene-exit that surfaces the
        /// pre-transition dialog, then hold the head through the post-answer scene settle
        /// (M-C1, ~120 s).</summary>
        internal const double AnswerMergeDialogSeconds = 120.0;

        /// <summary>TimeJump is synchronous but the spawn-queue settle + ledger recalc want a
        /// bound well under an infinite hang (M-C1, ~120 s).</summary>
        internal const double TimeJumpSeconds = 120.0;

        /// <summary>EvaExit (M-C2): dispatch defer (split-pending / scene settle) + spawn +
        /// auto-switch + optional ladder release + optional settleSeconds dwell.</summary>
        internal const double EvaExitSeconds = 120.0;

        /// <summary>PlantFlag (M-C2): not-eva defer + the F1 bounded-wait plant gate (landing
        /// settle) + heading acquire + animation + dialog answer.</summary>
        internal const double PlantFlagSeconds = 180.0;

        /// <summary>EvaBoard (M-C2): not-eva defer + board + vessel teardown + focus switch +
        /// board-merge quiescence + settle.</summary>
        internal const double EvaBoardSeconds = 120.0;

        /// <summary>
        /// The deferral budget (seconds) for <paramref name="verb"/>. For RunTests the
        /// scenario's declared runtime budget is authoritative when supplied via
        /// <paramref name="scenarioBudgetSeconds"/>; otherwise the fallback applies.
        /// </summary>
        internal static double BudgetSeconds(string verb, double? scenarioBudgetSeconds = null)
        {
            switch (verb)
            {
                case "LoadGame":
                    return LoadGameSeconds;
                case "StartRecording":
                    return StartRecordingSceneWaitSeconds;
                case "RunTests":
                    return scenarioBudgetSeconds ?? RunTestsFallbackSeconds;
                case "InvokeRewind":
                    return InvokeRewindSeconds;
                case "AnswerMergeDialog":
                    return AnswerMergeDialogSeconds;
                case "TimeJump":
                    return TimeJumpSeconds;
                case "EvaExit":
                    return EvaExitSeconds;
                case "PlantFlag":
                    return PlantFlagSeconds;
                case "EvaBoard":
                    return EvaBoardSeconds;
                // KscAction rides the default 60 s (career-ready / SPACECENTER wait; the
                // action itself is immediate).
                default:
                    return DefaultSeconds;
            }
        }

        /// <summary>
        /// True when a command that first began deferring at
        /// <paramref name="firstDeferredAtSeconds"/> has, by
        /// <paramref name="nowSeconds"/>, exceeded <paramref name="budgetSeconds"/> and
        /// must be converted to a TIMEOUT terminal.
        /// </summary>
        internal static bool ShouldTimeout(double firstDeferredAtSeconds, double nowSeconds, double budgetSeconds)
        {
            return (nowSeconds - firstDeferredAtSeconds) >= budgetSeconds;
        }
    }
}
