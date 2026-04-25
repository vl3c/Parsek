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
        public List<LedgerTombstone> LedgerTombstones = new List<LedgerTombstone>();

        /// <summary>Singleton; non-null only during an active re-fly session.</summary>
        public ReFlySessionMarker ActiveReFlySessionMarker;

        /// <summary>Singleton; non-null only during a staged-commit merge.</summary>
        public MergeJournal ActiveMergeJournal;

        // Phase 2 (Rewind-to-Staging): state-version counters consumed by
        // <see cref="EffectiveState"/> to invalidate ERS/ELS caches. Production
        // code bumps these whenever <see cref="RecordingSupersedes"/> or
        // <see cref="LedgerTombstones"/> mutate; the OnLoad path bumps on load
        // (every load invalidates caches). Public field so test helpers can
        // read the counter value for cache-invalidation assertions.
        public int SupersedeStateVersion;
        public int TombstoneStateVersion;

        /// <summary>
        /// Bumps <see cref="SupersedeStateVersion"/>. Called by any code path
        /// that adds / removes / clears entries in <see cref="RecordingSupersedes"/>
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
        // <see cref="LedgerTombstones"/>, and <see cref="ActiveReFlySessionMarker"/>
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
            LedgerOrchestrator.RecalculateAndPatch();
        }

        internal static bool ShouldUseCurrentUtCutoffForPostRewindFlightLoad(
            bool isRevert,
            bool loadedSceneIsFlight,
            bool planetariumReady,
            bool hasPendingTree,
            ActiveTreeRestoreMode restoreMode,
            bool hasLiveRecorder,
            bool hasActiveUncommittedTree,
            bool hasFutureLedgerActions)
        {
            return !isRevert
                && loadedSceneIsFlight
                && planetariumReady
                && !hasPendingTree
                && restoreMode == ActiveTreeRestoreMode.None
                && !hasLiveRecorder
                && !hasActiveUncommittedTree
                && hasFutureLedgerActions;
        }

        /// <summary>
        /// Scene-load follow-up recalculation for the specific post-rewind FLIGHT
        /// case where the loaded UT is still behind future committed actions.
        /// Filters the walk to the already-captured loaded UT while preserving the
        /// normal patch-deferral and same-branch repeatable-record behavior.
        /// </summary>
        internal static void RecalculateAndPatchForPostRewindFlightLoad(double loadedUT)
        {
            LedgerOrchestrator.RecalculateAndPatchForPostRewindFlightLoad(loadedUT);
        }

        /// <summary>
        /// #434 follow-up: dispatch-level guard that decides whether the OnLoad
        /// quickload-discard branch should fire. Pure function of the three
        /// classification bits so it can be unit-tested in isolation — the
        /// <see cref="OnLoad"/> call site it gates is itself not reachable from
        /// xUnit (ScenarioModule lifecycle).
        ///
        /// The truth table that matters:
        /// <list type="bullet">
        ///   <item>Pure F5/F9 quickload (<c>isRevert=false</c>, UT back, flight-to-flight): <b>true</b> — hard-discard the stashed-this-transition tree.</item>
        ///   <item>Revert to Launch (<c>isRevert=true</c>, UT back, flight-to-flight): <b>false</b> — the revert branch owns pending-tree handling via <see cref="RecordingStore.UnstashPendingTreeOnRevert"/>, which preserves sidecar files for F9-from-flight-quicksave.</item>
        ///   <item>Scene reload with same UT: <b>false</b> — nothing to discard.</item>
        /// </list>
        /// </summary>
        internal static bool ShouldRunQuickloadDiscard(
            bool utWentBackwards, bool isFlightToFlight, bool isRevert)
        {
            return utWentBackwards && isFlightToFlight && !isRevert;
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
        /// <see cref="ShouldRunQuickloadDiscard"/>; the defense-in-depth check
        /// on the first line below refuses to proceed if a revert just fired.
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
            try
            {
                ParsekLog.RecState("OnSave:pre", CaptureScenarioRecorderState());
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

                SaveTreeRecordings(node);
                PersistGameStateAndMilestones(node);
                // Rewind-to-Staging Phase 1 (design sections 5.1-5.9). Persistence
                // only in Phase 1; no behavior wired to these collections yet.
                SaveRewindStagingState(node);

                // Strip ghost map ProtoVessels — they are transient and reconstructed on load
                if (GhostMapPresence.ghostMapVesselPids.Count > 0)
                {
                    var flightState = HighLogic.CurrentGame?.flightState;
                    if (flightState != null)
                        GhostMapPresence.StripFromSave(flightState);
                }

                lastOnSaveScene = HighLogic.LoadedScene;
                ParsekLog.RecState("OnSave:post", CaptureScenarioRecorderState());
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
        private static void SaveTreeRecordings(ConfigNode node)
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

                // Write bulk data to external files only for recordings whose data changed
                foreach (var rec in tree.Recordings.Values)
                {
                    if (rec.FilesDirty && !RecordingStore.SaveRecordingFiles(rec))
                        ScenarioLog($"[Parsek Scenario] WARNING: File write failed for tree recording '{rec.VesselName}'");
                    treeRecCount++;
                    treeTotalPoints += rec.Points.Count;
                    treeTotalOrbitSegments += rec.OrbitSegments.Count;
                    treeTotalPartEvents += rec.PartEvents.Count;
                    if (rec.TrackSections != null && rec.TrackSections.Count > 0) treeWithTrackSections++;
                    if (rec.VesselSnapshot != null) treeWithSnapshots++;
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
            foreach (var rec in activeTree.Recordings.Values)
            {
                bool wasDirty = rec.FilesDirty;
                if (wasDirty)
                {
                    activeDirtyCount++;
                    if (RecordingStore.SaveRecordingFiles(rec))
                    {
                        activeSavedCount++;
                    }
                    else
                    {
                        activeSaveFailedCount++;
                        ScenarioLog($"[Parsek Scenario] WARNING: File write failed for active tree recording '{rec.VesselName}'");
                    }
                }
                activeRecCount++;
            }

            ParsekLog.Info("Scenario",
                $"SaveActiveTreeIfAny: iterated {activeRecCount} recording(s), " +
                $"{activeDirtyCount} dirty, {activeSavedCount} saved, {activeSaveFailedCount} failed");

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
            // research in R&D without launching a flight).
            ParsekLog.Verbose("Scenario", $"OnSave: flushing pending events at UT {Planetarium.GetUniversalTime():F0}");
            MilestoneStore.FlushPendingEvents(Planetarium.GetUniversalTime());

            // Save milestones to external file + mutable state to .sfs
            MilestoneStore.SaveMilestoneFile();
            MilestoneStore.SaveMutableState(node);

            // Save ledger to external file
            LedgerOrchestrator.OnSave();
            ParsekLog.Verbose("Scenario",
                $"OnSave: wrote external game-state files (events={GameStateStore.EventCount}, milestones={MilestoneStore.MilestoneCount})");
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
            node.RemoveNodes("LEDGER_TOMBSTONES");
            node.RemoveNodes(ReFlySessionMarker.NodeName);
            node.RemoveNodes(MergeJournal.NodeName);

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

            // Per-section tagged lines (design §10 tag conventions). Emitted
            // alongside the consolidated summary below so log-grep by tag still
            // works even when the summary line changes shape.
            ParsekLog.Info("Rewind", $"RewindPoints saved: {rpCount}");
            ParsekLog.Info("Supersede", $"RecordingSupersedes saved: {supersedeCount}");
            ParsekLog.Info("LedgerSwap", $"LedgerTombstones saved: {tombCount}");
            ParsekLog.Info("ReFlySession",
                $"Marker saved: {(markerWritten ? (markerSessionId ?? "<no-id>") : "none")}");
            ParsekLog.Info("MergeJournal",
                $"Journal saved: {(journalWritten ? (journalId ?? "<no-id>") : "none")}");

            ParsekLog.Info("Scenario",
                $"OnSave: rewind-staging persist: rewindPoints={rpCount} supersedes={supersedeCount} " +
                $"tombstones={tombCount} marker={markerWritten} journal={journalWritten}");
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
            LedgerTombstones = new List<LedgerTombstone>();
            ActiveReFlySessionMarker = null;
            ActiveMergeJournal = null;

            ConfigNode rpParent = node.GetNode("REWIND_POINTS");
            if (rpParent != null)
            {
                var entries = rpParent.GetNodes("POINT");
                for (int i = 0; i < entries.Length; i++)
                    RewindPoints.Add(RewindPoint.LoadFrom(entries[i]));
            }

            ConfigNode sParent = node.GetNode("RECORDING_SUPERSEDES");
            if (sParent != null)
            {
                var entries = sParent.GetNodes("ENTRY");
                for (int i = 0; i < entries.Length; i++)
                    RecordingSupersedes.Add(RecordingSupersedeRelation.LoadFrom(entries[i]));
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

            ConfigNode journalNode = node.GetNode(MergeJournal.NodeName);
            if (journalNode != null)
                ActiveMergeJournal = MergeJournal.LoadFrom(journalNode);

            // Per-section tagged lines (design §10 tag conventions). Emitted
            // alongside the consolidated summary below so log-grep by tag still
            // works even when the summary line changes shape.
            ParsekLog.Info("Rewind", $"RewindPoints loaded: {RewindPoints.Count}");
            ParsekLog.Info("Supersede",
                $"RecordingSupersedes loaded: {RecordingSupersedes.Count}");
            ParsekLog.Info("LedgerSwap",
                $"LedgerTombstones loaded: {LedgerTombstones.Count}");
            ParsekLog.Info("ReFlySession",
                $"Marker loaded: {(ActiveReFlySessionMarker != null ? (ActiveReFlySessionMarker.SessionId ?? "<no-id>") : "none")}");
            ParsekLog.Info("MergeJournal",
                $"Journal loaded: {(ActiveMergeJournal != null ? (ActiveMergeJournal.JournalId ?? "<no-id>") : "none")}");

            ParsekLog.Info("Scenario",
                $"OnLoad: rewind-staging load: rewindPoints={RewindPoints.Count} " +
                $"supersedes={RecordingSupersedes.Count} tombstones={LedgerTombstones.Count} " +
                $"marker={(ActiveReFlySessionMarker != null)} journal={(ActiveMergeJournal != null)}");

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

        public override void OnLoad(ConfigNode node)
        {
            DiagnosticsState.ResetSessionCounters();
            IncompleteBallisticSceneExitFinalizer.ResetLifecycleDiagnostics();
            var sw = Stopwatch.StartNew();
            int loadedRecordingCount = 0;
            try
            {
                // Reset deferred dialog flag and clear input lock (dialog may have been
                // destroyed by scene change without the user clicking a button)
                mergeDialogPending = false;
                InputLockManager.RemoveControlLock("ParsekMergeDialog");

                // Restore user-intent settings (tracking-station visibility, readable
                // sidecar mirrors, etc.) from the
                // external settings.cfg file, overwriting whatever stale value KSP's
                // GameParameters restored from the save. Runs before anything reads
                // ParsekSettings.Current so the rest of OnLoad sees the fresh values.
                // Survives rewind (parsek_rw_* quicksaves), save/load, and KSP restart.
                ParsekSettingsPersistence.ApplyTo(ParsekSettings.Current);
                ParsekLog.RecState("OnLoad:settings-applied", CaptureScenarioRecorderState());

                var recordings = RecordingStore.CommittedRecordings;
                loadedRecordingCount = recordings.Count;

                RegisterMainMenuHook();
                DetectSaveFolderChange();
                if (!RewindContext.IsRewinding)
                    KerbalLoadRepairDiagnostics.Begin();
                LoadCrewAndGroupState(node);
                // Rewind-to-Staging Phase 1 (design sections 5.1-5.9). Load runs
                // on every OnLoad so a revert or scene change rebuilds the lists
                // from .sfs rather than reusing stale in-memory state.
                LoadRewindStagingState(node);

                // Game state recorder lifecycle — re-subscribe on every OnLoad (handles reverts)
                stateRecorder?.Unsubscribe();
                if (!initialLoadDone)
                    LoadExternalFiles();
                stateRecorder = new GameStateRecorder();
                stateRecorder.SeedFacilityCacheFromCurrentState();
                stateRecorder.Subscribe();

                SubscribeVesselLifecycleEvents();
                CaptureInitialBaseline();

                if (initialLoadDone)
                {
                    // Go-back detection: must be BEFORE revert detection and BEFORE any
                    // .sfs data loading. In-memory state is the source of truth.
                    if (RewindContext.IsRewinding)
                    {
                        HandleRewindOnLoad(node, recordings);
                        ReconcileReadableSidecarMirrorsOnLoadIfDisabled();
                        WriteLoadTiming(sw, recordings.Count);
                        DiagnosticsComputation.EmitSceneLoadSnapshot(recordings.Count, HighLogic.LoadedScene.ToString());
                        return;
                    }

                    // Restore an in-flight tree from the save file (if OnSave wrote one).
                    // This stashes it into the pending-Limbo slot so the revert-detection
                    // dispatch below can decide between "restore and resume" (quickload)
                    // and "finalize and commit" (real revert).
                    // Runs AFTER ParsekSettingsPersistence.ApplyTo so restored settings
                    // affect the rest of OnLoad, but BEFORE revert detection so the
                    // pending slot is populated when it runs.
                    bool activeTreeRestoredFromSave = TryRestoreActiveTreeNode(node);
                    ParsekLog.RecState("OnLoad:active-tree-restored", CaptureScenarioRecorderState());

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
                        $"activeTreeRestoredFromSave={activeTreeRestoredFromSave}, hasOrphanedLimboTree={hasOrphanedLimboTree}");
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
                    for (int i = 0; i < recordings.Count; i++)
                    {
                        if (!recordings[i].IsTreeRecording) continue;

                        RecordingStore.RollbackContinuationData(recordings[i]);
                        ClearPostSpawnTerminalState(recordings[i], "tree recording");

                        recordings[i].VesselSpawned = false;
                        recordings[i].SpawnAttempts = 0;
                        recordings[i].SpawnDeathCount = 0;
                        recordings[i].SpawnedVesselPersistentId = 0;

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
                    // Always auto-commit on main menu (game is being unloaded, dialog would be meaningless).
                    bool forceAutoMerge = HighLogic.LoadedScene == GameScenes.MAINMENU;
                    if (!isRevert && HighLogic.LoadedScene != GameScenes.FLIGHT)
                    {
                        if (IsAutoMerge || forceAutoMerge)
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
                            bool showApproval = !forceAutoMerge && destScene.HasValue &&
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

                    bool loadedSceneIsFlight = HighLogic.LoadedScene == GameScenes.FLIGHT;
                    bool hasPendingTree = RecordingStore.HasPendingTree;
                    ActiveTreeRestoreMode restoreMode = ScheduleActiveTreeRestoreOnFlightReady;
                    bool hasLiveRecorder = GameStateRecorder.HasLiveRecorder();
                    bool hasActiveUncommittedTree = GameStateRecorder.HasActiveUncommittedTree();
                    bool hasFutureLedgerActions = LedgerOrchestrator.HasActionsAfterUT(loadedUT);
                    bool useCurrentUtCutoffForPostRewindFlightLoad =
                        ShouldUseCurrentUtCutoffForPostRewindFlightLoad(
                            isRevert,
                            loadedSceneIsFlight,
                            planetariumReady,
                            hasPendingTree,
                            restoreMode,
                            hasLiveRecorder,
                            hasActiveUncommittedTree,
                            hasFutureLedgerActions);
                    ParsekLog.Info("Scenario",
                        $"OnLoad: post-rewind FLIGHT cutoff decision useCurrentUtCutoff={useCurrentUtCutoffForPostRewindFlightLoad} " +
                        $"loadedUT={loadedUT.ToString("R", CultureInfo.InvariantCulture)} " +
                        $"isRevert={isRevert} loadedSceneIsFlight={loadedSceneIsFlight} " +
                        $"planetariumReady={planetariumReady} hasPendingTree={hasPendingTree} " +
                        $"restoreMode={restoreMode} hasLiveRecorder={hasLiveRecorder} " +
                        $"hasActiveUncommittedTree={hasActiveUncommittedTree} " +
                        $"hasFutureLedgerActions={hasFutureLedgerActions}");
                    if (useCurrentUtCutoffForPostRewindFlightLoad)
                    {
                        ParsekLog.Info("Scenario",
                            $"OnLoad: post-rewind FLIGHT recalc using current-UT cutoff {loadedUT.ToString("R", CultureInfo.InvariantCulture)} " +
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
                    ReconcileReadableSidecarMirrorsOnLoadIfDisabled();
                    WriteLoadTiming(sw, recordings.Count);
                    DiagnosticsComputation.EmitSceneLoadSnapshot(recordings.Count, HighLogic.LoadedScene.ToString());

                    // Phase 6 of Rewind-to-Staging: re-fly invocation drains
                    // the static context in the new scenario. Lives on the
                    // FLIGHT→FLIGHT branch because the RP quicksave always
                    // lands in FLIGHT and the invoker issues LoadScene(FLIGHT)
                    // from FLIGHT/SPACECENTER/TRACKSTATION.
                    DispatchRewindPostLoadIfPending();

                    // Phase 10 of Rewind-to-Staging (design §6.9 step 2):
                    // resume any interrupted staged-commit merge on the
                    // FLIGHT→FLIGHT branch too (quickload mid-merge, scene
                    // preservation, etc.).
                    if (ActiveMergeJournal != null)
                    {
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
                    LoadTimeSweep.Run();

                    // Phase 11 housekeeping pass — same rationale as the
                    // cold-start branch below; runs after the finisher so
                    // live-session RPs stay put.
                    RewindPointReaper.ReapOrphanedRPs();
                    return;
                }

                initialLoadDone = true;

                DiscardStalePendingState();

                RecordingStore.ClearCommittedInternal();

                // Validate chain integrity before any playback
                RecordingStore.ValidateChains();

                LoadRecordingTrees(node, recordings);

                // Cold-start active-tree restore (quickload-resume cold path).
                // When the player quits KSP mid-flight and later relaunches + "Resume Saved
                // Game", OnLoad runs with initialLoadDone=false and falls through to this
                // block. TryRestoreActiveTreeNode here picks up any isActive=True tree
                // from the save and stashes it into pending-Limbo so OnFlightReady's
                // restore coroutine can resume recording — same path used by in-session
                // quickload. Without this, cold-start resume silently drops the active
                // tree and the player's in-progress mission fragments just like the
                // original Bug C scenario.
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

                // Clean orphaned sidecar files (recordings deleted in previous sessions)
                RecordingStore.CleanOrphanFiles();

                // Restore milestone mutable state (LastReplayedEventIndex) from .sfs
                MilestoneStore.RestoreMutableState(node);

                // Reconcile ledger against loaded recordings (prunes orphaned actions, recalculates)
                var validIds = new System.Collections.Generic.HashSet<string>();
                for (int v = 0; v < recordings.Count; v++)
                {
                    if (!string.IsNullOrEmpty(recordings[v].RecordingId))
                        validIds.Add(recordings[v].RecordingId);
                }
                double reconcileUT = Planetarium.GetUniversalTime();
                LedgerOrchestrator.OnKspLoad(validIds, reconcileUT);

                // Schedule deferred seeding: during OnLoad, Funding/R&D/Reputation singletons
                // may exist but have not loaded their save data yet (KSP loads scenarios in
                // parallel). The deferred coroutine waits for singletons to be ready, then
                // seeds initial balances and recalculates so the ledger has correct values.
                StartCoroutine(DeferredSeedAndRecalculate());

                // Diagnostic summary of loaded recordings with UT context
                double loadUT = Planetarium.GetUniversalTime();
                ParsekLog.Info("Scenario", $"Scenario load summary — UT: {loadUT:F0}, {recordings.Count} recording(s)");
                for (int i = 0; i < recordings.Count; i++)
                {
                    var loadedRec = recordings[i];
                    double duration = loadedRec.EndUT - loadedRec.StartUT;
                    string status;
                    if (loadUT < loadedRec.StartUT)
                        status = $"future (starts in {loadedRec.StartUT - loadUT:F0}s)";
                    else if (loadUT <= loadedRec.EndUT && duration > 0)
                        status = $"IN PROGRESS ({(loadUT - loadedRec.StartUT) / duration * 100:F0}%)";
                    else if (loadUT <= loadedRec.EndUT)
                        status = "IN PROGRESS";
                    else
                        status = "past";
                    ParsekLog.Verbose("Scenario", $"  #{i}: \"{loadedRec.VesselName}\" — {status}");
                }

                if (CrewReservationManager.CrewReplacements.Count > 0)
                {
                    ParsekLog.Info("Scenario", $"Crew reservations active ({CrewReservationManager.CrewReplacements.Count}):");
                    foreach (var kvp in CrewReservationManager.CrewReplacements)
                        ParsekLog.Info("Scenario", $"  {kvp.Key} -> replacement: {kvp.Value}");
                }

                // Run recording optimization pass (merge redundant segments, split monolithic ones)
                RecordingStore.RunOptimizationPass();

                // Auto-unreserve crew for recordings whose EndUT has already passed
                // but vessel was never spawned. Skip at SpaceCenter — ParsekKSC now
                // handles spawning there (bug #99), so nulling the snapshot here would
                // pre-empt the KSC spawn.
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

                ReconcileReadableSidecarMirrorsOnLoadIfDisabled();
                WriteLoadTiming(sw, recordings.Count);

                // Scene load memory snapshot (once per load, after all recordings are loaded)
                DiagnosticsComputation.EmitSceneLoadSnapshot(recordings.Count, HighLogic.LoadedScene.ToString());

                // Phase 6 of Rewind-to-Staging (design §6.3 step 4 / §6.4):
                // if a re-fly invocation is pending, the preceding LoadGame was
                // triggered by RewindInvoker.StartInvoke. Drain the static
                // RewindInvokeContext now — Restore → Strip → Activate →
                // AtomicMarkerWrite — in the new scenario, synchronously, NO
                // coroutine (the old scenario's coroutine was torn down with
                // the scene).
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
                    MergeJournalOrchestrator.RunFinisher();
                }

                // Phase 13 of Rewind-to-Staging (design §6.9 full load-time
                // sweep): marker validation + zombie provisional cleanup +
                // orphan supersede/tombstone log + stray-field clearing.
                // Must run after the finisher (which may have cleared the
                // marker) and before the Phase 11 reaper (whose input is the
                // sweep's zombie-removed state).
                LoadTimeSweep.Run();

                // Phase 11 of Rewind-to-Staging (design §6.8 load-time sweep):
                // housekeeping pass for RPs orphaned by merges that crashed
                // between TagRpsForReap and the reaper, or whose slots went
                // Immutable later via a non-rewind code path. Runs after the
                // finisher so we never try to reap an RP whose session is
                // still live.
                RewindPointReaper.ReapOrphanedRPs();
            }
            finally
            {
                KerbalLoadRepairDiagnostics.Reset();
                // Always capture timing, even on exception (matches OnSave pattern)
                WriteLoadTiming(sw, loadedRecordingCount);
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
            int suppressedCount = MarkRewoundTreeRecordingsAsGhostOnly(recordings);
            if (suppressedCount > 0)
                ParsekLog.Info("Rewind",
                    $"OnLoad: SpawnSuppressedByRewind=true on {suppressedCount} active/source recording(s) — " +
                    $"same-tree future recordings remain spawn-eligible (#573/#589)");

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

            // Re-reserve crew from all recording snapshots.
            // Pass the adjusted rewind UT explicitly so the walk drops any ledger
            // actions with UT > adjusted (e.g., milestones achieved after the rewind
            // target). RewindContext.RewindAdjustedUT is still populated at this point;
            // EndRewind() below clears it. The deferred coroutine captures the same
            // value into a local BEFORE its yield so its second call is independent
            // of RewindContext state.
            LedgerOrchestrator.RecalculateAndPatch(RewindContext.RewindAdjustedUT);

            // Clear rewind flags — rewind loads into SpaceCenter, not Flight
            ParsekLog.Info("Rewind",
                $"OnLoad: rewind complete at UT {RewindContext.RewindUT}. " +
                $"Timeline: {recordings.Count} recordings");
            // Any pending recovery-funds callbacks deferred before the rewind cannot
            // pair after the boundary — stock already fired (or will never fire) the
            // paired FundsChanged(VesselRecovery) event relative to the old timeline.
            LedgerOrchestrator.FlushStalePendingRecoveryFunds("rewind end");
            RewindContext.EndRewind();
            ParsekLog.RecState("HandleRewindOnLoad:exit", CaptureScenarioRecorderState());
        }

        /// <summary>
        /// Writes OnLoad timing to DiagnosticsState and logs a summary.
        /// Called at every exit point of OnLoad to ensure timing is captured
        /// regardless of which code path (rewind, revert, scene change, initial load).
        /// </summary>
        private static void WriteLoadTiming(Stopwatch sw, int recordingCount)
        {
            sw.Stop();
            DiagnosticsState.lastLoadTiming = new SaveLoadTiming
            {
                totalMilliseconds = sw.ElapsedMilliseconds,
                recordingsProcessed = recordingCount,
                dirtyRecordingsWritten = 0
            };
            ParsekLog.Verbose("Diagnostics",
                string.Format(CultureInfo.InvariantCulture,
                    "OnLoad: {0}ms ({1} recordings)",
                    sw.ElapsedMilliseconds, recordingCount));
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
            GameEvents.onVesselRecovered.Remove(OnVesselRecovered);
            GameEvents.onVesselRecovered.Add(OnVesselRecovered);
            GameEvents.onVesselTerminated.Remove(OnVesselTerminated);
            GameEvents.onVesselTerminated.Add(OnVesselTerminated);
            GameEvents.onVesselSwitching.Remove(OnVesselSwitching);
            GameEvents.onVesselSwitching.Add(OnVesselSwitching);
            // #434: subscribe idempotently so revert detection is armed from first OnLoad.
            RevertDetector.Subscribe();
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
            Action unsubscribeLiveRecorder = null)
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

            RecordingStore.ResetForTesting();
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
        private static void LoadRecordingTrees(ConfigNode node, IReadOnlyList<Recording> recordings)
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

                    var tree = RecordingTree.Load(treeNode);
                    int sidecarHydrationFailures = 0;
                    int syntheticFixtureFailures = 0;

                    // Load bulk data from external files for each recording in the tree
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
                    $"{activeTreeCount} active-tree marker(s)): {treeTotalPoints} points, " +
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

        /// <summary>
        /// Bug #422: emits the per-tree sidecar-hydration roll-up. Suppresses the WARN
        /// (downgrades to INFO) when every failure in the batch is the synthetic-fixture
        /// marker — the per-recording INFO lines above already describe those cases and
        /// the WARN would duplicate them, making a clean test save look unhealthy. Any
        /// genuine degradation in the batch still emits the WARN. Internal so unit tests
        /// can exercise both branches without building a full RecordingTree fixture.
        /// </summary>
        internal static void EmitSidecarHydrationRollup(
            string treeName, int totalFailures, int syntheticFixtureFailures)
        {
            if (totalFailures <= 0) return;

            if (syntheticFixtureFailures >= totalFailures)
            {
                ParsekLog.Info("Scenario",
                    $"OnLoad: committed tree '{treeName}' had {totalFailures} " +
                    $"synthetic-fixture recording(s) with missing .prec sidecar " +
                    $"(no genuine degradations; per-recording INFO lines above describe each)");
                return;
            }

            int genuineFailures = totalFailures - syntheticFixtureFailures;
            ParsekLog.Warn("Scenario",
                $"OnLoad: committed tree '{treeName}' had {totalFailures} " +
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
                ParsekLog.Verbose("Scenario",
                    "TryRestoreActiveTreeNode: skipped (rewind in progress — active tree deliberately discarded)");
                return false;
            }

            ConfigNode[] treeNodes = node.GetNodes("RECORDING_TREE");
            for (int t = 0; t < treeNodes.Length; t++)
            {
                if (!IsActiveTreeNode(treeNodes[t])) continue;

                var tree = RecordingTree.Load(treeNodes[t]);
                int sidecarHydrationFailures = 0;
                int staleEpochHydrationFailures = 0;
                // Hydrate bulk data from sidecar files for each recording
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
                        : ""));
                ParsekLog.RecState("TryRestoreActiveTreeNode:stashed", CaptureScenarioRecorderState());
                return true;
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

            pendingQuickloadResumeContext = new QuickloadResumeContext
            {
                TreeId = tree.Id
            };

            ParsekLog.Verbose("Scenario",
                $"Quickload-resume context armed: treeId={tree.Id} activeRecId={tree.ActiveRecordingId}");
        }

        internal static bool MatchesPendingQuickloadResumeContext(string treeId)
        {
            return pendingQuickloadResumeContext != null
                && string.Equals(pendingQuickloadResumeContext.TreeId, treeId, StringComparison.Ordinal);
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
                ParsekFlight.FinalizeIndividualRecording(rec, commitUT, isSceneExit: true);
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

            // Show the tree merge dialog
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

            // Audited for #527: this initial-load-only reseed runs before any rewind
            // context exists, so it intentionally stays full-ledger.
            LedgerOrchestrator.RecalculateAndPatch();
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
            // this coroutine resumes, so we cannot read from RewindContext here —
            // the synchronous HandleRewindOnLoad call already ran and cleared state.
            LedgerOrchestrator.RecalculateAndPatch(adjustedUT);

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
            return marked;
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
        /// Extracted for testability.
        /// </summary>
        internal static void LoadRecordingMetadata(ConfigNode recNode, Recording rec)
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
                    if (rec.RecordingFormatVersion < RecordingStore.LaunchToLaunchLoopIntervalFormatVersion)
                    {
                        double effectiveLoopDuration;
                        double migratedLoopIntervalSeconds =
                            loopIntervalSeconds;
                        if (GhostPlaybackEngine.TryConvertLegacyGapToLoopPeriodSeconds(
                                rec, loopIntervalSeconds,
                                out migratedLoopIntervalSeconds, out effectiveLoopDuration))
                        {
                            int legacyRecordingFormatVersion = rec.RecordingFormatVersion;
                            rec.LoopIntervalSeconds = migratedLoopIntervalSeconds;
                            RecordingStore.NormalizeRecordingFormatVersionAfterLegacyLoopMigration(rec);
                            ParsekLog.Warn("Loop",
                                $"ParsekScenario: migrated recording '{rec.VesselName}' from legacy " +
                                $"gap loopIntervalSeconds={loopIntervalSeconds.ToString("R", CultureInfo.InvariantCulture)} " +
                                $"to launch-to-launch period={migratedLoopIntervalSeconds.ToString("R", CultureInfo.InvariantCulture)}s " +
                                $"using effectiveLoopDuration={effectiveLoopDuration.ToString("R", CultureInfo.InvariantCulture)}s " +
                                $"for recordingFormatVersion={legacyRecordingFormatVersion} (pre-v4 loop save).");
                        }
                        else
                        {
                            rec.LoopIntervalSeconds = loopIntervalSeconds;
                            ParsekLog.Warn("Loop",
                                $"ParsekScenario: loaded recording '{rec.VesselName}' with legacy " +
                                $"loopIntervalSeconds={loopIntervalSeconds.ToString("R", CultureInfo.InvariantCulture)} " +
                                $"for recordingFormatVersion={rec.RecordingFormatVersion}, but deferred migration " +
                                "because loop bounds are not hydrated yet.");
                        }
                    }
                    else
                    {
                        rec.LoopIntervalSeconds = loopIntervalSeconds;
                    }
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

            string vesselName = pv.vesselName;
            if (string.IsNullOrEmpty(vesselName)) return;

            double now = Planetarium.GetUniversalTime();
            bool updated = UpdateRecordingsForTerminalEvent(vesselName, TerminalState.Recovered, now);
            if (updated)
                ParsekLog.Info("Scenario", $"Vessel '{vesselName}' recovered — recording(s) updated with Recovered terminal state");

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
            if (ShouldPatchRecoveryFundsOutsideFlight(HighLogic.LoadedScene, vesselName))
                LedgerOrchestrator.OnVesselRecoveryFunds(now, vesselName, fromTrackingStation, pv.vesselType);
        }

        private void OnVesselTerminated(ProtoVessel pv)
        {
            if (pv == null) return;
            if (GhostMapPresence.IsGhostMapVessel(pv.persistentId)) return;
            if (RewindContext.IsRewinding) return;
            string vesselName = pv.vesselName;
            if (string.IsNullOrEmpty(vesselName)) return;

            double now = Planetarium.GetUniversalTime();
            // onVesselTerminated also fires after onVesselRecovered for the same vessel.
            // The guard in UpdateRecordingsForTerminalEvent prevents overwriting Recovered with Destroyed.
            bool updated = UpdateRecordingsForTerminalEvent(vesselName, TerminalState.Destroyed, now);
            if (updated)
                ParsekLog.Info("Scenario", $"Vessel '{vesselName}' terminated — recording(s) updated with Destroyed terminal state");
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
            bool anyUpdated = false;

            // Check pending tree recordings
            if (RecordingStore.HasPendingTree)
            {
                foreach (var rec in RecordingStore.PendingTree.Recordings.Values)
                {
                    if (MatchesVessel(rec, vesselName) && CanOverwriteTerminalState(rec.TerminalStateValue, state))
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
            return scene != GameScenes.FLIGHT &&
                   !HasPendingLedgerRecordingForVessel(vesselName);
        }

        /// <summary>
        /// Returns true when a pending-tree recording still owns the named vessel and
        /// will later contribute ledger actions on commit.
        /// </summary>
        internal static bool HasPendingLedgerRecordingForVessel(string vesselName)
        {
            if (string.IsNullOrEmpty(vesselName) || !RecordingStore.HasPendingTree)
                return false;

            foreach (var rec in RecordingStore.PendingTree.Recordings.Values)
            {
                if (rec == null || rec.IsGhostOnly)
                    continue;
                if (MatchesVessel(rec, vesselName))
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
            return !string.IsNullOrEmpty(rec.VesselName)
                && string.Equals(rec.VesselName, vesselName, StringComparison.Ordinal);
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
            GameEvents.onVesselRecovered.Remove(OnVesselRecovered);
            GameEvents.onVesselTerminated.Remove(OnVesselTerminated);
            GameEvents.onVesselSwitching.Remove(OnVesselSwitching);
            // #434: RevertDetector subscriptions are idempotent and persist for the
            // lifetime of the game session; tearing them down here so a scenario-module
            // shutdown doesn't leak dangling delegates if the session ends mid-flight.
            RevertDetector.Unsubscribe();
            // Phase 6 of Rewind-to-Staging: clear the per-RP precondition
            // cache so the dict does not grow unbounded across long sessions
            // (Fix 8). The cache is 60s-TTL'd anyway but long-lived scene loops
            // can accumulate entries faster than TTL cleanup.
            RewindInvoker.PreconditionCache.ClearAll();
            // Phase 2 (Rewind-to-Staging): drop the Instance back-reference so
            // EffectiveState does not read stale scenario state after destruction.
            if (ReferenceEquals(s_instance, this))
                s_instance = null;
        }
    }
}
