using System.Collections.Generic;
using System.Globalization;

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
                CaptureRevertTargetPids(FlightDriver.PostInitState, RevertKind.Launch);
                ParsekLog.Info("RevertDetector",
                    "GameEvents.OnRevertToLaunchFlightState fired; armed RevertKind.Launch for next OnLoad");
            }

            public void OnRevertToPrelaunch(FlightState _)
            {
                pending = RevertKind.Prelaunch;
                CaptureRevertTargetPids(FlightDriver.PreLaunchState, RevertKind.Prelaunch);
                ParsekLog.Info("RevertDetector",
                    "GameEvents.OnRevertToPrelaunchFlightState fired; armed RevertKind.Prelaunch for next OnLoad");
            }
        }

        private static readonly Handlers handlers = new Handlers();

        internal static RevertKind PendingKind => pending;

        // BUG-H: pids of vessels in the revert TARGET (the launch/prelaunch quicksave Parsek is
        // reverting to). The stock revert events fire with HighLogic.CurrentGame.flightState — the
        // state being LEFT, not the target — so the target vessels are read from the GameBackup
        // (FlightDriver.PreLaunchState for Prelaunch, PostInitState for Launch) at arm time, when
        // those statics are valid. A vessel present here pre-existed the reverted launch and must
        // never be stripped. Consumed and cleared by ParsekScenario.OnLoad's revert path.
        private static HashSet<uint> revertTargetVesselPids;

        /// <summary>
        /// Reads the revert-target vessel pids from the GameBackup that KSP is reverting to and
        /// stores them for the next OnLoad. Logs (and leaves the whitelist null) when the snapshot
        /// is unavailable or empty so the strip fails closed rather than treating "no scope" as
        /// "strip everything".
        /// </summary>
        private static void CaptureRevertTargetPids(GameBackup backup, RevertKind kind)
        {
            if (backup == null)
            {
                revertTargetVesselPids = null;
                ParsekLog.Warn("RevertDetector",
                    $"Revert ({kind}): no GameBackup target available — revert vessel strip will fail closed (no scope)");
                return;
            }

            revertTargetVesselPids = BuildRevertTargetWhitelist(backup.Config);
            if (revertTargetVesselPids == null)
                ParsekLog.Warn("RevertDetector",
                    $"Revert ({kind}): parsed 0 vessel pids from revert-target snapshot — " +
                    "revert vessel strip will fail closed (no scope)");
            else
                ParsekLog.Info("RevertDetector",
                    $"Revert ({kind}): captured {revertTargetVesselPids.Count} revert-target vessel pid(s) as the pre-existing scope whitelist");
        }

        /// <summary>
        /// Builds the revert-target vessel-pid whitelist from a saved game-state ConfigNode, or
        /// returns <c>null</c> when the snapshot yields no vessels. A launch/prelaunch snapshot always
        /// contains at least the launch vessel, so an empty parse means the Config layout was not what
        /// we expected — returning null makes the revert strip fail closed (strip nothing) rather than
        /// treating "no scope" as "strip everything". Pure / testable.
        /// </summary>
        internal static HashSet<uint> BuildRevertTargetWhitelist(ConfigNode gameStateConfig)
        {
            var pids = ExtractFlightStateVesselPids(gameStateConfig);
            return pids.Count > 0 ? pids : null;
        }

        /// <summary>
        /// Extracts vessel persistentIds from a saved game-state ConfigNode (GameBackup.Config from
        /// <c>Game.Save</c>: root -&gt; GAME -&gt; FLIGHTSTATE -&gt; VESSEL[] each carrying
        /// <c>persistentId</c>). Defensive about whether the GAME wrapper is present. Pure / testable.
        /// </summary>
        internal static HashSet<uint> ExtractFlightStateVesselPids(ConfigNode gameStateConfig)
        {
            var pids = new HashSet<uint>();
            if (gameStateConfig == null)
                return pids;

            ConfigNode flightStateNode =
                gameStateConfig.GetNode("GAME")?.GetNode("FLIGHTSTATE")
                ?? gameStateConfig.GetNode("FLIGHTSTATE");
            if (flightStateNode == null)
                return pids;

            foreach (ConfigNode vesselNode in flightStateNode.GetNodes("VESSEL"))
            {
                string raw = vesselNode.GetValue("persistentId");
                if (uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pid)
                    && pid != 0)
                {
                    pids.Add(pid);
                }
            }
            return pids;
        }

        /// <summary>
        /// Returns the captured revert-target whitelist and clears it (one-shot, like the kind).
        /// </summary>
        internal static HashSet<uint> ConsumeRevertTargetVesselPids()
        {
            var pids = revertTargetVesselPids;
            revertTargetVesselPids = null;
            return pids;
        }

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
            revertTargetVesselPids = null;
        }
    }
}
