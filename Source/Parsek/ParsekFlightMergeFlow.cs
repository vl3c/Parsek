using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// What <see cref="ParsekFlightMergeFlow"/> reads and drives on its host, so its merge and
    /// commit decisions run against a fake in unit tests. <see cref="ParsekFlight"/> implements it
    /// explicitly, each member forwarding to the host member of the same name (the two spawn
    /// members to its SpawnHost, and the last three to the live KSP call the service used to make
    /// itself). The state properties are
    /// named after the host fields they expose, so the service bodies read as they did on the host.
    /// The recorder objects themselves stay concrete: a test hands in a real
    /// <see cref="FlightRecorder"/> / <see cref="BackgroundRecorder"/> or leaves them null.
    /// </summary>
    internal interface IMergeFlowHost
    {
        // The recorder session: the live tree and its two recorders.
        RecordingTree activeTree { get; set; }
        FlightRecorder recorder { get; set; }
        BackgroundRecorder backgroundRecorder { get; set; }

        // Recorder-session operations that stay on the host (partials by measured decision).
        RecorderStateSnapshot CaptureRecorderState();
        void FlushRecorderToTreeRecording(FlightRecorder rec, RecordingTree tree);
        void FlushRecorderIntoActiveTreeForSerialization();
        RecordingFinalizationCache ResolveFinalizationCacheForRecording(RecordingTree tree, Recording recording);
        bool CanPreTransitionForVesselSwitch();
        void StashActiveTreeForVesselSwitch(double commitUT);
        void StashActiveTreeAsPendingLimbo(double commitUT);
        void ClearSceneChangeTransientState();

        // Host state and services.
        int lastPlaybackIndex { get; set; }
        void ScreenMessage(string message, float duration);
        void Log(string message);
        void SpawnTreeLeaves(RecordingTree tree, string activeRecordingId);
        void EvaluateAndApplyGhostChains();

        // Live KSP reads the decisions need: Planetarium.GetUniversalTime(),
        // ParsekScenario.IsVesselSwitchPendingFresh() (UnityEngine.Time.frameCount) and the
        // active vessel's persistentId (FlightGlobals.ActiveVessel, 0 when none).
        double CurrentUniversalTime();
        bool IsVesselSwitchPendingFresh();
        uint ActiveVesselPersistentIdOrZero();
    }

    // The flight-scene merge flow: the scene-exit finalize (commit, stash or discard of the active
    // tree), the in-flight, revert and scene-exit tree commits, the auto-discard of idle or no-op
    // trees, idle-on-pad, crew reservation for the committed leaves and the final segment phase and
    // terminal-body resolution the finalize reads. ParsekFlight keeps OnSceneChangeRequested (the
    // order of the scene-exit calls), the MergeDialog subscription (its handler forwards here), the
    // ShowPostDestructionTreeMergeDialog coroutine, the commit and terminal-inference statics other
    // files name with their test seams, and forwarders for the entry points other types call; it
    // reaches this service through MergeFlow. The service reaches its host only through
    // IMergeFlowHost.
    internal sealed class ParsekFlightMergeFlow
    {
        private readonly IMergeFlowHost host;

        internal ParsekFlightMergeFlow(IMergeFlowHost host)
        {
            this.host = host;
        }

        internal void FinalizeTreeOnSceneChange(GameScenes scene)
        {
            FinalizeTreeOnSceneChangeCore(
                scene,
                host.CurrentUniversalTime(),
                logRecorderState: true);
        }

        internal void FinalizeTreeOnSceneChangeForTesting(GameScenes scene, double commitUT)
        {
            FinalizeTreeOnSceneChangeCore(scene, commitUT, logRecorderState: false);
        }

        /// <summary>
        /// Pre-transition finalize entry for the in-flight merge dialog
        /// (<see cref="MergeDialog.ShowTreeDialog"/>'s
        /// <c>preCommitFinalize</c> callback). Identical to what
        /// <see cref="OnSceneChangeRequested"/> would have run a few frames
        /// later. The dialog button handler invokes this BEFORE
        /// <see cref="MergeDialog.MergeCommit"/> /
        /// <see cref="MergeDialog.MergeDiscard"/> so those operate on the
        /// just-stashed pending tree.
        ///
        /// <para>Mirrors the recorder-state prep that
        /// <see cref="OnSceneChangeRequested"/> runs immediately before
        /// <see cref="FinalizeTreeOnSceneChange"/>: clear scene-change
        /// transient state before <c>MergeCommit</c> and the optimization
        /// pass run.</para>
        /// </summary>
        internal void FinalizeTreeOnSceneChangeForCallback(GameScenes scene)
        {
            host.ClearSceneChangeTransientState();
            FinalizeTreeOnSceneChangeCore(
                scene,
                host.CurrentUniversalTime(),
                logRecorderState: true);
        }

        /// <summary>
        /// The active tree's recording that the live recorder is writing for the vessel
        /// <paramref name="vesselPid"/> right now, or null. The live binding is the identity
        /// proof for an in-flight Recover: a resumed spawned vessel whose spawn regenerated
        /// its identity carries the original launch guid on its recording but a fresh one on
        /// the vessel, so a launch-identity match alone cannot find its recording.
        /// </summary>
        internal string LiveRecordingIdForVessel(uint vesselPid)
        {
            if (vesselPid == 0 || host.activeTree?.Recordings == null || host.recorder == null)
                return null;
            if (!host.recorder.IsRecording || host.recorder.RecordingVesselId != vesselPid)
                return null;
            string id = host.activeTree.ActiveRecordingId;
            if (string.IsNullOrEmpty(id)
                || !host.activeTree.Recordings.TryGetValue(id, out Recording rec)
                || rec == null
                || rec.VesselPersistentId != vesselPid)
                return null;
            return id;
        }

        /// <summary>
        /// Live-state idle-on-pad check used by the
        /// <c>HighLogic.LoadScene</c> prefix's fast path. Returns true if
        /// every recording in the active tree is idle-on-pad (max
        /// distance from launch &lt; pad-localized threshold) computed
        /// from live data via
        /// <see cref="LiveVesselSnapshot.BackfillMaxDistanceFromBodyFixedSurfaces(Recording)"/>,
        /// which reads the body-fixed surface of an Absolute section
        /// (<c>frames</c>) and of a Relative section (<c>bodyFixedFrames</c>)
        /// and never a Relative section's anchor-local <c>frames</c>.
        ///
        /// <para>This method MUTATES: it calls
        /// <see cref="FlushRecorderIntoActiveTreeForSerialization"/> first
        /// so the live recorder's <c>Recording</c> + open
        /// <c>TrackSections</c> become populated on the tree recordings
        /// (<see cref="LiveVesselSnapshot.BackfillMaxDistanceFromBodyFixedSurfaces(Recording)"/>
        /// reads <c>rec.TrackSections</c> only). Without this, atmospheric
        /// flights that haven't crossed an env boundary or saved yet have
        /// empty <c>TrackSections</c> and the helper would falsely report
        /// idle (Opus pass-7 review P1). The flush is non-destructive: the
        /// recorder stays IsRecording=true and the only side effect is one
        /// extra TrackSection boundary at the current UT - identical to
        /// what every <c>OnSave</c> tick produces.</para>
        ///
        /// <para>Mirrors the <see cref="IsTreeIdleOnPad(RecordingTree)"/>
        /// data-loss safeguard (in the <c>anyHasPoints</c> block of that
        /// function): a tree where no recording has any trajectory points
        /// is data-loss, not idle. We refuse to classify and return false
        /// so the dialog still shows.</para>
        /// </summary>
        internal bool IsActiveTreeIdleOnPad()
        {
            if (host.activeTree == null) return false;
            if (host.activeTree.Recordings == null || host.activeTree.Recordings.Count == 0)
                return false;
            // Mirror the existing defensive guard pattern used by other
            // mutating paths in this file (e.g. line 2727 in
            // FinalizeTreeOnSceneChangeCore): if a restore coroutine owns
            // the recorder/activeTree, do not flush.
            if (ParsekFlight.restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    "IsActiveTreeIdleOnPad: skipped - restore coroutine in progress");
                return false;
            }

            if (ParsekFlight.TreeHasPlacedGroundPartsForIdle(host.activeTree, "IsActiveTreeIdleOnPad"))
                return false;

            // Flush live recorder data into the tree so subsequent walks
            // over rec.TrackSections / rec.Points see the in-flight data.
            host.FlushRecorderIntoActiveTreeForSerialization();

            // After the flush: a flag planted this flight is still in the recorder buffer before it.
            if (ParsekFlight.TreeHasPlantedFlagsForIdle(host.activeTree, "IsActiveTreeIdleOnPad")
                || ParsekFlight.TreeMovesHeldKerbalForIdle(host.activeTree, "IsActiveTreeIdleOnPad")
                || ParsekFlight.TreeInteractsWithForeignVesselForIdle(host.activeTree, "IsActiveTreeIdleOnPad")
                || ParsekFlight.TreeHasConverterWorkForIdle(host.activeTree, "IsActiveTreeIdleOnPad")
                || ParsekFlight.TreeHasLabPayoutsForIdle(host.activeTree, "IsActiveTreeIdleOnPad")
                || ParsekFlight.TreeHasPendingLabRecoveryForIdle(host.activeTree, "IsActiveTreeIdleOnPad"))
                return false;

            if (!AllRecordingsIdleOnPadAfterBackfill(host.activeTree, out bool anyHasPoints))
                return false;

            // Bug #290d safeguard: a tree where no recording has trajectory
            // points is data-loss, not idle-on-pad. Don't auto-discard.
            if (!anyHasPoints)
            {
                ParsekLog.Verbose("Flight",
                    $"IsActiveTreeIdleOnPad: all {host.activeTree.Recordings.Count} recordings " +
                    "have 0 points - cannot determine idle, returning false");
                return false;
            }

            // The flush above copied the live root's StartSituation / LaunchSiteName onto
            // its tree recording (FlushRecorderIntoActiveTreeForSerialization), so the
            // evidence check sees the live state.
            if (!ParsekFlight.TreeHasPadEvidenceForIdle(host.activeTree, "IsActiveTreeIdleOnPad"))
                return false;

            ParsekLog.Verbose("Flight",
                $"IsActiveTreeIdleOnPad: all {host.activeTree.Recordings.Count} recordings " +
                "within 30m - idle on pad");
            return true;
        }

        /// <summary>
        /// The per-recording walk of <see cref="IsActiveTreeIdleOnPad"/>: backfills each
        /// recording's max distance from its body-fixed surfaces, notes whether any recording
        /// has trajectory points, and returns false (logging the vessel) at the first
        /// recording that is not idle on the pad. Null slots are skipped. On a false return
        /// <paramref name="anyHasPoints"/> covers only the recordings walked so far.
        /// </summary>
        internal static bool AllRecordingsIdleOnPadAfterBackfill(
            RecordingTree tree, out bool anyHasPoints)
        {
            anyHasPoints = false;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec == null) continue;
                LiveVesselSnapshot.BackfillMaxDistanceFromBodyFixedSurfaces(rec);
                if (rec.Points != null && rec.Points.Count > 0)
                    anyHasPoints = true;
                if (!ParsekFlight.IsIdleOnPad(rec))
                {
                    ParsekLog.Verbose("Flight",
                        $"IsActiveTreeIdleOnPad: '{rec.VesselName}' maxDist=" +
                        $"{rec.MaxDistanceFromLaunch:F1}m - not idle");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Pre-transition idle-on-pad fast path: tear down the live
        /// recorder / activeTree without going through finalize / stash.
        /// Mirrors the suppressed-discard cleanup at
        /// <see cref="DiscardActiveTreeForSuppressedSceneExit"/>: stop
        /// the recorder, clear the
        /// <see cref="Patches.PhysicsFramePatch.ActiveRecorder"/> and
        /// <see cref="Patches.PhysicsFramePatch.BackgroundRecorderInstance"/>
        /// references, then null the fields. Also runs the same prep as
        /// <see cref="OnSceneChangeRequested"/> (transient state)
        /// since the prefix returns true after this and
        /// <see cref="OnSceneChangeRequested"/> only re-runs that prep
        /// once it sees <c>activeTree == null</c>.
        ///
        /// <para>After this returns, the prefix's blocked
        /// <c>HighLogic.LoadScene</c> proceeds and
        /// <see cref="OnSceneChangeRequested"/> sees
        /// <c>activeTree == null</c>, so no pending tree is stashed and
        /// the destination scene's OnLoad has nothing to commit.</para>
        /// </summary>
        internal void AutoDiscardIdleActiveTree(string reason)
        {
            AutoDiscardActiveTreeCore(
                reason: reason,
                screenMessage: "Recording discarded - idle on pad",
                ledgerRecalcReason: "suppressed-scene-exit-discard",
                reapSidecars: true);
        }

        /// <summary>
        /// Reason-aware variant of <see cref="AutoDiscardIdleActiveTree"/>
        /// that lets callers override the on-screen toast and the
        /// ledger-recalc reason string. Same teardown body as the original
        /// entry point; introduced for the pre-switch-dialog Case B
        /// no-session Discard handler (PR #876 round-6 review) where the
        /// "idle on pad" toast and "suppressed-scene-exit-discard" reason
        /// are wrong-context — the player has been actively flying out of
        /// the bubble, not sitting on the pad.
        /// </summary>
        /// <param name="reason">Internal log reason (subsystem trace).</param>
        /// <param name="screenMessage">On-screen toast shown to the player.</param>
        /// <param name="ledgerRecalcReason">Reason string passed to
        /// <see cref="LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions"/>;
        /// grep-able from ledger-recalc log lines.</param>
        /// <param name="reapSidecars">
        /// Whether this teardown also reaps the discarded tree's on-disk sidecars
        /// (see <see cref="DiscardSidecarReap"/>). True for every gameplay path;
        /// false only for the M-A2 <c>DiscardTree</c> test-command verb, which owns
        /// its own reap and its own summary line (pinned verbatim by the S0.5
        /// harness spec) and would otherwise report <c>reaped=0</c> on a
        /// double pass.
        /// </param>
        internal void AutoDiscardActiveTreeWithMessage(
            string reason,
            string screenMessage,
            string ledgerRecalcReason,
            bool reapSidecars = true)
        {
            AutoDiscardActiveTreeCore(
                reason: reason,
                screenMessage: screenMessage,
                ledgerRecalcReason: ledgerRecalcReason,
                reapSidecars: reapSidecars);
        }

        /// <summary>
        /// Shared teardown body for <see cref="AutoDiscardIdleActiveTree"/>
        /// and <see cref="AutoDiscardActiveTreeWithMessage"/>. The screen
        /// message and ledger-recalc reason are parameterized so the
        /// existing idle-on-pad entry point keeps its toast/reason and the
        /// new reason-aware entry point can supply context-appropriate
        /// strings (e.g. the Case B Switch-To Discard handler).
        ///
        /// <para><b>Sidecar reap.</b> This teardown is in-memory-only by
        /// design, but <c>StartRecording</c>'s quickload-resume OnSave may
        /// already have written the tree's <c>.prec</c> / <c>.pann</c> /
        /// <c>_ghost.craft</c> sidecars to disk. A discard that empties the
        /// store used to strand them permanently (the sweeper's zero-known
        /// guard correctly refuses to touch the directory), so the tree's
        /// recordings and the load-shape gate are sampled BEFORE the teardown
        /// and <see cref="DiscardSidecarReap"/> reaps them AFTER, by explicit
        /// id, never as a directory sweep. <paramref name="reapSidecars"/>
        /// is false only for callers that own their own reap + summary line
        /// (the M-A2 <c>DiscardTree</c> test-command verb, whose line the S0.5
        /// harness spec pins verbatim).</para>
        /// </summary>
        internal void AutoDiscardActiveTreeCore(
            string reason,
            string screenMessage,
            string ledgerRecalcReason,
            bool reapSidecars)
        {
            ParsekLog.Info("Flight",
                $"AutoDiscardActiveTreeCore: discarding live tree reason='{reason}' " +
                $"ledgerRecalcReason='{ledgerRecalcReason}'");
            if (!string.IsNullOrEmpty(screenMessage))
                host.ScreenMessage(screenMessage, 3f);

            // Sidecar-reap capture: BOTH inputs must be read before the teardown
            // nulls activeTree (the recordings) and while the load shape is still
            // observable (the skip gate). The reap itself runs after the teardown,
            // when the store's known-id set no longer owns the discarded ids.
            //
            // Wrapped for the same reason TryEvaluateActiveSwitchSegmentNoOp is: two of
            // this method's callers run inside a Harmony prefix (the HighLogic.LoadScene
            // scene-exit prefix and the map OnSelect prefix), so a throw here would
            // propagate out and abort the scene transition / the menu click. Fails
            // CLOSED - a capture we could not take reaps nothing, which is the pre-fix
            // status quo, never an unguarded delete.
            List<Recording> reapCapturedRecordings = null;
            string reapSkipReason = null;
            string reapTreeId = null;
            string reapTreeName = null;
            if (reapSidecars)
            {
                try
                {
                    reapCapturedRecordings = DiscardSidecarReap.CaptureTreeRecordings(host.activeTree);
                    reapTreeId = host.activeTree?.Id;
                    reapTreeName = host.activeTree?.TreeName;
                    var reapScenario = ParsekScenario.Instance;
                    reapSkipReason = DiscardSidecarReap.SkipReason(
                        reFlyMarkerActive: !object.ReferenceEquals(null, reapScenario)
                            && reapScenario.ActiveReFlySessionMarker != null,
                        mergeJournalActive: !object.ReferenceEquals(null, reapScenario)
                            && reapScenario.ActiveMergeJournal != null,
                        restoringActiveTree: ParsekFlight.restoringActiveTree);
                }
                catch (System.Exception ex)
                {
                    reapCapturedRecordings = null;
                    reapSkipReason = "capture-threw:" + ex.GetType().Name;
                    ParsekLog.Warn("Flight",
                        $"AutoDiscardActiveTreeCore: sidecar-reap capture threw " +
                        $"{ex.GetType().Name}: {ex.Message} - reap skipped, discard " +
                        $"continues (orphans retained) reason='{reason}'");
                }
            }

            // Preserve irreversible live-gameplay economy before tearing the tree down.
            // This (uncommitted) tree's contract completions / milestones / science are
            // tagged-but-uncommitted, so they were never ledger actions; the teardown
            // drops them while the recalc below would re-list an externally-accepted
            // contract as active (KSP keeps it finished -> dual-listing + duplicate
            // reward). Re-home the irreversible bits to the direct ledger first. Collect
            // ids BEFORE activeTree is nulled. This core does not purge the tree's own
            // store events (orphans; the direct ledger actions are the truth and the
            // orphaned tagged events are not recalc inputs). The one purge here is the
            // drop-site clear below, which removes a committed-restore attempt's same-id
            // post-cutoff tails only.
            if (host.activeTree?.Recordings != null && host.activeTree.Recordings.Count > 0)
            {
                var rehomeIds = CollectRehomeRecordingIds(host.activeTree);
                if (rehomeIds.Count > 0)
                    LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(
                        rehomeIds, $"AutoDiscardActiveTreeCore: {reason}");
            }

            // Rec-3 observability (see TreeDiscardService.DiscardPendingTree): report any
            // supply-route physical mutation that fired inside this discarded active-tree
            // window and persists un-reversed (funds+cargo both kept). Behavior-neutral;
            // rewind/RP-backed sessions are skipped inside the reporter. Read BEFORE
            // activeTree is nulled below.
            if (host.activeTree?.Recordings != null && host.activeTree.Recordings.Count > 0)
                Parsek.Logistics.RouteDiscardObservability.ReportDiscardLeakForRecordings(
                    host.activeTree.Recordings.Values,
                    Parsek.Logistics.RouteEventLog.Events,
                    ParsekScenario.IsReFlySessionActiveForQuickloadDiscard(),
                    $"AutoDiscardActiveTreeCore: {reason}");

            // Mirror OnSceneChangeRequested's pre-finalize prep so any
            // transient state is cleaned up before we drop the recorder.
            host.ClearSceneChangeTransientState();

            // Mirror DiscardActiveTreeForSuppressedSceneExit's recorder
            // teardown: ForceStop + clear ActiveRecorder reference before
            // nulling. Without ForceStop the recorder may still hold
            // buffered data; without clearing ActiveRecorder, the physics
            // patch retains a stale reference (regression-checked by
            // ExtendedRuntimeTests.cs:919's
            // PhysicsFramePatch_ActiveRecorder_NullWhenNotRecording).
            if (host.recorder != null)
            {
                if (host.recorder.IsRecording)
                    host.recorder.ForceStop();
                Patches.PhysicsFramePatch.ActiveRecorder = null;
                host.recorder = null;
            }

            if (host.backgroundRecorder != null)
            {
                host.backgroundRecorder.DiscardWithoutPersist(reason);
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                host.backgroundRecorder = null;
            }

            // This teardown drops the tree WITHOUT stashing it as pending, so its armed
            // committed-tree restore attempt must go with it: while the attempt outlives
            // its clone every OnSave defers the pending-event milestone flush, and its
            // same-id post-cutoff event tails must be purged with it or they become
            // durable on the next save. The helper purges first, then clears, and is
            // id-matched: it no-ops when a caller already discarded the attempt (the
            // no-op revert, the switch-segment discard, the pre-switch dialog discard)
            // or when the tree is another attempt's clone.
            CommittedTreeRestoreAttempt.ClearCommittedTreeRestoreAttemptForDroppedTree(
                host.activeTree?.Id, "auto-discard dropped the restore clone",
                preserveIrreversibleTails: true);
            host.activeTree = null;

            // A discarded active tree must not leave an armed switch-segment
            // session pointing at it. Without this, the idle-on-pad scene-exit
            // fast path (SceneExitInterceptor) tears the tree down but the
            // session (already persisted by the pre-exit OnSave) survives
            // into the save and resurfaces as a pending tree on the
            // next load. ClearSwitchSegmentSession no-ops when none is armed
            // (e.g. the Case B no-session Switch-To discard).
            ParsekScenario.Instance?.ClearSwitchSegmentSession(
                $"active-tree-auto-discard: {reason}");

            // Explicit-id sidecar reap for the ids this teardown just dropped.
            // Runs AFTER the in-memory removal so RecordingStore's known-id set is
            // the authority on what survives; failures inside are counted and
            // logged, never thrown, so a locked file cannot abort the discard.
            if (reapSidecars)
            {
                try
                {
                    DiscardSidecarReap.ReapDiscardedTreeSidecars(
                        reapCapturedRecordings, reapSkipReason, reason,
                        reapTreeId, reapTreeName);
                }
                catch (System.Exception ex)
                {
                    ParsekLog.Warn("Flight",
                        $"AutoDiscardActiveTreeCore: sidecar reap threw " +
                        $"{ex.GetType().Name}: {ex.Message} - discard continues " +
                        $"(orphans retained) reason='{reason}'");
                }
            }

            // No pending tree was stashed (we discard pre-finalize).
            // Roll back any in-flight ledger entries from the aborted
            // recording.
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions(
                ParsekScenario.GetCurrentTimelineUTForLedgerRecalc(),
                ledgerRecalcReason);
        }

        /// <summary>
        /// The ids <see cref="AutoDiscardActiveTreeCore"/> hands to the irreversible-gameplay
        /// re-home: every non-empty recording key of the tree, ordinal.
        /// </summary>
        internal static HashSet<string> CollectRehomeRecordingIds(RecordingTree tree)
        {
            var rehomeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rid in tree.Recordings.Keys)
                if (!string.IsNullOrEmpty(rid))
                    rehomeIds.Add(rid);
            return rehomeIds;
        }

        private void FinalizeTreeOnSceneChangeCore(
            GameScenes scene,
            double commitUT,
            bool logRecorderState)
        {
            // #267: skip if restore coroutine is mid-yield — it owns activeTree/recorder
            if (ParsekFlight.restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    $"FinalizeTreeOnSceneChange: skipped — restore coroutine in progress (dest={scene})");
                return;
            }

            if (PendingTreeSlot.TryConsumeNextTreeSceneExitCommitSuppression(scene, out string suppressReason))
            {
                DiscardActiveTreeForSuppressedSceneExit(
                    scene,
                    commitUT,
                    suppressReason,
                    logRecorderState);
                return;
            }

            if (logRecorderState)
                RecorderStateLog.RecState("FinalizeTreeOnSceneChange:entry", host.CaptureRecorderState());

            // Checkpoint all background vessels before finalization.
            // This captures clean orbital reference points at the scene-change boundary.
            if (host.backgroundRecorder != null)
            {
                host.backgroundRecorder.CheckpointAllVessels(commitUT);
                ParsekLog.Info("Checkpoint",
                    "Scene change: checkpointed all background vessels before finalization");
            }

            if (scene == GameScenes.FLIGHT)
            {
                // FLIGHT→FLIGHT: the player quickloaded, reverted, or vessel-switched
                // into a scene reload. We can't fully tell which until OnLoad runs,
                // but we CAN check the live vesselSwitchPending flag (set from KSP's
                // onVesselSwitching event once the scene is flight-ready). CAVEAT: a far
                // Switch-To does NOT set it - stock FlightGlobals.setActiveVessel fires
                // onVesselSwitchingToUnloaded and returns into StartAndFocusVessel before
                // onVesselSwitching - so a real far switch lands on the Limbo stash below
                // (todo VESSEL-SWITCH-266-UNREACHABLE-FOR-FAR-SWITCH).
                //
                // Bug #266: when this is a vessel switch, pre-transition the tree at
                // stash time — flush the recorder, move the active recording's PID
                // into BackgroundMap, null ActiveRecordingId — and stash as
                // LimboVesselSwitch. The restore coroutine just reinstalls and
                // optionally promotes the new vessel from BackgroundMap. This
                // preserves the in-progress mission across the scene reload, exactly
                // matching the in-session OnVesselSwitchComplete behavior.
                //
                // Otherwise (quickload, revert, or any case where we can't safely
                // pre-transition — see CanPreTransitionForVesselSwitch), fall back
                // to the legacy Limbo stash. OnLoad's revert detection dispatch will
                // then either restore-and-resume (quickload) or finalize-and-commit
                // (revert). See docs/dev/plans/quickload-resume-recording.md (Bug C).
                bool isVesselSwitch = host.IsVesselSwitchPendingFresh();
                if (isVesselSwitch && host.CanPreTransitionForVesselSwitch())
                {
                    host.StashActiveTreeForVesselSwitch(commitUT);
                }
                else
                {
                    if (isVesselSwitch)
                    {
                        ParsekLog.Info("Flight",
                            "FinalizeTreeOnSceneChange: vessel switch detected but " +
                            "pre-transition is unsafe (pendingTreeDockMerge / " +
                            "pendingSplit) — falling back to legacy Limbo stash; " +
                            "OnLoad safety net will finalize");
                    }
                    host.StashActiveTreeAsPendingLimbo(commitUT);
                }
            }
            else if (ParsekFlight.ShouldStashLiveTreeForSaveFileLoad(
                scene,
                SaveFileLoadIntent.IsArmed || NoSaveSceneExit.IsArmedFor(scene),
                RevertDetector.PendingKind != RevertKind.None))
            {
                // Also stock's quit without saving (NoSaveSceneExit, owner ruling 2026-11-01):
                // the destination reloads the older persistent.sfs, so the flight since that
                // save is dropped with the world, as for a save-file load.
                // A save-file load out of flight into a non-flight save (F9 or the load dialog
                // into a Space Center quicksave): the next OnLoad is Cold and rebuilds Parsek from
                // that save (design-load-restores-the-save.md 4.1), so the live flight is the
                // abandoned future, not a finished one. Stash it as Limbo, as an F9 into a flight
                // save does; the cold load's session reset drops it from memory only. Committing
                // it here would run a commit for a flight the load is about to forget.
                string stashCause = NoSaveSceneExit.IsArmedFor(scene)
                    ? "quit without saving"
                    : "save-file load";
                ParsekLog.Info("Flight",
                    $"FinalizeTreeOnSceneChange: {stashCause} out of flight (dest={scene}) - stashing the "
                    + $"live tree '{host.activeTree.TreeName}' as pending Limbo instead of committing it; the "
                    + "cold load replaces it from the save");
                if (NoSaveSceneExit.IsArmedFor(scene))
                    NoSaveSceneExit.NoteStashedTree(host.activeTree.Id, NoSaveSceneExit.CurrentSaveFolder());
                host.StashActiveTreeAsPendingLimbo(commitUT);
            }
            else if (!SceneExitInterceptor.IsAutoMergeForSceneExitLive(
                scene, "FinalizeTreeOnSceneChange"))
            {
                // autoMerge OFF: safe finalization but preserve snapshots for dialog in KSC/TS.
                // A rewind's own Space Center load never takes this branch: it commits like
                // auto-merge on (SceneExitInterceptor.ResolveSceneExitAutoMerge).
                // Uses isSceneExit: true; FinalizeIndividualRecording's #289 re-snapshot path
                // updates VesselSnapshot for stable-terminal recordings (Landed/Splashed/Orbiting)
                // so the dialog and spawn-at-end safety check see the correct sit field.
                FinalizeTreeRecordings(host.activeTree, commitUT, isSceneExit: true);

                // In-flight Recover (operator ruling 2026-10-01): the merge dialog commits
                // before stock recovers the vessel at the Space Center, so stamp the
                // recovered launch's tip Recovered here, while the tree is not committed.
                InFlightRecoveryRequest.ApplyAtSceneExit(
                    host.activeTree, scene, "scene-exit autoMerge-off");

                // #289: KSP's auto-save fired BEFORE this code path runs (it ran inside
                // OnSceneChangeRequested, before our scene-exit cleanup). Any sidecar files
                // dirtied by FinalizeTreeRecordings (e.g. fresh snapshots from the #289
                // re-snapshot) won't reach disk via the normal OnSave→FlushDirtyFiles path
                // before the next OnLoad runs TryRestoreActiveTreeNode, which hydrates the
                // restored tree from those sidecars. Force-write them now so the post-finalize
                // state survives the round-trip.
                int forcedWrites = ForceWriteDirtySidecarFiles(host.activeTree);
                if (forcedWrites > 0)
                {
                    ParsekLog.Info("Flight",
                        $"CommitTreeSceneExit (autoMerge off): force-wrote {forcedWrites} dirty sidecar(s) " +
                        $"after finalize so post-finalize state survives the next OnLoad [#289]");
                }

                // Default state = Finalized. TryRestoreActiveTreeNode relies on this to
                // skip replacing the in-memory tree with the stale .sfs version (which was
                // written before FinalizeTreeRecordings and lacks MaxDistanceFromLaunch,
                // terminal states, and re-snapshotted vessels). See bug #290d.
                PendingTreeSlot.StashPendingTree(host.activeTree);
                ParsekLog.Info("Flight",
                    $"CommitTreeSceneExit (autoMerge off): stashed tree '{host.activeTree.TreeName}' with snapshots preserved");
            }
            else
            {
                // Scene exit: null snapshots (ghost-only, no spawning outside Flight)
                CommitTreeSceneExit(commitUT, scene);
            }

            // A recovery request this exit did not consume (FLIGHT destination, or no
            // tree finalized above) must not outlive the transition.
            InFlightRecoveryRequest.Clear("scene-exit finalize dest=" + scene);

            // Clean up tree state
            host.recorder = null;
            if (host.backgroundRecorder != null)
            {
                host.backgroundRecorder.Shutdown();
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                host.backgroundRecorder = null;
            }
            host.activeTree = null;
        }

        internal void DiscardActiveTreeForSuppressedSceneExit(
            GameScenes scene,
            double commitUT,
            string suppressReason,
            bool logRecorderState)
        {
            string treeName = host.activeTree.TreeName;
            int recordingCount = host.activeTree.Recordings.Count;
            string activeRecordingId = host.activeTree.ActiveRecordingId ?? "<none>";
            ParsekLog.Info("Flight",
                $"FinalizeTreeOnSceneChange: suppressed tree scene-exit commit " +
                $"dest={scene} UT={commitUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"tree='{treeName}' recordings={recordingCount} active='{activeRecordingId}' " +
                $"reason='{suppressReason ?? "<unspecified>"}' - discarding in-memory tree without STASH");
            if (logRecorderState)
                RecorderStateLog.RecState("FinalizeTreeOnSceneChange:suppressed-entry", host.CaptureRecorderState());

            if (host.recorder != null)
            {
                if (host.recorder.IsRecording)
                    host.recorder.ForceStop();
                Patches.PhysicsFramePatch.ActiveRecorder = null;
                host.recorder = null;
            }

            // The Re-Fly restore path installs a loaded/detached active tree:
            // TryRestoreActiveTreeNode clones/reconciles committed recordings into
            // that tree, so background recorder mutations from the attempt are
            // discarded here instead of leaking into CommittedRecordings.
            if (host.backgroundRecorder != null)
            {
                host.backgroundRecorder.DiscardWithoutPersist(suppressReason);
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                host.backgroundRecorder = null;
            }

            // Discarded without a pending stash, so the armed committed-tree restore
            // attempt must be cleared with the tree (id-matched, reads the id before the
            // null below): a stale attempt keeps deferring the OnSave milestone flush.
            // A suppressed scene exit is the Discard Re-fly / rewind load: the loaded save
            // reverts KSP, so nothing of the attempt is preserved.
            CommittedTreeRestoreAttempt.ClearCommittedTreeRestoreAttemptForDroppedTree(
                host.activeTree?.Id, "suppressed scene exit dropped the restore clone",
                preserveIrreversibleTails: false);
            host.activeTree = null;
            if (logRecorderState)
                RecorderStateLog.RecState("FinalizeTreeOnSceneChange:suppressed-post", host.CaptureRecorderState());
        }

        internal static bool ShouldApplyFinalSegmentPhaseFromCapture(
            Recording treeRec,
            string activeRecordingId,
            Recording captureAtStop,
            RecordingTree treeContext = null)
        {
            return treeRec != null
                && captureAtStop != null
                && !string.IsNullOrEmpty(captureAtStop.SegmentPhase)
                && ParsekFlight.ShouldAllowFinalEndpointSegmentPhase(treeRec, activeRecordingId, treeContext);
        }

        internal static bool HasCommittedChainSegmentOwnership(Recording rec)
        {
            return rec != null
                && !string.IsNullOrEmpty(rec.ChainId)
                && rec.ChainIndex >= 0;
        }

        internal static bool HasExplicitReFlyProvisionalIdentity(Recording rec)
        {
            return rec != null
                && (!string.IsNullOrEmpty(rec.ProvisionalForRpId)
                    || !string.IsNullOrEmpty(rec.CreatingSessionId));
        }

        internal static bool SetFinalSegmentPhase(
            Recording rec,
            string phase,
            string bodyName,
            string source,
            string sourceRecordingId = null)
        {
            if (rec == null || string.IsNullOrEmpty(phase))
                return false;

            string oldPhase = rec.SegmentPhase;
            string oldBody = rec.SegmentBodyName;
            if (string.Equals(oldPhase, phase, StringComparison.Ordinal)
                && string.Equals(oldBody, bodyName, StringComparison.Ordinal))
            {
                return false;
            }

            rec.SegmentPhase = phase;
            rec.SegmentBodyName = bodyName;
            rec.InvalidateSegmentBodyDisplayLabelCache();

            ParsekLog.Verbose("Flight",
                $"Final SegmentPhase applied from {source}: rec={rec.RecordingId ?? "<none>"} " +
                $"old={FormatSegmentPhaseForLog(oldBody, oldPhase)} " +
                $"new={FormatSegmentPhaseForLog(bodyName, phase)} " +
                $"sourceRec={sourceRecordingId ?? "<none>"}");
            return true;
        }

        internal static string FormatSegmentPhaseForLog(string bodyName, string phase)
        {
            if (string.IsNullOrEmpty(phase))
                return "<none>";
            return string.IsNullOrEmpty(bodyName)
                ? phase
                : bodyName + " " + phase;
        }

        /// <summary>
        /// Called when the merge dialog commits a tree. Re-evaluates ghost chains
        /// so newly committed recordings get ghost map ProtoVessels immediately.
        /// Route creation is no longer offered here: eligible Supply Runs surface
        /// as derived candidates in the Logistics window (see
        /// <see cref="Logistics.RouteCandidateFinder"/>) instead of a commit-time popup.
        /// </summary>
        internal void OnTreeCommittedFromMergeDialog(RecordingTree tree)
        {
            ParsekLog.Info("GhostMap", "Tree committed from merge dialog - re-evaluating ghost chains");
            host.EvaluateAndApplyGhostChains();
        }

        /// <summary>
        /// Commits the active recording tree IN FLIGHT: finalizes all recordings, spawns
        /// leaf vessels, reserves crew. The active vessel stays live (VesselSpawned=true).
        /// <para>Caller set, re-derived 2026-09-11 (finding P22): the "Commit Flight" button
        /// this docstring used to name was removed, and the live callers are now the
        /// pre-switch decision dialog's two Merge handlers
        /// (<c>Patches/MapFocusObjectOnSelectPatch.cs:624</c> and <c>:813</c> - case A's
        /// prior-session commit and case B's active-tree commit) plus the harness
        /// <c>CommitTree</c> seam verb (<c>TestCommands/ParsekTestCommandAddon.cs:2016</c>).
        /// The merge DIALOG's own commit is a different path
        /// (<c>MergeDialog.MergeCommit</c>) and does not come through here.</para>
        /// <para>Both paths run <see cref="CommittedListOptimizer.RunOptimizationPass"/> after the
        /// commit. This one spawns / adopts the leaves AFTER the pass, so the spawn stamps land
        /// on the post-split chain tips, and moves the live active vessel's stamp onto its tip
        /// (<see cref="InFlightCommitOptimization"/>).</para>
        /// </summary>
        public void CommitTreeFlight()
        {
            if (host.activeTree == null)
            {
                ParsekLog.ScreenMessage("No active tree to commit", 2f);
                return;
            }

            double commitUT = host.CurrentUniversalTime();
            ParsekLog.Info("Flight", $"CommitTreeFlight: starting tree commit at UT={commitUT:F1}");

            // Finalize all recordings (active + background)
            FinalizeTreeRecordings(host.activeTree, commitUT, isSceneExit: false);

            // Name the finalized terminal verdict for every recording the commit is
            // about to consume. "starting tree commit at UT=" above only proves the
            // path RAN; it is emitted unconditionally and says nothing about WHERE
            // the tree ended, so a commit made while parked in a FOREIGN body's SOI
            // is indistinguishable in the log from one made at Kerbin. These lines
            // are the log witness for the terminal classification and the terminal
            // orbit body, which is what the autotest ORBIT lane (B11/B12) asserts.
            ParsekFlight.LogCommitTerminalVerdicts(host.activeTree);

            // Identify the active vessel's recording
            string activeRecId = host.activeTree.ActiveRecordingId;
            Recording activeRec = null;
            if (activeRecId != null)
                host.activeTree.Recordings.TryGetValue(activeRecId, out activeRec);

            // Mark active vessel's recording as already spawned
            if (activeRec != null)
            {
                activeRec.VesselSpawned = true;
                activeRec.SpawnedVesselPersistentId = host.recorder != null ? host.recorder.RecordingVesselId :
                    host.ActiveVesselPersistentIdOrZero();
                activeRec.LastAppliedResourceIndex = activeRec.Points.Count - 1;
            }

            // Tree resources are already live in the game — mark as applied via the
            // shared tree-scoped primitive. Phase C of the ledger / lump-sum reconciliation
            // fix (`docs/dev/done/plans/fix-ledger-lump-sum-reconciliation.md`) replaced the
            // inline equivalent with this call so the in-flight commit and merge-dialog
            // commit paths stay in lockstep.
            int markedCount = RecordingStore.MarkTreeAsApplied(host.activeTree);
            ParsekLog.Info("Flight",
                $"CommitTreeFlight: marked {markedCount}/{host.activeTree.Recordings.Count} recordings as fully applied");

            // Get spawnable leaves before commit (tree is consumed by CommitTree)
            var spawnableLeaves = host.activeTree.GetSpawnableLeaves();

            // Commit tree to storage
            TreeCommitService.CommitTree(host.activeTree);

            // Same position as MergeDialog.MergeCommit: after the commit + MarkTreeAsApplied,
            // before the ledger notify. Without it a tree committed in flight kept its idle
            // tails (and unsplit / unmerged segments) until the next cold load.
            string activeTipId = RunInFlightCommitOptimizationPass(activeRec, activeRecId);

            // Recalculate crew reservations
            LedgerOrchestrator.NotifyLedgerTreeCommitted(host.activeTree);

            // Spawn all non-active leaf vessels
            host.SpawnTreeLeaves(host.activeTree, activeTipId);

            // Crew swap on active vessel
            int swapped = CrewReservationManager.SwapReservedCrewInFlight();
            if (swapped > 0)
                ParsekLog.Info("Flight", $"CommitTreeFlight: swapped {swapped} crew on active vessel");

            int spawnCount = InFlightCommitOptimization.CountSpawnedLeafTips(
                spawnableLeaves, RecordingStore.CommittedRecordings, activeTipId);

            // Clear state
            var treeName = host.activeTree.TreeName;
            var committedTreeForPrompt = host.activeTree;
            host.activeTree = null;
            if (host.backgroundRecorder != null)
            {
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                host.backgroundRecorder = null;
            }
            host.recorder = null;
            host.lastPlaybackIndex = 0;

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions(
                ParsekScenario.GetCurrentTimelineUTForLedgerRecalc(),
                "flight-tree-commit");

            host.Log($"CommitTreeFlight: committed tree \"{treeName}\" — {spawnCount} vessel(s) spawned");
            if (spawnCount > 0)
                ParsekLog.ScreenMessage($"Tree merged to timeline! {spawnCount} vessel(s) spawned.", 3f);
            else
                ParsekLog.ScreenMessage("Tree merged to timeline!", 3f);

            // M6 Record-Supply-Run helper: one-time non-blocking prompt when
            // this commit produced an eligible route candidate. Covers the
            // in-flight commit path AND the pre-switch-dialog patch paths
            // (both route through CommitTreeFlight); the merge-dialog path
            // hooks in MergeDialog.MergeCommit. Internally gated (test batch,
            // restore window, prompted-once, dismissed) and never throws.
            Logistics.RouteRunPrompt.NotifyTreeCommitted(committedTreeForPrompt);
        }

        /// <summary>
        /// Runs <see cref="CommittedListOptimizer.RunOptimizationPass"/> for an in-flight tree commit
        /// (boring-tail trim, env splits, chain merges) and moves the live active vessel's
        /// spawn stamp onto its post-pass chain tip. Returns the id the rest of the commit
        /// must treat as the active vessel's recording. Skipped (the next cold load optimizes
        /// instead) while a Re-Fly session or a merge journal is live; see
        /// <see cref="InFlightCommitOptimization.DecideSkipReason"/>.
        /// </summary>
        private string RunInFlightCommitOptimizationPass(Recording activeRec, string activeRecId)
        {
            var scenario = ParsekScenario.Instance;
            bool reFly = !object.ReferenceEquals(null, scenario)
                && scenario.ActiveReFlySessionMarker != null;
            bool journal = !object.ReferenceEquals(null, scenario)
                && scenario.ActiveMergeJournal != null;
            string skipReason = InFlightCommitOptimization.DecideSkipReason(reFly, journal);
            if (skipReason != null)
            {
                ParsekLog.Info("Flight",
                    $"CommitTreeFlight: optimization pass skipped reason={skipReason} " +
                    "(the next cold load optimizes this tree)");
                return activeRecId;
            }

            int before = RecordingStore.CommittedRecordings.Count;
            bool stampMoved = false;
            // Guarded: a throw is logged as an Error and the commit continues to the ledger
            // notify and leaf spawn with whatever the committed list holds.
            string activeTipId = InFlightCommitOptimization.RunPassAndResolveActiveTip(
                CommittedListOptimizer.RunOptimizationPass,
                () =>
                {
                    Recording activeTip = InFlightCommitOptimization.ResolveChainTip(
                        activeRec, RecordingStore.CommittedRecordings);
                    stampMoved = InFlightCommitOptimization.CarrySpawnStampToTip(activeRec, activeTip);
                    return activeTip?.RecordingId;
                },
                activeRecId,
                out bool passThrew,
                out bool stampMoveThrew);
            int after = RecordingStore.CommittedRecordings.Count;
            ParsekLog.Info("Flight",
                $"CommitTreeFlight: optimization pass ran committed={before}->{after} " +
                $"activeRec={activeRecId ?? "<none>"} activeTip={activeTipId ?? "<none>"} " +
                $"spawnStampMoved={stampMoved} passThrew={passThrew} stampMoveThrew={stampMoveThrew}");
            return activeTipId;
        }

        /// <summary>
        /// Finalizes the active tree on scene exit and stashes it as pending for
        /// <see cref="ParsekScenario"/> OnLoad to commit. Preserves stable-terminal
        /// (Landed/Splashed/Orbiting) snapshots for spawn-at-end (the silent
        /// full-fidelity commit) and force-writes dirty sidecars so they are durable;
        /// nulls non-spawnable snapshots (ghost visual + crew reservation released).
        /// </summary>
        void CommitTreeSceneExit(double commitUT, GameScenes scene)
        {
            if (host.activeTree == null) return;

            ParsekLog.Info("Flight", $"CommitTreeSceneExit: finalizing tree at UT={commitUT:F1}");
            RecorderStateLog.RecState("CommitTreeSceneExit:entry", host.CaptureRecorderState());

            // Finalize all recordings
            FinalizeTreeRecordings(host.activeTree, commitUT, isSceneExit: true);

            // In-flight Recover (operator ruling 2026-10-01): the auto-merge commit runs in
            // the Space Center's OnLoad, before stock recovers the vessel there, so stamp
            // the recovered launch's tip Recovered now, while the tree is not committed.
            InFlightRecoveryRequest.ApplyAtSceneExit(
                host.activeTree, scene, "scene-exit autoMerge-on");

            // Null vessel snapshots for recordings that don't need spawn-at-end.
            // Preserve snapshots for stable-terminal recordings (Landed/Splashed/Orbiting)
            // so the snapshot's sit field survives the save→load round-trip and the
            // spawn-at-end safety check doesn't block with "snapshot situation unsafe". (#289)
            //
            // Bug #271: preserve GhostVisualSnapshot before nulling VesselSnapshot so ghost
            // rendering still has vessel geometry. Without this, the ghost mesh builder falls
            // back to the green sphere for any recording whose VesselSnapshot was nulled here.
            ReleaseNonSpawnableSceneExitSnapshots(host.activeTree);

            // #289 (silent full-fidelity auto-commit): force-write dirty sidecars now,
            // mirroring the autoMerge=OFF scene-exit branch. KSP's auto-save already
            // fired (inside OnSceneChangeRequested, before this cleanup), so the
            // finalized stable-terminal VesselSnapshot dirtied by FinalizeTreeRecordings
            // would not otherwise reach disk before the next OnLoad. Without this the
            // silent full-fidelity commit's spawn-at-end could not re-hydrate the
            // finalized _vessel.craft on a cold load — see
            // docs/dev/done/plans/silent-full-fidelity-autocommit.md.
            int forcedWrites = ForceWriteDirtySidecarFiles(host.activeTree);
            if (forcedWrites > 0)
            {
                ParsekLog.Info("Flight",
                    $"CommitTreeSceneExit: force-wrote {forcedWrites} dirty sidecar(s) after " +
                    "finalize so the post-finalize snapshot survives the next OnLoad [#289]");
            }

            // Stash as pending tree -- committed by ParsekScenario.OnLoad
            // (full-fidelity MergeCommit when it qualifies, else ghost-only).
            PendingTreeSlot.StashPendingTree(host.activeTree);

            ParsekLog.Info("Flight", $"CommitTreeSceneExit: stashed pending tree '{host.activeTree.TreeName}'");
            RecorderStateLog.RecState("CommitTreeSceneExit:post", host.CaptureRecorderState());
        }

        /// <summary>
        /// True for the stable terminals (Landed / Splashed / Orbiting) whose vessel snapshot
        /// the auto-merge scene-exit commit keeps for spawn-at-end.
        /// </summary>
        internal static bool ShouldKeepVesselSnapshotForSpawnAtEnd(Recording rec)
        {
            return rec.TerminalStateValue.HasValue
                && (rec.TerminalStateValue.Value == TerminalState.Landed
                    || rec.TerminalStateValue.Value == TerminalState.Splashed
                    || rec.TerminalStateValue.Value == TerminalState.Orbiting);
        }

        /// <summary>
        /// The auto-merge scene-exit commit's snapshot release: for every recording whose
        /// snapshot is not kept for spawn-at-end, copies the vessel snapshot into an empty
        /// ghost visual snapshot, releases the snapshot's crew reservations and nulls the
        /// vessel snapshot.
        /// </summary>
        internal static void ReleaseNonSpawnableSceneExitSnapshots(RecordingTree tree)
        {
            foreach (var rec in tree.Recordings.Values)
            {
                bool keepForSpawn = ShouldKeepVesselSnapshotForSpawnAtEnd(rec);
                if (!keepForSpawn)
                {
                    if (rec.GhostVisualSnapshot == null && rec.VesselSnapshot != null)
                        rec.GhostVisualSnapshot = rec.VesselSnapshot.CreateCopy();
                    // Unreserve crew before nulling, matching the dialog path's
                    // ApplyVesselDecisions ghost-only branch: a non-spawnable leaf must
                    // not keep its kerbals reserved (silent full-fidelity crew parity).
                    CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                    rec.VesselSnapshot = null;
                }
            }
        }

        /// <summary>
        /// Force-writes every dirty recording's sidecar files (epoch unchanged) and returns
        /// how many writes succeeded. Both scene-exit commits (auto-merge on and off) run it
        /// after finalize, because KSP's auto-save already fired before the finalize dirtied
        /// them.
        /// </summary>
        internal static int ForceWriteDirtySidecarFiles(RecordingTree tree)
        {
            int forcedWrites = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec.FilesDirty)
                {
                    if (RecordingSidecarStore.SaveRecordingFiles(rec, incrementEpoch: false))
                        forcedWrites++;
                }
            }
            return forcedWrites;
        }

        /// <summary>
        /// Finalizes all recordings in the tree: stops active recorder, flushes data,
        /// captures terminal state/orbit/position for all recordings.
        /// </summary>
        internal void FinalizeTreeRecordings(RecordingTree tree, double commitUT, bool isSceneExit)
        {
            string activeRecordingId = tree?.ActiveRecordingId;
            bool activeRecorderDestroyedDuringRecording =
                host.recorder != null && host.recorder.VesselDestroyedDuringRecording;

            // 1. Stop and flush the active recorder
            if (host.recorder != null)
            {
                if (host.recorder.IsRecording)
                {
                    if (host.recorder.IsBackgrounded)
                        host.recorder.IsRecording = false;
                    else
                        host.recorder.ForceStop();
                }
                host.FlushRecorderToTreeRecording(host.recorder, tree);

                // Copy rewind save + reserved budget to the root recording.
                ParsekFlight.CopyRewindSaveToRoot(tree, host.recorder,
                    logTag: "FinalizeTreeRecordings");
            }

            // 2. Finalize background recorder (close orbit segments, flush data)
            if (host.backgroundRecorder != null)
                host.backgroundRecorder.FinalizeAllForCommit(commitUT);

            ParsekFlight.FinalizeTreeRecordingsAfterFlush(
                tree,
                commitUT,
                isSceneExit,
                resolveFinalizationCache: rec => host.ResolveFinalizationCacheForRecording(tree, rec),
                resolveDestroyedDuringRecording: rec =>
                    activeRecorderDestroyedDuringRecording
                    && !string.IsNullOrEmpty(activeRecordingId)
                    && string.Equals(rec?.RecordingId, activeRecordingId, StringComparison.Ordinal));
        }

        internal static bool IsActiveRecorderCacheCandidate(
            RecordingTree tree,
            Recording recording,
            FlightRecorder activeRecorder)
        {
            if (recording == null || activeRecorder == null)
                return false;

            if (tree != null
                && !string.IsNullOrEmpty(tree.ActiveRecordingId)
                && string.Equals(
                    tree.ActiveRecordingId,
                    recording.RecordingId,
                    StringComparison.Ordinal))
            {
                return true;
            }

            return recording.VesselPersistentId != 0
                && recording.VesselPersistentId == activeRecorder.RecordingVesselId;
        }

        internal static bool CacheIdentityMatchesRecording(
            Recording recording,
            RecordingFinalizationCache cache)
        {
            if (recording == null || cache == null)
                return false;

            if (!string.IsNullOrEmpty(recording.RecordingId)
                && !string.IsNullOrEmpty(cache.RecordingId)
                && !string.Equals(
                    recording.RecordingId,
                    cache.RecordingId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            return recording.VesselPersistentId == 0
                || cache.VesselPersistentId == 0
                || recording.VesselPersistentId == cache.VesselPersistentId;
        }

        internal static bool TryApplyFinalizationCacheFallback(
            Recording recording,
            RecordingFinalizationCache cache,
            string consumerPath,
            bool allowStale,
            out RecordingFinalizationCacheApplyResult result,
            bool allowAlreadyFinalizedRepair = false)
        {
            var options = new RecordingFinalizationCacheApplyOptions
            {
                ConsumerPath = consumerPath,
                AllowStale = allowStale,
                AllowAlreadyFinalizedRepair = allowAlreadyFinalizedRepair
            };

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                recording,
                cache,
                options,
                out result);

            if (applied)
            {
                ParsekLog.Info("Flight",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Finalization source=cache consumer={0} rec={1} terminal={2} " +
                        "terminalUT={3:F3} appendedSegments={4} staleAllowed={5}",
                        consumerPath ?? "(null)",
                        recording?.DebugName ?? "(null)",
                        result.TerminalState?.ToString() ?? "(null)",
                        result.TerminalUT,
                        result.AppendedSegmentCount,
                        allowStale));

                if (cache.Status == FinalizationCacheStatus.Stale)
                {
                    ParsekLog.Warn("Flight",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Finalization source=cache applied stale cache consumer={0} rec={1} " +
                            "terminal={2} terminalUT={3:F3} owner={4} cachedAtUT={5:F3}",
                            consumerPath ?? "(null)",
                            recording?.DebugName ?? "(null)",
                            result.TerminalState?.ToString() ?? "(null)",
                            result.TerminalUT,
                            cache.Owner,
                            cache.CachedAtUT));
                }
            }

            return applied;
        }

        internal static bool IsCacheTerminalAtOrBeforeCommit(
            RecordingFinalizationCache cache,
            double commitUT)
        {
            if (cache == null)
                return false;
            if (double.IsNaN(commitUT) || double.IsInfinity(commitUT))
                return false;
            if (double.IsNaN(cache.TerminalUT) || double.IsInfinity(cache.TerminalUT))
                return false;

            return cache.TerminalUT <= commitUT + 1e-6;
        }

        internal static void LogDestroyedCacheRepairRejected(
            Recording recording,
            RecordingFinalizationCache cache,
            double commitUT,
            TerminalState existing,
            string reason)
        {
            ParsekLog.VerboseRateLimited(
                "Flight",
                $"finalization-cache-repair-rejected-{recording?.RecordingId ?? "null"}-{reason}",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Finalization cache repair skipped: rec={0} reason={1} " +
                    "existingTerminal={2} cacheTerminal={3} terminalUT={4:F3} commitUT={5:F3}",
                    recording?.DebugName ?? "(null)",
                    reason ?? "(null)",
                    existing,
                    cache?.TerminalState?.ToString() ?? "(null)",
                    cache != null ? cache.TerminalUT : double.NaN,
                    commitUT));
        }

        internal static bool TryResolveFinalEndpointSegmentPhase(
            Recording rec,
            bool finalizeVesselFound,
            Vessel finalizeVessel,
            out string phase,
            out string bodyName,
            out string source,
            out string skipReason)
        {
            phase = null;
            bodyName = null;
            source = null;
            skipReason = null;

            if (rec == null)
            {
                skipReason = "recording-null";
                return false;
            }

            if (finalizeVesselFound
                && SegmentPhaseClassifier.TryClassify(finalizeVessel, out phase, out bodyName))
            {
                source = "live-vessel";
                return true;
            }

            if (TryResolveTerminalStateSegmentPhase(rec, out phase, out bodyName, out source))
                return true;

            if (TryResolveTrackSectionEnvironmentSegmentPhase(
                rec,
                out phase,
                out bodyName,
                out source,
                out skipReason))
            {
                return true;
            }

            if (TryResolveAbsoluteEndpointPointSegmentPhase(
                rec,
                out phase,
                out bodyName,
                out source,
                out skipReason))
            {
                return true;
            }

            if (string.IsNullOrEmpty(skipReason))
                skipReason = "no-reliable-endpoint-evidence";
            return false;
        }

        internal static bool TryResolveTerminalStateSegmentPhase(
            Recording rec,
            out string phase,
            out string bodyName,
            out string source)
        {
            phase = null;
            bodyName = null;
            source = null;

            if (rec == null || !rec.TerminalStateValue.HasValue)
                return false;

            TerminalState terminal = rec.TerminalStateValue.Value;
            if ((terminal == TerminalState.Orbiting || terminal == TerminalState.Docked)
                && !string.IsNullOrEmpty(rec.TerminalOrbitBody))
            {
                phase = "exo";
                bodyName = rec.TerminalOrbitBody;
                source = "terminal-orbit";
                return true;
            }

            if (terminal == TerminalState.Landed || terminal == TerminalState.Splashed)
            {
                if (!TryGetTerminalSurfaceBodyName(rec, out bodyName))
                {
                    bool finalEndpointIsRelative =
                        TryGetFinalTrackSection(rec, out TrackSection finalSection)
                        && finalSection.referenceFrame == ReferenceFrame.Relative;
                    if (!finalEndpointIsRelative)
                        bodyName = GetBestEndpointBodyName(rec, includeFlatPointBody: false);
                }

                if (!string.IsNullOrEmpty(bodyName))
                {
                    phase = "surface";
                    source = "terminal-surface";
                    return true;
                }
            }

            return false;
        }

        internal static bool TryResolveTrackSectionEnvironmentSegmentPhase(
            Recording rec,
            out string phase,
            out string bodyName,
            out string source,
            out string skipReason)
        {
            phase = null;
            bodyName = null;
            source = null;
            skipReason = null;

            if (!TryGetFinalTrackSection(rec, out TrackSection section))
                return false;

            bool isRelative = section.referenceFrame == ReferenceFrame.Relative;
            if (isRelative)
            {
                if (!TryGetReliableRelativeEndpointBodyName(rec, section, out bodyName, out source))
                {
                    skipReason = "relative-endpoint-unresolved-no-terminal-body-or-absolute-shadow";
                    return false;
                }
            }
            else
            {
                bodyName = GetBestEndpointBodyName(rec, includeFlatPointBody: true);
            }

            phase = SegmentPhaseClassifier.EnvironmentToPhase(section.environment);
            if (string.IsNullOrEmpty(source))
                source = "track-section-environment";
            return true;
        }

        internal static bool TryGetTerminalSurfaceBodyName(Recording rec, out string bodyName)
        {
            bodyName = null;
            if (rec == null)
                return false;

            if (rec.TerminalPosition.HasValue
                && !string.IsNullOrEmpty(rec.TerminalPosition.Value.body))
            {
                bodyName = rec.TerminalPosition.Value.body;
                return true;
            }

            if (rec.SurfacePos.HasValue
                && !string.IsNullOrEmpty(rec.SurfacePos.Value.body))
            {
                bodyName = rec.SurfacePos.Value.body;
                return true;
            }

            return false;
        }

        internal static bool TryGetReliableRelativeEndpointBodyName(
            Recording rec,
            TrackSection section,
            out string bodyName,
            out string source)
        {
            bodyName = null;
            source = null;

            if (rec != null)
            {
                if (!string.IsNullOrEmpty(rec.TerminalOrbitBody))
                {
                    bodyName = rec.TerminalOrbitBody;
                    source = "terminal-orbit-body-environment";
                    return true;
                }

                if (TryGetTerminalSurfaceBodyName(rec, out bodyName))
                {
                    source = "terminal-position-body-environment";
                    return true;
                }
            }

            if (TryGetLastBodyFixedPrimaryPoint(section, out TrajectoryPoint bodyFixedPrimaryPoint)
                && !string.IsNullOrEmpty(bodyFixedPrimaryPoint.bodyName))
            {
                bodyName = bodyFixedPrimaryPoint.bodyName;
                source = "relative-absolute-shadow-environment";
                return true;
            }

            return false;
        }

        private static bool TryResolveAbsoluteEndpointPointSegmentPhase(
            Recording rec,
            out string phase,
            out string bodyName,
            out string source,
            out string skipReason)
        {
            phase = null;
            bodyName = null;
            source = null;
            skipReason = null;

            if (!TryGetFinalAbsoluteEndpointPoint(
                rec,
                out TrajectoryPoint point,
                out source,
                out skipReason))
            {
                return false;
            }

            bodyName = point.bodyName;
            if (string.IsNullOrEmpty(bodyName))
            {
                skipReason = "absolute-endpoint-point-has-no-body";
                return false;
            }

            CelestialBody body = null;
            try
            {
                body = FlightGlobals.GetBodyByName(bodyName);
            }
            catch (Exception ex)
            {
                skipReason = "body-lookup-failed-" + ex.GetType().Name;
                return false;
            }

            if (body == null)
            {
                skipReason = "body-lookup-returned-null";
                return false;
            }

            double approachAltitude = FlightRecorder.ComputeApproachAltitude(body);
            phase = SegmentPhaseClassifier.ClassifyFromValues(
                Vessel.Situations.FLYING,
                body.atmosphere,
                point.altitude,
                body.atmosphereDepth,
                approachAltitude);
            return true;
        }

        internal static bool TryGetFinalTrackSection(Recording rec, out TrackSection section)
        {
            section = default(TrackSection);
            if (rec?.TrackSections == null || rec.TrackSections.Count == 0)
                return false;

            int sectionIndex = -1;
            if (!double.IsNaN(rec.ExplicitEndUT))
                sectionIndex = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, rec.ExplicitEndUT);
            if (sectionIndex < 0 && rec.Points != null && rec.Points.Count > 0)
                sectionIndex = TrajectoryMath.FindTrackSectionForUT(
                    rec.TrackSections,
                    rec.Points[rec.Points.Count - 1].ut);
            if (sectionIndex < 0)
                sectionIndex = rec.TrackSections.Count - 1;

            section = rec.TrackSections[sectionIndex];
            return true;
        }

        internal static bool TryGetLastBodyFixedPrimaryPoint(
            TrackSection section,
            out TrajectoryPoint point)
        {
            point = default(TrajectoryPoint);
            if (section.bodyFixedFrames == null || section.bodyFixedFrames.Count == 0)
                return false;

            point = section.bodyFixedFrames[section.bodyFixedFrames.Count - 1];
            return true;
        }

        internal static bool TryGetFinalAbsoluteEndpointPoint(
            Recording rec,
            out TrajectoryPoint point,
            out string source,
            out string skipReason)
        {
            point = default(TrajectoryPoint);
            source = null;
            skipReason = null;

            if (rec == null)
            {
                skipReason = "recording-null";
                return false;
            }

            if (rec.TrackSections == null || rec.TrackSections.Count == 0)
            {
                if (rec.Points != null && rec.Points.Count > 0)
                {
                    point = rec.Points[rec.Points.Count - 1];
                    source = "legacy-flat-point";
                    return true;
                }

                skipReason = "no-track-sections-or-points";
                return false;
            }

            if (!TryGetFinalTrackSection(rec, out TrackSection section))
            {
                skipReason = "no-final-track-section";
                return false;
            }

            if (section.referenceFrame == ReferenceFrame.Relative)
            {
                if (TryGetLastBodyFixedPrimaryPoint(section, out point))
                {
                    source = "relative-absolute-shadow-point";
                    return true;
                }

                skipReason = "relative-endpoint-unresolved-no-absolute-shadow";
                return false;
            }

            if (section.referenceFrame != ReferenceFrame.Absolute)
            {
                skipReason = "unsupported-reference-frame-" + section.referenceFrame;
                return false;
            }

            if (section.frames != null && section.frames.Count > 0)
            {
                point = section.frames[section.frames.Count - 1];
                source = "absolute-section-point";
                return true;
            }

            if (rec.Points != null && rec.Points.Count > 0)
            {
                point = rec.Points[rec.Points.Count - 1];
                source = "absolute-flat-point";
                return true;
            }

            skipReason = "absolute-section-has-no-points";
            return false;
        }

        internal static string GetBestEndpointBodyName(
            Recording rec,
            bool includeFlatPointBody)
        {
            if (rec == null)
                return null;
            if (!string.IsNullOrEmpty(rec.EndpointBodyName))
                return rec.EndpointBodyName;
            if (!string.IsNullOrEmpty(rec.TerminalOrbitBody))
                return rec.TerminalOrbitBody;
            if (rec.TerminalPosition.HasValue
                && !string.IsNullOrEmpty(rec.TerminalPosition.Value.body))
                return rec.TerminalPosition.Value.body;
            if (rec.SurfacePos.HasValue
                && !string.IsNullOrEmpty(rec.SurfacePos.Value.body))
                return rec.SurfacePos.Value.body;
            if (!string.IsNullOrEmpty(rec.SegmentBodyName))
                return rec.SegmentBodyName;
            if (!string.IsNullOrEmpty(rec.StartBodyName))
                return rec.StartBodyName;
            if (includeFlatPointBody && rec.Points != null && rec.Points.Count > 0)
                return rec.Points[rec.Points.Count - 1].bodyName;
            return null;
        }

        internal static bool TryResolveTerminalInferenceBody(
            string bodyName,
            out double bodyRadius,
            out double periapsisFloor)
        {
            bodyRadius = 0.0;
            periapsisFloor = 0.0;
            if (string.IsNullOrEmpty(bodyName))
                return false;

            Func<string, double?> resolver = ParsekFlight.TerminalInferenceBodyRadiusResolverForTesting;
            if (resolver != null)
            {
                double? resolved = resolver(bodyName);
                if (resolved.HasValue && resolved.Value > 0.0)
                {
                    bodyRadius = resolved.Value;
                    Func<string, double?> floorResolver = ParsekFlight.TerminalInferencePeriapsisFloorResolverForTesting;
                    double? floor = floorResolver != null ? floorResolver(bodyName) : null;
                    periapsisFloor = floor.HasValue ? floor.Value : 0.0;
                    return true;
                }

                return false;
            }

            return TryResolveTerminalInferenceBodyLive(bodyName, out bodyRadius, out periapsisFloor);
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool TryResolveTerminalInferenceBodyLive(
            string bodyName,
            out double bodyRadius,
            out double periapsisFloor)
        {
            bodyRadius = 0.0;
            periapsisFloor = 0.0;
            try
            {
                var body = FlightGlobals.GetBodyByName(bodyName);
                if (body != null && body.Radius > 0.0)
                {
                    bodyRadius = body.Radius;
                    periapsisFloor = OrbitClearance.ResolvePeriapsisFloorAltitude(body);
                    return true;
                }
            }
            catch
            {
                // FlightGlobals is unavailable in headless tests.
            }

            return false;
        }

        /// <summary>
        /// Decides whether
        /// <see cref="FinalizeIndividualRecording"/> /
        /// <see cref="EnsureActiveRecordingTerminalState"/> should skip the
        /// scene-exit "vessel was alive when unloaded → infer Landed/Splashed
        /// from last trajectory point" branch.
        ///
        /// <para>
        /// PR #572 follow-up — the gate fires when the recording was just
        /// repaired from the committed tree this frame
        /// (<see cref="Recording.RestoredFromCommittedTreeThisFrame"/>).
        /// The trajectory is a copy of a recording that was committed
        /// mid-flight without a terminal state — typically because the
        /// missing live pid is a deliberate Re-Fly strip casualty, not a
        /// natural unload — so the surface inference's "vessel was alive
        /// when unloaded" assumption does not apply.
        /// </para>
        ///
        /// <para>
        /// Bug 2 follow-up (2026-04-30 STASH probe regression) — the gate
        /// also fires when the recording's trajectory has fewer than 2
        /// points. After an optimizer split nulls the original half's
        /// terminal state and a subsequent quickload-resume tree trim
        /// removes everything past the cutoff, the surviving payload is a
        /// single ascending start-point. <see cref="InferTerminalStateFromTrajectory"/>
        /// then defaults to <see cref="TerminalState.SubOrbital"/>, silently
        /// overwriting what was Splashed on a sibling chain segment that no
        /// longer exists. Skipping the inference leaves the terminal unset
        /// so <see cref="EffectiveState.ResolveChainTerminalRecording"/> /
        /// <see cref="UnfinishedFlightClassifier"/> see "no terminal" and
        /// the recording falls out of UF/STASH naturally.
        /// </para>
        ///
        /// <para>
        /// An additional "orbital evidence" clause (high
        /// <see cref="Recording.MaxDistanceFromLaunch"/> and/or a stable
        /// orbit segment) was considered as Option D in the design plan
        /// (<c>docs/dev/plans/refly-finalize-stripped-vessel-landed-fix.md</c>)
        /// and rejected: the legitimate "orbit-then-land" case shares the
        /// same shape (high MaxDistanceFromLaunch + stable orbit segment
        /// alongside a low-altitude last point), so any threshold that
        /// captures the user's strip-casualty case also breaks the existing
        /// pinned tests
        /// <c>EnsureActiveRecordingTerminalState_NoLiveVesselOnSceneExit_InfersFromTrajectory</c>
        /// and <c>SceneExitInferredActiveNonLeaf_DefaultsToPersistInMergeDialog</c>.
        /// Future readers: do not add an orbital-evidence clause here
        /// without first revisiting those two tests.
        /// </para>
        ///
        /// Returns true with a human-readable <paramref name="reason"/>
        /// when the inference must be skipped; false (with reason=null)
        /// when the normal inference path should proceed.
        /// </summary>
        internal static bool ShouldSkipSceneExitSurfaceInferenceForRestoredRecording(
            Recording rec,
            out string reason)
        {
            reason = null;
            if (rec == null)
                return false;

            if (rec.RestoredFromCommittedTreeThisFrame)
            {
                reason = "recording was repaired from committed tree this frame " +
                    "(PR #572 follow-up: trajectory came from a non-authoritative committed copy)";
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true when scene-exit terminal finalization must preserve the
        /// committed-tree repair instead of applying cache/finalizer/inference
        /// evidence. Requires a missing live vessel so normal live-vessel
        /// finalization remains authoritative.
        /// </summary>
        internal static bool ShouldPreserveRestoredSceneExitTerminalState(
            Recording rec,
            bool isSceneExit,
            bool vesselMissing,
            out string reason)
        {
            reason = null;
            if (!isSceneExit || !vesselMissing)
                return false;

            return ShouldSkipSceneExitSurfaceInferenceForRestoredRecording(rec, out reason);
        }

        internal static void TryCaptureTerrainHeightFromLastTrajectoryPoint(Recording rec)
        {
            if (rec?.Points == null || rec.Points.Count == 0)
                return;

            var lastPt = rec.Points[rec.Points.Count - 1];
            try
            {
                CelestialBody body = FlightGlobals.GetBodyByName(lastPt.bodyName);
                if (body != null)
                {
                    rec.TerrainHeightAtEnd = body.TerrainAltitude(lastPt.latitude, lastPt.longitude);
                    ParsekLog.Verbose("TerrainCorrect",
                        $"Captured terrain height from trajectory for unloaded vessel: " +
                        $"{rec.TerrainHeightAtEnd:F1}m (lastPt alt={lastPt.altitude:F1}m, " +
                        $"rec={rec.RecordingId})");
                }
            }
            catch
            {
                // FlightGlobals not available (unit tests)
            }
        }

        internal static bool UsesTerminalOrbitMetadata(TerminalState state)
        {
            return state == TerminalState.Orbiting
                || state == TerminalState.SubOrbital
                || state == TerminalState.Docked;
        }

        internal static bool DidSceneExitUpdateTerminalOrbitMetadata(
            ParsekFlight.TerminalOrbitMetadataSnapshot before,
            Recording rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.TerminalOrbitBody))
                return false;

            return !before.HasMetadata
                || !string.Equals(before.body, rec.TerminalOrbitBody, StringComparison.Ordinal)
                || before.inclination != rec.TerminalOrbitInclination
                || before.eccentricity != rec.TerminalOrbitEccentricity
                || before.semiMajorAxis != rec.TerminalOrbitSemiMajorAxis
                || before.lan != rec.TerminalOrbitLAN
                || before.argumentOfPeriapsis != rec.TerminalOrbitArgumentOfPeriapsis
                || before.meanAnomalyAtEpoch != rec.TerminalOrbitMeanAnomalyAtEpoch
                || before.epoch != rec.TerminalOrbitEpoch;
        }
    }
}
