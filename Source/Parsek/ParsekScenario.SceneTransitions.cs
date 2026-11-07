using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Scene transition bookkeeping: the editor-revert boundary, the fresh-launch UT, the
    // vessel-switch flag, the main-menu hook and the scene-switch handlers.
    public partial class ParsekScenario
    {
        internal static void SetCurrentFlightLaunchUTForTesting(double ut)
        {
            currentFlightLaunchUT = ut;
        }

        /// <summary>
        /// Resolves the editor-revert prune boundary: the captured fresh-launch UT when available
        /// (site/mode independent), else the VesselRollout action UT (career fallback), else NaN
        /// (handled by <see cref="ResolveRevertPruneCutoff"/>'s loadedUT fallback). Pure for testing.
        /// </summary>
        internal static double ResolveEditorRevertBoundaryUT(double capturedLaunchUT, double rolloutUT)
        {
            return !double.IsNaN(capturedLaunchUT) ? capturedLaunchUT : rolloutUT;
        }

        /// <summary>
        /// Decides what to do with the captured fresh-launch UT (<see cref="currentFlightLaunchUT"/>)
        /// on an OnLoad. Pure so the gating is unit-testable outside the OnLoad lifecycle.
        ///
        /// <para>Only FLIGHT loads that are neither a revert nor a vessel switch touch the static:
        /// a fresh launch (<paramref name="isFreshLaunchStartup"/>) captures <c>loadedUT</c> (the
        /// launch instant); any other flight load (quickload-resume) clears it so a stale value
        /// can't over-prune a later editor revert. Revert loads and non-flight loads return
        /// <see cref="FreshLaunchUtAction.Leave"/> so the captured UT survives until the matching
        /// Revert-to-editor reads it.</para>
        /// </summary>
        /// <summary>
        /// Arms the post-revert spawned-vessel cleanup set, keeping an ALREADY-ARMED set
        /// intact. A rewind arms this set and then zeroes live spawn tracking, so the
        /// revert load that follows collects nothing: overwriting would drop the pids and
        /// names of vessels that are still in the save and leave them uncleaned. The
        /// collector is a delegate so the guard can be driven without a live scene; it is
        /// only invoked when nothing is armed yet, which is also what the guard is for.
        /// </summary>
        internal static void ArmRevertCleanupData(
            Func<(HashSet<uint> pids, HashSet<string> names)> collectSpawnedVesselInfo)
        {
            bool alreadyHasCleanupData = RewindLauncher.PendingCleanupPids != null
                                          || RewindLauncher.PendingCleanupNames != null;
            if (alreadyHasCleanupData)
            {
                ParsekLog.Info("Scenario",
                    $"OnLoad: revert path skipping cleanup collection — " +
                    $"already set ({RewindLauncher.PendingCleanupPids?.Count ?? 0} pid(s), " +
                    $"{RewindLauncher.PendingCleanupNames?.Count ?? 0} name(s)) from prior rewind/revert");
                return;
            }

            var info = collectSpawnedVesselInfo();
            var spawnedPids = info.pids != null && info.pids.Count > 0 ? info.pids : null;
            var spawnedNames = info.names != null && info.names.Count > 0 ? info.names : null;
            RewindLauncher.PendingCleanupPids = spawnedPids;
            RewindLauncher.PendingCleanupNames = spawnedNames;
            ParsekLog.Verbose("Scenario",
                $"OnLoad: revert cleanup collected — " +
                $"{spawnedPids?.Count ?? 0} pid(s), {spawnedNames?.Count ?? 0} name(s)");
        }

        /// <summary>
        /// The OnFlightReady orphaned-vessel recovery is the belt for a revert that lands in
        /// FLIGHT, whose LOADED vessels the protoVessel strip cannot see. A revert to the
        /// editor (Revert to VAB / SPH) lands in EDITOR: every vessel is still a protoVessel,
        /// so the strip above already did the whole job, and an armed set would instead fire
        /// on the NEXT launch with this revert's stale whitelist, recovering a vessel Parsek
        /// legitimately spawned in between (a Space Center Real Spawn). Same release the
        /// rewind path does after its own strip.
        /// </summary>
        internal static bool ReleaseRevertCleanupOutsideFlight(GameScenes loadedScene)
        {
            if (loadedScene == GameScenes.FLIGHT)
                return false;
            bool wasArmed = RewindLauncher.ShouldRunPendingCleanupOnFlightReady()
                || RewindLauncher.PendingRevertPreExistingPids != null;
            RewindLauncher.PendingCleanupPids = null;
            RewindLauncher.PendingCleanupNames = null;
            RewindLauncher.PendingRevertPreExistingPids = null;
            ParsekLog.Info("Scenario",
                $"Revert cleanup released: scene={loadedScene} wasArmed={wasArmed} - the protoVessel " +
                "strip covered every vessel; no OnFlightReady recovery is left armed for a later launch");
            return true;
        }

        internal static FreshLaunchUtAction DecideFreshLaunchUtAction(
            GameScenes loadedScene, bool isRevert, bool isVesselSwitch, bool isFreshLaunchStartup)
        {
            if (loadedScene != GameScenes.FLIGHT || isRevert || isVesselSwitch)
                return FreshLaunchUtAction.Leave;

            return isFreshLaunchStartup
                ? FreshLaunchUtAction.Capture
                : FreshLaunchUtAction.Clear;
        }

        /// <summary>
        /// Called by <see cref="ParsekFlight.OnSceneChangeRequested"/> to stamp
        /// the pre-transition UT. OnLoad reads and consumes this value to
        /// detect F5/F9 quickloads (UT regresses across the transition).
        /// </summary>
        internal static void StampSceneChangeRequestedUT(double ut)
        {
            lastSceneChangeRequestedUT = ut;
        }

        /// <summary>
        /// Pure decision: returns true if the vessel-switch flag should be
        /// interpreted as a fresh vessel switch at scene-load time. Used by
        /// OnLoad's FLIGHT→FLIGHT dispatch and directly testable without Unity.
        /// </summary>
        internal static bool IsVesselSwitchFlagFresh(
            bool pending, int pendingFrame, int currentFrame, int maxAgeFrames)
        {
            if (!pending) return false;
            if (pendingFrame < 0) return false;
            int age = currentFrame - pendingFrame;
            return age >= 0 && age <= maxAgeFrames;
        }

        /// <summary>
        /// The <c>onVesselSwitching</c> handler's decision and side effect, static so it is
        /// testable headless. Arms <see cref="vesselSwitchPending"/> only when the current
        /// flight scene already reached <c>onFlightReady</c>; the scene-entry initial focus
        /// fires before that and is logged and ignored. Returns true when the flag armed.
        /// </summary>
        internal static bool TryArmVesselSwitchFlag(string fromName, string toName, int frame)
        {
            if (!flightSceneReadyForVesselSwitchFlag)
            {
                ParsekLog.Info("Scenario",
                    $"Vessel switch ignored: '{fromName}' -> '{toName}' fired before " +
                    "onFlightReady (scene-entry initial focus, not a player switch) - " +
                    $"vessel-switch flag not armed (frame={frame})");
                return false;
            }
            vesselSwitchPending = true;
            // Time.frameCount is monotonic across scene loads within a single
            // KSP session (Unity only resets it on application restart, not on
            // scene change), so the staleness check in OnLoad can rely on the
            // difference between the stamp here and the frame count at
            // scene-load time being a meaningful "frames elapsed" measurement.
            vesselSwitchPendingFrame = frame;
            ParsekLog.Info("Scenario",
                $"Vessel switch detected: '{fromName}' → '{toName}' — " +
                $"next FLIGHT→FLIGHT OnLoad within {VesselSwitchPendingMaxAgeFrames} frames " +
                $"will skip revert strip/cleanup (frame={vesselSwitchPendingFrame})");
            return true;
        }

        /// <summary>
        /// Called from <c>ParsekFlight.OnFlightReady</c>: from here on an
        /// <c>onVesselSwitching</c> event in this scene is a real focus change.
        /// </summary>
        internal static void NoteFlightSceneReadyForVesselSwitchFlag()
        {
            if (flightSceneReadyForVesselSwitchFlag)
                return;
            flightSceneReadyForVesselSwitchFlag = true;
            ParsekLog.Verbose("Scenario",
                "Vessel-switch flag armed for this flight scene (onFlightReady observed)");
        }

        internal static void ClearFlightSceneReadyForVesselSwitchFlag()
        {
            flightSceneReadyForVesselSwitchFlag = false;
        }

        internal static bool FlightSceneReadyForVesselSwitchFlagForTesting
        {
            get => flightSceneReadyForVesselSwitchFlag;
            set => flightSceneReadyForVesselSwitchFlag = value;
        }

        internal static bool VesselSwitchPendingForTesting
        {
            get => vesselSwitchPending;
            set { vesselSwitchPending = value; if (!value) vesselSwitchPendingFrame = -1; }
        }

        internal static int VesselSwitchPendingFrameForTesting => vesselSwitchPendingFrame;

        /// <summary>
        /// Live query for use during <c>OnSceneChangeRequested</c> (#266): returns true
        /// if the in-flight <see cref="vesselSwitchPending"/> flag was set within the
        /// freshness window. Unlike <see cref="IsVesselSwitchFlagFresh"/>, this is the
        /// runtime accessor — it does NOT consume the flag, and it reads the live
        /// <c>UnityEngine.Time.frameCount</c>. Used by <c>FinalizeTreeOnSceneChange</c>
        /// to decide between the legacy Limbo stash and the bug #266 pre-transition
        /// path.
        /// </summary>
        internal static bool IsVesselSwitchPendingFresh()
        {
            return IsVesselSwitchFlagFresh(
                vesselSwitchPending,
                vesselSwitchPendingFrame,
                UnityEngine.Time.frameCount,
                VesselSwitchPendingMaxAgeFrames);
        }

        /// <summary>
        /// Subscribes the main-menu session-reset hook (<see cref="OnMainMenuTransition"/>),
        /// which resets <c>initialLoadDone</c> and clears pending-cleanup / playback-scope
        /// state when returning to the main menu so stale in-memory recordings don't leak
        /// into a new save (e.g. deleting a career and creating a new one with the same
        /// name). Idempotent (Remove then Add), mirroring
        /// <see cref="SubscribeVesselLifecycleEvents"/>; <see cref="OnDestroy"/> removes the
        /// subscription on scenario teardown.
        ///
        /// <see cref="OnMainMenuTransition"/> MUST stay an instance method: KSP's
        /// <c>EventData&lt;T&gt;.Add</c> builds an <c>EvtDelegate</c> whose ctor
        /// unconditionally runs <c>originatorType = evt.Target.GetType().Name</c>. For a
        /// <c>static</c> handler <c>evt.Target</c> is null, so <c>Add</c> throws
        /// <c>NullReferenceException</c>. That throw is DETERMINISTIC, not a transient
        /// "GameEvents not ready" condition, so the old try/catch + register-once flag
        /// silently swallowed every registration and this hook never fired (dormant since
        /// the handler was made static).
        /// </summary>
        private void RegisterMainMenuHook()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnMainMenuTransition);
            GameEvents.onGameSceneLoadRequested.Add(OnMainMenuTransition);
        }

        /// <summary>
        /// Resets session state when transitioning to main menu, preventing stale
        /// in-memory recordings from leaking between saves with the same name.
        ///
        /// MUST be an instance method (never <c>static</c>): KSP's
        /// <c>EventData&lt;T&gt;.Add</c> dereferences <c>evt.Target</c> (null for a
        /// static handler) in the <c>EvtDelegate</c> ctor and throws
        /// <c>NullReferenceException</c>. Subscribed from
        /// <see cref="RegisterMainMenuHook"/>; unsubscribed in <see cref="OnDestroy"/>.
        /// See <see cref="RegisterMainMenuHook"/> for the full rationale.
        /// </summary>
        private void OnMainMenuTransition(GameScenes newScene)
        {
            if (newScene == GameScenes.MAINMENU)
            {
                // The game being unloaded ends any window in which an in-game batch's
                // persistent.sfs restore was still the intended final state of a save, so
                // a later FlushAndQuit must save normally again.
                InGameTests.InGameTestRunner.NotifyStateMutatedAfterBatchBaselineRestore(
                    "main-menu transition");
                initialLoadDone = false;
                committedTreeStateLoaded = false;
                lastSaveFolder = null;
                lastOnSaveScene = GameScenes.MAINMENU;
                lastSceneChangeRequestedUT = -1.0;
                RewindLauncher.PendingCleanupPids = null;
                RewindLauncher.PendingCleanupNames = null;
                RewindLauncher.PendingRevertPreExistingPids = null;
                // BUG-B: drop per-recording replay-scope latches on game unload so a
                // later save's recordings start out historical (dormant) rather than
                // inheriting a stale "in replay scope" mark from a previous game.
                PlaybackScopeTracker.Reset();
                // M4b Phase B2 (plan D11 / OQ7): drop the RAM-only cargo escrow on
                // game unload, mirroring the other RAM-cache resets here. A multi-stop
                // cycle's reservation is recomputed from pending route state on the
                // next RouteOrchestrator.Tick (the B3 C1 dispatchAlready-resume
                // re-establish); the single-stop path has no gap to recompute. Either
                // way the escrow must not survive into a different save's logistics
                // state. DROP-not-revert (no ledger row to reverse).
                RouteStore.ClearAllEscrow("main-menu-transition");
                // A handoff captured before this reset would otherwise wait for the next game's
                // load (which is cold anyway and would drop it there).
                InSessionStagedStateHandoff.Drop(InSessionStagedStateHandoff.DropMainMenu);
                // An announced save-file load that never reached an OnLoad belongs to the game
                // being unloaded; the next game's own load arms its own.
                SaveFileLoadIntent.Clear("main-menu");
                ParsekLog.Info("Scenario",
                    "Main menu transition — reset initialLoadDone to prevent stale data leak");
            }

            // Flush any unclaimed recovery-funds callbacks at every scene switch.
            // Recovery callbacks deferred in the old scene (FLIGHT teardown, tracking
            // station recovery) cannot pair with a FundsChanged(VesselRecovery) event
            // captured after the scene transition — stock recovery bookkeeping is
            // scoped to the scene that fired onVesselRecovered. Calling this
            // unconditionally (cheap no-op when the queue is empty) keeps the
            // staleness eviction running on the broadest lifecycle boundary
            // LedgerOrchestrator can observe.
            //
            // REACTIVATION NOTE (PR #1180 review): this handler was dormant ~3 months
            // (the static-handler NRE swallowed every RegisterMainMenuHook registration)
            // until the static->instance fix, so this per-scene-switch flush now runs in
            // normal play for the first time. FlushStalePendingRecoveryFunds clears the
            // WHOLE pending queue, not just age-stale entries; the pairing-can't-straddle-
            // a-scene-boundary argument above is why it is believed safe, but the standing
            // verification is a career recover-then-immediately-switch-scene playtest (see
            // docs/dev/todo-and-known-bugs.md) before fully relying on it. If a legit
            // recovery is ever evicted, scope this flush to MAINMENU rather than every scene.
            LedgerOrchestrator.FlushStalePendingRecoveryFunds(
                $"scene switch to {newScene}");
            RecoveryPayoutContextStore.Clear($"scene switch to {newScene}");
        }

        private void OnVesselSwitching(Vessel from, Vessel to)
        {
            TryArmVesselSwitchFlag(from?.vesselName, to?.vesselName, UnityEngine.Time.frameCount);
        }

        /// <summary>
        /// M4b Phase B2 (plan D11 / OQ7): drop the RAM-only cargo escrow on any
        /// within-game scene change. A multi-stop cycle's in-flight reservation is
        /// recomputed from pending route state on the next
        /// <see cref="RouteOrchestrator.Tick(double)"/> - its pre-pass re-establishes the
        /// un-fired windows' hold before any route runs
        /// (<see cref="RouteOrchestrator.ReEstablishInFlightCycleEscrow"/>); the
        /// single-stop path has no dispatch-to-debit gap. Clearing here prevents a stale
        /// craft-baked-pid reservation from mis-gating a competing route after the
        /// scene reloads. DROP-not-revert (no ledger row to reverse).
        ///
        /// MUST be an instance method: KSP's <c>EventData&lt;T&gt;.Add</c> builds an
        /// <c>EvtDelegate</c> whose ctor unconditionally runs
        /// <c>originatorType = evt.Target.GetType().Name</c>. For a <c>static</c> handler
        /// <c>evt.Target</c> is null, so <c>Add</c> throws <c>NullReferenceException</c>.
        /// Subscribed from <see cref="SubscribeVesselLifecycleEvents"/> during
        /// <see cref="OnLoad"/>, so a static handler here aborts the whole OnLoad before
        /// recordings load and the next save then writes 0 RECORDING_TREE nodes — a silent
        /// recording-index wipe. Unsubscribed in <see cref="OnDestroy"/>, so the
        /// disposed-instance edge is already handled. The body only makes a static call,
        /// so instance-ness has no behavioral cost.
        /// </summary>
        private void OnGameSceneSwitchClearEscrow(
            GameEvents.FromToAction<GameScenes, GameScenes> action)
        {
            RouteStore.ClearAllEscrow($"scene-switch {action.from}->{action.to}");
        }
    }
}
