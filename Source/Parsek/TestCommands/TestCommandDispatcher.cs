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
