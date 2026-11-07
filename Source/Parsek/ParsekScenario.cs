using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// What OnLoad should do with the captured fresh-launch UT used as the editor-revert
    /// orphan-prune boundary. See <see cref="ParsekScenario.DecideFreshLaunchUtAction"/>.
    /// </summary>
    internal enum FreshLaunchUtAction
    {
        /// <summary>Leave the captured UT untouched (revert load, non-flight load).</summary>
        Leave,
        /// <summary>Capture loadedUT as the launch instant (fresh launch from editor).</summary>
        Capture,
        /// <summary>Clear the captured UT (non-fresh flight load, e.g. quickload-resume).</summary>
        Clear,
    }

    /// <summary>
    /// ScenarioModule that persists committed recordings to save games.
    /// Handles OnSave/OnLoad to serialize trajectory data into ConfigNodes.
    /// Also manages crew reservation for deferred vessel spawns.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames,
        GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION, GameScenes.EDITOR)]
    public partial class ParsekScenario : ScenarioModule
    {
        internal const string RewindSpawnSuppressionReasonSameRecording = "same-recording";
        internal const string RewindSpawnSuppressionReasonSameTreeFutureRecording = "same-tree-future-recording";

        private const double RewindSpawnSuppressionUTEpsilon = 1e-3;
        private const double ChainPredecessorRepairUTEpsilon = 1e-3;

        private GameStateRecorder stateRecorder;

        // These collections are persistence-only in Phase 1. Behavior wiring (RP
        // creation, reap, session lifecycle, merge journal finisher) lands in
        // later phases; Phase 1 only guarantees OnSave/OnLoad round-trip and
        // the empty-default on pre-feature saves.
        public List<RewindPoint> RewindPoints = new List<RewindPoint>();
        public List<RecordingSupersedeRelation> RecordingSupersedes = new List<RecordingSupersedeRelation>();
        public List<RecordingRewindRetirement> RecordingRewindRetirements = new List<RecordingRewindRetirement>();
        public List<LedgerTombstone> LedgerTombstones = new List<LedgerTombstone>();

        /// <summary>Singleton; non-null only during an active re-fly session.</summary>
        public ReFlySessionMarker ActiveReFlySessionMarker;

        /// <summary>Singleton; non-null only while an in-game test batch is in
        /// progress. Persisted into persistent.sfs so a mid-batch crash leaves it on
        /// disk for the next OnLoad crash-reconcile finisher. See
        /// <see cref="TestBatchMarker"/> and <see cref="RunTestBatchCrashReconcile"/>.</summary>
        public TestBatchMarker ActiveTestBatchMarker;

        /// <summary>
        /// [M-A3 correction G3] Read-only observable for the H1 autorun settle gate:
        /// true from the moment a crash-reconcile schedules its deferred real reload
        /// (set on the reloadable branch of <see cref="RunTestBatchCrashReconcileCore"/>)
        /// until that reload completes (cleared at the end of
        /// <see cref="DeferredReloadAfterTestBatchCrashReconcile"/>). H1 holds fire
        /// while this is true so an autorun batch never captures its baseline against a
        /// half-reverted save. Static so the DDOL TestRunnerShortcut addon can read it
        /// across the reconcile reload; a failed reload deliberately leaves it set (the
        /// reconcile did not complete, so H1 correctly keeps waiting). The flag only
        /// EXPOSES existing in-flight state; it changes no crash-reconcile behavior.
        /// </summary>
        internal static bool CrashReconcileInProgress { get; private set; }

        /// <summary>Singleton; non-null only during a staged-commit merge.</summary>
        public MergeJournal ActiveMergeJournal;

        // ---------------------------------------------------------------------
        // Switch-segment auto-record (segment-scoped Fly / Switch-To). Phase A.3
        // wires only the storage + serialization; arming sites (Harmony patches
        // in Phase B) and consume sites (Phase C) land later. See
        // docs/dev/plans/segment-scoped-switch-fly-autorecord.md.
        // ---------------------------------------------------------------------

        private StockActionIntentMarker activeStockActionIntent;
        private SwitchSegmentSession activeSwitchSegmentSession;

        // Phase 2 (Rewind-to-Staging): state-version counters consumed by
        // <see cref="EffectiveState"/> to invalidate ERS/ELS caches. Production
        // code bumps these whenever <see cref="RecordingSupersedes"/>,
        // <see cref="RecordingRewindRetirements"/>, or <see cref="LedgerTombstones"/> mutate; the OnLoad path bumps on load
        // (every load invalidates caches). Public field so test helpers can
        // read the counter value for cache-invalidation assertions.
        public int SupersedeStateVersion;
        public int TombstoneStateVersion;

        /// <summary>
        /// Bumps <see cref="SupersedeStateVersion"/>. Called by any code path
        /// that adds / removes / clears entries in <see cref="RecordingSupersedes"/>
        /// or <see cref="RecordingRewindRetirements"/>
        /// so <see cref="EffectiveState.ComputeERS"/> knows to rebuild.
        ///
        /// <para>
        /// Also triggers <see cref="RouteStore.RevalidateSources"/> so route
        /// statuses cannot drift mid-session: every ERS-invalidating bump
        /// implicitly drives route revalidation. The dispatch evaluator's
        /// defensive <c>RouteHasValidSourcesInErs</c> gate would still block
        /// incorrect dispatches if this call were missing — but the UI route
        /// panel would show stale Active / InTransit until save/load. New
        /// callers of <c>BumpSupersedeStateVersion</c> get route reactivity
        /// for free; <see cref="ComputeERS"/> recursion is impossible because
        /// <see cref="RouteStore.RevalidateSources"/> never bumps the version
        /// counter.
        /// </para>
        /// <para>
        /// <paramref name="routeLiveEmitUT"/> (route-timeline events): the
        /// caller-gated live UT for the revalidation pass's auto-pause /
        /// auto-resume ledger markers. The default (-1) keeps the pass silent
        /// (no ledger row), which every load-context and bookkeeping bump site
        /// must use; only a confirmed-live bump (currently the re-fly supersede
        /// commit in <c>SupersedeCommit.FlipMergeStateAndClearTransient</c>)
        /// resolves the UT defensively and passes it so the auto-flip lands on
        /// the timeline. See <see cref="RouteStore.RevalidateSources(string, double)"/>.
        /// </para>
        /// </summary>
        public void BumpSupersedeStateVersion(double routeLiveEmitUT = -1.0)
        {
            unchecked { SupersedeStateVersion++; }

            // Route subsystem reactivity: every ERS-invalidating bump must
            // trigger route revalidation so cached Route.Status doesn't lie
            // until next OnLoad. Wrapped in try/catch so a route-side bug
            // cannot crash the bump path — supersede / retirement bookkeeping
            // is load-bearing for many subsystems.
            try
            {
                RouteStore.RevalidateSources("SupersedeStateVersion-bump", routeLiveEmitUT);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Route",
                    $"RevalidateSources from BumpSupersedeStateVersion threw " +
                    $"{ex.GetType().Name}: {ex.Message}; route statuses may be stale until next OnLoad");
            }
        }

        // Route-timeline events: true while ParsekScenario.OnLoad is on the
        // stack (set/cleared with try/finally). Static because the live-bump
        // helper below may be reached through static seams during load.
        private static bool onLoadInProgress;

        /// <summary>True while <see cref="OnLoad"/> is executing (route-marker load guard).</summary>
        internal static bool IsOnLoadInProgress => onLoadInProgress;

        /// <summary>Test seam for the OnLoad route-marker guard. Tests must reset to false in Dispose.</summary>
        internal static void SetOnLoadInProgressForTesting(bool value)
        {
            onLoadInProgress = value;
        }

        /// <summary>
        /// LIVE variant of <see cref="BumpSupersedeStateVersion(double)"/> for
        /// player-driven ERS mutations (re-fly merge commit, tree discard,
        /// revert/discard handlers, unfinished-flight seal/stash): resolves the
        /// live UT defensively and passes it so the revalidation pass can stamp
        /// auto-pause / auto-resume route-timeline markers. Central load guard:
        /// while <see cref="OnLoad"/> is on the stack the resolved UT is forced
        /// to -1 (silent), so a live site that turns out to be reachable from a
        /// load path can never write a ledger row from load context - on a
        /// scene-change load Planetarium reports a stale nonzero UT, so the UT
        /// resolution alone is not a sufficient guard. Any flip silenced this
        /// way is repaired by the catch-up net on the next genuinely-live pass
        /// (<c>RouteStore.AutoResumeCatchUpReason</c>).
        /// </summary>
        public void BumpSupersedeStateVersionLive()
        {
            BumpSupersedeStateVersion(TryResolveLiveUniversalTimeForRouteMarkers());
        }

        /// <summary>
        /// Defensive live-UT read for route-timeline marker emission: -1 while
        /// OnLoad is in progress or when Planetarium is unavailable (off-Unity
        /// test context), the live UT otherwise. -1 keeps the revalidation
        /// pass silent (the OnLoad shape).
        /// </summary>
        internal static double TryResolveLiveUniversalTimeForRouteMarkers()
        {
            if (onLoadInProgress)
                return -1.0;
            try
            {
                return Planetarium.GetUniversalTime();
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose("Route",
                    $"BumpSupersedeStateVersionLive: live UT resolution threw {ex.GetType().Name}; " +
                    "revalidation runs without timeline markers");
                return -1.0;
            }
        }

        /// <summary>
        /// Bumps <see cref="TombstoneStateVersion"/>. Called by any code path
        /// that adds / removes / clears entries in <see cref="LedgerTombstones"/>
        /// so <see cref="EffectiveState.ComputeELS"/> knows to rebuild.
        /// </summary>
        public void BumpTombstoneStateVersion()
        {
            unchecked { TombstoneStateVersion++; }
        }

        // Phase 2 (Rewind-to-Staging): static accessor for the live scenario
        // module. EffectiveState reads <see cref="RecordingSupersedes"/>,
        // <see cref="RecordingRewindRetirements"/>, <see cref="LedgerTombstones"/>, and <see cref="ActiveReFlySessionMarker"/>
        // through this field. Maintained by OnAwake / OnDestroy so the value
        // tracks whichever ScenarioModule KSP currently owns.
        private static ParsekScenario s_instance;

        /// <summary>
        /// Live scenario module instance (if any). Null outside FLIGHT / KSC /
        /// tracking-station, or during early initialization before
        /// <see cref="OnAwake"/> fires. Consumers must null-check.
        /// </summary>
        public static ParsekScenario Instance => s_instance;

        /// <summary>Clears the <see cref="Instance"/> back-reference. Unit tests only.</summary>
        internal static void ResetInstanceForTesting()
        {
            s_instance = null;
            CurrentTimelineUTProviderForTesting = null;
            currentFlightLaunchUT = double.NaN;
            flightEntryLedgerActionIds = null;
            prelaunchEntryLedgerActionIds = null;
            freshLaunchEntryPending = false;
            lastLoadDroppedRecordingIds = null;
            pendingFreshRecordingAfterDiscard = null;
        }

        internal static Func<double> CurrentTimelineUTProviderForTesting;

        internal static void SetCachedAutoMergeForTesting(bool value)
        {
            cachedAutoMerge = value;
        }

        /// <summary>
        /// Installs a test fixture as <see cref="Instance"/>. Unit tests that
        /// exercise <see cref="EffectiveState"/> use this to inject a scenario
        /// carrying the supersede / tombstone / marker state without needing
        /// the full KSP ScenarioModule lifecycle.
        /// </summary>
        internal static void SetInstanceForTesting(ParsekScenario scenario)
        {
            s_instance = scenario;
        }


        // Vessel switch detection: FLIGHT→FLIGHT transitions from vessel switching
        // are NOT reverts and must not trigger orphan strip/cleanup.
        //
        // The flag is set by <see cref="OnVesselSwitching"/> from KSP's
        // <c>onVesselSwitching</c> GameEvent, which fires on EVERY vessel focus
        // change — including EVAs that stay in the same scene. Tracking-station-
        // style vessel switches trigger a FLIGHT→FLIGHT scene reload within 1-2
        // frames of the event, so the flag's truth at scene-load time correctly
        // identifies those. EVA events, by contrast, set the flag and then
        // leave it sticky for thousands of frames until the next unrelated
        // scene change (quickload, revert, etc.) — at which point the stale
        // flag mis-identifies the quickload as a vessel switch and routes the
        // pending-Limbo tree into FinalizeLimboForRevert instead of the
        // restore-and-resume path. Observed 2026-04-09 playtest (PR #164).
        //
        // Staleness check: capture the frame count on set. In OnLoad, consider
        // the flag "fresh" only if it was set within the last few frames (the
        // maximum between onVesselSwitching and OnLoad for a legitimate
        // tracking-station reload is small — the scene reload is dispatched
        // immediately after the switching event). A stale flag (many frames
        // old, typical of EVA leakage) is ignored.
        private static bool vesselSwitchPending;
        private static int vesselSwitchPendingFrame = -1;

        // FRESH-LAUNCH-JOINS-RESTORED-COMMITTED-TREE: KSP fires onVesselSwitching for the
        // scene's INITIAL focus while a flight scene is still starting up (after OnLoad,
        // before onFlightReady; from=null on a cold boot, from=the previous scene's
        // vessel after a FLIGHT->FLIGHT reload). That event is not a player switch, but
        // it used to arm vesselSwitchPending, so any FLIGHT->FLIGHT scene change within
        // VesselSwitchPendingMaxAgeFrames of scene entry (a mod-driven launch from
        // FLIGHT, an F9 spam) read as a tracking-station switch. The flag now arms only
        // once the current flight scene has reached onFlightReady. Cleared at every
        // OnLoad (which runs before the new scene's initial focus); set by
        // ParsekFlight.OnFlightReady.
        private static bool flightSceneReadyForVesselSwitchFlag;

        /// <summary>
        /// Maximum age (in frames) for <see cref="vesselSwitchPending"/> to be
        /// considered "fresh" in OnLoad. Tracking-station vessel switches
        /// dispatch the scene reload in the same or next frame, so a tight cap
        /// covers the scene-load duration without letting older EVA flag
        /// leakage through. Lowered from 300 to 60 in the Bug A fix: at low
        /// render FPS (~12 fps under loaded KSC), a 24-second EVA walk only
        /// advances ~288 frames and slipped under the old 300-frame cap,
        /// mis-classifying an EVA-then-F9 sequence as a fresh vessel switch.
        /// The tighter cap is the primary defense; <see cref="lastSceneChangeRequestedUT"/>
        /// UT-backwards detection is the secondary defense.
        /// </summary>
        internal const int VesselSwitchPendingMaxAgeFrames = 60;

        /// <summary>
        /// UT captured in <see cref="ParsekFlight.OnSceneChangeRequested"/> just
        /// before the scene tears down. Compared against <c>Planetarium.GetUniversalTime()</c>
        /// in OnLoad — if the loaded UT is strictly less, the player F5/F9
        /// quickloaded (or reverted) and any pending/limbo recording stashed
        /// during this transition must be discarded, regardless of the
        /// vessel-switch flag state. <c>-1.0</c> = unset / already consumed.
        /// Bug A (2026-04-09 playtest).
        /// </summary>
        private static double lastSceneChangeRequestedUT = -1.0;

        /// <summary>
        /// UT at which the current flight launched, captured when OnLoad enters a FLIGHT scene
        /// via a fresh launch (NEW_FROM_FILE / NEW_FROM_CRAFT_NODE) where <c>loadedUT</c> is the
        /// launch instant. This is the launch-boundary the editor-revert orphan prune uses,
        /// independent of launch site (KSC pad/runway, Making History Desert/Woomerang) and game
        /// mode, unlike the vessel's <c>launchTime</c>/<c>missionTime</c> (KSP churns them during
        /// PRELAUNCH) or a VesselRollout ledger action (not recorded at Making History alt sites).
        /// Cleared to NaN on a non-fresh flight load (quickload-resume) so a stale value can't
        /// over-prune. <c>NaN</c> = not captured this session.
        /// </summary>
        private static double currentFlightLaunchUT = double.NaN;

        /// <summary>
        /// Epsilon for the UT-backwards quickload signal. A single physics
        /// tick is 0.02 s; 0.1 s is 5 ticks of slack — large enough to ignore
        /// any legitimate sub-tick rounding, small enough to fire for any
        /// real quickload (which regresses UT by seconds at minimum).
        /// </summary>
        internal const double UtBackwardsEpsilon = 0.1;

        public override void OnAwake()
        {
            base.OnAwake();
            // Phase 2 (Rewind-to-Staging): publish the live scenario module so
            // EffectiveState can read RecordingSupersedes / LedgerTombstones /
            // ActiveReFlySessionMarker through <see cref="Instance"/>. OnAwake
            // fires before OnLoad, so the Instance is available throughout the
            // load path.
            // A predecessor still registered here was not torn down before this instance woke:
            // take the in-session handoff from it now (its OnDestroy will no longer be Instance).
            var predecessor = s_instance;
            if (!ReferenceEquals(predecessor, null) && !ReferenceEquals(predecessor, this))
            {
                try
                {
                    predecessor.CaptureInSessionStagedStateHandoff(InSessionStagedStateHandoff.CaptureReasonOnAwake);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(InSessionStagedStateHandoff.Tag,
                        $"Staged-list handoff capture from the predecessor threw in OnAwake: {ex.GetType().Name}: {ex.Message}");
                }
            }
            s_instance = this;

            // Drawdown-guard signal 5 fail-safe (MINOR-A, plan §3.2): clear the deferred
            // rewind-resource-adjustment flag here, NOT on onGameSceneSwitchRequested.
            // OnAwake of this fresh instance runs only after the previous (SPACECENTER)
            // ParsekScenario instance and its ApplyRewindResourceAdjustment coroutine were
            // destroyed, so old and new hosts never coexist - it cannot race a live
            // coroutine's ~2s singleton wait, and it runs BEFORE this instance's own OnLoad
            // so it never clears a flag the same instance is about to set. If the coroutine
            // completed normally the finally already cleared the flag (no-op here); if the
            // host was destroyed mid-wait the deferred patch never fired (nothing to
            // mis-guard) and this clears the stranded flag before any new-scene recalc reads
            // it. EndRewindResourceAdjustment is idempotent.
            RewindContext.EndRewindResourceAdjustment();
        }

        /// <summary>
        /// UT at which the last <see cref="RouteOrchestrator.Tick(double)"/>
        /// fired. Sentinel <c>-1.0</c> means "no tick yet this session" — the
        /// first Update merely seeds the accumulator and skips the tick body
        /// so the very first tick does not see a zero-length delta. Advanced by
        /// <see cref="RouteTickClock.Advance"/>, which also re-seeds it when the
        /// clock moves behind it.
        /// </summary>
        private double lastRouteTickUT = -1.0;

        /// <summary>
        /// Unity MonoBehaviour Update hook driven by the live ScenarioModule.
        /// Drives the route dispatch orchestrator at the cadence defined by
        /// <see cref="RouteOrchestrator.TickIntervalSec"/>. UT-delta accumulator
        /// (not wall-clock) so time warp is respected — at 10000x the route
        /// system sees one Tick per ~100ms wall-clock instead of one per
        /// 10000s game time. Exceptions from the tick body are caught and
        /// logged so a transient KSP-state error cannot kill the scenario
        /// module.
        /// </summary>
        private void Update()
        {
            if (Planetarium.fetch == null)
                return;
            // S9 game-mode gate: no route dispatch in an inert game (the static RouteStore
            // may still hold the previously loaded career's routes).
            if (ParsekGameModeGate.IsInertForCurrentGame)
                return;

            double currentUT;
            try
            {
                currentUT = Planetarium.GetUniversalTime();
            }
            catch (Exception ex)
            {
                // Defensive: Planetarium.GetUniversalTime() can throw during
                // very early load / scene teardown. A single skipped tick is
                // benign — the next Update reseeds the accumulator.
                ParsekLog.Verbose("Route",
                    $"Update: Planetarium.GetUniversalTime threw {ex.GetType().Name}: {ex.Message}; skipping tick");
                return;
            }

            // Seeds on the first Update and re-seeds when the clock is behind the baseline (the
            // go-back rewind moves the UT back after this instance's first Update).
            if (RouteTickClock.Advance(ref lastRouteTickUT, currentUT, RouteOrchestrator.TickIntervalSec)
                != RouteTickClockStep.Tick)
                return;

            try
            {
                RouteOrchestrator.Tick(currentUT);
            }
            catch (Exception ex)
            {
                ParsekLog.Error("Route",
                    $"RouteOrchestrator.Tick(currentUT) threw {ex.GetType().Name}: {ex.Message}");
            }

            // (Removed) The commit-time "Create Supply Route?" modal and its
            // deferred cross-scene retry are gone. Eligible Supply Runs now
            // surface as derived candidates in the Logistics window (see
            // RouteCandidateFinder), so route creation no longer interrupts
            // gameplay with a popup.
        }

        public override void OnSave(ConfigNode node)
        {
            // S9 game-mode gate: an inert game writes back exactly what it loaded (no
            // recordings, sidecars, ledger or settings writes), so a mission or scenario
            // save round-trips any Parsek node it carries unchanged.
            if (ParsekGameModeGate.CheckInert("ParsekScenario.OnSave"))
            {
                try
                {
                    WriteInertGameModeNode(node);
                }
                finally
                {
                    StripGhostMapVesselsFailClosed(
                        HighLogic.CurrentGame?.flightState, "OnSave(inert)", "inert-node", phasesCompleted: true);
                }
                return;
            }

            var sw = Stopwatch.StartNew();
            int recordingCount = 0;
            int dirtyCount = 0;
            string savePhase = "entry";
            bool phasesCompleted = false;
            try
            {
                RecorderStateLog.RecState("OnSave:pre", CaptureScenarioRecorderState());
                savePhase = "safety-net";
                SafetyNetAutoCommitPending();

                // A just-spawned miner whose drills stock switched off before it settled.
                SpawnedHarvesterRestore.CarryIntoSavedFlightState("OnSave");

                // Diagnostic: detect if HighLogic.SaveFolder changed since this scenario loaded.
                // Under normal KSP flow OnSave fires before the folder changes, but if it doesn't,
                // file writes (SaveRecordingFiles, GameStateStore, MilestoneStore) would target
                // the wrong save directory.
                string currentSaveFolder = HighLogic.SaveFolder;
                if (IsSaveFolderMismatch(scenarioSaveFolder, currentSaveFolder))
                {
                    ParsekLog.Warn("Scenario",
                        $"OnSave: save folder mismatch — loaded for '{scenarioSaveFolder}' " +
                        $"but current is '{currentSaveFolder}'. Data may write to wrong save directory.");
                }

                // Clear any existing recording nodes
                savePhase = "clear-nodes";
                node.RemoveNodes("RECORDING");

                var recordings = RecordingStore.CommittedRecordings;
                recordingCount = recordings.Count;
                ParsekLog.Info("Scenario", $"OnSave: saving {recordings.Count} committed recordings");

                // Count dirty recordings before save (SaveRecordingFiles clears FilesDirty)
                for (int i = 0; i < recordings.Count; i++)
                {
                    if (recordings[i].FilesDirty)
                        dirtyCount++;
                }
                var committedTrees = RecordingStore.CommittedTrees;
                for (int t = 0; t < committedTrees.Count; t++)
                {
                    foreach (var rec in committedTrees[t].Recordings.Values)
                    {
                        if (rec.FilesDirty)
                            dirtyCount++;
                    }
                }

                savePhase = "recordings";
                SaveTreeRecordings(node);
                savePhase = "missions";
                MissionStore.Save(node);
                savePhase = "vessel-names";
                VesselNameStore.Save(node);
                // Data-loss guard: if the committed store is empty because the tree/mission load
                // did not complete (committedTreeStateLoaded==false) while stranded sidecars remain
                // on disk, preserve the on-disk save's committed trees + missions instead of
                // hollowing the .sfs. Runs AFTER the mission write so it restores both surfaces here.
                savePhase = "preserve-tree-state-guard";
                PreserveRecordingStateIfLoadFault(node);
                savePhase = "game-state";
                PersistGameStateAndMilestones(node);
                // Rewind-to-Staging Phase 1 (design sections 5.1-5.9). Persistence
                // only in Phase 1; no behavior wired to these collections yet.
                savePhase = "refly-state-persist";
                SaveRewindStagingState(node);

                // Supply routes (design §4.7). RouteStore strips any pre-existing
                // ROUTES wrapper before writing so stale entries from a prior save
                // cannot leak through. Empty store writes nothing.
                savePhase = "routes";
                RouteStore.SaveRoutesTo(node);

                // The drawdown guard's stock pool reference (committed-value rule plan 2.4):
                // saved in the same .sfs as the live pools it describes.
                savePhase = "stock-pool-reference";
                StockPoolReference.SaveTo(node);

                // The ledger held-through UT, saved with the world whose rows it holds; dropped
                // first when the clock has passed it (the clock is trusted outside OnLoad and a
                // rewind's pending UT adjustment).
                savePhase = "ledger-held-window";
                SaveLedgerHeldWindow(node);

                // The vessels the last plain rewind held back, with their rewind-time copies
                // (rewind design 10.13).
                savePhase = "rewind-vessel-holds";
                RewindVesselHold.SaveInto(node);

                // The replay-scope latch (BUG-B), so a load of this save draws and relays every
                // ghost that replays at its UT (archived todo REPLAY-SCOPE-LOST-ON-SAVE-LOAD).
                savePhase = "playback-replay-scope";
                PlaybackScopeTracker.SaveInto(node);

                // The flags the player removed, so no replay of this save plants them again
                // (todo LOOP-REPLAY-REPLANTS-REMOVED-FLAG).
                savePhase = "removed-replay-flags";
                RemovedReplayFlags.SaveInto(node);

                // A fresh stamp per save: an in-session load that reads another one read a
                // save no stock load announced (the canary, design 4.1).
                savePhase = "save-stamp";
                WriteSaveStamp(node);
                // The save-wide format (SaveFormat.CurrentSaveFormat) and the writer's version.
                savePhase = "save-format";
                SaveFormat.StampScenarioNode(node);

                lastOnSaveScene = HighLogic.LoadedScene;
                RecorderStateLog.RecState("OnSave:post", CaptureScenarioRecorderState());
                phasesCompleted = true;
            }
            catch (Exception ex)
            {
                LogScenarioLifecycleException("OnSave", savePhase, ex);
                throw;
            }
            finally
            {
                // Fail closed: ghost map ProtoVessels are stripped whether or not a phase threw.
                StripGhostMapVesselsFailClosed(
                    HighLogic.CurrentGame?.flightState, "OnSave", savePhase, phasesCompleted);
                if (sw.IsRunning)
                    sw.Stop();
                WriteSaveTiming(sw, recordingCount, dirtyCount);
            }
        }

        /// <summary>Campaign save file whose tree/mission state the load-fault guard preserves.</summary>
        internal const string PersistentSaveFileName = "persistent.sfs";

        /// <summary>
        /// Testing seam for <see cref="PreserveRecordingStateIfLoadFault"/>: when non-null,
        /// the guard reads this path instead of resolving the campaign persistent.sfs via
        /// <see cref="RecordingPaths.ResolveSaveScopedPath"/> (which needs a live Unity save
        /// context). Production leaves it null.
        /// </summary>
        internal static string PersistentSavePathOverrideForTesting;

        internal const string CommittedTreeKind = "committed tree";
        internal const string ActiveTreeKind = "active tree";

        /// <summary>Deferred reload of the reverted persistent.sfs so the in-memory
        /// game + ledger come from the .bak + the restored Parsek/ sidecar snapshot.
        /// Runs one frame after OnLoad settles (NEVER calls SaveGame). Loads via
        /// GamePersistence.LoadGame + Game.Start() into the current scene (or
        /// FLIGHT->StartAndFocusVessel), mirroring CommitNonFlightSceneLoad. The
        /// reload's OnLoad fires on the COLD branch (RunTestBatchCrashReconcile set
        /// initialLoadDone=false), so LoadExternalFiles -> LedgerOrchestrator.OnLoad
        /// re-loads the ledger from the now-clean events.pgse. The batch artifacts
        /// (.bak + slot + snapshot) are swept ONLY AFTER the reload has been issued
        /// (the snapshot was consumed by the pre-reload sidecar restore + this reload
        /// reads the now-clean live dir). Best-effort; a failure leaves the disk
        /// correct (.bak + snapshot already applied) and preserves the artifacts.</summary>
        private IEnumerator DeferredReloadAfterTestBatchCrashReconcile(
            string saveFolder, TestBatchMarker marker)
        {
            yield return null; // let OnLoad + ScenarioRunner settle before reloading
            if (string.IsNullOrEmpty(saveFolder))
                yield break;
            Game game = null;
            try
            {
                game = GamePersistence.LoadGame("persistent", saveFolder, true, false);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("TestBatch",
                    $"crash-reconcile deferred reload: LoadGame threw: {ex.Message} (disk is correct; "
                    + "load the save manually to recover in-memory state). Preserving artifacts.");
                yield break;
            }
            if (game == null || game.flightState == null)
            {
                ParsekLog.Warn("TestBatch",
                    "crash-reconcile deferred reload: LoadGame returned null/no flightState "
                    + "(disk is correct; load the save manually). Preserving artifacts.");
                yield break;
            }
            ParsekLog.Info("TestBatch",
                $"crash-reconcile deferred reload: loading clean '{saveFolder}/persistent' "
                + $"into {HighLogic.LoadedScene}");
            if (HighLogic.LoadedScene == GameScenes.FLIGHT
                && game.flightState.activeVesselIdx >= 0
                && game.flightState.activeVesselIdx < game.flightState.protoVessels.Count)
            {
                FlightDriver.StartAndFocusVessel(game, game.flightState.activeVesselIdx);
            }
            else
            {
                HighLogic.CurrentGame = game;
                GamePersistence.UpdateScenarioModules(HighLogic.CurrentGame);
                HighLogic.CurrentGame.startScene = HighLogic.LoadedScene;
                HighLogic.CurrentGame.Start();
            }

            // Reload issued and the clean disk consumed: NOW sweep the batch
            // artifacts (.bak + slot + .loadmeta + -parsek snapshot). Deferring the
            // delete to here (rather than before the reload) keeps the snapshot
            // available right up until the clean reload, so a reload failure above
            // leaves every artifact intact for manual recovery.
            TryDeletePersistentBackupAndSnapshotArtifacts(marker);

            // [M-A3 G3] Reconcile has fully completed: release the H1 settle gate.
            // A failed reload above returns early WITHOUT reaching here, so the flag
            // stays set and H1 keeps waiting (the reconcile did not complete).
            CrashReconcileInProgress = false;
            ParsekLog.Info("TestBatch", "crash-reconcile complete; cleared");
        }

        // Static flag: only load from save once per KSP session.
        // On revert, the launch quicksave has stale data — the in-memory
        // static list is the real source of truth within a session.
        // Reset on main menu transition to prevent stale data leaking between saves.
        private static bool initialLoadDone = false;
        // Set true ONLY after a cold load's committed tree + mission state finishes loading
        // (LoadRecordingTrees + MissionStore.Load). Distinct from initialLoadDone, which is
        // flipped true at cold-start ENTRY — before the trees load — for branch-decision reasons
        // and therefore does NOT distinguish a completed load from one that threw mid-load. The
        // load-fault guard (PreserveRecordingStateIfLoadFault) gates on THIS: a throw during
        // tree/mission load leaves it false so the guard fires, while a successful load or an
        // intentional wipe leaves it true so the guard stays silent. Reset everywhere
        // initialLoadDone is reset (each precedes a fresh cold load).
        private static bool committedTreeStateLoaded = false;
        private static string lastSaveFolder = null;
        private static bool budgetDeductionApplied = false;

        // Tracks the scene from which the last OnSave fired.
        // Used to detect FLIGHT→FLIGHT transitions (Revert to Launch / quickload)
        // that aren't caught by epoch/recording-count comparison.
        private static GameScenes lastOnSaveScene = GameScenes.MAINMENU;

        // Cached autoMerge setting — ParsekSettings.Current can be null during early
        // scene loads (OnLoad fires before GameParameters are available). This is set
        // from ParsekSettings.Current whenever it's accessible, and used as fallback.
        // The seed is deliberately false and does NOT track the ParsekSettings.autoMerge
        // shipping default (ON since 0.10.4): it only governs the window before any read
        // of a real settings instance has happened, and there the conservative answer is
        // the right one — falling back to OFF shows a confirmation dialog the player can
        // still answer, while falling back to ON would silently commit against a player
        // who has explicitly turned auto-merge off.
        private static bool cachedAutoMerge = false;

        // True while a merge dialog popup is up: set by MergeDialog when it spawns the
        // popup, cleared by its buttons and teardown (MergeDialog.ClearPendingFlag) and by
        // the session reset.
        private static bool mergeDialogPending = false;
        internal static bool MergeDialogPending
        {
            get => mergeDialogPending;
            set => mergeDialogPending = value;
        }

        /// <summary>
        /// Reads the autoMerge setting reliably. ParsekSettings.Current can be null
        /// during early scene loads — falls back to the cached value in that case.
        /// </summary>
        internal static bool IsAutoMerge
        {
            get
            {
                var settings = ParsekSettings.Current;
                if (settings != null)
                {
                    cachedAutoMerge = settings.autoMerge;
                    return settings.autoMerge;
                }
                return cachedAutoMerge;
            }
        }

        // Tracks which save folder this scenario instance was loaded for.
        // Used to detect OnSave firing after HighLogic.SaveFolder has changed.
        private string scenarioSaveFolder;

        // The node an inert-game-mode OnLoad received (ParsekGameModeGate). KSP builds a
        // FRESH node for every OnSave, so an inert OnSave that wrote nothing would erase
        // whatever Parsek data the save carried; it writes this copy back instead.
        private ConfigNode inertGameModePassthroughNode;
        private string inertPassthroughReason = "inert game mode";

        /// <summary>
        /// The load-time half of the pre-Parsek backup hold. While the hold names this save,
        /// a load that is NOT a fresh load from the main menu keeps Parsek inert (OnLoad then
        /// takes the inert game-mode branch: the node is stashed for the verbatim pass-through
        /// save and the in-session handoff dropped); a fresh load (the main-menu transition nulled the last save folder, so
        /// <see cref="DetectSaveFolderChange"/> resets <c>initialLoadDone</c>) clears the hold
        /// and returns false, so OnLoad runs its cold path and retries the backup.
        /// </summary>
        private const string BackupHoldInertReason = "pre-Parsek backup hold";

        private string backupHoldRetrySaveFolderBefore;
        private bool backupHoldRetryDetected;

        public override void OnLoad(ConfigNode node)
        {
            // Pre-Parsek backup hold (PreParsekBackup): a save whose first-time backup failed
            // stays inert until it is loaded again from the main menu, which retries the
            // backup. Decided ahead of the game-mode gate, which reads the same hold and would
            // otherwise keep the retry load inert too.
            bool keepBackupHold = ShouldKeepBackupHold();
            // S9 game-mode gate (ParsekGameModeGate): stock never adds this scenario to a
            // mission / scenario game, but a save that already carries the node still loads
            // it. Keep the node verbatim for the inert OnSave and touch nothing else: no
            // settings, no stores, no subscriptions, no sidecar reads.
            if (keepBackupHold || ParsekGameModeGate.CheckInert("ParsekScenario.OnLoad"))
            {
                StashInertGameModeNode(node, keepBackupHold ? BackupHoldInertReason : "inert game mode");
                PreParsekBackup.DropUnusedAutomationFault("an inert load makes no backup attempt");
                InSessionStagedStateHandoff.Drop(InSessionStagedStateHandoff.DropInertGameMode);
                SaveFileLoadIntent.Clear("inert-game-mode");
                return;
            }
            inertGameModePassthroughNode = null;
            // An in-flight recovery request is consumed by the scene-exit finalize that
            // precedes this load; any leftover belongs to no live flight.
            InFlightRecoveryRequest.Clear("OnLoad");
            // Jumped spans belong to the timeline that made them; a load starts another.
            ConverterJumpPolicy.ClearJumpedSpans("OnLoad");
            // The subject science mirror is the committed science at the last mirror pass's
            // clock in the save that ran it; a load (another save, an earlier UT) rebuilds it at
            // its own first pass, so a stale one never reaches stock's subjects.
            CommittedMilestoneReach.ClearSubjectScienceMirror("OnLoad");
            // Attempt events held for the last load's flight restore belong to that load only.
            DropHeldSaveAttemptEvents("next-load");

            DiagnosticsState.ResetSessionCounters();
            IncompleteBallisticSceneExitFinalizer.ResetLifecycleDiagnostics();
            var sw = Stopwatch.StartNew();
            int loadedRecordingCount = 0;
            string loadPhase = "entry";
            string loadStatus = "running";
            // Route-timeline events: while OnLoad is on the stack, every
            // BumpSupersedeStateVersionLive degrades to the silent shape
            // (ledger writes during load are unsafe, and on a scene-change
            // load Planetarium can report a stale nonzero UT that would stamp
            // a mis-placed row). Cleared in finally so an OnLoad exception
            // never leaves the guard stuck.
            onLoadInProgress = true;
            // A new game state is loading: the scene-entry initial focus that follows
            // must not arm the vessel-switch flag (FRESH-LAUNCH-JOINS-RESTORED-COMMITTED-TREE).
            ClearFlightSceneReadyForVesselSwitchFlag();
            try
            {
                // Reset deferred dialog flag and clear input lock (dialog may have been
                // destroyed by scene change without the user clicking a button)
                loadPhase = "reset-dialog";
                mergeDialogPending = false;
                InputLockManager.RemoveControlLock("ParsekMergeDialog");

                // GUI state gallery (design-gui-state-gallery.md 7.5, layer 4). A mock
                // scope is a static on an automation-only type and every exit path clears
                // it, so finding one set here is UNREACHABLE across processes: a crash
                // mid-mock takes the static with the process. One bool read per load
                // turns "nothing injected can survive into a load" from an assumption
                // into an observable claim, which is the only kind of crash-safety
                // statement worth making.
                if (Parsek.UI.Gallery.GuiMockSession.IsLive)
                {
                    ParsekLog.Warn("Scenario",
                        "GuiMock session found LIVE at OnLoad: state="
                        + (Parsek.UI.Gallery.GuiMockSession.StateId ?? "-")
                        + " window=" + (Parsek.UI.Gallery.GuiMockSession.Window ?? "-")
                        + " - dropping it (nothing injected reaches a save, so no data is "
                        + "at risk; an exit path did not clear)");
                    Parsek.UI.Gallery.GuiMockSession.ResetForTesting();
                }

                // Restore user-intent settings (tracking-station visibility, readable
                // sidecar mirrors, etc.) from the
                // external settings.cfg file, overwriting whatever stale value KSP's
                // GameParameters restored from the save. Runs before anything reads
                // ParsekSettings.Current so the rest of OnLoad sees the fresh values.
                // Survives rewind (parsek_rw_* quicksaves), save/load, and KSP restart.
                loadPhase = "settings";
                // scenarioNodePopulated is a BARE BOOL: "does my own SCENARIO node carry
                // real Parsek data", judged exactly as PreParsekBackup.HasParsekGameplayFootprint
                // judges it (any child node, or any value beyond the stock name+scene pair;
                // the empty node KSP injects on first AddToAllGames contact is NOT data).
                // The settings layer owns what that signal MEANS - this file deliberately
                // names no UI-mode symbol.
                bool scenarioNodePopulated =
                    node != null && (node.nodes.Count > 0 || node.values.Count > 2);
                ParsekSettingsPersistence.ApplyTo(
                    ParsekSettings.Current, scenarioNodePopulated: scenarioNodePopulated);
                // The hidden-but-kept settings (auto-record trio, autoMerge,
                // forceFaithfulLoopPlayback) have no player-facing UI since the 2026-08-27
                // settings simplification, so a stale save-stored value (e.g. autoMerge=False
                // from an older career) is clamped back to the shipping value here - unless
                // an automation env hook is armed, in which case the harness keeps authority
                // over these fields (fixture pins + SetSetting must survive scene loads).
                if (!ParsekSettings.AutomationEnvPresent
                    && ParsekSettings.ClampHiddenSettingsToShippingValues(ParsekSettings.Current))
                {
                    ParsekLog.Info("Settings",
                        "Hidden settings clamped to shipping values (stale save-stored values overridden)");
                }
                RecorderStateLog.RecState("OnLoad:settings-applied", CaptureScenarioRecorderState());

                // The save-wide format stamp: log what wrote this save (pre-1.0, current, newer).
                // Each store's own reader decides what it keeps; this read changes nothing.
                loadPhase = "save-format";
                SaveFormat.ClassifyLoadedScenarioNode(node, scenarioNodePopulated);

                var recordings = RecordingStore.CommittedRecordings;
                loadedRecordingCount = recordings.Count;

                loadPhase = "hooks";
                RegisterMainMenuHook();
                string saveFolderBeforeDetect = backupHoldRetryDetected
                    ? backupHoldRetrySaveFolderBefore
                    : lastSaveFolder;
                backupHoldRetryDetected = false;
                backupHoldRetrySaveFolderBefore = null;
                DetectSaveFolderChange();

                // Load classification (LoadReconcilePolicy) before the prologue consumes any
                // state, after DetectSaveFolderChange settles initialLoadDone. An in-session
                // load is refined once the revert detector has run (below); every other kind
                // is final here and logs now.
                loadPhase = "load-classify";
                bool classifyInitialLoadDone = initialLoadDone;
                bool classifyRewinding = RewindContext.IsRewinding;
                bool classifyReFlyInvoke = RewindInvokeContext.Pending;
                bool classifyDiscardReFly = DiscardReFlyLoadIntent.TryConsume(
                    HighLogic.LoadedScene, "ParsekScenario.OnLoad", out RevertTarget discardReFlyIntentTarget,
                    HighLogic.CurrentGame?.flightState != null
                        ? HighLogic.CurrentGame.flightState.universalTime
                        : double.NaN);
                string classifyDiscardReFlyTarget = classifyDiscardReFly
                    ? discardReFlyIntentTarget.ToString()
                    : null;
                // A save file stock just loaded (F9, the pause-menu Load, the main-menu Load):
                // consumed by this load whatever it classifies as. The revert detector is read
                // without consuming it (the revert branch consumes it below): stock fires the
                // save-file event inside a revert too, and the revert wins.
                bool classifyRevertPending = RevertDetector.PendingKind != RevertKind.None;
                bool classifySaveFileLoad = SaveFileLoadIntent.TryConsume(
                    UnityEngine.Time.realtimeSinceStartup, UnityEngine.Time.frameCount,
                    "ParsekScenario.OnLoad", out double saveFileIntentNodeUT, out int saveFileIntentAgeFrames);
                string classifyHandoff = InSessionStagedStateHandoff.DescribeForClassification();
                EarlyLoadKind earlyLoadKind = LoadReconcilePolicy.ClassifyEarly(
                    classifyInitialLoadDone, classifyRewinding, classifyReFlyInvoke, classifyDiscardReFly,
                    classifySaveFileLoad, classifyRevertPending);
                string coldReason = LoadReconcilePolicy.ResolveColdReason(
                    earlyLoadKind, classifyInitialLoadDone,
                    coldLoadsThisProcess, saveFolderBeforeDetect, HighLogic.SaveFolder);
                var classification = new LoadClassificationFacts(
                    classifyInitialLoadDone, classifyRewinding, classifyReFlyInvoke, classifyDiscardReFlyTarget,
                    classifyHandoff, coldReason, classifySaveFileLoad, classifyRevertPending,
                    classifySaveFileLoad ? saveFileIntentAgeFrames : -1,
                    classifySaveFileLoad ? saveFileIntentNodeUT : double.NaN);
                if (earlyLoadKind != EarlyLoadKind.InSession)
                    LogLoadClassification(earlyLoadKind, LoadReconcilePolicy.ToLoadKind(earlyLoadKind), classification);

                // A save-file load replaces everything from the save (design 4.1): the cold
                // branch below runs, and the handoff the torn-down scenario captured is not
                // this load's (the save's own staging nodes are).
                if (coldReason == LoadReconcilePolicy.ColdReasonSaveFile)
                {
                    initialLoadDone = false;
                    InSessionStagedStateHandoff.Drop(InSessionStagedStateHandoff.DropSaveFileLoad);
                }
                else if (classifySaveFileLoad)
                {
                    ParsekLog.Verbose(LoadReconcilePolicy.LogTag,
                        "Save-file load intent dropped reason=consumed-by-" + earlyLoadKind
                        + (classifyRevertPending ? " (a revert is pending)" : ""));
                }

                // Every Cold load starts from nothing: the save (and, for a save written by an
                // older build, the external files) is the only source of what follows
                // (design-load-restores-the-save.md 4.2).
                if (earlyLoadKind == EarlyLoadKind.Cold)
                {
                    loadPhase = "save-scoped-reset";
                    ResetSaveScopedSessionState(coldReason);
                }

                if (!RewindContext.IsRewinding)
                    KerbalLoadRepairDiagnostics.Begin();
                loadPhase = "crew-groups";
                LoadCrewAndGroupState(node);
                // Rewind-to-Staging Phase 1 (design sections 5.1-5.9). Load runs
                // on every OnLoad so a revert or scene change rebuilds the lists
                // from .sfs rather than reusing stale in-memory state.
                loadPhase = "refly-state-load";
                LoadRewindStagingState(node);

                // The drawdown guard's stock pool reference comes from the same .sfs as the
                // live pools it describes, on every load kind (LoadReconcilePolicy,
                // StockPoolReference category); a save without one starts afresh at its
                // first patch.
                loadPhase = "stock-pool-reference-load";
                LoadStockPoolReference(node, earlyLoadKind);

                // The ledger held-through UT says which ledger rows the loaded world already
                // holds (a save written inside a Rewind-to-Launch lead window), before any walk
                // or committed-future read of this load (LoadReconcilePolicy, LedgerHeldWindow).
                loadPhase = "ledger-held-window-load";
                LoadLedgerHeldWindow(node, earlyLoadKind);

                // The vessels a plain rewind held back for their committed future describe the
                // world of the save that holds them (LoadReconcilePolicy, RewindVesselHolds).
                loadPhase = "rewind-vessel-holds-load";
                LoadRewindVesselHolds(node, earlyLoadKind);

                // The replay-scope latch describes the timeline of the save that carries it
                // (LoadReconcilePolicy, PlaybackReplayScope): a cold load takes the save's, so a
                // save written mid-replay keeps its ghosts drawn and relaying.
                loadPhase = "playback-replay-scope-load";
                LoadPlaybackReplayScope(node, earlyLoadKind);

                // The flags the player removed describe the world of the save that carries them
                // (LoadReconcilePolicy, RemovedReplayFlags): a cold load takes the save's; every
                // other load keeps memory's and drops the removals after the loaded world's UT below.
                loadPhase = "removed-replay-flags-load";
                LoadRemovedReplayFlags(node, earlyLoadKind);

                // A rewind point always survives a plain rewind (operator ruling
                // 2026-09-23). The node above is persistent.sfs as last written, not the
                // rewind save (SpaceCenterMain.Start reloads persistent), so its RP list is
                // of unknown age: put back the in-memory list captured when the rewind
                // started. No-op on every load that is not a rewind.
                loadPhase = "rewind-point-carry-over";
                RewindCarry.ReinstallRewindCarriedRewindPointsAfterLoad(this);

                // Same for the supersede, rewind-retirement and tombstone lists and the merge
                // journal: the recordings and the ledger are kept from memory across a plain
                // rewind, so these must be too. A rewind keeps every supersede row, the rewound
                // tree's included (owner ruling 2026-10-24). No-op when not a rewind.
                loadPhase = "rewind-staged-lists-carry-over";
                RewindCarry.ReinstallRewindCarriedStagedListsAfterLoad(this);

                // In-session loads: the same lists, the journal and (on a Discard Re-fly) the marker
                // come from the scenario instance this load replaces, as the committed recordings
                // and the ledger do (InSessionStagedStateHandoff, captured in OnDestroy). Consumed
                // by every load: a cold load, a plain rewind (the carries above own its lists) and
                // a Re-Fly start (the reconciliation bundle owns them) drop it. The rewind points
                // are partitioned by owner, and a resumed tree's rows handed back to the save, once
                // the tree restores below have detached that tree (step B).
                loadPhase = "in-session-staged-handoff";
                ApplyInSessionStagedStateHandoffStepA(earlyLoadKind);

                // Pre-Parsek safety backup: the first time Parsek cold-loads a save with no
                // Parsek footprint, copy it (before any Parsek write, so the persistent.sfs is
                // gameplay-pristine) into a timestamped sibling folder that shows up in KSP's
                // Load menu. Cold-load-only (!initialLoadDone) so it never captures a
                // post-autosave file on a later same-session OnLoad; runs before LoadExternalFiles
                // (Parsek's first read/recalc). Idempotent via on-disk footprint + done-marker.
                loadPhase = "pre-parsek-backup";
                if (!initialLoadDone)
                {
                    PreParsekBackup.SweepOrphanStagingDirs();
                    if (PreParsekBackup.MaybeBackupOnFirstColdContact()
                        == PreParsekBackup.BackupAttemptOutcome.Failed)
                    {
                        // Owner ruling 2026-11-06: never change a save Parsek could not back
                        // up. Everything above only read the (footprint-free) node into empty
                        // stores; nothing below runs, and the save's node goes back out empty.
                        EnterBackupHold(node, HighLogic.SaveFolder);
                        loadStatus = "backup-hold";
                        return;
                    }
                }
                else
                {
                    PreParsekBackup.DropUnusedAutomationFault("an in-session load makes no backup attempt");
                }

                // Game state recorder lifecycle — re-subscribe on every OnLoad (handles reverts)
                loadPhase = "game-state-recorder";
                stateRecorder?.Unsubscribe();
                if (!initialLoadDone)
                    LoadExternalFiles(node);
                stateRecorder = new GameStateRecorder();
                stateRecorder.SeedFacilityCacheFromCurrentState();
                stateRecorder.Subscribe();
                // Subscribe() seeds the resource baselines from whichever currency singletons
                // exist right now, and OnLoad runs from the middle of KSP's own scenario-module
                // load pass - so on some scene loads one or more baselines are left NaN, and an
                // unseeded baseline eats the first real change of the scene instead of emitting
                // it. Top them up as soon as the singletons appear.
                StartCoroutine(SeedRecorderResourceBaselinesWhenReady(stateRecorder));

                loadPhase = "initial-baseline";
                SubscribeVesselLifecycleEvents();
                CaptureInitialBaseline();

                if (initialLoadDone)
                {
                    // Go-back detection: must be BEFORE revert detection and BEFORE any
                    // .sfs data loading. In-memory state is the source of truth.
                    if (RewindContext.IsRewinding)
                    {
                        // Timeline break (design 3.15 step 5): a plain rewind keeps the in-memory
                        // ledger and loads the rewind save's stock state. This branch returns
                        // before the refined classification below, so it records its own break.
                        // Both read the rewind save's WORLD UT, not the wound-back clock: the
                        // world holds everything up to the save's own UT (and the held-through
                        // UT it carries), so income earned in the lead window is not forgotten
                        // (todo REWIND-LEAD-WINDOW-KSC-ACTIONS-LAND-IN-THE-FUTURE).
                        loadPhase = "timeline-break";
                        double rewindWorldUT = ResolvePlainRewindWorldUT(
                            KerbalsModule.ReadLoadedSaveUT(), RewindContext.RewindSaveOwnClockUT,
                            RewindContext.RewindSaveLedgerHeldThroughUT);
                        StockShadowTakeBack.NoteLoad(
                            LoadKind.PlainRewind, rewindWorldUT, CaptureSequence.CurrentHighWater());
                        // Live-only income (design 3.15 step 9): the rewind save's stock state
                        // forgets income no ledger channel converts that was earned after its UT.
                        LiveOnlyIncome.DropAfterTimeTravelLoad(LoadKind.PlainRewind, rewindWorldUT);
                        // A flag the player removed after the rewind save's UT stands in its world
                        // again (or replay plants it at its moment).
                        ReconcileRemovedReplayFlagsAtLoad(LoadKind.PlainRewind, rewindWorldUT);

                        loadPhase = "rewind";
                        HandleRewindOnLoad(node, recordings);
                        ReconcileReadableSidecarMirrorsOnLoadIfDisabled();
                        // The rewind save's vessels carry the names of its UT: re-apply the
                        // decisions (design 7.6 step 1; a rewind keeps every rename, ruling 5).
                        loadPhase = "vessel-names-apply";
                        VesselNameStore.RunSpawnLinkBackfill("onload-tail:rewind");
                        VesselNameStore.ApplyAll("onload-tail:rewind", VesselNameLiveScope.ProtosOnly);
                        DiagnosticsComputation.EmitSceneLoadSnapshot(recordings.Count, HighLogic.LoadedScene.ToString());
                        loadedRecordingCount = recordings.Count;
                        loadStatus = "returned";
                        return;
                    }

                    // Restore an in-flight tree from the save file (if OnSave wrote one).
                    // This stashes it into the pending-Limbo slot so the revert-detection
                    // dispatch below can decide between "restore and resume" (quickload)
                    // and "finalize and commit" (real revert).
                    // Runs AFTER ParsekSettingsPersistence.ApplyTo so restored settings
                    // affect the rest of OnLoad, but BEFORE revert detection so the
                    // pending slot is populated when it runs.
                    loadPhase = "active-tree-restore";
                    bool activeTreeRestoredFromSave = TryRestoreActiveTreeNode(
                        node, earlyLoadKind, HighLogic.LoadedScene == GameScenes.FLIGHT);
                    bool pendingTreeRestoredFromSave = TryRestorePendingTreeNode(
                        node, activeTreeRestoredFromSave);
                    RecorderStateLog.RecState("OnLoad:active-tree-restored", CaptureScenarioRecorderState());

                    // Step B of the in-session handoff: rewind points of trees memory still holds
                    // committed come from memory, the rest from the save. After the restores
                    // above, so the committed set it partitions by is the one this load keeps.
                    loadPhase = "in-session-handoff-step-b";
                    ApplyInSessionStagedStateHandoffStepB();

                    loadPhase = "revert-classification";

                    // Count tree recordings from saved tree nodes for accurate revert
                    // detection. Pure node arithmetic, extracted so the rule is testable.
                    int savedTreeRecCount = CountSavedCommittedRecordingNodes(node);

                    // FLIGHT→FLIGHT can be a revert, a quickload, or a vessel switch.
                    // The event-based revert detector below owns the distinction; the
                    // FLIGHT→FLIGHT boolean alone is not enough to classify the load.
                    bool isFlightToFlight = lastOnSaveScene == GameScenes.FLIGHT
                                            && HighLogic.LoadedScene == GameScenes.FLIGHT;
                    // vesselSwitchPending must be BOTH set AND fresh. EVA events
                    // fire onVesselSwitching without causing a scene reload, so
                    // the raw flag can sit sticky for thousands of frames before
                    // the next unrelated quickload consumes it — the staleness
                    // check prevents an EVA-then-F9 sequence from being
                    // mis-identified as a tracking-station vessel switch and
                    // routed into FinalizeLimboForRevert (post-PR #163 playtest
                    // bug; see CHANGELOG for full narrative).
                    int currentFrame = UnityEngine.Time.frameCount;
                    bool vesselSwitchFlagFresh = IsVesselSwitchFlagFresh(
                        vesselSwitchPending, vesselSwitchPendingFrame,
                        currentFrame, VesselSwitchPendingMaxAgeFrames);
                    bool isVesselSwitch = isFlightToFlight && vesselSwitchFlagFresh;
                    if (vesselSwitchPending && !vesselSwitchFlagFresh)
                    {
                        int age = currentFrame - vesselSwitchPendingFrame;
                        ParsekLog.Info("Scenario",
                            $"vesselSwitchPending flag stale ({age} frames old, " +
                            $"max {VesselSwitchPendingMaxAgeFrames}) — " +
                            "treating FLIGHT→FLIGHT as quickload, not vessel switch");
                    }
                    vesselSwitchPending = false; // consume the flag regardless
                    int vesselSwitchFlagAgeOnConsume =
                        vesselSwitchPendingFrame >= 0
                            ? currentFrame - vesselSwitchPendingFrame
                            : -1;
                    vesselSwitchPendingFrame = -1;

                    // UT-backwards signal (Bug A). F5 followed by flight and then F9
                    // is indistinguishable from a normal FLIGHT→FLIGHT scene change
                    // on every recording-count-based signal when F5 happens post-merge
                    // (both sides have equal counts). The one unambiguous fingerprint
                    // is that Planetarium UT regresses
                    // between OnSceneChangeRequested and OnLoad — a quickload /
                    // revert / rewind is the only legitimate way that happens.
                    // Captured in ParsekFlight.OnSceneChangeRequested via
                    // StampSceneChangeRequestedUT; consumed exactly once here.
                    // Planetarium.fetch can theoretically be null during early
                    // OnLoad in non-flight scenes; if so we have no clock to
                    // compare and must NOT report a UT regression (would false-
                    // positive a discard against any non-zero preChangeUT).
                    bool planetariumReady = Planetarium.fetch != null;
                    double loadedUT = planetariumReady
                        ? Planetarium.GetUniversalTime()
                        : 0.0;
                    double preChangeUT = lastSceneChangeRequestedUT;
                    lastSceneChangeRequestedUT = -1.0; // consume regardless of outcome
                    bool utWentBackwards = planetariumReady && IsQuickloadOnLoad(
                        preChangeUT, loadedUT, UtBackwardsEpsilon);

                    // Contradict a stale-but-fresh-classified vessel-switch flag on
                    // quickload: the frame-count heuristic can mis-classify under
                    // low render FPS, and even when it's correct, a quickload must
                    // always win over a vessel-switch interpretation.
                    if (utWentBackwards && isFlightToFlight && isVesselSwitch)
                    {
                        ParsekLog.Info("Scenario",
                            "OnLoad: UT went backwards " +
                            $"({preChangeUT.ToString("F2", CultureInfo.InvariantCulture)} → " +
                            $"{loadedUT.ToString("F2", CultureInfo.InvariantCulture)}) — " +
                            "forcing isVesselSwitch=false for quickload classification " +
                            $"(flag was fresh at age={vesselSwitchFlagAgeOnConsume} frames; " +
                            $"cap={VesselSwitchPendingMaxAgeFrames})");
                        isVesselSwitch = false;
                    }

                    // Bug #300: on first-ever flight (no prior commits), recording
                    // counts are zero on both sides. The distinguishing signal is that
                    // TryRestoreActiveTreeNode returned false (the launch quicksave
                    // has no active tree) yet a Limbo tree persists from the
                    // StashActiveTreeAsPendingLimbo call before this OnLoad.
                    // Quickloads (F5/F9) always have an active tree in the save file
                    // because OnSave writes it, so activeTreeRestoredFromSave=true.
                    bool hasOrphanedLimboTree = ComputeHasOrphanedLimboTree(
                        PendingTreeSlot.HasPendingTree,
                        PendingTreeSlot.PendingTreeStateValue,
                        activeTreeRestoredFromSave);
                    // #434: event-based revert detection. GameEvents.OnRevertTo{Launch,Prelaunch}FlightState
                    // fires synchronously inside FlightDriver.RevertToLaunch / RevertToPrelaunch,
                    // BEFORE HighLogic.LoadScene, so by the time OnLoad runs the flag is set.
                    // We consume it here; any later OnLoad (e.g. an F9 into a pre-revert flight
                    // quicksave) sees RevertKind.None and classifies as a plain quickload resume.
                    var revertKind = RevertDetector.Consume("ParsekScenario.OnLoad");
                    // BUG-H: consume the revert-target whitelist together with the kind so the
                    // captured static can never leak into a later OnLoad (e.g. a revert event that
                    // this load classifies as a vessel switch). Only used below when isRevert.
                    HashSet<uint> revertTargetPids = RevertDetector.ConsumeRevertTargetVesselPids();
                    bool isRevert = ComputeIsRevertOnLoad(isVesselSwitch, revertKind);
                    ParsekLog.Verbose("Scenario",
                        $"OnLoad: revert detection — revertKind={revertKind}, " +
                        $"savedTreeRecs={savedTreeRecCount}, " +
                        $"memoryRecordings={recordings.Count}, lastOnSaveScene={lastOnSaveScene}, " +
                        $"isFlightToFlight={isFlightToFlight}, isVesselSwitch={isVesselSwitch}, isRevert={isRevert}, " +
                        $"pendingTreeState={PendingTreeSlot.PendingTreeStateValue}, " +
                        $"activeTreeRestoredFromSave={activeTreeRestoredFromSave}, " +
                        $"pendingTreeRestoredFromSave={pendingTreeRestoredFromSave}, " +
                        $"hasOrphanedLimboTree={hasOrphanedLimboTree}");

                    // Refined load kind: the revert detector has run, so an in-session load now
                    // splits into StockRevert / QuickloadFlight / InSessionOther. Every consumer
                    // of the refined kind sits below this line.
                    LoadKind refinedLoadKind = LoadReconcilePolicy.ClassifyRefined(
                        earlyLoadKind, isRevert, isFlightToFlight, utWentBackwards);
                    if (earlyLoadKind == EarlyLoadKind.InSession)
                        LogLoadClassification(earlyLoadKind, refinedLoadKind, classification);

                    // The saveStamp canary (design 4.1): an in-session read of a save this
                    // process did not write last is a load no stock path announced. One Warn;
                    // the classification stands.
                    CheckLoadedSaveStamp(node, refinedLoadKind);

                    // Timeline break (design 3.15 step 5): a load that keeps the in-memory
                    // ledger and loads an earlier stock state makes stock forget every credit
                    // after the loaded UT, so a later live repeat of one is a duplicate payment.
                    // The loaded world's UT: the loaded clock raised to the held-through UT the
                    // prologue installed from this save (a save written inside a Rewind-to-Launch
                    // lead window holds everything up to it).
                    double loadedWorldUT = LedgerHeldWindow.ResolveLedgerNowUT(KerbalsModule.ReadLoadedSaveUT());
                    StockShadowTakeBack.NoteLoad(
                        refinedLoadKind, loadedWorldUT, CaptureSequence.CurrentHighWater());
                    // Live-only income (design 3.15 step 9): the same load forgets income no
                    // ledger channel converts that was earned after the loaded UT.
                    LiveOnlyIncome.DropAfterTimeTravelLoad(refinedLoadKind, loadedWorldUT);
                    // The same load forgets the player's flag removals after the loaded UT.
                    ReconcileRemovedReplayFlagsAtLoad(refinedLoadKind, loadedWorldUT);

                    // Vessel names (design 7.6, ruling 5): a stock revert or Discard Re-fly cancels
                    // the current flight epoch, and with it the stock renames made inside it.
                    // Before the OnLoad tail re-applies the decisions to recordings and protos.
                    if (LoadReconcilePolicy.Decide(refinedLoadKind, LoadStateCategory.VesselNames).Action
                        == LoadReconcileAction.MemoryUndoRevertedFlight)
                    {
                        loadPhase = "vessel-names-revert-undo";
                        VesselNameStore.UndoCurrentFlightEpoch("onload:" + refinedLoadKind);
                    }

                    // Capture the launch UT for the editor-revert orphan-prune boundary. A fresh
                    // launch from the editor (NEW_FROM_FILE / NEW_FROM_CRAFT_NODE) enters FLIGHT
                    // with loadedUT == the launch instant, regardless of launch site (KSC
                    // pad/runway, Making History Desert/Woomerang) or game mode. A non-fresh
                    // flight load (quickload-resume) clears the static so a stale value can't
                    // over-prune a later editor revert. Revert loads (isRevert) and non-flight
                    // loads leave the static untouched so the captured launch UT survives until
                    // the matching Revert-to-editor reads it.
                    switch (DecideFreshLaunchUtAction(
                        HighLogic.LoadedScene, isRevert, isVesselSwitch,
                        ParsekFlight.IsFreshLaunchStartupBehaviour(FlightDriver.StartupBehaviour)))
                    {
                        case FreshLaunchUtAction.Capture:
                            currentFlightLaunchUT = loadedUT;
                            freshLaunchEntryPending = true;
                            ParsekLog.Info("Scenario",
                                $"Captured fresh-launch UT {loadedUT.ToString("R", CultureInfo.InvariantCulture)} " +
                                "for the Revert-to-editor orphan-prune boundary");
                            break;
                        case FreshLaunchUtAction.Clear:
                            freshLaunchEntryPending = false;
                            if (!double.IsNaN(currentFlightLaunchUT))
                            {
                                currentFlightLaunchUT = double.NaN;
                                ParsekLog.Verbose("Scenario",
                                    "Cleared captured fresh-launch UT on non-fresh flight load (quickload-resume)");
                            }
                            break;
                        // FreshLaunchUtAction.Leave: revert / non-flight load keeps the captured UT.
                    }

                    // Discard stashed-this-transition recordings on quickload (Bug A).
                    // Must run BEFORE the isRevert branch at line ~580, because the
                    // revert branch consumes PendingStashedThisTransition for its own
                    // "keep across revert" logic. For a pure F5/F9 after a merged
                    // tree, isRevert=false, so the existing revert-branch discard path
                    // never runs — this is why the bug manifested in the 2026-04-09 playtest.
                    //
                    // #434 follow-up (2026-04-17): skip this on revert. Revert-to-Launch is
                    // always `utWentBackwards && isFlightToFlight`, so without the !isRevert
                    // guard the hard-discard path ran first and deleted sidecar files +
                    // purged tagged events, defeating the soft-unstash invariant established
                    // in the isRevert branch below. Observed in
                    // `logs/2026-04-17_2158_revert-stress-test`: recording 4f2a8438's .prec /
                    // _ghost.craft files were deleted before `UnstashPendingTreeOnRevert`
                    // could run, which would break F9-from-flight-quicksave in any playthrough
                    // where the user F5'd during the doomed flight. Gate extracted to
                    // ShouldRunQuickloadDiscard so the contract is unit-testable.
                    //
                    // Re-Fly retry has the same quickload shape, but during the pre-marker
                    // invoke window the session is represented by RewindInvokeContext.Pending.
                    // The gate treats that pending invoke as active so the RP quicksave and
                    // pending tree survive until RewindInvoker.ConsumePostLoad recreates the
                    // marker and resumes the session.
                    //
                    // The pending-science half reads the policy table. Wherever this gate
                    // passes, the refined kind is QuickloadFlight (Clear): an early ReFlyStart
                    // keeps RewindInvokeContext.Pending set, so the gate refuses; an early
                    // DiscardReFly only matches a Space Center / editor load, so
                    // isFlightToFlight is false; an in-session load with the clock backwards,
                    // FLIGHT->FLIGHT and no revert refines to QuickloadFlight.
                    if (ShouldRunQuickloadDiscard(utWentBackwards, isFlightToFlight, isRevert))
                    {
                        DiscardStashedOnQuickload(preChangeUT, loadedUT,
                            clearPendingScience: LoadReconcilePolicy.Decide(
                                refinedLoadKind, LoadStateCategory.PendingScience).Action
                                == LoadReconcileAction.Clear);
                    }

                    RecorderStateLog.RecState(
                        isRevert ? "OnLoad:revert-decided=Y" : "OnLoad:revert-decided=N",
                        CaptureScenarioRecorderState());
                    if (isFlightToFlight && !isRevert && !isVesselSwitch)
                    {
                        ParsekLog.Info("Scenario",
                            "OnLoad: FLIGHT→FLIGHT without revert indicators — treating as quickload / scene reload, not revert");
                    }

                    // Collect spawned vessel PIDs + names BEFORE restore resets them.
                    // Only on revert — on normal scene changes the spawned vessels are
                    // legitimate and must not be cleaned up.
                    // Guard: if cleanup data was already set by a prior rewind path,
                    // do NOT overwrite — the rewind path's data is authoritative.
                    // (After rewind, ResetAllPlaybackState zeros spawn tracking, so
                    // CollectSpawnedVesselInfo returns empty here and would clobber
                    // the rewind data with null.)
                    if (isRevert)
                    {
                        loadPhase = "revert-cleanup";
                        ArmRevertCleanupData(RewindLauncher.CollectSpawnedVesselInfo);

                        // Clear pending tree/recording from a PREVIOUS flight — dialog was shown
                        // but user reverted before acting on it. Prevents OnFlightReady fallback
                        // from showing the dialog again (#64).
                        // However, if the pending was stashed during THIS scene transition
                        // (OnSceneChangeRequested → StashPendingTree), it is fresh and must
                        // survive long enough for the current OnLoad to classify it correctly.
                        // On true revert the later soft-unstash branch below clears it again;
                        // on quickload/non-revert paths other dispatch owns it.
                        var revertDisposition = ClassifyRevertPendingTreeDisposition(
                            PendingTreeSlot.PendingStashedThisTransition,
                            PendingTreeSlot.HasPendingTree,
                            PendingTreeSlot.PendingTreeStateValue);
                        if (revertDisposition == RevertPendingTreeDisposition.KeepFreshlyStashed)
                        {
                            ParsekLog.Info("Scenario",
                                "Revert: keeping freshly-stashed pending (stashed this transition) — " +
                                $"tree={PendingTreeSlot.HasPendingTree}");
                            PendingTreeSlot.PendingStashedThisTransition = false;
                        }
                        else if (revertDisposition == RevertPendingTreeDisposition.KeepLimbo)
                        {
                            ParsekLog.Info("Scenario",
                                $"Revert: keeping pending Limbo tree " +
                                $"'{PendingTreeSlot.PendingTree?.TreeName}' " +
                                $"(state={PendingTreeSlot.PendingTreeStateValue}) — stashed for dispatch");
                        }
                        else if (revertDisposition == RevertPendingTreeDisposition.DiscardOrphaned)
                        {
                            ParsekLog.Info("Scenario", "Clearing orphaned pending tree on revert (stale from previous flight)");
                            DiscardPendingTreeAndAbandonDeferredFlightResults(
                                "orphaned pending tree discarded on revert");
                        }
                    }

                    // Restore tree recording mutable state from RECORDING_TREE nodes.
                    // First, reset ALL tree recordings' spawn-tracking fields to defaults, so
                    // VesselSpawned/SpawnedPid/etc. never carry over from a flight whose
                    // vessels a revert undid; the tree-state-restore loop below then puts
                    // back what the saved RECORDING_TREE nodes say. Those nodes are present
                    // on revert too: the launch/prelaunch GameBackup the revert loads was
                    // written through OnSave, so it carries every tree committed before the
                    // launch. Terminal verdicts are NOT reset here: a committed recording's
                    // terminalState is recorded content (see
                    // ResetTreeRecordingMutableStateForLoad).
                    loadPhase = "tree-mutable-state";
                    ResetTreeRecordingMutableStateForLoad(recordings);

                    // Strip orphaned spawned vessels from flightState on revert.
                    // These vessels were spawned by Parsek in a previous flight but their
                    // tracking was lost when spawn flags were reset. Without stripping,
                    // they contaminate the next launch quicksave and persist across reverts.
                    //
                    // BUG-H: this is launch-identity-aware AND scoped to the reverted flight.
                    // The deletion decision matches a vessel against a recording's spawn endpoint
                    // by pid+launch-Guid (never name alone) and only removes vessels that appeared
                    // DURING the reverted flight (not in the revert-target launch/prelaunch
                    // quicksave whitelist). A stock revert must only undo the current launch — it
                    // must never delete a real, separately-launched craft from an unrelated mission
                    // that merely reuses the craft-baked name/pid of a recording. The strip fails
                    // closed when the scope whitelist is unavailable.
                    if (isRevert)
                    {
                        loadPhase = "revert-strip";
                        // Preserve for the OnFlightReady belt-and-suspenders cleanup (FLIGHT->FLIGHT
                        // launch revert recovers LOADED vessels, which OnLoad's protoVessel strip
                        // does not see). Null is a meaningful "fail-closed" scope there too.
                        RewindLauncher.PendingRevertPreExistingPids = revertTargetPids;

                        var flightState = HighLogic.CurrentGame?.flightState;
                        if (flightState != null)
                        {
                            var allCommitted = RecordingStore.CollectAllCommittedRecordings();
                            StripOrphanedSpawnedVessels(
                                flightState.protoVessels, allCommitted,
                                matchSource: false, skipPrelaunch: true,
                                requireWhitelist: true, preExistingWhitelist: revertTargetPids);
                        }
                        ReleaseRevertCleanupOutsideFlight(HighLogic.LoadedScene);
                    }

                    // Rescue crew orphaned by vessel stripping (#116)
                    if (isRevert && HighLogic.CurrentGame?.flightState != null)
                        CrewReservationManager.RescueOrphanedCrew(
                            HighLogic.CurrentGame.flightState.protoVessels);

                    // Then restore from saved tree nodes (present on scene change and on revert:
                    // the revert target was written through OnSave at launch).
                    loadPhase = "tree-state-restore";
                    ConfigNode[] savedTreeNodes = node.GetNodes("RECORDING_TREE");
                    if (savedTreeNodes.Length > 0)
                    {
                        // Rebuild tree mutable state from saved tree nodes.
                        // Skip active-tree (in-flight) marker nodes: their recordings live
                        // in pending-Limbo and are not in `recordings` (the committed list).
                        // Matching against `recordings` would harmlessly fail today, but
                        // the defensive skip avoids future refactors conflating the two.
                        foreach (var savedTreeNode in savedTreeNodes)
                        {
                            if (IsActiveTreeNode(savedTreeNode)) continue;
                            if (IsPendingTreeNode(savedTreeNode)) continue;

                            ConfigNode[] savedTreeRecNodes = savedTreeNode.GetNodes("RECORDING");
                            foreach (var savedTreeRecNode in savedTreeRecNodes)
                            {
                                string savedRecId = savedTreeRecNode.GetValue("recordingId");
                                if (string.IsNullOrEmpty(savedRecId)) continue;

                                // Find the in-memory recording by ID and restore mutable state
                                for (int i = 0; i < recordings.Count; i++)
                                {
                                    if (recordings[i].RecordingId == savedRecId)
                                    {
                                        string pidStr = savedTreeRecNode.GetValue("spawnedPid");
                                        uint savedPid = 0;
                                        if (pidStr != null)
                                            uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out savedPid);
                                        recordings[i].SpawnedVesselPersistentId = savedPid;
                                        // Keep the VesselSpawned flag in sync with the spawned-pid invariant
                                        // so the scene-enter resume path's filter matches after save/load.
                                        recordings[i].VesselSpawned = savedPid != 0;

                                        string resIdxStr = savedTreeRecNode.GetValue("lastResIdx");
                                        int resIdx = -1;
                                        if (resIdxStr != null)
                                            int.TryParse(resIdxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out resIdx);
                                        recordings[i].LastAppliedResourceIndex = resIdx;

                                        // BUG-C: re-restore the durable terminal-orbit
                                        // cannot-spawn-safely abandon that the tree-mutable-state
                                        // reset cleared above. This in-session ("returned-scene-
                                        // change" / quickload) path reconciles the in-memory
                                        // committed recordings rather than rebuilding them via
                                        // RecordingTreeRecordCodec, so without this a known-dead
                                        // terminal-orbit vessel re-spawns after every scene change.
                                        RestorePersistedTerminalAbandon(recordings[i], savedTreeRecNode);

                                        break;
                                    }
                                }
                            }
                        }
                    }

                    // Reconcile spawn state after all restore + strip operations (#168).
                    // If a recording's SpawnedVesselPersistentId points to a vessel that was
                    // stripped, reset the PID so the vessel can be re-spawned at the correct time.
                    // BUG-H: guid-aware — a surviving same-pid vessel from a DIFFERENT launch does
                    // not keep the recording's spawn state alive (it would otherwise block re-spawn).
                    if (isRevert)
                    {
                        var flightStateForReconcile = HighLogic.CurrentGame?.flightState;
                        if (flightStateForReconcile != null)
                            ReconcileSpawnStateAfterStrip(
                                CollectSurvivingVesselIdentities(flightStateForReconcile.protoVessels),
                                recordings);
                    }

                    // The Discard Re-fly lands in the rewind point's world: hold back what the
                    // committed future uses there, as a plain rewind does (rewind design 10.13).
                    // After the spawn-state restore above, so the claims the hold releases stay
                    // released.
                    if (refinedLoadKind == LoadKind.DiscardReFly)
                    {
                        loadPhase = "discard-refly-vessel-holds";
                        ApplyDiscardReFlyVesselHolds(recordings, KerbalsModule.ReadLoadedSaveUT());
                    }

                    // The revert prune's launch boundary, read by the route reconcile below.
                    double revertPruneCutoffUTForRoutes = double.NaN;
                    if (isRevert)
                    {
                        loadPhase = "revert-milestones";
                        // Restore milestone mutable state from .sfs.
                        // resetUnmatched: true — milestones created after the launch quicksave
                        // (not in the saved state) must be reset to unreplayed (-1). Hidden
                        // branch exclusion now comes from recording-id visibility, not a
                        // synthetic epoch bump.
                        MilestoneStore.RestoreMutableState(node, resetUnmatched: true);

                        // #434: Revert is a player declaration that "this mission never happened."
                        // Soft-unstash the pending tree — clear the slot but preserve sidecar files
                        // and event state so that a quicksave taken during the flight can still be
                        // F9'd back into (the ACTIVE_TREE node in that quicksave refers to the same
                        // recording ids, and both persistent.sfs and sidecars survive KSP's revert).
                        // Events stay in-memory and on-disk; recording-id visibility filters
                        // them out of the post-revert ledger walks until a matching quickload
                        // restores the tree.
                        //
                        // This runs BEFORE the limbo-dispatch block below — clearing the pending
                        // slot short-circuits the finalize/restore paths because the tree is gone.
                        //
                        // Call UnstashPendingTreeOnRevert unconditionally — even with no pending
                        // tree, the method clears any in-flight PendingScienceSubjects captured
                        // after the launch quicksave, which otherwise would leak onto the next
                        // committed recording. (The function no-ops cleanly when both the tree and
                        // the subject list are empty.) Only show the screen message when a tree
                        // was actually unstashed, since the user's only observable action was the
                        // revert itself.
                        bool hadPendingTree = PendingTreeSlot.HasPendingTree;
                        PendingTreeSlot.UnstashPendingTreeOnRevert();

                        // Drop orphan ledger actions earned live during the reverted flight that
                        // carry no recording id (e.g. launch-pad science transmitted during the
                        // PRELAUNCH window before auto-record starts, their milestone rewards, and
                        // the vessel rollout spend). UnstashPendingTreeOnRevert only filters
                        // recording-tied actions; these untagged ones would otherwise be
                        // re-applied by the recalc below, overriding stock KSP's revert. The
                        // cutoff is the launch UT (see ResolveRevertPruneCutoff): Revert-to-Launch
                        // uses loadedUT (the rewound clock == launch UT) and keeps the rollout on
                        // the pad (exclusive); Revert-to-editor does not rewind, so it anchors on
                        // the captured fresh-launch UT (site/mode independent), falling back to the
                        // VesselRollout-spend UT, and refunds the rollout (inclusive).
                        bool pruneInclusive;
                        double editorBoundaryUT = ResolveEditorRevertBoundaryUT(
                            currentFlightLaunchUT, Ledger.GetLatestUntaggedVesselBuildUT());
                        double pruneCutoffUT = ResolveRevertPruneCutoff(
                            revertKind, loadedUT, editorBoundaryUT, out pruneInclusive);
                        // Route events are not ledger rows: the in-session route reconcile below
                        // retires the reverted flight's route events at this cutoff (raised to the
                        // revert save's route state floor).
                        int prunedOrphans = PruneRevertedFlightLedgerRows(
                            pruneCutoffUT, pruneInclusive, revertKind);
                        revertPruneCutoffUTForRoutes = pruneCutoffUT;
                        if (prunedOrphans > 0)
                            ParsekLog.Info("Scenario",
                                $"Revert ({revertKind}): pruned {prunedOrphans} untagged ledger action(s) " +
                                $"{(pruneInclusive ? "at/after" : "after")} launchUT=" +
                                $"{pruneCutoffUT.ToString("R", CultureInfo.InvariantCulture)} " +
                                "(launch-pad/PRELAUNCH currency earned before auto-record)");
                        // The reverted flight's strategy conversion rows carry its own
                        // (never-committed) recording tag, so the orphan prune above skips them.
                        LedgerOrchestrator.PruneRevertedFlightStrategyConversionRows(
                            pruneCutoffUT, pruneInclusive);

                        if (hadPendingTree)
                        {
                            // ScreenMessages.PostScreenMessage calls into Unity's UI stack,
                            // which isn't wired up in xUnit test hosts — typed catch so
                            // real runtime errors still surface in logs.
                            try { ParsekLog.ScreenMessage("Recording unstashed (revert)", 4f); }
                            catch (System.NullReferenceException) { /* xUnit: no KSP UI */ }
                            catch (System.MissingMethodException) { /* xUnit: no KSP UI */ }
                            catch (System.TypeInitializationException) { /* xUnit: no KSP UI */ }
                        }

                        // Schedule committed resource deduction (singletons may not be ready yet)
                        ParsekLog.Verbose("Scenario", "Scheduling budget deduction coroutine (singletons may not be ready yet)");
                        budgetDeductionApplied = false;
                        StartCoroutine(ApplyBudgetDeductionWhenReady());
                    }
                    else
                    {
                        loadPhase = "scene-milestones";
                        // Scene change — restore milestone state without resetting unmatched.
                        MilestoneStore.RestoreMutableState(node);
                        ParsekLog.Verbose("Scenario", "Scene change — milestone state restored (resetUnmatched=false)");
                    }

                    // Dispatch pending-Limbo trees (Bug C fix: quickload-resume).
                    // The tree was stashed without finalization in StashActiveTreeAsPendingLimbo
                    // (Limbo) or pre-transitioned for a vessel switch in
                    // StashActiveTreeForVesselSwitch (LimboVesselSwitch — bug #266).
                    // #434: on revert the block above already discarded the pending tree, so
                    // HasPendingTree will be false here and the dispatch is skipped entirely.
                    loadPhase = "limbo-dispatch";
                    ClearPendingQuickloadResumeContext();
                    if (PendingTreeSlot.HasPendingTree
                        && (PendingTreeSlot.PendingTreeStateValue == PendingTreeState.Limbo
                            || PendingTreeSlot.PendingTreeStateValue == PendingTreeState.LimboVesselSwitch))
                    {
                        RecorderStateLog.RecState("OnLoad:limbo-dispatched", CaptureScenarioRecorderState());
                        var pendState = PendingTreeSlot.PendingTreeStateValue;
                        var limboOutcome = ClassifyLimboDispatch(isVesselSwitch, pendState);
                        if (limboOutcome == LimboDispatchOutcome.VesselSwitchRestore)
                        {
                            // Bug #266: tree was pre-transitioned at stash time. Just defer
                            // to OnFlightReady for the vessel-switch restore coroutine, which
                            // reinstalls the tree and (optionally) promotes the new active
                            // vessel from BackgroundMap.
                            ClearPendingQuickloadResumeContext();
                            ScheduleActiveTreeRestoreOnFlightReady = ActiveTreeRestoreMode.VesselSwitch;
                            ParsekLog.Info("Scenario",
                                $"OnLoad: pending-LimboVesselSwitch tree '{PendingTreeSlot.PendingTree?.TreeName}' " +
                                "deferred to OnFlightReady for vessel-switch restore (#266)");
                        }
                        else if (limboOutcome == LimboDispatchOutcome.SafetyNetFinalize)
                        {
                            // Safety net: Limbo state (NOT LimboVesselSwitch) but the
                            // OnLoad classifier still says vessel switch. This means the
                            // stash path missed the vessel-switch detection (e.g.
                            // pendingTreeDockMerge bailed out, or vesselSwitchPending was
                            // set after FinalizeTreeOnSceneChange ran). Fall back to the
                            // pre-#266 finalize behavior — better to lose the tree than
                            // to leak a half-transitioned state into the restore path.
                            // The bug #278 fix applies here too: surviving leaves keep
                            // their real situation, since this safety-net path is the
                            // exact case where vessels ARE still loaded in FlightGlobals
                            // (it's a vessel switch, not a quickload — no scene reset).
                            FinalizePendingLimboTreeForRevert();
                            ClearPendingQuickloadResumeContext();
                            ParsekLog.Info("Scenario",
                                "OnLoad: Limbo tree finalized on vessel switch (safety-net path; " +
                                "stash didn't pre-transition — usually means a guard at stash " +
                                "time bailed out, see #266)");
                        }
                        else
                        {
                            // Quickload or cold-start resume: defer restore to OnFlightReady.
                            // ParsekFlight.RestoreActiveTreeFromPending picks up the
                            // pending-Limbo tree and wires a fresh recorder to it.
                            ConfigurePendingQuickloadResumeContext(
                                PendingTreeSlot.PendingTree,
                                refinedLoadKind);
                            ScheduleActiveTreeRestoreOnFlightReady = ActiveTreeRestoreMode.Quickload;
                            ParsekLog.Info("Scenario",
                                $"OnLoad: pending-Limbo tree '{PendingTreeSlot.PendingTree?.TreeName}' " +
                                "deferred to OnFlightReady for quickload-resume");
                        }
                    }

                    // Handle pending recordings on non-revert scene exits to non-flight scenes.
                    // The pre-transition merge dialog (SceneExitInterceptor) is the
                    // primary path; this OnLoad path commits whatever a flight-exit left
                    // pending (one that bypassed our HighLogic.LoadScene prefix: KSP
                    // version drift, a foreign mod patch). It never asks: leaving a flight
                    // commits (owner ruling 2026-11-01), and the deferred merge dialog
                    // that used to ask here under autoMerge OFF was retired 2026-11-06
                    // (todo GUI-D5); AutoCommitPendingTreeOutsideFlight still reads the
                    // setting to pick its commit fidelity.
                    loadPhase = "pending-outside-flight";
                    if (!isRevert && HighLogic.LoadedScene != GameScenes.FLIGHT)
                    {
                        {
                            // #88 folded: landed/splashed no longer forces an approval
                            // dialog under autoMerge — the silent commit is now
                            // full-fidelity (spawn-at-end preserved), so it is safe for
                            // surviving vessels.

                            // Auto-discard idle-on-pad before auto-commit. Only for
                            // Finalized trees (Limbo trees are resume-flow stashes).
                            if (PendingTreeSlot.HasPendingTree
                                && PendingTreeSlot.PendingTreeStateValue == PendingTreeState.Finalized
                                && ParsekFlight.IsTreeIdleOnPad(PendingTreeSlot.PendingTree))
                            {
                                ParsekLog.Info("Scenario", "Idle on pad at scene exit — auto-discarding tree");
                                DiscardPendingTreeAndRecalculate(
                                    "scene-exit idle-on-pad auto-discard");
                            }

                            // Silent auto-commit: full-fidelity MergeCommit when the tree
                            // qualifies (Finalized, no active re-fly, real scene), else the
                            // lightweight ghost-only commit. Resources were applied live
                            // during the flight we are exiting (!isRevert checked upstream),
                            // so the tree is marked applied to disarm the lump-sum replay.
                            AutoCommitPendingTreeOutsideFlight("scene-exit");
                        }
                    }

                    // Routes on a load back in time (LoadReconcilePolicy Routes cells): RouteStore and
                    // the route event log stay in memory across an in-session load, so an F9, a stock
                    // revert or the Discard Re-fly load runs the go-back rewind's reconcile keyed to the
                    // loaded save, and any other in-session load does when route state lies after the
                    // save. Removes events only, never adds one, so it is OnLoad-safe.
                    loadPhase = "route-load-reconcile";
                    double loadedSaveUTForRoutes = KerbalsModule.ReadLoadedSaveUT();
                    try
                    {
                        Logistics.RouteLoadReconcile.ReconcileAtInSessionLoad(
                            refinedLoadKind,
                            Logistics.RouteLoadReconcile.ResolveCutoffUT(
                                refinedLoadKind, loadedSaveUTForRoutes, revertPruneCutoffUTForRoutes),
                            loadedSaveUTForRoutes,
                            node);
                    }
                    catch (Exception ex)
                    {
                        ParsekLog.Error(LoadReconcilePolicy.LogTag,
                            $"Route reconcile at in-session load threw {ex.GetType().Name}: {ex.Message}; " +
                            $"routes left as they were (kind={refinedLoadKind})");
                    }
                    Logistics.RouteEventLog.LogWalkSummary();

                    loadPhase = "ledger-recalculate";
                    // The ledger walks at the LOADED save's clock: inside a Space Center OnLoad
                    // Planetarium still reads the clock from before the load (QL-1 measured
                    // 1000.10 against the quicksave's 409.9), which would walk the committed
                    // rows the load moved back in front of the clock as if already reached.
                    double ledgerClockUT = ResolveInSessionLoadLedgerClockUT(
                        loadedSaveUTForRoutes, planetariumReady, loadedUT);
                    double inSessionLedgerCutoffUT = double.IsNaN(ledgerClockUT) ? loadedUT : ledgerClockUT;
                    // A Discard Re-fly load lands on the rewind point's pools after a session whose
                    // Re-Fly start walked the whole ledger (committed future included), so its first
                    // patch is a time-travel patch like the merge dialog's discard: it writes the
                    // ledger at the loaded clock and re-baselines the stock pool reference instead of
                    // measuring a leak (LoadReconcilePolicy, StockPoolReference category;
                    // REFLY-DISCARD-AFTER-REWIND-LOSES-COMMITTED-FUTURE).
                    bool inSessionLedgerUsedCutoff = RecalculateLedgerForInSessionLoad(
                        isRevert, HighLogic.LoadedScene, !double.IsNaN(ledgerClockUT),
                        inSessionLedgerCutoffUT,
                        planetariumReady ? loadedUT : double.NaN,
                        reFlyDiscard: refinedLoadKind == LoadKind.DiscardReFly);
                    // KSP loads the save's SCENARIO modules in file order, and R&D and
                    // Reputation usually come after this one: the patch above skipped
                    // their pools, and their own OnLoad then installs the save's values.
                    // Patch again once every currency module has loaded.
                    CurrencySingletons expectedCurrency = CurrencyScenarioReadiness.ExpectedForCurrentGame();
                    CurrencySingletons presentCurrency = CurrencyScenarioReadiness.PresentNow();
                    CurrencySingletons loadedCurrency = CurrencyScenarioReadiness.LoadedNow();
                    if (ShouldRepatchAfterInSessionLoad(expectedCurrency, presentCurrency, loadedCurrency))
                    {
                        ParsekLog.Info("Scenario",
                            $"OnLoad: in-session ledger patch ran before the currency modules loaded " +
                            $"(expected={CurrencyScenarioReadiness.Format(expectedCurrency)} " +
                            $"present={CurrencyScenarioReadiness.Format(presentCurrency)} " +
                            $"loaded={CurrencyScenarioReadiness.Format(loadedCurrency)}); " +
                            "scheduling a re-patch once they have");
                        StartCoroutine(RepatchAfterInSessionLoadWhenCurrenciesLoaded(
                            inSessionLedgerUsedCutoff, inSessionLedgerCutoffUT,
                            refinedLoadKind == LoadKind.DiscardReFly));
                    }
                    if (KerbalLoadRepairDiagnostics.IsActive)
                        KerbalLoadRepairDiagnostics.EmitAndReset();
                    ParsekLog.Info("Scenario", $"{(isRevert ? "Revert" : "Scene change")} — preserving {recordings.Count} session recordings");
                    loadPhase = "sidecar-reconcile";
                    ReconcileReadableSidecarMirrorsOnLoadIfDisabled();
                    DiagnosticsComputation.EmitSceneLoadSnapshot(recordings.Count, HighLogic.LoadedScene.ToString());

                    // Phase 6 of Rewind-to-Staging: re-fly invocation drains
                    // the static context in the new scenario. Lives on the
                    // FLIGHT→FLIGHT branch because the RP quicksave always
                    // lands in FLIGHT and the invoker issues LoadScene(FLIGHT)
                    // from FLIGHT/SPACECENTER/TRACKSTATION.
                    loadPhase = "rewind-post-load";
                    DispatchRewindPostLoadIfPending(node);

                    // Phase 10 of Rewind-to-Staging (design §6.9 step 2):
                    // resume any interrupted staged-commit merge on the
                    // FLIGHT→FLIGHT branch too (quickload mid-merge, scene
                    // preservation, etc.).
                    if (ActiveMergeJournal != null)
                    {
                        loadPhase = "merge-journal";
                        MergeJournalOrchestrator.RunFinisher();
                    }

                    // Phase 13 load-time sweep (design §6.9): validate the
                    // re-fly session marker, gather + delete zombie
                    // provisionals, log orphan supersedes / tombstones,
                    // clear stray transient fields. Runs AFTER the Phase 6
                    // re-fly dispatch (which may have populated the marker)
                    // and the Phase 10 finisher (which may have cleared it),
                    // and BEFORE the Phase 11 reaper (whose input is the
                    // sweep's post-zombie-removal state).
                    loadPhase = "load-time-sweep";
                    LoadTimeSweep.Run();
                    RefreshPendingQuickloadTrimScope();

                    // The sweep runs after this branch's ledger recalculation: when it discarded a
                    // zombie attempt (rewind design section 2.12 Rule 3), walk the ledger again so
                    // the patched career no longer carries its rows, nor what it earned into the
                    // pools of a save written during its session (it files rows only at merge).
                    if (LoadTimeSweep.SweepDiscardedAnAttempt(
                            LoadTimeSweep.LedgerRowsRemovedByLastRun, LoadTimeSweep.ZombiesDiscardedByLastRun))
                    {
                        loadPhase = "load-time-sweep-recalculate";
                        ParsekLog.Info("Scenario",
                            $"OnLoad: load-time sweep discarded {LoadTimeSweep.ZombiesDiscardedByLastRun} zombie attempt recording(s) " +
                            $"and removed {LoadTimeSweep.LedgerRowsRemovedByLastRun} zombie ledger row(s) " +
                            "after the in-session recalculation; recalculating again");
                        RecalculateLedgerForInSessionLoad(
                            isRevert, HighLogic.LoadedScene, !double.IsNaN(ledgerClockUT),
                            inSessionLedgerCutoffUT,
                            planetariumReady ? loadedUT : double.NaN,
                            reFlyDiscard: true);
                    }

                    loadPhase = "test-batch-reconcile";
                    RunTestBatchCrashReconcile();

                    // Phase 11 housekeeping pass — same rationale as the
                    // cold-start branch below; runs after the finisher so
                    // live-session RPs stay put.
                    loadPhase = "rewind-point-reap";
                    RewindPointReaper.ReapOrphanedRPs();

                    // Memory's lists are final: collect files no save and no memory names
                    // (design-load-restores-the-save.md 4.5; rate-limited in-session).
                    loadPhase = "file-collection";
                    ParsekFileCollector.RunAtLoad(earlyLoadKind, coldBranch: false, scenario: this);

                    loadPhase = "vessel-names-apply";
                    VesselNameStore.RunSpawnLinkBackfill(isRevert ? "onload-tail:revert" : "onload-tail:scene-change");
                    VesselNameStore.ApplyAll(isRevert ? "onload-tail:revert" : "onload-tail:scene-change",
                        VesselNameLiveScope.ProtosOnly);
                    // The revert target of the flight that starts here (O2); a revert keeps the
                    // entry it went back to.
                    if (HighLogic.LoadedScene == GameScenes.FLIGHT && !isRevert)
                        RememberFlightEntryLedgerRows("in-session:" + refinedLoadKind);
                    loadedRecordingCount = recordings.Count;
                    loadStatus = isRevert ? "returned-revert" : "returned-scene-change";
                    return;
                }

                loadPhase = "cold-start";
                initialLoadDone = true;
                // Recording hydration from here to the end of this OnLoad reads the sidecar
                // versions the save names (design-load-restores-the-save.md 4.4).
                SidecarVersionResolution.BeginColdLoad(node);

                // A recording file the collector quarantined goes back before hydration when
                // this save names it (design-load-restores-the-save.md 4.5).
                loadPhase = "quarantine-restore";
                ParsekFileCollector.RestoreQuarantinedNamedBy(node);

                // A game load ends any window in which an in-game batch's persistent.sfs
                // restore was still the intended final state of a save (the batch's own
                // baseline quickload precedes its teardown latch, so this never disarms the
                // H38-H41 teardown -> FlushAndQuit suppression).
                InGameTests.InGameTestRunner.NotifyStateMutatedAfterBatchBaselineRestore(
                    "game load (cold OnLoad)");

                // The stale-pending discard below deletes the discarded tree's sidecars,
                // and its committed-overlap guard reads the in-memory committed store -
                // which LoadRecordingTrees has NOT populated yet at this point. A pending
                // tree that is a copy-on-write clone of a committed one (the
                // TryTakeCommittedTreeForSpawnedVesselRestore overlap, ids PRESERVED) would
                // therefore take the COMMITTED tree's .prec files down with it, and every
                // later save would drop that tree as unserializable. Publish the save
                // node's own committed ids for exactly this window.
                loadPhase = "durable-committed-id-hint";
                RecordingStore.SetDurableCommittedRecordingIdHint(
                    CollectCommittedRecordingIdsFromScenarioNode(node));
                try
                {
                    loadPhase = "stale-pending-discard";
                    DiscardStalePendingState();
                }
                finally
                {
                    RecordingStore.ClearDurableCommittedRecordingIdHint();
                }

                loadPhase = "clear-committed";
                RecordingStore.ClearCommittedInternal();

                // Validate chain integrity before any playback
                loadPhase = "chain-validation";
                RecordingChainQueries.ValidateChains();

                // The vessel name decisions read only their own node; loading them before the
                // trees keeps a tree-load fault from dropping them on the next save.
                loadPhase = "vessel-names";
                VesselNameStore.Load(node);

                loadPhase = "recording-trees";
                LoadRecordingTrees(node, recordings);
                loadedRecordingCount = recordings.Count;
                // GC-3: one screen message per load that kept trees of another schema
                // generation inert (posted once the scene settles).
                string inertTreeNotice = InertRecordingTrees.TakeLoadNotice();
                if (inertTreeNotice != null)
                    ParsekHarmony.QueueNoticeForSettledScene(inertTreeNotice);
                loadPhase = "sanitize-nonloopable-loop";
                RecordingStore.SanitizeNonLoopableLoopPlayback();
                loadPhase = "missions";
                MissionStore.Load(node);
                // The committed tree + mission state finished loading for this cold load, so the
                // load-fault guard may now trust an empty committed store as legitimate. A throw
                // in either call above leaves this false (guard fires on the next save); the later
                // reconcile steps operate on already-loaded data and do not clear the store.
                committedTreeStateLoaded = true;
                // Protect missions whose tree is parked (a quickload-resume isActive / isPending
                // node restored LATER in this OnLoad by TryRestoreActiveTreeNode, or an already
                // stashed pending tree) so PruneOrphans does not strip the mission name + loop
                // settings of a tree that survives but is not yet committed. Limbo-tree data-loss fix.
                List<string> parkedTreeIds = CollectParkedTreeIdsForMissionPrune(node);
                MissionStore.PruneOrphans(
                    RecordingStore.CommittedTrees,
                    parkedTreeIds);
                MissionStore.EnsureDefaultsForTrees(RecordingStore.CommittedTrees);
                // Parked ids passed so the M-MIS-8 cross-tree link reconcile DEFERS while a
                // parked tree (possibly a link's foreign tree) is not committed yet - dropping
                // the link now would permanently lose the player's partner-journey selection.
                MissionStore.ReconcileSelections(RecordingStore.CommittedTrees, parkedTreeIds);
                // Trees passed so the one-loop invariant also covers SPANNED tree sets
                // (a cross-tree-linked loop vs a loop on its linked foreign tree).
                MissionStore.NormalizeOneLoopPerTree(RecordingStore.CommittedTrees);

                // Cold-start active-tree restore (quickload-resume cold path).
                // When the player quits KSP mid-flight and later relaunches + "Resume Saved
                // Game", OnLoad runs with initialLoadDone=false and falls through to this
                // block. TryRestoreActiveTreeNode here picks up any isActive=True tree
                // from the save and stashes it into pending-Limbo so OnFlightReady's
                // restore coroutine can resume recording — same path used by in-session
                // quickload. Without this, cold-start resume silently drops the active
                // tree and the player's in-progress mission fragments just like the
                // original Bug C scenario.
                loadPhase = "cold-active-tree-restore";
                ClearPendingQuickloadResumeContext();
                if (TryRestoreActiveTreeNode(
                        node, EarlyLoadKind.Cold, HighLogic.LoadedScene == GameScenes.FLIGHT))
                {
                    // Flag the coroutine to run on OnFlightReady so the active vessel is
                    // available for name matching. Cold start always lands in flight for
                    // a "Resume" action, so OnFlightReady will fire.
                    //
                    // Bug #266: TryRestoreActiveTreeNode picks the stash state based on
                    // whether the saved tree had an active recording. Pick the matching
                    // restore mode here:
                    //   - LimboVesselSwitch (outsider state at save time) → VesselSwitch
                    //   - Limbo (active recording present) → Quickload
                    ScheduleActiveTreeRestoreOnFlightReady =
                        PendingTreeSlot.PendingTreeStateValue == PendingTreeState.LimboVesselSwitch
                            ? ActiveTreeRestoreMode.VesselSwitch
                            : ActiveTreeRestoreMode.Quickload;
                    if (ScheduleActiveTreeRestoreOnFlightReady == ActiveTreeRestoreMode.Quickload)
                        ConfigurePendingQuickloadResumeContext(PendingTreeSlot.PendingTree, LoadKind.Cold);
                    else
                        ClearPendingQuickloadResumeContext();
                    ParsekLog.Info("Scenario",
                        $"OnLoad: cold-start active tree detected (state={PendingTreeSlot.PendingTreeStateValue}) — " +
                        $"deferred to OnFlightReady as {ScheduleActiveTreeRestoreOnFlightReady}");
                }
                // The save's restore-attempt events (memory only) follow the active tree they
                // belong to; dropped otherwise (design 4.3, #866).
                loadPhase = "cold-attempt-events";
                ApplySaveAttemptEventsAfterActiveTreeRestore();
                bool coldActiveTreeRestoredFromSave =
                    PendingTreeSlot.PendingTreeStateValue == PendingTreeState.Limbo
                    || PendingTreeSlot.PendingTreeStateValue == PendingTreeState.LimboVesselSwitch;
                bool coldPendingTreeRestoredFromSave = TryRestorePendingTreeNode(
                    node, coldActiveTreeRestoredFromSave);
                if (coldPendingTreeRestoredFromSave)
                {
                    string pendingRestoreDisposition = PendingTreeSlot.HasSavedPendingTreeDuringActiveRestore
                        ? "preserved alongside active-tree restore"
                        : "restored as finalized pending";
                    ParsekLog.Info("Scenario",
                        $"OnLoad: cold-start pending tree detected (state={PendingTreeSlot.PendingTreeStateValue}) — " +
                        pendingRestoreDisposition);
                }

                // Restore milestone mutable state (LastReplayedEventIndex) from .sfs
                loadPhase = "milestones";
                MilestoneStore.RestoreMutableState(node);

                // Reconcile ledger against loaded recordings (prunes orphaned actions, recalculates)
                // at the loaded save's own clock (design 4.1): never the clock from before the load.
                loadPhase = "ledger-load";
                var validIds = BuildValidRecordingIdsForKspLoad();
                double coldPlanetariumUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0.0;
                double coldLoadedSaveUT = ReadLoadedFlightStateUTRaw();
                double coldClockUT = ResolveColdLoadClockUT(coldLoadedSaveUT, coldPlanetariumUT);
                ParsekLog.Info("Scenario",
                    "OnLoad: cold clock=" + coldClockUT.ToString("R", CultureInfo.InvariantCulture)
                    + " (saveUT=" + coldLoadedSaveUT.ToString("R", CultureInfo.InvariantCulture)
                    + " planetariumUT=" + coldPlanetariumUT.ToString("R", CultureInfo.InvariantCulture)
                    + " coldReason=" + (coldReason ?? "none") + ")");
                double reconcileUT = coldClockUT;
                LedgerOrchestrator.OnKspLoad(
                    validIds,
                    reconcileUT,
                    useCurrentUtCutoffForFutureActions:
                        IsCurrentUtCutoffSupportedScene(HighLogic.LoadedScene),
                    loadDroppedRecordingIds: TakeLoadDroppedRecordingIds());

                // Supply routes (design §4.7). Loads after recordings + ledger so
                // Phase 5 source-ref validation has the ERS / ELS data it needs.
                // RevalidateSources is folded into the "routes" phase: load + validate
                // is one logical operation, and missing-source / fingerprint-drift
                // transitions must run on every load before any UI / scheduler sees
                // a stale Active status.
                loadPhase = "routes";
                RouteStore.LoadRoutesFrom(node);
                RouteStore.RevalidateSources("OnLoad");

                // Mutual-exclusion reconcile (design §0.6): a tree is EITHER a supply route
                // OR a manually looped recording/mission, never both. Runs AFTER MissionStore
                // load/normalize (above) and AFTER RouteStore load + RevalidateSources (so
                // route statuses (and thus BindsTree decisions) are final). For every
                // route-bound tree, clear both the mission loop and any per-recording loop a
                // hand-edited or legacy save might carry on that tree. Route looping wins.
                loadPhase = "route-loop-reconcile";
                {
                    double reconcileUtForLoops = coldClockUT;
                    var boundTreeIds = RouteTreeGuard.BoundTreeIds();
                    int reconciledTreeCount = 0;
                    for (int bi = 0; bi < boundTreeIds.Count; bi++)
                    {
                        RouteTreeGuard.ForceClearManualLoopForRouteTree(
                            boundTreeIds[bi], reconcileUtForLoops);
                        reconciledTreeCount++;
                    }
                    ParsekLog.Info("RouteGuard",
                        $"OnLoad route-loop reconcile: cleared manual loops on {reconciledTreeCount} route-bound tree(s)");
                }

                // Schedule deferred seeding: during OnLoad, Funding/R&D/Reputation singletons
                // may exist but have not loaded their save data yet (KSP loads scenarios in
                // parallel). The deferred coroutine waits for singletons to be ready, then
                // seeds initial balances and recalculates so the ledger has correct values.
                loadPhase = "deferred-seed";
                StartCoroutine(DeferredSeedAndRecalculate());

                // Diagnostic summary of loaded recordings with UT context. Per-recording
                // status used to be enumerated at Verbose, but a save with hundreds of
                // recordings (synthetic showcases, KSC eligibles, long histories) emits
                // hundreds of identical "future (starts in …s)" lines per scenario load.
                // Bucket the statuses into counts; keep IN_PROGRESS detail (rare and
                // most useful when debugging spawn timing) at Verbose.
                loadPhase = "load-summary";
                double loadUT = coldClockUT;
                ParsekLog.Info("Scenario",
                    string.Format(CultureInfo.InvariantCulture,
                        "Scenario load summary — UT: {0:F0}, {1} recording(s)",
                        loadUT, recordings.Count));
                int futureCount = 0, inProgressCount = 0, pastCount = 0;
                List<string> inProgressDetail = null;
                for (int i = 0; i < recordings.Count; i++)
                {
                    var loadedRec = recordings[i];
                    double duration = loadedRec.EndUT - loadedRec.StartUT;
                    if (loadUT < loadedRec.StartUT)
                    {
                        futureCount++;
                    }
                    else if (loadUT <= loadedRec.EndUT)
                    {
                        inProgressCount++;
                        if (inProgressDetail == null)
                            inProgressDetail = new List<string>();
                        string pct = duration > 0
                            ? string.Format(CultureInfo.InvariantCulture, "({0:F0}%)",
                                (loadUT - loadedRec.StartUT) / duration * 100)
                            : "";
                        inProgressDetail.Add($"  #{i}: \"{loadedRec.VesselName}\" — IN PROGRESS {pct}".TrimEnd());
                    }
                    else
                    {
                        pastCount++;
                    }
                }
                ParsekLog.Verbose("Scenario",
                    $"Load summary buckets: future={futureCount} in-progress={inProgressCount} past={pastCount}");
                if (inProgressDetail != null)
                {
                    foreach (var line in inProgressDetail)
                        ParsekLog.Verbose("Scenario", line);
                }

                if (CrewReservationManager.CrewReplacements.Count > 0)
                {
                    ParsekLog.Info("Scenario", $"Crew reservations active ({CrewReservationManager.CrewReplacements.Count}):");
                    foreach (var kvp in CrewReservationManager.CrewReplacements)
                        ParsekLog.Info("Scenario", $"  {kvp.Key} -> replacement: {kvp.Value}");
                }

                // Run recording optimization pass (merge redundant segments, split monolithic ones)
                loadPhase = "optimization";
                CommittedListOptimizer.RunOptimizationPass();

                // Auto-unreserve crew for recordings whose EndUT has already passed
                // but vessel was never spawned. Skip at SpaceCenter — ParsekKSC now
                // handles spawning there (bug #99), so nulling the snapshot here would
                // pre-empt the KSC spawn.
                loadPhase = "crew-auto-unreserve";
                double currentUT = coldClockUT;
                if (HighLogic.LoadedScene != GameScenes.SPACECENTER)
                {
                    for (int i = 0; i < recordings.Count; i++)
                    {
                        var rec = recordings[i];
                        if (rec.LoopPlayback) continue;
                        if (rec.VesselSnapshot != null && !rec.VesselSpawned && currentUT > rec.EndUT)
                        {
                            CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                            rec.VesselSnapshot = null;
                            rec.VesselSpawned = true;
                            ScenarioLog($"[Parsek Scenario] Auto-unreserved crew for recording #{i} " +
                                $"({rec.VesselName}) — EndUT passed without spawn");
                        }
                    }
                }

                // Handle pending recordings outside Flight (Esc > Abort Mission → Space Center path).
                // Always auto-commit on main menu (game is being unloaded).
                loadPhase = "pending-outside-flight";
                if (HighLogic.LoadedScene != GameScenes.FLIGHT &&
                    PendingTreeSlot.HasPendingTree)
                {
                    // Auto-discard idle-on-pad recordings before auto-committing.
                    // ONLY for Finalized-state trees — those come from a
                    // completed flight session that produced a terminal-state
                    // merge-dialog candidate. For Limbo / LimboVesselSwitch
                    // trees (quickload-resume stashes populated from the
                    // save's ACTIVE_TREE node by `TryRestoreActiveTreeNode`),
                    // the maxDist values can legitimately be near zero right
                    // after a rewind reload because the provisional recording
                    // was just created and hasn't moved yet. Auto-discarding
                    // those would silently wipe the player's entire mission
                    // tree plus its Rewind Point and quicksave on every
                    // subsequent SPACECENTER load, as seen in
                    // logs/2026-04-25_0103_recordings-vanish-on-load:
                    //   TryRestoreActiveTreeNode: stashed active tree
                    //     'Kerbal X' (8 recording(s)) into pending-Limbo slot
                    //   IsTreeIdleOnPad: all 8 recordings within 30m — idle on pad
                    //   Idle on pad — auto-discarding pending tree
                    //   PurgeTree deleted rewind quicksave rp=... path=...
                    //   Discarded pending tree 'Kerbal X' (state=Limbo)
                    // The normal idle-on-pad use case (player starts recording,
                    // doesn't launch, exits to KSC) produces a Finalized state
                    // via CommitTreeSceneExit, so the gate still fires there.
                    if (PendingTreeSlot.PendingTreeStateValue == PendingTreeState.Finalized
                        && ParsekFlight.IsTreeIdleOnPad(PendingTreeSlot.PendingTree))
                    {
                        ScenarioLog("[Parsek Scenario] Idle on pad — auto-discarding pending tree");
                        DiscardPendingTreeAndRecalculate(
                            "outside-flight idle-on-pad auto-discard");
                    }
                    else if (PendingTreeSlot.PendingTreeStateValue != PendingTreeState.Finalized)
                    {
                        ParsekLog.Verbose("Scenario",
                            $"Idle-on-pad auto-discard skipped: pending tree state is " +
                            $"{PendingTreeSlot.PendingTreeStateValue} (not Finalized), " +
                            "resume/restore flow will run instead");
                    }

                    // Silent auto-commit: full-fidelity MergeCommit when the tree
                    // qualifies (autoMerge, Finalized, no active re-fly, real scene),
                    // else the lightweight ghost-only commit. MAINMENU (game
                    // unloading) and non-autoMerge always fall to ghost-only. The
                    // originating flight already applied its resources live, so the
                    // commit marks the tree applied to disarm the lump-sum replay.
                    // Never a dialog here: the deferred merge dialog was retired
                    // 2026-11-06 (todo GUI-D5).
                    AutoCommitPendingTreeOutsideFlight("cold-load outside-flight");
                }

                loadPhase = "sidecar-reconcile";
                ReconcileReadableSidecarMirrorsOnLoadIfDisabled();

                // Scene load memory snapshot (once per load, after all recordings are loaded)
                DiagnosticsComputation.EmitSceneLoadSnapshot(recordings.Count, HighLogic.LoadedScene.ToString());

                // Phase 6 of Rewind-to-Staging (design §6.3 step 4 / §6.4):
                // if a re-fly invocation is pending, the preceding LoadGame was
                // triggered by RewindInvoker.StartInvoke. Drain the static
                // RewindInvokeContext now — Restore → Strip → Activate →
                // AtomicMarkerWrite — in the new scenario, synchronously, NO
                // coroutine (the old scenario's coroutine was torn down with
                // the scene).
                loadPhase = "rewind-post-load";
                DispatchRewindPostLoadIfPending(node);

                // Phase 10 of Rewind-to-Staging (design §6.9 step 2): if a
                // staged-commit merge crashed mid-way, the scenario's
                // MergeJournal persisted across the load. The finisher drives
                // the remaining steps to completion from whatever phase the
                // journal reached (no phase rolls back). Runs AFTER Phase 6
                // so any re-fly invocation that was interrupted can still
                // rehydrate its context before the journal finisher decides
                // what to do with the session marker.
                if (ActiveMergeJournal != null)
                {
                    loadPhase = "merge-journal";
                    MergeJournalOrchestrator.RunFinisher();
                }

                // Phase 13 of Rewind-to-Staging (design §6.9 full load-time
                // sweep): marker validation + zombie provisional cleanup +
                // orphan supersede/tombstone log + stray-field clearing.
                // Must run after the finisher (which may have cleared the
                // marker) and before the Phase 11 reaper (whose input is the
                // sweep's zombie-removed state).
                loadPhase = "load-time-sweep";
                LoadTimeSweep.Run();
                RefreshPendingQuickloadTrimScope();

                loadPhase = "test-batch-reconcile";
                RunTestBatchCrashReconcile();

                // Phase 11 of Rewind-to-Staging (design §6.8 load-time sweep):
                // housekeeping pass for RPs orphaned by merges that crashed
                // between TagRpsForReap and the reaper, or whose slots went
                // Immutable later via a non-rewind code path. Runs after the
                // finisher so we never try to reap an RP whose session is
                // still live.
                loadPhase = "rewind-point-reap";
                RewindPointReaper.ReapOrphanedRPs();

                // Memory's lists are final: delete the files of a quit-without-saving flight
                // this load dropped (NoSaveSceneExit), then collect files no save and no memory
                // names (design-load-restores-the-save.md 4.5).
                loadPhase = "quit-without-saving-reap";
                NoSaveSceneExit.ReapDroppedFlightSidecars(
                    RewindContext.IsRewinding,
                    ActiveReFlySessionMarker != null,
                    ActiveMergeJournal != null);

                loadPhase = "file-collection";
                ParsekFileCollector.RunAtLoad(earlyLoadKind, coldBranch: true, scenario: this);

                // After hydration repair and the journal finisher: re-apply every vessel name
                // decision to the recordings this load produced (design 7.4 / 7.6).
                loadPhase = "vessel-names-apply";
                VesselNameStore.RunSpawnLinkBackfill("onload-tail:cold");
                VesselNameStore.ApplyAll("onload-tail:cold", VesselNameLiveScope.ProtosOnly);
                if (HighLogic.LoadedScene == GameScenes.FLIGHT)
                    RememberFlightEntryLedgerRows("cold");
                loadedRecordingCount = recordings.Count;
                loadStatus = "completed";
            }
            catch (Exception ex)
            {
                loadStatus = "exception";
                LogScenarioLifecycleException("OnLoad", loadPhase, ex);
                throw;
            }
            finally
            {
                onLoadInProgress = false;
                SidecarVersionResolution.End("cold-onload");
                KerbalLoadRepairDiagnostics.Reset();
                // Always capture timing exactly once, even on exception.
                loadedRecordingCount = SafeCommittedRecordingCount();
                WriteLoadTiming(sw, loadedRecordingCount, loadPhase, loadStatus);
            }
        }

        // Set by TryRestoreActiveTreeNode, taken by the next ConfigurePendingQuickloadResumeContext
        // for the same tree id.
        private static QuicksaveTreeFacts lastRestoredQuicksaveTreeFacts;

        // Resume hints parsed from PARSEK_ACTIVE_TREE, consumed by the quickload-resume
        // path when FlightRecorder.StartRecording reopens the restored active tree.
        internal static string pendingActiveTreeResumeRewindSave;
        private static QuickloadResumeContext pendingQuickloadResumeContext;

        /// <summary>
        /// Signal to <see cref="ParsekFlight.OnFlightReady"/> that a pending-Limbo tree
        /// is waiting for a restore path. Set by OnLoad when it detects quickload (mode
        /// = Quickload) or vessel switch (mode = VesselSwitch) with a Limbo-state tree.
        /// Replaces the previous bool flag so the two paths can't conflict (#266 review).
        /// </summary>
        internal static ActiveTreeRestoreMode ScheduleActiveTreeRestoreOnFlightReady;

        /// <summary>
        /// Bounded frame budget for waiting on KSP's currency ScenarioModules to appear
        /// (~2 seconds at 60fps). Career loads them within a frame or two of Parsek's own
        /// module; sandbox never loads them at all, so every wait keyed on this constant must
        /// fall through and proceed with whatever is present rather than requiring the set.
        /// </summary>
        private const int CurrencySingletonWaitMaxFrames = 120;

        public void OnDestroy()
        {
            stateRecorder?.Unsubscribe();
            GameEvents.onVesselRecoveryProcessing.Remove(OnVesselRecoveryProcessing);
            GameEvents.onVesselRecoveryProcessingComplete.Remove(OnVesselRecoveryProcessingComplete);
            GameEvents.onVesselRecovered.Remove(OnVesselRecovered);
            GameEvents.OnVesselRecoveryRequested.Remove(OnVesselRecoveryRequested);
            GameEvents.onVesselTerminated.Remove(OnVesselTerminated);
            GameEvents.onVesselSwitching.Remove(OnVesselSwitching);
            GameEvents.OnDifficultySettingsDismiss.Remove(OnDifficultySettingsDismissed);
            // M4b Phase B2: drop the scene-switch escrow-clear subscription on
            // scenario teardown so a re-loaded scenario re-subscribes cleanly.
            GameEvents.onGameSceneSwitchRequested.Remove(OnGameSceneSwitchClearEscrow);
            // Main-menu session-reset hook (RegisterMainMenuHook). Now an instance
            // subscription, so it MUST be removed here or the delegate leaks a stale
            // Target into the next scenario instantiation.
            GameEvents.onGameSceneLoadRequested.Remove(OnMainMenuTransition);
            // #434: RevertDetector subscriptions are idempotent and persist for the
            // lifetime of the game session; tearing them down here so a scenario-module
            // shutdown doesn't leak dangling delegates if the session ends mid-flight.
            RevertDetector.Unsubscribe();
            ReFlyRevertButtonGate.Unsubscribe();
            // Phase 6 of Rewind-to-Staging: clear the per-RP precondition
            // cache so the dict does not grow unbounded across long sessions
            // (Fix 8). The cache is 60s-TTL'd anyway but long-lived scene loops
            // can accumulate entries faster than TTL cleanup.
            RewindInvoker.PreconditionCache.ClearAll();
            // Phase 2 (ghost rendering pipeline, design doc §18 Phase 2):
            // drop the in-memory anchor ε map on scenario teardown so a
            // subsequent scenario load starts with a clean slate.
            Parsek.Rendering.RenderSessionState.Clear("scenario-destroyed");
            // Phase 2 (Rewind-to-Staging): drop the Instance back-reference so
            // EffectiveState does not read stale scenario state after destruction.
            // Before that, hand this instance's staged lists to the next OnLoad of the session:
            // stock destroys every scenario module when a scene load is requested
            // (ScenarioRunner.OnGameSceneLoadRequested), before the next scene's Game.Load.
            if (ReferenceEquals(s_instance, this))
            {
                try
                {
                    CaptureInSessionStagedStateHandoff(InSessionStagedStateHandoff.CaptureReasonOnDestroy);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(InSessionStagedStateHandoff.Tag,
                        $"Staged-list handoff capture threw in OnDestroy: {ex.GetType().Name}: {ex.Message}");
                }
                s_instance = null;
            }
        }
    }
}
