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

        #region Game State Recording

        private GameStateRecorder stateRecorder;

        #endregion

        #region Rewind-to-Staging persistence (design sections 5.1 - 5.9)

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

        /// <summary>
        /// Pending stock-action intent armed by a Tracking Station Fly / KSC
        /// marker Fly / Map Switch-To click and not yet consumed by the
        /// FLIGHT-side scene-load tail. Null when no UI click is in flight.
        /// </summary>
        internal StockActionIntentMarker CurrentStockActionIntent => activeStockActionIntent;

        /// <summary>
        /// Active switch-segment session (live attempt or pending-after-reload).
        /// Null outside an active switch-segment attempt.
        /// </summary>
        internal SwitchSegmentSession ActiveSwitchSegmentSession => activeSwitchSegmentSession;

        /// <summary>
        /// Arms a new stock-action intent marker. Replaces any previously
        /// armed marker (which is then logged as superseded). Idempotent
        /// on a structurally identical marker; callers must rebuild the
        /// marker rather than mutate an armed one in place.
        /// </summary>
        internal void ArmStockActionIntent(StockActionIntentMarker marker)
        {
            if (marker == null) throw new ArgumentNullException(nameof(marker));
            if (activeStockActionIntent != null)
            {
                ParsekLog.Info("SwitchIntent",
                    $"intent superseded: prior intentId={activeStockActionIntent.IntentId:D} " +
                    $"action={activeStockActionIntent.Action} reason=stale-intent-superseded " +
                    $"new intentId={marker.IntentId:D} action={marker.Action}");
            }
            activeStockActionIntent = marker;
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info("SwitchIntent",
                $"armed: intentId={marker.IntentId:D} action={marker.Action} " +
                $"targetPid={marker.TargetVesselPersistentId.ToString(ic)} " +
                $"sourceScene={marker.SourceScene} " +
                $"capturedUT={marker.CapturedUT.ToString("R", ic)} " +
                $"capturedRealtime={marker.CapturedRealtime.ToString("R", ic)}");
        }

        /// <summary>
        /// Clears the current stock-action intent marker with a reason.
        /// Idempotent: clearing with no marker armed logs a Verbose line
        /// and returns.
        /// </summary>
        internal void ClearStockActionIntent(string reason)
        {
            if (activeStockActionIntent == null)
            {
                ParsekLog.Verbose("SwitchIntent",
                    $"clear no-op: no marker armed (reason={reason ?? "<none>"})");
                return;
            }
            var marker = activeStockActionIntent;
            activeStockActionIntent = null;
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info("SwitchIntent",
                $"cleared: intentId={marker.IntentId:D} action={marker.Action} " +
                $"targetPid={marker.TargetVesselPersistentId.ToString(ic)} " +
                $"reason={reason ?? "<none>"}");
        }

        /// <summary>
        /// Arms an active switch-segment session. Replaces any previously
        /// armed session (which is logged as superseded).
        /// </summary>
        internal void ArmSwitchSegmentSession(SwitchSegmentSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (activeSwitchSegmentSession != null)
            {
                ParsekLog.Info("SwitchSegment",
                    $"session superseded: prior sessionId={activeSwitchSegmentSession.SessionId:D} " +
                    $"reason=session-superseded new sessionId={session.SessionId:D}");
            }
            activeSwitchSegmentSession = session;
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info("SwitchSegment",
                $"armed: sessionId={session.SessionId:D} intentId={session.IntentId:D} " +
                $"entryReason={session.EntryReason} " +
                $"treeId={session.TreeId ?? "<null>"} " +
                $"parentRecordingId={session.ParentRecordingId ?? "<null>"} " +
                $"activeSegmentRecordingId={session.ActiveSegmentRecordingId ?? "<null>"} " +
                $"sourcePid={session.SourceVesselPersistentId.ToString(ic)} " +
                $"focusedPid={session.FocusedVesselPersistentId.ToString(ic)} " +
                $"switchUT={session.SwitchUT.ToString("R", ic)}");
        }

        /// <summary>
        /// Clears the active switch-segment session with a reason.
        /// Idempotent: clearing with no session armed logs Verbose only.
        /// </summary>
        internal void ClearSwitchSegmentSession(string reason)
        {
            if (activeSwitchSegmentSession == null)
            {
                ParsekLog.Verbose("SwitchSegment",
                    $"clear no-op: no session armed (reason={reason ?? "<none>"})");
                return;
            }
            var session = activeSwitchSegmentSession;
            activeSwitchSegmentSession = null;
            ParsekLog.Info("SwitchSegment",
                $"cleared: sessionId={session.SessionId:D} intentId={session.IntentId:D} " +
                $"entryReason={session.EntryReason} reason={reason ?? "<none>"}");
        }

        /// <summary>
        /// Called by <see cref="LoadRewindStagingState"/> right after the intent
        /// marker is loaded. Applies the documented cross-run / TTL / UT
        /// regression staleness checks and clears the marker with the
        /// appropriate reason on a miss. Fresh markers stay armed for the
        /// future Phase C consume site to act on.
        /// </summary>
        private void ValidateLoadedStockActionIntentFreshness()
        {
            if (activeStockActionIntent == null)
                return;
            float currentRealtime = Time.realtimeSinceStartup;
            double currentUT = Planetarium.fetch != null
                ? Planetarium.GetUniversalTime()
                : activeStockActionIntent.CapturedUT;
            var classification = StockActionIntentMarker.EvaluateStaleness(
                activeStockActionIntent,
                ParsekProcess.ProcessSessionId,
                currentRealtime,
                currentUT);
            switch (classification)
            {
                case StockActionIntentStaleness.StaleCrossRun:
                    ClearStockActionIntent("stale-cross-run");
                    return;
                case StockActionIntentStaleness.StaleIntentTtlExpired:
                case StockActionIntentStaleness.StaleIntentUtRegressed:
                    ClearStockActionIntent("stale-intent");
                    return;
                case StockActionIntentStaleness.Fresh:
                default:
                    ParsekLog.Info("SwitchIntent",
                        $"intent kept-armed on OnLoad: intentId={activeStockActionIntent.IntentId:D} " +
                        $"action={activeStockActionIntent.Action} " +
                        $"targetPid={activeStockActionIntent.TargetVesselPersistentId}");
                    return;
            }
        }

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
        }

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
        internal static FreshLaunchUtAction DecideFreshLaunchUtAction(
            GameScenes loadedScene, bool isRevert, bool isVesselSwitch, bool isFreshLaunchStartup)
        {
            if (loadedScene != GameScenes.FLIGHT || isRevert || isVesselSwitch)
                return FreshLaunchUtAction.Leave;

            return isFreshLaunchStartup
                ? FreshLaunchUtAction.Capture
                : FreshLaunchUtAction.Clear;
        }

        internal static Func<double> CurrentTimelineUTProviderForTesting;

        internal static double GetCurrentTimelineUTForLedgerRecalc()
        {
            if (CurrentTimelineUTProviderForTesting != null)
                return CurrentTimelineUTProviderForTesting();

            return Planetarium.fetch != null
                ? Planetarium.GetUniversalTime()
                : double.MaxValue;
        }

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

        #endregion


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
        /// Pure decision: returns true if the game clock regressed across a
        /// scene transition, i.e. the player quickloaded/reverted/rewound.
        /// <paramref name="preChangeUT"/> is <c>-1.0</c> when unset (no scene
        /// change has been stamped, e.g. first load). The strict <c>&lt; 0.0</c>
        /// check (rather than <c>&lt;= 0.0</c>) preserves the legitimate
        /// <c>preChangeUT == 0.0</c> case for fresh sandbox saves that start at
        /// UT 0 — a scene change in the very first frame must still be able to
        /// detect a backwards quickload. Directly testable without Unity.
        /// </summary>
        internal static bool IsQuickloadOnLoad(
            double preChangeUT, double currentUT, double epsilon)
        {
            if (preChangeUT < 0.0) return false;
            if (epsilon < 0.0) return false;
            return currentUT < preChangeUT - epsilon;
        }

        /// <summary>
        /// Discards a pending tree from an abandoned future / stale context.
        /// #434: previously also cleared FlightResultsPatch deferred-results state; that patch
        /// is gone now. Kept as a single entry point so callers have a named reason-string slot
        /// for logging.
        /// </summary>
        internal static void DiscardPendingTreeAndAbandonDeferredFlightResults(string reason)
        {
            ParsekLog.Verbose("Scenario", $"DiscardPendingTree abandon path: {reason}");
            // Abandon path = quickload-backwards / revert / stale-pending-from-another-save.
            // KSP's economy is being rolled back (or belongs to a different save), so do NOT
            // re-home the discarded tree's contracts/science/milestones into the ledger —
            // that would credit economy KSP no longer reflects (see DiscardPendingTree).
            RecordingStore.DiscardPendingTree(preserveIrreversibleLiveGameplay: false);
        }

        /// <summary>
        /// Discards a pending tree after its live effects have already been allowed to stay
        /// in KSP (for example while waiting on a merge/discard decision), then immediately
        /// rebuilds state from the committed ledger.
        /// </summary>
        internal static void DiscardPendingTreeAndRecalculate(string reason)
        {
            if (!RecordingStore.HasPendingTree)
                return;

            ParsekLog.Verbose("Scenario", $"DiscardPendingTree recalc path: {reason}");
            RecordingStore.DiscardPendingTree();
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions(
                GetCurrentTimelineUTForLedgerRecalc(),
                "pending-tree-discard");
        }

        internal static bool ShouldUseCurrentUtCutoffForPostRewindFlightLoad(
            bool isRevert,
            bool loadedSceneSupportsCurrentUtCutoff,
            bool planetariumReady,
            bool hasPendingTree,
            ActiveTreeRestoreMode restoreMode,
            bool hasLiveRecorder,
            bool hasActiveUncommittedTree,
            bool hasFutureLedgerActions)
        {
            return !isRevert
                && loadedSceneSupportsCurrentUtCutoff
                && planetariumReady
                && !hasPendingTree
                && restoreMode == ActiveTreeRestoreMode.None
                && !hasLiveRecorder
                && !hasActiveUncommittedTree
                && hasFutureLedgerActions;
        }

        internal static bool IsCurrentUtCutoffSupportedScene(GameScenes scene)
        {
            return scene == GameScenes.FLIGHT || scene == GameScenes.SPACECENTER;
        }

        /// <summary>
        /// Resolves the launch-boundary cutoff for the revert orphan-ledger prune. Pure so the
        /// cutoff/strictness contract is unit-testable outside the OnLoad lifecycle.
        ///
        /// <para>Revert-to-Launch rewinds the game clock to the launch instant, so
        /// <paramref name="loadedUT"/> is the exact launch UT; the at-launch rollout is kept
        /// (the vessel stays on the pad), hence <paramref name="inclusive"/>=false. Revert to
        /// the editor (VAB/SPH, <see cref="RevertKind.Prelaunch"/>) does NOT rewind the clock, so
        /// <paramref name="loadedUT"/> is the revert-moment UT (after the in-flight actions) and
        /// is useless as a launch boundary. <paramref name="editorBoundaryUT"/> (the captured
        /// fresh-launch UT, else the VesselRollout-spend UT, via
        /// <see cref="ResolveEditorRevertBoundaryUT"/>) is the real launch UT, and the rollout is
        /// dropped too (KSP refunds it), hence <paramref name="inclusive"/>=true. When neither is
        /// available (NaN, e.g. a quickload-resumed free / science-mode vessel) the editor case
        /// falls back to <paramref name="loadedUT"/>, which prunes nothing harmful rather than
        /// risking a wrong cutoff.</para>
        /// </summary>
        internal static double ResolveRevertPruneCutoff(
            RevertKind revertKind, double loadedUT, double editorBoundaryUT, out bool inclusive)
        {
            if (revertKind == RevertKind.Prelaunch)
            {
                inclusive = true;
                return double.IsNaN(editorBoundaryUT) ? loadedUT : editorBoundaryUT;
            }

            inclusive = false;
            return loadedUT;
        }

        /// <summary>
        /// Scene-load follow-up recalculation for post-rewind current-timeline
        /// case where the loaded UT is still behind future committed actions.
        /// Filters the walk to the already-captured loaded UT while preserving the
        /// normal patch-deferral and same-branch repeatable-record behavior.
        /// </summary>
        internal static void RecalculateAndPatchForPostRewindFlightLoad(double loadedUT)
        {
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(loadedUT, "post-rewind-load");
        }

        /// <summary>
        /// #434 follow-up: dispatch-level guard that decides whether the OnLoad
        /// quickload-discard branch should fire. The pure overload takes the
        /// three scene-classification bits plus the active re-fly session bit so
        /// it can be unit-tested in isolation — the <see cref="OnLoad"/> call
        /// site it gates is itself not reachable from xUnit (ScenarioModule
        /// lifecycle).
        ///
        /// The truth table that matters:
        /// <list type="bullet">
        ///   <item>Pure F5/F9 quickload (<c>isRevert=false</c>, UT back, flight-to-flight): <b>true</b> — hard-discard the stashed-this-transition tree.</item>
        ///   <item>Revert to Launch (<c>isRevert=true</c>, UT back, flight-to-flight): <b>false</b> — the revert branch owns pending-tree handling via <see cref="RecordingStore.UnstashPendingTreeOnRevert"/>, which preserves sidecar files for F9-from-flight-quicksave.</item>
        ///   <item>Active re-fly / retry invoke (<c>isReFlySessionActive=true</c>): <b>false</b> — the session owns the pending tree and rewind point being reloaded.</item>
        ///   <item>Scene reload with same UT: <b>false</b> — nothing to discard.</item>
        /// </list>
        /// </summary>
        internal static bool ShouldRunQuickloadDiscard(
            bool utWentBackwards, bool isFlightToFlight, bool isRevert)
        {
            return ShouldRunQuickloadDiscard(
                utWentBackwards,
                isFlightToFlight,
                isRevert,
                IsReFlySessionActiveForQuickloadDiscard());
        }

        internal static bool ShouldRunQuickloadDiscard(
            bool utWentBackwards,
            bool isFlightToFlight,
            bool isRevert,
            bool isReFlySessionActive)
        {
            return utWentBackwards
                && isFlightToFlight
                && !isRevert
                && !isReFlySessionActive;
        }

        /// <summary>
        /// True when quickload discard must treat a re-fly session as active.
        /// This includes the normal persisted marker and the retry/invoke window
        /// before <see cref="RewindInvoker.AtomicMarkerWrite"/> recreates the
        /// marker from <see cref="RewindInvokeContext"/>.
        /// </summary>
        internal static bool IsReFlySessionActiveForQuickloadDiscard()
        {
            var scenario = Instance;
            if (!object.ReferenceEquals(null, scenario)
                && scenario.ActiveReFlySessionMarker != null)
                return true;

            return RewindInvokeContext.Pending;
        }

        /// <summary>
        /// True when an active Re-Fly session is operating under the
        /// in-place continuation contract: <see cref="ActiveReFlySessionMarker"/>
        /// is non-null AND its <c>InPlaceContinuation</c> flag is set.
        ///
        /// <para>
        /// Stricter than <see cref="IsReFlySessionActiveForQuickloadDiscard"/>:
        /// the placeholder-mode marker (where <see cref="RewindInvoker.AtomicMarkerWrite"/>
        /// could not eagerly attach the fork to a tree because PID changed
        /// or the chain tip was orphaned) returns FALSE here. The OnFlightReady
        /// recorder-restore carve-out relies on the in-place marker swap
        /// (<see cref="ReFlySessionMarker.ResolveInPlaceContinuationTarget"/>);
        /// in placeholder mode that swap returns <c>placeholder-pattern</c>
        /// and the wait loop targets the pre-rewind PID, so the carve-out
        /// must NOT fire there — instead, the original merge-dialog fallback
        /// runs and the player can discard the orphan attempt.
        /// </para>
        ///
        /// <para>
        /// Unlike <see cref="IsReFlySessionActiveForQuickloadDiscard"/>,
        /// this helper does NOT consider <see cref="RewindInvokeContext.Pending"/>:
        /// during the brief invoke window the marker is null and the
        /// in-place-vs-placeholder decision has not been made yet. Callers
        /// that need to cover the brief window should combine this with a
        /// separate <c>RewindInvokeContext.Pending</c> check.
        /// </para>
        /// </summary>
        internal static bool IsReFlyInPlaceContinuationActive()
        {
            var scenario = Instance;
            if (object.ReferenceEquals(null, scenario)) return false;
            var marker = scenario.ActiveReFlySessionMarker;
            return marker != null && marker.InPlaceContinuation;
        }

        private static string DescribeReFlySessionForQuickloadDiscard()
        {
            var scenario = Instance;
            var marker = object.ReferenceEquals(null, scenario)
                ? null
                : scenario.ActiveReFlySessionMarker;
            if (marker != null)
            {
                return "marker "
                    + $"sess={marker.SessionId ?? "<no-id>"} "
                    + $"rp={marker.RewindPointId ?? "<no-rp>"}";
            }

            if (RewindInvokeContext.Pending)
            {
                return "pending-invoke "
                    + $"sess={RewindInvokeContext.SessionId ?? "<no-id>"} "
                    + $"rp={RewindInvokeContext.RewindPointId ?? "<no-rp>"}";
            }

            return "none";
        }

        /// <summary>
        /// Discards any pending tree that was stashed
        /// during the current scene transition, on a detected quickload
        /// (UT regressed between OnSceneChangeRequested and OnLoad). Clears
        /// <see cref="RecordingStore.PendingStashedThisTransition"/> and also
        /// clears in-memory science subjects accumulated post-F5 (they are
        /// not serialized to .sfs, so they can only ever be stale on
        /// quickload). Limbo pending trees are explicitly preserved — they
        /// are the quickload-resume carrier for tree-mode recording and are
        /// handled by the restore-and-resume dispatch further down in OnLoad.
        /// Extracted as <c>internal static</c> so log-assertion tests can
        /// exercise it without a full Unity lifecycle.
        ///
        /// <para>
        /// <b>Caller contract (#434 follow-up, 2026-04-17):</b> this method is
        /// destructive — it deletes sidecar files via
        /// <see cref="RecordingStore.DiscardPendingTree"/> — and MUST NOT be
        /// called from the revert path. Revert-to-Launch in particular matches
        /// the "UT went backwards and scene stayed in FLIGHT" quickload
        /// heuristic, but reverted recordings must survive on disk so a flight
        /// F5 quicksave's F9 can still restore. The production caller in
        /// <see cref="OnLoad"/> gates the call through
        /// <see cref="ShouldRunQuickloadDiscard"/>; the defense-in-depth checks
        /// on the first lines below refuse to proceed if a revert just fired
        /// or a re-fly session is active.
        /// </para>
        /// <para>
        /// Retry during the Re-Fly dialog is the same class of exclusion as
        /// revert: it clears the old marker, arms <see cref="RewindInvokeContext"/>
        /// for the fresh invocation, and quickloads the RP back into FLIGHT.
        /// During that pre-marker window the pending tree and RP quicksave are
        /// still dependencies of the live session, so the quickload-discard
        /// heuristic must not delete them.
        /// </para>
        /// </summary>
        internal static void DiscardStashedOnQuickload(
            double preChangeUT, double currentUT)
        {
            // Defense-in-depth: even if a future refactor inlines or removes
            // the ShouldRunQuickloadDiscard gate at the OnLoad call site, the
            // armed RevertDetector state means a revert is in progress.
            // OnLoad consumes the flag immediately before the dispatch call,
            // so under the correct (!isRevert) path this check always reads
            // None; only a buggy caller that skipped the gate would see an
            // armed kind. The method refuses rather than delete sidecar files
            // tied to the reverted flight.
            if (RevertDetector.PendingKind != RevertKind.None)
            {
                ParsekLog.Warn("Scenario",
                    "DiscardStashedOnQuickload: refusing to run with armed RevertDetector " +
                    $"(pending={RevertDetector.PendingKind}). Caller skipped the #434 guard at " +
                    "the OnLoad dispatch — sidecar preservation invariant enforced here.");
                return;
            }

            if (IsReFlySessionActiveForQuickloadDiscard())
            {
                ParsekLog.Warn("Scenario",
                    "DiscardStashedOnQuickload: refusing to run with active re-fly session " +
                    $"({DescribeReFlySessionForQuickloadDiscard()}). Caller skipped the re-fly " +
                    "guard at the OnLoad dispatch — rewind point and pending-tree " +
                    "preservation invariant enforced here.");
                return;
            }

            ParsekLog.Info("Scenario",
                "Quickload detected: UT " +
                preChangeUT.ToString("F2", CultureInfo.InvariantCulture) +
                " → " +
                currentUT.ToString("F2", CultureInfo.InvariantCulture) +
                " — discarding recordings stashed this transition");

            int discardedTree = 0;

            if (RecordingStore.HasPendingTree
                && RecordingStore.PendingStashedThisTransition
                && RecordingStore.PendingTreeStateValue != PendingTreeState.Limbo
                && RecordingStore.PendingTreeStateValue != PendingTreeState.LimboVesselSwitch)
            {
                // Non-Limbo pending tree stashed this transition: discard.
                // Limbo + LimboVesselSwitch trees are the quickload-resume / switch
                // carriers and must survive — they are picked up later in OnLoad by
                // the ScheduleActiveTreeRestoreOnFlightReady path.
                string treeName = RecordingStore.PendingTree?.TreeName;
                int recCount = RecordingStore.PendingTree?.Recordings?.Count ?? 0;
                DiscardPendingTreeAndAbandonDeferredFlightResults(
                    "pending tree discarded on quickload");
                discardedTree = 1;
                ParsekLog.Info("Scenario",
                    $"Quickload: discarded pending tree '{treeName}' " +
                    $"({recCount} recording(s), state != Limbo)");
            }

            RecordingStore.PendingStashedThisTransition = false;

            // Science subjects are never serialized to .sfs, so any entries
            // in PendingScienceSubjects accumulated between the quicksave and
            // the quickload are by definition from the discarded future. Clear
            // them so they don't get mis-attached to the next commit.
            int staleScience = GameStateRecorder.PendingScienceSubjects.Count;
            if (staleScience > 0)
            {
                GameStateRecorder.PendingScienceSubjects.Clear();
                ParsekLog.Info("Scenario",
                    $"Quickload: cleared {staleScience} stale pending science subject(s)");
            }

            ParsekLog.Info("Scenario",
                $"Quickload discard complete: tree={discardedTree} " +
                $"science={staleScience}");
        }

        /// <summary>
        /// Captures a recorder state snapshot from any scene. In flight scenes the
        /// active <see cref="ParsekFlight"/> instance contributes its live recorder /
        /// activeTree / chain manager state; in non-flight scenes those fields are
        /// null and only the static <see cref="RecordingStore"/> pending slots
        /// populate the snapshot. Used by OnSave/OnLoad lifecycle <c>[RecState]</c>
        /// emit sites.
        /// </summary>
        private static RecorderStateSnapshot CaptureScenarioRecorderState()
        {
            var flight = ParsekFlight.Instance;
            if (flight != null)
                return flight.CaptureRecorderState();
            return RecorderStateSnapshot.CaptureFromParts(
                activeTree: null,
                recorder: null,
                pendingTree: RecordingStore.PendingTree,
                pendingTreeState: RecordingStore.PendingTreeStateValue,
                pendingStandalone: null,
                pendingSplitRecorder: null,
                pendingSplitInProgress: false,
                chain: null,
                currentUT: Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0.0,
                loadedScene: HighLogic.LoadedScene);
        }

        public override void OnAwake()
        {
            base.OnAwake();
            // Phase 2 (Rewind-to-Staging): publish the live scenario module so
            // EffectiveState can read RecordingSupersedes / LedgerTombstones /
            // ActiveReFlySessionMarker through <see cref="Instance"/>. OnAwake
            // fires before OnLoad, so the Instance is available throughout the
            // load path.
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
        /// so the very first tick does not see a zero-length delta.
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

            if (lastRouteTickUT < 0.0)
            {
                lastRouteTickUT = currentUT;
                return;
            }
            if (currentUT - lastRouteTickUT < RouteOrchestrator.TickIntervalSec)
                return;
            lastRouteTickUT = currentUT;

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
            var sw = Stopwatch.StartNew();
            int recordingCount = 0;
            int dirtyCount = 0;
            string savePhase = "entry";
            try
            {
                ParsekLog.RecState("OnSave:pre", CaptureScenarioRecorderState());
                savePhase = "safety-net";
                SafetyNetAutoCommitPending();

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

                // Strip ghost map ProtoVessels — they are transient and reconstructed on load
                savePhase = "ghost-map-strip";
                if (GhostMapPresence.ghostMapVesselPids.Count > 0)
                {
                    var flightState = HighLogic.CurrentGame?.flightState;
                    if (flightState != null)
                        GhostMapPresence.StripFromSave(flightState);
                }

                lastOnSaveScene = HighLogic.LoadedScene;
                ParsekLog.RecState("OnSave:post", CaptureScenarioRecorderState());
            }
            catch (Exception ex)
            {
                LogScenarioLifecycleException("OnSave", savePhase, ex);
                throw;
            }
            finally
            {
                if (sw.IsRunning)
                    sw.Stop();
                WriteSaveTiming(sw, recordingCount, dirtyCount);
            }
        }

        /// <summary>
        /// Safety net (defense-in-depth): if a pending tree still exists outside Flight
        /// and the dialog is not actively pending, auto-commit ghost-only before serialization.
        /// Under normal operation this is unreachable — Sites A/B handle all paths.
        /// </summary>
        private static void SafetyNetAutoCommitPending()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT && !mergeDialogPending)
            {
                if (RecordingStore.HasPendingTree)
                {
                    AutoCommitTreeGhostOnly(RecordingStore.PendingTree);
                    var treeToCommit = RecordingStore.PendingTree;
                    // Resources were applied live during the originating flight (the
                    // pending tree came from a prior FLIGHT scene). MarkTreeAsApplied
                    // disarms the lump-sum replay path so the next FLIGHT entry does
                    // not re-credit the already-live income — same rationale as
                    // MergeDialog.MergeCommit (Phase C).
                    CommitPendingTreeAsApplied(treeToCommit);
                    LedgerOrchestrator.NotifyLedgerTreeCommitted(treeToCommit);
                    ParsekLog.Warn("Scenario",
                        "Safety net: committed pending tree on save outside Flight");
                }
            }
        }

        /// <summary>
        /// Thin commit-seam used by every non-dialog tree-commit path in
        /// <see cref="ParsekScenario"/> (<see cref="SafetyNetAutoCommitPending"/>,
        /// the scene-exit auto-merge branch, and the outside-Flight auto-commit branch).
        ///
        /// <para>Performs the two steps that every auto-commit path must run in lockstep:</para>
        /// <list type="number">
        ///   <item><description><see cref="RecordingStore.CommitPendingTree"/> — moves the tree from pending to committed state.</description></item>
        ///   <item><description><see cref="RecordingStore.MarkTreeAsApplied(RecordingTree)"/> — advances each committed tree recording's <c>LastAppliedResourceIndex</c> so resource-budget reservation does not re-hold resources that were already live during the originating flight.</description></item>
        /// </list>
        ///
        /// <para>Callers chain <see cref="LedgerOrchestrator.NotifyLedgerTreeCommitted(RecordingTree)"/>
        /// themselves — kept outside this seam because the orchestrator call runs heavy
        /// recalculation + patching and some call sites log / screen-message around it.</para>
        ///
        /// <para>Do NOT call from post-revert or other rollback paths where KSP's funds
        /// have been rewound to pre-flight: those paths intentionally discard or unstash
        /// the pending tree instead of committing it. None of the current callers are on
        /// that path — the revert branch runs <see cref="RecordingStore.DiscardPendingTree"/>
        /// (or <see cref="RecordingStore.UnstashPendingTreeOnRevert"/>) instead (#434).</para>
        /// </summary>
        internal static void CommitPendingTreeAsApplied(RecordingTree tree)
        {
            RecordingStore.CommitPendingTree();
            RecordingStore.MarkTreeAsApplied(tree);
        }

        /// <summary>
        /// Saves committed recording trees to RECORDING_TREE ConfigNodes and writes
        /// bulk data to external files for recordings whose data changed. Also writes
        /// the currently-active (in-flight) tree as a RECORDING_TREE with isActive=True
        /// when the save is taken during flight with an active recorder, so quickload
        /// can resume recording instead of finalizing the mission (see
        /// <c>docs/dev/plans/quickload-resume-recording.md</c>).
        /// </summary>
        internal static void SaveTreeRecordings(ConfigNode node)
        {
            node.RemoveNodes("RECORDING_TREE");
            var committedTrees = RecordingStore.CommittedTrees;
            ParsekLog.Info("Scenario", $"OnSave: saving {committedTrees.Count} committed tree(s)");
            int treeRecCount = 0;
            int treeTotalPoints = 0, treeTotalOrbitSegments = 0, treeTotalPartEvents = 0;
            int treeWithTrackSections = 0, treeWithSnapshots = 0, treeTotalBranchPoints = 0;
            for (int t = 0; t < committedTrees.Count; t++)
            {
                var tree = committedTrees[t];
                treeTotalBranchPoints += tree.BranchPoints.Count;

                bool treeFilesCurrent = true;
                foreach (var rec in tree.Recordings.Values)
                {
                    if (!EnsureRecordingFilesCurrentForSave(rec, "committed tree"))
                        treeFilesCurrent = false;
                    treeRecCount++;
                    treeTotalPoints += rec.Points.Count;
                    treeTotalOrbitSegments += rec.OrbitSegments.Count;
                    treeTotalPartEvents += rec.PartEvents.Count;
                    if (rec.TrackSections != null && rec.TrackSections.Count > 0) treeWithTrackSections++;
                    if (rec.VesselSnapshot != null) treeWithSnapshots++;
                }

                if (!treeFilesCurrent)
                {
                    ParsekLog.Warn("Scenario",
                        $"OnSave: skipped tree '{tree.TreeName}' id={tree.Id ?? "<none>"} " +
                        "because at least one recording could not be written with current v0 sidecars");
                    continue;
                }

                ConfigNode treeNode = node.AddNode("RECORDING_TREE");
                tree.Save(treeNode);
            }
            if (committedTrees.Count > 0)
                ParsekLog.Verbose("Scenario",
                    $"Saved {committedTrees.Count} trees ({treeRecCount} recordings): {treeTotalPoints} points, " +
                    $"{treeTotalOrbitSegments} orbit segments, {treeTotalPartEvents} part events, " +
                    $"{treeWithTrackSections} with track sections, {treeWithSnapshots} with snapshots, " +
                    $"{treeTotalBranchPoints} branch points");

            SaveActiveTreeIfAny(node);
            SavePendingTreeIfAny(node);

            // Zero-tree write over stranded sidecars is the "scenario load lost its tree
            // state" fingerprint; it is handled protectively (not just diagnosed) by
            // PreserveRecordingStateIfLoadFault, which OnSave invokes AFTER the mission
            // section so it can re-hydrate both trees and missions. See that method.
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

        /// <summary>
        /// Testing seam for <see cref="PreserveRecordingStateIfLoadFault"/>: lets tests drive the
        /// committed-tree-state-loaded flag the guard reads. Production sets it only inside the
        /// cold-load OnLoad (Unity-gated, so it never runs in the xUnit host — where it defaults
        /// false, i.e. the fault path). Tests set true to pin the successful-load / wipe skip
        /// path, and reset it in teardown.
        /// </summary>
        internal static bool CommittedTreeStateLoadedForTesting
        {
            get => committedTreeStateLoaded;
            set => committedTreeStateLoaded = value;
        }

        /// <summary>
        /// Load-fault data-loss guard (the OnSave-side partner of
        /// <see cref="RecordingStore.CleanOrphanFiles"/>'s refuse-to-delete guard). Fires
        /// ONLY on the "scenario load lost its tree state" fingerprint: an INCOMPLETE cold
        /// load (<c>initialLoadDone == false</c>) left the COMMITTED store empty while the
        /// recordings directory still holds stranded sidecar bulk data. All three conditions
        /// matter:
        /// <list type="bullet">
        ///   <item><description><c>!committedTreeStateLoaded</c> — the committed tree/mission load
        ///   did not complete. A successful/fresh load and every INTENTIONAL empty state (the
        ///   player's "Wipe All" / delete-all runs against a fully-loaded store) leave
        ///   <c>committedTreeStateLoaded == true</c>, so the guard can never resurrect
        ///   deliberately-removed recordings. Only a throw DURING tree/mission load leaves it
        ///   false. (initialLoadDone is unusable for this: it is set true at cold-start ENTRY,
        ///   before the trees load, so a mid-load throw would leave it true.)</description></item>
        ///   <item><description>committed count == 0 — the committed history is the surface
        ///   that gets hollowed. Gating on the COMMITTED count (not the total RECORDING_TREE
        ///   node count) means an in-flight active/pending recording — which writes its own
        ///   node via <see cref="SaveActiveTreeIfAny"/>/<see cref="SavePendingTreeIfAny"/> —
        ///   does NOT mask the loss of the committed trees.</description></item>
        ///   <item><description>stranded sidecars &gt; 0 — deleting a recording removes its
        ///   sidecars (<see cref="RecordingStore.RemoveRecordingAt"/> → <c>DeleteRecordingFiles</c>),
        ///   so leftover sidecars corroborate that committed recordings existed and were lost,
        ///   not that the save is genuinely empty.</description></item>
        /// </list>
        /// Writing 0 committed trees here would HOLLOW the campaign .sfs — dropping every
        /// committed tree + mission while the recorded data survives only as orphaned sidecars
        /// (the way a save is progressively emptied across sessions). Instead, re-hydrate the
        /// committed RECORDING_TREE + MISSION nodes from the on-disk save (which KSP has NOT
        /// overwritten yet at OnSave time) so the save keeps its state until the next load can
        /// load it. Additive: it only ADDS the on-disk committed nodes, preserving any live
        /// active/pending node the caller already wrote; it never touches a populated save.
        /// Never throws. NOTE: it reads the campaign persistent.sfs; a save targeting a quicksave
        /// slot during the (rare) fault window will re-inject the campaign's committed trees into
        /// that slot too, which is harmless (a quicksave is not the campaign save).
        /// </summary>
        internal static void PreserveRecordingStateIfLoadFault(ConfigNode node)
        {
            if (node == null)
                return;
            // Cheap gates first (no disk I/O on the common path): a non-empty committed store is
            // a normal populated save, and a completed tree/mission load (fresh career, populated
            // save, or an intentional wipe against a loaded store) leaves committedTreeStateLoaded
            // == true. Only a load that THREW during tree/mission load leaves it false — the case
            // that can hollow the .sfs. NB: initialLoadDone is deliberately NOT used here; it is
            // flipped true at cold-start entry (before the trees load), so it cannot distinguish a
            // completed load from a mid-load fault.
            if (RecordingStore.CommittedTrees.Count != 0 || committedTreeStateLoaded)
                return;
            var strandedIds = RecordingStore.CollectSidecarIdsOnDisk();
            if (!ShouldPreserveCommittedTreeState(
                    committedTreeStateLoaded, RecordingStore.CommittedTrees.Count, strandedIds.Count))
                return;

            string persistentPath = PersistentSavePathOverrideForTesting
                ?? RecordingPaths.ResolveSaveScopedPath(PersistentSaveFileName);
            int rehydratedTrees = TryRehydrateCommittedTreesAndMissionsFromDiskSave(
                node, persistentPath, out int rehydratedMissions);

            if (rehydratedTrees > 0)
            {
                ParsekLog.Warn("Scenario",
                    $"OnSave load-fault guard: committed store empty after an incomplete load, but {strandedIds.Count} "
                    + "stranded sidecar recording ID(s) on disk indicate the scenario load lost its tree state. "
                    + $"Re-hydrated {rehydratedTrees} committed RECORDING_TREE + {rehydratedMissions} MISSION node(s) "
                    + "from the on-disk save so it was NOT hollowed. Investigate the originating load fault.");
            }
            else
            {
                ParsekLog.Warn("Scenario",
                    $"OnSave load-fault guard: writing 0 committed RECORDING_TREE nodes after an incomplete load; disk "
                    + $"has {strandedIds.Count} stranded sidecar recording ID(s) but the on-disk save has no committed "
                    + "tree metadata to preserve. Sidecars are preserved by CleanOrphanFiles; recover the trees from "
                    + "quicksave.sfs or a backup.");
            }
        }

        /// <summary>
        /// Pure trigger for <see cref="PreserveRecordingStateIfLoadFault"/>: fires only on the
        /// load-fault fingerprint — the committed tree/mission load did NOT complete
        /// (<paramref name="committedTreeStateLoaded"/> == false) yet the committed store is empty
        /// while stranded sidecars remain on disk. A successful/fresh load or an intentional wipe
        /// runs with <paramref name="committedTreeStateLoaded"/> == true; a genuinely-empty save
        /// has no stranded sidecars (deletion removes them).
        /// </summary>
        internal static bool ShouldPreserveCommittedTreeState(
            bool committedTreeStateLoaded, int committedTreeCount, int strandedSidecarCount)
        {
            return !committedTreeStateLoaded && committedTreeCount == 0 && strandedSidecarCount > 0;
        }

        /// <summary>
        /// Re-hydrates the COMMITTED RECORDING_TREE + MISSION nodes from an on-disk KSP save
        /// file (<paramref name="persistentSavePath"/>, a *.sfs) into <paramref name="targetNode"/>
        /// (the ParsekScenario node being written), reading the save's own ParsekScenario SCENARIO
        /// node. Returns the number of committed RECORDING_TREE nodes copied (0 when the file is
        /// missing / unparseable / has no ParsekScenario node / has no committed trees). ADDITIVE
        /// for trees: it appends the on-disk COMMITTED trees (skipping any on-disk active/pending
        /// marker) WITHOUT removing the caller's nodes, so a live in-flight active/pending node is
        /// preserved and no second active node is introduced. Missions (which belong to the
        /// restored committed trees, and are empty in a fault since MissionStore.Load runs after
        /// LoadRecordingTrees) are replaced with the on-disk set. Deep-copies each node so the
        /// discarded loaded document is not aliased. Never throws.
        /// </summary>
        internal static int TryRehydrateCommittedTreesAndMissionsFromDiskSave(
            ConfigNode targetNode, string persistentSavePath, out int rehydratedMissions)
        {
            rehydratedMissions = 0;
            if (targetNode == null || string.IsNullOrEmpty(persistentSavePath))
                return 0;
            try
            {
                if (!System.IO.File.Exists(persistentSavePath))
                    return 0;
                ConfigNode root = ConfigNode.Load(persistentSavePath);
                if (root == null)
                    return 0;
                ConfigNode gameNode = root.GetNode("GAME") ?? root;
                ConfigNode parsekNode = FindParsekScenarioNodeInGame(gameNode);
                if (parsekNode == null)
                    return 0;

                // Add only the COMMITTED on-disk trees; skip any on-disk active/pending marker so
                // a second active node is never introduced, and do NOT remove the caller's nodes so
                // a live in-flight active/pending recording written this save is preserved.
                int addedTrees = 0;
                foreach (ConfigNode diskTree in parsekNode.GetNodes("RECORDING_TREE"))
                {
                    if (IsActiveTreeNode(diskTree) || IsPendingTreeNode(diskTree))
                        continue;
                    targetNode.AddNode(diskTree.CreateCopy());
                    addedTrees++;
                }
                if (addedTrees == 0)
                    return 0;

                // Missions belong to the committed trees just restored; replace the (empty, from
                // the faulted load) mission set with the on-disk one.
                targetNode.RemoveNodes("MISSION");
                foreach (ConfigNode diskMission in parsekNode.GetNodes("MISSION"))
                {
                    targetNode.AddNode(diskMission.CreateCopy());
                    rehydratedMissions++;
                }
                return addedTrees;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Scenario",
                    "OnSave load-fault guard: failed to re-hydrate tree/mission state from "
                    + $"'{persistentSavePath}': {ex.Message}");
                return 0;
            }
        }

        /// <summary>Finds the ParsekScenario SCENARIO node inside a loaded GAME node, or null.</summary>
        private static ConfigNode FindParsekScenarioNodeInGame(ConfigNode gameNode)
        {
            if (gameNode == null)
                return null;
            ConfigNode[] scenarios = gameNode.GetNodes("SCENARIO");
            for (int i = 0; i < scenarios.Length; i++)
            {
                if (string.Equals(scenarios[i].GetValue("name"), nameof(ParsekScenario), StringComparison.Ordinal))
                    return scenarios[i];
            }
            return null;
        }

        private static bool EnsureRecordingFilesCurrentForSave(Recording rec, string treeKind)
        {
            if (rec == null)
                return false;

            if (RecordingStore.SkipSidecarCurrencyCheckForTesting)
                return true;

            string currentReason;
            bool filesCurrent = RecordingStore.AreRecordingFilesCurrentForSave(rec, out currentReason);
            if (rec.FilesDirty || !filesCurrent)
            {
                if (!RecordingStore.SuppressLogging && !filesCurrent)
                {
                    ParsekLog.Info("Scenario",
                        $"OnSave: rewriting sidecars for recording='{rec.VesselName ?? "<unnamed>"}' " +
                        $"id={rec.RecordingId ?? "<none>"} treeKind={treeKind} reason={currentReason ?? "<none>"}");
                }

                if (!RecordingStore.SaveRecordingFiles(rec))
                {
                    ScenarioLog($"[Parsek Scenario] WARNING: File write failed for tree recording '{rec.VesselName}'");
                    return false;
                }
            }

            if (rec.FilesDirty || rec.SidecarLoadFailed)
            {
                ParsekLog.Warn("Scenario",
                    $"OnSave: recording '{rec.VesselName ?? "<unnamed>"}' id={rec.RecordingId ?? "<none>"} " +
                    $"treeKind={treeKind} remains unsafe to serialize " +
                    $"filesDirty={rec.FilesDirty} sidecarLoadFailed={rec.SidecarLoadFailed} " +
                    $"reason={rec.SidecarLoadFailureReason ?? "<none>"}");
                return false;
            }

            if (!RecordingStore.AreRecordingFilesCurrentForSave(rec, out currentReason))
            {
                ParsekLog.Warn("Scenario",
                    $"OnSave: recording '{rec.VesselName ?? "<unnamed>"}' id={rec.RecordingId ?? "<none>"} " +
                    $"treeKind={treeKind} still does not have current sidecars after save " +
                    $"reason={currentReason ?? "<none>"}");
                return false;
            }

            return true;
        }

        private static void SavePendingTreeIfAny(ConfigNode node)
        {
            if (RecordingStore.HasPendingTree)
            {
                var pendingTree = RecordingStore.PendingTree;
                var pendingState = RecordingStore.PendingTreeStateValue;
                if (pendingTree != null)
                {
                    if (pendingState == PendingTreeState.Finalized)
                    {
                        SavePendingTreeNode(
                            node,
                            pendingTree,
                            preservedDuringActiveRestore: false);
                    }
                    else if (pendingState == PendingTreeState.Limbo
                        || pendingState == PendingTreeState.LimboVesselSwitch)
                    {
                        // Data-loss fix (Limbo-tree drop): a Limbo / LimboVesselSwitch tree is
                        // the quickload-resume stash — TryRestoreActiveTreeNode removed it from
                        // committedTrees and parked it here, deferring the re-commit to a later
                        // OnFlightReady. Previously ONLY Finalized pending trees were serialized,
                        // so any OnSave that ran in the resume window (autosave / scene-exit /
                        // exiting KSP before OnFlightReady fired) wrote a persistent.sfs with the
                        // tree missing. Its mission then dangled and the tree + immutable sidecars
                        // were purged as orphans on the next load. Serialize it with the SAME
                        // isActive marker SaveActiveTreeIfAny uses so it round-trips through
                        // TryRestoreActiveTreeNode straight back into the Limbo resume flow (the
                        // stash state is re-derived from ActiveRecordingId: non-null => Limbo,
                        // null => LimboVesselSwitch). If an active node was already written this
                        // save (unexpected — the live active tree and the Limbo stash are normally
                        // mutually exclusive), fall back to a plain committed node so the tree is
                        // still durable and loads as a committed mission rather than a lost one.
                        bool activeNodeAlreadyWritten = HasActiveTreeNode(node);
                        string markerKey = activeNodeAlreadyWritten ? null : "isActive";
                        string resumeRewindSave = activeNodeAlreadyWritten
                            ? null
                            : ResolveLimboResumeRewindSave(pendingTree);
                        ParsekLog.Info("Scenario",
                            $"SavePendingTreeIfAny: serializing Limbo pending tree '{pendingTree.TreeName}' " +
                            $"state={pendingState} as {(activeNodeAlreadyWritten ? "committed-node (active node already present)" : "isActive resume node")} " +
                            "to keep the quickload-resume window crash/exit-safe");
                        SavePendingTreeNode(
                            node,
                            pendingTree,
                            preservedDuringActiveRestore: false,
                            markerKey: markerKey,
                            resumeRewindSave: resumeRewindSave);
                    }
                    else
                    {
                        ParsekLog.Verbose("Scenario",
                            $"SavePendingTreeIfAny: skipped pending tree '{pendingTree.TreeName}' " +
                            $"state={pendingState} (only Finalized / Limbo pending trees are serialized)");
                    }
                }
            }

            var savedPending = RecordingStore.SavedPendingTreeDuringActiveRestore;
            if (savedPending != null
                && !ReferenceEquals(savedPending, RecordingStore.PendingTree))
            {
                SavePendingTreeNode(
                    node,
                    savedPending,
                    preservedDuringActiveRestore: true);
            }
        }

        private static void SavePendingTreeNode(
            ConfigNode node,
            RecordingTree pendingTree,
            bool preservedDuringActiveRestore,
            string markerKey = "isPending",
            string resumeRewindSave = null)
        {
            if (pendingTree == null)
                return;

            int pendingRecCount = 0;
            int pendingDirtyCount = 0;
            int pendingSavedCount = 0;
            int pendingSaveFailedCount = 0;
            int pendingCommittedOverlapCount = 0;
            int pendingSkippedCommittedDirtyCount = 0;
            bool pendingFilesCurrent = true;
            foreach (var rec in pendingTree.Recordings.Values)
            {
                if (rec == null)
                    continue;

                bool committedOverlap = RecordingStore.IsCommittedRecordingId(rec.RecordingId);
                if (committedOverlap)
                    pendingCommittedOverlapCount++;

                bool wasDirty = rec.FilesDirty;
                // Non-dirty overlaps are still listed in the pending tree node: they
                // reference already-durable committed sidecars. Only dirty overlap
                // writes are blocked because they would mutate committed history
                // before the player consents to Merge.
                if (wasDirty)
                {
                    pendingDirtyCount++;
                    if (committedOverlap)
                    {
                        pendingSkippedCommittedDirtyCount++;
                        ParsekLog.Warn("Scenario",
                            $"SavePendingTreeIfAny: skipped dirty sidecar save for committed-overlap " +
                            $"recording '{rec.RecordingId ?? "<no-id>"}' in pending tree '{pendingTree.TreeName}' " +
                            "to avoid mutating committed history before merge consent");
                        pendingFilesCurrent = false;
                        pendingRecCount++;
                        continue;
                    }
                }

                if (!EnsureRecordingFilesCurrentForSave(rec, "pending tree"))
                {
                    pendingFilesCurrent = false;
                    pendingSaveFailedCount++;
                }
                else if (wasDirty)
                {
                    pendingSavedCount++;
                }
                pendingRecCount++;
            }

            if (!pendingFilesCurrent)
            {
                ParsekLog.Warn("Scenario",
                    $"SavePendingTreeIfAny: skipped pending tree '{pendingTree.TreeName}' " +
                    "because at least one recording could not be written with current v0 sidecars");
                return;
            }

            ConfigNode treeNode = node.AddNode("RECORDING_TREE");
            pendingTree.Save(treeNode);
            // markerKey selects how the node round-trips on load: "isPending" (Finalized
            // pending tree -> TryRestorePendingTreeNode), "isActive" (Limbo resume tree ->
            // TryRestoreActiveTreeNode -> re-stash Limbo), or null/empty (plain committed
            // node -> LoadRecordingTrees). resumeRewindSave mirrors the live recorder rewind
            // hint onto the isActive node so the resume coroutine keeps its rewind affordance.
            if (!string.IsNullOrEmpty(markerKey))
                treeNode.AddValue(markerKey, "True");
            if (!string.IsNullOrEmpty(resumeRewindSave))
                treeNode.AddValue("resumeRewindSave", resumeRewindSave);
            if (preservedDuringActiveRestore)
                RecordingStore.MarkSavedPendingTreeDuringActiveRestoreSerializedForSave("SavePendingTreeIfAny");
            else
                RecordingStore.MarkPendingTreeSerializedForSave("SavePendingTreeIfAny");

            ParsekLog.Info("Scenario",
                $"OnSave: wrote {(string.IsNullOrEmpty(markerKey) ? "COMMITTED" : (markerKey == "isActive" ? "ACTIVE-RESUME" : "PENDING"))} tree '{pendingTree.TreeName}' " +
                (preservedDuringActiveRestore ? "preserved during active-tree restore " : "") +
                $"({pendingRecCount} recording(s), dirty={pendingDirtyCount}, saved={pendingSavedCount}, " +
                $"failed={pendingSaveFailedCount}, committedOverlap={pendingCommittedOverlapCount}, " +
                $"skippedCommittedDirty={pendingSkippedCommittedDirtyCount})");
        }

        /// <summary>
        /// True if any RECORDING_TREE node already written to <paramref name="node"/> carries
        /// the <c>isActive</c> marker. Used by <see cref="SavePendingTreeIfAny"/> to avoid
        /// writing a second active node when serializing a Limbo resume tree (only one active
        /// node round-trips through <see cref="TryRestoreActiveTreeNode"/>).
        /// </summary>
        internal static bool HasActiveTreeNode(ConfigNode node)
        {
            if (node == null)
                return false;
            var treeNodes = node.GetNodes("RECORDING_TREE");
            for (int t = 0; t < treeNodes.Length; t++)
                if (IsActiveTreeNode(treeNodes[t]))
                    return true;
            return false;
        }

        /// <summary>
        /// Resolves the rewind-save filename to mirror onto a serialized Limbo resume tree's
        /// isActive node (read back by <see cref="TryRestoreActiveTreeNode"/> as the resume
        /// hint). The live recorder is gone during the resume window, so the value comes from
        /// the tree root recording's <see cref="Recording.RewindSaveFileName"/> (copied there
        /// at stash time). Returns null when unavailable — a missing hint is benign.
        /// </summary>
        private static string ResolveLimboResumeRewindSave(RecordingTree tree)
        {
            if (tree == null || string.IsNullOrEmpty(tree.RootRecordingId) || tree.Recordings == null)
                return null;
            if (tree.Recordings.TryGetValue(tree.RootRecordingId, out var root) && root != null)
                return root.RewindSaveFileName;
            return null;
        }

        /// <summary>
        /// Tree ids that exist but are not yet committed at mission-reconcile time, so
        /// <see cref="MissionStore.PruneOrphans"/> must not treat their missions as orphans:
        /// the ids of any isActive / isPending RECORDING_TREE node in <paramref name="node"/>
        /// (restored into the pending slot LATER this OnLoad by <see cref="TryRestoreActiveTreeNode"/>
        /// / <see cref="TryRestorePendingTreeNode"/>), plus any already-stashed pending /
        /// saved-pending tree. Limbo-tree data-loss fix (second half).
        /// <para>
        /// If that later restore fails the schema gate (all recordings rejected), the protected
        /// mission dangles for one cycle, then the next save writes no node and the next load
        /// prunes it normally — self-healing, and still strictly better than deleting data.
        /// </para>
        /// </summary>
        internal static List<string> CollectParkedTreeIdsForMissionPrune(ConfigNode node)
        {
            var ids = new List<string>();
            if (node != null)
            {
                var treeNodes = node.GetNodes("RECORDING_TREE");
                for (int t = 0; t < treeNodes.Length; t++)
                {
                    if (!IsActiveTreeNode(treeNodes[t]) && !IsPendingTreeNode(treeNodes[t]))
                        continue;
                    string id = treeNodes[t].GetValue("id");
                    if (!string.IsNullOrEmpty(id))
                        ids.Add(id);
                }
            }
            if (RecordingStore.HasPendingTree && !string.IsNullOrEmpty(RecordingStore.PendingTree?.Id))
                ids.Add(RecordingStore.PendingTree.Id);
            var savedPending = RecordingStore.SavedPendingTreeDuringActiveRestore;
            if (savedPending != null && !string.IsNullOrEmpty(savedPending.Id))
                ids.Add(savedPending.Id);
            return ids;
        }

        /// <summary>
        /// Writes the currently-active (in-flight) recording tree as an extra RECORDING_TREE
        /// ConfigNode marked with <c>isActive=True</c>, plus any recorder state needed to
        /// resume on quickload (chain id, boundary anchor UT, rewind save filename).
        /// <para>
        /// Guarded: only writes when <c>LoadedScene == FLIGHT</c>, <c>ParsekFlight.Instance</c>
        /// has a live active tree, and a recorder exists. Any other scene (KSC, TS, Main Menu)
        /// has no in-flight recording to preserve and would emit a stale stub.
        /// </para>
        /// </summary>
        private static void SaveActiveTreeIfAny(ConfigNode node)
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT) return;

            var flight = ParsekFlight.Instance;
            if (flight == null) return;

            var activeTree = flight.ActiveTreeForSerialization;
            if (activeTree == null) return;

            // Bug #266: the recorder may legitimately be null while activeTree is alive
            // — this is the "outsider" state, where the player switched (in-session or
            // via scene reload) to a vessel that has no recording context in the tree.
            // The tree is still tracking background vessels, so we MUST serialize it.
            // Without this branch, an F5 in outsider state would lose the tree on F9.
            var recorder = flight.ActiveRecorderForSerialization;

            // Flush recorder's buffered data into the active tree's current recording
            // before serializing. Otherwise the on-disk tree misses points/events that
            // were captured since the last chain boundary. Skip the flush in outsider
            // state — there is no recorder buffer to flush.
            if (recorder != null)
            {
                try
                {
                    flight.FlushRecorderIntoActiveTreeForSerialization();
                }
                catch (System.Exception ex)
                {
                    ParsekLog.Warn("Scenario",
                        $"SaveActiveTreeIfAny: flush failed ({ex.Message}) — active tree will be written with stale recorder data");
                }
            }
            else
            {
                ParsekLog.Verbose("Scenario",
                    $"SaveActiveTreeIfAny: no live recorder for active tree '{activeTree.TreeName}' " +
                    $"(activeRecId={activeTree.ActiveRecordingId ?? "<null>"}) — outsider state (#266), " +
                    $"serializing tree without flush");
            }

            // Persist bulk data for active-tree recordings (same pattern as committed trees).
            int restoredFromCommittedTree = RestoreHydrationFailedRecordingsFromCommittedTree(
                activeTree,
                activeTree.ActiveRecordingId);
            if (restoredFromCommittedTree > 0)
            {
                ParsekLog.Warn("Scenario",
                    $"SaveActiveTreeIfAny: repaired {restoredFromCommittedTree} hydration-failed active-tree " +
                    "recording(s) from the committed tree before saving sidecars");
            }

            //
            // Observability (2026-04-09 debris-flow investigation, bug #280): track how many
            // of the iterated recordings were dirty vs saved, so future playtest logs can
            // diagnose any FilesDirty-propagation gap. The outer `wrote ACTIVE tree ... (N
            // recording(s))` log only reports the total iteration count, which hid the fact
            // that all debris recordings were silently failing to save.
            int activeRecCount = 0;
            int activeDirtyCount = 0;
            int activeSavedCount = 0;
            int activeSaveFailedCount = 0;
            int activeSkippedDegradedCount = 0;
            int activeSkippedCommittedRestoreOverlapCount = 0;
            bool activeFilesCurrent = true;
            foreach (var rec in activeTree.Recordings.Values)
            {
                bool wasDirty = rec.FilesDirty;
                if (wasDirty)
                {
                    activeDirtyCount++;
                    if (RecordingStore.IsCommittedTreeRestoreAttemptRecordingId(rec.RecordingId)
                        && !RecordingStore.IsMarkerOwnedSwitchSegmentRecordingId(rec.RecordingId))
                    {
                        activeSkippedCommittedRestoreOverlapCount++;
                        ParsekLog.Warn("Scenario",
                            $"SaveActiveTreeIfAny: skipped dirty sidecar save for committed-restore overlap " +
                            $"recording '{rec.RecordingId ?? "<no-id>"}' in active tree '{activeTree.TreeName}' " +
                            "to avoid mutating committed history before merge consent");
                        activeFilesCurrent = false;
                        activeRecCount++;
                        continue;
                    }

                    // Switch-segment narrowing (segment-scoped-switch-fly-autorecord
                    // Phase D): marker-owned new segment recordings must be durable
                    // enough to survive F5/save/reload. They share id space with the
                    // committed-restore attempt only when both run concurrently
                    // (e.g. a switch/Fly under a #866 clone-restore), but the new
                    // segment id is a fresh-owned recording, not an original
                    // committed parent. Allow the dirty sidecar save through.
                    if (RecordingStore.IsMarkerOwnedSwitchSegmentRecordingId(rec.RecordingId))
                    {
                        var bypassSession = ParsekScenario.Instance?.ActiveSwitchSegmentSession;
                        ParsekLog.Verbose("Scenario",
                            $"SaveActiveTreeIfAny: dirty sidecar save bypassed for " +
                            $"reason=marker-owned-switch-segment recId={rec.RecordingId ?? "<no-id>"} " +
                            $"sessionId={(bypassSession != null ? bypassSession.SessionId.ToString("D", CultureInfo.InvariantCulture) : "<null>")}");
                    }

                    if (ShouldSkipActiveTreeEmptySidecarOverwrite(rec))
                    {
                        activeSkippedDegradedCount++;
                        ParsekLog.Warn("Scenario",
                            $"SaveActiveTreeIfAny: skipped empty sidecar overwrite for hydration-failed " +
                            $"recording '{rec.RecordingId ?? "<no-id>"}' vessel='{rec.VesselName ?? "<no-name>"}' " +
                            $"reason={rec.SidecarLoadFailureReason ?? "<unknown>"}");
                        activeFilesCurrent = false;
                        continue;
                    }
                }

                if (!EnsureRecordingFilesCurrentForSave(rec, "active tree"))
                {
                    activeFilesCurrent = false;
                    activeSaveFailedCount++;
                }
                else if (wasDirty)
                {
                    activeSavedCount++;
                }
                activeRecCount++;
            }

            ParsekLog.Info("Scenario",
                $"SaveActiveTreeIfAny: iterated {activeRecCount} recording(s), " +
                $"{activeDirtyCount} dirty, {activeSavedCount} saved, {activeSaveFailedCount} failed, " +
                $"{activeSkippedDegradedCount} skippedDegraded, " +
                $"{activeSkippedCommittedRestoreOverlapCount} skippedCommittedRestoreOverlap");

            if (!activeFilesCurrent)
            {
                ParsekLog.Warn("Scenario",
                    $"SaveActiveTreeIfAny: skipped active tree '{activeTree.TreeName}' " +
                    "because at least one recording could not be written with current v0 sidecars");
                return;
            }

            // Quickload resume restores the recorder's rewind save hint from
            // resumeRewindSave, but the committed tree's Rewind button resolves through the
            // root recording. Mirror the live recorder rewind metadata onto the root before
            // serializing so an F5/F9 tree keeps its rewind affordance after merge.
            ParsekFlight.CopyRewindSaveToRoot(
                activeTree,
                recorder,
                logTag: "SaveActiveTreeIfAny");

            ConfigNode treeNode = node.AddNode("RECORDING_TREE");
            activeTree.Save(treeNode);
            treeNode.AddValue("isActive", "True");

            // Persist recorder state needed to resume after quickload.
            // NOTE: BoundaryAnchor is a TrajectoryPoint struct with lat/lon/alt/rotation/
            // velocity, and serializing just the UT is insufficient to reconstruct it on
            // restore — RestoreActiveTreeFromPending explicitly leaves BoundaryAnchor
            // unset. Either serialize the full TrajectoryPoint or don't write anything;
            // we chose the latter because a missing anchor just produces one extra
            // boundary point on the next chain continuation, which is benign.
            //
            // In outsider state (#266), recorder is null — there's no live rewind save
            // filename to persist. The tree's root recording still has rewindSave from
            // the original launch (copied into rootRec.RewindSaveFileName at stash time).
            if (recorder != null && !string.IsNullOrEmpty(recorder.RewindSaveFileName))
                treeNode.AddValue("resumeRewindSave", recorder.RewindSaveFileName);

            ParsekLog.Info("Scenario",
                $"OnSave: wrote ACTIVE tree '{activeTree.TreeName}' ({activeRecCount} recording(s), " +
                $"activeRecId={activeTree.ActiveRecordingId ?? "<null>"}) for quickload resume");
        }

        /// <summary>
        /// Persists crew state, group hierarchy, game state events, baselines,
        /// milestones, and ledger data to the save node and external files.
        /// </summary>
        private static void PersistGameStateAndMilestones(ConfigNode node)
        {
            // Persist kerbal slots (new format) and crew replacements (backward compat)
            LedgerOrchestrator.Kerbals?.SaveSlots(node);
            CrewReservationManager.SaveCrewReplacements(node);

            // Persist group hierarchy and hidden groups
            GroupHierarchyStore.SaveInto(node);

            // Save game state events to external file
            GameStateStore.SaveEventFile();
            node.AddValue("gameStateEventCount", GameStateStore.EventCount);

            // Save any pending baselines
            foreach (var baseline in GameStateStore.Baselines)
                GameStateStore.SaveBaseline(baseline);

            // Flush any uncaptured game state events into a milestone before saving.
            // Handles events that happened without a recording commit (e.g. tech
            // research in R&D without launching a flight). A committed-tree restore
            // attempt is special: same recording IDs are being reused by an unmerged
            // active clone. The event file save above filters those same-id attempt
            // tails from disk, but flushing a milestone here would make them durable
            // through milestones and advance the watermark before the player chooses
            // Merge. Defer the flush; the normal commit path will create the milestone
            // if the player accepts the segment.
            if (ShouldDeferPendingEventMilestoneFlushForSave())
            {
                ParsekLog.Warn("Scenario",
                    "OnSave: deferred pending event milestone flush while a committed-tree " +
                    "restore attempt is active; unmerged same-id attempt events remain memory-only");
            }
            else
            {
                ParsekLog.Verbose("Scenario", $"OnSave: flushing pending events at UT {Planetarium.GetUniversalTime():F0}");
                MilestoneStore.FlushPendingEvents(Planetarium.GetUniversalTime());
            }

            // Save milestones to external file + mutable state to .sfs
            MilestoneStore.SaveMilestoneFile();
            MilestoneStore.SaveMutableState(node);

            // Save ledger to external file
            LedgerOrchestrator.OnSave();
            ParsekLog.Verbose("Scenario",
                $"OnSave: wrote external game-state files (events={GameStateStore.EventCount}, milestones={MilestoneStore.MilestoneCount})");
        }

        /// <summary>
        /// Returns true when an OnSave-time milestone flush should be deferred to
        /// preserve the #866 same-id committed-restore-tail invariant. Defers only
        /// when at least one pending event would persist into a milestone for a
        /// committed-tree-restore-overlap recording that is NOT marker-owned by
        /// the active <see cref="SwitchSegmentSession"/>. When all pending events
        /// for overlap ids are marker-owned new segment recordings (or there are
        /// no pending overlap-id events at all), the flush is allowed so the new
        /// segment's events survive F5/save/reload.
        ///
        /// <para>Segment-scoped-switch-fly-autorecord Phase D narrowing of the
        /// original predicate, which deferred unconditionally on any active
        /// committed-tree restore attempt and would have lost marker-owned new-id
        /// events across save/reload of an active switch/Fly segment.</para>
        /// </summary>
        internal static bool ShouldDeferPendingEventMilestoneFlushForSave()
        {
            if (!RecordingStore.HasCommittedTreeRestoreAttempt)
                return false;

            // Phase D narrowing: enumerate pending events. Defer only if at
            // least one event belongs to a committed-tree-restore-overlap
            // recording that is NOT marker-owned. Untagged events and events
            // for unrelated ids are ignored — they were never gated by the
            // suppression contract in the first place.
            var events = GameStateStore.Events;
            if (events == null || events.Count == 0)
                return false;

            // LOW 15 (PR #876 review): the loop short-circuits on the first
            // non-marker-owned committed-overlap event via the `break` below,
            // so save-time cost is O(events-until-first-hit), not O(events).
            // Untagged + marker-owned + unrelated entries fall through to the
            // next iteration; the diagnostic counters under the loop summarize
            // the skip distribution so a saturated tail still leaves a trail
            // when the predicate genuinely needs to walk every event before
            // returning false.
            bool sawNonMarkerOwnedOverlap = false;
            int markerOwnedSkipped = 0;
            int unrelatedSkipped = 0;
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (string.IsNullOrEmpty(evt.recordingId))
                {
                    unrelatedSkipped++;
                    continue;
                }

                if (RecordingStore.IsMarkerOwnedSwitchSegmentRecordingId(evt.recordingId))
                {
                    markerOwnedSkipped++;
                    continue;
                }

                if (RecordingStore.IsCommittedTreeRestoreAttemptRecordingId(evt.recordingId))
                {
                    sawNonMarkerOwnedOverlap = true;
                    break;
                }

                unrelatedSkipped++;
            }

            if (!sawNonMarkerOwnedOverlap)
            {
                var session = ParsekScenario.Instance?.ActiveSwitchSegmentSession;
                ParsekLog.Verbose("Scenario",
                    $"ShouldDeferPendingEventMilestoneFlushForSave: not-deferred " +
                    $"reason=marker-owned-switch-segment markerOwned={markerOwnedSkipped} " +
                    $"unrelated={unrelatedSkipped} " +
                    $"sessionId={(session != null ? session.SessionId.ToString("D", CultureInfo.InvariantCulture) : "<null>")}");
            }

            return sawNonMarkerOwnedOverlap;
        }

        /// <summary>
        /// Persists the Rewind-to-Staging Phase 1 collections and singletons
        /// (design sections 5.1 - 5.9). Idempotent: always re-creates the four
        /// parent nodes from scratch so stale entries from a prior save do not
        /// leak into the new one.
        /// </summary>
        private void SaveRewindStagingState(ConfigNode node)
        {
            node.RemoveNodes("REWIND_POINTS");
            node.RemoveNodes("RECORDING_SUPERSEDES");
            node.RemoveNodes("RECORDING_REWIND_RETIREMENTS");
            node.RemoveNodes("LEDGER_TOMBSTONES");
            node.RemoveNodes(ReFlySessionMarker.NodeName);
            node.RemoveNodes(MergeJournal.NodeName);
            node.RemoveNodes(StockActionIntentMarker.NodeName);
            node.RemoveNodes(SwitchSegmentSession.NodeName);
            node.RemoveNodes(TestBatchMarker.NodeName);

            int rpCount = SaveStagingList(node, "REWIND_POINTS", RewindPoints, (item, parent) => item.SaveInto(parent));

            int supersedeCount = SaveStagingList(node, "RECORDING_SUPERSEDES", RecordingSupersedes, (item, parent) => item.SaveInto(parent));

            int retirementCount = SaveStagingList(node, "RECORDING_REWIND_RETIREMENTS", RecordingRewindRetirements, (item, parent) => item.SaveInto(parent));

            int tombCount = SaveStagingList(node, "LEDGER_TOMBSTONES", LedgerTombstones, (item, parent) => item.SaveInto(parent));

            bool markerWritten = false;
            string markerSessionId = null;
            if (ActiveReFlySessionMarker != null)
            {
                ActiveReFlySessionMarker.SaveInto(node);
                markerWritten = true;
                markerSessionId = ActiveReFlySessionMarker.SessionId;
            }

            bool journalWritten = false;
            string journalId = null;
            if (ActiveMergeJournal != null)
            {
                ActiveMergeJournal.SaveInto(node);
                journalWritten = true;
                journalId = ActiveMergeJournal.JournalId;
            }

            bool intentWritten = false;
            string intentId = null;
            if (activeStockActionIntent != null)
            {
                activeStockActionIntent.SaveInto(node);
                intentWritten = true;
                intentId = activeStockActionIntent.IntentId.ToString("D");
            }

            bool segmentWritten = false;
            string segmentSessionId = null;
            if (activeSwitchSegmentSession != null)
            {
                activeSwitchSegmentSession.SaveInto(node);
                segmentWritten = true;
                segmentSessionId = activeSwitchSegmentSession.SessionId.ToString("D");
            }

            bool testBatchWritten = false;
            if (ActiveTestBatchMarker != null)
            {
                ActiveTestBatchMarker.SaveInto(node);
                testBatchWritten = true;
            }
            ParsekLog.Info("TestBatch",
                $"marker saved: {(testBatchWritten ? (ActiveTestBatchMarker.ProcessSessionId ?? "<no-pid>") : "none")}");

            // Per-section tagged lines (design §10 tag conventions). Emitted
            // alongside the consolidated summary below so log-grep by tag still
            // works even when the summary line changes shape.
            ParsekLog.Info("Rewind", $"RewindPoints saved: {rpCount}");
            ParsekLog.Info("Supersede", $"RecordingSupersedes saved: {supersedeCount}");
            ParsekLog.Info("Rewind", $"RecordingRewindRetirements saved: {retirementCount}");
            ParsekLog.Info("LedgerSwap", $"LedgerTombstones saved: {tombCount}");
            ParsekLog.Info("ReFlySession",
                $"Marker saved: {(markerWritten ? (markerSessionId ?? "<no-id>") : "none")}");
            ParsekLog.Info("MergeJournal",
                $"Journal saved: {(journalWritten ? (journalId ?? "<no-id>") : "none")}");
            ParsekLog.Info("SwitchIntent",
                $"intent saved: {(intentWritten ? (intentId ?? "<no-id>") : "none")}");
            ParsekLog.Info("SwitchSegment",
                $"session saved: {(segmentWritten ? (segmentSessionId ?? "<no-id>") : "none")}");

            ParsekLog.Info("Scenario",
                $"OnSave: Re-Fly subsystem state persisted (saved collections, not a rewind action; " +
                $"reFlyInProgress={markerWritten}): rewindPoints={rpCount} supersedes={supersedeCount} " +
                $"rewindRetirements={retirementCount} " +
                $"tombstones={tombCount} marker={markerWritten} journal={journalWritten} " +
                $"switchIntent={intentWritten} switchSegment={segmentWritten}");
        }

        private static int SaveStagingList<T>(ConfigNode node, string nodeName, IReadOnlyList<T> list, Action<T, ConfigNode> save)
        {
            int count = 0;
            if (list != null && list.Count > 0)
            {
                var parent = node.AddNode(nodeName);
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] == null) continue;
                    save(list[i], parent);
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Restores the Rewind-to-Staging Phase 1 collections and singletons.
        /// Missing parent nodes yield empty lists and null singletons so
        /// pre-feature saves round-trip cleanly (design section 9).
        /// </summary>
        private void LoadRewindStagingState(ConfigNode node)
        {
            RewindPoints = new List<RewindPoint>();
            RecordingSupersedes = new List<RecordingSupersedeRelation>();
            RecordingRewindRetirements = new List<RecordingRewindRetirement>();
            LedgerTombstones = new List<LedgerTombstone>();
            ActiveReFlySessionMarker = null;
            ActiveMergeJournal = null;
            ActiveTestBatchMarker = null;
            // Phase 2 (ghost rendering pipeline): drop any prior session's
            // anchor ε map. If the just-loaded save carries a marker the
            // post-load Rebuild below repopulates; if it does not, this
            // Clear keeps stale anchors from a previous game leaking into
            // the new load (HR-9 visibility).
            Parsek.Rendering.RenderSessionState.Clear("marker-cleared");
            // Switch-segment auto-record statics are loaded fresh below from
            // their dedicated nodes; null here so a pre-feature save (no
            // serialized markers) leaves both fields cleared.
            activeStockActionIntent = null;
            activeSwitchSegmentSession = null;

            LoadStagingList(node, "REWIND_POINTS", "POINT", RewindPoints, RewindPoint.LoadFrom);
            RecordingsTableUI.ClearAllRewindSlotCanInvokeLogState();

            LoadStagingList(node, "RECORDING_SUPERSEDES", "ENTRY", RecordingSupersedes, RecordingSupersedeRelation.LoadFrom);

            LoadStagingList(node, "RECORDING_REWIND_RETIREMENTS", "ENTRY", RecordingRewindRetirements, RecordingRewindRetirement.LoadFrom);

            LoadStagingList(node, "LEDGER_TOMBSTONES", "ENTRY", LedgerTombstones, LedgerTombstone.LoadFrom);

            ConfigNode markerNode = node.GetNode(ReFlySessionMarker.NodeName);
            if (markerNode != null)
                ActiveReFlySessionMarker = ReFlySessionMarker.LoadFrom(markerNode);

            // Phase 2 (ghost rendering pipeline, design doc §17.2 / §18 Phase
            // 2): rebuild the in-memory anchor ε map from the just-loaded
            // marker so the OnLoad path matches the post-AtomicMarkerWrite
            // path in RewindInvoker.ConsumePostLoad. HR-9 visible-failure —
            // a throw inside the rebuild must not abort save load.
            if (ActiveReFlySessionMarker != null)
            {
                try
                {
                    Parsek.Rendering.RenderSessionState.RebuildFromMarker(ActiveReFlySessionMarker);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("Pipeline-Session",
                        $"OnLoad RebuildFromMarker threw (non-fatal): {ex.Message}");
                }
            }

            // Re-fly Esc-menu button gate: covers the case where a save was
            // made mid-re-fly and is being loaded fresh. If the load lands in
            // a non-flight scene the call is a logged no-op; the next
            // onFlightReady (handled by ReFlyRevertButtonGate.Subscribe) will
            // re-assert when the player enters flight. If the load lands in
            // flight (rare — KSP normally loads to KSC first), this is the
            // immediate force.
            ReFlyRevertButtonGate.Apply("OnLoad:after-marker-load");

            ConfigNode journalNode = node.GetNode(MergeJournal.NodeName);
            if (journalNode != null)
                ActiveMergeJournal = MergeJournal.LoadFrom(journalNode);

            // Switch-segment auto-record markers (Phase A.3 wiring; consume
            // sites land in Phase C). The intent marker carries TTL + UT +
            // ProcessSessionId so we validate freshness here at load time and
            // clear stale-on-OnLoad rather than at consume site — that way
            // the player isn't surprised by a delayed auto-start after they
            // click around in TS.
            ConfigNode intentNode = node.GetNode(StockActionIntentMarker.NodeName);
            StockActionIntentMarker loadedIntent;
            if (intentNode != null && StockActionIntentMarker.TryLoadFrom(intentNode, out loadedIntent))
            {
                activeStockActionIntent = loadedIntent;
                ValidateLoadedStockActionIntentFreshness();
            }

            ConfigNode segmentNode = node.GetNode(SwitchSegmentSession.NodeName);
            SwitchSegmentSession loadedSession;
            if (segmentNode != null && SwitchSegmentSession.TryLoadFrom(segmentNode, out loadedSession))
            {
                activeSwitchSegmentSession = loadedSession;
                ParsekLog.Info("SwitchSegment",
                    $"session loaded: sessionId={loadedSession.SessionId:D} " +
                    $"intentId={loadedSession.IntentId:D} entryReason={loadedSession.EntryReason}");
            }

            ConfigNode testBatchNode = node.GetNode(TestBatchMarker.NodeName);
            if (testBatchNode != null)
                ActiveTestBatchMarker = TestBatchMarker.LoadFrom(testBatchNode);
            ParsekLog.Info("TestBatch",
                $"marker loaded: {(ActiveTestBatchMarker != null ? (ActiveTestBatchMarker.ProcessSessionId ?? "<no-pid>") : "none")}");

            // Per-section tagged lines (design §10 tag conventions). Emitted
            // alongside the consolidated summary below so log-grep by tag still
            // works even when the summary line changes shape.
            ParsekLog.Info("Rewind", $"RewindPoints loaded: {RewindPoints.Count}");
            ParsekLog.Info("Supersede",
                $"RecordingSupersedes loaded: {RecordingSupersedes.Count}");
            ParsekLog.Info("Rewind",
                $"RecordingRewindRetirements loaded: {RecordingRewindRetirements.Count}");
            ParsekLog.Info("LedgerSwap",
                $"LedgerTombstones loaded: {LedgerTombstones.Count}");
            ParsekLog.Info("ReFlySession",
                $"Marker loaded: {(ActiveReFlySessionMarker != null ? (ActiveReFlySessionMarker.SessionId ?? "<no-id>") : "none")}");
            ParsekLog.Info("MergeJournal",
                $"Journal loaded: {(ActiveMergeJournal != null ? (ActiveMergeJournal.JournalId ?? "<no-id>") : "none")}");
            ParsekLog.Info("SwitchIntent",
                $"intent loaded: {(activeStockActionIntent != null ? activeStockActionIntent.IntentId.ToString("D") : "none")}");
            ParsekLog.Info("SwitchSegment",
                $"session loaded summary: {(activeSwitchSegmentSession != null ? activeSwitchSegmentSession.SessionId.ToString("D") : "none")}");

            ParsekLog.Info("Scenario",
                $"OnLoad: Re-Fly subsystem state loaded (restored collections, not a rewind action; " +
                $"reFlyInProgress={(ActiveReFlySessionMarker != null)}): rewindPoints={RewindPoints.Count} " +
                $"supersedes={RecordingSupersedes.Count} rewindRetirements={RecordingRewindRetirements.Count} " +
                $"tombstones={LedgerTombstones.Count} " +
                $"marker={(ActiveReFlySessionMarker != null)} journal={(ActiveMergeJournal != null)} " +
                $"switchIntent={(activeStockActionIntent != null)} switchSegment={(activeSwitchSegmentSession != null)}");

            // Phase 2: a new load invalidates every derived cache. Bump both
            // counters so <see cref="EffectiveState.ComputeERS"/> and
            // <see cref="EffectiveState.ComputeELS"/> recompute on next access.
            BumpSupersedeStateVersion();
            BumpTombstoneStateVersion();
        }

        /// <summary>
        /// Loads a staging collection from a named parent node by appending each
        /// child node (matched by <paramref name="childName"/>) through
        /// <paramref name="load"/> into <paramref name="target"/>. Collapses the
        /// four parallel REWIND_POINTS / RECORDING_SUPERSEDES /
        /// RECORDING_REWIND_RETIREMENTS / LEDGER_TOMBSTONES load blocks in
        /// <see cref="LoadRewindStagingState"/>; the child name differs ("POINT"
        /// vs "ENTRY") so it is a parameter.
        /// </summary>
        private static void LoadStagingList<T>(ConfigNode node, string parentName, string childName, List<T> target, Func<ConfigNode, T> load)
        {
            ConfigNode parent = node.GetNode(parentName);
            if (parent != null)
            {
                var entries = parent.GetNodes(childName);
                for (int i = 0; i < entries.Length; i++)
                    target.Add(load(entries[i]));
            }
        }

        /// <summary>
        /// OnLoad crash-reconcile finisher for the in-game test runner's campaign
        /// isolation. Mirrors the re-fly marker + OnLoad-finisher idiom: a
        /// <see cref="TestBatchMarker"/> persisted into persistent.sfs before a
        /// batch started means, on the NEXT OnLoad in a DIFFERENT process, that the
        /// batch was interrupted (crash, hard quit, KSP kill) before it could revert
        /// the campaign. The finisher reverts persistent.sfs from the clean .bak AND
        /// schedules a real deferred reload so the in-memory game + ledger come from
        /// the .bak (a bare disk overwrite is insufficient: ledger-load + the
        /// deferred-seed recalc have already patched the live career from the mutated
        /// save in THIS pass, and the next autosave would re-clobber the .bak). NEVER
        /// calls SaveGame from inside OnLoad.
        /// </summary>
        private void RunTestBatchCrashReconcile()
        {
            // Nit N1: the entire reconcile is best-effort and must NEVER throw out
            // of OnLoad. A reconcile that throws would abort the whole OnLoad after
            // the ledger/recordings have already loaded; on the crash path the disk
            // is left whatever the partial revert achieved (warned below) and the
            // .bak/snapshot are preserved for manual recovery.
            try
            {
                RunTestBatchCrashReconcileCore();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("TestBatch",
                    "crash-reconcile threw and was swallowed to protect OnLoad; the campaign "
                    + "may be partially reverted. Load the save manually to recover. detail: "
                    + ex.Message);
            }
        }

        private void RunTestBatchCrashReconcileCore()
        {
            var marker = ActiveTestBatchMarker;
            string reason;
            bool shouldReconcile = TestBatchMarker.ShouldReconcileOnLoad(
                marker, ParsekProcess.ProcessSessionId.ToString("N"),
                HighLogic.SaveFolder, out reason);

            if (!shouldReconcile)
            {
                if (marker != null && reason == "same-process-no-crash")
                {
                    // In-process load between/within batches; the in-process teardown
                    // owns cleanup. Do NOT clear (a running batch still needs the
                    // disk marker).
                    ParsekLog.Verbose("TestBatch", $"crash-reconcile skipped: {reason}");
                }
                else if (marker != null)
                {
                    // Stale / wrong-save / no-backup-path marker: drop the in-memory
                    // copy (persisted out clean on the next OnSave); do NOT touch
                    // persistent.sfs.
                    ParsekLog.Warn("TestBatch", $"crash-reconcile clearing stale marker: {reason}");
                    ActiveTestBatchMarker = null;
                }
                return;
            }

            ParsekLog.Warn("TestBatch",
                $"Interrupted test batch detected ({reason}); reverting persistent.sfs from "
                + $"'{marker.PersistentBackupPath}' and the Parsek/ sidecar dir from "
                + $"'{marker.ParsekSnapshotDir ?? "(none)"}', then scheduling a clean reload");

            // 1. Revert the on-disk persistent.sfs from the clean (marker-free) .bak.
            bool diskReverted = RestoreCampaignPersistentSaveOnReconcile(marker.PersistentBackupPath);

            // 2. Revert the live Parsek/ save-scoped sidecar dir (the LEDGER
            //    events.pgse + all other sidecar state) from the batch-start
            //    snapshot, BEFORE the deferred reload and BEFORE any artifact
            //    deletion. The deferred reload's cold OnLoad re-loads the ledger
            //    from this now-clean events.pgse (see step 4). DiskOnly-mode markers
            //    carry no snapshot (ParsekSnapshotDir == null) — the helper no-ops
            //    and returns true (nothing to restore). On a non-null snapshot that
            //    fails to restore we do NOT reload (a stale-ledger reload would
            //    re-corrupt the career); the .bak + snapshot are preserved below.
            bool sidecarsReverted = RestoreParsekSidecarDirOnReconcile(marker.ParsekSnapshotDir);

            // 3. Force a clean in-memory reload for the deferred reload's OnLoad:
            //    set initialLoadDone=false and run the SAME *.ResetForTesting() set
            //    the success path (PrepareForIsolatedBatchFlightBaselineRestore)
            //    uses, so the next OnLoad takes the COLD branch and re-loads the
            //    ledger from the now-clean events.pgse instead of the scene-change
            //    branch (which skips LoadExternalFiles/LedgerOrchestrator.OnLoad and
            //    would RecalculateAndPatch from the STALE in-memory ledger). Only do
            //    this when both disk surfaces are clean — otherwise we leave the
            //    in-memory state as-is and skip the reload entirely.
            bool reloadable = diskReverted && sidecarsReverted;
            if (reloadable)
            {
                // Unsubscribe the live recorder this scenario instance subscribed
                // earlier in OnLoad (mirrors the success path's unsubscribeLiveRecorder
                // callback) so the wiped GameStateRecorder statics are not re-fed by a
                // still-live subscription before the deferred reload rebuilds it.
                stateRecorder?.Unsubscribe();
                PrepareInMemoryStateForTestBatchCrashReload();
            }

            // 4. Clear the in-memory marker so this OnLoad pass does not re-trigger
            //    and the deferred reload's OnLoad sees no marker.
            ActiveTestBatchMarker = null;

            // 5. Schedule a one-frame-deferred REAL reload of the now-clean
            //    persistent.sfs so the in-memory game + ledger come from the .bak +
            //    snapshot (NOT just the disk files). A bare disk overwrite is
            //    insufficient: ledger-load (cold OnLoad) and DeferredSeedAndRecalculate
            //    have already loaded/patched the live career from the MUTATED save in
            //    THIS pass, and the next autosave would re-clobber the reverted .bak.
            //    The artifact sweep (.bak + slot + snapshot) is performed INSIDE the
            //    deferred coroutine, AFTER the reload has consumed them, so the
            //    snapshot survives until the clean reload reads it. Do NOT call
            //    SaveGame from inside OnLoad.
            if (reloadable)
            {
                // [M-A3 G3] Arm the H1 settle gate: hold autorun fire until the
                // deferred reload completes so the batch baseline is captured against
                // the reverted save, not this half-reverted OnLoad pass.
                CrashReconcileInProgress = true;
                ParsekLog.Info("TestBatch", $"crash-reconcile in progress: {reason}");
                StartCoroutine(DeferredReloadAfterTestBatchCrashReconcile(HighLogic.SaveFolder, marker));
            }
            else
            {
                // [M-A3 FIX 2] The marker was already nulled above (step 4), so H1's
                // MarkerWouldReconcile now reports false - but the on-disk save is only
                // HALF reverted here (disk revert incomplete) and NO deferred reload will
                // clean it. Leaving the H1 settle gate open would fire an autorun batch
                // against the mutated save and capture a poisoned baseline. Set the
                // in-progress flag so ReconcileGateClear holds fire FOREVER; unlike the
                // reloadable path there is no deferred-reload completion to clear it, so
                // the external orchestrator's timeout is the intended reaper.
                CrashReconcileInProgress = true;
                ParsekLog.Warn("TestBatch",
                    "crash-reconcile: disk revert incomplete "
                    + $"(persistent={diskReverted}, sidecars={sidecarsReverted}); skipping deferred "
                    + "reload and preserving the .bak + snapshot for manual recovery. Holding the "
                    + "autorun H1 gate closed indefinitely (no deferred reload will clear it; the "
                    + "orchestrator timeout is the reaper) so no autorun batch runs against the "
                    + "half-reverted save");
            }
        }

        /// <summary>Reverts the live save-scoped Parsek/ sidecar dir (where the ledger
        /// events.pgse lives) from the batch-start snapshot directory. OnLoad-safe (no
        /// SaveGame). Mirrors InGameTestRunner.RestoreParsekSidecarsFromSnapshot but is
        /// duplicated here so production code carries no dependency on the InGameTests
        /// namespace. A null/missing snapshot (DiskOnly mode) is a no-op success.
        /// Idempotent recursive replace: deletes the live dir then copies the snapshot
        /// in. The snapshot dir itself is left in place for the deferred reload + the
        /// post-reload artifact sweep.</summary>
        private static bool RestoreParsekSidecarDirOnReconcile(string snapshotDir)
        {
            if (string.IsNullOrEmpty(snapshotDir))
                return true; // DiskOnly mode: no sidecar snapshot to restore
            try
            {
                if (!System.IO.Directory.Exists(snapshotDir))
                {
                    ParsekLog.Warn("TestBatch",
                        $"crash-reconcile: Parsek/ snapshot missing at '{snapshotDir}'");
                    return false;
                }
                string liveParsekDir = RecordingPaths.ResolveSaveScopedPath("Parsek");
                if (string.IsNullOrEmpty(liveParsekDir))
                {
                    ParsekLog.Warn("TestBatch",
                        "crash-reconcile: could not resolve live save-scoped Parsek/ dir");
                    return false;
                }
                if (System.IO.Directory.Exists(liveParsekDir))
                    DeleteDirectoryRecursiveOnReconcile(liveParsekDir);
                CopyDirectoryRecursiveOnReconcile(snapshotDir, liveParsekDir);
                ParsekLog.Info("TestBatch",
                    $"crash-reconcile: reverted Parsek/ sidecar dir from snapshot '{snapshotDir}'");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("TestBatch",
                    $"crash-reconcile: Parsek/ sidecar revert threw: {ex.Message}");
                return false;
            }
        }

        /// <summary>Forces the deferred reload's OnLoad onto the COLD branch so it
        /// re-loads the ledger from the now-clean events.pgse. Mirrors the success
        /// path's PrepareForIsolatedBatchFlightBaselineRestore reset set (minus the
        /// recorder-unsubscribe + RecordingStore guard-bypass wipe, which the cold
        /// OnLoad's own ClearCommittedInternal already covers). Pure in-memory; no
        /// SaveGame.</summary>
        private static void PrepareInMemoryStateForTestBatchCrashReload()
        {
            ParsekLog.Info("TestBatch",
                "crash-reconcile: forcing cold in-memory reload (initialLoadDone=false + "
                + "ResetForTesting set) so the deferred reload re-loads the ledger from the "
                + "clean events.pgse");
            initialLoadDone = false;
            committedTreeStateLoaded = false;
            budgetDeductionApplied = false;
            mergeDialogPending = false;
            pendingActiveTreeResumeRewindSave = null;
            ScheduleActiveTreeRestoreOnFlightReady = ActiveTreeRestoreMode.None;
            vesselSwitchPending = false;
            vesselSwitchPendingFrame = -1;

            RecordingStore.ResetForBatchFlightBaselineRestoreBypassingGuard();
            GroupHierarchyStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RevertDetector.ResetForTesting();
        }

        /// <summary>Recursive directory copy, OnLoad-safe (no SaveGame). Duplicated
        /// here (not shared with InGameTests) so production code carries no dependency
        /// on the test namespace.</summary>
        private static void CopyDirectoryRecursiveOnReconcile(string sourceDir, string destDir)
        {
            System.IO.Directory.CreateDirectory(destDir);
            foreach (string dir in System.IO.Directory.GetDirectories(
                sourceDir, "*", System.IO.SearchOption.AllDirectories))
            {
                string rel = dir.Substring(sourceDir.Length)
                    .TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(destDir, rel));
            }
            foreach (string file in System.IO.Directory.GetFiles(
                sourceDir, "*", System.IO.SearchOption.AllDirectories))
            {
                string rel = file.Substring(sourceDir.Length)
                    .TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                string destFile = System.IO.Path.Combine(destDir, rel);
                string destParent = System.IO.Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destParent))
                    System.IO.Directory.CreateDirectory(destParent);
                System.IO.File.Copy(file, destFile, overwrite: true);
            }
        }

        private static void DeleteDirectoryRecursiveOnReconcile(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
                return;
            foreach (string file in System.IO.Directory.GetFiles(
                dir, "*", System.IO.SearchOption.AllDirectories))
            {
                try { System.IO.File.SetAttributes(file, System.IO.FileAttributes.Normal); }
                catch { /* best-effort attribute clear */ }
            }
            System.IO.Directory.Delete(dir, recursive: true);
        }

        /// <summary>Reverts persistent.sfs from the .bak bytes (no SaveGame;
        /// OnLoad-safe). Mirrors InGameTestRunner.RestoreCampaignPersistentSave but
        /// lives here so production code carries no dependency on the InGameTests
        /// assembly namespace.</summary>
        private static bool RestoreCampaignPersistentSaveOnReconcile(string backupPath)
        {
            if (string.IsNullOrEmpty(backupPath)) return false;
            try
            {
                if (!System.IO.File.Exists(backupPath))
                {
                    ParsekLog.Warn("TestBatch", $"crash-reconcile: .bak missing at '{backupPath}'");
                    return false;
                }
                string persistentPath = RecordingPaths.ResolveSaveScopedPath("persistent.sfs");
                if (string.IsNullOrEmpty(persistentPath)) return false;
                byte[] bytes = System.IO.File.ReadAllBytes(backupPath);
                FileIOUtils.SafeWriteBytes(bytes, persistentPath, "TestBatch");
                ParsekLog.Info("TestBatch",
                    $"crash-reconcile: reverted persistent.sfs ({bytes.Length} bytes) from .bak");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("TestBatch", $"crash-reconcile: persistent.sfs revert threw: {ex.Message}");
                return false;
            }
        }

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

        /// <summary>Best-effort crash-path artifact sweep: deletes the .bak named in
        /// the marker and derives + deletes the sibling slot .sfs / .loadmeta /
        /// -parsek/ snapshot (slot name = .bak filename minus the "-persistent.bak"
        /// suffix).</summary>
        private static void TryDeletePersistentBackupAndSnapshotArtifacts(TestBatchMarker marker)
        {
            try
            {
                string bak = marker?.PersistentBackupPath;
                if (string.IsNullOrEmpty(bak)) return;
                string savesDir = System.IO.Path.GetDirectoryName(bak);
                string bakName = System.IO.Path.GetFileName(bak); // "<slot>-persistent.bak"
                const string suffix = "-persistent.bak";
                string slot = bakName.EndsWith(suffix, StringComparison.Ordinal)
                    ? bakName.Substring(0, bakName.Length - suffix.Length)
                    : null;

                TryDeleteFileQuiet(bak);
                if (!string.IsNullOrEmpty(slot) && !string.IsNullOrEmpty(savesDir))
                {
                    TryDeleteFileQuiet(System.IO.Path.Combine(savesDir, slot + ".sfs"));
                    TryDeleteFileQuiet(System.IO.Path.Combine(savesDir, slot + ".loadmeta"));
                }
                // Delete the snapshot dir the marker actually recorded (authoritative),
                // falling back to the slot-derived sibling path for older markers that
                // predate the ParsekSnapshotDir field.
                string snapshotDir = !string.IsNullOrEmpty(marker.ParsekSnapshotDir)
                    ? marker.ParsekSnapshotDir
                    : (!string.IsNullOrEmpty(slot) && !string.IsNullOrEmpty(savesDir)
                        ? System.IO.Path.Combine(savesDir, slot + "-parsek")
                        : null);
                if (!string.IsNullOrEmpty(snapshotDir) && System.IO.Directory.Exists(snapshotDir))
                    System.IO.Directory.Delete(snapshotDir, recursive: true);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("TestBatch", $"crash-reconcile artifact sweep partial: {ex.Message}");
            }
        }

        private static void TryDeleteFileQuiet(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path)) System.IO.File.Delete(path); }
            catch (Exception ex) { ParsekLog.Warn("TestBatch", $"crash-reconcile delete '{path}' failed: {ex.Message}"); }
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
        private static bool cachedAutoMerge = false;

        // Deferred merge dialog: when autoMerge is off, the dialog follows the player
        // across scenes until they address it. Reset on each OnLoad so scene changes
        // get a fresh chance to show the dialog.
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

        private static void ReconcileReadableSidecarMirrorsOnLoadIfDisabled()
        {
            var settings = ParsekSettings.Current;
            if (settings != null && !settings.writeReadableSidecarMirrors)
                RecordingStore.ReconcileReadableSidecarMirrorsForKnownRecordings();
        }

        internal static HashSet<string> BuildValidRecordingIdsForKspLoad()
        {
            return RecordingStore.BuildKnownRecordingIds();
        }

        public override void OnLoad(ConfigNode node)
        {
            DiagnosticsState.ResetSessionCounters();
            IncompleteBallisticSceneExitFinalizer.ResetLifecycleDiagnostics();
            var sw = Stopwatch.StartNew();
            int loadedRecordingCount = 0;
            string loadPhase = "entry";
            string loadStatus = "running";
            try
            {
                // Reset deferred dialog flag and clear input lock (dialog may have been
                // destroyed by scene change without the user clicking a button)
                loadPhase = "reset-dialog";
                mergeDialogPending = false;
                InputLockManager.RemoveControlLock("ParsekMergeDialog");

                // Restore user-intent settings (tracking-station visibility, readable
                // sidecar mirrors, etc.) from the
                // external settings.cfg file, overwriting whatever stale value KSP's
                // GameParameters restored from the save. Runs before anything reads
                // ParsekSettings.Current so the rest of OnLoad sees the fresh values.
                // Survives rewind (parsek_rw_* quicksaves), save/load, and KSP restart.
                loadPhase = "settings";
                ParsekSettingsPersistence.ApplyTo(ParsekSettings.Current);
                ParsekLog.RecState("OnLoad:settings-applied", CaptureScenarioRecorderState());

                var recordings = RecordingStore.CommittedRecordings;
                loadedRecordingCount = recordings.Count;

                loadPhase = "hooks";
                RegisterMainMenuHook();
                DetectSaveFolderChange();
                if (!RewindContext.IsRewinding)
                    KerbalLoadRepairDiagnostics.Begin();
                loadPhase = "crew-groups";
                LoadCrewAndGroupState(node);
                // Rewind-to-Staging Phase 1 (design sections 5.1-5.9). Load runs
                // on every OnLoad so a revert or scene change rebuilds the lists
                // from .sfs rather than reusing stale in-memory state.
                loadPhase = "refly-state-load";
                LoadRewindStagingState(node);

                // PR #774 cross-LoadScene fix: re-apply the rewind-time supersede drop
                // performed in RecordingStore.InitiateRewind. The in-memory mutation
                // there is reverted by KSP's scenario-state restoration across the
                // LoadScene boundary, so without this re-apply the dropped rows return
                // and downstream branch ghosts stay suppressed by
                // `reason=superseded-by-relation` for the rest of the session. Gated
                // internally on `RewindContext.IsRewinding` so non-rewind loads are
                // a no-op.
                loadPhase = "supersede-rewind-reapply";
                RecordingStore.ReapplyRewindSupersedeDropAfterLoad();

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
                    PreParsekBackup.MaybeBackupOnFirstColdContact();
                }

                // Game state recorder lifecycle — re-subscribe on every OnLoad (handles reverts)
                loadPhase = "game-state-recorder";
                stateRecorder?.Unsubscribe();
                if (!initialLoadDone)
                    LoadExternalFiles();
                stateRecorder = new GameStateRecorder();
                stateRecorder.SeedFacilityCacheFromCurrentState();
                stateRecorder.Subscribe();

                loadPhase = "initial-baseline";
                SubscribeVesselLifecycleEvents();
                CaptureInitialBaseline();

                if (initialLoadDone)
                {
                    // Go-back detection: must be BEFORE revert detection and BEFORE any
                    // .sfs data loading. In-memory state is the source of truth.
                    if (RewindContext.IsRewinding)
                    {
                        loadPhase = "rewind";
                        HandleRewindOnLoad(node, recordings);
                        ReconcileReadableSidecarMirrorsOnLoadIfDisabled();
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
                    bool activeTreeRestoredFromSave = TryRestoreActiveTreeNode(node);
                    bool pendingTreeRestoredFromSave = TryRestorePendingTreeNode(
                        node, activeTreeRestoredFromSave);
                    ParsekLog.RecState("OnLoad:active-tree-restored", CaptureScenarioRecorderState());

                    loadPhase = "revert-classification";
                    ConfigNode[] savedRecNodes = node.GetNodes("RECORDING");
                    if (savedRecNodes.Length > 0)
                        ParsekLog.Warn("Scenario",
                            $"OnLoad: found {savedRecNodes.Length} legacy standalone RECORDING node(s) — " +
                            "these are no longer loaded (T56). Re-save to remove them.");

                    // Count tree recordings from saved tree nodes for accurate revert detection.
                    // All committed recordings are serialized under RECORDING_TREE nodes.
                    // Skip active-tree (in-flight) marker nodes — they're not "committed" recordings.
                    ConfigNode[] savedTreeNodesForRevert = node.GetNodes("RECORDING_TREE");
                    int savedTreeRecCount = 0;
                    for (int t = 0; t < savedTreeNodesForRevert.Length; t++)
                    {
                        if (IsActiveTreeNode(savedTreeNodesForRevert[t])) continue;
                        if (IsPendingTreeNode(savedTreeNodesForRevert[t])) continue;
                        savedTreeRecCount += savedTreeNodesForRevert[t].GetNodes("RECORDING").Length;
                    }
                    int totalSavedRecCount = savedRecNodes.Length + savedTreeRecCount;

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
                    bool hasOrphanedLimboTree = RecordingStore.HasPendingTree
                        && RecordingStore.PendingTreeStateValue == PendingTreeState.Limbo
                        && !activeTreeRestoredFromSave;
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
                    bool isRevert = !isVesselSwitch && revertKind != RevertKind.None;
                    ParsekLog.Verbose("Scenario",
                        $"OnLoad: revert detection — revertKind={revertKind}, " +
                        $"savedRecNodes={savedRecNodes.Length}, savedTreeRecs={savedTreeRecCount}, " +
                        $"memoryRecordings={recordings.Count}, lastOnSaveScene={lastOnSaveScene}, " +
                        $"isFlightToFlight={isFlightToFlight}, isVesselSwitch={isVesselSwitch}, isRevert={isRevert}, " +
                        $"pendingTreeState={RecordingStore.PendingTreeStateValue}, " +
                        $"activeTreeRestoredFromSave={activeTreeRestoredFromSave}, " +
                        $"pendingTreeRestoredFromSave={pendingTreeRestoredFromSave}, " +
                        $"hasOrphanedLimboTree={hasOrphanedLimboTree}");

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
                            ParsekLog.Info("Scenario",
                                $"Captured fresh-launch UT {loadedUT.ToString("R", CultureInfo.InvariantCulture)} " +
                                "for the Revert-to-editor orphan-prune boundary");
                            break;
                        case FreshLaunchUtAction.Clear:
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
                    if (ShouldRunQuickloadDiscard(utWentBackwards, isFlightToFlight, isRevert))
                    {
                        DiscardStashedOnQuickload(preChangeUT, loadedUT);
                    }

                    ParsekLog.RecState(
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
                        bool alreadyHasCleanupData = RecordingStore.PendingCleanupPids != null
                                                      || RecordingStore.PendingCleanupNames != null;
                        if (alreadyHasCleanupData)
                        {
                            ParsekLog.Info("Scenario",
                                $"OnLoad: revert path skipping cleanup collection — " +
                                $"already set ({RecordingStore.PendingCleanupPids?.Count ?? 0} pid(s), " +
                                $"{RecordingStore.PendingCleanupNames?.Count ?? 0} name(s)) from prior rewind/revert");
                        }
                        else
                        {
                            var info = RecordingStore.CollectSpawnedVesselInfo();
                            var spawnedPids = info.pids.Count > 0 ? info.pids : null;
                            var spawnedNames = info.names.Count > 0 ? info.names : null;
                            RecordingStore.PendingCleanupPids = spawnedPids;
                            RecordingStore.PendingCleanupNames = spawnedNames;
                            ParsekLog.Verbose("Scenario",
                                $"OnLoad: revert cleanup collected — " +
                                $"{spawnedPids?.Count ?? 0} pid(s), {spawnedNames?.Count ?? 0} name(s)");
                        }

                        // Clear pending tree/recording from a PREVIOUS flight — dialog was shown
                        // but user reverted before acting on it. Prevents OnFlightReady fallback
                        // from showing the dialog again (#64).
                        // However, if the pending was stashed during THIS scene transition
                        // (OnSceneChangeRequested → StashPendingTree), it is fresh and must
                        // survive long enough for the current OnLoad to classify it correctly.
                        // On true revert the later soft-unstash branch below clears it again;
                        // on quickload/non-revert paths other dispatch owns it.
                        if (RecordingStore.PendingStashedThisTransition)
                        {
                            ParsekLog.Info("Scenario",
                                "Revert: keeping freshly-stashed pending (stashed this transition) — " +
                                $"tree={RecordingStore.HasPendingTree}");
                            RecordingStore.PendingStashedThisTransition = false;
                        }
                        else
                        {
                            if (RecordingStore.HasPendingTree)
                            {
                                // Limbo trees are stashed for OnLoad dispatch (merge dialog
                                // accept → StashActiveTreeAsPendingLimbo). The quickload
                                // discard path (DiscardPendingOnQuickload) correctly preserves
                                // them but resets PendingStashedThisTransition, so the flag
                                // is false by the time we get here. Check the state instead
                                // of relying solely on the flag — Limbo trees are never
                                // orphaned (#290).
                                var treeState = RecordingStore.PendingTreeStateValue;
                                if (treeState == PendingTreeState.Limbo
                                    || treeState == PendingTreeState.LimboVesselSwitch)
                                {
                                    ParsekLog.Info("Scenario",
                                        $"Revert: keeping pending Limbo tree " +
                                        $"'{RecordingStore.PendingTree?.TreeName}' " +
                                        $"(state={treeState}) — stashed for dispatch");
                                }
                                else
                                {
                                    ParsekLog.Info("Scenario", "Clearing orphaned pending tree on revert (stale from previous flight)");
                                    DiscardPendingTreeAndAbandonDeferredFlightResults(
                                        "orphaned pending tree discarded on revert");
                                }
                            }
                        }
                    }

                    // Restore tree recording mutable state from RECORDING_TREE nodes.
                    // First, reset ALL tree recordings to defaults. On revert, the launch
                    // quicksave has no tree nodes so this reset is the only thing that runs,
                    // ensuring VesselSpawned/SpawnedPid/etc. don't carry over from the
                    // committed flight (whose vessels were undone by the revert).
                    // On scene change, the reset is overwritten by the saved values below.
                    loadPhase = "tree-mutable-state";
                    for (int i = 0; i < recordings.Count; i++)
                    {
                        if (!recordings[i].IsTreeRecording) continue;

                        RecordingStore.RollbackContinuationData(recordings[i]);
                        ClearPostSpawnTerminalState(recordings[i], "tree recording");

                        recordings[i].VesselSpawned = false;
                        recordings[i].SpawnAttempts = 0;
                        recordings[i].SpawnDeathCount = 0;
                        recordings[i].SpawnedVesselPersistentId = 0;
                        TerminalOrbitSpawnSafety.Clear(recordings[i]);

                        recordings[i].LastAppliedResourceIndex = -1;
                    }

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
                        RecordingStore.PendingRevertPreExistingPids = revertTargetPids;

                        var flightState = HighLogic.CurrentGame?.flightState;
                        if (flightState != null)
                        {
                            var allCommitted = RecordingStore.CollectAllCommittedRecordings();
                            StripOrphanedSpawnedVessels(
                                flightState.protoVessels, allCommitted,
                                matchSource: false, skipPrelaunch: true,
                                requireWhitelist: true, preExistingWhitelist: revertTargetPids);
                        }
                    }

                    // Rescue crew orphaned by vessel stripping (#116)
                    if (isRevert && HighLogic.CurrentGame?.flightState != null)
                        CrewReservationManager.RescueOrphanedCrew(
                            HighLogic.CurrentGame.flightState.protoVessels);

                    // Then restore from saved tree nodes (present on scene change, absent on revert)
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
                        bool hadPendingTree = RecordingStore.HasPendingTree;
                        RecordingStore.UnstashPendingTreeOnRevert();

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
                        int prunedOrphans = Ledger.PruneOrphanActionsAfterUT(pruneCutoffUT, pruneInclusive);
                        if (prunedOrphans > 0)
                            ParsekLog.Info("Scenario",
                                $"Revert ({revertKind}): pruned {prunedOrphans} untagged ledger action(s) " +
                                $"{(pruneInclusive ? "at/after" : "after")} launchUT=" +
                                $"{pruneCutoffUT.ToString("R", CultureInfo.InvariantCulture)} " +
                                "(launch-pad/PRELAUNCH currency earned before auto-record)");

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
                    if (RecordingStore.HasPendingTree
                        && (RecordingStore.PendingTreeStateValue == PendingTreeState.Limbo
                            || RecordingStore.PendingTreeStateValue == PendingTreeState.LimboVesselSwitch))
                    {
                        ParsekLog.RecState("OnLoad:limbo-dispatched", CaptureScenarioRecorderState());
                        var pendState = RecordingStore.PendingTreeStateValue;
                        if (pendState == PendingTreeState.LimboVesselSwitch)
                        {
                            // Bug #266: tree was pre-transitioned at stash time. Just defer
                            // to OnFlightReady for the vessel-switch restore coroutine, which
                            // reinstalls the tree and (optionally) promotes the new active
                            // vessel from BackgroundMap.
                            ClearPendingQuickloadResumeContext();
                            ScheduleActiveTreeRestoreOnFlightReady = ActiveTreeRestoreMode.VesselSwitch;
                            ParsekLog.Info("Scenario",
                                $"OnLoad: pending-LimboVesselSwitch tree '{RecordingStore.PendingTree?.TreeName}' " +
                                "deferred to OnFlightReady for vessel-switch restore (#266)");
                        }
                        else if (isVesselSwitch)
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
                            ConfigurePendingQuickloadResumeContext(RecordingStore.PendingTree);
                            ScheduleActiveTreeRestoreOnFlightReady = ActiveTreeRestoreMode.Quickload;
                            ParsekLog.Info("Scenario",
                                $"OnLoad: pending-Limbo tree '{RecordingStore.PendingTree?.TreeName}' " +
                                "deferred to OnFlightReady for quickload-resume");
                        }
                    }

                    // Handle pending recordings on non-revert scene exits to non-flight scenes.
                    // The pre-transition merge dialog (SceneExitInterceptor) is the
                    // primary path; this OnLoad path is the deferred fallback for
                    // any flight-exit that bypassed our HighLogic.LoadScene prefix
                    // (KSP version drift, foreign mod patch, etc.). Pre-transition
                    // path now handles MAINMENU too, so the historical
                    // forceAutoMerge=MAINMENU shortcut is gone.
                    loadPhase = "pending-outside-flight";
                    if (!isRevert && HighLogic.LoadedScene != GameScenes.FLIGHT)
                    {
                        if (IsAutoMerge)
                        {
                            // #88 folded: landed/splashed no longer forces an approval
                            // dialog under autoMerge — the silent commit is now
                            // full-fidelity (spawn-at-end preserved), so it is safe for
                            // surviving vessels.

                            // Auto-discard idle-on-pad before auto-commit. Only for
                            // Finalized trees (Limbo trees are resume-flow stashes).
                            if (RecordingStore.HasPendingTree
                                && RecordingStore.PendingTreeStateValue == PendingTreeState.Finalized
                                && ParsekFlight.IsTreeIdleOnPad(RecordingStore.PendingTree))
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
                        else if (RecordingStore.HasPendingTree && !mergeDialogPending)
                        {
                            // autoMerge OFF: defer to merge dialog in the new scene
                            mergeDialogPending = true;
                            StartCoroutine(ShowDeferredMergeDialog());
                        }
                    }

                    GameScenes loadedScene = HighLogic.LoadedScene;
                    bool loadedSceneSupportsCurrentUtCutoff =
                        IsCurrentUtCutoffSupportedScene(loadedScene);
                    bool hasPendingTree = RecordingStore.HasPendingTree;
                    ActiveTreeRestoreMode restoreMode = ScheduleActiveTreeRestoreOnFlightReady;
                    bool hasLiveRecorder = GameStateRecorder.HasLiveRecorder();
                    bool hasActiveUncommittedTree = GameStateRecorder.HasActiveUncommittedTree();
                    bool hasFutureLedgerActions = LedgerOrchestrator.HasActionsAfterUT(loadedUT);
                    loadPhase = "ledger-recalculate";
                    bool useCurrentUtCutoffForPostRewindFlightLoad =
                        ShouldUseCurrentUtCutoffForPostRewindFlightLoad(
                            isRevert,
                            loadedSceneSupportsCurrentUtCutoff,
                            planetariumReady,
                            hasPendingTree,
                            restoreMode,
                            hasLiveRecorder,
                            hasActiveUncommittedTree,
                            hasFutureLedgerActions);
                    ParsekLog.Info("Scenario",
                        $"OnLoad: post-rewind current-UT cutoff decision useCurrentUtCutoff={useCurrentUtCutoffForPostRewindFlightLoad} " +
                        $"loadedUT={loadedUT.ToString("R", CultureInfo.InvariantCulture)} " +
                        $"isRevert={isRevert} loadedScene={loadedScene} " +
                        $"loadedSceneSupportsCurrentUtCutoff={loadedSceneSupportsCurrentUtCutoff} " +
                        $"planetariumReady={planetariumReady} hasPendingTree={hasPendingTree} " +
                        $"restoreMode={restoreMode} hasLiveRecorder={hasLiveRecorder} " +
                        $"hasActiveUncommittedTree={hasActiveUncommittedTree} " +
                        $"hasFutureLedgerActions={hasFutureLedgerActions}");
                    if (useCurrentUtCutoffForPostRewindFlightLoad)
                    {
                        ParsekLog.Info("Scenario",
                            $"OnLoad: post-rewind scene-load recalc using current-UT cutoff {loadedUT.ToString("R", CultureInfo.InvariantCulture)} " +
                            "to keep future funds/contracts filtered until replay catches up");
                        RecalculateAndPatchForPostRewindFlightLoad(loadedUT);
                    }
                    else
                    {
                        LedgerOrchestrator.RecalculateAndPatch();
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
                    DispatchRewindPostLoadIfPending();

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

                    loadPhase = "test-batch-reconcile";
                    RunTestBatchCrashReconcile();

                    // Phase 11 housekeeping pass — same rationale as the
                    // cold-start branch below; runs after the finisher so
                    // live-session RPs stay put.
                    loadPhase = "rewind-point-reap";
                    RewindPointReaper.ReapOrphanedRPs();
                    loadedRecordingCount = recordings.Count;
                    loadStatus = isRevert ? "returned-revert" : "returned-scene-change";
                    return;
                }

                loadPhase = "cold-start";
                initialLoadDone = true;

                loadPhase = "stale-pending-discard";
                DiscardStalePendingState();

                loadPhase = "clear-committed";
                RecordingStore.ClearCommittedInternal();

                // Validate chain integrity before any playback
                loadPhase = "chain-validation";
                RecordingStore.ValidateChains();

                loadPhase = "recording-trees";
                LoadRecordingTrees(node, recordings);
                loadedRecordingCount = recordings.Count;
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
                if (TryRestoreActiveTreeNode(node))
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
                        RecordingStore.PendingTreeStateValue == PendingTreeState.LimboVesselSwitch
                            ? ActiveTreeRestoreMode.VesselSwitch
                            : ActiveTreeRestoreMode.Quickload;
                    if (ScheduleActiveTreeRestoreOnFlightReady == ActiveTreeRestoreMode.Quickload)
                        ConfigurePendingQuickloadResumeContext(RecordingStore.PendingTree);
                    else
                        ClearPendingQuickloadResumeContext();
                    ParsekLog.Info("Scenario",
                        $"OnLoad: cold-start active tree detected (state={RecordingStore.PendingTreeStateValue}) — " +
                        $"deferred to OnFlightReady as {ScheduleActiveTreeRestoreOnFlightReady}");
                }
                bool coldActiveTreeRestoredFromSave =
                    RecordingStore.PendingTreeStateValue == PendingTreeState.Limbo
                    || RecordingStore.PendingTreeStateValue == PendingTreeState.LimboVesselSwitch;
                bool coldPendingTreeRestoredFromSave = TryRestorePendingTreeNode(
                    node, coldActiveTreeRestoredFromSave);
                if (coldPendingTreeRestoredFromSave)
                {
                    string pendingRestoreDisposition = RecordingStore.HasSavedPendingTreeDuringActiveRestore
                        ? "preserved alongside active-tree restore"
                        : "restored as finalized pending";
                    ParsekLog.Info("Scenario",
                        $"OnLoad: cold-start pending tree detected (state={RecordingStore.PendingTreeStateValue}) — " +
                        pendingRestoreDisposition);
                }

                // Clean orphaned sidecar files (recordings deleted in previous sessions)
                loadPhase = "orphan-sidecar-cleanup";
                RecordingStore.CleanOrphanFiles();

                // Restore milestone mutable state (LastReplayedEventIndex) from .sfs
                loadPhase = "milestones";
                MilestoneStore.RestoreMutableState(node);

                // Reconcile ledger against loaded recordings (prunes orphaned actions, recalculates)
                loadPhase = "ledger-load";
                var validIds = BuildValidRecordingIdsForKspLoad();
                double reconcileUT = Planetarium.GetUniversalTime();
                LedgerOrchestrator.OnKspLoad(
                    validIds,
                    reconcileUT,
                    useCurrentUtCutoffForFutureActions:
                        IsCurrentUtCutoffSupportedScene(HighLogic.LoadedScene));

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
                    double reconcileUtForLoops = Planetarium.fetch != null
                        ? Planetarium.GetUniversalTime() : 0.0;
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
                double loadUT = Planetarium.GetUniversalTime();
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
                RecordingStore.RunOptimizationPass();

                // Auto-unreserve crew for recordings whose EndUT has already passed
                // but vessel was never spawned. Skip at SpaceCenter — ParsekKSC now
                // handles spawning there (bug #99), so nulling the snapshot here would
                // pre-empt the KSC spawn.
                loadPhase = "crew-auto-unreserve";
                double currentUT = Planetarium.GetUniversalTime();
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
                    RecordingStore.HasPendingTree)
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
                    if (RecordingStore.PendingTreeStateValue == PendingTreeState.Finalized
                        && ParsekFlight.IsTreeIdleOnPad(RecordingStore.PendingTree))
                    {
                        ScenarioLog("[Parsek Scenario] Idle on pad — auto-discarding pending tree");
                        DiscardPendingTreeAndRecalculate(
                            "outside-flight idle-on-pad auto-discard");
                    }
                    else if (RecordingStore.PendingTreeStateValue != PendingTreeState.Finalized)
                    {
                        ParsekLog.Verbose("Scenario",
                            $"Idle-on-pad auto-discard skipped: pending tree state is " +
                            $"{RecordingStore.PendingTreeStateValue} (not Finalized), " +
                            "resume/restore flow will run instead");
                    }

                    if (IsAutoMerge || HighLogic.LoadedScene == GameScenes.MAINMENU)
                    {
                        // Silent auto-commit: full-fidelity MergeCommit when the tree
                        // qualifies (autoMerge, Finalized, no active re-fly, real scene),
                        // else the lightweight ghost-only commit. MAINMENU (game
                        // unloading) and non-autoMerge always fall to ghost-only. The
                        // originating flight already applied its resources live, so the
                        // commit marks the tree applied to disarm the lump-sum replay.
                        AutoCommitPendingTreeOutsideFlight("cold-load outside-flight");
                    }
                    else if (!mergeDialogPending)
                    {
                        // autoMerge OFF: defer to merge dialog
                        mergeDialogPending = true;
                        StartCoroutine(ShowDeferredMergeDialog());
                    }
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
                DispatchRewindPostLoadIfPending();

                // Phase 10 of Rewind-to-Staging (design §6.9 step 2): if a
                // staged-commit merge crashed mid-way, the scenario's
                // MergeJournal persisted across the load. The finisher either
                // rolls back (pre-Durable1 crash) or drives the remaining
                // steps to completion (post-Durable1 crash). Runs AFTER Phase 6
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
                KerbalLoadRepairDiagnostics.Reset();
                // Always capture timing exactly once, even on exception.
                loadedRecordingCount = SafeCommittedRecordingCount();
                WriteLoadTiming(sw, loadedRecordingCount, loadPhase, loadStatus);
            }
        }

        /// <summary>
        /// Phase 6 of Rewind-to-Staging: drains <see cref="RewindInvokeContext"/>
        /// if a re-fly invocation was initiated pre-load. Runs in the new
        /// scenario after the normal OnLoad pipeline has settled — bundle
        /// restore overrides the .sfs-loaded recordings / ledger / scenario
        /// lists with the pre-load in-memory state, as per the §6.4 table.
        /// <para>
        /// Only fires when <c>LoadedScene == FLIGHT</c>; the RP quicksave is
        /// always a flight-scene save. Any other scene means the load took an
        /// unexpected branch — log Error and clear the context.
        /// </para>
        /// </summary>
        private static void DispatchRewindPostLoadIfPending()
        {
            if (!RewindInvokeContext.Pending) return;

            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                ParsekLog.Error("Rewind",
                    $"DispatchRewindPostLoadIfPending: pending invocation " +
                    $"but scene is {HighLogic.LoadedScene} (expected FLIGHT) — aborting");
                RewindInvoker.ShowUserError(
                    $"Rewind failed: scene loaded as {HighLogic.LoadedScene} " +
                    "instead of flight");
                RewindInvokeContext.Clear();
                return;
            }

            RewindInvoker.ConsumePostLoad();
        }

        /// <summary>
        /// Handles the rewind (go-back) path during OnLoad. Restores milestone state,
        /// collects cleanup PIDs/names, resets playback state, strips orphaned vessels,
        /// schedules resource adjustment, re-reserves crew, and clears rewind flags.
        /// </summary>
        private void HandleRewindOnLoad(ConfigNode node, IReadOnlyList<Recording> recordings)
        {
            ParsekLog.RecState("HandleRewindOnLoad:entry", CaptureScenarioRecorderState());
            ParsekLog.Info("Rewind",
                $"OnLoad: rewind detected, skipping .sfs recording/crew load " +
                $"(using {recordings.Count} in-memory recordings)");
            ClearActiveReFlyMarkerForPlainRewind();

            // Restore milestone mutable state with resetUnmatched=true
            // (milestones created after rewind point get reset to unreplayed)
            MilestoneStore.RestoreMutableState(node, resetUnmatched: true);

            // Collect spawned vessel PIDs for belt-and-suspenders in OnFlightReady
            var (rewindSpawnedPids, _) = RecordingStore.CollectSpawnedVesselInfo();
            RecordingStore.PendingCleanupPids = rewindSpawnedPids.Count > 0 ? rewindSpawnedPids : null;

            // Collect ALL recording vessel names for flightState stripping.
            // On rewind, ANY vessel matching a recording name is from the future
            // and must be stripped — not just those with non-zero SpawnedVesselPersistentId.
            // (PIDs may already be zero from a previous rewind's ResetAllPlaybackState.)
            var allRecordingNames = RecordingStore.CollectAllRecordingVesselNames();
            RecordingStore.PendingCleanupNames = allRecordingNames.Count > 0 ? allRecordingNames : null;
            ParsekLog.Info("Rewind",
                $"OnLoad: rewind cleanup data set — " +
                $"{rewindSpawnedPids.Count} pid(s), {allRecordingNames.Count} name(s)");

            // Reset ALL playback state (recordings + trees)
            var (standaloneCount, treeCount) = RecordingStore.ResetAllPlaybackState();
            ParsekLog.Info("Rewind",
                $"OnLoad: resetting playback state for {standaloneCount} recordings + {treeCount} trees");

            // Mark only the active/source recording that plain Rewind-to-Launch stripped
            // from flightState. Same-tree future recordings remain spawn-eligible so
            // their normal terminal materialization can happen after playback reaches
            // their EndUT (#589), while #573 still blocks the source duplicate.
            int protectedCount = MarkRewoundTreeRecordingsAsGhostOnly(recordings);
            ParsekLog.Info("Rewind",
                $"OnLoad: SpawnSuppressedByRewind scope evaluated — " +
                $"protectedActiveSource={protectedCount}; same-tree future recordings remain spawn-eligible (#573/#589)");

            // Strip vessels belonging to a recording's launch from flightState.
            // The rewind save was preprocessed to strip the recorded vessel,
            // but KSP's scene transition may reintroduce vessels from the old
            // persistent.sfs. On rewind every vessel that belongs to a recording's
            // launch (its recorded source OR its spawn/adoption endpoint) is from the
            // future and is removed.
            //
            // BUG-H: launch-identity-aware (pid + launch-Guid), so a DIFFERENT real
            // launch that merely reuses the craft-baked name/pid is never deleted.
            // requireWhitelist=false preserves the rewind contract: genuinely
            // future-timeline vessels are still removed (the launch-identity match here
            // plus the separate StripFuturePrelaunchVessels pid-whitelist pass below).
            // The rewind quicksave pids are passed as a protect-set backstop so a vessel
            // that pre-existed the rewind point (present in the quicksave) is never deleted
            // even for the rare guidless adoption-stamp recording where the Guid gate alone
            // would fall back to pid-only. When the quicksave pids are unavailable this is
            // null and the strip falls back to identity-only (its prior behaviour).
            {
                var flightState = HighLogic.CurrentGame?.flightState;
                if (flightState != null)
                {
                    var allCommitted = RecordingStore.CollectAllCommittedRecordings();
                    StripOrphanedSpawnedVessels(
                        flightState.protoVessels, allCommitted,
                        matchSource: true, skipPrelaunch: false,
                        requireWhitelist: false,
                        preExistingWhitelist: RewindContext.RewindQuicksaveVesselPids);
                }
            }

            // Rewind strip already handled protoVessel cleanup in flightState.
            // Clear pending data so OnFlightReady doesn't re-run with overbroad names
            // that would match freshly-spawned past vessels (bug #134). The revert path's
            // alreadyHasCleanupData guard (line ~352) will see null and collect
            // fresh data from CollectSpawnedVesselInfo() if needed.
            RecordingStore.PendingCleanupPids = null;
            RecordingStore.PendingCleanupNames = null;
            RecordingStore.PendingRevertPreExistingPids = null;
            ParsekLog.Info("Rewind",
                "OnLoad: cleared PendingCleanupPids/Names after strip — " +
                "prevents OnFlightReady from destroying freshly-spawned past vessels");

            // Strip PRELAUNCH vessels from the future (bug #129).
            // StripOrphanedSpawnedVessels filters by name — unrecorded PRELAUNCH vessels
            // (e.g. a pad vessel from a later launch) fail the name check and survive.
            // Use the quicksave PID whitelist to identify and remove them.
            var quicksavePids = RewindContext.RewindQuicksaveVesselPids;
            if (quicksavePids != null)
            {
                var fs = HighLogic.CurrentGame?.flightState;
                if (fs != null)
                {
                    int prelaunchStripped = StripFuturePrelaunchVessels(fs.protoVessels, quicksavePids);
                    if (prelaunchStripped > 0)
                        ParsekLog.Info("Rewind",
                            $"Stripped {prelaunchStripped} future vessel(s) not in quicksave whitelist");
                }
                RewindContext.SetQuicksaveVesselPids(null);
            }

            // Defense-in-depth: reconcile spawn state after all strips (#168).
            // ResetAllPlaybackState already zeroed spawn PIDs, so this should be a no-op,
            // but guards against any future code path that restores PIDs before the strip.
            {
                var fsReconcile = HighLogic.CurrentGame?.flightState;
                if (fsReconcile != null)
                    ReconcileSpawnStateAfterStrip(fsReconcile.protoVessels, recordings);
            }

            // Rescue crew orphaned by vessel stripping (#116).
            if (HighLogic.CurrentGame?.flightState != null)
                CrewReservationManager.RescueOrphanedCrew(
                    HighLogic.CurrentGame.flightState.protoVessels);

            // Schedule resource + UT adjustment (deferred — singletons from the OLD
            // scene may still be alive during OnLoad; we must yield at least one frame
            // so the new scene's Funding/R&D/Reputation/Planetarium are initialized).
            // Setting UT before LoadScene does NOT work — scene transition overwrites it.
            RecordingStore.RewindUTAdjustmentPending = true;
            // Drawdown-guard signal 5 (Blocker 1): arm BEFORE scheduling the coroutine so
            // it is true before EndRewind() below clears IsRewinding. The coroutine's
            // try/finally around the deferred RecalculateAndPatch clears it (authoritative);
            // the next scene's OnAwake clears it as a race-free fail-safe if the host is
            // destroyed mid-wait. This authorizes the deferred plain-rewind drawdown, which
            // runs after all four other signals are false.
            RewindContext.BeginRewindResourceAdjustment();
            StartCoroutine(ApplyRewindResourceAdjustment());
            ParsekLog.Info("Rewind",
                "OnLoad: resource + UT adjustment deferred (waiting for new scene singletons)");

            // Restore career state to the rewind target. The cutoff walk keeps
            // funds/science/tech at the adjusted UT; LedgerOrchestrator then
            // reprojects crew reservations from the full committed timeline so
            // future recorded crew remain unavailable for new missions.
            // RewindContext.RewindAdjustedUT is still populated at this point;
            // EndRewind() below clears it. The deferred coroutine captures the
            // same value into a local BEFORE its yield so its second call is
            // independent of RewindContext state.
            GameStateStore.PruneBaselinesAfterUT(RewindContext.RewindAdjustedUT);
            LedgerOrchestrator.RecalculateAndPatch(
                RewindContext.RewindAdjustedUT,
                suppressSuspiciousDrawdownWarnings: true);

            // Clear rewind flags — rewind loads into SpaceCenter, not Flight
            ParsekLog.Info("Rewind",
                $"OnLoad: rewind complete at UT {RewindContext.RewindUT}. " +
                $"Timeline: {recordings.Count} recordings");
            // Any pending recovery-funds callbacks deferred before the rewind cannot
            // pair after the boundary — stock already fired (or will never fire) the
            // paired FundsChanged(VesselRecovery) event relative to the old timeline.
            LedgerOrchestrator.FlushStalePendingRecoveryFunds("rewind end");
            RecoveryPayoutContextStore.Clear("rewind end");
            RewindContext.EndRewind();
            ParsekLog.RecState("HandleRewindOnLoad:exit", CaptureScenarioRecorderState());
        }

        /// <summary>
        /// Clears a loaded active Re-Fly marker while the plain rewind OnLoad branch resumes replay.
        /// </summary>
        internal bool ClearActiveReFlyMarkerForPlainRewind()
        {
            var marker = ActiveReFlySessionMarker;
            if (marker == null)
                return false;

            string sessionId = marker.SessionId ?? "<no-id>";
            string activeRecordingId = marker.ActiveReFlyRecordingId ?? "<no-id>";
            string originRecordingId = marker.OriginChildRecordingId ?? "<no-id>";
            string rewindPointId = marker.RewindPointId ?? "<no-rp>";

            // This discards only stale session state; supersede caches do not
            // depend on the marker and should not be bumped for this cleanup.
            ActiveReFlySessionMarker = null;
            Parsek.Rendering.RenderSessionState.Clear("plain-rewind");
            // Plain rewind loads before FlightDriver state is ready, so Apply is
            // normally a logged no-op here. Keep it paired with marker clears.
            ReFlyRevertButtonGate.Apply("PlainRewind:clear-refly-marker");
            ParsekLog.Info("Rewind",
                "OnLoad: cleared stale active Re-Fly marker during plain rewind " +
                $"sess={sessionId} active={activeRecordingId} origin={originRecordingId} rp={rewindPointId}");
            return true;
        }

        internal string BuildScenarioLifecycleExceptionMessageForTesting(
            string lifecycle, string phase, Exception ex)
        {
            return BuildScenarioLifecycleExceptionMessage(lifecycle, phase, ex);
        }

        internal void ExecuteScenarioLifecyclePhaseForTesting(
            string lifecycle, string phase, Action body)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                LogScenarioLifecycleException(lifecycle, phase, ex);
                throw;
            }
        }

        internal void ExecuteOnSaveLifecyclePhaseForTesting(string phase, Action body)
        {
            ExecuteScenarioLifecyclePhaseForTesting("OnSave", phase, body);
        }

        private void LogScenarioLifecycleException(string lifecycle, string phase, Exception ex)
        {
            try
            {
                ParsekLog.Error("Scenario",
                    BuildScenarioLifecycleExceptionMessage(lifecycle, phase, ex));
            }
            catch (Exception logEx)
            {
                ParsekLog.Error("Scenario",
                    $"{(string.IsNullOrEmpty(lifecycle) ? "Scenario" : lifecycle)}: exception logging failed " +
                    $"phase={phase ?? "<none>"} original={FormatExceptionForLog(ex)} " +
                    $"logEx={FormatExceptionForLog(logEx)}");
            }

            try
            {
                ParsekLog.RecState(
                    $"{(string.IsNullOrEmpty(lifecycle) ? "Scenario" : lifecycle)}:exception",
                    CaptureScenarioRecorderState());
            }
            catch (Exception recStateEx)
            {
                ParsekLog.Warn("Scenario",
                    $"{(string.IsNullOrEmpty(lifecycle) ? "Scenario" : lifecycle)}: exception RecState capture failed " +
                    $"phase={phase ?? "<none>"} ex={FormatExceptionForLog(recStateEx)}");
            }
        }

        private string BuildScenarioLifecycleExceptionMessage(
            string lifecycle, string phase, Exception ex)
        {
            RecorderStateSnapshot snap = SafeCaptureScenarioRecorderState();
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}: exception phase={1} scene={2} saveFolder={3} scenarioSaveFolder={4} " +
                "committedRecordings={5} committedTrees={6} pendingTree={7} " +
                "activeMode={8} activeTree={9} activeRec={10} marker={11} journal={12} ex={13}",
                string.IsNullOrEmpty(lifecycle) ? "Scenario" : lifecycle,
                string.IsNullOrEmpty(phase) ? "<none>" : phase,
                SafeLoadedScene(),
                SafeSaveFolder(),
                string.IsNullOrEmpty(scenarioSaveFolder) ? "<null>" : scenarioSaveFolder,
                SafeCommittedRecordingCount(),
                SafeCommittedTreeCount(),
                DescribePendingTreeForLog(),
                snap.mode,
                string.IsNullOrEmpty(snap.treeId) ? "<none>" : snap.treeId,
                string.IsNullOrEmpty(snap.activeRecId) ? "<none>" : snap.activeRecId,
                DescribeMarkerForLog(ActiveReFlySessionMarker),
                DescribeJournalForLog(ActiveMergeJournal),
                FormatExceptionForLog(ex));
        }

        private static RecorderStateSnapshot SafeCaptureScenarioRecorderState()
        {
            try
            {
                return CaptureScenarioRecorderState();
            }
            catch
            {
                return default(RecorderStateSnapshot);
            }
        }

        private static string DescribePendingTreeForLog()
        {
            RecordingTree tree = RecordingStore.PendingTree;
            if (tree == null)
                return "none";

            int count = tree.Recordings != null ? tree.Recordings.Count : 0;
            return string.Format(
                CultureInfo.InvariantCulture,
                "id={0},state={1},recordings={2}",
                string.IsNullOrEmpty(tree.Id) ? "<no-id>" : tree.Id,
                RecordingStore.PendingTreeStateValue,
                count);
        }

        private static string DescribeMarkerForLog(ReFlySessionMarker marker)
        {
            if (marker == null)
                return "none";

            return string.Format(
                CultureInfo.InvariantCulture,
                "sess={0},tree={1},active={2},origin={3},rp={4}",
                string.IsNullOrEmpty(marker.SessionId) ? "<no-session>" : marker.SessionId,
                string.IsNullOrEmpty(marker.TreeId) ? "<no-tree>" : marker.TreeId,
                string.IsNullOrEmpty(marker.ActiveReFlyRecordingId) ? "<no-active>" : marker.ActiveReFlyRecordingId,
                string.IsNullOrEmpty(marker.OriginChildRecordingId) ? "<no-origin>" : marker.OriginChildRecordingId,
                string.IsNullOrEmpty(marker.RewindPointId) ? "<no-rp>" : marker.RewindPointId);
        }

        private static string DescribeJournalForLog(MergeJournal journal)
        {
            if (journal == null)
                return "none";

            return string.Format(
                CultureInfo.InvariantCulture,
                "journal={0},sess={1},tree={2},phase={3}",
                string.IsNullOrEmpty(journal.JournalId) ? "<no-journal>" : journal.JournalId,
                string.IsNullOrEmpty(journal.SessionId) ? "<no-session>" : journal.SessionId,
                string.IsNullOrEmpty(journal.TreeId) ? "<no-tree>" : journal.TreeId,
                string.IsNullOrEmpty(journal.Phase) ? "<no-phase>" : journal.Phase);
        }

        private static string FormatExceptionForLog(Exception ex)
        {
            if (ex == null)
                return "<none>";

            return ex.GetType().Name + ":" + (ex.Message ?? string.Empty);
        }

        private static int SafeCommittedRecordingCount()
        {
            try { return RecordingStore.CommittedRecordings?.Count ?? 0; }
            catch { return -1; }
        }

        private static int SafeCommittedTreeCount()
        {
            try { return RecordingStore.CommittedTrees?.Count ?? 0; }
            catch { return -1; }
        }

        private static string SafeLoadedScene()
        {
            try { return HighLogic.LoadedScene.ToString(); }
            catch (Exception ex) { return "<scene-error:" + ex.GetType().Name + ">"; }
        }

        private static string SafeSaveFolder()
        {
            try { return string.IsNullOrEmpty(HighLogic.SaveFolder) ? "<null>" : HighLogic.SaveFolder; }
            catch (Exception ex) { return "<save-error:" + ex.GetType().Name + ">"; }
        }

        internal static string FormatLoadTimingMessageForTesting(
            long elapsedMilliseconds, int recordingCount, string phase, string status)
        {
            return FormatLoadTimingMessage(elapsedMilliseconds, recordingCount, phase, status);
        }

        private static string FormatLoadTimingMessage(
            long elapsedMilliseconds, int recordingCount, string phase, string status)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "OnLoad: {0}ms ({1} recordings) phase={2} status={3}",
                elapsedMilliseconds,
                recordingCount,
                string.IsNullOrEmpty(phase) ? "<none>" : phase,
                string.IsNullOrEmpty(status) ? "<none>" : status);
        }

        /// <summary>
        /// Writes OnLoad timing to DiagnosticsState and logs a summary.
        /// Called exactly once from OnLoad's finally block so branch-specific
        /// returns cannot double-count or miss timing.
        /// </summary>
        private static void WriteLoadTiming(
            Stopwatch sw, int recordingCount, string phase, string status)
        {
            if (sw.IsRunning)
                sw.Stop();
            DiagnosticsState.lastLoadTiming = new SaveLoadTiming
            {
                totalMilliseconds = sw.ElapsedMilliseconds,
                recordingsProcessed = recordingCount,
                dirtyRecordingsWritten = 0
            };
            ParsekLog.Verbose("Diagnostics",
                FormatLoadTimingMessage(sw.ElapsedMilliseconds, recordingCount, phase, status));
        }

        /// <summary>
        /// Writes OnSave timing to DiagnosticsState and logs a summary.
        /// Called from the finally block of OnSave to ensure timing is captured
        /// even if an exception occurs during the save process.
        /// </summary>
        private static void WriteSaveTiming(Stopwatch sw, int recordingCount, int dirtyCount)
        {
            sw.Stop();
            DiagnosticsState.lastSaveTiming = new SaveLoadTiming
            {
                totalMilliseconds = sw.ElapsedMilliseconds,
                recordingsProcessed = recordingCount,
                dirtyRecordingsWritten = dirtyCount
            };
            ParsekLog.Verbose("Diagnostics",
                string.Format(CultureInfo.InvariantCulture,
                    "OnSave: {0}ms ({1} dirty of {2})",
                    sw.ElapsedMilliseconds, dirtyCount, recordingCount));
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
        /// Detects loading a different save game (not a revert) and resets session state
        /// if the save folder changed.
        /// </summary>
        private void DetectSaveFolderChange()
        {
            string currentSave = HighLogic.SaveFolder;
            scenarioSaveFolder = currentSave;
            if (currentSave != lastSaveFolder)
            {
                initialLoadDone = false;
                committedTreeStateLoaded = false;
                lastSaveFolder = currentSave;
                // Genuine new-session boundary: re-arm the drawdown-guard clamp toast so a
                // persistent leak in a DIFFERENT save toasts once again. A plain scene
                // change within the same save does NOT reset these latches (plan §9).
                KspStatePatcher.ResetDrawdownGuardSessionLatches();
                ScenarioLog($"[Parsek Scenario] Save folder changed to '{currentSave}' — resetting session state");
            }
        }

        /// <summary>
        /// Loads crew replacement mappings, kerbal slots, and group hierarchy from the save node.
        /// Skipped during rewind since in-memory state is the source of truth.
        /// </summary>
        private static void LoadCrewAndGroupState(ConfigNode node)
        {
            if (RewindContext.IsRewinding)
                return;

            CrewReservationManager.LoadCrewReplacements(node);
            LedgerOrchestrator.Initialize();
            var slotSummary = LedgerOrchestrator.Kerbals != null
                ? LedgerOrchestrator.Kerbals.LoadSlots(node)
                : default(KerbalSlotLoadSummary);
            KerbalLoadRepairDiagnostics.RecordSlotLoad(slotSummary);

            if (!ShouldLoadGroupHierarchyFromSave(initialLoadDone, RewindContext.IsRewinding))
            {
                ParsekLog.Verbose("Scenario",
                    "OnLoad: preserving in-memory group hierarchy for in-session load");
                return;
            }

            GroupHierarchyStore.LoadGroupHierarchy(node);
            GroupHierarchyStore.LoadHiddenGroups(node);
        }

        internal static bool ShouldLoadGroupHierarchyFromSave(bool initialLoadDone, bool isRewinding)
        {
            return !isRewinding && !initialLoadDone;
        }

        /// <summary>
        /// Loads external data files (game state events, baselines, milestones, ledger)
        /// and cleans up stale rewind temp files. Only called on initial load (not
        /// revert/scene change).
        /// </summary>
        private static void LoadExternalFiles()
        {
            ParsekLog.Verbose("Scenario", "OnLoad: initial load — loading external files");
            GameStateStore.LoadEventFile();
            GameStateStore.LoadBaselines();
            MilestoneStore.LoadMilestoneFile();

            // Load ledger from external file (skip during rewind — in-memory ledger is source of truth)
            if (!RewindContext.IsRewinding)
                LedgerOrchestrator.OnLoad();

            // Clean up stale parsek_rw_*.sfs temp files left by a crash during rewind
            try
            {
                string savesDir = System.IO.Path.Combine(
                    KSPUtil.ApplicationRootPath ?? "", "saves", HighLogic.SaveFolder ?? "");
                if (System.IO.Directory.Exists(savesDir))
                {
                    string[] staleFiles = System.IO.Directory.GetFiles(savesDir, "parsek_rw_*.sfs");
                    for (int s = 0; s < staleFiles.Length; s++)
                    {
                        try
                        {
                            System.IO.File.Delete(staleFiles[s]);
                            ScenarioLog($"[Parsek Scenario] Deleted stale rewind temp file: {System.IO.Path.GetFileName(staleFiles[s])}");
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                ScenarioLog($"[Parsek Scenario] Failed to scan for stale rewind temp files: {ex.Message}");
            }

            budgetDeductionApplied = false;
        }

        /// <summary>
        /// Subscribes to vessel lifecycle events (recovery, termination, switching).
        /// Removes existing subscriptions first to avoid duplicates on revert/scene change.
        /// </summary>
        private void SubscribeVesselLifecycleEvents()
        {
            GameEvents.onVesselRecoveryProcessing.Remove(OnVesselRecoveryProcessing);
            GameEvents.onVesselRecoveryProcessing.Add(OnVesselRecoveryProcessing);
            GameEvents.onVesselRecovered.Remove(OnVesselRecovered);
            GameEvents.onVesselRecovered.Add(OnVesselRecovered);
            GameEvents.onVesselTerminated.Remove(OnVesselTerminated);
            GameEvents.onVesselTerminated.Add(OnVesselTerminated);
            GameEvents.onVesselSwitching.Remove(OnVesselSwitching);
            GameEvents.onVesselSwitching.Add(OnVesselSwitching);
            // M4b Phase B2 (plan D11 / OQ7): clear the RAM-only cargo escrow on any
            // within-game scene change. The in-cycle reservation is a within-tick /
            // dispatch-to-window-phase guard recomputed from pending route state on
            // the next RouteOrchestrator.Tick, so it need not survive a scene change;
            // dropping it here avoids a stale craft-baked-pid reservation mis-gating
            // a competing route after the scene reloads. Idempotent (Remove then Add).
            GameEvents.onGameSceneSwitchRequested.Remove(OnGameSceneSwitchClearEscrow);
            GameEvents.onGameSceneSwitchRequested.Add(OnGameSceneSwitchClearEscrow);
            // #434: subscribe idempotently so revert detection is armed from first OnLoad.
            RevertDetector.Subscribe();
            // Re-fly Esc-menu button gate: Apply() on every onFlightReady so a
            // save loaded with an active re-fly marker re-enables the Revert
            // button before the player can click it.
            ReFlyRevertButtonGate.Subscribe();
        }

        /// <summary>
        /// Captures an initial game state baseline if none exist yet and this is the initial load.
        /// </summary>
        private static void CaptureInitialBaseline()
        {
            if (!initialLoadDone && GameStateStore.BaselineCount == 0)
            {
                try
                {
                    GameStateStore.CaptureBaselineIfNeeded();
                }
                catch (System.Exception ex)
                {
                    ScenarioLog($"[Parsek Scenario] Failed to capture initial baseline: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// In-game test helper: clears save-scoped in-memory state before an isolated
        /// FLIGHT baseline quickload restore so the next OnLoad uses the cold-load
        /// path and rebuilds Parsek state from the restored save + sidecars.
        /// </summary>
        internal void UnsubscribeStateRecorderForIsolatedBatchFlightBaselineRestore()
        {
            stateRecorder?.Unsubscribe();
        }

        internal static void PrepareForIsolatedBatchFlightBaselineRestore(
            Action unsubscribeLiveRecorder = null,
            Action onWipeStart = null)
        {
            ParsekLog.Info("Scenario",
                "Preparing save-scoped state for isolated FLIGHT batch baseline restore");

            unsubscribeLiveRecorder?.Invoke();
            initialLoadDone = false;
            committedTreeStateLoaded = false;
            budgetDeductionApplied = false;
            mergeDialogPending = false;
            pendingActiveTreeResumeRewindSave = null;
            ScheduleActiveTreeRestoreOnFlightReady = ActiveTreeRestoreMode.None;
            vesselSwitchPending = false;
            vesselSwitchPendingFrame = -1;

            // onWipeStart fires IMMEDIATELY before the first in-memory
            // store reset. This is the destructive boundary -- callers
            // wire this to arm their rollback flag here, not after this
            // method returns, because the eight Reset calls below are not
            // atomic. If GroupHierarchyStore.ResetForTesting (or any
            // later reset) throws, RecordingStore has already been wiped
            // and the caller's rollback must fire to recover live data.
            // Arming after this method returned would leave the rollback
            // disabled for that partial-failure window.
            onWipeStart?.Invoke();

            // The next operation after this prep is a quickload from the
            // baseline save slot, which restores RecordingStore from disk
            // via OnLoad. The in-memory wipe is transient. Use the
            // explicit guard-bypassing variant -- ResetForTesting() throws
            // when committedRecordings/Trees are non-empty (the
            // PersistenceSplitOptimizerTest 2026-05-01 bug guard), and any
            // batch FLIGHT test running on a save with live recordings hits
            // that throw on the prep step before its body runs.
            RecordingStore.ResetForBatchFlightBaselineRestoreBypassingGuard();
            GroupHierarchyStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RevertDetector.ResetForTesting();
        }

        /// <summary>
        /// Clears pending trees and stale rewind flags that may have leaked from a
        /// previous save. Static fields survive scene changes, so without this cleanup,
        /// pending state from save A would be auto-committed into save B's timeline.
        /// </summary>
        private void DiscardStalePendingState()
        {
            if (RecordingStore.HasPendingTree)
            {
                ParsekLog.Warn("Scenario",
                    "OnLoad initial: discarding pending tree from previous save");
                DiscardPendingTreeAndAbandonDeferredFlightResults(
                    "stale pending tree discarded on initial load");
            }
            if (RewindContext.IsRewinding)
            {
                ParsekLog.Warn("Scenario",
                    "OnLoad initial: clearing stale rewind flags from previous save");
                RewindContext.EndRewind();
                RecordingStore.RewindUTAdjustmentPending = false;
            }
            // Defensive: clear stale UT adjustment flag even outside the rewind block.
            // The flag may have been left set if the coroutine didn't complete (crash/exit).
            if (RecordingStore.RewindUTAdjustmentPending)
            {
                ParsekLog.Warn("Scenario",
                    "OnLoad initial: clearing stale RewindUTAdjustmentPending flag");
                RecordingStore.RewindUTAdjustmentPending = false;
            }
        }

        /// <summary>
        /// Loads committed recording trees from RECORDING_TREE ConfigNodes. Clears stale
        /// trees, loads each tree and its bulk data, and adds tree recordings to the
        /// committed recordings list for ghost playback.
        /// </summary>
        internal static void LoadRecordingTrees(ConfigNode node, IReadOnlyList<Recording> recordings)
        {
            // Always clear CommittedTrees — if the new save has no trees, stale trees
            // from the previous save would otherwise persist and contaminate this save.
            ConfigNode[] treeNodes = node.GetNodes("RECORDING_TREE");
            var committedTrees = RecordingStore.CommittedTrees;
            committedTrees.Clear();
            ParsekLog.Info("Scenario",
                $"OnLoad initial: cleared CommittedTrees, loading {treeNodes.Length} tree(s)");

            if (treeNodes.Length > 0)
            {
                int treeRecCount = 0;
                int treeTotalPoints = 0;
                int treeTotalOrbitSegments = 0;
                int treeTotalPartEvents = 0;
                int treeWithTrackSections = 0;
                int treeWithSnapshots = 0;
                int activeTreeCount = 0;
                int pendingTreeCount = 0;
                for (int t = 0; t < treeNodes.Length; t++)
                {
                    var treeNode = treeNodes[t];

                    // Active-tree marker: these go into the pending-limbo slot, NOT into
                    // committedTrees. TryRestoreActiveTreeNode earlier in OnLoad may have
                    // already consumed this node, so skip it here either way.
                    if (IsActiveTreeNode(treeNode))
                    {
                        activeTreeCount++;
                        continue;
                    }
                    if (IsPendingTreeNode(treeNode))
                    {
                        pendingTreeCount++;
                        continue;
                    }

                    var tree = RecordingTree.Load(treeNode);
                    if (tree.Recordings == null || tree.Recordings.Count == 0)
                    {
                        ParsekLog.Warn("Scenario",
                            $"LoadRecordingTrees: skipped tree '{tree.TreeName}' id={tree.Id} " +
                            "because all recordings were rejected by the schema gate");
                        continue;
                    }
                    int sidecarHydrationFailures = 0;
                    int syntheticFixtureFailures = 0;

                    // Load bulk data from external files for each recording in the tree.
                    foreach (var rec in tree.Recordings.Values)
                    {
                        if (!RecordingStore.LoadRecordingFiles(rec))
                        {
                            sidecarHydrationFailures++;
                            // Bug #422: distinguish synthetic-fixture markers (no .prec sidecar,
                            // zero trajectory points — expected for injected test recordings)
                            // from genuine degradations (parse error, id mismatch, stale epoch,
                            // snapshot failure, etc). The per-recording INFO line already
                            // describes the synthetic case; the roll-up WARN would duplicate it.
                            if (IsSyntheticFixtureSidecarMarker(rec))
                                syntheticFixtureFailures++;
                        }
                    }

                    int droppedHydrationFailures = DropFailedSidecarHydrationRecordings(
                        tree,
                        "LoadRecordingTrees");
                    if (tree.Recordings == null || tree.Recordings.Count == 0)
                    {
                        ParsekLog.Warn("Scenario",
                            $"LoadRecordingTrees: skipped tree '{tree.TreeName}' id={tree.Id} " +
                            $"after dropping {droppedHydrationFailures} recording(s) with incompatible sidecars");
                        continue;
                    }

                    if (RepairMissingContiguousChainPredecessors(tree, "LoadRecordingTrees") > 0)
                        tree.RebuildBackgroundMap();

                    // Phase 8 review-pass-3: deferred co-bubble sweeps
                    // moved out of the per-tree loop. Cross-tree co-bubble
                    // traces are possible because commit-time DetectAndStore
                    // scans CommittedRecordings (which spans trees), so a
                    // deferred entry in tree T1 may reference a peer in
                    // tree T2. Per-tree sweeps fired before T2 was visible
                    // and silently dropped cross-tree recomputes. The
                    // global sweep runs ONCE after the loop with the full
                    // union of just-hydrated committed recordings — handles
                    // intra-tree AND cross-tree correctly.
                    committedTrees.Add(tree);

                    // Add tree recordings to CommittedRecordings for ghost playback
                    foreach (var rec in tree.Recordings.Values)
                    {
                        RecordingStore.AddCommittedInternal(rec);
                        treeRecCount++;
                        treeTotalPoints += rec.Points.Count;
                        treeTotalOrbitSegments += rec.OrbitSegments.Count;
                        treeTotalPartEvents += rec.PartEvents.Count;
                        if (rec.TrackSections != null && rec.TrackSections.Count > 0) treeWithTrackSections++;
                        if (rec.VesselSnapshot != null) treeWithSnapshots++;
                    }

                    ScenarioLog($"[Parsek Scenario] Loaded tree '{tree.TreeName}': " +
                        $"{tree.Recordings.Count} recordings, {tree.BranchPoints.Count} branch points");
                    EmitSidecarHydrationRollup(
                        tree.TreeName, sidecarHydrationFailures, syntheticFixtureFailures);
                }

                ParsekLog.Verbose("Scenario",
                    $"Loaded {treeNodes.Length} tree nodes ({treeRecCount} committed recordings + " +
                    $"{activeTreeCount} active-tree marker(s) + {pendingTreeCount} pending-tree marker(s)): " +
                    $"{treeTotalPoints} points, " +
                    $"{treeTotalOrbitSegments} orbit segments, {treeTotalPartEvents} part events, " +
                    $"{treeWithTrackSections} with track sections, {treeWithSnapshots} with snapshots");
            }
        }

        /// <summary>
        /// Bug #422: a sidecar-hydration failure is a "synthetic-fixture marker" — not a
        /// genuine degradation — when the <c>.prec</c> trajectory file is simply missing
        /// and the recording carries zero trajectory points. That combination is the
        /// expected shape of an injected test recording whose <c>.prec</c> was never
        /// written. All other failure reasons (parse errors, id mismatches, stale
        /// epochs, snapshot failures, exceptions) indicate real data problems.
        /// </summary>
        internal static bool IsSyntheticFixtureSidecarMarker(Recording rec)
        {
            if (rec == null) return false;
            if (rec.SidecarLoadFailureReason != "trajectory-missing") return false;
            return rec.Points == null || rec.Points.Count == 0;
        }

        internal static int DropFailedSidecarHydrationRecordings(RecordingTree tree, string context)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return 0;

            HashSet<string> rejectedIds = null;
            var examples = new List<string>();
            foreach (KeyValuePair<string, Recording> kvp in tree.Recordings)
            {
                Recording rec = kvp.Value;
                if (rec == null || !rec.SidecarLoadFailed)
                    continue;
                if (IsSyntheticFixtureSidecarMarker(rec))
                    continue;

                if (rejectedIds == null)
                    rejectedIds = new HashSet<string>(StringComparer.Ordinal);

                if (!string.IsNullOrEmpty(kvp.Key))
                    rejectedIds.Add(kvp.Key);
                if (!string.IsNullOrEmpty(rec.RecordingId))
                    rejectedIds.Add(rec.RecordingId);

                if (examples.Count < 3)
                {
                    examples.Add(
                        $"{rec.RecordingId ?? kvp.Key ?? "<no-id>"}:{rec.SidecarLoadFailureReason ?? "<unknown>"}");
                }
            }

            if (rejectedIds == null || rejectedIds.Count == 0)
                return 0;

            if (!string.IsNullOrEmpty(tree.RootRecordingId)
                && rejectedIds.Contains(tree.RootRecordingId))
            {
                int removedRecordings = tree.Recordings.Count;
                int rootRemovedBranchPoints = tree.BranchPoints != null ? tree.BranchPoints.Count : 0;
                string rootRecordingId = tree.RootRecordingId;
                tree.RootRecordingId = string.Empty;
                tree.ActiveRecordingId = null;
                tree.Recordings.Clear();
                if (tree.BranchPoints != null)
                    tree.BranchPoints.Clear();
                if (tree.BackgroundMap != null)
                    tree.BackgroundMap.Clear();
                ParsekLog.Warn("Scenario",
                    $"{context}: dropped entire tree '{tree.TreeName}' id={tree.Id ?? "<none>"} " +
                    $"because root recording '{rootRecordingId}' had an incompatible sidecar; " +
                    $"removedRecordings={removedRecordings} removedBranchPoints={rootRemovedBranchPoints} " +
                    $"examples=[{string.Join(", ", examples.ToArray())}]");
                return removedRecordings;
            }

            if (!string.IsNullOrEmpty(tree.ActiveRecordingId)
                && rejectedIds.Contains(tree.ActiveRecordingId))
            {
                tree.ActiveRecordingId = null;
            }

            int removed = PruneFutureOnlyRecordings(tree, rejectedIds);
            int removedBranchPoints = RemoveEmptyBranchPoints(tree);
            foreach (Recording rec in tree.Recordings.Values)
            {
                if (rec == null)
                    continue;
                if (!string.IsNullOrEmpty(rec.ParentAnchorRecordingId)
                    && rejectedIds.Contains(rec.ParentAnchorRecordingId))
                {
                    rec.ParentAnchorRecordingId = null;
                }
            }

            tree.RebuildBackgroundMap();
            ParsekLog.Warn("Scenario",
                $"{context}: dropped {removed} recording(s) with incompatible sidecars " +
                $"from tree '{tree.TreeName}' id={tree.Id ?? "<none>"} " +
                $"removedBranchPoints={removedBranchPoints} examples=[{string.Join(", ", examples.ToArray())}]");
            return removed;
        }

        /// <summary>
        /// Bug #422: emits the per-tree sidecar-hydration roll-up. Suppresses the WARN
        /// (downgrades to INFO) when every failure in the batch is the synthetic-fixture
        /// marker — the per-recording INFO lines above already describe those cases and
        /// the WARN would duplicate them, making a clean test save look unhealthy. Any
        /// genuine degradation in the batch still emits the WARN. Internal so unit tests
        /// can exercise both branches without building a full RecordingTree fixture.
        /// </summary>
        internal static void EmitSidecarHydrationRollup(
            string treeName,
            int totalFailures,
            int syntheticFixtureFailures,
            string treeKind = "committed tree")
        {
            if (totalFailures <= 0) return;

            if (syntheticFixtureFailures >= totalFailures)
            {
                ParsekLog.Info("Scenario",
                    $"OnLoad: {treeKind} '{treeName}' had {totalFailures} " +
                    $"synthetic-fixture recording(s) with missing .prec sidecar " +
                    $"(no genuine degradations; per-recording INFO lines above describe each)");
                return;
            }

            int genuineFailures = totalFailures - syntheticFixtureFailures;
            ParsekLog.Warn("Scenario",
                $"OnLoad: {treeKind} '{treeName}' had {totalFailures} " +
                $"recording(s) with sidecar hydration failures " +
                $"({genuineFailures} genuine, {syntheticFixtureFailures} synthetic-fixture)");
        }

        /// <summary>
        /// Returns true if a RECORDING_TREE ConfigNode represents an active (in-flight)
        /// tree written by <c>SaveActiveTreeIfAny</c>, distinguished by the
        /// <c>isActive=True</c> key.
        /// </summary>
        internal static bool IsActiveTreeNode(ConfigNode treeNode)
        {
            if (treeNode == null) return false;
            string val = treeNode.GetValue("isActive");
            return !string.IsNullOrEmpty(val)
                && bool.TryParse(val, out bool isActive) && isActive;
        }

        /// <summary>
        /// Returns true if a RECORDING_TREE ConfigNode represents a finalized pending
        /// tree written by <c>SavePendingTreeIfAny</c>, distinguished by
        /// <c>isPending=True</c>.
        /// </summary>
        internal static bool IsPendingTreeNode(ConfigNode treeNode)
        {
            if (treeNode == null) return false;
            string val = treeNode.GetValue("isPending");
            return !string.IsNullOrEmpty(val)
                && bool.TryParse(val, out bool isPending) && isPending;
        }

        internal static bool TryRestorePendingTreeNode(
            ConfigNode node,
            bool activeTreeRestoredFromSave = false)
        {
            if (node == null) return false;
            if (RewindContext.IsRewinding)
            {
                ParsekLog.Verbose("Scenario",
                    "TryRestorePendingTreeNode: skipped (rewind in progress)");
                return false;
            }

            ConfigNode[] treeNodes = node.GetNodes("RECORDING_TREE");
            int pendingMarkerCount = 0;
            int activeMarkerCount = 0;
            ConfigNode pendingNode = null;
            for (int t = 0; t < treeNodes.Length; t++)
            {
                if (IsActiveTreeNode(treeNodes[t]))
                    activeMarkerCount++;
                if (IsPendingTreeNode(treeNodes[t]))
                {
                    pendingMarkerCount++;
                    if (pendingNode == null)
                        pendingNode = treeNodes[t];
                }
            }

            if (pendingMarkerCount == 0)
                return false;

            bool preserveDuringActiveRestore = false;
            if (activeMarkerCount > 0)
            {
                preserveDuringActiveRestore = activeTreeRestoredFromSave;
                ParsekLog.Warn("Scenario",
                    $"TryRestorePendingTreeNode: found {pendingMarkerCount} pending marker(s) " +
                    $"alongside {activeMarkerCount} active marker(s); " +
                    (preserveDuringActiveRestore
                        ? "preserving saved pending tree separately while active tree restore keeps priority"
                        : "active restore did not run, restoring pending tree normally") +
                    $" (activeRestored={activeTreeRestoredFromSave})");
            }

            if (pendingMarkerCount > 1)
            {
                ParsekLog.Warn("Scenario",
                    $"TryRestorePendingTreeNode: found {pendingMarkerCount} pending marker(s); " +
                    "restoring the first and ignoring the rest");
            }

            var tree = RecordingTree.Load(pendingNode);
            if (tree.Recordings == null || tree.Recordings.Count == 0)
            {
                ParsekLog.Warn("Scenario",
                    $"TryRestorePendingTreeNode: skipped pending tree '{tree.TreeName}' id={tree.Id} " +
                    "because all recordings were rejected by the schema gate");
                return false;
            }
            int sidecarHydrationFailures = 0;
            int syntheticFixtureFailures = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                if (!RecordingStore.LoadRecordingFiles(rec))
                {
                    sidecarHydrationFailures++;
                    if (IsSyntheticFixtureSidecarMarker(rec))
                        syntheticFixtureFailures++;
                }
            }

            int droppedHydrationFailures = DropFailedSidecarHydrationRecordings(
                tree,
                "TryRestorePendingTreeNode");
            if (tree.Recordings == null || tree.Recordings.Count == 0)
            {
                ParsekLog.Warn("Scenario",
                    $"TryRestorePendingTreeNode: skipped pending tree '{tree.TreeName}' id={tree.Id} " +
                    $"after dropping {droppedHydrationFailures} recording(s) with incompatible sidecars");
                return false;
            }

            if (RepairMissingContiguousChainPredecessors(tree, "TryRestorePendingTreeNode") > 0)
                tree.RebuildBackgroundMap();

            if (preserveDuringActiveRestore)
                RecordingStore.PreservePendingTreeFromSaveDuringActiveRestore(tree);
            else
                RecordingStore.RestorePendingTreeFromSave(tree);
            EmitSidecarHydrationRollup(
                tree.TreeName, sidecarHydrationFailures, syntheticFixtureFailures, "pending tree");

            ParsekLog.Info("Scenario",
                $"TryRestorePendingTreeNode: " +
                (preserveDuringActiveRestore ? "preserved" : "restored") +
                $" pending tree '{tree.TreeName}' " +
                $"({tree.Recordings.Count} recording(s), sidecarFailures={sidecarHydrationFailures}, " +
                $"stashedThisTransition={RecordingStore.PendingStashedThisTransition})");
            ParsekLog.RecState("TryRestorePendingTreeNode:restored", CaptureScenarioRecorderState());
            return true;
        }

        /// <summary>
        /// Attempts to restore an active (in-flight) recording tree from the save node
        /// into the pending-limbo slot. Called from OnLoad immediately after settings
        /// persistence and before revert detection, so the revert-detection logic can
        /// decide whether to restore-and-resume (quickload) or finalize-and-commit
        /// (real revert).
        ///
        /// <para>Skipped when rewinding — <see cref="RewindContext.IsRewinding"/> means
        /// the load is Parsek's own rewind flow which explicitly resets playback state.</para>
        ///
        /// Returns true if an active tree was found and stashed as Limbo.
        /// </summary>
        internal static bool TryRestoreActiveTreeNode(ConfigNode node)
        {
            if (node == null) return false;
            if (RewindContext.IsRewinding)
            {
                pendingActiveTreeResumeRewindSave = null;
                if (RecordingStore.TryConsumeNextActiveTreeRestoreSuppression(
                    "TryRestoreActiveTreeNode:rewind-in-progress",
                    out string rewindSuppressReason))
                {
                    ParsekLog.Warn("Scenario",
                        "TryRestoreActiveTreeNode: consumed active-tree restore " +
                        $"suppression while rewind was already skipping restore reason='{rewindSuppressReason}'");
                }
                ParsekLog.Verbose("Scenario",
                    "TryRestoreActiveTreeNode: skipped (rewind in progress — active tree deliberately discarded)");
                return false;
            }

            ConfigNode[] treeNodes = node.GetNodes("RECORDING_TREE");
            for (int t = 0; t < treeNodes.Length; t++)
            {
                if (!IsActiveTreeNode(treeNodes[t])) continue;

                var tree = RecordingTree.Load(treeNodes[t]);
                if (tree.Recordings == null || tree.Recordings.Count == 0)
                {
                    pendingActiveTreeResumeRewindSave = null;
                    ClearPendingQuickloadResumeContext();
                    ParsekLog.Warn("Scenario",
                        $"TryRestoreActiveTreeNode: skipped active tree '{tree.TreeName}' id={tree.Id} " +
                        "because all recordings were rejected by the schema gate");
                    return false;
                }
                if (RecordingStore.TryConsumeNextActiveTreeRestoreSuppression(
                    "TryRestoreActiveTreeNode:active-tree",
                    out string suppressReason))
                {
                    pendingActiveTreeResumeRewindSave = null;
                    ParsekLog.Info("Scenario",
                        "TryRestoreActiveTreeNode: suppressed saved active tree restore " +
                        $"tree='{tree.TreeName}' id={tree.Id} recordings={tree.Recordings.Count} " +
                        $"activeRecId={tree.ActiveRecordingId ?? "<null>"} reason='{suppressReason}' - " +
                        "leaving committed mission tree intact and not stashing pending-Limbo");
                    return false;
                }

                int sidecarHydrationFailures = 0;
                int staleEpochHydrationFailures = 0;
                // Hydrate bulk data from sidecar files for each recording.
                foreach (var rec in tree.Recordings.Values)
                {
                    if (!RecordingStore.LoadRecordingFiles(rec))
                    {
                        sidecarHydrationFailures++;
                        if (rec.SidecarLoadFailureReason == "stale-sidecar-epoch")
                            staleEpochHydrationFailures++;
                    }
                }

                if (ShouldKeepPendingTreeAfterHydrationFailure(tree, staleEpochHydrationFailures))
                {
                    ParsekLog.Warn("Scenario",
                        $"TryRestoreActiveTreeNode: keeping in-memory pending tree " +
                        $"'{RecordingStore.PendingTree.TreeName}' because saved active tree " +
                        $"'{tree.TreeName}' had {staleEpochHydrationFailures} stale-sidecar epoch failure(s)");
                    return true;
                }

                int salvagedHydrationFailures = RestoreHydrationFailedRecordingsFromPendingTree(tree);
                int droppedHydrationFailures = DropFailedSidecarHydrationRecordings(
                    tree,
                    "TryRestoreActiveTreeNode");
                if (tree.Recordings == null || tree.Recordings.Count == 0)
                {
                    pendingActiveTreeResumeRewindSave = null;
                    ClearPendingQuickloadResumeContext();
                    ParsekLog.Warn("Scenario",
                        $"TryRestoreActiveTreeNode: skipped active tree '{tree.TreeName}' id={tree.Id} " +
                        $"after dropping {droppedHydrationFailures} recording(s) with incompatible sidecars");
                    return false;
                }

                // Bug #601: Re-Fly load preserves post-RP merge tree mutations.
                // The RP's frozen .sfs snapshots the tree state AT RP creation time.
                // If a merge / SplitAtSection ran between RP creation and Re-Fly invocation,
                // the in-memory committed tree has post-split recording IDs (and updated
                // BranchPoint parent refs) that the loaded .sfs does NOT know about. The
                // .prec sidecars for the post-split halves still exist on disk, but they're
                // orphaned because the loaded tree never lists them. Splice those recordings
                // (and any committed-tree-only BranchPoints / parent-id updates) back into
                // the loaded tree BEFORE RemoveCommittedTreeById destroys the in-memory copy.
                // The spliced recordings are deep-cloned and marked dirty so the next OnSave
                // rewrites the .sfs + .prec with fresh sidecar epochs and the live shape.
                // The active recording id is forwarded so the same-ID refresh path can run
                // in recorder-state-preserving mode for the active recording — at this point
                // the recorder has not yet rebound (rebind fires on the deferred onFlightReady
                // pass after this scene-load OnLoad), so there is no in-flight payload state
                // to lose, but a small set of [NonSerialized] mitigation flags
                // (FilesDirty / SidecarLoadFailed / SidecarLoadFailureReason /
                // ContinuationBoundaryIndex / Pre-Continuation snapshots /
                // Pre-ReFly anchor trajectory) may already be set
                // by earlier load-time code paths and the refresh must not wipe them. The
                // structural fields (trajectory, orbit segments, track sections, terminal
                // state, ChildBranchPointId) ARE refreshed for the active recording — that is
                // the whole point of the P1 fix, since the active id IS most likely the stale
                // post-split first half from the RP's frozen .sfs.
                int splicedFromCommitted = SpliceMissingCommittedRecordingsIntoLoadedTree(
                    tree, tree.ActiveRecordingId);
                if (RepairMissingContiguousChainPredecessors(tree, "TryRestoreActiveTreeNode") > 0)
                    tree.RebuildBackgroundMap();

                // If the same tree id is already in committedTrees (e.g. the player
                // quicksaved in flight, then exited to TS which committed the tree, then
                // quickloaded — the in-memory committedTrees retains the T3 version even
                // though the save file has the T2 active version), remove the committed
                // copy so the active version is the single source of truth. Otherwise
                // the next OnSave would write the tree twice with the same id.
                if (!RecordingStore.RemoveCommittedTreeById(
                        tree.Id,
                        logContext: "TryRestoreActiveTreeNode"))
                {
                    ParsekLog.Verbose("Scenario",
                        $"TryRestoreActiveTreeNode: no committed copy of tree '{tree.TreeName}' " +
                        $"(id={tree.Id}) needed detaching");
                }

                // Bug #290d: if the pending tree is already Finalized (set by
                // CommitTreeSceneExit during the same scene transition), it has
                // post-finalize data (MaxDistanceFromLaunch, terminal states, snapshots)
                // that the .sfs version does NOT have (OnSave runs BEFORE finalization).
                // Skip the replacement — the in-memory Finalized tree is authoritative.
                if (RecordingStore.HasPendingTree
                    && RecordingStore.PendingTreeStateValue == PendingTreeState.Finalized)
                {
                    ParsekLog.Info("Scenario",
                        $"TryRestoreActiveTreeNode: keeping in-memory Finalized tree " +
                        $"'{RecordingStore.PendingTree.TreeName}' — skipping .sfs replacement " +
                        $"(OnSave ran before finalization, .sfs maxDist is stale)");
                    return true;
                }

                // Pop any existing pending tree silently — StashActiveTreeAsPendingLimbo
                // stashed the in-memory (future-timeline) version at OnSceneChangeRequested
                // time; we want the freshly-loaded disk version (matches the quicksave UT
                // the user is rewinding to). PopPendingTree is non-destructive (doesn't
                // delete sidecar files), and avoids the "overwriting existing pending tree"
                // warning on the expected-overwrite quickload path.
                RecordingStore.PopPendingTree();

                // Bug #266: pick the stash state based on whether the saved tree had a
                // live active recording. If ActiveRecordingId is null, the tree was in
                // outsider state at OnSave time (the player switched to a vessel with
                // no recording context). The vessel-switch restore coroutine handles
                // this case; the quickload restore would bail on the null active rec.
                var stashState = string.IsNullOrEmpty(tree.ActiveRecordingId)
                    ? PendingTreeState.LimboVesselSwitch
                    : PendingTreeState.Limbo;
                RecordingStore.StashPendingTree(tree, stashState);

                // Read resume hints for the restore coroutine (rewind save filename only;
                // BoundaryAnchor can't round-trip because we only have the UT, not the
                // full TrajectoryPoint state — restore leaves it unset).
                pendingActiveTreeResumeRewindSave = treeNodes[t].GetValue("resumeRewindSave");

                ParsekLog.Info("Scenario",
                    $"TryRestoreActiveTreeNode: stashed active tree '{tree.TreeName}' " +
                    $"({tree.Recordings.Count} recording(s), activeRecId={tree.ActiveRecordingId ?? "<null>"}) " +
                    $"into pending-{stashState} slot for revert-detection dispatch" +
                    (sidecarHydrationFailures > 0
                        ? $" with {sidecarHydrationFailures} sidecar hydration failure(s)" +
                          (salvagedHydrationFailures > 0
                              ? $", salvaged {salvagedHydrationFailures} from pending"
                              : "")
                        : "") +
                    (splicedFromCommitted > 0
                        ? $", spliced {splicedFromCommitted} post-RP recording(s) from committed tree"
                        : ""));
                ParsekLog.RecState("TryRestoreActiveTreeNode:stashed", CaptureScenarioRecorderState());
                return true;
            }
            if (RecordingStore.TryConsumeNextActiveTreeRestoreSuppression(
                "TryRestoreActiveTreeNode:no-active-tree",
                out string noActiveSuppressReason))
            {
                ParsekLog.Warn("Scenario",
                    "TryRestoreActiveTreeNode: consumed active-tree restore " +
                    $"suppression but no active tree node was present reason='{noActiveSuppressReason}'");
            }
            return false;
        }

        private sealed class QuickloadResumeContext
        {
            internal string TreeId;
            internal QuickloadTrimScope TrimScope;
            internal string TrimScopeReason;
        }

        // Resume hints parsed from PARSEK_ACTIVE_TREE, consumed by the quickload-resume
        // path when FlightRecorder.StartRecording reopens the restored active tree.
        internal static string pendingActiveTreeResumeRewindSave;
        private static QuickloadResumeContext pendingQuickloadResumeContext;

        internal static void ConfigurePendingQuickloadResumeContext(RecordingTree tree)
        {
            if (tree == null || string.IsNullOrEmpty(tree.Id) || string.IsNullOrEmpty(tree.ActiveRecordingId))
            {
                ClearPendingQuickloadResumeContext();
                return;
            }

            var marker = Instance?.ActiveReFlySessionMarker;
            var trimScope = ChooseQuickloadTrimScope(tree.Id, marker, out string trimScopeReason);

            pendingQuickloadResumeContext = new QuickloadResumeContext
            {
                TreeId = tree.Id,
                TrimScope = trimScope,
                TrimScopeReason = trimScopeReason,
            };

            ParsekLog.Verbose("Scenario",
                $"Quickload-resume context armed: treeId={tree.Id} activeRecId={tree.ActiveRecordingId} " +
                $"trimScope={trimScope} ({trimScopeReason})");
        }

        internal static void RefreshPendingQuickloadTrimScope()
        {
            QuickloadResumeContext context = pendingQuickloadResumeContext;
            if (context == null)
                return;

            var marker = Instance?.ActiveReFlySessionMarker;
            var trimScope = ChooseQuickloadTrimScope(context.TreeId, marker, out string trimScopeReason);
            context.TrimScope = trimScope;
            context.TrimScopeReason = trimScopeReason;

            ParsekLog.Verbose("Scenario",
                $"Quickload-resume trim scope refreshed: treeId={context.TreeId} " +
                $"trimScope={trimScope} ({trimScopeReason})");
        }

        internal static bool MatchesPendingQuickloadResumeContext(string treeId)
        {
            return pendingQuickloadResumeContext != null
                && string.Equals(pendingQuickloadResumeContext.TreeId, treeId, StringComparison.Ordinal);
        }

        internal static QuickloadTrimScope GetPendingQuickloadTrimScope(
            string treeId,
            out string reason)
        {
            if (pendingQuickloadResumeContext == null)
            {
                reason = "no-pending-quickload-resume-context";
                return QuickloadTrimScope.TreeWide;
            }

            if (string.IsNullOrEmpty(treeId)
                || !string.Equals(pendingQuickloadResumeContext.TreeId, treeId, StringComparison.Ordinal))
            {
                reason = $"pending-context-tree-mismatch contextTree={pendingQuickloadResumeContext.TreeId ?? "<null>"} resumeTree={treeId ?? "<null>"}";
                return QuickloadTrimScope.TreeWide;
            }

            reason = pendingQuickloadResumeContext.TrimScopeReason ?? "pending-context-no-reason";
            return pendingQuickloadResumeContext.TrimScope;
        }

        internal static void ClearPendingQuickloadResumeContext()
        {
            pendingQuickloadResumeContext = null;
        }

        /// <summary>
        /// Restore path that <see cref="ParsekFlight.OnFlightReady"/> should run on
        /// the next flight load.
        /// </summary>
        internal enum ActiveTreeRestoreMode
        {
            /// <summary>No restore scheduled.</summary>
            None = 0,
            /// <summary>
            /// Quickload-resume path: name-match the active vessel against the tree's
            /// active recording and resume the in-flight recorder. Used after F9 / cold
            /// start with a saved <c>PARSEK_ACTIVE_TREE</c> node.
            /// </summary>
            Quickload = 1,
            /// <summary>
            /// Vessel-switch restore path (#266): the tree was pre-transitioned at stash
            /// time, so the restore coroutine just reinstalls it as <c>activeTree</c>
            /// and optionally promotes the new active vessel from <c>BackgroundMap</c>.
            /// </summary>
            VesselSwitch = 2,
        }

        /// <summary>
        /// Signal to <see cref="ParsekFlight.OnFlightReady"/> that a pending-Limbo tree
        /// is waiting for a restore path. Set by OnLoad when it detects quickload (mode
        /// = Quickload) or vessel switch (mode = VesselSwitch) with a Limbo-state tree.
        /// Replaces the previous bool flag so the two paths can't conflict (#266 review).
        /// </summary>
        internal static ActiveTreeRestoreMode ScheduleActiveTreeRestoreOnFlightReady;

        /// <summary>
        /// Finalizes a Limbo tree on the revert path (and on the safety-net
        /// vessel-switch path where the stash missed pre-transitioning to
        /// LimboVesselSwitch). Routes each recording through
        /// <see cref="ParsekFlight.FinalizeIndividualRecording"/> — the same
        /// per-recording helper the live commit path uses — so any leaf whose
        /// vessel is still loaded in <c>FlightGlobals</c> gets its actual
        /// situation (Landed/Splashed/Orbiting/SubOrbital), and only leaves
        /// whose vessel is gone fall back to Destroyed.
        ///
        /// Then runs <see cref="ParsekFlight.EnsureActiveRecordingTerminalState"/>
        /// for the active-non-leaf case and <see cref="ParsekFlight.PruneZeroPointLeaves"/>
        /// to drop empty placeholders. Finally flips the pending tree state from
        /// Limbo (or LimboVesselSwitch) to Finalized so the existing auto-commit /
        /// merge-dialog path handles it normally.
        ///
        /// Bug #278 (the rewrite from blanket-Destroyed to situation-aware) means
        /// EVA kerbals walking on the surface at the moment of revert/switch keep
        /// their real Landed state and remain canPersist=True. The previous
        /// blanket-stamped-Destroyed code killed canPersist for every leaf even
        /// when the vessel was still loaded.
        /// </summary>
        private static void FinalizePendingLimboTreeForRevert()
        {
            var tree = RecordingStore.PendingTree;
            if (tree == null) return;

            ParsekLog.RecState("FinalizeLimboForRevert:entry", CaptureScenarioRecorderState());

            // Bug #278: run the same per-recording finalize the live commit path uses
            // (ParsekFlight.FinalizeTreeRecordings, minus the active-recorder-flush
            // step which already happened during StashActiveTreeAsPendingLimbo).
            // FinalizeIndividualRecording attempts FlightRecorder.FindVesselByPid +
            // RecordingTree.DetermineTerminalState; if the vessel is alive in
            // FlightGlobals, the leaf gets its actual situation (Landed/SubOrbital/
            // Orbiting/etc.) and CaptureTerminalOrbit + CaptureTerminalPosition fill
            // in the orbital metadata. If the vessel is gone, it falls back to
            // Destroyed + PopulateTerminalOrbitFromLastSegment — same behavior as
            // the previous blanket-Destroyed code, but only for leaves that actually
            // lost their vessel. Surviving leaves (notably EVA kerbals walking on
            // the surface at the moment of revert/switch) keep their real situation
            // and remain canPersist=True.
            double commitUT = Planetarium.GetUniversalTime();
            int newlySet = 0;
            int alreadyTerminal = 0;
            foreach (var kvp in tree.Recordings)
            {
                var rec = kvp.Value;
                bool wasTerminal = rec.TerminalStateValue.HasValue;
                // isSceneExit: true skips FinalizeIndividualRecording's
                // re-snapshot branch (the `if (!isSceneExit)` block at L5795
                // of ParsekFlight). The limbo tree is a frozen snapshot —
                // StashActiveTreeAsPendingLimbo already captured each leaf's
                // VesselSnapshot at the moment of OnSceneChangeRequested, and
                // re-mutating those snapshots here would invalidate the
                // "limbo = exact state at scene-change time" invariant the
                // dispatch comment (L926-948) relies on. The vessel-switch
                // case in particular must not re-snapshot, because the new
                // active vessel may have already started physics-loading
                // and a fresh snapshot would capture mid-load state.
                ParsekFlight.FinalizeIndividualRecording(rec, commitUT, isSceneExit: true, treeContext: tree);
                if (wasTerminal)
                    alreadyTerminal++;
                else if (rec.TerminalStateValue.HasValue)
                    newlySet++;
            }
            ParsekFlight.EnsureActiveRecordingTerminalState(tree, isSceneExit: true);
            ParsekFlight.PruneZeroPointLeaves(tree);
            ParsekFlight.PruneSinglePointDebrisLeaves(tree);

            RecordingStore.MarkPendingTreeFinalized();
            ParsekLog.Info("Scenario",
                $"FinalizePendingLimboTreeForRevert: {newlySet} recording(s) got terminal state set, " +
                $"{alreadyTerminal} already had it, in tree '{tree.TreeName}' — " +
                $"transitioned Limbo → Finalized");
            ParsekLog.RecState("FinalizeLimboForRevert:post", CaptureScenarioRecorderState());
        }

        #region Deferred Merge Dialog

        /// <summary>
        /// Shows the merge dialog after a short delay, allowing the scene to fully load.
        /// Used when autoMerge is off and the player leaves Flight with a pending recording.
        /// </summary>
        private IEnumerator ShowDeferredMergeDialog()
        {
            // Canary: the pre-transition merge dialog
            // (SceneExitInterceptor's HighLogic.LoadScene prefix) is the
            // primary path. If this deferred coroutine fires, the
            // pre-transition path missed the transition (mod compat,
            // KSP version drift, foreign LoadScene patch). Warn so we
            // can investigate.
            ParsekLog.Warn("Scenario",
                $"Deferred merge dialog fired - pre-transition intercept missed " +
                $"scene={HighLogic.LoadedScene} pendingTree=" +
                $"{(RecordingStore.HasPendingTree ? RecordingStore.PendingTree?.TreeName ?? "<unnamed>" : "<none>")} " +
                "(check SceneExitInterceptor or KSP version compat)");

            // Wait ~60 frames for scene to fully load (UI skin, singletons, etc.)
            int waitFrames = 60;
            while (waitFrames-- > 0)
                yield return null;

            // Guard: pending may have been consumed during the wait
            if (!RecordingStore.HasPendingTree)
            {
                mergeDialogPending = false;
                ParsekLog.Verbose("Scenario", "Deferred merge dialog: pending consumed during wait — aborting");
                yield break;
            }

            // Auto-discard idle-on-pad recordings before showing the dialog.
            // Only for Finalized trees (Limbo trees are resume-flow stashes,
            // not merge candidates).
            if (RecordingStore.PendingTreeStateValue == PendingTreeState.Finalized
                && ParsekFlight.IsTreeIdleOnPad(RecordingStore.PendingTree))
            {
                ParsekLog.Info("Scenario", "Idle on pad detected — auto-discarding tree recording");
                DiscardPendingTreeAndRecalculate(
                    "deferred merge dialog idle-on-pad auto-discard");
                ScreenMessages.PostScreenMessage("Recording discarded - vessel idle on pad", 4f);
                mergeDialogPending = false;
                yield break;
            }

            // Show the tree merge dialog.
            //
            // Note: unlike the OnFlightReady fallback (ParsekFlight.cs), this
            // deferred coroutine does NOT need the active-Re-Fly skip guard.
            // The OnFlightReady fallback fires the moment the player enters
            // FLIGHT after Re-Fly invocation or Retry-from-RP — i.e. when the
            // user just started flying a fresh attempt and the dialog would
            // be wrong-timing. The deferred coroutine, by contrast, only
            // fires in a non-FLIGHT scene (call sites at 1862, 1888, 2211
            // all gate on leaving / having left FLIGHT), which means the
            // Re-Fly attempt is already concluded by the player's scene
            // change. Surfacing the merge decision there is the correct
            // recovery path when SceneExitInterceptor missed the
            // pre-transition catch. MergeDialog.ShowTreeDialog already
            // detects ActiveReFlySessionMarker != null and renders the
            // Re-Fly-specific message + suppressed-subtree closure, so the
            // dialog presented here is semantically correct.
            ParsekLog.Info("Scenario",
                $"Showing deferred tree merge dialog in {HighLogic.LoadedScene}");
            MergeDialog.ShowTreeDialog(RecordingStore.PendingTree);
            // mergeDialogPending stays true until the user clicks a button
            // (ClearPendingFlag is called from the button callbacks)
        }

        #endregion

        #region Deferred Seeding

        /// <summary>
        /// Waits for resource singletons to be ready, then triggers RecalculateAndPatch
        /// so the ledger's FundsInitial/ScienceInitial/ReputationInitial actions capture
        /// correct values. Called on initial save load because OnLoad runs before KSP's
        /// Funding/R&amp;D/Reputation scenarios have loaded their data from the save file.
        /// </summary>
        private IEnumerator DeferredSeedAndRecalculate()
        {
            // Phase 1: wait for singletons to exist (non-null).
            // In sandbox mode none will ever appear — bail after timeout.
            int maxWait = 120;
            while (maxWait-- > 0
                   && Funding.Instance == null
                   && ResearchAndDevelopment.Instance == null
                   && Reputation.Instance == null)
                yield return null;

            if (Funding.Instance == null && ResearchAndDevelopment.Instance == null
                && Reputation.Instance == null)
            {
                ParsekLog.Verbose("Scenario",
                    "DeferredSeed: no resource singletons available (sandbox mode) — skipping");
                yield break;
            }

            // Phase 2: wait for singletons to have NON-ZERO values.
            // KSP creates singletons immediately but populates their data from the
            // save file on a separate schedule (can be many seconds on heavy saves).
            // Spin until at least one singleton reports a non-zero value, or timeout.
            int maxValueWait = 600; // ~10 seconds at 60fps
            while (maxValueWait-- > 0
                   && (Funding.Instance == null || Funding.Instance.Funds == 0.0)
                   && (ResearchAndDevelopment.Instance == null || ResearchAndDevelopment.Instance.Science == 0f)
                   && (Reputation.Instance == null || Math.Abs(Reputation.Instance.reputation) < 0.01f))
                yield return null;

            int framesWaited = 599 - maxValueWait; // post-decrement: 600→599 on first check

            // Phase 3 (BUG-F): wait for the universe clock to be initialized before deciding
            // whether to apply a current-UT ledger cutoff. On a cold load
            // Planetarium.GetUniversalTime() returns 0 until the scene's clock is set up;
            // cutting the committed ledger off at UT=0 filters out an entire established career
            // (funds collapse to the starting seed, science to 0). The value-wait above can exit
            // immediately (singletons already carry their save values) while the clock is still
            // 0, so an explicit clock-readiness wait is needed. Spin until the clock reports a
            // real positive UT, or bail on timeout — the recalc below then falls back to the
            // safe full replay (cutoffUT=null), which never wipes an established career.
            // TryReadReadyUniverseTime wraps Planetarium.GetUniversalTime() in a try/catch
            // (it can throw during very early load / scene teardown — see Update()), so the
            // per-frame poll cannot kill this coroutine. A throw / null fetch is treated as
            // not-ready (keep waiting) and yields currentUT=0.0, which routes to full replay.
            int maxUtWait = 600; // ~10 seconds at 60fps
            double currentUT = 0.0;
            bool clockReady = false;
            while (maxUtWait-- > 0)
            {
                clockReady = TryReadReadyUniverseTime(out currentUT);
                if (clockReady)
                    break;
                yield return null;
            }
            int utFramesWaited = 599 - maxUtWait;

            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Verbose("Scenario",
                $"DeferredSeed: values ready after {framesWaited} frames, " +
                $"clock ready={clockReady} after {utFramesWaited} frames (currentUT={currentUT.ToString("R", ic)}) — " +
                $"Funding={(Funding.Instance != null ? Funding.Instance.Funds.ToString("F0", ic) : "null")}, " +
                $"Science={(ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science.ToString("F0", ic) : "null")}, " +
                $"Rep={(Reputation.Instance != null ? Reputation.Instance.reputation.ToString("F1", ic) : "null")}");

            // Route through the guarded helper: it applies the current-UT cutoff only when the
            // clock is ready (currentUT > 0) AND committed actions still lie ahead of it
            // (post-rewind / future-timeline case), otherwise it replays the full committed
            // ledger. A not-ready clock therefore restores an established career instead of
            // wiping it.
            if (IsCurrentUtCutoffSupportedScene(HighLogic.LoadedScene))
            {
                LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions(
                    currentUT,
                    "deferred-seed");
            }
            else
            {
                LedgerOrchestrator.RecalculateAndPatch();
            }
        }

        /// <summary>
        /// Reads the universe clock for the deferred-seed readiness wait, returning true only
        /// when the clock is initialized to a real positive UT. Wrapped in try/catch because
        /// <see cref="Planetarium.GetUniversalTime"/> can throw during very early load / scene
        /// teardown (same defensive pattern as <see cref="Update"/>); a throw or null fetch is
        /// treated as not-ready so the per-frame poll cannot kill the coroutine, and
        /// <paramref name="ut"/> is set to 0.0 (which the recalc routes to a safe full replay).
        /// </summary>
        private static bool TryReadReadyUniverseTime(out double ut)
        {
            ut = 0.0;
            if (Planetarium.fetch == null)
                return false;

            try
            {
                ut = Planetarium.GetUniversalTime();
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose("Scenario",
                    $"DeferredSeed: Planetarium.GetUniversalTime threw {ex.GetType().Name}: " +
                    $"{ex.Message}; treating clock as not ready");
                ut = 0.0;
                return false;
            }

            return LedgerOrchestrator.IsCurrentUtReadyForCutoff(ut);
        }

        #endregion

        #region Budget Deduction

        /// <summary>
        /// Waits for resource singletons to be available, then deducts committed
        /// budget from the game state. This ensures the KSP top bar and all
        /// purchase checks reflect available (non-committed) resources.
        /// </summary>
        private IEnumerator ApplyBudgetDeductionWhenReady()
        {
            // Wait until ALL resource singletons are available (may take a few frames
            // after scene load). Use || so we wait while ANY singleton is still null.
            int maxWait = 120; // ~2 seconds at 60fps
            while (maxWait-- > 0
                   && (Funding.Instance == null
                       || ResearchAndDevelopment.Instance == null
                       || Reputation.Instance == null))
                yield return null;

            ParsekLog.Verbose("Scenario",
                $"ApplyBudgetDeduction: singletons ready after {120 - maxWait} frames. " +
                $"Funding={Funding.Instance != null}, R&D={ResearchAndDevelopment.Instance != null}, Rep={Reputation.Instance != null}");

            if (budgetDeductionApplied)
            {
                ParsekLog.Verbose("Scenario",
                    "Budget deduction already applied for this revert load");
    
                yield break;
            }
            budgetDeductionApplied = true;

            // Audited for #527: this coroutine only runs on true revert follow-up
            // budget restoration. The rewind path marks this guard itself and
            // uses ApplyRewindResourceAdjustment(adjustedUT) instead.
            LedgerOrchestrator.RecalculateAndPatch();

        }

        /// <summary>
        /// Applies the differential resource adjustment after a rewind.
        /// Deferred via coroutine because Funding/R&amp;D/Reputation singletons
        /// are not available during OnLoad.
        /// </summary>
        private IEnumerator ApplyRewindResourceAdjustment()
        {
            // Capture rewind state before yielding — flags are cleared synchronously
            // in OnLoad after StartCoroutine returns.
            var saved = RewindContext.RewindReserved;
            double rewindUT = RewindContext.RewindUT;
            double adjustedUT = RewindContext.RewindAdjustedUT;
            double baselineFunds = RewindContext.RewindBaselineFunds;
            double baselineScience = RewindContext.RewindBaselineScience;
            float baselineRep = RewindContext.RewindBaselineRep;

            // CRITICAL: yield at least one frame before touching any singleton.
            // During OnLoad, singletons from the OLD scene may still be alive.
            // Without this yield, SetUniversalTime modifies the OLD Planetarium
            // which is then destroyed when the new scene finishes loading.
            yield return null;

            var ic = CultureInfo.InvariantCulture;

            // Apply adjusted UT unconditionally — Planetarium is always available
            // after the first yield. This must NOT be gated on resource singletons
            // (sandbox/science mode has no Funding/R&D/Reputation, but still needs UT).
            // UT=0 is valid (recording near game start with lead time clamped to 0).
            {
                double prePlanetariumUT = Planetarium.GetUniversalTime();
                Planetarium.SetUniversalTime(adjustedUT);
                RecordingStore.RewindUTAdjustmentPending = false;
                ParsekLog.Info("Rewind",
                    $"UT adjustment: {prePlanetariumUT.ToString("F1", ic)} → {adjustedUT.ToString("F1", ic)} " +
                    $"(post-set check: {Planetarium.GetUniversalTime().ToString("F1", ic)})");
            }

            // Wait for resource singletons (career mode only).
            // In sandbox/science mode these are permanently null — skip gracefully.
            int maxWait = 120; // ~2 seconds at 60fps
            while (maxWait-- > 0
                   && (Funding.Instance == null
                       || ResearchAndDevelopment.Instance == null
                       || Reputation.Instance == null))
                yield return null;

            // Pass the adjusted UT captured BEFORE `yield return null` above.
            // RewindContext.EndRewind() has already cleared the global by the time
            // this coroutine resumes, so we cannot read from RewindContext here.
            // The cutoff applies to career resources; crew reservations are rebuilt
            // from the full committed timeline inside LedgerOrchestrator.
            //
            // Drawdown-guard signal 5 (Blocker 1): this is the AUTHORITATIVE deferred
            // plain-rewind drawdown. Clear the in-progress flag in a finally so a clean OR
            // thrown patch both release it; the flag must stay true THROUGH this patch so
            // the guard authorizes any legitimate rewind reduction (all four other signals
            // are false here). NOT cleared by EndRewind (which ran before this resumed).
            try
            {
                LedgerOrchestrator.RecalculateAndPatch(
                    adjustedUT,
                    suppressSuspiciousDrawdownWarnings: true);
            }
            finally
            {
                RewindContext.EndRewindResourceAdjustment();
            }

            // Belt-and-suspenders guard: if some future refactor accidentally schedules
            // the normal revert budget-deduction coroutine during this rewind load, it
            // should no-op instead of patching the same balances again.
            budgetDeductionApplied = true;

        }

        #endregion

        #region Recording Serialization

        /// <summary>
        /// Clears terminal state (Recovered/Destroyed) that was set after a vessel was spawned.
        /// On revert, the spawn is undone so the terminal state from the previous flight is stale.
        /// </summary>
        internal static void ClearPostSpawnTerminalState(Recording rec, string context = "recording")
        {
            if (rec.VesselSpawned && rec.TerminalStateValue.HasValue)
            {
                var ts = rec.TerminalStateValue.Value;
                if (ts == TerminalState.Recovered || ts == TerminalState.Destroyed)
                {
                    ParsekLog.Verbose("Scenario",
                        $"Clearing post-spawn terminal state {ts} for {context} '{rec.VesselName}'");
                    rec.TerminalStateValue = null;
                }
            }
        }

        /// <summary>
        /// BUG-H — the single launch-identity-aware decision every revert/rewind vessel deletion
        /// routes through. A candidate flightState/loaded vessel may be deleted ONLY when both hold:
        ///
        /// <list type="number">
        /// <item><b>Launch-identity match</b> (Correction 1): the candidate (its
        /// <c>persistentId</c> + <c>Vessel.id</c> Guid) is the SAME launch as some recording — the
        /// recording's spawn/adoption endpoint (always) and, on rewind, its recorded source vessel
        /// (<paramref name="matchSource"/>). A conclusive launch-Guid mismatch is never a match, so
        /// a relaunch of the same craft (shared craft-baked name/pid, fresh Guid) is never deleted
        /// in place of the recording's vessel.</item>
        /// <item><b>Pre-existing whitelist</b> (Correction 2): a candidate whose pid is in
        /// <paramref name="preExistingWhitelist"/> pre-existed this operation and is NEVER stripped.
        /// On revert this is the revert-target launch/prelaunch quicksave (a vessel present there
        /// belongs to an unrelated mission, not the reverted flight). On rewind this is the rewind
        /// quicksave (a vessel present there pre-existed the rewind point, so it is not future); this
        /// backstops the launch-Guid gate for the rare guidless adoption-stamp recording, mirroring
        /// the existing <see cref="StripFuturePrelaunchVessels"/> pid-whitelist contract.</item>
        /// </list>
        ///
        /// <paramref name="requireWhitelist"/> (revert only): when the whitelist is unavailable the
        /// candidate is NOT deleted (fail-closed, data-loss safe) — a missing scope signal can never
        /// delete real vessels. Rewind passes <paramref name="requireWhitelist"/>=false so that, if
        /// the rewind quicksave pids are unavailable, it falls back to launch-identity-only stripping
        /// (its prior behaviour) rather than failing to remove genuinely future-timeline vessels; the
        /// separate <see cref="StripFuturePrelaunchVessels"/> pid-whitelist pass still backstops it.
        ///
        /// Pure and testable (no ProtoVessel / FlightGlobals dependency): callers resolve the
        /// candidate's pid + Guid + situation and pass them in.
        /// </summary>
        internal static bool ShouldStripVesselForRecordings(
            uint candidatePid, string candidateGuid, Vessel.Situations situation,
            IReadOnlyList<Recording> recordings,
            bool matchSource, bool skipPrelaunch,
            bool requireWhitelist, HashSet<uint> preExistingWhitelist,
            out Recording matched, out string reason)
        {
            matched = null;

            if (candidatePid == 0)
            {
                reason = "candidate pid zero";
                return false;
            }

            // On KSP Revert, skip PRELAUNCH vessels — these are the user's launch vessel on the pad.
            // On Parsek Rewind, strip them too — a PRELAUNCH vessel from a later launch is
            // incompatible with the earlier game state being restored.
            if (skipPrelaunch && situation == Vessel.Situations.PRELAUNCH)
            {
                reason = "PRELAUNCH skipped (launch vessel protected)";
                return false;
            }

            // Correction 2: a vessel present in the pre-existing whitelist (revert target / rewind
            // quicksave) pre-existed this operation and belongs to an unrelated mission or an earlier
            // timeline point — never strip it. Applied whenever a whitelist is supplied (revert AND
            // rewind), so the rewind path is backstopped against the guidless adoption-stamp gap.
            if (preExistingWhitelist != null && preExistingWhitelist.Contains(candidatePid))
            {
                reason = "pre-existing in launch/rewind quicksave (not part of this operation)";
                return false;
            }

            // Revert requires the whitelist: with no scope signal, fail closed so a missing target
            // snapshot can never delete real vessels. (Rewind passes requireWhitelist=false.)
            if (requireWhitelist && preExistingWhitelist == null)
            {
                reason = "scope required but launch-quicksave whitelist unavailable (fail-closed)";
                return false;
            }

            // Correction 1: only delete a vessel that is genuinely the SAME launch as a recording.
            matched = VesselLaunchIdentity.FindMatchingRecording(
                recordings, candidatePid, candidateGuid, matchSource, matchSpawn: true);
            if (matched == null)
            {
                reason = "no same-launch recording match (different real launch or unrelated vessel)";
                return false;
            }

            reason = requireWhitelist
                ? "same-launch recording match + appeared during the reverted flight"
                : "same-launch recording match";
            return true;
        }

        /// <summary>
        /// Strips protoVessels from flightState that genuinely belong to a recording's launch and
        /// (on revert) appeared during the reverted flight. Called during revert/rewind to remove
        /// orphaned spawned vessels before they contaminate the next launch quicksave. The
        /// per-vessel decision is the launch-identity-aware <see cref="ShouldStripVesselForRecordings"/>
        /// (BUG-H): name-only matching previously deleted real, separately-launched craft that merely
        /// reuse the craft-baked name/pid of a recording.
        /// </summary>
        /// <param name="protoVessels">flightState's protoVessels list (modified in place).</param>
        /// <param name="recordings">All committed recordings (for the launch-identity lookup).</param>
        /// <param name="matchSource">Also match a recording's recorded SOURCE vessel (rewind=true,
        /// revert=false). Revert only undoes Parsek-spawned/adopted vessels of the reverted flight;
        /// rewind also removes the recorded vessel itself, which is future relative to the rewind point.</param>
        /// <param name="skipPrelaunch">When true (KSP Revert), PRELAUNCH vessels are kept; when false
        /// (Parsek Rewind), a PRELAUNCH vessel from a later launch is also stripped.</param>
        /// <param name="requireWhitelist">When true (revert), fail closed (strip nothing) if
        /// <paramref name="preExistingWhitelist"/> is null. Rewind passes false (identity-only fallback).</param>
        /// <param name="preExistingWhitelist">Pids that pre-existed this operation and are never
        /// stripped: the revert-target launch/prelaunch quicksave on revert, or the rewind quicksave
        /// on rewind. Applied as a protect-set whenever supplied.</param>
        internal static int StripOrphanedSpawnedVessels(
            List<ProtoVessel> protoVessels,
            IReadOnlyList<Recording> recordings,
            bool matchSource, bool skipPrelaunch,
            bool requireWhitelist, HashSet<uint> preExistingWhitelist)
        {
            if (protoVessels == null || recordings == null || recordings.Count == 0)
                return 0;

            int stripped = 0;
            int skippedPreExisting = 0;
            int skippedDifferentLaunch = 0;
            for (int i = protoVessels.Count - 1; i >= 0; i--)
            {
                var pv = protoVessels[i];
                if (GhostMapPresence.IsGhostMapVessel(pv.persistentId)) continue;

                string candidateGuid = pv.vesselID != Guid.Empty
                    ? pv.vesselID.ToString("N")
                    : null;

                bool shouldStrip = ShouldStripVesselForRecordings(
                    pv.persistentId, candidateGuid, pv.situation, recordings,
                    matchSource, skipPrelaunch, requireWhitelist, preExistingWhitelist,
                    out Recording matched, out string reason);

                if (!shouldStrip)
                {
                    if (reason != null && reason.StartsWith("pre-existing"))
                        skippedPreExisting++;
                    else if (reason != null && reason.StartsWith("no same-launch"))
                        skippedDifferentLaunch++;
                    ParsekLog.Verbose("Scenario",
                        $"Keeping vessel '{pv.vesselName}' (pid={pv.persistentId}, guid={candidateGuid ?? "(none)"}, " +
                        $"situation={pv.situation}) — {reason}");
                    continue;
                }

                ParsekLog.Info("Scenario",
                    $"Stripping orphaned spawned vessel '{pv.vesselName}' " +
                    $"(pid={pv.persistentId}, guid={candidateGuid ?? "(none)"}, situation={pv.situation}) " +
                    $"from flightState — matched recording '{matched?.VesselName ?? "(unknown)"}' " +
                    $"(recordedGuid={matched?.RecordedVesselGuid ?? "(none)"}); {reason}");
                protoVessels.RemoveAt(i);
                stripped++;
            }

            if (stripped > 0 || skippedPreExisting > 0 || skippedDifferentLaunch > 0)
                ParsekLog.Info("Scenario",
                    $"StripOrphanedSpawnedVessels: removed {stripped} vessel(s) from flightState " +
                    $"(kept {skippedPreExisting} pre-existing, {skippedDifferentLaunch} different-launch; " +
                    $"matchSource={matchSource}, requireWhitelist={requireWhitelist}, " +
                    $"whitelistPids={preExistingWhitelist?.Count ?? 0})");

            return stripped;
        }

        /// <summary>
        /// Strips vessels whose persistentId is NOT in the rewind quicksave whitelist (#129/#164).
        /// These are vessels from after the rewind point (e.g. a pad vessel from a later launch)
        /// that persisted through rewind because the launch-identity strip only removes vessels that
        /// belong to a recording's launch — an unrecorded future vessel survives that match.
        ///
        /// BUG-H audit: this path is NOT in the name/pid-without-Guid defect class. It is keyed on the
        /// rewind quicksave whitelist (keep-if-present), never on a recording-name match, so it cannot
        /// delete a vessel "in place of" a recording's relaunched craft. A vessel present in the
        /// quicksave is always kept (the data-loss-safe direction); only vessels genuinely absent from
        /// the rewind target are removed, preserving the rewind contract. Behaviour intentionally
        /// unchanged.
        /// </summary>
        /// <param name="protoVessels">The flightState's protoVessels list (modified in-place).</param>
        /// <param name="quicksavePids">PIDs of vessels that existed in the rewind quicksave.</param>
        internal static int StripFuturePrelaunchVessels(
            List<ProtoVessel> protoVessels, HashSet<uint> quicksavePids)
        {
            if (protoVessels == null || quicksavePids == null)
                return 0;

            int stripped = 0;
            for (int i = protoVessels.Count - 1; i >= 0; i--)
            {
                var pv = protoVessels[i];
                if (!ShouldStripFuturePrelaunch(pv.situation, pv.persistentId, quicksavePids))
                    continue;

                ParsekLog.Info("Scenario",
                    $"Stripping future vessel '{pv.vesselName}' " +
                    $"(pid={pv.persistentId}, sit={pv.situation}) — not in quicksave whitelist");
                protoVessels.RemoveAt(i);
                stripped++;
            }

            return stripped;
        }

        /// <summary>
        /// Pure decision: should this vessel be stripped as a future PRELAUNCH vessel?
        /// Returns true if the vessel is PRELAUNCH and its PID is not in the quicksave whitelist.
        /// Extracted for testability (ProtoVessel can't be constructed outside KSP).
        /// </summary>
        internal static bool ShouldStripFuturePrelaunch(
            Vessel.Situations situation, uint persistentId, HashSet<uint> quicksavePids)
        {
            if (quicksavePids == null)
                return false;
            // Strip ANY vessel not in the quicksave whitelist (#164).
            // Previously only stripped PRELAUNCH; now catches flags, landed capsules,
            // and other player-created vessels from the future after rewind.
            return !quicksavePids.Contains(persistentId);
        }

        /// <summary>
        /// After vessel stripping, reconcile recording spawn state with the actual flightState.
        /// If a recording's SpawnedVesselPersistentId points to a vessel that was stripped
        /// (no longer in flightState), reset the spawn tracking so the vessel can be re-spawned
        /// at the correct time. Without this, the PID dedup check in ShouldSpawnAtRecordingEnd
        /// blocks respawning permanently (#168).
        /// </summary>
        internal static int ReconcileSpawnStateAfterStrip(
            List<ProtoVessel> remainingVessels, IReadOnlyList<Recording> recordings)
        {
            return ReconcileSpawnStateAfterStrip(CollectSurvivingPids(remainingVessels), recordings);
        }

        /// <summary>
        /// Testable overload that takes pre-collected surviving PIDs.
        /// </summary>
        internal static int ReconcileSpawnStateAfterStrip(
            HashSet<uint> survivingPids, IReadOnlyList<Recording> recordings)
        {
            if (recordings == null || recordings.Count == 0)
                return 0;

            int reconciled = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                if (ShouldResetSpawnState(recordings[i].SpawnedVesselPersistentId, survivingPids))
                {
                    uint oldPid = recordings[i].SpawnedVesselPersistentId;
                    recordings[i].SpawnedVesselPersistentId = 0;
                    recordings[i].VesselSpawned = false;
                    recordings[i].SpawnAttempts = 0;
                    recordings[i].SpawnDeathCount = 0;
                    TerminalOrbitSpawnSafety.Clear(recordings[i]);
                    ParsekLog.Info("Scenario",
                        $"Reconciled spawn state for recording #{i} \"{recordings[i].VesselName}\": " +
                        $"pid={oldPid} no longer in flightState — reset for re-spawn");
                    reconciled++;
                }
            }

            if (reconciled > 0)
                ParsekLog.Info("Scenario",
                    $"ReconcileSpawnStateAfterStrip: reset {reconciled} recording(s) whose spawned vessel was stripped");

            return reconciled;
        }

        /// <summary>
        /// Pure decision: should this recording's spawn state be reset?
        /// Returns true when spawnedPid is non-zero but not found in the surviving vessel set.
        /// </summary>
        internal static bool ShouldResetSpawnState(uint spawnedPid, HashSet<uint> survivingPids)
        {
            if (spawnedPid == 0)
                return false;
            return survivingPids == null || !survivingPids.Contains(spawnedPid);
        }

        /// <summary>
        /// BUG-H: launch-identity-aware reconcile. A recording is reset only when its spawned vessel
        /// is no longer present as the SAME launch among the survivors. A surviving vessel that shares
        /// the recording's spawned pid but is a DIFFERENT launch (craft-baked pid collision) does NOT
        /// keep the recording's spawn state alive — the recording is reset so it can re-spawn, instead
        /// of being permanently blocked by a same-pid stranger. Adoption-stamp recordings are matched
        /// by launch Guid; genuine Parsek spawns (KSP-unique spawn pid) match by pid.
        /// </summary>
        internal static int ReconcileSpawnStateAfterStrip(
            IReadOnlyList<(uint pid, string guid)> survivors, IReadOnlyList<Recording> recordings)
        {
            if (recordings == null || recordings.Count == 0)
                return 0;

            int reconciled = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                if (ShouldResetSpawnState(recordings[i], survivors))
                {
                    uint oldPid = recordings[i].SpawnedVesselPersistentId;
                    recordings[i].SpawnedVesselPersistentId = 0;
                    recordings[i].VesselSpawned = false;
                    recordings[i].SpawnAttempts = 0;
                    recordings[i].SpawnDeathCount = 0;
                    TerminalOrbitSpawnSafety.Clear(recordings[i]);
                    ParsekLog.Info("Scenario",
                        $"Reconciled spawn state for recording #{i} \"{recordings[i].VesselName}\": " +
                        $"pid={oldPid} not present as the same launch in flightState — reset for re-spawn");
                    reconciled++;
                }
            }

            if (reconciled > 0)
                ParsekLog.Info("Scenario",
                    $"ReconcileSpawnStateAfterStrip (guid-aware): reset {reconciled} recording(s) whose spawned vessel was stripped");

            return reconciled;
        }

        /// <summary>
        /// Guid-aware pure decision for <see cref="ReconcileSpawnStateAfterStrip(IReadOnlyList{ValueTuple{uint,string}}, IReadOnlyList{Recording})"/>:
        /// reset the recording's spawn state when its spawn endpoint is non-zero and no surviving
        /// vessel is the same launch (via <see cref="VesselLaunchIdentity.LiveVesselIsRecordedSpawn"/>).
        /// A null survivor set means nothing survived, so any non-zero spawn pid is reset.
        /// </summary>
        internal static bool ShouldResetSpawnState(
            Recording rec, IReadOnlyList<(uint pid, string guid)> survivors)
        {
            if (rec == null || rec.SpawnedVesselPersistentId == 0)
                return false;
            if (survivors == null)
                return true;
            for (int i = 0; i < survivors.Count; i++)
            {
                if (VesselLaunchIdentity.LiveVesselIsRecordedSpawn(rec, survivors[i].pid, survivors[i].guid))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Collects (persistentId, launch-Guid) identities of the surviving protoVessels for the
        /// guid-aware reconcile. Extracted so the reconcile logic stays testable without a live
        /// flightState (ProtoVessel can't be constructed outside KSP).
        /// </summary>
        internal static List<(uint pid, string guid)> CollectSurvivingVesselIdentities(
            List<ProtoVessel> remainingVessels)
        {
            var list = new List<(uint pid, string guid)>();
            if (remainingVessels == null)
                return list;
            for (int i = 0; i < remainingVessels.Count; i++)
            {
                var pv = remainingVessels[i];
                if (pv == null) continue;
                string g = pv.vesselID != Guid.Empty ? pv.vesselID.ToString("N") : null;
                list.Add((pv.persistentId, g));
            }
            return list;
        }

        /// <summary>
        /// BUG-C (2026-06-07 career playtest): re-applies the durable terminal-orbit
        /// "cannot spawn safely" abandon from a saved RECORDING node onto an in-memory
        /// committed recording. The in-session OnLoad reconcile clears all terminal
        /// spawn-safety fields (via <see cref="TerminalOrbitSpawnSafety.Clear"/>) and
        /// then restores only the saved subset; without re-restoring this flag a
        /// terminal-orbit vessel that already died on spawn is re-materialized after
        /// every scene change (it logs "will not be retried" yet is retried). The flag
        /// is absent on a revert quicksave (no tree nodes), so the abandon correctly
        /// does not carry across a revert. Mirrors
        /// <see cref="RecordingTreeRecordCodec"/>'s save/load of the same keys, which
        /// covers the cold-start rebuild path.
        /// </summary>
        internal static void RestorePersistedTerminalAbandon(Recording rec, ConfigNode savedTreeRecNode)
        {
            if (rec == null || savedTreeRecNode == null)
                return;

            bool savedCannotSpawn = false;
            bool.TryParse(savedTreeRecNode.GetValue("terminalSpawnCannotSpawnSafely"), out savedCannotSpawn);
            rec.TerminalSpawnCannotSpawnSafely = savedCannotSpawn;

            string savedReason = savedTreeRecNode.GetValue("terminalSpawnSafetyReasonCode");
            if (!string.IsNullOrEmpty(savedReason))
                rec.TerminalSpawnSafetyReasonCode = savedReason;
        }

        /// <summary>
        /// After plain Rewind-to-Launch, mark only the active/source recording that was
        /// stripped from flightState as <see cref="Recording.SpawnSuppressedByRewind"/>.
        /// This preserves #573's duplicate-source protection without turning every
        /// future same-tree recording into permanent ghost-only history (#589).
        ///
        /// <para>Same-tree future recordings are logged and deliberately left
        /// spawn-eligible. Legacy unscoped markers from older saves are cleared when
        /// this helper can prove they are future-of-rewind.</para>
        /// </summary>
        internal static int MarkRewoundTreeRecordingsAsGhostOnly(
            IReadOnlyList<Recording> recordings)
        {
            if (recordings == null || recordings.Count == 0)
                return 0;

            uint rewindSourcePid = RecordingStore.RewindReplayTargetSourcePid;
            string rewindRecId = RecordingStore.RewindReplayTargetRecordingId;
            string rewoundTreeId = ResolveRewoundTreeId(recordings, rewindRecId);
            string rewindRecGuid = ResolveRewoundRecordingGuid(recordings, rewindRecId);
            double rewindUT = RewindContext.RewindUT;

            int marked = 0;
            int retained = 0;
            int futureAllowed = 0;
            int cleared = 0;
            int skipped = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec == null)
                {
                    skipped++;
                    continue;
                }

                bool inRewoundTree = rewoundTreeId != null
                    && string.Equals(rec.TreeId, rewoundTreeId, StringComparison.Ordinal);
                bool sameTreeFuture = inRewoundTree && IsFutureOfRewindTarget(rec, rewindUT);

                if (ShouldApplyRewindSpawnSuppression(
                        rec,
                        rewindRecId,
                        rewindSourcePid,
                        rewoundTreeId,
                        rewindUT,
                        out string applyReason,
                        rewindRecGuid))
                {
                    if (!rec.SpawnSuppressedByRewind)
                        marked++;
                    else
                        retained++;

                    rec.SpawnSuppressedByRewind = true;
                    rec.SpawnSuppressedByRewindReason = applyReason;
                    rec.SpawnSuppressedByRewindUT = rewindUT;

                    ParsekLog.Verbose("Rewind",
                        $"SpawnSuppressedByRewind applied: #{i} \"{rec.VesselName}\" " +
                        $"id={rec.RecordingId} tree={(rec.TreeId ?? "<none>")} " +
                        $"reason={applyReason} rewindUT={FormatRewindUT(rewindUT)} " +
                        $"startUT={FormatRewindUT(rec.StartUT)} endUT={FormatRewindUT(rec.EndUT)} " +
                        "(#573 active/source recording protection)");
                    continue;
                }

                if (sameTreeFuture)
                {
                    futureAllowed++;
                    if (rec.SpawnSuppressedByRewind)
                    {
                        ClearRewindSpawnSuppression(
                            rec,
                            $"reason={RewindSpawnSuppressionReasonSameTreeFutureRecording} " +
                            $"rewindUT={FormatRewindUT(rewindUT)} startUT={FormatRewindUT(rec.StartUT)} " +
                            $"endUT={FormatRewindUT(rec.EndUT)}",
                            "future-of-rewind same-tree recording remains spawn-eligible");
                        cleared++;
                    }

                    ParsekLog.Verbose("Rewind",
                        $"SpawnSuppressedByRewind not applied: #{i} \"{rec.VesselName}\" " +
                        $"id={rec.RecordingId} tree={rec.TreeId} " +
                        $"reason={RewindSpawnSuppressionReasonSameTreeFutureRecording} " +
                        $"rewindUT={FormatRewindUT(rewindUT)} startUT={FormatRewindUT(rec.StartUT)} " +
                        $"endUT={FormatRewindUT(rec.EndUT)} — spawn allowed when endpoint is reached post-rewind");
                    continue;
                }

                if (rec.SpawnSuppressedByRewind)
                {
                    ClearRewindSpawnSuppression(
                        rec,
                        $"reason=unrelated-to-current-rewind rewindUT={FormatRewindUT(rewindUT)}",
                        "stale marker no longer matches active/source scope");
                    cleared++;
                }
                skipped++;
            }

            ParsekLog.Verbose("Rewind",
                $"SpawnSuppressedByRewind scope summary: marked={marked} retained={retained} " +
                $"cleared={cleared} futureAllowed={futureAllowed} skipped={skipped} " +
                $"rewindRec={rewindRecId ?? "<null>"} tree={rewoundTreeId ?? "<none>"} " +
                $"rewindUT={FormatRewindUT(rewindUT)}");
            return marked + retained;
        }

        internal static bool ShouldApplyRewindSpawnSuppression(
            Recording rec,
            string rewindRecordingId,
            uint rewindSourcePid,
            string rewoundTreeId,
            double rewindUT,
            out string reason,
            string rewindRecordingGuid = null)
        {
            reason = null;
            if (rec == null)
                return false;

            if (!string.IsNullOrEmpty(rewindRecordingId)
                && string.Equals(rec.RecordingId, rewindRecordingId, StringComparison.Ordinal))
            {
                reason = RewindSpawnSuppressionReasonSameRecording;
                return true;
            }

            // #976-class: rewindSourcePid is the craft-baked pid, reused by every launch of the
            // same craft, so a bare pid match would suppress prior UNRELATED launches' terminal
            // spawns (the long-latent #829 hole). Require the candidate to share the rewound
            // launch (guid); a conclusive guid mismatch means a different launch and is not
            // suppressed. A null/unknown guid on either side falls back to today's pid-only match.
            bool matchesRewindSource = rewindSourcePid != 0
                && rec.VesselPersistentId == rewindSourcePid
                && !VesselLaunchIdentity.GuidsConclusivelyDiffer(rec.RecordedVesselGuid, rewindRecordingGuid);
            if (!matchesRewindSource)
                return false;

            bool inRewoundTree = rewoundTreeId != null
                && string.Equals(rec.TreeId, rewoundTreeId, StringComparison.Ordinal);
            if (inRewoundTree && IsFutureOfRewindTarget(rec, rewindUT))
                return false;

            if (string.IsNullOrEmpty(rewoundTreeId))
            {
                reason = RewindSpawnSuppressionReasonSameRecording;
                return true;
            }

            if (RecordingOverlapsRewindTarget(rec, rewindUT))
            {
                reason = RewindSpawnSuppressionReasonSameRecording;
                return true;
            }

            return false;
        }

        internal static bool IsFutureOfRewindTarget(Recording rec, double rewindUT)
        {
            if (rec == null)
                return false;

            return rec.StartUT > rewindUT + RewindSpawnSuppressionUTEpsilon
                && rec.EndUT > rewindUT + RewindSpawnSuppressionUTEpsilon;
        }

        internal static bool RecordingOverlapsRewindTarget(Recording rec, double rewindUT)
        {
            if (rec == null)
                return false;

            return rec.StartUT <= rewindUT + RewindSpawnSuppressionUTEpsilon
                && rec.EndUT >= rewindUT - RewindSpawnSuppressionUTEpsilon;
        }

        internal static void ClearRewindSpawnSuppression(
            Recording rec,
            string reason,
            string lifecycle)
        {
            if (rec == null || !rec.SpawnSuppressedByRewind)
                return;

            string oldReason = string.IsNullOrEmpty(rec.SpawnSuppressedByRewindReason)
                ? "<none>"
                : rec.SpawnSuppressedByRewindReason;
            double oldUT = rec.SpawnSuppressedByRewindUT;

            rec.SpawnSuppressedByRewind = false;
            rec.SpawnSuppressedByRewindReason = null;
            rec.SpawnSuppressedByRewindUT = double.NaN;

            ParsekLog.Info("Rewind",
                $"SpawnSuppressedByRewind cleared: \"{rec.VesselName}\" id={rec.RecordingId} " +
                $"previousReason={oldReason} previousRewindUT={FormatRewindUT(oldUT)} " +
                $"{reason} lifecycle=\"{lifecycle}\"");
        }

        /// <summary>
        /// Clears the #573 active/source spawn-suppression marker when the player
        /// engages with a rewound recording via Watch. The original #573 protection
        /// was scoped at rewind time to avoid background ghost playback materialising
        /// a duplicate of a vessel the player just stripped (chain-tip respawn next to
        /// the player's freshly-launched vessel). Watching the rewound recording is
        /// the player's explicit signal that they want to see this recording's outcome
        /// — the spawn at terminal end should be allowed to evaluate normally.
        /// Returns true when a marker was actually cleared. Only acts on the
        /// <see cref="RewindSpawnSuppressionReasonSameRecording"/> reason, which is
        /// the only marker reason produced today. This helper
        /// deliberately does NOT gate on terminal state — final spawnability is
        /// owned by <c>ShouldSpawnAtRecordingEnd</c>'s other gates (snapshot
        /// situation, terminal-state, PID dedup, etc.). Gating here on an enum
        /// whitelist would re-introduce the bug for null-terminal recordings that
        /// pass <c>ShouldSpawnAtRecordingEnd</c> via the snapshot-situation path.
        /// </summary>
        internal static bool TryClearSpawnSuppressionOnWatchEntry(Recording rec)
        {
            if (rec == null || !rec.SpawnSuppressedByRewind)
                return false;
            if (!string.Equals(rec.SpawnSuppressedByRewindReason,
                    RewindSpawnSuppressionReasonSameRecording,
                    StringComparison.Ordinal))
                return false;

            ClearRewindSpawnSuppression(
                rec,
                $"reason={rec.SpawnSuppressedByRewindReason}",
                "watch-entry: user engaged with rewound recording, allowing spawn at recording end");
            return true;
        }

        internal static string FormatRewindUT(double ut)
        {
            return double.IsNaN(ut)
                ? "<nan>"
                : ut.ToString("F3", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Returns the TreeId of the rewind owner recording, or null when the rewind
        /// scope was not a tree (e.g., standalone recording rewind, or scope cleared).
        /// </summary>
        private static string ResolveRewoundTreeId(
            IReadOnlyList<Recording> recordings, string rewindRecordingId)
        {
            if (string.IsNullOrEmpty(rewindRecordingId))
                return null;

            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec != null && string.Equals(rec.RecordingId, rewindRecordingId, StringComparison.Ordinal))
                    return rec.TreeId;
            }
            return null;
        }

        // Launch guid of the rewound recording, so suppression only catches recordings sharing
        // that launch (not other launches of the same craft that reuse the baked pid).
        private static string ResolveRewoundRecordingGuid(
            IReadOnlyList<Recording> recordings, string rewindRecordingId)
        {
            if (string.IsNullOrEmpty(rewindRecordingId))
                return null;

            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec != null && string.Equals(rec.RecordingId, rewindRecordingId, StringComparison.Ordinal))
                    return rec.RecordedVesselGuid;
            }
            return null;
        }

        /// <summary>
        /// Collects persistent IDs from all remaining protoVessels.
        /// </summary>
        private static HashSet<uint> CollectSurvivingPids(List<ProtoVessel> protoVessels)
        {
            var pids = new HashSet<uint>();
            if (protoVessels == null)
                return pids;
            for (int i = 0; i < protoVessels.Count; i++)
                pids.Add(protoVessels[i].persistentId);
            return pids;
        }

        /// <summary>
        /// Builds the post-strip survivor PID set from the raw
        /// <c>flightState.protoVessels</c> PID enumeration minus
        /// <see cref="PostLoadStripResult.StrippedPids"/>. Pure function
        /// extracted for direct xUnit coverage — the production input shape
        /// (raw <c>List&lt;ProtoVessel&gt;</c> from KSP) cannot be constructed
        /// outside KSP, but the PID-level subtraction logic can be.
        ///
        /// <para>
        /// The Re-Fly post-load contract requires this subtraction:
        /// <see cref="PostLoadStripper.Strip"/> removes vessels via
        /// <see cref="Vessel.Die"/> but does NOT remove the matching
        /// <see cref="ProtoVessel"/> from
        /// <c>HighLogic.CurrentGame.flightState.protoVessels</c>. That list is
        /// the save-shape mirror and is not auto-synchronized with
        /// <c>Vessel.Die()</c>. Without subtracting <c>StrippedPids</c>, a
        /// recording's stale <c>SpawnedVesselPersistentId</c> still appears
        /// "alive" and the reconcile silently leaves <c>VesselSpawned=true</c>.
        /// </para>
        /// </summary>
        internal static HashSet<uint> ComputeSurvivorsFromProtoVesselPids(
            IEnumerable<uint> protoVesselPids, IEnumerable<uint> strippedPids)
        {
            var stripped = new HashSet<uint>();
            if (strippedPids != null)
            {
                foreach (uint p in strippedPids)
                    stripped.Add(p);
            }

            var survivors = new HashSet<uint>();
            if (protoVesselPids == null)
                return survivors;

            foreach (uint pid in protoVesselPids)
            {
                if (!stripped.Contains(pid))
                    survivors.Add(pid);
            }
            return survivors;
        }

        /// <summary>
        /// Saves versioned recording metadata and ghost-geometry metadata.
        /// Extracted for testability.
        /// </summary>
        internal static void SaveRecordingMetadata(ConfigNode recNode, Recording rec)
        {
            SaveRecordingIdentityAndLoopMetadata(recNode, rec);

            SaveRecordingBudgetAndRewindMetadata(recNode, rec);
            SaveRecordingGroupAndSegmentMetadata(recNode, rec);

            SaveRecordingLocationAndTerminalMetadata(recNode, rec);
            SaveRecordingPlaybackFlags(recNode, rec);

            SaveRecordingManifestMetadata(recNode, rec);

        }

        private static void SaveRecordingIdentityAndLoopMetadata(ConfigNode recNode, Recording rec)
        {
            recNode.AddValue("recordingId", rec.RecordingId ?? "");
            recNode.AddValue("recordingFormatVersion", rec.RecordingFormatVersion);
            recNode.AddValue("loopPlayback", rec.LoopPlayback);
            recNode.AddValue("loopIntervalSeconds", rec.LoopIntervalSeconds.ToString("R", CultureInfo.InvariantCulture));
            if (!double.IsNaN(rec.LoopStartUT))
                recNode.AddValue("loopStartUT", rec.LoopStartUT.ToString("R", CultureInfo.InvariantCulture));
            if (!double.IsNaN(rec.LoopEndUT))
                recNode.AddValue("loopEndUT", rec.LoopEndUT.ToString("R", CultureInfo.InvariantCulture));
            if (rec.LoopAnchorVesselId != 0)
                recNode.AddValue("loopAnchorPid", rec.LoopAnchorVesselId.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(rec.LoopAnchorBodyName))
                recNode.AddValue("loopAnchorBodyName", rec.LoopAnchorBodyName);
            if (rec.LoopTimeUnit != LoopTimeUnit.Sec)
                recNode.AddValue("loopTimeUnit", rec.LoopTimeUnit.ToString());
        }

        private static void SaveRecordingBudgetAndRewindMetadata(ConfigNode recNode, Recording rec)
        {
            if (rec.PreLaunchFunds != 0)
                recNode.AddValue("preLaunchFunds", rec.PreLaunchFunds.ToString("R", CultureInfo.InvariantCulture));
            if (rec.PreLaunchScience != 0)
                recNode.AddValue("preLaunchScience", rec.PreLaunchScience.ToString("R", CultureInfo.InvariantCulture));
            if (rec.PreLaunchReputation != 0)
                recNode.AddValue("preLaunchRep", rec.PreLaunchReputation.ToString("R", CultureInfo.InvariantCulture));

            // Rewind save metadata
            if (!string.IsNullOrEmpty(rec.RewindSaveFileName))
            {
                recNode.AddValue("rewindSave", rec.RewindSaveFileName);
                recNode.AddValue("rewindResFunds", rec.RewindReservedFunds.ToString("R", CultureInfo.InvariantCulture));
                recNode.AddValue("rewindResSci", rec.RewindReservedScience.ToString("R", CultureInfo.InvariantCulture));
                recNode.AddValue("rewindResRep", rec.RewindReservedRep.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        private static void SaveRecordingGroupAndSegmentMetadata(ConfigNode recNode, Recording rec)
        {
            // UI grouping tags (multi-group membership)
            if (rec.RecordingGroups != null)
                for (int g = 0; g < rec.RecordingGroups.Count; g++)
                    recNode.AddValue("recordingGroup", rec.RecordingGroups[g]);

            // Atmosphere segment metadata (only if set, saves space)
            if (!string.IsNullOrEmpty(rec.SegmentPhase))
                recNode.AddValue("segmentPhase", rec.SegmentPhase);
            if (!string.IsNullOrEmpty(rec.SegmentBodyName))
                recNode.AddValue("segmentBodyName", rec.SegmentBodyName);
        }

        private static void SaveRecordingLocationAndTerminalMetadata(ConfigNode recNode, Recording rec)
        {
            // Location context (Phase 10)
            if (!string.IsNullOrEmpty(rec.StartBodyName))
                recNode.AddValue("startBodyName", rec.StartBodyName);
            if (!string.IsNullOrEmpty(rec.StartBiome))
                recNode.AddValue("startBiome", rec.StartBiome);
            if (!string.IsNullOrEmpty(rec.StartSituation))
                recNode.AddValue("startSituation", rec.StartSituation);
            if (!string.IsNullOrEmpty(rec.EndBiome))
                recNode.AddValue("endBiome", rec.EndBiome);
            if (!string.IsNullOrEmpty(rec.LaunchSiteName))
                recNode.AddValue("launchSiteName", rec.LaunchSiteName);
            if (rec.EndpointPhase != RecordingEndpointPhase.Unknown)
                recNode.AddValue("endpointPhase", ((int)rec.EndpointPhase).ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(rec.EndpointBodyName))
                recNode.AddValue("endpointBodyName", rec.EndpointBodyName);

            // Terminal orbit fields (only when Orbiting or SubOrbital)
            if (!string.IsNullOrEmpty(rec.TerminalOrbitBody))
            {
                recNode.AddValue("tOrbInc", rec.TerminalOrbitInclination.ToString("R", CultureInfo.InvariantCulture));
                recNode.AddValue("tOrbEcc", rec.TerminalOrbitEccentricity.ToString("R", CultureInfo.InvariantCulture));
                recNode.AddValue("tOrbSma", rec.TerminalOrbitSemiMajorAxis.ToString("R", CultureInfo.InvariantCulture));
                recNode.AddValue("tOrbLan", rec.TerminalOrbitLAN.ToString("R", CultureInfo.InvariantCulture));
                recNode.AddValue("tOrbArgPe", rec.TerminalOrbitArgumentOfPeriapsis.ToString("R", CultureInfo.InvariantCulture));
                recNode.AddValue("tOrbMna", rec.TerminalOrbitMeanAnomalyAtEpoch.ToString("R", CultureInfo.InvariantCulture));
                recNode.AddValue("tOrbEpoch", rec.TerminalOrbitEpoch.ToString("R", CultureInfo.InvariantCulture));
                recNode.AddValue("tOrbBody", rec.TerminalOrbitBody);
            }
        }

        private static void SaveRecordingPlaybackFlags(ConfigNode recNode, Recording rec)
        {
            if (!rec.PlaybackEnabled)
                recNode.AddValue("playbackEnabled", rec.PlaybackEnabled.ToString());
            if (rec.Hidden)
                recNode.AddValue("hidden", rec.Hidden.ToString());
        }

        private static void SaveRecordingManifestMetadata(ConfigNode recNode, Recording rec)
        {
            // Resource manifests (Phase 11)
            RecordingStore.SerializeResourceManifest(recNode, rec);

            // Inventory manifests (Phase 11)
            RecordingStore.SerializeInventoryManifest(recNode, rec);
            if (rec.StartInventorySlots != 0)
                recNode.AddValue("startInvSlots", rec.StartInventorySlots.ToString(CultureInfo.InvariantCulture));
            if (rec.EndInventorySlots != 0)
                recNode.AddValue("endInvSlots", rec.EndInventorySlots.ToString(CultureInfo.InvariantCulture));

            // Crew manifests (Phase 11)
            RecordingStore.SerializeCrewManifest(recNode, rec);

            // Dock target vessel PID (Phase 11)
            if (rec.DockTargetVesselPid != 0)
                recNode.AddValue("dockTargetPid", rec.DockTargetVesselPid.ToString(CultureInfo.InvariantCulture));

            // Logistics route proof metadata (additive; missing node = no proof data)
            RecordingStore.SerializeRouteProofMetadata(recNode, rec);
        }

        /// <summary>
        /// Loads versioned recording metadata and ghost-geometry metadata.
        /// Missing fields are treated as old-format recordings.
        /// <para>
        /// <b>Test-only.</b> Production recording-tree load paths must use
        /// <see cref="RecordingTree.LoadRecordingFrom"/>, which enforces the
        /// current format gate (see
        /// <c>RecordingTreeRecordCodec</c>) before hydrating metadata. This
        /// helper bypasses that gate by design so unit tests can exercise
        /// metadata serialization in isolation. Do not add production callers;
        /// the explicit name suffix is the only thing preventing accidental
        /// reuse.
        /// </para>
        /// </summary>
        internal static void LoadRecordingMetadataForTests(ConfigNode recNode, Recording rec)
        {
            LoadRecordingIdentityAndLoopMetadata(recNode, rec);

            LoadRecordingBudgetAndRewindMetadata(recNode, rec);
            LoadRecordingGroupAndSegmentMetadata(recNode, rec);

            LoadRecordingLocationAndTerminalMetadata(recNode, rec);
            LoadRecordingPlaybackFlags(recNode, rec);

            LoadRecordingManifestMetadata(recNode, rec);

            RecordingEndpointResolver.BackfillEndpointDecision(rec, "ParsekScenario.LoadRecordingMetadata");
        }

        private static void LoadRecordingIdentityAndLoopMetadata(ConfigNode recNode, Recording rec)
        {
            string id = recNode.GetValue("recordingId");
            if (!string.IsNullOrEmpty(id))
                rec.RecordingId = id;

            string formatVersionStr = recNode.GetValue("recordingFormatVersion");
            if (formatVersionStr != null)
            {
                int formatVersion;
                if (int.TryParse(formatVersionStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out formatVersion))
                    rec.RecordingFormatVersion = formatVersion;
                else
                    rec.RecordingFormatVersion = 0;
            }
            else
            {
                rec.RecordingFormatVersion = 0;
            }

            string loopPlaybackStr = recNode.GetValue("loopPlayback");
            if (loopPlaybackStr != null)
            {
                bool loopPlayback;
                if (bool.TryParse(loopPlaybackStr, out loopPlayback))
                    rec.LoopPlayback = loopPlayback;
            }

            string loopStartUTStr = recNode.GetValue("loopStartUT");
            if (loopStartUTStr != null)
            {
                double loopStartUT;
                if (double.TryParse(loopStartUTStr, NumberStyles.Float, CultureInfo.InvariantCulture, out loopStartUT))
                    rec.LoopStartUT = loopStartUT;
            }

            string loopEndUTStr = recNode.GetValue("loopEndUT");
            if (loopEndUTStr != null)
            {
                double loopEndUT;
                if (double.TryParse(loopEndUTStr, NumberStyles.Float, CultureInfo.InvariantCulture, out loopEndUT))
                    rec.LoopEndUT = loopEndUT;
            }

            string loopIntervalStr = recNode.GetValue("loopIntervalSeconds");
            if (loopIntervalStr != null)
            {
                double loopIntervalSeconds;
                if (double.TryParse(loopIntervalStr, NumberStyles.Float, CultureInfo.InvariantCulture, out loopIntervalSeconds))
                {
                    rec.LoopIntervalSeconds = loopIntervalSeconds;
                }
            }

            string loopTimeUnitStr = recNode.GetValue("loopTimeUnit");
            if (loopTimeUnitStr != null)
            {
                LoopTimeUnit loopTimeUnit;
                if (System.Enum.TryParse(loopTimeUnitStr, out loopTimeUnit))
                    rec.LoopTimeUnit = loopTimeUnit;
            }

            string loopAnchorPidStr = recNode.GetValue("loopAnchorPid");
            if (loopAnchorPidStr != null)
            {
                uint loopAnchorPid;
                if (uint.TryParse(loopAnchorPidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out loopAnchorPid))
                    rec.LoopAnchorVesselId = loopAnchorPid;
            }

            string loopAnchorBodyNameStr = recNode.GetValue("loopAnchorBodyName");
            if (!string.IsNullOrEmpty(loopAnchorBodyNameStr))
                rec.LoopAnchorBodyName = loopAnchorBodyNameStr;
        }

        private static void LoadRecordingBudgetAndRewindMetadata(ConfigNode recNode, Recording rec)
        {
            string preLaunchFundsStr = recNode.GetValue("preLaunchFunds");
            if (preLaunchFundsStr != null)
            {
                double preLaunchFunds;
                if (double.TryParse(preLaunchFundsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out preLaunchFunds))
                    rec.PreLaunchFunds = preLaunchFunds;
            }
            string preLaunchScienceStr = recNode.GetValue("preLaunchScience");
            if (preLaunchScienceStr != null)
            {
                double preLaunchScience;
                if (double.TryParse(preLaunchScienceStr, NumberStyles.Float, CultureInfo.InvariantCulture, out preLaunchScience))
                    rec.PreLaunchScience = preLaunchScience;
            }
            string preLaunchRepStr = recNode.GetValue("preLaunchRep");
            if (preLaunchRepStr != null)
            {
                float preLaunchRep;
                if (float.TryParse(preLaunchRepStr, NumberStyles.Float, CultureInfo.InvariantCulture, out preLaunchRep))
                    rec.PreLaunchReputation = preLaunchRep;
            }

            // Rewind save metadata
            rec.RewindSaveFileName = recNode.GetValue("rewindSave");
            string rewindFundsStr = recNode.GetValue("rewindResFunds");
            if (rewindFundsStr != null)
            {
                double rewindFunds;
                if (double.TryParse(rewindFundsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out rewindFunds))
                    rec.RewindReservedFunds = rewindFunds;
            }
            string rewindSciStr = recNode.GetValue("rewindResSci");
            if (rewindSciStr != null)
            {
                double rewindSci;
                if (double.TryParse(rewindSciStr, NumberStyles.Float, CultureInfo.InvariantCulture, out rewindSci))
                    rec.RewindReservedScience = rewindSci;
            }
            string rewindRepStr = recNode.GetValue("rewindResRep");
            if (rewindRepStr != null)
            {
                float rewindRep;
                if (float.TryParse(rewindRepStr, NumberStyles.Float, CultureInfo.InvariantCulture, out rewindRep))
                    rec.RewindReservedRep = rewindRep;
            }
        }

        private static void LoadRecordingGroupAndSegmentMetadata(ConfigNode recNode, Recording rec)
        {
            // UI grouping tags (multi-group membership, backward compat with single value)
            string[] groups = recNode.GetValues("recordingGroup");
            if (groups != null && groups.Length > 0)
            {
                for (int g = 0; g < groups.Length; g++)
                    groups[g] = Recording.ResolveLocalizedName(groups[g]);
                rec.RecordingGroups = new List<string>(groups);
            }

            // Atmosphere segment metadata
            rec.SegmentPhase = recNode.GetValue("segmentPhase");
            rec.SegmentBodyName = recNode.GetValue("segmentBodyName");
        }

        private static void LoadRecordingLocationAndTerminalMetadata(ConfigNode recNode, Recording rec)
        {
            // Location context (Phase 10) — null if missing (legacy recordings)
            rec.StartBodyName = recNode.GetValue("startBodyName");
            rec.StartBiome = recNode.GetValue("startBiome");
            rec.StartSituation = recNode.GetValue("startSituation");
            rec.EndBiome = recNode.GetValue("endBiome");
            rec.LaunchSiteName = recNode.GetValue("launchSiteName");
            string endpointPhaseStr = recNode.GetValue("endpointPhase");
            if (endpointPhaseStr != null)
            {
                int endpointPhaseInt;
                if (int.TryParse(endpointPhaseStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out endpointPhaseInt)
                    && Enum.IsDefined(typeof(RecordingEndpointPhase), endpointPhaseInt))
                    rec.EndpointPhase = (RecordingEndpointPhase)endpointPhaseInt;
            }
            rec.EndpointBodyName = recNode.GetValue("endpointBodyName");
            // Terminal orbit fields
            string tOrbBody = recNode.GetValue("tOrbBody");
            if (!string.IsNullOrEmpty(tOrbBody))
            {
                rec.TerminalOrbitBody = tOrbBody;
                double.TryParse(recNode.GetValue("tOrbInc"), NumberStyles.Float, CultureInfo.InvariantCulture, out rec.TerminalOrbitInclination);
                double.TryParse(recNode.GetValue("tOrbEcc"), NumberStyles.Float, CultureInfo.InvariantCulture, out rec.TerminalOrbitEccentricity);
                double.TryParse(recNode.GetValue("tOrbSma"), NumberStyles.Float, CultureInfo.InvariantCulture, out rec.TerminalOrbitSemiMajorAxis);
                double.TryParse(recNode.GetValue("tOrbLan"), NumberStyles.Float, CultureInfo.InvariantCulture, out rec.TerminalOrbitLAN);
                double.TryParse(recNode.GetValue("tOrbArgPe"), NumberStyles.Float, CultureInfo.InvariantCulture, out rec.TerminalOrbitArgumentOfPeriapsis);
                double.TryParse(recNode.GetValue("tOrbMna"), NumberStyles.Float, CultureInfo.InvariantCulture, out rec.TerminalOrbitMeanAnomalyAtEpoch);
                double.TryParse(recNode.GetValue("tOrbEpoch"), NumberStyles.Float, CultureInfo.InvariantCulture, out rec.TerminalOrbitEpoch);
            }
        }

        private static void LoadRecordingPlaybackFlags(ConfigNode recNode, Recording rec)
        {
            string playbackEnabledStr = recNode.GetValue("playbackEnabled");
            if (playbackEnabledStr != null)
            {
                bool playbackEnabled;
                if (bool.TryParse(playbackEnabledStr, out playbackEnabled))
                    rec.PlaybackEnabled = playbackEnabled;
            }
            string hiddenStr = recNode.GetValue("hidden");
            if (hiddenStr != null)
            {
                bool hidden;
                if (bool.TryParse(hiddenStr, out hidden))
                    rec.Hidden = hidden;
            }
        }

        private static void LoadRecordingManifestMetadata(ConfigNode recNode, Recording rec)
        {
            // Resource manifests (Phase 11)
            RecordingStore.DeserializeResourceManifest(recNode, rec);

            // Inventory manifests (Phase 11)
            RecordingStore.DeserializeInventoryManifest(recNode, rec);
            string startInvSlotsStr = recNode.GetValue("startInvSlots");
            if (startInvSlotsStr != null)
            {
                int startInvSlots;
                if (int.TryParse(startInvSlotsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out startInvSlots))
                    rec.StartInventorySlots = startInvSlots;
            }
            string endInvSlotsStr = recNode.GetValue("endInvSlots");
            if (endInvSlotsStr != null)
            {
                int endInvSlots;
                if (int.TryParse(endInvSlotsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out endInvSlots))
                    rec.EndInventorySlots = endInvSlots;
            }

            // Crew manifests (Phase 11)
            RecordingStore.DeserializeCrewManifest(recNode, rec);

            // Dock target vessel PID (Phase 11)
            string dockTargetPidStr = recNode.GetValue("dockTargetPid");
            if (dockTargetPidStr != null)
            {
                uint dockTargetPid;
                if (uint.TryParse(dockTargetPidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out dockTargetPid))
                    rec.DockTargetVesselPid = dockTargetPid;
            }

            // Logistics route proof metadata (additive; missing node = no proof data)
            RecordingStore.DeserializeRouteProofMetadata(recNode, rec);
        }

        /// <summary>
        /// Returns true if OnSave should warn about a save folder mismatch.
        /// This detects when HighLogic.SaveFolder has changed since the scenario
        /// was loaded, which could cause file writes to target the wrong save.
        /// </summary>
        internal static bool IsSaveFolderMismatch(string scenarioFolder, string currentFolder)
        {
            return !string.IsNullOrEmpty(scenarioFolder) && currentFolder != scenarioFolder;
        }

        #region Vessel Lifecycle Events

        /// <summary>
        /// Pure predicate: should an outside-Flight auto-commit (autoMerge ON)
        /// route through the dialog's full-fidelity <see cref="MergeDialog.MergeCommit"/>
        /// (spawn-at-end preserved) rather than the lightweight ghost-only commit?
        ///
        /// <para>True only when the silent commit is safe for surviving vessels and
        /// carries no irreversible-timeline risk: autoMerge is on, the pending tree
        /// is a completed-flight <see cref="PendingTreeState.Finalized"/> candidate
        /// (never a Limbo resume-stash), no re-fly session is active (a silent
        /// MergeCommit would otherwise supersede it — kept dialog/journal-gated),
        /// and the destination is a real scene (not MAINMENU, where the game is
        /// unloading and spawn-at-end never runs). See
        /// docs/dev/plans/silent-full-fidelity-autocommit.md.</para>
        /// </summary>
        internal static bool ShouldSilentFullFidelityCommit(
            bool isAutoMerge,
            PendingTreeState pendingState,
            bool reFlyActive,
            GameScenes loadedScene)
        {
            return isAutoMerge
                && pendingState == PendingTreeState.Finalized
                && !reFlyActive
                && loadedScene != GameScenes.MAINMENU;
        }

        /// <summary>
        /// Silent auto-commit of the current pending tree on an outside-Flight
        /// OnLoad path. Routes through the dialog's full-fidelity
        /// <see cref="MergeDialog.MergeCommit"/> (spawn-at-end + resources
        /// preserved, no popup) when <see cref="ShouldSilentFullFidelityCommit"/>
        /// qualifies, otherwise falls back to the lightweight ghost-only commit
        /// (re-fly / Limbo / MAINMENU). Shared by the warm scene-change fallback
        /// and the cold-load pending-outside-flight paths.
        /// </summary>
        private static void AutoCommitPendingTreeOutsideFlight(string context)
        {
            if (!RecordingStore.HasPendingTree)
            {
                ParsekLog.Verbose("Scenario",
                    $"AutoCommitPendingTreeOutsideFlight ({context}): no pending tree — nothing to commit");
                return;
            }

            var pt = RecordingStore.PendingTree;
            var scenario = ParsekScenario.Instance;
            bool reFlyActive =
                !object.ReferenceEquals(null, scenario)
                && scenario.ActiveReFlySessionMarker != null;

            // Hardening: this runs inside ParsekScenario.OnLoad, whose top-level catch
            // rethrows (an OnLoad abort has historically wiped the persistent index). A
            // commit failure must not take down the rest of OnLoad — Error-log and leave
            // the tree stashed so the next load retries, instead of propagating.
            try
            {
                if (ShouldSilentFullFidelityCommit(
                        IsAutoMerge,
                        RecordingStore.PendingTreeStateValue,
                        reFlyActive,
                        HighLogic.LoadedScene))
                {
                    var decisions = MergeDialog.BuildDefaultVesselDecisions(pt);
                    int spawnCount = 0;
                    foreach (var v in decisions.Values)
                        if (v) spawnCount++;
                    ParsekLog.Info("Scenario",
                        $"Silent full-fidelity auto-commit ({context}): tree='{pt.TreeName}' " +
                        $"recordings={pt.Recordings.Count} spawnable={spawnCount}");
                    // MergeCommit runs the full dialog-commit sequence: ApplyVesselDecisions
                    // (keeps spawnable-leaf snapshots), CommitPendingTree + MarkTreeAsApplied,
                    // RunOptimizationPass, NotifyLedgerTreeCommitted, crew swap, and posts its
                    // own screen message. Its M1 guard (pt == RecordingStore.PendingTree) holds.
                    //
                    // refreshQuicksaveAfterCommit: false — we run inside OnLoad, so a
                    // GamePersistence.SaveGame here would re-enter OnSave mid-load and
                    // snapshot before the OnLoad ledger recalc. The commit is durable via
                    // the next normal OnSave (matching the old ghost-only auto-commit,
                    // which never refreshed the quicksave). Also lets the OnLoad recalc
                    // (later this frame) patch the just-committed tree's ledger actions.
                    MergeDialog.MergeCommit(pt, decisions, spawnCount,
                        refreshQuicksaveAfterCommit: false);
                }
                else
                {
                    string reason = !IsAutoMerge ? "not-automerge"
                        : reFlyActive ? "re-fly-active"
                        : RecordingStore.PendingTreeStateValue != PendingTreeState.Finalized
                            ? $"state={RecordingStore.PendingTreeStateValue}"
                        : "mainmenu";
                    AutoCommitTreeGhostOnly(pt);
                    CommitPendingTreeAsApplied(pt);
                    LedgerOrchestrator.NotifyLedgerTreeCommitted(pt);
                    ScreenMessages.PostScreenMessage("[Parsek] Tree recording committed to timeline", 5f);
                    RecordingStore.RunOptimizationPass();
                    ParsekLog.Info("Scenario",
                        $"Ghost-only auto-commit ({context}, reason={reason}): tree='{pt.TreeName}'");
                }
            }
            catch (System.Exception ex)
            {
                ParsekLog.Error("Scenario",
                    $"AutoCommitPendingTreeOutsideFlight ({context}) threw {ex.GetType().Name}: " +
                    $"{ex.Message} — leaving tree '{pt?.TreeName}' stashed for retry on next load");
            }
        }

        /// <summary>
        /// Prepares all recordings in a pending tree for ghost-only commit (no vessel spawn).
        /// Nulls vessel snapshot and unreserves crew. Call RecordingStore.CommitPendingTree() after this.
        /// </summary>
        private static void AutoCommitTreeGhostOnly(RecordingTree tree)
        {
            foreach (var rec in tree.Recordings.Values)
            {
                CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                rec.VesselSnapshot = null;
            }
            ParsekLog.Info("Scenario", $"Auto-commit tree ghost-only: tree '{tree.Id}' " +
                $"({tree.Recordings.Count} recordings)");
        }

        private void OnVesselRecoveryProcessing(
            ProtoVessel pv,
            KSP.UI.Screens.MissionRecoveryDialog recoveryDialog,
            float recoveryFactor)
        {
            if (pv == null) return;
            if (GhostMapPresence.IsGhostMapVessel(pv.persistentId)) return;
            if (RewindContext.IsRewinding) return;
            if (string.IsNullOrEmpty(pv.vesselName)) return;

            double now = Planetarium.GetUniversalTime();
            bool hasFundsEarned = recoveryDialog != null;
            double fundsEarned = hasFundsEarned ? recoveryDialog.fundsEarned : double.NaN;
            double beforeMissionFunds = hasFundsEarned ? recoveryDialog.beforeMissionFunds : double.NaN;
            double totalFunds = hasFundsEarned ? recoveryDialog.totalFunds : double.NaN;

            RecoveryPayoutContext context = RecoveryPayoutContextStore.Remember(
                pv.persistentId,
                pv.vesselName,
                pv.vesselType,
                now,
                recoveryFactor,
                hasFundsEarned,
                fundsEarned,
                beforeMissionFunds,
                totalFunds);

            if (context == null)
                return;

            ParsekLog.Verbose("Scenario",
                $"Recovery processing captured: {context.Identity.FormatForLog()} " +
                $"pid={pv.persistentId} vesselType={pv.vesselType} " +
                $"ut={now.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"fundsEarned={(context.HasFundsEarned ? fundsEarned.ToString("F1", CultureInfo.InvariantCulture) : "(unknown)")} " +
                $"before={(hasFundsEarned ? beforeMissionFunds.ToString("F1", CultureInfo.InvariantCulture) : "(unknown)")} " +
                $"total={(hasFundsEarned ? totalFunds.ToString("F1", CultureInfo.InvariantCulture) : "(unknown)")} " +
                $"recoveryFactor={recoveryFactor.ToString("R", CultureInfo.InvariantCulture)}");
        }

        private void OnVesselRecovered(ProtoVessel pv, bool fromTrackingStation)
        {
            if (pv == null) return;
            if (GhostMapPresence.IsGhostMapVessel(pv.persistentId)) return;

            // During rewind, vessels are stripped from the save which fires onVesselRecovered.
            // Ignore these — the recordings must keep their snapshots for ghost playback and spawning.
            if (RewindContext.IsRewinding)
            {
                ParsekLog.Info("Scenario",
                    $"Ignoring recovery of '{pv.vesselName}' during rewind");
                return;
            }

            RecoveredVesselIdentity identity = RecoveredVesselIdentity.FromRawName(pv.vesselName);
            if (!identity.HasName) return;

            double now = Planetarium.GetUniversalTime();
            RecoveryPayoutContextStore.TryFind(
                pv.persistentId,
                identity,
                now,
                out RecoveryPayoutContext payoutContext);

            bool updated = UpdateRecordingsForTerminalEvent(identity, TerminalState.Recovered, now);
            if (updated)
                ParsekLog.Info("Scenario", $"Vessel '{identity.DisplayName}' recovered — recording(s) updated with Recovered terminal state");

            // #444: when recovery happens outside the Flight scene (tracking station or
            // post-flight summary at KSC), KSP's FundsChanged(VesselRecovery) event lies
            // between the committed recording's points window — CreateVesselCostActions
            // never sees it and the funds get silently dropped from the ledger. Route the
            // payout into the ledger as a real-time FundsEarning(Recovery) action tagged
            // with the matching committed recording (or null for non-Parsek vessels).
            // In-flight recovery is already covered by the terminal-state path above:
            // UpdateRecordingsForTerminalEvent flips the live recording to Recovered, and
            // the later commit now pairs the FundsChanged(VesselRecovery) event near the
            // recording end UT. Only patch immediately when no pending-tree recording
            // still owns this vessel; otherwise the commit-time path should emit the
            // recovery action exactly once.
            if (ShouldPatchRecoveryFundsOutsideFlight(HighLogic.LoadedScene, identity))
                LedgerOrchestrator.OnVesselRecoveryFunds(
                    now,
                    identity,
                    fromTrackingStation,
                    pv.vesselType,
                    payoutContext);
        }

        private void OnVesselTerminated(ProtoVessel pv)
        {
            if (pv == null) return;
            if (GhostMapPresence.IsGhostMapVessel(pv.persistentId)) return;
            if (RewindContext.IsRewinding) return;
            RecoveredVesselIdentity identity = RecoveredVesselIdentity.FromRawName(pv.vesselName);
            if (!identity.HasName) return;

            double now = Planetarium.GetUniversalTime();
            // onVesselTerminated also fires after onVesselRecovered for the same vessel.
            // The guard in UpdateRecordingsForTerminalEvent prevents overwriting Recovered with Destroyed.
            bool updated = UpdateRecordingsForTerminalEvent(identity, TerminalState.Destroyed, now);
            if (updated)
                ParsekLog.Info("Scenario", $"Vessel '{identity.DisplayName}' terminated — recording(s) updated with Destroyed terminal state");
        }

        /// <summary>
        /// Finds recordings matching the given vessel name and updates their terminal state.
        /// Checks pending tree recordings.
        /// Recovered/Destroyed can overwrite situation-based terminal states (Orbiting, Landed, etc.)
        /// that were set by OnSceneChangeRequested. Only prevents Destroyed from overwriting Recovered
        /// (onVesselTerminated fires after onVesselRecovered for the same vessel).
        /// </summary>
        internal static bool UpdateRecordingsForTerminalEvent(string vesselName, TerminalState state, double ut)
        {
            return UpdateRecordingsForTerminalEvent(
                RecoveredVesselIdentity.FromRawName(vesselName),
                state,
                ut);
        }

        internal static bool UpdateRecordingsForTerminalEvent(
            RecoveredVesselIdentity identity,
            TerminalState state,
            double ut)
        {
            bool anyUpdated = false;

            // Check pending tree recordings
            if (RecordingStore.HasPendingTree)
            {
                foreach (var rec in RecordingStore.PendingTree.Recordings.Values)
                {
                    if (MatchesVessel(rec, identity) && CanOverwriteTerminalState(rec.TerminalStateValue, state))
                    {
                        rec.TerminalStateValue = state;
                        rec.ExplicitEndUT = ut;
                        CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                        rec.VesselSnapshot = null;
                        anyUpdated = true;
                        ParsekLog.Verbose("Scenario", $"Updated pending tree recording '{rec.VesselName}' with {state}");
                    }
                }
            }

            // Committed recordings are never modified by terminal events. Recovery or
            // destruction of a real vessel (whether spawned by Parsek or pre-existing) must
            // not alter frozen recording data — snapshot, terminal state, crew, or EndUT.
            // Name-based matching is ambiguous (multiple recordings share vessel names) and
            // any mutation persists through reverts, permanently preventing re-spawn.

            return anyUpdated;
        }

        /// <summary>
        /// Returns true when an outside-FLIGHT recovery should be patched into the
        /// ledger immediately. Pending-tree recordings that still own the vessel are
        /// handled at commit time by CreateVesselCostActions, which now pairs the
        /// FundsChanged(VesselRecovery) event near the recording end UT.
        /// </summary>
        internal static bool ShouldPatchRecoveryFundsOutsideFlight(GameScenes scene, string vesselName)
        {
            return ShouldPatchRecoveryFundsOutsideFlight(
                scene,
                RecoveredVesselIdentity.FromRawName(vesselName));
        }

        internal static bool ShouldPatchRecoveryFundsOutsideFlight(
            GameScenes scene,
            RecoveredVesselIdentity identity)
        {
            return scene != GameScenes.FLIGHT &&
                   !HasPendingLedgerRecordingForVessel(identity);
        }

        /// <summary>
        /// Returns true when a pending-tree recording still owns the named vessel and
        /// will later contribute ledger actions on commit.
        /// </summary>
        internal static bool HasPendingLedgerRecordingForVessel(string vesselName)
        {
            return HasPendingLedgerRecordingForVessel(
                RecoveredVesselIdentity.FromRawName(vesselName));
        }

        internal static bool HasPendingLedgerRecordingForVessel(
            RecoveredVesselIdentity identity)
        {
            if (!identity.HasName || !RecordingStore.HasPendingTree)
                return false;

            foreach (var rec in RecordingStore.PendingTree.Recordings.Values)
            {
                if (rec == null || rec.IsGhostOnly)
                    continue;
                if (MatchesVessel(rec, identity))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Determines whether a new terminal state can overwrite the existing one.
        /// Recovered/Destroyed can overwrite null or situation-based states (Orbiting, Landed, etc.).
        /// Destroyed cannot overwrite Recovered (onVesselTerminated fires after onVesselRecovered).
        /// </summary>
        private static bool CanOverwriteTerminalState(TerminalState? existing, TerminalState incoming)
        {
            if (!existing.HasValue) return true;

            // Recovered and Destroyed are "final" — only Recovered blocks further overwrite
            if (existing.Value == TerminalState.Recovered) return false;
            if (existing.Value == TerminalState.Destroyed) return false;

            // Situation-based states (Orbiting, Landed, Splashed, SubOrbital) can be overwritten
            // by Recovered or Destroyed (lifecycle events take precedence over scene-exit situation)
            return true;
        }

        /// <summary>
        /// Checks if a recording matches the given vessel name.
        /// Uses name-based matching (ProtoVessel doesn't expose vessel persistentId directly).
        /// </summary>
        private static bool MatchesVessel(Recording rec, string vesselName)
        {
            return MatchesVessel(rec, RecoveredVesselIdentity.FromRawName(vesselName));
        }

        private static bool MatchesVessel(Recording rec, RecoveredVesselIdentity identity)
        {
            return rec != null &&
                   identity.HasName &&
                   !string.IsNullOrEmpty(rec.VesselName) &&
                   identity.MatchesName(rec.VesselName);
        }

        #endregion

        private static void ScenarioLog(string message)
        {
            const string legacyPrefix = "[Parsek Scenario] ";
            string clean = message ?? "(empty)";
            if (clean.StartsWith(legacyPrefix, StringComparison.Ordinal))
                clean = clean.Substring(legacyPrefix.Length);

            if (clean.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith("WARN:", StringComparison.OrdinalIgnoreCase))
            {
                int idx = clean.IndexOf(':');
                string trimmed = idx >= 0 ? clean.Substring(idx + 1).TrimStart() : clean;
                ParsekLog.Warn("Scenario", trimmed);
                return;
            }

            ParsekLog.Info("Scenario", clean);
        }

        #endregion

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
                initialLoadDone = false;
                committedTreeStateLoaded = false;
                lastSaveFolder = null;
                lastOnSaveScene = GameScenes.MAINMENU;
                lastSceneChangeRequestedUT = -1.0;
                RecordingStore.PendingCleanupPids = null;
                RecordingStore.PendingCleanupNames = null;
                RecordingStore.PendingRevertPreExistingPids = null;
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
            vesselSwitchPending = true;
            // Time.frameCount is monotonic across scene loads within a single
            // KSP session (Unity only resets it on application restart, not on
            // scene change), so the staleness check in OnLoad can rely on the
            // difference between the stamp here and the frame count at
            // scene-load time being a meaningful "frames elapsed" measurement.
            vesselSwitchPendingFrame = UnityEngine.Time.frameCount;
            ParsekLog.Info("Scenario",
                $"Vessel switch detected: '{from?.vesselName}' → '{to?.vesselName}' — " +
                $"next FLIGHT→FLIGHT OnLoad within {VesselSwitchPendingMaxAgeFrames} frames " +
                $"will skip revert strip/cleanup (frame={vesselSwitchPendingFrame})");
        }

        /// <summary>
        /// M4b Phase B2 (plan D11 / OQ7): drop the RAM-only cargo escrow on any
        /// within-game scene change. A multi-stop cycle's in-flight reservation is
        /// recomputed from pending route state on the next
        /// <see cref="RouteOrchestrator.Tick(double)"/> - the B3 C1 dispatchAlready-resume
        /// re-establishes the un-fired windows' hold
        /// (<see cref="RouteOrchestrator.ReEstablishEscrowForUnfiredWindows"/>); the
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

        public void OnDestroy()
        {
            stateRecorder?.Unsubscribe();
            GameEvents.onVesselRecoveryProcessing.Remove(OnVesselRecoveryProcessing);
            GameEvents.onVesselRecovered.Remove(OnVesselRecovered);
            GameEvents.onVesselTerminated.Remove(OnVesselTerminated);
            GameEvents.onVesselSwitching.Remove(OnVesselSwitching);
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
            if (ReferenceEquals(s_instance, this))
                s_instance = null;
        }
    }
}
