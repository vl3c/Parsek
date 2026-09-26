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
    /// short-circuits the stock body before it can replay the captured
    /// <c>PostInitState</c> (which holds the mid-flight RP-quicksave state, not
    /// a launch-time backup — <c>FlightDriver.PostInit</c> rebuilds it on every
    /// scene load) — every click reaches our 3-option dialog instead.
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
        // true, so a normal launch that legitimately set the flag (NEW_FROM_FILE
        // hits line 835 of decompiled FlightDriver, RESUME_SAVED_CACHE hits line
        // 387) is never disturbed. ComputeNaturalCanRevertToPostInit below
        // mirrors only the RESUME_SAVED_CACHE formula, which is the only branch
        // where forcedFlag could plausibly have been set true beforehand.
        private static bool forcedFlag;
        internal static bool ForcedFlagForTesting => forcedFlag;

        // Test seam: when non-null, Apply() routes to this hook instead of
        // touching FlightDriver / FlightGlobals static state. The bool argument
        // is the "marker active" decision Apply computed.
        internal static Action<bool> ApplyForTesting;

        // S7 (owner ruling 2026-09-26, todo KSP-SETTINGS-AUDIT-2026-09-26): Rewind and
        // Re-Fly ignore the Hard preset's Flight.CanRestart = false, but stock builds the
        // Esc-menu "Revert Flight" button (PauseMenu), its Revert-to-Launch entry
        // (PauseMenu.drawStockRevertOptions) and the flight-results Revert-to-Launch button
        // (FlightResultsDialog) ONLY when Parameters.Flight.CanRestart is true, so without
        // help the re-fly Retry choice (RevertInterceptor -> ReFlyRevertDialog) is
        // unreachable on Hard. While a re-fly session is live in FLIGHT we therefore set
        // the CURRENT game's Flight.CanRestart to true IN MEMORY ONLY:
        //   - every serialization of that FlightParams instance (GamePersistence.SaveGame,
        //     quicksave, autosave, GameBackup / PostInitState, RP quicksave) goes through
        //     GameParameters.ParameterNode.Save, whose Parsek postfix
        //     (Patches/FlightParamsCanRestartPersistPatch) rewrites the saved value back to
        //     False, so the player's preset can never be persisted as True;
        //   - the value is put back on every exit: marker cleared (every Apply site),
        //     any scene-load request (leaving FLIGHT, Retry's reload, main menu), and a
        //     game-object change (the held instance is restored even when orphaned);
        //   - the flip is never taken unless the persistence postfix is verified installed.
        // Only ever forced from False, so the restored value is always False.
        private static GameParameters.FlightParams forcedCanRestartParams;
        internal static bool CanRestartOverrideHeldForTesting => forcedCanRestartParams != null;

        // Test seam: replaces the live Harmony patch-info probe for the persistence guard.
        internal static Func<bool> PersistenceGuardInstalledForTesting;

        internal const string CanRestartValueName = "CanRestart";

        internal static void ResetForTesting()
        {
            ApplyForTesting = null;
            forcedFlag = false;
            forcedCanRestartParams = null;
            PersistenceGuardInstalledForTesting = null;
            // Tear down any leftover GameEvents subscription so a test that
            // called Subscribe() can re-Subscribe() in a follow-up test
            // without double-registration. The Unsubscribe call is a no-op
            // when subscribed=false.
            Unsubscribe();
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

            // S7 exit path: any scene load (leaving FLIGHT, a FLIGHT->FLIGHT reload, main
            // menu) puts Flight.CanRestart back before the next scene reads it. Stock's
            // save-before-exit already ran by now; the persistence postfix covered it.
            public void OnGameSceneLoadRequested(GameScenes scene)
                => ReleaseCanRestartOverride("scene-load-requested:" + scene);
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
            GameEvents.onGameSceneLoadRequested.Add(handlers.OnGameSceneLoadRequested);
            ParsekLog.Verbose(Tag,
                "ReFlyRevertButtonGate: subscribed to GameEvents.onFlightReady + onGameSceneLoadRequested");
        }

        internal static void Unsubscribe()
        {
            if (!subscribed) return;
            subscribed = false;
            GameEvents.onFlightReady.Remove(handlers.OnFlightReady);
            GameEvents.onGameSceneLoadRequested.Remove(handlers.OnGameSceneLoadRequested);
            ParsekLog.Verbose(Tag,
                "ReFlyRevertButtonGate: unsubscribed from GameEvents.onFlightReady + onGameSceneLoadRequested");
        }

        /// <summary>
        /// S7 pure decision: hold the in-memory <c>Flight.CanRestart = true</c> override
        /// only while a re-fly session is live in FLIGHT, the scene is not being left, the
        /// player's own value is False (Hard preset or a custom game), and the save-time
        /// persistence guard is verified installed.
        /// </summary>
        internal static bool ShouldHoldCanRestartOverride(
            bool reFlyActive,
            bool inFlightScene,
            bool leavingScene,
            bool playerCanRestart,
            bool persistenceGuardInstalled)
        {
            return reFlyActive
                && inFlightScene
                && !leavingScene
                && !playerCanRestart
                && persistenceGuardInstalled;
        }

        /// <summary>
        /// S7 core, over plain objects so it runs headless: releases a held override that
        /// no longer applies (restoring False on the HELD instance, current or orphaned),
        /// then forces the current instance when the decision says so. Idempotent.
        /// </summary>
        internal static void ApplyCanRestartOverride(
            GameParameters.FlightParams current,
            bool reFlyActive,
            bool inFlightScene,
            bool leavingScene,
            bool persistenceGuardInstalled,
            string site,
            string sessionId)
        {
            string where = site ?? "(no-site)";
            var held = forcedCanRestartParams;
            bool heldIsCurrent = held != null && ReferenceEquals(held, current);
            // While we hold the current instance its live value is ours; the player's is False.
            bool playerCanRestart = !heldIsCurrent && current != null && current.CanRestart;
            bool want = current != null
                && ShouldHoldCanRestartOverride(
                    reFlyActive, inFlightScene, leavingScene, playerCanRestart, persistenceGuardInstalled);

            if (held != null && !(want && heldIsCurrent))
            {
                held.CanRestart = false;
                forcedCanRestartParams = null;
                string reason = !heldIsCurrent ? "game-parameters-replaced"
                    : leavingScene ? "leaving-scene"
                    : !reFlyActive ? "re-fly-ended"
                    : !inFlightScene ? "not-in-flight"
                    : "guard-missing";
                ParsekLog.Info(Tag,
                    $"ReFlyRevertButtonGate: restored Parameters.Flight.CanRestart=False at {where} reason={reason}");
            }

            if (want && forcedCanRestartParams == null)
            {
                current.CanRestart = true;
                forcedCanRestartParams = current;
                ParsekLog.Info(Tag,
                    $"ReFlyRevertButtonGate: forced Parameters.Flight.CanRestart=True in memory at {where} sess={sessionId ?? "<no-id>"} " +
                    "- stock Revert Flight / Revert to Launch shown for the re-fly Retry dialog; saves still write CanRestart=False");
            }
            else if (!want && reFlyActive && inFlightScene && !leavingScene
                && current != null && !playerCanRestart && !persistenceGuardInstalled)
            {
                ParsekLog.Warn(Tag,
                    $"ReFlyRevertButtonGate: CanRestart=False and the save-time persistence guard is not installed at {where} " +
                    "- not forcing; re-fly Retry reachable only via scene exit + Rewind");
            }
        }

        /// <summary>
        /// S7 save-time guard core: when <paramref name="parameterNode"/> is the instance
        /// holding Parsek's in-memory override, rewrite the just-saved <c>CanRestart</c>
        /// value to False. Returns true when it rewrote.
        /// </summary>
        internal static bool RewritePersistedCanRestart(object parameterNode, ConfigNode node)
        {
            var held = forcedCanRestartParams;
            if (held == null || node == null || !ReferenceEquals(parameterNode, held))
                return false;
            string saved = false.ToString();
            if (!node.SetValue(CanRestartValueName, saved))
                node.AddValue(CanRestartValueName, saved);
            ParsekLog.Info(Tag,
                "ReFlyRevertButtonGate: save wrote Parameters.Flight.CanRestart=False (in-memory re-fly override not persisted)");
            return true;
        }

        /// <summary>Releases the S7 override on an exit path (scene load request).</summary>
        internal static void ReleaseCanRestartOverride(string site)
        {
            if (forcedCanRestartParams == null)
                return;
            try
            {
                ApplyCanRestartOverride(
                    ReadCurrentFlightParamsSafe(),
                    reFlyActive: false, inFlightScene: false, leavingScene: true,
                    persistenceGuardInstalled: false, site: site, sessionId: null);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"ReFlyRevertButtonGate.ReleaseCanRestartOverride threw at {site ?? "(no-site)"}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static GameParameters.FlightParams ReadCurrentFlightParamsSafe()
        {
            try { return ReadCurrentFlightParamsCore(); }
            catch { return null; }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static GameParameters.FlightParams ReadCurrentFlightParamsCore()
        {
            var game = HighLogic.CurrentGame;
            return game != null && game.Parameters != null ? game.Parameters.Flight : null;
        }

        private static bool IsPersistenceGuardInstalled()
        {
            var hook = PersistenceGuardInstalledForTesting;
            if (hook != null)
                return hook();
            return Patches.FlightParamsCanRestartPersistPatch.IsInstalled();
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
                    // Mirror the force-branch ordering: assign the engine
                    // field FIRST, then drop the override-tracking bool. If the
                    // field assignment threw, forcedFlag stays true and the
                    // next Apply gets another chance to reset; if we dropped
                    // forcedFlag first and the assignment threw, the override
                    // would be silently forgotten and the engine flag stuck at
                    // true forever.
                    bool natural = ComputeNaturalCanRevertToPostInit();
                    if (FlightDriver.CanRevertToPostInit != natural)
                    {
                        FlightDriver.CanRevertToPostInit = natural;
                        forcedFlag = false;
                        ParsekLog.Info(Tag,
                            $"ReFlyRevertButtonGate: reset FlightDriver.CanRevertToPostInit={natural} at {site ?? "(no-site)"} — re-fly marker cleared");
                    }
                    else
                    {
                        forcedFlag = false;
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

            // S7: the Hard preset's CanRestart = false hides the button the block above
            // re-enables; see forcedCanRestartParams.
            try
            {
                var current = ReadCurrentFlightParamsSafe();
                if (current != null || forcedCanRestartParams != null)
                {
                    bool inFlight = HighLogic.LoadedScene == GameScenes.FLIGHT;
                    bool guard = active && inFlight && current != null && IsPersistenceGuardInstalled();
                    ApplyCanRestartOverride(
                        current,
                        reFlyActive: active,
                        inFlightScene: inFlight,
                        leavingScene: false,
                        persistenceGuardInstalled: guard,
                        site: site,
                        sessionId: active ? scenario.ActiveReFlySessionMarker.SessionId : null);
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"ReFlyRevertButtonGate.Apply CanRestart override threw at {site ?? "(no-site)"}: {ex.GetType().Name}: {ex.Message}");
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
