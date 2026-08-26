using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>The outcome of one map-view toggle attempt, as the pure decision sees it.</summary>
    internal enum MapViewToggleOutcome
    {
        /// <summary>No <c>MapView</c> instance exists in this scene (<c>MapView.fetch</c> is
        /// null), so the call could not even be attempted: REJECTED
        /// <c>map-view-unavailable</c>.</summary>
        Unavailable,

        /// <summary>The map was ALREADY in the requested state. Terminal OK with the
        /// idempotency flag set; the stock call is deliberately not made.</summary>
        AlreadyInState,

        /// <summary>The call ran and the read-back confirms the requested state: terminal
        /// OK.</summary>
        Changed,

        /// <summary>The call ran and the read-back still disagrees - stock declined
        /// (constant-map mode, <c>CanUseMap</c> off, or a MissionSystem camera-switch
        /// block). REJECTED with the per-direction reason.</summary>
        Refused,
    }

    /// <summary>
    /// Pure decision / payload half of the SINGLE-PHASE <c>EnterMapView</c> and
    /// <c>ExitMapView</c> verbs (M-A2 command seam). The applier partial
    /// (<c>ParsekTestCommandAddon.MapView.cs</c>) owns the two Unity-coupled calls -
    /// <c>MapView.EnterMapView()</c> / <c>MapView.ExitMapView()</c> plus the
    /// <c>MapView.MapIsEnabled</c> read-back - and nothing else, so the response shape a
    /// harness spec pins is xUnit-covered without KSP.
    ///
    /// <para>
    /// WHY THESE VERBS EXIST. Everything Parsek draws on the map surface is gated on the map
    /// being OPEN, and no seam verb ever opened it. The consequence was measured, not
    /// guessed: `RC-OWN-DRAW-HALF-IS-MAP-GATED` (todo-and-known-bugs.md) recorded that
    /// `GhostTrajectoryPolylineRenderer.Driver.LateUpdate`'s second statement is
    /// <c>if (!MapView.MapIsEnabled) return;</c>, so on EVERY manifest lane flown to date the
    /// ownership-publish half of the render pipeline never ran once - zero
    /// <c>Polyline frame:</c> summaries across a whole flight - while the INTENT half, driven
    /// from ParsekFlight's per-frame update, ran map-open or not. A render-composition lane
    /// that never opens the map therefore measures intent and calls it a draw.
    /// </para>
    ///
    /// <para>
    /// SINGLE-PHASE by construction, and this is a decompile fact rather than an assumption.
    /// KSP 1.12.5's <c>MapView.EnterMapView()</c> delegates to the instance
    /// <c>enterMapView()</c>, whose body assigns <c>MapIsEnabled = true</c> and fires
    /// <c>GameEvents.OnMapEntered</c> SYNCHRONOUSLY before returning (the deferred
    /// <c>Invoke("endEnterMapTransition", transitionDuration)</c> that follows only disables
    /// the UI cameras - it does not gate <c>MapIsEnabled</c>). <c>exitMapView()</c> is the
    /// mirror: its FIRST statement is <c>MapIsEnabled = false</c>. So the very property the
    /// polyline Driver gates on is truthfully readable on the line after the call, there is
    /// nothing to wait for, and a deferred shape here would invent a wait that does not
    /// exist (the MissionConfig / SimulateStockSwitchClick shape, no <c>TryComplete*</c>
    /// counterpart). Every stock refusal path is synchronous too, so a false read-back is a
    /// final answer, never a not-yet.
    /// </para>
    ///
    /// <para>
    /// NO ARGS in v1. Camera focus / zoom were considered and deliberately left out: the
    /// draw-half evidence a lane reads is the manifest plus the tracer log lines, and neither
    /// is a function of where the map camera points. Adding an arg that no consumer reads
    /// would put an untested surface into a gated seam.
    /// </para>
    /// </summary>
    internal static class TestCommandMapViewVerbs
    {
        /// <summary>
        /// PRE-CALL gate: no <c>MapView</c> instance exists in the current scene, so stock
        /// was never called. Shared by both directions - "there is no MapView here" is the
        /// same fact whichever way the spec asked. Verdict <c>REJECTED</c> (the
        /// <c>SimulateStockSwitchClick</c> <c>scenario-not-ready</c> class). NOT a defer: the
        /// dispatcher's <c>RequiresFlight</c> precondition already waits for FLIGHT to
        /// settle, so a scene that IS in flight and still has no MapView is a state this verb
        /// does not drive, not a not-yet.
        /// </summary>
        internal const string MapViewUnavailableReason = "map-view-unavailable";

        /// <summary>
        /// POST-CALL terminal: <c>EnterMapView</c> ran and the read-back is still closed.
        /// Stock declines silently on three paths - <c>MapView.fetch.ConstantMode</c>,
        /// <c>MissionSystem.AllowCameraSwitch(CameraMode.Map)</c> false (which posts its own
        /// screen message), and <c>HighLogic.CurrentGame.Parameters.Flight.CanUseMap</c>
        /// false - so the read-back is the only honest verdict source. The refusal CLASS is
        /// not re-derived here: naming a cause we did not observe could disagree with the one
        /// that actually fired (the <c>EnterWatchMode</c> discipline).
        ///
        /// <para>Verdict <c>ERROR</c>, not <c>REJECTED</c>, and the line is drawn where the
        /// two nearest precedents draw it: <c>SimulateStockSwitchClick</c> answers REJECTED
        /// for every gate it evaluates BEFORE calling stock and ERROR for
        /// <c>switch-refused-by-stock</c> / <c>switch-threw</c> AFTER, and
        /// <c>EnterWatchMode</c>'s post-call <c>watch-not-entered</c> is ERROR too. A
        /// REJECTED here would claim we declined to act; we acted and the game
        /// declined.</para>
        /// </summary>
        internal const string MapNotEnteredReason = "map-not-entered";

        /// <summary>
        /// POST-CALL terminal: <c>ExitMapView</c> ran and the read-back is still open (the
        /// <c>ConstantMode</c> path is the mirror decline). Verdict <c>ERROR</c>, same
        /// reasoning as <see cref="MapNotEnteredReason"/>.
        /// </summary>
        internal const string MapNotExitedReason = "map-not-exited";

        /// <summary>
        /// POST-CALL terminal for the one thing the read-back cannot describe: the stock call
        /// THREW. Shared by both directions and kept distinct from the silent-decline tokens
        /// for exactly the reason <c>SimulateStockSwitchClick</c> keeps <c>switch-threw</c>
        /// apart from <c>switch-refused-by-stock</c> - a throw is a different investigation
        /// from a policy decline. Verdict <c>ERROR</c>.
        /// </summary>
        internal const string MapViewThrewReason = "map-view-threw";

        /// <summary>
        /// The whole decision, both directions, from four sampled booleans.
        ///
        /// <para>ORDER MATTERS. Availability first (a null <c>MapView.fetch</c> makes every
        /// other reading meaningless), then the idempotent short-circuit, then the
        /// read-back. <paramref name="openAfter"/> is ignored on the first two branches, so
        /// the applier may pass <paramref name="openBefore"/> for it when it never made the
        /// call.</para>
        /// </summary>
        /// <param name="mapViewPresent">Whether a <c>MapView</c> instance exists
        /// (<c>MapView.fetch != null</c>).</param>
        /// <param name="wantOpen">True for <c>EnterMapView</c>, false for
        /// <c>ExitMapView</c>.</param>
        /// <param name="openBefore"><c>MapView.MapIsEnabled</c> sampled before the
        /// call.</param>
        /// <param name="openAfter"><c>MapView.MapIsEnabled</c> sampled after it.</param>
        internal static MapViewToggleOutcome DecideToggleOutcome(
            bool mapViewPresent, bool wantOpen, bool openBefore, bool openAfter)
        {
            if (!mapViewPresent)
                return MapViewToggleOutcome.Unavailable;
            if (openBefore == wantOpen)
                return MapViewToggleOutcome.AlreadyInState;
            if (openAfter == wantOpen)
                return MapViewToggleOutcome.Changed;
            return MapViewToggleOutcome.Refused;
        }

        /// <summary>
        /// The refusal reason for a non-OK outcome, or null when the outcome is a success
        /// (<see cref="MapViewToggleOutcome.Changed"/> /
        /// <see cref="MapViewToggleOutcome.AlreadyInState"/>). Keeping the mapping here
        /// rather than in the applier is what lets an xUnit cell pin that the two directions
        /// never share a decline token - a spec asserting <c>map-not-entered</c> must never
        /// match an exit that declined.
        /// </summary>
        internal static string RefusalReason(MapViewToggleOutcome outcome, bool wantOpen)
        {
            if (outcome == MapViewToggleOutcome.Unavailable)
                return MapViewUnavailableReason;
            if (outcome == MapViewToggleOutcome.Refused)
                return wantOpen ? MapNotEnteredReason : MapNotExitedReason;
            return null;
        }

        /// <summary>
        /// The wire VERDICT for a non-OK outcome: <c>REJECTED</c> only for the pre-call
        /// availability gate, <c>ERROR</c> once stock has actually been called and declined.
        /// Returns null for the two success outcomes.
        ///
        /// <para>This split IS the contract a spec's <c>expect</c> is written against, which
        /// is why it lives in the pure half with a cell on it rather than as two string
        /// literals in the applier.</para>
        /// </summary>
        internal static string RefusalVerdict(MapViewToggleOutcome outcome)
        {
            if (outcome == MapViewToggleOutcome.Unavailable)
                return "REJECTED";
            if (outcome == MapViewToggleOutcome.Refused)
                return "ERROR";
            return null;
        }

        /// <summary>
        /// OK payload for <c>EnterMapView</c>: the flat state claim the verdict stands behind
        /// plus the idempotency flag. <c>alreadyOpen</c> is ALWAYS present (both values), not
        /// present-only-when-true like <c>StartRecording</c>'s <c>already</c>: a lane reading
        /// this verb is deciding whether ITS step is what opened the map, and an absent key
        /// would make "no, it was already open" indistinguishable from an older seam build.
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildEnterPayload(bool alreadyOpen)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("mapOpen", "true"),
                new KeyValuePair<string, string>("alreadyOpen", alreadyOpen ? "true" : "false"),
            };

        /// <summary>OK payload for <c>ExitMapView</c>: the mirror of
        /// <see cref="BuildEnterPayload"/>.</summary>
        internal static List<KeyValuePair<string, string>> BuildExitPayload(bool alreadyClosed)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("mapOpen", "false"),
                new KeyValuePair<string, string>("alreadyClosed", alreadyClosed ? "true" : "false"),
            };
    }
}
