using System;

namespace Parsek
{
    /// <summary>
    /// Re-enables the flight-scene Esc-menu Revert-to-Launch button while a
    /// re-fly session is active so the player can route into Parsek's
    /// Retry / Discard Re-fly / Cancel dialog (see <see cref="RevertInterceptor"/>).
    ///
    /// <para>
    /// KSP's <c>FlightDriver.Start</c> sets <c>CanRevertToPostInit</c> based on
    /// the active vessel's situation on the <c>RESUME_SAVED_CACHE</c> branch
    /// (true only when <c>vessel.situation == PRELAUNCH</c>; see decompiled
    /// <c>FlightDriver.cs:386-387</c>). When <see cref="RewindInvoker"/> loads
    /// a rewind point's quicksave via <c>FlightDriver.StartAndFocusVessel</c>,
    /// the loaded vessel is mid-flight at the staging point, so KSP correctly
    /// considers the state non-revertable and the pause menu's Revert button
    /// (interactable predicate <c>FlightDriver.CanRevert</c>; see decompiled
    /// <c>PauseMenu.cs:573</c>) grays out — blocking access to
    /// <see cref="RevertInterceptor.Prefix"/> and the re-fly dialog.
    /// </para>
    ///
    /// <para>
    /// While a re-fly marker is active we forcibly set
    /// <c>FlightDriver.CanRevertToPostInit = true</c> so the button is clickable.
    /// This is safe because the Harmony prefix on <c>FlightDriver.RevertToLaunch</c>
    /// short-circuits the stock body before it can dereference the null
    /// <c>PostInitState</c> we never had — every click reaches our 3-option
    /// dialog instead.
    /// </para>
    ///
    /// <para>
    /// <see cref="Apply"/> is idempotent and is invoked at every site where the
    /// marker can change while the player is in flight: on
    /// <c>GameEvents.onFlightReady</c> (covers OnLoad-with-active-marker), right
    /// after <see cref="RewindInvoker.AtomicMarkerWrite"/> (covers in-flight
    /// invocation), and at every marker-clear site reachable from a flight
    /// scene (atomic-write rollback, Retry handler, Discard handler failure
    /// branches). When the marker becomes null after we forced the flag, the
    /// reset branch puts it back: PRELAUNCH if the vessel is still on the pad
    /// (matches the natural state the engine would compute), otherwise false.
    /// We track our own override via <see cref="forcedFlag"/> so we never
    /// clobber a legitimate engine-set value (e.g., a fresh launch that's
    /// genuinely revertable).
    /// </para>
    /// </summary>
    internal static class ReFlyRevertButtonGate
    {
        private const string Tag = "ReFlySession";

        // True iff Parsek's last Apply() forced CanRevertToPostInit from false
        // to true. Cleared in the reset branch (marker cleared post-force) and
        // by ResetForTesting. We only restore engine-default state when this is
        // true, so a normal launch that legitimately set the flag (line
        // 835/387 of FlightDriver) is never disturbed.
        private static bool forcedFlag;
        internal static bool ForcedFlagForTesting => forcedFlag;

        // Test seam: when non-null, Apply() routes to this hook instead of
        // touching FlightDriver / FlightGlobals static state. The bool argument
        // is the "marker active" decision Apply computed.
        internal static Action<bool> ApplyForTesting;

        internal static void ResetForTesting()
        {
            ApplyForTesting = null;
            forcedFlag = false;
        }

        // KSP's EventData<T>.EvtDelegate..ctor reads evt.Target.GetType().Name
        // without a null check; a delegate bound to a static method has
        // Target == null and NREs inside GameEvents.*.Add. RevertDetector
        // solved this by routing through an instance singleton — mirror that
        // pattern here. Handler state still lives in static fields, not on
        // the instance.
        private sealed class Handlers
        {
            public void OnFlightReady() => Apply("onFlightReady");
        }

        private static readonly Handlers handlers = new Handlers();
        private static bool subscribed;

        /// <summary>
        /// Wires the GameEvents subscription. Idempotent; safe to call from
        /// every <see cref="ParsekScenario"/> lifecycle.
        /// </summary>
        internal static void Subscribe()
        {
            if (subscribed) return;
            subscribed = true;
            GameEvents.onFlightReady.Add(handlers.OnFlightReady);
            ParsekLog.Verbose(Tag,
                "ReFlyRevertButtonGate: subscribed to GameEvents.onFlightReady");
        }

        internal static void Unsubscribe()
        {
            if (!subscribed) return;
            subscribed = false;
            GameEvents.onFlightReady.Remove(handlers.OnFlightReady);
            ParsekLog.Verbose(Tag,
                "ReFlyRevertButtonGate: unsubscribed from GameEvents.onFlightReady");
        }

        /// <summary>
        /// Re-evaluates the gate against the current re-fly marker state.
        /// <list type="bullet">
        ///   <item><description>Marker active + flag false → force flag true, log Info, remember override.</description></item>
        ///   <item><description>Marker active + flag already true → no-op (engine or earlier Apply already set it).</description></item>
        ///   <item><description>Marker null + we previously forced → restore natural state (PRELAUNCH-true / otherwise-false), log Info, drop override.</description></item>
        ///   <item><description>Marker null + we did not force → no-op (engine value is authoritative).</description></item>
        /// </list>
        /// </summary>
        /// <param name="site">Short identifier of the call-site for log diagnostics
        /// (e.g. <c>"onFlightReady"</c>, <c>"AtomicMarkerWrite"</c>).</param>
        internal static void Apply(string site)
        {
            var scenario = ParsekScenario.Instance;
            bool active = !ReferenceEquals(null, scenario)
                && scenario.ActiveReFlySessionMarker != null;

            var hook = ApplyForTesting;
            if (hook != null)
            {
                hook(active);
                return;
            }

            try
            {
                if (active)
                {
                    if (!FlightDriver.CanRevertToPostInit)
                    {
                        FlightDriver.CanRevertToPostInit = true;
                        forcedFlag = true;
                        string sessionId = scenario.ActiveReFlySessionMarker.SessionId ?? "<no-id>";
                        ParsekLog.Info(Tag,
                            $"ReFlyRevertButtonGate: forced FlightDriver.CanRevertToPostInit=true at {site ?? "(no-site)"} sess={sessionId} — Esc menu Revert button re-enabled for re-fly dialog");
                    }
                    else
                    {
                        ParsekLog.Verbose(Tag,
                            $"ReFlyRevertButtonGate: CanRevertToPostInit already true at {site ?? "(no-site)"} — no override needed");
                    }
                }
                else if (forcedFlag)
                {
                    bool natural = ComputeNaturalCanRevertToPostInit();
                    forcedFlag = false;
                    if (FlightDriver.CanRevertToPostInit != natural)
                    {
                        FlightDriver.CanRevertToPostInit = natural;
                        ParsekLog.Info(Tag,
                            $"ReFlyRevertButtonGate: reset FlightDriver.CanRevertToPostInit={natural} at {site ?? "(no-site)"} — re-fly marker cleared");
                    }
                    else
                    {
                        ParsekLog.Verbose(Tag,
                            $"ReFlyRevertButtonGate: reset cleared override at {site ?? "(no-site)"} — flag already at natural value {natural}");
                    }
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"ReFlyRevertButtonGate.Apply threw at {site ?? "(no-site)"}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Mirrors the engine-side computation FlightDriver.Start would have
        // performed if it ran right now: true on PRELAUNCH, false otherwise.
        // No exceptions allowed — callers are in catch-all blocks already and
        // a defensive false matches the gray-out behaviour the player sees.
        private static bool ComputeNaturalCanRevertToPostInit()
        {
            try
            {
                var v = FlightGlobals.ActiveVessel;
                return v != null && v.situation == Vessel.Situations.PRELAUNCH;
            }
            catch
            {
                return false;
            }
        }
    }
}
