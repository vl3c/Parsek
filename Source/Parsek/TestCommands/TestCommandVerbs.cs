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
        // Implemented (v1 + M-C1 batch 1 + M-C1.1 follow-up + M-C2 EVA batch + EVA-4 + R12 + the arrival-validation lane + the player-workflow lane + M-A7 + the map-view pair + InvokeRewindToLaunch + the logistics pair): 30 verbs.
        // M-C1 promoted InvokeRewind, AnswerMergeDialog, TimeJump, and KscAction from
        // Reserved to Implemented (design-autotest-seam-verbs-c1.md). The M-C1.1 follow-up
        // added SaveGame (the M-B3 L2/R6 persist-before-reload dependency). M-C2 added the
        // EVA family EvaExit / EvaBoard / PlantFlag (design-autotest-eva-missions.md); like
        // SaveGame, none was ever in the reserved envelope, so they are NEW implemented verb
        // names (additive, not a promotion). The wire tokens for the promoted verbs are
        // byte-identical before and after; only the response changes (not-implemented-v1 ->
        // real). EVA-4 added EvaChuteDeploy (the kerbal personal parachute), additive in the
        // same way - never in the reserved envelope. R12 added ExitToSpaceCenter (the driven
        // FLIGHT -> SPACECENTER exit that reaches the pending-tree auto-commit), additive
        // too: the reserved envelope never carried a scene-transition verb, and a generic
        // LoadScene verb was rejected in favour of this narrow one (see
        // TestCommandExitToSpaceCenter). R12's OTHER half is an additive `scene=` ARG on the
        // existing LoadGame verb, which needs no table entry at all - that is exactly the
        // envelope-stability property the design's "readers ignore unknown keys" clause buys.
        // R12 ALSO PROMOTED SimulateStockSwitchClick out of the reserved list below (20 -> 21
        // implemented, 11 -> 10 reserved). That one IS a promotion in the strict sense the
        // comment above describes: the wire token is byte-identical before and after, and
        // only the response changes (REJECTED not-implemented-v1 -> a real terminal), so no
        // existing spec's bytes move. It is the first reserved name to be promoted since
        // M-C1.
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
            "EvaChuteDeploy",
            "ExitToSpaceCenter",
            "SimulateStockSwitchClick",
            "MissionConfig",
            // The player-workflow lane promoted the THIRD and FOURTH reserved names,
            // same strict shape as the two before them (wire token byte-identical,
            // only the response changes). Together they close the flight-scene half
            // of the player loop that MissionConfig only armed: StartLoopPlayback is
            // the Missions window's "Warp to..." button (fast-forward to the looped
            // mission's next faithful departure through
            // MissionLoopUnitBuilder -> ComputeNextRelaunchUT -> FastForwardToEventUT),
            // and EnterWatchMode is the "Watch" button (point the flight camera at a
            // replaying ghost, with the entry VERIFIED by read-back because the
            // underlying call is a silent-failing toggle).
            "StartLoopPlayback",
            "EnterWatchMode",
            // M-A7 (render composition manifest). ADDITIVE, like SaveGame and the EVA
            // family: the reserved envelope never carried an export verb. It flushes the
            // armed RenderCompositionRecorder's accumulation to the KSP root and reports
            // the record counts, so a lane can take a manifest at a chosen instant instead
            // of waiting for the scene-exit / teardown auto-flush. Read-only with respect
            // to the game world - the only side effect is the manifest file.
            "ExportRenderManifest",
            // The map-view pair. ADDITIVE (the reserved envelope never carried a
            // camera / scene-presentation verb), and the reason they exist is a
            // MEASURED instrument gap rather than a wish: everything Parsek draws on
            // the map surface is gated on the map being OPEN, and no seam verb ever
            // opened it. GhostTrajectoryPolylineRenderer.Driver.LateUpdate's second
            // statement is `if (!MapView.MapIsEnabled) return;`, so on every render-
            // composition lane flown to date the ownership-PUBLISH half of the pipeline
            // never ran once while the INTENT half ran every frame - see
            // RC-OWN-DRAW-HALF-IS-MAP-GATED in todo-and-known-bugs.md. ExitMapView is
            // the mirror, so a lane that opens the map can close it again before
            // flight-scene steps that behave differently under the overlay.
            "EnterMapView",
            "ExitMapView",
            // Rewind-to-Launch. ADDITIVE (27 -> 28 implemented, reserved unchanged at 7),
            // like SaveGame, the EVA family, ExitToSpaceCenter, ExportRenderManifest and
            // the map-view pair: the reserved envelope never carried a rewind-to-launch
            // verb. It is emphatically NOT a second spelling of InvokeRewind - that verb
            // drives the Re-Fly / Rewind-to-Separation system (RewindPoint + ChildSlot ->
            // RewindInvoker.StartInvoke -> a ReFlySessionMarker and a merge journal), while
            // this one drives the Recordings-table "R" button
            // (RecordingStore.InitiateRewind: reload the parsek_rw_* launch quicksave, no
            // RewindPoint, no marker, no journal, no supersede write). Two mechanisms, two
            // verbs; folding them into one would make the wire token ambiguous about which
            // half of the timeline machinery a spec exercised.
            "InvokeRewindToLaunch",
            // The logistics pair: the FIFTH and SIXTH strict promotions out of the
            // reserved list below (28 -> 30 implemented, 7 -> 5 reserved), same shape as
            // the four before them - the wire token is byte-identical, only the response
            // changes. Together they close THE wall in front of every route lane: no
            // driven run could create or operate a supply route, so every committed
            // route fixture in the suite is a HARVEST of a hand-flown session.
            // SealSlot is the Unfinished Flights per-row Seal button
            // (UnfinishedFlightSealHandler.TrySeal), which is what closes the candidacy
            // gate RouteCandidateFinder.IsTreeFullySealed reads; RouteCommand is the
            // Logistics window's Create Route button (through the shared
            // RouteCreationService funnel) plus the three row operations
            // (RouteOrchestrator.TrySendOneCycleNow / TryPause / TryActivate).
            // Ordered so seal-then-create is the readable pair.
            "SealSlot",
            "RouteCommand",
        };

        // Reserved (recognized, not implemented in v1): 5 verbs.
        // SimulateStockSwitchClick left this set in R12; MissionConfig left it for the
        // arrival-validation lane; StartLoopPlayback and EnterWatchMode left it for the
        // player-workflow lane; SealSlot and RouteCommand left it for the logistics lane
        // (every one of the six a strict promotion: wire token byte-identical, only the
        // response changes -- REJECTED not-implemented-v1 -> a real terminal).
        // StopPlayback deliberately STAYS reserved: teardown is FlushAndQuit's job, so a
        // stop verb would be a second, weaker owner of it. StashSlot and FlySlot stay
        // reserved beside their promoted sibling SealSlot on purpose: FlySlot's mechanism
        // is already driveable under a DIFFERENT name (InvokeRewind, the re-fly), so a
        // second spelling would make the wire token ambiguous about which half of the
        // timeline machinery a spec exercised, and StashSlot has no consumer - nothing in
        // the suite needs to OPEN a slot, only to close one.
        private static readonly HashSet<string> ReservedVerbs = new HashSet<string>
        {
            "StopPlayback",
            "StashSlot",
            "FlySlot",
            "CrashAfterJournalPhase",
            "RunInvariantReport",
        };

        /// <summary>
        /// Verbs that CANNOT change anything a save would capture. Everything else is
        /// treated as state-mutating by <see cref="IsStateMutatingVerb"/>.
        ///
        /// <para>Deliberately an allow-list of the harmless ones rather than a deny-list
        /// of the dangerous ones: the consumer is the FlushAndQuit save suppression, and
        /// the fail-safe direction is "assume it mutated" -> clear the latch -> save
        /// normally (the pre-suppression behaviour). A new verb that is forgotten here is
        /// therefore safe by default; only a wrongly-added entry could suppress a save
        /// that should have happened.</para>
        ///
        /// <para><c>FlushAndQuit</c> itself is listed because it is the reader - treating
        /// it as mutating would clear the latch on the very dispatch that consults it.</para>
        ///
        /// <para><b>Kept in step with the harness.</b> <c>hlib.SEAM_VERB_TAIL_ROLE</c>
        /// classifies the same verbs for a DIFFERENT question (may an unmet-mission tail
        /// still drive this?), but its <c>TAIL_ROLE_INERT</c> members are picked on the same
        /// underlying fact - "reads state or stamps the log, never changes the game". The two
        /// must not drift, and a harness cell reads THIS list out of the source to enforce
        /// it. The single deliberate difference is <c>FlushAndQuit</c>, which hlib calls
        /// <c>TAIL_ROLE_CLEANUP</c>; that cell excludes it by name, for the reason above.</para>
        /// </summary>
        private static readonly HashSet<string> NonMutatingVerbs = new HashSet<string>
        {
            "RecordingState",        // read-only report
            "MissionMark",           // stamps one log line, nothing else (hlib: TAIL_ROLE_INERT)
            "ExportRenderManifest",  // read-only w.r.t. the game world; writes only the manifest file
            "FlushAndQuit",          // the reader of the latch, not a mutator of the world
        };

        /// <summary>
        /// True when dispatching <paramref name="verb"/> may change state a subsequent
        /// save would capture. Pure; see <see cref="NonMutatingVerbs"/> for the
        /// fail-safe direction. An unknown / null verb counts as mutating.
        /// </summary>
        internal static bool IsStateMutatingVerb(string verb)
        {
            return string.IsNullOrEmpty(verb) || !NonMutatingVerbs.Contains(verb);
        }

        /// <summary>Read-only view of the non-mutating verbs (for coverage tests).</summary>
        internal static IReadOnlyCollection<string> NonMutatingVerbNames => NonMutatingVerbs;

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
