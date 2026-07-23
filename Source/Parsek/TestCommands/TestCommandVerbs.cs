using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Classification of a command verb against the known-verb table.
    /// </summary>
    internal enum TestCommandVerbClass
    {
        /// <summary>A v1 verb this addon implements.</summary>
        Implemented,

        /// <summary>A recognized phase-3+ verb, not implemented in v1
        /// (REJECTED with <c>not-implemented-v1</c> so the orchestrator can probe
        /// capability rather than confuse it with a typo).</summary>
        Reserved,

        /// <summary>Not a known verb (REJECTED with <c>unknown-command</c>).</summary>
        Unknown,
    }

    /// <summary>
    /// Pure known-verb table for the ParsekTestCommands seam (M-A2). Verb match is
    /// case-sensitive and exact. Reserving the phase-3 names now keeps the wire
    /// envelope (id/cmd/args, percent-encoding, journal, verdicts) designed once so
    /// later commands slot in without a format break.
    /// </summary>
    internal static class TestCommandVerbs
    {
        // Implemented (v1 + M-C1 batch 1 + M-C1.1 follow-up + M-C2 EVA batch): 18 verbs.
        // M-C1 promoted InvokeRewind, AnswerMergeDialog, TimeJump, and KscAction from
        // Reserved to Implemented (design-autotest-seam-verbs-c1.md). The M-C1.1 follow-up
        // added SaveGame (the M-B3 L2/R6 persist-before-reload dependency). M-C2 added the
        // EVA family EvaExit / EvaBoard / PlantFlag (design-autotest-eva-missions.md); like
        // SaveGame, none was ever in the reserved envelope, so they are NEW implemented verb
        // names (additive, not a promotion). The wire tokens for the promoted verbs are
        // byte-identical before and after; only the response changes (not-implemented-v1 ->
        // real).
        private static readonly HashSet<string> ImplementedVerbs = new HashSet<string>
        {
            "SetSetting",
            "StartRecording",
            "StopRecording",
            "CommitTree",
            "DiscardTree",
            "RecordingState",
            "RunTests",
            "LoadGame",
            "MissionMark",
            "FlushAndQuit",
            "InvokeRewind",
            "AnswerMergeDialog",
            "TimeJump",
            "KscAction",
            "SaveGame",
            "EvaExit",
            "EvaBoard",
            "PlantFlag",
        };

        // Reserved (recognized, not implemented in v1): 11 verbs.
        private static readonly HashSet<string> ReservedVerbs = new HashSet<string>
        {
            "StartLoopPlayback",
            "StopPlayback",
            "EnterWatchMode",
            "SealSlot",
            "StashSlot",
            "FlySlot",
            "RouteCommand",
            "MissionConfig",
            "SimulateStockSwitchClick",
            "CrashAfterJournalPhase",
            "RunInvariantReport",
        };

        internal static TestCommandVerbClass Classify(string verb)
        {
            if (verb != null && ImplementedVerbs.Contains(verb))
                return TestCommandVerbClass.Implemented;
            if (verb != null && ReservedVerbs.Contains(verb))
                return TestCommandVerbClass.Reserved;
            return TestCommandVerbClass.Unknown;
        }

        /// <summary>Read-only view of the implemented v1 verbs (for coverage tests).</summary>
        internal static IReadOnlyCollection<string> ImplementedVerbNames => ImplementedVerbs;

        /// <summary>Read-only view of the reserved phase-3 verbs (for coverage tests).</summary>
        internal static IReadOnlyCollection<string> ReservedVerbNames => ReservedVerbs;
    }
}
