using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The two-phase LoadGame completion outcome (F2). Distinguishes the success
    /// (a settled FLIGHT scene with a game loaded), the two terminal FAILURES a
    /// bad load can produce, and the still-in-progress case.
    /// </summary>
    /// <remarks>
    /// Pure decision + payload helpers for the two-phase <c>LoadGame</c> boot verb
    /// (P5.7). The Unity side realises the <c>.sfs</c> into a KSP <c>Game</c> via
    /// <c>GamePersistence.LoadGame</c> and hands the primitive shape of that result here:
    /// a null / incompatible game or an out-of-range active-vessel index cannot be
    /// focused, so the verb fails with <c>load-failed</c> rather than sending
    /// <c>FlightDriver.StartAndFocusVessel</c> a bad index (design edge case 27). Kept
    /// pure so the focusability decision + completion payload are xUnit-covered without
    /// a live KSP <c>Game</c>.
    /// </remarks>
    internal enum LoadCompletionDecision
    {
        /// <summary>The load has not settled yet: keep polling.</summary>
        StillWaiting,

        /// <summary>Settled FLIGHT scene with a game loaded: terminal OK.</summary>
        CompleteOk,

        /// <summary>The completion budget expired without a settled flight: terminal
        /// ERROR (msg=load-timeout). A never-settling load must not ride the harness
        /// run budget.</summary>
        LoadTimeout,

        /// <summary>The scene settled back at MAINMENU with no flight (a failed load,
        /// e.g. an NRE in FlightDriver.Start on an incompatible save): terminal ERROR
        /// (msg=load-failed-returned-to-menu).</summary>
        LoadFailedMenu,
    }

    /// <summary>
    /// Where a loaded game boots (the ledger-lane no-vessel extension). A
    /// FOCUSABLE game flies (<c>FlightDriver.StartAndFocusVessel</c>); a VALID
    /// game with no focusable vessel (a vessel-less clean-slate career, or
    /// <c>activeVessel = -1</c> with only parked vessels) resumes to SPACECENTER
    /// exactly like the stock Load menu; anything else is <c>load-failed</c>.
    /// The first live L-track run (2026-07-23) proved every career fixture is
    /// NECESSARILY vessel-less, so the old focusable-or-fail contract blocked
    /// the entire ledger lane.
    ///
    /// <para>R12 added <see cref="TrackingStation"/>: the boot channel is the only
    /// route into TRACKSTATION (nothing in Parsek ever entered that scene
    /// programmatically), and 7 in-game categories / 25 <c>Scene =
    /// GameScenes.TRACKSTATION</c> declarations were stranded behind its absence.
    /// The route is caller-REQUESTED (<c>scene=trackstation</c>), never derived:
    /// no property of a save says "this one wants the tracking station".</para>
    /// </summary>
    internal enum LoadRoute
    {
        /// <summary>Focusable active vessel: boot to FLIGHT (the original contract).</summary>
        Focusable,

        /// <summary>Valid game, no focusable vessel (or <c>scene=spacecenter</c>):
        /// resume to SPACECENTER.</summary>
        NoVesselSpaceCenter,

        /// <summary>Explicitly requested <c>scene=trackstation</c>: boot to TRACKSTATION
        /// through the same <c>Game.Start</c> -> <c>HighLogic.LoadScene</c> path the
        /// SPACECENTER route takes (R12).</summary>
        TrackingStation,

        /// <summary>Null / incompatible / no flight state: terminal ERROR load-failed.</summary>
        Failed,
    }

    /// <summary>
    /// The optional <c>scene=</c> boot-route override on <c>LoadGame</c> (R12).
    /// ABSENT is the pre-R12 contract verbatim: the SAVE's shape derives the route
    /// (focusable -> FLIGHT, otherwise SPACECENTER). A present value makes the route
    /// caller-chosen instead, which is the only way to reach TRACKSTATION at all.
    /// </summary>
    internal enum RequestedBootScene
    {
        /// <summary>No <c>scene=</c> arg: derive the route from the save (pre-R12 behavior).</summary>
        Unspecified,

        /// <summary><c>scene=spacecenter</c>: force the SPACECENTER resume even when the
        /// save carries a focusable vessel.</summary>
        SpaceCenter,

        /// <summary><c>scene=trackstation</c>: boot to TRACKSTATION regardless of vessels.</summary>
        TrackingStation,
    }

    internal static class TestCommandLoadGame
    {
        /// <summary>Reject reason for a <c>scene</c> arg that is not one of the two
        /// accepted wire values.</summary>
        internal const string SceneArgInvalidReason = "scene-arg-invalid";

        /// <summary>
        /// Parses the optional <c>scene=</c> arg (R12). FAIL-CLOSED and case-sensitive,
        /// exactly like <c>RunTests</c>' <c>isolated=</c>: the value reaches the wire
        /// through <c>run.py::encode_value</c> (a bare <c>str(value)</c>), so a lenient
        /// parse would silently accept a spelling the harness-side validator rejects, and
        /// a mis-spelled scene would boot the DEFAULT route while the spec believes it
        /// asked for another one - the B10-shaped fail-open (a silently-wrong-scene boot
        /// reads green through an all-skipped batch). An ABSENT arg is the pre-R12
        /// derived route; an EMPTY value is a typo, not an omission, so it is rejected.
        ///
        /// <para><c>flight</c> is deliberately NOT an accepted value. A forced FLIGHT
        /// boot is not expressible: the focusable route needs an in-range
        /// <c>activeVesselIdx</c> that only the save can supply, so <c>scene=flight</c>
        /// on a vessel-less save could only mean "fail", which <c>load-failed</c> already
        /// says. It also inherits known-gate 6 (the FLIGHT route deliberately does NOT run
        /// <c>UpdateScenarioModules</c>), a documented product asymmetry that must not be
        /// widened as a side effect of adding a scene argument.</para>
        /// </summary>
        internal static bool TryParseRequestedScene(string raw, out RequestedBootScene scene)
        {
            scene = RequestedBootScene.Unspecified;
            if (raw == null)
                return true;
            if (raw == "spacecenter")
            {
                scene = RequestedBootScene.SpaceCenter;
                return true;
            }
            if (raw == "trackstation")
            {
                scene = RequestedBootScene.TrackingStation;
                return true;
            }
            return false;
        }
        /// <summary>
        /// True when the loaded game can be focused: it exists, is version-COMPATIBLE
        /// (<c>Game.compatible</c>, the gate v0.5.4 <c>TestingTools.LoadSave</c> applied), has
        /// a flight state with a proto-vessel list, and its active-vessel index is in range.
        /// An incompatible game (a save from a mismatched KSP / mod version) must fail with
        /// <c>load-failed</c> rather than being flown into a broken scene.
        /// </summary>
        internal static bool IsLoadedGameFocusable(
            bool gamePresent, bool compatible, bool flightStatePresent, bool protoVesselsPresent,
            int activeVesselIdx, int protoVesselCount)
        {
            if (!gamePresent || !compatible || !flightStatePresent || !protoVesselsPresent)
                return false;
            return activeVesselIdx >= 0 && activeVesselIdx < protoVesselCount;
        }

        /// <summary>
        /// Route the loaded game: FLIGHT when focusable, SPACECENTER when the game
        /// is valid (present + compatible + flight state) but carries no focusable
        /// vessel, load-failed otherwise. The proto-vessel LIST being null is
        /// tolerated on the no-vessel route (KSP normalizes it at scene start);
        /// game/compatibility/flight-state absence is never tolerated.
        ///
        /// <para>R12 PRECEDENCE, in order: (1) game VALIDITY gates every route - a null /
        /// incompatible / flight-state-less game is <see cref="LoadRoute.Failed"/> no
        /// matter what scene the caller asked for, because a requested scene cannot
        /// rescue a save that did not parse; (2) an explicit
        /// <paramref name="requestedScene"/> then chooses the route outright; (3) only
        /// with no request does the SAVE's shape decide, verbatim as before R12. So
        /// <c>scene=spacecenter</c> forces the SPACECENTER resume even when the save
        /// carries a focusable vessel, and <c>scene=trackstation</c> boots to TRACKSTATION
        /// regardless of vessels.</para>
        ///
        /// <para><see cref="RequestedBootScene"/> is a THREE-valued enum with no invalid
        /// member: an unparseable <c>scene=</c> arg is REJECTED by the executor before the
        /// save is ever read (a bad ARG must not present as a bad SAVE), so this decision
        /// only ever sees the three legal values.</para>
        /// </summary>
        internal static LoadRoute DecideLoadRoute(
            bool gamePresent, bool compatible, bool flightStatePresent, bool protoVesselsPresent,
            int activeVesselIdx, int protoVesselCount,
            RequestedBootScene requestedScene = RequestedBootScene.Unspecified)
        {
            // (1) Validity first: identical to the pre-R12 predicate, just hoisted.
            // IsLoadedGameFocusable already requires all three of these, so hoisting
            // them cannot change the Unspecified outcome.
            if (!gamePresent || !compatible || !flightStatePresent)
                return LoadRoute.Failed;

            // (2) An explicit request wins over the derived route.
            if (requestedScene == RequestedBootScene.TrackingStation)
                return LoadRoute.TrackingStation;
            if (requestedScene == RequestedBootScene.SpaceCenter)
                return LoadRoute.NoVesselSpaceCenter;

            // (3) No request: the save's shape decides (pre-R12 contract).
            if (IsLoadedGameFocusable(gamePresent, compatible, flightStatePresent,
                                      protoVesselsPresent, activeVesselIdx, protoVesselCount))
                return LoadRoute.Focusable;
            return LoadRoute.NoVesselSpaceCenter;
        }

        /// <summary>
        /// The scene a given route must SETTLE in for its two-phase completion to read
        /// OK. Pairs with <see cref="DecideLoadCompletion"/>'s <c>expectedScene</c> so the
        /// route decision and the completion decision cannot drift apart: teaching a new
        /// route where it lands is one edit here, not two.
        ///
        /// <para><see cref="LoadRoute.Failed"/> never reaches two-phase (it is a
        /// single-phase terminal ERROR before any scene change), so its value is
        /// unreachable; FLIGHT is returned as the inert default rather than throwing,
        /// because a decision helper that throws inside a completion poll would convert a
        /// diagnosable failure into an exception terminal.</para>
        /// </summary>
        internal static TestCommandScene ExpectedSceneFor(LoadRoute route)
        {
            switch (route)
            {
                case LoadRoute.NoVesselSpaceCenter: return TestCommandScene.SpaceCenter;
                case LoadRoute.TrackingStation: return TestCommandScene.TrackingStation;
                default: return TestCommandScene.Flight;
            }
        }

        /// <summary>
        /// Decide the two-phase LoadGame completion (F2). The addon polls this only at
        /// SETTLED scenes (the pump gates off during LOADING / scene transition / settle),
        /// and the scene-transition flag is raised synchronously when the load is initiated,
        /// so the FIRST settled observation is already the destination scene. That makes a
        /// MAINMENU observation a reliable "the load bounced back to the menu" signal with
        /// no grace period needed.
        ///
        /// Order: a settled EXPECTED scene with a game loaded is the success; a settle-back to
        /// MAINMENU is the fast failure (checked before the budget so it does not have to wait
        /// out the whole load budget); the budget expiry is the catch-all for a load that
        /// never settles anywhere. Any other settled scene keeps waiting until one of those
        /// three fires. Kept pure so every cell is xUnit-covered without a live KSP scene.
        /// </summary>
        /// <remarks>
        /// <paramref name="expectedScene"/> is the route's landing scene (R12; it
        /// replaced the pre-R12 two-valued <c>bool expectSpaceCenter</c> when the
        /// TRACKSTATION route made the destination three-valued). Supply it from
        /// <see cref="ExpectedSceneFor"/>. A settle-back to MAINMENU and the budget
        /// expiry keep their meanings on EVERY route; only the success scene moves.
        /// The default keeps FLIGHT, so every pre-R12 four-argument call site reads
        /// unchanged.
        /// </remarks>
        internal static LoadCompletionDecision DecideLoadCompletion(
            double elapsedSeconds, TestCommandScene currentScene, bool currentGameNonNull,
            double budgetSeconds, TestCommandScene expectedScene = TestCommandScene.Flight)
        {
            if (currentScene == expectedScene && currentGameNonNull)
                return LoadCompletionDecision.CompleteOk;
            if (currentScene == TestCommandScene.MainMenu)
                return LoadCompletionDecision.LoadFailedMenu;
            if (elapsedSeconds >= budgetSeconds)
                return LoadCompletionDecision.LoadTimeout;
            return LoadCompletionDecision.StillWaiting;
        }

        /// <summary>Terminal completion payload once the new scene settles with a game loaded.</summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(string sceneName, string save)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("scene", sceneName ?? string.Empty),
                new KeyValuePair<string, string>("save", save ?? string.Empty),
            };
    }
}
