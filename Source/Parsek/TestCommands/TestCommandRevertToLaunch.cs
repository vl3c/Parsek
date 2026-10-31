using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure half of the automation-only <c>RevertToLaunch</c> verb (vessel names, design
    /// <c>design-vessel-and-mission-names.md</c> section 15.3, ruling 5): stock's Revert to
    /// Launch OUTSIDE a Re-Fly session, the option the Esc menu's Revert Flight offers.
    ///
    /// <para><b>Why a verb of its own.</b> <c>ReFlyRevert</c> drives the same stock option but
    /// only during a live Re-Fly, where <c>RevertInterceptor</c>'s prefix blocks the stock body
    /// and answers with Parsek's dialog; it refuses <c>reflyrevert-no-session</c> otherwise.
    /// The plain revert (load kind StockRevert, the <c>MemoryUndoRevertedFlight</c> policy
    /// cell) had no driver but the isolated in-game <c>RevertFlow</c> cell. This verb calls
    /// <c>FlightDriver.RevertToLaunch()</c>, the option's callback, so stock fires
    /// <c>OnRevertToLaunchFlightState</c> and reloads the post-init flight state exactly as
    /// for a player, and Parsek's revert detection and undo run unchanged.</para>
    ///
    /// <para><b>Target.</b> Optional <c>target=launch|prelaunch</c> (default <c>launch</c>, the
    /// <c>ReFlyRevert</c> spelling): <c>prelaunch</c> is the Esc menu's Revert to VAB / SPH,
    /// <c>FlightDriver.RevertToPrelaunch(ShipConstruction.ShipType)</c>. Stock offers it under
    /// <c>CanLeaveToEditor</c>, <c>FlightDriver.CanRevertToPrelaunch</c>, a ship config and a
    /// VAB / SPH ship type (<c>PauseMenu.drawStockRevertOptions</c>), so only a flight launched
    /// from the editor has it (<c>reverttolaunch-prelaunch-unavailable</c> otherwise). It lands
    /// in EDITOR: OK once the editor is up and settled and the revert's OnLoad consumed the
    /// revert kind, payload <c>scene= facility= ship=</c>, line
    /// <c>reverttolaunch complete target=prelaunch facility= ship='..' elapsed=</c>.</para>
    ///
    /// <para><b>Grammar.</b> <c>cmd=RevertToLaunch [target=launch|prelaunch]</c>, issued in FLIGHT. TWO-PHASE,
    /// <c>RequiresFlight</c>, 180 s (a FLIGHT bootstrap, the <c>LaunchFromEditor</c> size).
    /// Refusals (REJECTED, before anything is pressed): a live Re-Fly marker
    /// (<c>reverttolaunch-refly-session</c>: the prefix would answer with its own dialog, which
    /// is <c>ReFlyRevert</c>'s job), and stock not offering the option
    /// (<c>reverttolaunch-unavailable</c>: <c>FlightDriver.CanRevertToPostInit</c> false, no
    /// post-init state, or the game's <c>CanRestart</c> off, the three conditions the Esc menu
    /// shows the button under).</para>
    ///
    /// <para><b>Completion.</b> OK once a NEW flight scene settled (FLIGHT, a game loaded, a
    /// <c>ParsekFlight</c> instance other than the one the revert started from that saw its own
    /// <c>onFlightReady</c>, and an unpacked active vessel: LoadGame's flight-ready rule,
    /// <c>TestCommandLoadGame.IsFlightReadyForThisLoad</c>): payload <c>scene= activePid= activeName= situation=</c>, line
    /// <c>reverttolaunch complete pid= name='..' situation= elapsed=</c>. MAINMENU is the fast
    /// ERROR (<c>revert-failed-returned-to-menu</c>), the budget the catch-all
    /// (<c>revert-timeout</c>).</para>
    /// </summary>
    internal static class TestCommandRevertToLaunch
    {
        internal const string Verb = "RevertToLaunch";

        internal const string TargetKey = "target";
        internal const string LaunchToken = "launch";
        internal const string PrelaunchToken = "prelaunch";

        /// <summary>The closed <c>target=</c> vocabulary. hlib.REVERTTOLAUNCH_TARGET_VALUES
        /// mirrors it.</summary>
        internal static readonly string[] TargetValues = { "launch", "prelaunch" };

        internal const string TargetArgInvalidReason = "reverttolaunch-target-arg-invalid";
        internal const string ReFlySessionReason = "reverttolaunch-refly-session";
        internal const string UnavailableReason = "reverttolaunch-unavailable";
        internal const string PrelaunchUnavailableReason = "reverttolaunch-prelaunch-unavailable";

        /// <summary>Frames the editor must be up before the revert to the editor answers OK,
        /// the <c>GoToEditor</c> settle (its OnLoad and first frames have run).</summary>
        internal const int EditorSettleFrames = TestCommandEditorRoute.MinSettleFrames;

        /// <summary>The REJECTED vocabulary, in gate order. hlib.REVERTTOLAUNCH_REASONS
        /// mirrors it (RevertToLaunchSourceSyncTests).</summary>
        internal static readonly string[] Reasons =
        {
            "reverttolaunch-target-arg-invalid",
            "reverttolaunch-refly-session",
            "reverttolaunch-unavailable",
            "reverttolaunch-prelaunch-unavailable",
        };

        internal const string ThrewReason = "reverttolaunch-threw";
        internal const string ReturnedToMenuReason = "revert-failed-returned-to-menu";
        internal const string TimeoutReason = "revert-timeout";

        /// <summary>The post-call ERROR vocabulary.</summary>
        internal static readonly string[] ErrorReasons =
        {
            "reverttolaunch-threw",
            "revert-failed-returned-to-menu",
            "revert-timeout",
        };

        /// <summary>Parses the optional <c>target=</c>: absent is Revert to Launch. Returns
        /// null or the refusal reason.</summary>
        internal static string ParseTarget(string raw, out bool prelaunch)
        {
            prelaunch = false;
            if (raw == null || raw == LaunchToken) return null;
            if (raw == PrelaunchToken) { prelaunch = true; return null; }
            return TargetArgInvalidReason;
        }

        /// <summary>The Revert to VAB / SPH gate: no live Re-Fly marker, then the four
        /// conditions stock's revert menu offers the option under. Null to proceed.</summary>
        internal static string DecidePrelaunchGate(bool reFlyMarkerLive, bool canLeaveToEditor,
            bool canRevertToPrelaunch, bool hasShipConfig, bool shipTypeIsEditor)
        {
            if (reFlyMarkerLive) return ReFlySessionReason;
            if (!canLeaveToEditor || !canRevertToPrelaunch || !hasShipConfig || !shipTypeIsEditor)
                return PrelaunchUnavailableReason;
            return null;
        }

        /// <summary>
        /// The Revert to VAB / SPH completion: MAINMENU fails fast; OK once the scene is
        /// EDITOR with a game, the editor up for <see cref="EditorSettleFrames"/> frames and the
        /// revert kind consumed by the editor's OnLoad (so the revert branch ran); the budget
        /// ends everything else.
        /// </summary>
        internal static LoadCompletionDecision DecidePrelaunchCompletion(
            double elapsedSeconds, TestCommandScene scene, bool gameLoaded, bool editorUp,
            int editorUpFrames, bool revertConsumed, double budgetSeconds)
        {
            if (scene == TestCommandScene.MainMenu)
                return LoadCompletionDecision.LoadFailedMenu;
            if (scene == TestCommandScene.Editor && gameLoaded && editorUp
                && editorUpFrames >= EditorSettleFrames && revertConsumed)
                return LoadCompletionDecision.CompleteOk;
            return elapsedSeconds >= budgetSeconds
                ? LoadCompletionDecision.LoadTimeout
                : LoadCompletionDecision.StillWaiting;
        }

        /// <summary>The grep-stable Revert to VAB / SPH completion line.</summary>
        internal static string FormatPrelaunchCompleteLine(string facility, string ship, double elapsedSeconds)
            => "reverttolaunch complete target=prelaunch facility=" + (facility ?? string.Empty)
               + " ship='" + (ship ?? string.Empty) + "'"
               + " elapsed=" + elapsedSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s";

        /// <summary>The Revert to VAB / SPH OK payload, in wire order.</summary>
        internal static List<KeyValuePair<string, string>> BuildPrelaunchCompletePayload(
            string scene, string facility, string ship)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("scene", scene ?? string.Empty),
                new KeyValuePair<string, string>("facility", facility ?? string.Empty),
                new KeyValuePair<string, string>("ship", ship ?? string.Empty),
            };

        /// <summary>The pre-press gate, in order: no live Re-Fly marker, then the three
        /// conditions stock's Esc menu offers Revert to Launch under. Null to proceed.</summary>
        internal static string DecideGate(bool reFlyMarkerLive, bool canRevertToPostInit,
            bool hasPostInitState, bool canRestart)
        {
            if (reFlyMarkerLive) return ReFlySessionReason;
            if (!canRevertToPostInit || !hasPostInitState || !canRestart) return UnavailableReason;
            return null;
        }

        /// <summary>
        /// The completion decision. The scene the revert starts from is itself a settled FLIGHT
        /// with an unpacked active vessel, so OK additionally needs the NEW scene's flight
        /// readiness (<paramref name="flightReadyForThisRevert"/>: a <c>ParsekFlight</c> other
        /// than the starting one that observed its <c>onFlightReady</c>) and an unpacked active
        /// vessel: a terminal that read the dying scene, or a scene whose post-load passes had
        /// not run, would hand the next step the wrong flight. Only OK / StillWaiting /
        /// LoadFailedMenu / LoadTimeout come back (the readiness wait is folded in).
        /// </summary>
        internal static LoadCompletionDecision DecideCompletion(
            double elapsedSeconds, TestCommandScene scene, bool gameLoaded,
            bool flightReadyForThisRevert, bool activeVesselUnpacked, double budgetSeconds)
        {
            if (scene == TestCommandScene.Flight && gameLoaded
                && (!flightReadyForThisRevert || !activeVesselUnpacked))
                return elapsedSeconds >= budgetSeconds
                    ? LoadCompletionDecision.LoadTimeout
                    : LoadCompletionDecision.StillWaiting;
            return TestCommandLoadGame.DecideLoadCompletion(
                elapsedSeconds, scene, gameLoaded, budgetSeconds, TestCommandScene.Flight);
        }

        /// <summary>The start line (before the stock call).</summary>
        internal static string FormatStartLine(uint pid, string name, string situation,
            bool recording, bool hasActiveTree, bool prelaunch = false)
            => "reverttolaunch start " + (prelaunch ? "target=" + PrelaunchToken + " " : string.Empty)
               + "pid=" + pid.ToString(CultureInfo.InvariantCulture)
               + " name='" + (name ?? string.Empty) + "'"
               + " situation=" + (situation ?? string.Empty)
               + " recording=" + (recording ? "true" : "false")
               + " hasActiveTree=" + (hasActiveTree ? "true" : "false");

        /// <summary>The grep-stable completion line.</summary>
        internal static string FormatCompleteLine(uint pid, string name, string situation,
            double elapsedSeconds)
            => "reverttolaunch complete pid=" + pid.ToString(CultureInfo.InvariantCulture)
               + " name='" + (name ?? string.Empty) + "'"
               + " situation=" + (situation ?? string.Empty)
               + " elapsed=" + elapsedSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s";

        /// <summary>The OK payload, in wire order.</summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(
            string scene, uint activePid, string activeName, string situation)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("scene", scene ?? string.Empty),
                new KeyValuePair<string, string>("activePid", activePid.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("activeName", activeName ?? string.Empty),
                new KeyValuePair<string, string>("situation", situation ?? string.Empty),
            };
    }
}
