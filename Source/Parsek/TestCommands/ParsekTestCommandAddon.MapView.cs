using System;

namespace Parsek.TestCommands
{
    /// <summary>
    /// M-A2 partial: the thin Unity applier for the two SINGLE-PHASE map-view verbs,
    /// <c>EnterMapView</c> and <c>ExitMapView</c>. Every decision is delegated to the pure
    /// sibling <see cref="TestCommandMapViewVerbs"/>; this file only samples
    /// <c>MapView.fetch</c> / <c>MapView.MapIsEnabled</c>, makes the one stock call, and
    /// stashes the verdict.
    ///
    /// <para>
    /// WHY. See the pure half's header for the full statement. In one line: the polyline
    /// Driver's publish walk bails on <c>if (!MapView.MapIsEnabled) return;</c>, so a
    /// render-composition lane that never opens the map measures the INTENT half of
    /// ownership and never the DRAW half (RC-OWN-DRAW-HALF-IS-MAP-GATED).
    /// </para>
    ///
    /// <para>
    /// WHY THE EXIT MIRROR SHIPS WITH IT. Not for teardown - <c>FlushAndQuit</c> saves and
    /// quits regardless of camera mode, and nothing about the manifest's scene-exit flush
    /// cares whether the map was open. It ships because a map-open lane wants to CLOSE the
    /// map again before flight-scene steps whose behaviour differs under the map overlay:
    /// <c>SimulateStockSwitchClick</c> already has to call <c>MapView.ExitMapView()</c>
    /// itself after switching, and <c>EnterWatchMode</c> drives the flight camera. Without
    /// the mirror, a lane could open the map and never get back.
    /// </para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private void EnterMapViewImpl(ParsedCommand cmd) => MapViewToggleImpl(wantOpen: true);

        private void ExitMapViewImpl(ParsedCommand cmd) => MapViewToggleImpl(wantOpen: false);

        // One body for both directions: the sampled booleans and the terminal shape are
        // identical, and only the stock call and the payload builder differ. Two copies of
        // this would be two places for the read-back discipline to drift.
        private void MapViewToggleImpl(bool wantOpen)
        {
            string verb = wantOpen ? "entermapview" : "exitmapview";

            // MapView is a UnityEngine.Object reference: compare through the Unity
            // lifetime-aware operator (a destroyed instance is "null" here and a plain
            // reference check would miss it).
            bool present = MapView.fetch != null;
            bool openBefore = present && MapView.MapIsEnabled;
            bool openAfter = openBefore;

            if (present && openBefore != wantOpen)
            {
                try
                {
                    if (wantOpen) MapView.EnterMapView();
                    else MapView.ExitMapView();
                }
                catch (Exception ex)
                {
                    // The stock call is void and its decline paths all return quietly, so a
                    // THROW is the one thing the read-back cannot describe. Kept as its OWN
                    // token (the SimulateStockSwitchClick switch-threw / switch-refused-by-
                    // stock split): a throw is a different investigation from a policy
                    // decline, and collapsing them would hide that.
                    ParsekLog.Error(Tag,
                        $"{verb} threw {ex.GetType().Name}: {ex.Message}");
                    SetExecResult("ERROR", null, TestCommandMapViewVerbs.MapViewThrewReason);
                    return;
                }

                // Read the SAME property the polyline Driver's LateUpdate gate reads. Stock
                // assigns it inside the call (decompile note in the pure half), so this is a
                // final answer rather than a first sample of a settling value.
                openAfter = MapView.MapIsEnabled;
            }

            MapViewToggleOutcome outcome = TestCommandMapViewVerbs.DecideToggleOutcome(
                present, wantOpen, openBefore, openAfter);

            if (outcome == MapViewToggleOutcome.Unavailable
                || outcome == MapViewToggleOutcome.Refused)
            {
                // REJECTED only for the pre-call availability gate; ERROR once stock has
                // actually been called and declined. Both come from the pure half so the
                // contract a spec's `expect` is written against has a cell on it.
                string reason = TestCommandMapViewVerbs.RefusalReason(outcome, wantOpen);
                string verdict = TestCommandMapViewVerbs.RefusalVerdict(outcome);
                ParsekLog.Warn(Tag, $"{verb} {verdict} reason={reason} present={Bool(present)} "
                    + $"openBefore={Bool(openBefore)} openAfter={Bool(openAfter)}");
                SetExecResult(verdict, null, reason);
                return;
            }

            bool already = outcome == MapViewToggleOutcome.AlreadyInState;
            ParsekLog.Info(Tag, $"{verb} ok mapOpen={Bool(wantOpen)} already={Bool(already)}");
            SetExecResult("OK", wantOpen
                ? TestCommandMapViewVerbs.BuildEnterPayload(already)
                : TestCommandMapViewVerbs.BuildExitPayload(already), null);
        }
    }
}
