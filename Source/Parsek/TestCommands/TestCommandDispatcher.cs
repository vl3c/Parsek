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

            // 4. LoadGame guards: never silently discard an in-flight recording, and
            // never overlap two loads.
            if (parsed.Verb == "LoadGame")
            {
                if (state.Recording)
                    return DispatchResult.Reject("recording-active");
                if (state.LoadInFlight)
                    return DispatchResult.Reject("load-in-flight");
            }

            // 5. Ready to execute.
            return DispatchResult.Execute();
        }

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
}
