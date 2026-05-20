using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// ScenarioModule that persists committed recordings to save games.
    /// Handles OnSave/OnLoad to serialize trajectory data into ConfigNodes.
    /// Also manages crew reservation for deferred vessel spawns.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames,
        GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION, GameScenes.EDITOR)]
    public class ParsekScenario : ScenarioModule
    {
        internal const string RewindSpawnSuppressionReasonSameRecording = "same-recording";
        internal const string RewindSpawnSuppressionReasonSameTreeFutureRecording = "same-tree-future-recording";
        internal const string RewindSpawnSuppressionReasonLegacyUnscoped = "legacy-unscoped";

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
        /// </summary>
        public void BumpSupersedeStateVersion()
        {
            unchecked { SupersedeStateVersion++; }
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
            RecordingStore.DiscardPendingTree();
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
                savePhase = "game-state";
                PersistGameStateAndMilestones(node);
                // Rewind-to-Staging Phase 1 (design sections 5.1-5.9). Persistence
                // only in Phase 1; no behavior wired to these collections yet.
                savePhase = "rewind-staging";
                SaveRewindStagingState(node);

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
            int treeWithTrackSections = 0, treeWithSnapshots = 0;
            for (int t = 0; t < committedTrees.Count; t++)
            {
                var tree = committedTrees[t];

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
                    $"{treeWithTrackSections} with track sections, {treeWithSnapshots} with snapshots");

            SaveActiveTreeIfAny(node);
            SavePendingTreeIfAny(node);

            // Stranded-sidecar diagnostic: if SaveActiveTreeIfAny did not add an in-flight
            // RECORDING_TREE either, we are about to write an .sfs with zero RECORDING_TREE
            // nodes. When the recordings directory still holds live sidecar IDs that warns
            // of a state-management bug — the next OnLoad would read 0 trees and
            // CleanOrphanFiles' safety guard will refuse the deletion (preserving recovery
            // options), but the bug itself needs investigation. This is a diagnostic only;
            // we don't refuse the save (KSP scenario contracts make that fragile).
            int treeNodeCount = node.GetNodes("RECORDING_TREE").Length;
            if (treeNodeCount == 0)
            {
                var diskIds = RecordingStore.CollectSidecarIdsOnDisk();
                if (diskIds.Count > 0)
                {
                    ParsekLog.Warn("Scenario",
                        $"OnSave: writing 0 RECORDING_TREE nodes but disk has {diskIds.Count} stranded sidecar " +
                        $"recording ID(s). Likely state-management bug — sidecars preserved by CleanOrphanFiles " +
                        $"safety guard on next load. Restore from quicksave.sfs or backup if recordings are missing.");
                }
            }
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
                    else
                    {
                        ParsekLog.Verbose("Scenario",
                            $"SavePendingTreeIfAny: skipped pending tree '{pendingTree.TreeName}' " +
                            $"state={pendingState} (only Finalized pending trees are serialized)");
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
            bool preservedDuringActiveRestore)
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
            treeNode.AddValue("isPending", "True");
            if (preservedDuringActiveRestore)
                RecordingStore.MarkSavedPendingTreeDuringActiveRestoreSerializedForSave("SavePendingTreeIfAny");
            else
                RecordingStore.MarkPendingTreeSerializedForSave("SavePendingTreeIfAny");

            ParsekLog.Info("Scenario",
                $"OnSave: wrote PENDING tree '{pendingTree.TreeName}' " +
                (preservedDuringActiveRestore ? "preserved during active-tree restore " : "") +
                $"({pendingRecCount} recording(s), dirty={pendingDirtyCount}, saved={pendingSavedCount}, " +
                $"failed={pendingSaveFailedCount}, committedOverlap={pendingCommittedOverlapCount}, " +
                $"skippedCommittedDirty={pendingSkippedCommittedDirtyCount})");
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

            int rpCount = 0;
            if (RewindPoints != null && RewindPoints.Count > 0)
            {
                var parent = node.AddNode("REWIND_POINTS");
                for (int i = 0; i < RewindPoints.Count; i++)
                {
                    if (RewindPoints[i] == null) continue;
                    RewindPoints[i].SaveInto(parent);
                    rpCount++;
                }
            }

            int supersedeCount = 0;
            if (RecordingSupersedes != null && RecordingSupersedes.Count > 0)
            {
                var parent = node.AddNode("RECORDING_SUPERSEDES");
                for (int i = 0; i < RecordingSupersedes.Count; i++)
                {
                    if (RecordingSupersedes[i] == null) continue;
                    RecordingSupersedes[i].SaveInto(parent);
                    supersedeCount++;
                }
            }

            int retirementCount = 0;
            if (RecordingRewindRetirements != null && RecordingRewindRetirements.Count > 0)
            {
                var parent = node.AddNode("RECORDING_REWIND_RETIREMENTS");
                for (int i = 0; i < RecordingRewindRetirements.Count; i++)
                {
                    if (RecordingRewindRetirements[i] == null) continue;
                    RecordingRewindRetirements[i].SaveInto(parent);
                    retirementCount++;
                }
            }

            int tombCount = 0;
            if (LedgerTombstones != null && LedgerTombstones.Count > 0)
            {
                var parent = node.AddNode("LEDGER_TOMBSTONES");
                for (int i = 0; i < LedgerTombstones.Count; i++)
                {
                    if (LedgerTombstones[i] == null) continue;
                    LedgerTombstones[i].SaveInto(parent);
                    tombCount++;
                }
            }

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
                $"OnSave: rewind-staging persist: rewindPoints={rpCount} supersedes={supersedeCount} " +
                $"rewindRetirements={retirementCount} " +
                $"tombstones={tombCount} marker={markerWritten} journal={journalWritten} " +
                $"switchIntent={intentWritten} switchSegment={segmentWritten}");
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

            ConfigNode rpParent = node.GetNode("REWIND_POINTS");
            if (rpParent != null)
            {
                var entries = rpParent.GetNodes("POINT");
                for (int i = 0; i < entries.Length; i++)
                    RewindPoints.Add(RewindPoint.LoadFrom(entries[i]));
            }
            RecordingsTableUI.ClearAllRewindSlotCanInvokeLogState();

            ConfigNode sParent = node.GetNode("RECORDING_SUPERSEDES");
            if (sParent != null)
            {
                var entries = sParent.GetNodes("ENTRY");
                for (int i = 0; i < entries.Length; i++)
                    RecordingSupersedes.Add(RecordingSupersedeRelation.LoadFrom(entries[i]));
            }

            ConfigNode rParent = node.GetNode("RECORDING_REWIND_RETIREMENTS");
            if (rParent != null)
            {
                var entries = rParent.GetNodes("ENTRY");
                for (int i = 0; i < entries.Length; i++)
                    RecordingRewindRetirements.Add(RecordingRewindRetirement.LoadFrom(entries[i]));
            }

            ConfigNode tParent = node.GetNode("LEDGER_TOMBSTONES");
            if (tParent != null)
            {
                var entries = tParent.GetNodes("ENTRY");
                for (int i = 0; i < entries.Length; i++)
                    LedgerTombstones.Add(LedgerTombstone.LoadFrom(entries[i]));
            }

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
                $"OnLoad: rewind-staging load: rewindPoints={RewindPoints.Count} " +
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

        // Static flag: only load from save once per KSP session.
        // On revert, the launch quicksave has stale data — the in-memory
        // static list is the real source of truth within a session.
        // Reset on main menu transition to prevent stale data leaking between saves.
        private static bool initialLoadDone = false;
        private static string lastSaveFolder = null;
        private static bool budgetDeductionApplied = false;
        private static bool mainMenuHookRegistered = false;

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
                loadPhase = "rewind-staging";
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
                    // Use RecordingStore.PendingCleanupNames as the authoritative source —
                    // the local collection may have been skipped by the guard above.
                    var cleanupNames = RecordingStore.PendingCleanupNames;
                    if (isRevert && cleanupNames != null && cleanupNames.Count > 0)
                    {
                        var flightState = HighLogic.CurrentGame?.flightState;
                        if (flightState != null)
                            StripOrphanedSpawnedVessels(flightState.protoVessels, cleanupNames,
                                skipPrelaunch: true);
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

                                        break;
                                    }
                                }
                            }
                        }
                    }

                    // Reconcile spawn state after all restore + strip operations (#168).
                    // If a recording's SpawnedVesselPersistentId points to a vessel that was
                    // stripped, reset the PID so the vessel can be re-spawned at the correct time.
                    if (isRevert)
                    {
                        var flightStateForReconcile = HighLogic.CurrentGame?.flightState;
                        if (flightStateForReconcile != null)
                            ReconcileSpawnStateAfterStrip(flightStateForReconcile.protoVessels, recordings);
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
                        // PRELAUNCH window before auto-record starts, and their milestone rewards).
                        // UnstashPendingTreeOnRevert only filters recording-tied actions; these
                        // untagged ones would otherwise be re-applied by the recalc below,
                        // overriding stock KSP's revert. loadedUT is the post-revert clock (the
                        // launch UT for Revert-to-Launch, an earlier UT for Revert-to-Assembly),
                        // so strict UT > loadedUT keeps the at-launch rollout spend on a
                        // Revert-to-Launch while refunding it on a Revert-to-Assembly.
                        int prunedOrphans = Ledger.PruneOrphanActionsAfterUT(loadedUT);
                        if (prunedOrphans > 0)
                            ParsekLog.Info("Scenario",
                                $"Revert: pruned {prunedOrphans} untagged ledger action(s) after " +
                                $"loadedUT={loadedUT.ToString("R", CultureInfo.InvariantCulture)} " +
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
                            // Check if commit approval dialog should be shown (#88):
                            // landed/splashed vessel going to KSC or Tracking Station
                            var destScene = RecordingStore.PendingDestinationScene;
                            TerminalState? termState = null;
                            if (RecordingStore.HasPendingTree)
                            {
                                // Find root recording's terminal state from tree
                                Recording rootRec;
                                if (RecordingStore.PendingTree.Recordings.TryGetValue(
                                    RecordingStore.PendingTree.RootRecordingId, out rootRec))
                                    termState = rootRec.TerminalStateValue;
                            }
                            bool showApproval = destScene.HasValue &&
                                GhostPlaybackLogic.ShouldShowCommitApproval(destScene.Value, termState);
                            RecordingStore.PendingDestinationScene = null;

                            // Auto-discard idle-on-pad before commit approval or auto-commit.
                            // Only for Finalized trees (Limbo trees are resume-flow stashes).
                            if (RecordingStore.HasPendingTree
                                && RecordingStore.PendingTreeStateValue == PendingTreeState.Finalized
                                && ParsekFlight.IsTreeIdleOnPad(RecordingStore.PendingTree))
                            {
                                ParsekLog.Info("Scenario", "Idle on pad at scene exit — auto-discarding tree");
                                DiscardPendingTreeAndRecalculate(
                                    "scene-exit idle-on-pad auto-discard");
                            }

                            bool anythingLeft = RecordingStore.HasPendingTree;
                            if (anythingLeft && showApproval && !mergeDialogPending)
                            {
                                // Defer to approval dialog instead of auto-committing
                                mergeDialogPending = true;
                                StartCoroutine(ShowDeferredMergeDialog());
                                ParsekLog.Info("Scenario",
                                    "Commit approval deferred: vessel landed/splashed at KSC/TS exit");
                            }
                            else
                            {
                                // autoMerge ON: auto-commit ghost-only (existing behavior)
                                if (RecordingStore.HasPendingTree)
                                {
                                    AutoCommitTreeGhostOnly(RecordingStore.PendingTree);
                                    var treeToCommit = RecordingStore.PendingTree;
                                    // Resources were applied live during the flight we are now
                                    // exiting (!isRevert is checked upstream at line ~1143), so
                                    // mark applied to disarm the lump-sum replay path — same
                                    // rationale as MergeDialog.MergeCommit (Phase C).
                                    CommitPendingTreeAsApplied(treeToCommit);
                                    LedgerOrchestrator.NotifyLedgerTreeCommitted(treeToCommit);
                                    ScreenMessages.PostScreenMessage("[Parsek] Tree recording committed to timeline", 5f);
                                }
                                RecordingStore.RunOptimizationPass();
                            }
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
                        // autoMerge ON: auto-commit ghost-only
                        if (RecordingStore.HasPendingTree)
                        {
                            var pt = RecordingStore.PendingTree;
                            foreach (var rec in pt.Recordings.Values)
                            {
                                if (rec.VesselSnapshot != null)
                                    CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                                rec.VesselSnapshot = null;
                            }
                            // Esc > Abort Mission → Space Center path: the flight that produced
                            // this pending tree already applied its resources live. Mark applied
                            // to disarm the lump-sum replay path — same rationale as
                            // MergeDialog.MergeCommit (Phase C).
                            CommitPendingTreeAsApplied(pt);
                            LedgerOrchestrator.NotifyLedgerTreeCommitted(pt);
                            ScenarioLog($"[Parsek Scenario] Auto-committed pending tree outside Flight " +
                                $"(scene: {HighLogic.LoadedScene})");
                        }
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

            // Strip ALL vessels matching recording names from flightState.
            // The rewind save was preprocessed to strip the recorded vessel,
            // but KSP's scene transition may reintroduce vessels from the old
            // persistent.sfs. Strip unconditionally — on rewind, every matching
            // vessel is from the future.
            if (allRecordingNames.Count > 0)
            {
                var flightState = HighLogic.CurrentGame?.flightState;
                if (flightState != null)
                    StripOrphanedSpawnedVessels(flightState.protoVessels, allRecordingNames,
                        skipPrelaunch: false);
            }

            // Rewind strip already handled protoVessel cleanup in flightState.
            // Clear pending data so OnFlightReady doesn't re-run with overbroad names
            // that would match freshly-spawned past vessels (bug #134). The revert path's
            // alreadyHasCleanupData guard (line ~352) will see null and collect
            // fresh data from CollectSpawnedVesselInfo() if needed.
            RecordingStore.PendingCleanupPids = null;
            RecordingStore.PendingCleanupNames = null;
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
        /// Registers a one-time hook to reset session state when returning to main menu.
        /// This prevents stale in-memory recordings from leaking into a new save
        /// (e.g., deleting a career and creating a new one with the same name).
        /// </summary>
        private static void RegisterMainMenuHook()
        {
            if (!mainMenuHookRegistered)
            {
                // Wrapped in try-catch: KSP's EvtDelegate constructor can throw
                // NullReferenceException during early scene loads when GameEvents
                // internals aren't fully initialized.
                try
                {
                    GameEvents.onGameSceneLoadRequested.Add(OnMainMenuTransition);
                    mainMenuHookRegistered = true;
                }
                catch (System.NullReferenceException)
                {
                    ParsekLog.Verbose("Scenario",
                        "Main menu hook deferred (GameEvents not ready) — will retry on next OnLoad");
                }
            }
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
                lastSaveFolder = currentSave;
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

        /// <summary>
        /// Truncates a live active-tree recording to the current quickload-resume UT so
        /// post-load recording can continue from the restored timeline without appending
        /// stale samples/events from the pre-load future. Returns true when any payload was
        /// removed or clipped.
        /// </summary>
        internal static bool TrimRecordingPastUT(Recording rec, double cutoffUT)
        {
            if (rec == null || double.IsNaN(cutoffUT) || double.IsInfinity(cutoffUT))
                return false;

            bool mutated = false;

            mutated |= RemoveItemsPastUT(rec.Points, cutoffUT, p => p.ut);
            mutated |= TrimOrbitSegmentsPastUT(rec.OrbitSegments, cutoffUT);
            mutated |= RemoveItemsPastUT(rec.PartEvents, cutoffUT, e => e.ut);
            mutated |= RemoveItemsPastUT(rec.FlagEvents, cutoffUT, e => e.ut);
            mutated |= RemoveItemsPastUT(rec.SegmentEvents, cutoffUT, e => e.ut);
            mutated |= TrimTrackSectionsPastUT(rec.TrackSections, cutoffUT);

            if (!double.IsNaN(rec.ExplicitStartUT) && rec.ExplicitStartUT > cutoffUT)
            {
                rec.ExplicitStartUT = cutoffUT;
                mutated = true;
            }

            if (double.IsNaN(rec.ExplicitEndUT) || rec.ExplicitEndUT > cutoffUT)
            {
                rec.ExplicitEndUT = cutoffUT;
                mutated = true;
            }

            if (mutated)
                rec.MarkFilesDirty();

            return mutated;
        }

        internal static bool TrimRecordingTreePastUT(RecordingTree tree, double cutoffUT)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0
                || double.IsNaN(cutoffUT) || double.IsInfinity(cutoffUT))
            {
                return false;
            }

            bool mutated = false;
            int trimmedCount = 0;
            HashSet<string> futureOnlyIds = CollectFutureOnlyRecordingIds(tree, cutoffUT);
            int recordingCountBeforeTrim = tree.Recordings.Count;
            foreach (Recording rec in tree.Recordings.Values)
            {
                if (TrimRecordingPastUT(rec, cutoffUT))
                {
                    mutated = true;
                    trimmedCount++;
                }
            }

            if (mutated || (futureOnlyIds != null && futureOnlyIds.Count > 0))
            {
                int prunedRecordings = PruneFutureOnlyRecordings(tree, futureOnlyIds);
                int prunedBranchPoints = RemoveEmptyBranchPoints(tree);
                if (prunedRecordings > 0 || prunedBranchPoints > 0)
                    mutated = true;

                tree.RebuildBackgroundMap();
                ParsekLog.Info("Scenario",
                    $"Quickload tree trim: tree='{tree.TreeName}' cutoffUT={cutoffUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"trimmedRecordings={trimmedCount}/{recordingCountBeforeTrim} " +
                    $"prunedFutureRecordings={prunedRecordings} prunedBranchPoints={prunedBranchPoints} " +
                    $"backgroundEntries={tree.BackgroundMap.Count}");
            }

            return mutated;
        }

        /// <summary>
        /// Bug #610: scope of the quickload-resume tail trim. Tree-wide is correct
        /// for F9 quickload — the world rewound, every recording's post-cutoff data
        /// is stale and future-only recordings never existed at the resume UT.
        /// Re-Fly is different: the splice has already restored post-RP recordings
        /// that represent OTHER vessels' continued timelines and the re-flown
        /// vessel's destroyed-fork; tree-wide trimming would clip and prune them.
        /// Only the in-place continuation target (the active rec) needs its tail
        /// trimmed so the recorder can append fresh post-cutoff data without
        /// colliding with the pre-cutoff timeline.
        /// </summary>
        internal enum QuickloadTrimScope
        {
            TreeWide = 0,
            ActiveRecOnly = 1,
        }

        /// <summary>
        /// Picks the trim scope based on whether an active Re-Fly session pins
        /// this tree. Pure function so the decision is unit-testable. The
        /// <paramref name="reason"/> string is appended to the resume-prep log
        /// line so the chosen branch is auditable from KSP.log alone (#610).
        /// </summary>
        internal static QuickloadTrimScope ChooseQuickloadTrimScope(
            string treeId,
            ReFlySessionMarker marker,
            out string reason)
        {
            if (marker == null)
            {
                reason = "no-active-refly-marker";
                return QuickloadTrimScope.TreeWide;
            }
            if (string.IsNullOrEmpty(marker.TreeId))
            {
                reason = $"refly-marker-has-no-treeid sess={marker.SessionId ?? "<no-id>"}";
                return QuickloadTrimScope.TreeWide;
            }
            if (string.IsNullOrEmpty(treeId))
            {
                reason = $"resume-tree-has-no-id markerTree={marker.TreeId}";
                return QuickloadTrimScope.TreeWide;
            }
            if (!string.Equals(marker.TreeId, treeId, StringComparison.Ordinal))
            {
                reason = $"refly-marker-tree-mismatch markerTree={marker.TreeId} resumeTree={treeId} sess={marker.SessionId ?? "<no-id>"}";
                return QuickloadTrimScope.TreeWide;
            }
            reason = $"refly-active sess={marker.SessionId ?? "<no-id>"} markerTree={marker.TreeId} originRec={marker.OriginChildRecordingId ?? "<null>"}";
            return QuickloadTrimScope.ActiveRecOnly;
        }

        private static HashSet<string> CollectFutureOnlyRecordingIds(RecordingTree tree, double cutoffUT)
        {
            HashSet<string> futureOnlyIds = null;
            foreach (KeyValuePair<string, Recording> kvp in tree.Recordings)
            {
                string recordingId = kvp.Key;
                Recording rec = kvp.Value;
                if (rec == null
                    || rec.SidecarLoadFailed
                    || string.Equals(recordingId, tree.ActiveRecordingId, StringComparison.Ordinal)
                    || string.Equals(recordingId, tree.RootRecordingId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (rec.StartUT >= cutoffUT)
                {
                    if (futureOnlyIds == null)
                        futureOnlyIds = new HashSet<string>(StringComparer.Ordinal);
                    futureOnlyIds.Add(recordingId);
                }
            }

            return futureOnlyIds;
        }

        private static int PruneFutureOnlyRecordings(RecordingTree tree, HashSet<string> futureOnlyIds)
        {
            if (tree == null || futureOnlyIds == null || futureOnlyIds.Count == 0)
                return 0;

            int removed = 0;
            foreach (string recordingId in futureOnlyIds)
            {
                if (tree.Recordings.Remove(recordingId))
                    removed++;
            }

            if (removed == 0)
                return 0;

            if (tree.BranchPoints != null)
            {
                for (int i = 0; i < tree.BranchPoints.Count; i++)
                {
                    BranchPoint bp = tree.BranchPoints[i];
                    bp.ParentRecordingIds?.RemoveAll(id => futureOnlyIds.Contains(id));
                    bp.ChildRecordingIds?.RemoveAll(id => futureOnlyIds.Contains(id));
                }
            }

            foreach (Recording rec in tree.Recordings.Values)
            {
                if (rec != null && futureOnlyIds.Contains(rec.ParentRecordingId))
                    rec.ParentRecordingId = null;
            }

            return removed;
        }

        private static int RemoveEmptyBranchPoints(RecordingTree tree)
        {
            if (tree == null || tree.BranchPoints == null || tree.BranchPoints.Count == 0)
                return 0;

            HashSet<string> removedBranchPointIds = null;
            int removed = 0;
            for (int i = tree.BranchPoints.Count - 1; i >= 0; i--)
            {
                BranchPoint bp = tree.BranchPoints[i];
                int parentCount = bp.ParentRecordingIds != null ? bp.ParentRecordingIds.Count : 0;
                int childCount = bp.ChildRecordingIds != null ? bp.ChildRecordingIds.Count : 0;
                if (parentCount > 0 && childCount > 0)
                    continue;

                if (removedBranchPointIds == null)
                    removedBranchPointIds = new HashSet<string>(StringComparer.Ordinal);
                removedBranchPointIds.Add(bp.Id);
                tree.BranchPoints.RemoveAt(i);
                removed++;
            }

            if (removedBranchPointIds == null || removedBranchPointIds.Count == 0)
                return 0;

            foreach (Recording rec in tree.Recordings.Values)
            {
                if (rec == null)
                    continue;

                if (!string.IsNullOrEmpty(rec.ParentBranchPointId)
                    && removedBranchPointIds.Contains(rec.ParentBranchPointId))
                {
                    rec.ParentBranchPointId = null;
                }

                if (!string.IsNullOrEmpty(rec.ChildBranchPointId)
                    && removedBranchPointIds.Contains(rec.ChildBranchPointId))
                {
                    rec.ChildBranchPointId = null;
                }
            }

            return removed;
        }

        private static bool TrimOrbitSegmentsPastUT(List<OrbitSegment> segments, double cutoffUT)
        {
            if (segments == null || segments.Count == 0)
                return false;

            bool mutated = false;
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                OrbitSegment seg = segments[i];
                if (seg.startUT >= cutoffUT)
                {
                    segments.RemoveAt(i);
                    mutated = true;
                    continue;
                }

                if (seg.endUT > cutoffUT)
                {
                    seg.endUT = cutoffUT;
                    segments[i] = seg;
                    mutated = true;
                }
            }

            return mutated;
        }

        private static bool TrimTrackSectionsPastUT(List<TrackSection> sections, double cutoffUT)
        {
            if (sections == null || sections.Count == 0)
                return false;

            bool mutated = false;
            for (int i = sections.Count - 1; i >= 0; i--)
            {
                TrackSection section = sections[i];
                if (section.startUT >= cutoffUT)
                {
                    sections.RemoveAt(i);
                    mutated = true;
                    continue;
                }

                bool sectionMutated = false;
                if (section.endUT > cutoffUT)
                {
                    section.endUT = cutoffUT;
                    sectionMutated = true;
                }

                sectionMutated |= TrimTrackSectionFramesPastUT(ref section, cutoffUT);
                sectionMutated |= TrimTrackSectionCheckpointsPastUT(ref section, cutoffUT);

                bool hasFrames = section.frames != null && section.frames.Count > 0;
                bool hasCheckpoints = section.checkpoints != null && section.checkpoints.Count > 0;
                if (!hasFrames && !hasCheckpoints)
                {
                    sections.RemoveAt(i);
                    mutated = true;
                    continue;
                }

                if (sectionMutated)
                {
                    RecomputeTrimmedTrackSectionMetadata(ref section);
                    sections[i] = section;
                    mutated = true;
                }
            }

            return mutated;
        }

        private static bool TrimTrackSectionFramesPastUT(ref TrackSection section, double cutoffUT)
        {
            if (section.frames == null || section.frames.Count == 0)
                return false;

            int originalCount = section.frames.Count;
            for (int i = section.frames.Count - 1; i >= 0; i--)
            {
                if (section.frames[i].ut > cutoffUT)
                    section.frames.RemoveAt(i);
            }

            if (section.frames.Count == 0)
                section.frames = null;

            int remainingCount = section.frames != null ? section.frames.Count : 0;
            return remainingCount != originalCount;
        }

        private static bool TrimTrackSectionCheckpointsPastUT(ref TrackSection section, double cutoffUT)
        {
            if (section.checkpoints == null || section.checkpoints.Count == 0)
                return false;

            bool mutated = false;
            for (int i = section.checkpoints.Count - 1; i >= 0; i--)
            {
                OrbitSegment checkpoint = section.checkpoints[i];
                if (checkpoint.startUT >= cutoffUT)
                {
                    section.checkpoints.RemoveAt(i);
                    mutated = true;
                    continue;
                }

                if (checkpoint.endUT > cutoffUT)
                {
                    checkpoint.endUT = cutoffUT;
                    section.checkpoints[i] = checkpoint;
                    mutated = true;
                }
            }

            if (section.checkpoints.Count == 0)
                section.checkpoints = null;

            return mutated;
        }

        private static void RecomputeTrimmedTrackSectionMetadata(ref TrackSection section)
        {
            section.sampleRateHz = 0f;
            section.minAltitude = float.NaN;
            section.maxAltitude = float.NaN;

            if (section.frames == null || section.frames.Count == 0)
                return;

            for (int i = 0; i < section.frames.Count; i++)
            {
                float alt = (float)section.frames[i].altitude;
                if (float.IsNaN(section.minAltitude) || alt < section.minAltitude)
                    section.minAltitude = alt;
                if (float.IsNaN(section.maxAltitude) || alt > section.maxAltitude)
                    section.maxAltitude = alt;
            }

            double duration = section.endUT - section.startUT;
            if (duration > 0.0 && section.frames.Count > 1)
                section.sampleRateHz = (float)(section.frames.Count / duration);
        }

        private static bool RemoveItemsPastUT<T>(List<T> items, double cutoffUT, Func<T, double> getUT)
        {
            if (items == null || items.Count == 0)
                return false;

            int originalCount = items.Count;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (getUT(items[i]) > cutoffUT)
                    items.RemoveAt(i);
            }

            return items.Count != originalCount;
        }

        internal static bool ShouldKeepPendingTreeAfterHydrationFailure(
            RecordingTree loadedTree,
            int staleEpochHydrationFailures)
        {
            return staleEpochHydrationFailures > 0
                && loadedTree != null
                && RecordingStore.HasPendingTree
                && RecordingStore.PendingTree != null
                && RecordingStore.PendingTree.Id == loadedTree.Id;
        }

        internal static int RestoreHydrationFailedRecordingsFromPendingTree(RecordingTree loadedTree)
        {
            if (loadedTree == null
                || !RecordingStore.HasPendingTree
                || RecordingStore.PendingTree == null
                || RecordingStore.PendingTree.Id != loadedTree.Id)
            {
                return 0;
            }

            var pendingTree = RecordingStore.PendingTree;
            var failedIds = new List<string>();
            foreach (var kvp in loadedTree.Recordings)
            {
                if (kvp.Value != null && kvp.Value.SidecarLoadFailed)
                    failedIds.Add(kvp.Key);
            }

            if (failedIds.Count == 0)
                return 0;

            int restored = 0;
            int snapshotOnlyRestored = 0;
            for (int i = 0; i < failedIds.Count; i++)
            {
                string recordingId = failedIds[i];
                Recording loadedRec;
                if (!loadedTree.Recordings.TryGetValue(recordingId, out loadedRec) || loadedRec == null)
                    continue;

                Recording pendingRec;
                if (!pendingTree.Recordings.TryGetValue(recordingId, out pendingRec) || pendingRec == null)
                    continue;

                bool snapshotFailure = IsSnapshotHydrationFailure(loadedRec.SidecarLoadFailureReason);
                if (TryRestoreSnapshotStateFromPendingRecording(loadedRec, pendingRec))
                {
                    restored++;
                    snapshotOnlyRestored++;
                    continue;
                }
                if (snapshotFailure)
                    continue;

                Recording restoredRec = Recording.DeepClone(pendingRec);
                RecordingStore.ClearSidecarLoadFailure(restoredRec);
                restoredRec.MarkFilesDirty();
                loadedTree.AddOrReplaceRecording(restoredRec);
                restored++;
            }

            if (restored > 0)
            {
                loadedTree.RebuildBackgroundMap();
                ParsekLog.Warn("Scenario",
                    $"TryRestoreActiveTreeNode: restored {restored} hydration-failed recording(s) " +
                    $"from matching pending tree '{pendingTree.TreeName}' into '{loadedTree.TreeName}'" +
                    (snapshotOnlyRestored > 0
                        ? $" ({snapshotOnlyRestored} snapshot-only, {restored - snapshotOnlyRestored} full)"
                        : ""));
            }

            return restored;
        }

        internal static int RestoreHydrationFailedRecordingsFromCommittedTree(
            RecordingTree loadedTree,
            string activeRecordingId = null)
        {
            if (loadedTree == null || string.IsNullOrEmpty(loadedTree.Id))
                return 0;

            RecordingTree committedTree = FindCommittedTreeById(loadedTree.Id, exclude: loadedTree);
            if (committedTree == null)
                return 0;

            int restored = 0;
            var restoreIds = new List<string>();
            foreach (var kvp in loadedTree.Recordings)
            {
                Recording loadedRec = kvp.Value;
                if (loadedRec == null)
                    continue;

                Recording sourceRec;
                if (!committedTree.Recordings.TryGetValue(kvp.Key, out sourceRec) || sourceRec == null)
                    continue;

                if (!ShouldRestoreHydrationFailureFromCommittedRecording(
                        loadedRec, sourceRec, activeRecordingId))
                    continue;

                restoreIds.Add(kvp.Key);
            }

            for (int i = 0; i < restoreIds.Count; i++)
            {
                string recordingId = restoreIds[i];
                Recording sourceRec;
                if (!committedTree.Recordings.TryGetValue(recordingId, out sourceRec) || sourceRec == null)
                    continue;

                Recording loadedRec;
                if (!loadedTree.Recordings.TryGetValue(recordingId, out loadedRec) || loadedRec == null)
                    continue;

                RestoreCommittedSidecarPayloadIntoActiveTreeRecording(loadedRec, sourceRec);
                restored++;
            }

            if (restored > 0)
            {
                loadedTree.RebuildBackgroundMap();
                ParsekLog.Warn("Scenario",
                    $"RestoreHydrationFailedRecordingsFromCommittedTree: restored {restored} active-tree " +
                    $"recording(s) from committed tree '{committedTree.TreeName}' (id={committedTree.Id})");
            }

            return restored;
        }

        /// <summary>
        /// Load-time topology repair for orphaned optimizer-split first halves.
        ///
        /// <para>
        /// Diagnosed from a 2026-05-02 playtest: a same-vessel chain successor
        /// with <c>ChainIndex=1</c> appeared in the committed tree, but the
        /// matching first-half predecessor had <c>ChainId=null</c> and
        /// <c>ChainIndex=-1</c>. Playback then treats the successor as a fresh
        /// ghost on the chain boundary — engines/FX rebuild and the seamless
        /// handoff is broken. The bad shape is already on disk before any
        /// session that loads it, and
        /// <see cref="SpliceMissingCommittedRecordingsIntoLoadedTree"/> faithfully
        /// copies the broken committed state, so a load-time self-heal is the
        /// right scope: fix the topology, mark dirty, and let the next OnSave
        /// persist the corrected shape.
        /// </para>
        ///
        /// <para>
        /// Conservative criteria: a successor must have
        /// <c>ChainBranch == 0</c>, <c>ChainIndex == 1</c>, a non-empty
        /// <c>ChainId</c>, a non-zero <c>VesselPersistentId</c>, a finite
        /// <c>StartUT</c>, and a non-negative <c>TreeOrder</c>. A candidate
        /// predecessor must live in the same tree (the tree's
        /// <c>Id</c> must be set), share the successor's
        /// <c>VesselPersistentId</c>, have an empty <c>ChainId</c> AND
        /// <c>ChainIndex &lt; 0</c> (so we never overwrite an existing chain
        /// assignment), carry a non-negative <c>TreeOrder</c> strictly less
        /// than the successor's, and end within
        /// <see cref="ChainPredecessorRepairUTEpsilon"/> of the successor's
        /// start. Recordings without an assigned <c>TreeOrder</c> on either
        /// side are rejected: the load-time
        /// <see cref="RecordingTree.RebuildBackgroundMap"/> path always
        /// assigns one before this repair can run, so an unset value at this
        /// point is a sign the data has not been normalized and the safe
        /// move is to skip rather than guess at chain ordering.
        /// </para>
        ///
        /// <para>
        /// Out of scope (gate keeps the repair narrow): orphaned
        /// <c>ChainBranch &gt; 0</c> branches; chains where the surviving
        /// successor's <c>ChainIndex</c> is anything other than <c>1</c>
        /// (e.g. a length-3 chain that lost both <c>[0]</c> and <c>[1]</c>
        /// — only the trailing-most index-1 successor's predecessor can be
        /// rebuilt by this pass).
        /// </para>
        ///
        /// <para>
        /// On match the repair sets the predecessor's <c>ChainId</c>,
        /// <c>ChainIndex = 0</c>, and <c>ChainBranch</c> from the successor
        /// and calls <see cref="Recording.MarkFilesDirty"/> so the next
        /// OnSave rewrites the predecessor's <c>.sfs</c> with the corrected
        /// shape. The repair does NOT itself rebuild the tree's background
        /// map; callers that batch repair with other tree mutations should
        /// rebuild once at the end of their batch (the splice path already
        /// does), and standalone callers must rebuild themselves when the
        /// returned count is non-zero — chain field changes affect
        /// <see cref="RecordingTree.IsBackgroundMapEligible"/>. Tie-break on
        /// equal <c>EndUT</c> gaps: smaller <c>TreeOrder</c> first, then
        /// ordinal <c>RecordingId</c> — fully deterministic.
        /// </para>
        ///
        /// <para>
        /// Idempotent: a second call on a tree the first call already
        /// healed finds no candidate (every former orphan now has
        /// <c>ChainId</c> set, which the candidate gate rejects) and
        /// returns 0.
        /// </para>
        /// </summary>
        internal static int RepairMissingContiguousChainPredecessors(
            RecordingTree tree,
            string context)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return 0;

            int considered = 0;
            int repaired = 0;
            HashSet<string> reservedPredecessorIds = null;

            foreach (var kvp in tree.Recordings)
            {
                Recording successor = kvp.Value;
                if (!IsRepairableMissingChainPredecessorSuccessor(successor))
                    continue;

                considered++;
                Recording predecessor = FindMissingContiguousChainPredecessor(
                    tree,
                    successor,
                    reservedPredecessorIds);
                if (predecessor == null)
                    continue;

                double predecessorEndUT = predecessor.EndUT;
                double gapMs = Math.Abs(predecessorEndUT - successor.StartUT) * 1000.0;

                predecessor.ChainId = successor.ChainId;
                predecessor.ChainIndex = successor.ChainIndex - 1;
                predecessor.ChainBranch = successor.ChainBranch;
                predecessor.MarkFilesDirty();
                if (reservedPredecessorIds == null)
                    reservedPredecessorIds = new HashSet<string>(StringComparer.Ordinal);
                reservedPredecessorIds.Add(predecessor.RecordingId);
                repaired++;

                // Logged BEFORE rebuild so a future log post-mortem sees the
                // exact boundary-UT pair and gap that drove the decision.
                // predecessor.ChainIndex is omitted from the line because
                // the gate forces it to -1 going in and 0 coming out, so
                // the field is tautological; predecessorEndUT + gapMs are
                // the actually-diagnostic numbers when reviewing borderline
                // matches near the 1ms epsilon.
                ParsekLog.Info("Scenario", string.Format(
                    CultureInfo.InvariantCulture,
                    "RepairMissingChainPredecessor: context={0} tree={1} predecessor={2} successor={3} " +
                    "chain={4} predecessorEndUT={5:R} successorStartUT={6:R} gapMs={7:F4} vesselPid={8}",
                    context ?? "unknown",
                    tree.Id ?? "",
                    predecessor.RecordingId ?? "",
                    successor.RecordingId ?? "",
                    successor.ChainId ?? "",
                    predecessorEndUT,
                    successor.StartUT,
                    gapMs,
                    successor.VesselPersistentId));
            }

            if (considered > 0 || repaired > 0)
            {
                ParsekLog.Verbose("Scenario", string.Format(
                    CultureInfo.InvariantCulture,
                    "RepairMissingChainPredecessor summary: context={0} tree={1} considered={2} repaired={3}",
                    context ?? "unknown",
                    tree.Id ?? "",
                    considered,
                    repaired));
            }

            return repaired;
        }

        private static bool IsRepairableMissingChainPredecessorSuccessor(Recording successor)
        {
            return successor != null
                && !string.IsNullOrEmpty(successor.RecordingId)
                && !string.IsNullOrEmpty(successor.ChainId)
                && successor.ChainBranch == 0
                && successor.ChainIndex == 1
                && successor.VesselPersistentId != 0
                && successor.TreeOrder >= 0
                && IsFiniteChainBoundaryUT(successor.StartUT);
        }

        private static Recording FindMissingContiguousChainPredecessor(
            RecordingTree tree,
            Recording successor,
            HashSet<string> reservedPredecessorIds)
        {
            Recording best = null;
            double bestGap = double.MaxValue;
            // Note on outer-loop determinism: tree.Recordings is a Dictionary
            // and its enumeration order is implementation-defined. With the
            // gate's ChainBranch==0 / ChainIndex==1 / non-zero VesselPersistentId
            // requirements, real-world chains have at most one matching
            // successor per (vesselPid, branch) pair, so successor processing
            // order is effectively unique. reservedPredecessorIds prevents
            // a single predecessor being claimed by multiple successors in
            // any pathological multi-successor case.
            foreach (var candidateKvp in tree.Recordings)
            {
                Recording candidate = candidateKvp.Value;
                if (candidate == null || ReferenceEquals(candidate, successor))
                    continue;
                if (string.IsNullOrEmpty(candidate.RecordingId))
                    continue;
                if (reservedPredecessorIds != null
                    && reservedPredecessorIds.Contains(candidate.RecordingId))
                    continue;
                if (!string.IsNullOrEmpty(candidate.ChainId) || candidate.ChainIndex >= 0)
                    continue;
                if (candidate.VesselPersistentId == 0
                    || candidate.VesselPersistentId != successor.VesselPersistentId)
                    continue;
                if (!SameTreeForChainRepair(candidate, successor, tree))
                    continue;
                // Strict TreeOrder gate: both sides must be set (>= 0) and
                // candidate must come strictly before successor. The
                // successor-side check is in
                // IsRepairableMissingChainPredecessorSuccessor; this is the
                // candidate-side counterpart.
                if (candidate.TreeOrder < 0 || candidate.TreeOrder >= successor.TreeOrder)
                    continue;

                double candidateEndUT = candidate.EndUT;
                if (!IsFiniteChainBoundaryUT(candidateEndUT))
                    continue;

                double gap = Math.Abs(candidateEndUT - successor.StartUT);
                if (gap > ChainPredecessorRepairUTEpsilon)
                    continue;

                if (best == null
                    || CompareChainPredecessorCandidates(candidate, gap, best, bestGap) < 0)
                {
                    best = candidate;
                    bestGap = gap;
                }
            }

            return best;
        }

        private static int CompareChainPredecessorCandidates(
            Recording a, double aGap,
            Recording b, double bGap)
        {
            // Smaller boundary-UT gap wins.
            int gapCmp = aGap.CompareTo(bGap);
            if (gapCmp != 0)
                return gapCmp;
            // Tie on gap: lower TreeOrder wins. Both sides have TreeOrder >= 0
            // by the find-loop's strict gate, so no unset-ordering coercion
            // is needed here.
            int orderCmp = a.TreeOrder.CompareTo(b.TreeOrder);
            if (orderCmp != 0)
                return orderCmp;
            // Final tie-break: ordinal RecordingId (always non-empty here —
            // the find-loop guard rejects empty ids).
            return string.CompareOrdinal(a.RecordingId, b.RecordingId);
        }

        private static bool SameTreeForChainRepair(
            Recording candidate, Recording successor, RecordingTree tree)
        {
            // Fail-closed when the tree has no Id: treating two un-treed
            // recordings as same-tree would be a false positive in repair
            // gating. In practice every loaded tree has a non-empty Id by
            // construction; this guard exists so a future code path that
            // somehow constructs a tree without one cannot accidentally
            // pull untreed orphan recordings into a chain.
            string treeId = tree != null ? tree.Id : null;
            if (string.IsNullOrEmpty(treeId))
                return false;
            return string.Equals(candidate.TreeId, treeId, StringComparison.Ordinal)
                && string.Equals(successor.TreeId, treeId, StringComparison.Ordinal);
        }

        private static bool IsFiniteChainBoundaryUT(double ut)
        {
            return !double.IsNaN(ut) && !double.IsInfinity(ut);
        }

        /// <summary>
        /// Bug #601: Re-Fly load preserves post-RP merge tree mutations.
        ///
        /// <para>
        /// The Rewind Point's frozen <c>.sfs</c> snapshots the recording tree at the
        /// moment the RP was authored. If <c>RecordingOptimizer.SplitAtSection</c>
        /// (or any other tree-shape mutation) ran AFTER RP creation but BEFORE the
        /// player invoked Re-Fly, the in-memory <see cref="RecordingStore.CommittedTrees"/>
        /// has post-mutation recording IDs (and updated BranchPoint parent refs)
        /// that the loaded RP <c>.sfs</c> does NOT know about. Their <c>.prec</c>
        /// sidecars remain on disk but are orphaned because the loaded tree's
        /// <c>RECORDING_TREE</c> ConfigNode doesn't list them.
        /// </para>
        ///
        /// <para>
        /// This helper splices any recording present in the in-memory committed
        /// tree but missing from the loaded tree into the loaded tree as a
        /// deep-cloned, files-dirty copy, so the next <c>OnSave</c> rewrites the
        /// <c>.sfs</c> + <c>.prec</c> with fresh sidecar epochs and the correct
        /// merged shape. For recordings whose ID exists in BOTH the loaded and
        /// committed trees the helper additionally REFRESHES the structural
        /// fields of the loaded copy from the committed copy, since
        /// <c>SplitAtSection</c> mutates the original recording in place — it
        /// truncates the trajectory, moves the terminal payload to the new second
        /// half, and reassigns the original recording's <c>ChildBranchPointId</c>
        /// to the second half. Without that refresh, the loaded copy would keep
        /// the pre-split full trajectory + the old child link while the committed
        /// BP's parent list named the new second half — a referential mismatch
        /// (P1 review of PR #575). The <paramref name="activeRecordingId"/>, when
        /// supplied, identifies the recording the recorder will rebind to once
        /// <c>onFlightReady</c> fires; that recording still gets the structural
        /// refresh (it is precisely the one most likely to be the stale post-split
        /// first half — the splice runs BEFORE recorder rebind, so there is no
        /// in-flight recorder state to lose), but the refresh runs in
        /// recorder-state-preserving mode so transient flags
        /// <see cref="Recording.FilesDirty"/>,
        /// <see cref="Recording.SidecarLoadFailed"/>,
        /// <see cref="Recording.SidecarLoadFailureReason"/>,
        /// <see cref="Recording.ContinuationBoundaryIndex"/>,
        /// <see cref="Recording.PreContinuationVesselSnapshot"/>, and
        /// <see cref="Recording.PreContinuationGhostSnapshot"/> are NOT clobbered.
        /// The committed tree never carries those transient flags (DeepClone +
        /// the [NonSerialized] attribute strip them on copy), so non-active
        /// recordings cannot lose live state by being refreshed; the
        /// preserve-mode is only needed when subsequent code paths between this
        /// splice and the recorder's first sample have set those flags on the
        /// active recording (e.g. the load-time hydration mitigation may set
        /// <see cref="Recording.SidecarLoadFailed"/>). BranchPoints follow the
        /// same rule: any committed-tree-only BP is cloned in, and any loaded
        /// BP whose Id matches a committed BP gets its
        /// <c>ParentRecordingIds</c> / <c>ChildRecordingIds</c> overwritten
        /// from the committed copy (the post-merge truth).
        /// </para>
        ///
        /// <para>
        /// MUST be called before <see cref="RecordingStore.RemoveCommittedTreeById"/>,
        /// otherwise the in-memory committed copy (the splice source) is gone.
        /// Returns the number of recordings spliced. Always logs a structured
        /// <see cref="ParsekLog.Info"/> line so the decision is auditable even when
        /// the splice count is zero.
        /// </para>
        /// </summary>
        internal static int SpliceMissingCommittedRecordingsIntoLoadedTree(
            RecordingTree loadedTree,
            string activeRecordingId = null)
        {
            if (loadedTree == null || string.IsNullOrEmpty(loadedTree.Id))
                return 0;

            RecordingTree committedTree = FindCommittedTreeById(loadedTree.Id, exclude: loadedTree);
            if (committedTree == null)
            {
                ParsekLog.Verbose("Scenario",
                    $"SpliceMissingCommittedRecordings: tree id={loadedTree.Id} has no in-memory " +
                    $"committed counterpart — nothing to splice");
                return 0;
            }

            int loadedBefore = loadedTree.Recordings != null ? loadedTree.Recordings.Count : 0;
            int committedCount = committedTree.Recordings != null ? committedTree.Recordings.Count : 0;

            int splicedRecordings = 0;
            int refreshedRecordings = 0;
            int refreshedRecordingsFull = 0;
            int refreshedRecordingsRecorderStatePreserved = 0;
            int splicedBranchPoints = 0;
            int updatedBranchPoints = 0;
            var splicedRecordingIds = new List<string>();
            var refreshedRecordingIds = new List<string>();

            if (committedTree.Recordings != null && committedTree.Recordings.Count > 0)
            {
                foreach (var kvp in committedTree.Recordings)
                {
                    string recId = kvp.Key;
                    Recording committedRec = kvp.Value;
                    if (string.IsNullOrEmpty(recId) || committedRec == null)
                        continue;

                    Recording loadedRec = null;
                    bool loadedHasId = loadedTree.Recordings != null
                        && loadedTree.Recordings.TryGetValue(recId, out loadedRec)
                        && loadedRec != null;

                    if (!loadedHasId)
                    {
                        Recording clone = Recording.DeepClone(committedRec);
                        RecordingStore.ClearSidecarLoadFailure(clone);
                        // Mark dirty so the next OnSave rewrites the .sfs with the
                        // spliced shape AND advances the .prec sidecar epoch in
                        // lockstep — otherwise the committed-but-not-loaded recording
                        // would resurface as a "stale-sidecar-epoch" warning on a
                        // future scene reload (bug #270's mismatch detector).
                        clone.MarkFilesDirty();
                        loadedTree.AddOrReplaceRecording(clone);
                        splicedRecordings++;
                        splicedRecordingIds.Add(recId);
                        continue;
                    }

                    // Same-ID refresh path (P1 review of PR #575 + follow-up).
                    // The committed copy is the post-merge truth (post-
                    // SplitAtSection: truncated trajectory, moved terminal
                    // payload, reassigned ChildBranchPointId). The loaded copy
                    // is the pre-merge .sfs snapshot (full trajectory, original
                    // child link). Without refreshing, the loaded copy would
                    // internally disagree with the committed BP parent lists
                    // that the BP loop below overwrites onto the loaded BPs
                    // (e.g. parent BP's ParentRecordingIds names the new exo
                    // half but the original recording's ChildBranchPointId
                    // still points at the parent BP).
                    //
                    // The active recording is NOT skipped any more (the initial
                    // PR #575 follow-up did skip it — the reviewer rejected
                    // that because the active recording is precisely the one
                    // most likely to be a stale post-split first half kept by
                    // the RP's frozen .sfs). At splice time the recorder has
                    // not yet bound to the active recording — TryRestoreActiveTreeNode
                    // runs in OnLoad, the splice runs immediately after sidecar
                    // hydration, then the tree is stashed as pending-Limbo and
                    // the recorder rebind only fires on the deferred onFlightReady
                    // pass. There is therefore no in-flight recorder-owned
                    // payload state in the active recording to lose. The
                    // structural refresh always runs; the active id is forwarded
                    // into the helper so it can switch to recorder-state-
                    // preserving mode for the small set of [NonSerialized]
                    // flags that load-time mitigation paths may have already
                    // set on the active recording (FilesDirty / SidecarLoadFailed /
                    // SidecarLoadFailureReason / ContinuationBoundaryIndex /
                    // PreContinuationVesselSnapshot / PreContinuationGhostSnapshot /
                    // PreReFlyAnchor* snapshots).
                    bool isActive = !string.IsNullOrEmpty(activeRecordingId)
                        && string.Equals(recId, activeRecordingId, StringComparison.Ordinal);

                    if (RefreshLoadedRecordingFromCommittedSplit(
                            loadedRec, committedRec, preserveRecorderOwnedState: isActive))
                    {
                        refreshedRecordings++;
                        refreshedRecordingIds.Add(recId);
                        if (isActive)
                            refreshedRecordingsRecorderStatePreserved++;
                        else
                            refreshedRecordingsFull++;
                    }
                }
            }

            if (committedTree.BranchPoints != null && committedTree.BranchPoints.Count > 0)
            {
                if (loadedTree.BranchPoints == null)
                    loadedTree.BranchPoints = new List<BranchPoint>();

                var loadedBpIndex = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < loadedTree.BranchPoints.Count; i++)
                {
                    BranchPoint loadedBp = loadedTree.BranchPoints[i];
                    if (loadedBp != null && !string.IsNullOrEmpty(loadedBp.Id))
                        loadedBpIndex[loadedBp.Id] = i;
                }

                for (int b = 0; b < committedTree.BranchPoints.Count; b++)
                {
                    BranchPoint committedBp = committedTree.BranchPoints[b];
                    if (committedBp == null || string.IsNullOrEmpty(committedBp.Id))
                        continue;

                    if (!loadedBpIndex.TryGetValue(committedBp.Id, out int existingIdx))
                    {
                        // Brand-new BranchPoint authored after the RP snapshot.
                        BranchPoint clonedBp = CloneBranchPoint(committedBp);
                        loadedTree.BranchPoints.Add(clonedBp);
                        splicedBranchPoints++;
                        continue;
                    }

                    // Existing BP — overwrite parent/child id lists if the post-merge
                    // committed version diverges from the .sfs version. This is what
                    // catches the "Split: updated BranchPoint ParentRecordingIds:
                    // X -> Y" case where the parent BP id is unchanged but its
                    // ParentRecordingIds was rewritten to point at the new split
                    // half. Copying the lists also covers any future BP-edit path
                    // that doesn't change the BP id.
                    BranchPoint loadedBp = loadedTree.BranchPoints[existingIdx];
                    if (loadedBp == null) continue;
                    bool listsDiverged =
                        !StringListsEqual(loadedBp.ParentRecordingIds, committedBp.ParentRecordingIds)
                        || !StringListsEqual(loadedBp.ChildRecordingIds, committedBp.ChildRecordingIds);
                    if (listsDiverged)
                    {
                        loadedBp.ParentRecordingIds = committedBp.ParentRecordingIds != null
                            ? new List<string>(committedBp.ParentRecordingIds)
                            : new List<string>();
                        loadedBp.ChildRecordingIds = committedBp.ChildRecordingIds != null
                            ? new List<string>(committedBp.ChildRecordingIds)
                            : new List<string>();
                        updatedBranchPoints++;
                    }
                }
            }

            int repairedChainPredecessors = RepairMissingContiguousChainPredecessors(
                loadedTree, "SpliceMissingCommittedRecordings");

            int loadedAfter = loadedTree.Recordings != null ? loadedTree.Recordings.Count : 0;

            if (splicedRecordings > 0
                || refreshedRecordings > 0
                || repairedChainPredecessors > 0
                || splicedBranchPoints > 0
                || updatedBranchPoints > 0)
            {
                loadedTree.RebuildBackgroundMap();
                ParsekLog.Info("Scenario",
                    $"SpliceMissingCommittedRecordings: tree '{loadedTree.TreeName}' (id={loadedTree.Id}) " +
                    $"loadedBefore={loadedBefore} committed={committedCount} after={loadedAfter} " +
                    $"splicedRecordings={splicedRecordings} " +
                    $"refreshedRecordings={refreshedRecordings} " +
                    $"(full={refreshedRecordingsFull} " +
                    $"recorderStatePreserved={refreshedRecordingsRecorderStatePreserved}) " +
                    $"repairedChainPredecessors={repairedChainPredecessors} " +
                    $"splicedBranchPoints={splicedBranchPoints} " +
                    $"updatedBranchPoints={updatedBranchPoints} " +
                    $"source=committed-tree-in-memory");
                if (splicedRecordings > 0 || refreshedRecordings > 0)
                {
                    ParsekLog.Verbose("Scenario",
                        $"SpliceMissingCommittedRecordings: " +
                        $"splicedIds=[{string.Join(",", splicedRecordingIds)}] " +
                        $"refreshedIds=[{string.Join(",", refreshedRecordingIds)}]");
                }
            }
            else
            {
                ParsekLog.Verbose("Scenario",
                    $"SpliceMissingCommittedRecordings: tree '{loadedTree.TreeName}' (id={loadedTree.Id}) " +
                    $"loaded={loadedBefore} committed={committedCount} — already in sync, nothing to splice");
            }

            return splicedRecordings;
        }

        /// <summary>
        /// Refreshes the structural fields of a same-ID loaded recording from
        /// its committed-tree counterpart (the post-merge truth) when
        /// <c>SplitAtSection</c> has mutated the recording in place after the
        /// RP <c>.sfs</c> was authored. Mirrors the field-set pattern of
        /// <see cref="RestoreCommittedSidecarPayloadIntoActiveTreeRecording"/>
        /// — overwrites trajectory + terminal-state + child-link fields while
        /// preserving the loaded copy's identity (RecordingId, TreeId,
        /// TreeOrder, MergeState, CreatingSessionId, supersede/provisional refs).
        /// The loaded recording is marked <c>FilesDirty</c> so the next
        /// <c>OnSave</c> rewrites the <c>.sfs</c> + <c>.prec</c> with the
        /// post-split shape and a fresh sidecar epoch.
        ///
        /// <para>
        /// When <paramref name="preserveRecorderOwnedState"/> is <c>true</c>
        /// (passed for the active recording — see the helper's caller for the
        /// full load-order rationale) the refresh additionally preserves the
        /// set of <c>[NonSerialized]</c> flags any load-time mitigation may
        /// have already set on the loaded copy: <c>FilesDirty</c>,
        /// <c>SidecarLoadFailed</c>, <c>SidecarLoadFailureReason</c>,
        /// <c>ContinuationBoundaryIndex</c>, <c>PreContinuationVesselSnapshot</c>,
        /// <c>PreContinuationGhostSnapshot</c>, and <c>PreReFlyAnchor*</c>
        /// snapshots. The preserve-mode exists because
        /// the structural overwrite happens to land on the same recording the
        /// recorder will later rebind to, and downstream save paths look at
        /// <c>FilesDirty</c> / <c>SidecarLoadFailed</c> to decide whether to
        /// rewrite the <c>.prec</c> or repair from a donor — losing those
        /// flags would silently disable those paths for the active recording.
        /// </para>
        /// </summary>
        /// <returns>
        /// <c>true</c> if the committed copy diverged from the loaded copy in a
        /// split-relevant structural field and a refresh was applied;
        /// <c>false</c> if the loaded copy already matched (no-op).
        /// </returns>
        private static bool RefreshLoadedRecordingFromCommittedSplit(
            Recording loadedRec,
            Recording committedRec,
            bool preserveRecorderOwnedState)
        {
            if (loadedRec == null || committedRec == null)
                return false;

            // Detect divergence on split-relevant fields. SplitAtSection
            // mutates point count / last-point UT (truncation), TerminalStateValue
            // (moved to second half — first half ends up null), TerminalOrbitBody
            // (cleared on first half), OrbitSegments count, TrackSections count,
            // ChildBranchPointId (reassigned to second half), and EndBiome (cleared
            // and recomputed). If none of these diverge the loaded copy already
            // matches the committed shape and the refresh is a no-op.
            bool diverged =
                CountOrNull(loadedRec.Points) != CountOrNull(committedRec.Points)
                || CountOrNull(loadedRec.OrbitSegments) != CountOrNull(committedRec.OrbitSegments)
                || CountOrNull(loadedRec.TrackSections) != CountOrNull(committedRec.TrackSections)
                || !string.Equals(
                    loadedRec.ChildBranchPointId,
                    committedRec.ChildBranchPointId,
                    StringComparison.Ordinal)
                || !Nullable.Equals(loadedRec.TerminalStateValue, committedRec.TerminalStateValue)
                || !string.Equals(
                    loadedRec.TerminalOrbitBody,
                    committedRec.TerminalOrbitBody,
                    StringComparison.Ordinal)
                || LastPointUTOrNaN(loadedRec) != LastPointUTOrNaN(committedRec);

            if (!diverged)
                return false;

            // Preserve identity + transient flight state owned by the loaded
            // recording. These fields tag the recording within its tree shape
            // and are NOT what SplitAtSection rewrites; clobbering them would
            // re-parent the recording or lose mutations made between load and
            // splice. Mirror RestoreCommittedSidecarPayloadIntoActiveTreeRecording.
            string recordingId = loadedRec.RecordingId;
            string treeId = loadedRec.TreeId;
            int treeOrder = loadedRec.TreeOrder;
            MergeState mergeState = loadedRec.MergeState;
            string creatingSessionId = loadedRec.CreatingSessionId;
            string supersedeTargetId = loadedRec.SupersedeTargetId;
            string provisionalForRpId = loadedRec.ProvisionalForRpId;
            string switchSegmentSessionId = loadedRec.SwitchSegmentSessionId;

            // Recorder-owned [NonSerialized] flags. Snapshotted before the
            // overwrite so the active-refresh path can put them back. The
            // committed copy never carries them (DeepClone resets the flags
            // to their defaults), so a full refresh ends with them all
            // cleared / defaulted; preserve-mode reapplies the snapshot.
            // Audit anchor: the [NonSerialized] flag set in Recording.cs is
            // {FilesDirty, SidecarLoadFailed, SidecarLoadFailureReason,
            // ContinuationBoundaryIndex, PreContinuationVesselSnapshot,
            // PreContinuationGhostSnapshot, PreReFlyAnchor*}. Add to this
            // preserve-list when any new [NonSerialized] flag tracking
            // per-session live state is added to Recording.
            bool savedFilesDirty = loadedRec.FilesDirty;
            bool savedSidecarLoadFailed = loadedRec.SidecarLoadFailed;
            string savedSidecarLoadFailureReason = loadedRec.SidecarLoadFailureReason;
            int savedContinuationBoundaryIndex = loadedRec.ContinuationBoundaryIndex;
            ConfigNode savedPreContinuationVesselSnapshot = loadedRec.PreContinuationVesselSnapshot;
            ConfigNode savedPreContinuationGhostSnapshot = loadedRec.PreContinuationGhostSnapshot;
            string savedPreReFlyAnchorSessionId = loadedRec.PreReFlyAnchorSessionId;
            List<TrajectoryPoint> savedPreReFlyAnchorPoints = loadedRec.PreReFlyAnchorPoints;
            List<OrbitSegment> savedPreReFlyAnchorOrbitSegments = loadedRec.PreReFlyAnchorOrbitSegments;
            List<TrackSection> savedPreReFlyAnchorTrackSections = loadedRec.PreReFlyAnchorTrackSections;

            Recording sourceClone = Recording.DeepClone(committedRec);
            loadedRec.ApplyPersistenceArtifactsFrom(sourceClone);
            loadedRec.CopyStartLocationFrom(sourceClone);
            loadedRec.VesselName = sourceClone.VesselName;
            loadedRec.Points = sourceClone.Points ?? new List<TrajectoryPoint>();
            loadedRec.OrbitSegments = sourceClone.OrbitSegments ?? new List<OrbitSegment>();
            loadedRec.PartEvents = sourceClone.PartEvents ?? new List<PartEvent>();
            loadedRec.FlagEvents = sourceClone.FlagEvents ?? new List<FlagEvent>();
            loadedRec.SegmentEvents = sourceClone.SegmentEvents ?? new List<SegmentEvent>();
            loadedRec.TrackSections = sourceClone.TrackSections ?? new List<TrackSection>();
            loadedRec.Controllers = sourceClone.Controllers;
            loadedRec.CrewEndStates = sourceClone.CrewEndStates != null
                ? new Dictionary<string, KerbalEndState>(sourceClone.CrewEndStates)
                : null;
            loadedRec.SpawnSuppressedByRewind = sourceClone.SpawnSuppressedByRewind;
            loadedRec.SpawnSuppressedByRewindReason = sourceClone.SpawnSuppressedByRewindReason;
            loadedRec.SpawnSuppressedByRewindUT = sourceClone.SpawnSuppressedByRewindUT;
            loadedRec.SidecarEpoch = sourceClone.SidecarEpoch;
            RecordingStore.ClearSidecarLoadFailure(loadedRec);
            // Mark dirty so the next OnSave rewrites the .sfs with the refreshed
            // shape + advances the .prec sidecar epoch in lockstep (same
            // contract as the missing-id splice path above).
            loadedRec.MarkFilesDirty();

            loadedRec.RecordingId = recordingId;
            loadedRec.TreeId = treeId;
            loadedRec.TreeOrder = treeOrder;
            loadedRec.MergeState = mergeState;
            loadedRec.CreatingSessionId = creatingSessionId;
            loadedRec.SupersedeTargetId = supersedeTargetId;
            loadedRec.ProvisionalForRpId = provisionalForRpId;
            loadedRec.SwitchSegmentSessionId = switchSegmentSessionId;

            if (preserveRecorderOwnedState)
            {
                // Restore the recorder-owned flag snapshot. FilesDirty is OR-ed
                // with the freshly-marked-dirty value because either being
                // true means the next OnSave must rewrite the sidecar — we
                // don't want a previously-dirty state to be downgraded just
                // because the structural overwrite is canonical-by-itself.
                loadedRec.FilesDirty = savedFilesDirty || loadedRec.FilesDirty;
                loadedRec.SidecarLoadFailed = savedSidecarLoadFailed;
                loadedRec.SidecarLoadFailureReason = savedSidecarLoadFailureReason;
                loadedRec.ContinuationBoundaryIndex = savedContinuationBoundaryIndex;
                loadedRec.PreContinuationVesselSnapshot = savedPreContinuationVesselSnapshot;
                loadedRec.PreContinuationGhostSnapshot = savedPreContinuationGhostSnapshot;
                loadedRec.PreReFlyAnchorSessionId = savedPreReFlyAnchorSessionId;
                loadedRec.PreReFlyAnchorPoints = savedPreReFlyAnchorPoints;
                loadedRec.PreReFlyAnchorOrbitSegments = savedPreReFlyAnchorOrbitSegments;
                loadedRec.PreReFlyAnchorTrackSections = savedPreReFlyAnchorTrackSections;
            }

            return true;
        }

        private static int CountOrNull<T>(List<T> list) => list != null ? list.Count : 0;

        private static double LastPointUTOrNaN(Recording rec)
        {
            if (rec == null || rec.Points == null || rec.Points.Count == 0)
                return double.NaN;
            return rec.Points[rec.Points.Count - 1].ut;
        }

        private static BranchPoint CloneBranchPoint(BranchPoint source)
        {
            if (source == null) return null;
            var clone = new BranchPoint
            {
                Id = source.Id,
                UT = source.UT,
                Type = source.Type,
                ParentRecordingIds = source.ParentRecordingIds != null
                    ? new List<string>(source.ParentRecordingIds)
                    : new List<string>(),
                ChildRecordingIds = source.ChildRecordingIds != null
                    ? new List<string>(source.ChildRecordingIds)
                    : new List<string>(),
                SplitCause = source.SplitCause,
                DecouplerPartId = source.DecouplerPartId,
                BreakupCause = source.BreakupCause,
                BreakupDuration = source.BreakupDuration,
                DebrisCount = source.DebrisCount,
                CoalesceWindow = source.CoalesceWindow,
                MergeCause = source.MergeCause,
                TargetVesselPersistentId = source.TargetVesselPersistentId,
                TerminalCause = source.TerminalCause,
                RewindPointId = source.RewindPointId,
            };
            return clone;
        }

        private static bool StringListsEqual(List<string> a, List<string> b)
        {
            if (ReferenceEquals(a, b)) return true;
            int aCount = a != null ? a.Count : 0;
            int bCount = b != null ? b.Count : 0;
            if (aCount != bCount) return false;
            for (int i = 0; i < aCount; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static RecordingTree FindCommittedTreeById(string treeId, RecordingTree exclude = null)
        {
            if (string.IsNullOrEmpty(treeId))
                return null;

            var trees = RecordingStore.CommittedTrees;
            if (trees == null)
                return null;

            for (int i = 0; i < trees.Count; i++)
            {
                RecordingTree tree = trees[i];
                if (tree == null || ReferenceEquals(tree, exclude))
                    continue;
                if (string.Equals(tree.Id, treeId, StringComparison.Ordinal))
                    return tree;
            }

            return null;
        }

        private static bool ShouldRestoreHydrationFailureFromCommittedRecording(
            Recording loadedRec,
            Recording sourceRec,
            string activeRecordingId)
        {
            if (loadedRec == null || sourceRec == null)
                return false;

            if (!string.IsNullOrEmpty(activeRecordingId)
                && string.Equals(
                    loadedRec.RecordingId,
                    activeRecordingId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!HasTrajectoryPayload(sourceRec))
                return false;

            // Repair must be scoped to records that explicitly failed to
            // hydrate from sidecar (PR #572 P2 review). Without this gate, any
            // dirty active-tree record with empty trajectory lists would match
            // — including legitimate metadata-only / snapshot-only edits where
            // the trajectory hasn't been seeded yet — and the committed copy
            // would silently overwrite the in-memory mutation. Snapshot-only
            // hydration failures route through the pending-tree salvage path
            // (`TryRestoreSnapshotStateFromPendingRecording`) and are excluded
            // here so that snapshot/trajectory recoveries cannot cross-pollute.
            return loadedRec.SidecarLoadFailed
                && !IsSnapshotHydrationFailure(loadedRec.SidecarLoadFailureReason)
                && IsTrajectoryPayloadEmpty(loadedRec);
        }

        private static void RestoreCommittedSidecarPayloadIntoActiveTreeRecording(
            Recording target,
            Recording source)
        {
            if (target == null || source == null)
                return;

            string recordingId = target.RecordingId;
            string treeId = target.TreeId;
            int treeOrder = target.TreeOrder;
            MergeState mergeState = target.MergeState;
            string creatingSessionId = target.CreatingSessionId;
            string supersedeTargetId = target.SupersedeTargetId;
            string provisionalForRpId = target.ProvisionalForRpId;
            string switchSegmentSessionId = target.SwitchSegmentSessionId;

            Recording sourceClone = Recording.DeepClone(source);
            target.ApplyPersistenceArtifactsFrom(sourceClone);
            target.CopyStartLocationFrom(sourceClone);
            target.VesselName = sourceClone.VesselName;
            target.Points = sourceClone.Points ?? new List<TrajectoryPoint>();
            target.OrbitSegments = sourceClone.OrbitSegments ?? new List<OrbitSegment>();
            target.PartEvents = sourceClone.PartEvents ?? new List<PartEvent>();
            target.FlagEvents = sourceClone.FlagEvents ?? new List<FlagEvent>();
            target.SegmentEvents = sourceClone.SegmentEvents ?? new List<SegmentEvent>();
            target.TrackSections = sourceClone.TrackSections ?? new List<TrackSection>();
            target.Controllers = sourceClone.Controllers;
            // PR #572 P2 review follow-up: ApplyPersistenceArtifactsFrom copies
            // `CrewEndStatesResolved` but NOT the `CrewEndStates` dictionary
            // itself; without this explicit copy, a source with populated crew
            // end-states would repair the target into `resolved=true` with a
            // null/stale dict, and the safety-net population path skips
            // already-resolved records on the next save (loss persisted).
            // Mirror DeepClone's CrewEndStates copy.
            target.CrewEndStates = sourceClone.CrewEndStates != null
                ? new Dictionary<string, KerbalEndState>(sourceClone.CrewEndStates)
                : null;
            // PR #572 P2 review follow-up: SpawnSuppressedByRewind / Reason / UT
            // are persisted (#573 / #589 active/source recording protection)
            // but ApplyPersistenceArtifactsFrom doesn't copy them. Mirror
            // DeepClone so the repair preserves rewind-strip-protection scope
            // alongside the trajectory data.
            target.SpawnSuppressedByRewind = sourceClone.SpawnSuppressedByRewind;
            target.SpawnSuppressedByRewindReason = sourceClone.SpawnSuppressedByRewindReason;
            target.SpawnSuppressedByRewindUT = sourceClone.SpawnSuppressedByRewindUT;
            target.FilesDirty = false;
            target.SidecarEpoch = sourceClone.SidecarEpoch;
            RecordingStore.ClearSidecarLoadFailure(target);

            target.RecordingId = recordingId;
            target.TreeId = treeId;
            target.TreeOrder = treeOrder;
            target.MergeState = mergeState;
            target.CreatingSessionId = creatingSessionId;
            target.SupersedeTargetId = supersedeTargetId;
            target.ProvisionalForRpId = provisionalForRpId;
            target.SwitchSegmentSessionId = switchSegmentSessionId;

            // PR #572 second-order data-loss companion: the trajectory just
            // copied over came from a committed recording that, in the
            // Re-Fly in-place-continuation case, was committed mid-flight
            // with TerminalStateValue=none. The immediately-following
            // FinalizeTreeRecordings on scene exit would otherwise treat
            // `vessel pid=… not found on scene exit` + last-point altitude<50m
            // as evidence of a fresh landing and stamp Landed/Splashed onto
            // a recording that actually represents a stripped Re-Fly origin
            // vessel. The transient marker tells the finalize path to skip
            // the surface inference for this recording on this frame —
            // [NonSerialized] so a fresh session never sees it.
            target.RestoredFromCommittedTreeThisFrame = true;
        }

        private static bool ShouldSkipActiveTreeEmptySidecarOverwrite(Recording rec)
        {
            return rec != null
                && rec.SidecarLoadFailed
                && !IsSnapshotHydrationFailure(rec.SidecarLoadFailureReason)
                && IsTrajectoryPayloadEmpty(rec);
        }

        private static bool HasTrajectoryPayload(Recording rec)
        {
            if (rec == null)
                return false;

            return (rec.Points != null && rec.Points.Count > 0)
                || (rec.OrbitSegments != null && rec.OrbitSegments.Count > 0)
                || (rec.TrackSections != null && rec.TrackSections.Count > 0)
                || (rec.PartEvents != null && rec.PartEvents.Count > 0)
                || (rec.FlagEvents != null && rec.FlagEvents.Count > 0)
                || (rec.SegmentEvents != null && rec.SegmentEvents.Count > 0);
        }

        private static bool IsTrajectoryPayloadEmpty(Recording rec)
        {
            return !HasTrajectoryPayload(rec);
        }

        private static bool TryRestoreSnapshotStateFromPendingRecording(Recording loadedRec, Recording pendingRec)
        {
            if (loadedRec == null || pendingRec == null)
                return false;

            if (!IsSnapshotHydrationFailure(loadedRec.SidecarLoadFailureReason))
                return false;

            bool restoredAny = false;
            if (loadedRec.VesselSnapshot == null)
            {
                if (pendingRec.VesselSnapshot != null)
                {
                    loadedRec.VesselSnapshot = pendingRec.VesselSnapshot.CreateCopy();
                    restoredAny = true;
                }
                else if (pendingRec.GhostSnapshotMode == GhostSnapshotMode.AliasVessel
                    && pendingRec.GhostVisualSnapshot != null)
                {
                    loadedRec.VesselSnapshot = pendingRec.GhostVisualSnapshot.CreateCopy();
                    restoredAny = true;
                }
            }

            GhostSnapshotMode restoredMode = pendingRec.GhostSnapshotMode != GhostSnapshotMode.Unspecified
                ? pendingRec.GhostSnapshotMode
                : loadedRec.GhostSnapshotMode;

            if (loadedRec.GhostVisualSnapshot == null)
            {
                if (pendingRec.GhostVisualSnapshot != null)
                {
                    loadedRec.GhostVisualSnapshot = pendingRec.GhostVisualSnapshot.CreateCopy();
                    restoredAny = true;
                }
                else if (restoredMode == GhostSnapshotMode.AliasVessel)
                {
                    ConfigNode aliasSource = loadedRec.VesselSnapshot ?? pendingRec.VesselSnapshot;
                    if (aliasSource != null)
                    {
                        loadedRec.GhostVisualSnapshot = aliasSource.CreateCopy();
                        restoredAny = true;
                    }
                }
            }

            if (!restoredAny)
                return false;

            loadedRec.GhostSnapshotMode = restoredMode;

            if (loadedRec.VesselSnapshot != null
                && loadedRec.GhostSnapshotMode == GhostSnapshotMode.AliasVessel)
            {
                loadedRec.GhostVisualSnapshot = loadedRec.VesselSnapshot.CreateCopy();
            }

            if (!HasCoherentSnapshotState(loadedRec))
                return false;

            RecordingStore.ClearSidecarLoadFailure(loadedRec);
            loadedRec.MarkFilesDirty();
            return true;
        }

        private static bool IsSnapshotHydrationFailure(string reason)
        {
            return reason == "snapshot-vessel-invalid"
                || reason == "snapshot-vessel-unsupported"
                || reason == "snapshot-ghost-invalid"
                || reason == "snapshot-ghost-unsupported";
        }

        private static bool HasCoherentSnapshotState(Recording rec)
        {
            if (rec == null)
                return false;

            GhostSnapshotMode mode = rec.GhostSnapshotMode != GhostSnapshotMode.Unspecified
                ? rec.GhostSnapshotMode
                : RecordingStore.DetermineGhostSnapshotMode(rec);
            if (mode == GhostSnapshotMode.AliasVessel)
            {
                return rec.VesselSnapshot != null
                    && rec.GhostVisualSnapshot != null
                    && RecordingStore.ConfigNodesEquivalent(rec.VesselSnapshot, rec.GhostVisualSnapshot);
            }
            if (mode == GhostSnapshotMode.Separate)
                return rec.GhostVisualSnapshot != null;
            return rec.VesselSnapshot != null || rec.GhostVisualSnapshot != null;
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

            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Verbose("Scenario",
                $"DeferredSeed: values ready after {framesWaited} frames — " +
                $"Funding={(Funding.Instance != null ? Funding.Instance.Funds.ToString("F0", ic) : "null")}, " +
                $"Science={(ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science.ToString("F0", ic) : "null")}, " +
                $"Rep={(Reputation.Instance != null ? Reputation.Instance.reputation.ToString("F1", ic) : "null")}");

            double currentUT = Planetarium.GetUniversalTime();
            if (IsCurrentUtCutoffSupportedScene(HighLogic.LoadedScene)
                && LedgerOrchestrator.HasActionsAfterUT(currentUT))
            {
                LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(
                    currentUT,
                    "deferred-seed");
            }
            else
            {
                LedgerOrchestrator.RecalculateAndPatch();
            }
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
            LedgerOrchestrator.RecalculateAndPatch(
                adjustedUT,
                suppressSuspiciousDrawdownWarnings: true);

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
        /// Strips protoVessels from flightState whose vesselName matches a spawned recording name.
        /// Called during revert/rewind to remove orphaned spawned vessels before they contaminate
        /// the next launch quicksave. Uses name-based matching because ProtoVessel doesn't expose
        /// vessel persistentId directly.
        /// <param name="skipPrelaunch">When true (KSP Revert), PRELAUNCH vessels are kept —
        /// they are the user's launch vessel, not spawned vessels. When false (Parsek Rewind),
        /// all matching vessels are stripped — a PRELAUNCH vessel from a later launch is
        /// incompatible with the earlier game state being restored.</param>
        /// </summary>
        internal static int StripOrphanedSpawnedVessels(
            List<ProtoVessel> protoVessels, HashSet<string> spawnedNames, bool skipPrelaunch)
        {
            if (protoVessels == null || spawnedNames == null || spawnedNames.Count == 0)
                return 0;

            int stripped = 0;
            for (int i = protoVessels.Count - 1; i >= 0; i--)
            {
                var pv = protoVessels[i];
                if (GhostMapPresence.IsGhostMapVessel(pv.persistentId)) continue;
                if (!spawnedNames.Contains(Recording.ResolveLocalizedName(pv.vesselName)))
                    continue;

                // On KSP Revert, skip PRELAUNCH vessels — these are the user's launch
                // vessel on the pad. On Parsek Rewind, strip them too — a PRELAUNCH
                // vessel from a future launch is incompatible with the rewound state.
                if (skipPrelaunch && pv.situation == Vessel.Situations.PRELAUNCH)
                {
                    ParsekLog.Verbose("Scenario",
                        $"Skipping PRELAUNCH vessel '{pv.vesselName}' (revert — protecting launch vessel)");
                    continue;
                }

                ParsekLog.Info("Scenario",
                    $"Stripping orphaned spawned vessel '{pv.vesselName}' " +
                    $"(situation={pv.situation}) from flightState");
                protoVessels.RemoveAt(i);
                stripped++;
            }

            if (stripped > 0)
                ParsekLog.Info("Scenario",
                    $"StripOrphanedSpawnedVessels: removed {stripped} vessel(s) from flightState");

            return stripped;
        }

        /// <summary>
        /// Strips PRELAUNCH vessels whose persistentId is NOT in the quicksave whitelist.
        /// These are pad vessels from a future launch that persisted through rewind because
        /// StripOrphanedSpawnedVessels only filters by name — unrecorded PRELAUNCH vessels
        /// fail the name check and survive.
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
                        out string applyReason))
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
            out string reason)
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

            bool matchesRewindSource = rewindSourcePid != 0
                && rec.VesselPersistentId == rewindSourcePid;
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
        /// <see cref="RewindSpawnSuppressionReasonSameRecording"/> reason: legacy
        /// unscoped markers are normalized by
        /// <c>ShouldBlockSpawnForRewindSuppression</c>'s separate path. This helper
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

            // UI grouping tags (multi-group membership)
            if (rec.RecordingGroups != null)
                for (int g = 0; g < rec.RecordingGroups.Count; g++)
                    recNode.AddValue("recordingGroup", rec.RecordingGroups[g]);

            // Atmosphere segment metadata (only if set, saves space)
            if (!string.IsNullOrEmpty(rec.SegmentPhase))
                recNode.AddValue("segmentPhase", rec.SegmentPhase);
            if (!string.IsNullOrEmpty(rec.SegmentBodyName))
                recNode.AddValue("segmentBodyName", rec.SegmentBodyName);

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

            if (!rec.PlaybackEnabled)
                recNode.AddValue("playbackEnabled", rec.PlaybackEnabled.ToString());
            if (rec.Hidden)
                recNode.AddValue("hidden", rec.Hidden.ToString());

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
        /// </summary>
        private static void OnMainMenuTransition(GameScenes newScene)
        {
            if (newScene == GameScenes.MAINMENU)
            {
                initialLoadDone = false;
                lastSaveFolder = null;
                lastOnSaveScene = GameScenes.MAINMENU;
                lastSceneChangeRequestedUT = -1.0;
                RecordingStore.PendingCleanupPids = null;
                RecordingStore.PendingCleanupNames = null;
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

        public void OnDestroy()
        {
            stateRecorder?.Unsubscribe();
            GameEvents.onVesselRecoveryProcessing.Remove(OnVesselRecoveryProcessing);
            GameEvents.onVesselRecovered.Remove(OnVesselRecovered);
            GameEvents.onVesselTerminated.Remove(OnVesselTerminated);
            GameEvents.onVesselSwitching.Remove(OnVesselSwitching);
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
