namespace Parsek
{
    /// <summary>
    /// #434: canonical revert signal for <see cref="ParsekScenario.OnLoad"/>. Subscribes to
    /// KSP's <c>GameEvents.OnRevertToLaunchFlightState</c> / <c>OnRevertToPrelaunchFlightState</c>,
    /// which fire synchronously inside <c>FlightDriver.RevertToLaunch</c> /
    /// <c>FlightDriver.RevertToPrelaunch</c> BEFORE <c>HighLogic.LoadScene</c>.
    ///
    /// The previous revert-detection heuristic (save-state regression by epoch/recording count)
    /// false-positived on an F9 to a pre-revert flight quicksave: the quicksave's metadata was
    /// legitimately older than the post-revert in-memory state, so the load was mis-classified
    /// as a second revert. Event-based detection consumes a one-shot
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
        private static double pendingLaunchUT = double.NaN;
        private static bool subscribed = false;

        /// <summary>
        /// UT at which the about-to-be-reverted flight launched, captured from the active
        /// vessel at the moment the revert GameEvent fired (still in the FLIGHT scene, before
        /// the scene reload). NaN when no active vessel was available.
        ///
        /// <para>Revert-to-Launch rewinds the game clock to the launch instant, so the
        /// post-reload UT equals the launch UT and this capture is redundant there. Revert to
        /// the editor (VAB/SPH) does NOT rewind the clock (the post-reload UT is the revert
        /// moment, after the in-flight actions), so the orphan-ledger prune needs this captured
        /// launch UT instead of the post-reload UT to find the launch boundary.</para>
        /// </summary>
        internal static double PendingLaunchUT => pendingLaunchUT;

        // Captured in the FLIGHT scene before HighLogic.LoadScene runs, so the active vessel is
        // still alive. missionTime is seconds since launch, so GetUniversalTime() - missionTime
        // is the launch UT regardless of game mode (works when a free / sandbox vessel has no
        // VesselRollout ledger row to derive the boundary from).
        private static double CaptureActiveVesselLaunchUT()
        {
            var v = FlightGlobals.ActiveVessel;
            if (v == null || Planetarium.fetch == null)
                return double.NaN;
            return Planetarium.GetUniversalTime() - v.missionTime;
        }

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
                pendingLaunchUT = CaptureActiveVesselLaunchUT();
                ParsekLog.Info("RevertDetector",
                    $"GameEvents.OnRevertToLaunchFlightState fired; armed RevertKind.Launch for next OnLoad (launchUT={pendingLaunchUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)})");
            }

            public void OnRevertToPrelaunch(FlightState _)
            {
                pending = RevertKind.Prelaunch;
                pendingLaunchUT = CaptureActiveVesselLaunchUT();
                ParsekLog.Info("RevertDetector",
                    $"GameEvents.OnRevertToPrelaunchFlightState fired; armed RevertKind.Prelaunch for next OnLoad (launchUT={pendingLaunchUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)})");
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
            return Consume(site, out _);
        }

        /// <summary>
        /// As <see cref="Consume(string)"/>, but also hands back the captured launch UT
        /// (<see cref="PendingLaunchUT"/>) and resets it in the same step, so the kind and its
        /// launch UT are consumed atomically. Callers must use this value rather than reading
        /// <see cref="PendingLaunchUT"/> later, since this resets the field to NaN.
        /// </summary>
        internal static RevertKind Consume(string site, out double launchUT)
        {
            launchUT = pendingLaunchUT;
            pendingLaunchUT = double.NaN;

            if (pending == RevertKind.None)
                return RevertKind.None;

            var kind = pending;
            pending = RevertKind.None;
            ParsekLog.Info("RevertDetector",
                $"Consumed pending revert ({kind}) at {site ?? "(no site)"} " +
                $"(launchUT={launchUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)})");
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

        internal static void SetPendingLaunchUTForTesting(double launchUT)
        {
            pendingLaunchUT = launchUT;
        }

        internal static void ResetForTesting()
        {
            pending = RevertKind.None;
            pendingLaunchUT = double.NaN;
        }
    }
}
