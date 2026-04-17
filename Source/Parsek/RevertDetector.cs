namespace Parsek
{
    /// <summary>
    /// #434: canonical revert signal for <see cref="ParsekScenario.OnLoad"/>. Subscribes to
    /// KSP's <c>GameEvents.OnRevertToLaunchFlightState</c> / <c>OnRevertToPrelaunchFlightState</c>,
    /// which fire synchronously inside <c>FlightDriver.RevertToLaunch</c> /
    /// <c>FlightDriver.RevertToPrelaunch</c> BEFORE <c>HighLogic.LoadScene</c>.
    ///
    /// The previous revert-detection heuristic (savedEpoch &lt; CurrentEpoch, or recording-count
    /// regression) false-positived on an F9 to a pre-revert flight quicksave: the quicksave's
    /// epoch/recording count are legitimately older than the post-revert in-memory state, so the
    /// load was mis-classified as a second revert. Event-based detection consumes a one-shot
    /// flag: the flag is set by the KSP event and cleared by the first OnLoad that reads it.
    /// F9 into an older quicksave after that consumption sees <see cref="RevertKind.None"/>
    /// and runs as a plain quickload resume, preserving the pending tree.
    /// </summary>
    internal enum RevertKind
    {
        None,
        Launch,
        Prelaunch,
    }

    internal static class RevertDetector
    {
        private static RevertKind pending = RevertKind.None;
        private static bool subscribed = false;

        // KSP's EventData<T>.EvtDelegate..ctor reads evt.Target.GetType().Name without a
        // null check. A delegate bound to a static method has Target == null, which NREs
        // inside GameEvents.*.Add and aborts the caller (ParsekScenario.OnLoad). Route
        // handlers through a singleton instance so Target is non-null; handler state
        // still lives in the static `pending` field, not on the instance.
        private sealed class Handlers
        {
            public void OnRevertToLaunch(FlightState _)
            {
                pending = RevertKind.Launch;
                ParsekLog.Info("RevertDetector",
                    "GameEvents.OnRevertToLaunchFlightState fired — armed RevertKind.Launch for next OnLoad");
            }

            public void OnRevertToPrelaunch(FlightState _)
            {
                pending = RevertKind.Prelaunch;
                ParsekLog.Info("RevertDetector",
                    "GameEvents.OnRevertToPrelaunchFlightState fired — armed RevertKind.Prelaunch for next OnLoad");
            }
        }

        private static readonly Handlers handlers = new Handlers();

        internal static RevertKind PendingKind => pending;

        /// <summary>
        /// Wires the KSP <c>GameEvents</c> subscriptions. Idempotent: safe to call from
        /// every ParsekScenario lifecycle if needed. Subscribe once per game instance;
        /// GameEvents is itself a global, so a single subscription covers every scene.
        /// </summary>
        internal static void Subscribe()
        {
            if (subscribed) return;
            subscribed = true;
            GameEvents.OnRevertToLaunchFlightState.Add(handlers.OnRevertToLaunch);
            GameEvents.OnRevertToPrelaunchFlightState.Add(handlers.OnRevertToPrelaunch);
            ParsekLog.Info("RevertDetector", "Subscribed to GameEvents.OnRevertTo{Launch,Prelaunch}FlightState");
        }

        internal static void Unsubscribe()
        {
            if (!subscribed) return;
            subscribed = false;
            GameEvents.OnRevertToLaunchFlightState.Remove(handlers.OnRevertToLaunch);
            GameEvents.OnRevertToPrelaunchFlightState.Remove(handlers.OnRevertToPrelaunch);
            ParsekLog.Info("RevertDetector", "Unsubscribed from GameEvents.OnRevertTo*FlightState");
        }

        /// <summary>
        /// Reads the pending-revert kind and resets it to <see cref="RevertKind.None"/>.
        /// The first OnLoad after a revert sees the armed value; every subsequent OnLoad
        /// (including an F9 into a pre-revert quicksave) sees None and classifies as a
        /// regular load.
        /// </summary>
        internal static RevertKind Consume(string site)
        {
            if (pending == RevertKind.None)
                return RevertKind.None;

            var kind = pending;
            pending = RevertKind.None;
            ParsekLog.Info("RevertDetector",
                $"Consumed pending revert ({kind}) at {site ?? "(no site)"}");
            return kind;
        }

        /// <summary>
        /// Directly set the pending kind (tests only). Production callers must use
        /// <see cref="Subscribe"/> + the GameEvents path.
        /// </summary>
        internal static void SetPendingForTesting(RevertKind kind)
        {
            pending = kind;
        }

        internal static void ResetForTesting()
        {
            pending = RevertKind.None;
        }
    }
}
