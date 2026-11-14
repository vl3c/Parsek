using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // The merge flow: scene-exit finalize, tree commit, auto-discard of idle or no-op trees, and
    // terminal inference.
    public partial class ParsekFlight : IMergeFlowHost
    {
        // IMergeFlowHost: explicit, so the interface adds nothing to ParsekFlight's own surface.
        // Each member forwards to the host member of the same name; the last three make the live
        // KSP reads ParsekFlightMergeFlow made itself before it took the interface.
        RecordingTree IMergeFlowHost.activeTree { get => activeTree; set => activeTree = value; }
        FlightRecorder IMergeFlowHost.recorder { get => recorder; set => recorder = value; }
        BackgroundRecorder IMergeFlowHost.backgroundRecorder
        {
            get => backgroundRecorder;
            set => backgroundRecorder = value;
        }
        RecorderStateSnapshot IMergeFlowHost.CaptureRecorderState() => CaptureRecorderState();
        void IMergeFlowHost.FlushRecorderToTreeRecording(FlightRecorder rec, RecordingTree tree) =>
            FlushRecorderToTreeRecording(rec, tree);
        void IMergeFlowHost.FlushRecorderIntoActiveTreeForSerialization() =>
            FlushRecorderIntoActiveTreeForSerialization();
        RecordingFinalizationCache IMergeFlowHost.ResolveFinalizationCacheForRecording(
            RecordingTree tree, Recording recording) =>
            ResolveFinalizationCacheForRecording(tree, recording);
        bool IMergeFlowHost.CanPreTransitionForVesselSwitch() => CanPreTransitionForVesselSwitch();
        void IMergeFlowHost.StashActiveTreeForVesselSwitch(double commitUT) =>
            StashActiveTreeForVesselSwitch(commitUT);
        void IMergeFlowHost.StashActiveTreeAsPendingLimbo(double commitUT) =>
            StashActiveTreeAsPendingLimbo(commitUT);
        void IMergeFlowHost.ClearSceneChangeTransientState() => ClearSceneChangeTransientState();
        int IMergeFlowHost.lastPlaybackIndex { get => lastPlaybackIndex; set => lastPlaybackIndex = value; }
        void IMergeFlowHost.ScreenMessage(string message, float duration) => ScreenMessage(message, duration);
        void IMergeFlowHost.Log(string message) => Log(message);
        void IMergeFlowHost.SpawnTreeLeaves(RecordingTree tree, string activeRecordingId) =>
            SpawnHost.SpawnTreeLeaves(tree, activeRecordingId);
        void IMergeFlowHost.EvaluateAndApplyGhostChains() => SpawnHost.EvaluateAndApplyGhostChains();
        double IMergeFlowHost.CurrentUniversalTime() => Planetarium.GetUniversalTime();
        bool IMergeFlowHost.IsVesselSwitchPendingFresh() => ParsekScenario.IsVesselSwitchPendingFresh();
        uint IMergeFlowHost.ActiveVesselPersistentIdOrZero() =>
            FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.persistentId : 0u;

        /// <summary>
        /// Pure: true when a scene exit out of flight to a non-flight scene belongs to a save-file
        /// load stock announced (<see cref="SaveFileLoadIntent"/>) that is not a revert, so the live
        /// tree is stashed (and later dropped by the cold load's reset) rather than committed.
        /// A FLIGHT destination already stashes; a revert keeps today's scene-exit handling.
        /// </summary>
        internal static bool ShouldStashLiveTreeForSaveFileLoad(
            GameScenes destination, bool saveFileLoadIntentArmed, bool revertPending)
        {
            return destination != GameScenes.FLIGHT && saveFileLoadIntentArmed && !revertPending;
        }

        /// <summary>
        /// Coroutine that checks whether all tree leaves are terminal after the active vessel
        /// is destroyed. If so, finalizes the tree and shows the tree merge dialog.
        /// Waits for any deferred split/coalescer work still in progress so false-alarm
        /// split data or real breakup children are attached to the tree before finalize.
        /// </summary>
        IEnumerator ShowPostDestructionTreeMergeDialog()
        {
            RecorderStateLog.RecState("ShowPostDestructionTreeMergeDialog:entry", CaptureRecorderState());

            // Wait one frame for destruction events to settle
            yield return null;

            // Guard: tree cleaned up during wait (scene change, etc.)
            if (activeTree == null)
            {
                if (sceneChangeInProgress)
                {
                    ParsekLog.Info("Flight",
                        "ShowPostDestructionTreeMergeDialog: activeTree is null during scene change — aborting in-flight merge");
                }
                else
                {
                    ParsekLog.Verbose("Flight",
                        "ShowPostDestructionTreeMergeDialog: activeTree is null — aborting");
                }
                treeDestructionDialogPending = false;
                yield break;
            }

            // Guard: someone else handled it
            if (!treeDestructionDialogPending)
            {
                ParsekLog.Verbose("Flight", "ShowPostDestructionTreeMergeDialog: flag already cleared — aborting");
                yield break;
            }

            // Guard: active vessel still alive
            if (recorder != null && recorder.IsRecording && !recorder.VesselDestroyedDuringRecording)
            {
                ParsekLog.Verbose("Flight", "ShowPostDestructionTreeMergeDialog: active vessel still alive — aborting");
                treeDestructionDialogPending = false;
                yield break;
            }

            bool ignorePendingCrashWait = false;
            float pendingCrashWaitStart = 0f;
            while (true)
            {
                bool activeDestroyed = recorder == null || !recorder.IsRecording
                    || recorder.VesselDestroyedDuringRecording;
                bool allLeavesTerminal = RecordingTree.AreAllLeavesTerminal(
                    activeTree,
                    activeDestroyed);
                bool onlyDebrisBlockersRemain = RecordingTree.AreAllActiveCrashBlockersDebris(
                    activeTree);
                bool pendingCrashResolution = !ignorePendingCrashWait
                    && HasPendingPostDestructionCrashResolution(
                        activeDestroyed,
                        pendingSplitInProgress,
                        crashCoalescer != null && crashCoalescer.HasPendingBreakup);

                switch (ClassifyPostDestructionMergeResolution(
                    activeDestroyed,
                    allLeavesTerminal,
                    onlyDebrisBlockersRemain,
                    pendingCrashResolution))
                {
                    case PostDestructionMergeResolution.WaitForPendingCrashResolution:
                        if (pendingCrashWaitStart <= 0f)
                        {
                            pendingCrashWaitStart = Time.unscaledTime;
                            ParsekLog.Info("Flight",
                                "ShowPostDestructionTreeMergeDialog: waiting for pending split/coalescer resolution before finalizing tree");
                        }
                        else if (Time.unscaledTime - pendingCrashWaitStart > 5f)
                        {
                            ParsekLog.Warn("Flight",
                                "ShowPostDestructionTreeMergeDialog: pending split/coalescer wait timed out (5s real-time) — continuing with current tree state");
                            ignorePendingCrashWait = true;
                            continue;
                        }

                        yield return null;

                        // Re-run the guards after yielding while the deferred split resolves.
                        if (activeTree == null)
                        {
                            if (sceneChangeInProgress)
                            {
                                ParsekLog.Info("Flight",
                                    "ShowPostDestructionTreeMergeDialog: activeTree is null during scene change — aborting in-flight merge");
                            }
                            else
                            {
                                ParsekLog.Verbose("Flight",
                                    "ShowPostDestructionTreeMergeDialog: activeTree is null — aborting");
                            }
                            treeDestructionDialogPending = false;
                            yield break;
                        }

                        if (!treeDestructionDialogPending)
                        {
                            ParsekLog.Verbose("Flight",
                                "ShowPostDestructionTreeMergeDialog: flag cleared while waiting for pending crash resolution — aborting");
                            yield break;
                        }

                        if (recorder != null && recorder.IsRecording && !recorder.VesselDestroyedDuringRecording)
                        {
                            ParsekLog.Verbose("Flight",
                                "ShowPostDestructionTreeMergeDialog: active vessel still alive — aborting");
                            treeDestructionDialogPending = false;
                            yield break;
                        }

                        continue;

                    case PostDestructionMergeResolution.AbortAndKeepRecording:
                        ParsekLog.Info("Flight",
                            "ShowPostDestructionTreeMergeDialog: not all leaves terminal — other vessels still alive");
                        treeDestructionDialogPending = false;
                        yield break;

                    case PostDestructionMergeResolution.FinalizeNow:
                        if (allLeavesTerminal)
                        {
                            ParsekLog.Info("Flight",
                                "ShowPostDestructionTreeMergeDialog: all leaves terminal — finalizing tree");
                        }
                        else
                        {
                            ParsekLog.Info("Flight",
                                "ShowPostDestructionTreeMergeDialog: only debris leaves still block same-scene merge — finalizing tree in flight");
                        }
                        break;
                }

                break;
            }

            double commitUT = Planetarium.GetUniversalTime();

            // FinalizeTreeRecordings handles ForceStop + FlushRecorderToTreeRecording
            FinalizeTreeRecordings(activeTree, commitUT, isSceneExit: false);
            ParsekLog.Info("Flight",
                $"ShowPostDestructionTreeMergeDialog: finalized tree '{activeTree.TreeName}'");

            PendingTreeSlot.StashPendingTree(activeTree);
            ParsekLog.Info("Flight",
                $"ShowPostDestructionTreeMergeDialog: stashed pending tree '{activeTree.TreeName}'");

            string autoDiscardReason = null;
            // Auto-discard pad failures: if every recording in the tree is a pad failure
            // (< 10s duration AND < 30m from launch), discard the whole tree.
            // Also discard if every recording is idle-on-pad (< 30m, any duration).
            if (IsTreePadFailure(activeTree) || IsTreeIdleOnPad(activeTree))
            {
                autoDiscardReason = IsTreePadFailure(activeTree) ? "pad failure" : "idle on pad";
                ParsekLog.Info("Flight",
                    $"ShowPostDestructionTreeMergeDialog: tree {autoDiscardReason} — auto-discarding");
                ScreenMessage($"Recording discarded - {autoDiscardReason}", 3f);
            }

            // Clean up flight state
            recorder = null;
            if (backgroundRecorder != null)
            {
                backgroundRecorder.Shutdown();
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                backgroundRecorder = null;
            }
            activeTree = null;
            treeDestructionDialogPending = false;

            if (!string.IsNullOrEmpty(autoDiscardReason))
            {
                ParsekScenario.DiscardPendingTreeAndRecalculate(
                    $"post-destruction {autoDiscardReason} auto-discard");
                yield break;
            }

            // #434: do NOT show the in-flight merge dialog or commit via auto-merge here.
            // The tree is stashed; the stock KSP crash report shows first (with Revert /
            // Space Center). If the player reverts, ParsekScenario.OnLoad's isRevert branch
            // auto-discards the pending tree via TreeDiscardService.DiscardPendingTree (see #431
            // for the purge semantics). Otherwise the destination scene's OnLoad auto-commit
            // branch commits it (no dialog: the deferred merge dialog was retired, todo GUI-D5).
            ParsekLog.Info("Flight",
                "ShowPostDestructionTreeMergeDialog: pending tree stashed — deferring to post-report scene transition");
        }

        internal static bool ApplyFinalSegmentPhaseFromCapture(
            Recording treeRec,
            string activeRecordingId,
            Recording captureAtStop,
            RecordingTree treeContext = null)
        {
            if (!ParsekFlightMergeFlow.ShouldApplyFinalSegmentPhaseFromCapture(treeRec, activeRecordingId, captureAtStop, treeContext))
                return false;

            return ParsekFlightMergeFlow.SetFinalSegmentPhase(
                treeRec,
                captureAtStop.SegmentPhase,
                captureAtStop.SegmentBodyName,
                "CaptureAtStop",
                captureAtStop.RecordingId);
        }

        /// <param name="treeContext">The recording's tree. With it, a recording whose child
        /// branch point leaves its vessel line open (a breakup-continuous flight that staged
        /// and kept flying) is still the endpoint row; without it any child branch point
        /// refuses.</param>
        internal static bool ShouldAllowFinalEndpointSegmentPhase(
            Recording rec,
            string activeRecordingId,
            RecordingTree treeContext = null)
        {
            if (rec == null)
                return false;
            if (string.IsNullOrEmpty(activeRecordingId)
                || !string.Equals(activeRecordingId, rec.RecordingId, StringComparison.Ordinal))
                return false;
            if (!string.IsNullOrEmpty(rec.ChildBranchPointId)
                && (treeContext == null
                    || !RecordingTree.ChildBranchLeavesVesselLineOpen(
                        rec, treeContext.Recordings, treeContext.BranchPoints)))
                return false;
            if (ParsekFlightMergeFlow.HasCommittedChainSegmentOwnership(rec))
                return false;

            return string.IsNullOrEmpty(rec.ChainId)
                || ParsekFlightMergeFlow.HasExplicitReFlyProvisionalIdentity(rec);
        }

        /// <summary>
        /// Formats one committed recording's terminal verdict. Pure and
        /// <c>internal static</c> so the exact wording is unit-testable without a
        /// live flight scene (the live call site is <see cref="CommitTreeFlight"/>).
        /// </summary>
        internal static string FormatCommitTerminalLine(Recording rec)
        {
            string recId = string.IsNullOrEmpty(rec?.RecordingId) ? "(null)" : rec.RecordingId;
            string terminalState = rec?.TerminalStateValue?.ToString() ?? "(null)";
            // A LANDED / SPLASHED recording never carries TerminalOrbitBody:
            // CaptureTerminalOrbit returns early for surface situations, and the
            // finalizer's refresh is gated on UsesTerminalOrbitMetadata, which
            // excludes Landed. Its body lives on TerminalPosition instead. Without
            // a fallback the ONE fact a landed-on-another-body recording exists
            // to prove - WHICH body it is sitting on - appears in no log line at
            // all, which is exactly the gap that let the orbit lane's commit claim
            // ship unverified.
            //
            // The precedence is TERMINAL-STATE-AWARE, not "orbit body if non-empty"
            // (review 2026-07-26). NOTHING ever CLEARS TerminalOrbitBody: it is
            // write-only from CaptureTerminalOrbit, CopyTerminalOrbitFromSegment
            // and the ballistic scene-exit finalizer. So a craft that orbited the
            // Mun, came home and landed on KERBIN still carries
            // TerminalOrbitBody="Mun" alongside TerminalPosition.body="Kerbin",
            // and an orbit-body-first rule would print
            // `terminalState=Landed terminalOrbitBody=Mun` - confidently wrong,
            // and wrong in the one line an operator uses to answer "where did
            // this recording end". For a SURFACE terminal the vessel's position
            // IS the authority; for every other terminal the orbit metadata is.
            string terminalBody = IsSurfaceTerminalState(rec?.TerminalStateValue)
                ? FirstNonEmpty(rec?.TerminalPosition?.body, rec?.TerminalOrbitBody)
                : FirstNonEmpty(rec?.TerminalOrbitBody, rec?.TerminalPosition?.body);
            if (string.IsNullOrEmpty(terminalBody))
            {
                terminalBody = "(null)";
            }
            return $"CommitTreeFlight terminal: rec={recId} " +
                $"terminalState={terminalState} terminalOrbitBody={terminalBody}";
        }

        /// <summary>
        /// True when the terminal classification puts the vessel ON a body's surface,
        /// so <see cref="Recording.TerminalPosition"/> is the authoritative body and
        /// any stale <c>TerminalOrbitBody</c> from an earlier orbital phase is not.
        /// The complement of <c>UsesTerminalOrbitMetadata</c> plus the states that
        /// carry no body at all (Destroyed / Recovered / Boarded fall through to the
        /// orbit-first branch, where both fields are usually empty anyway).
        /// </summary>
        internal static bool IsSurfaceTerminalState(TerminalState? state)
        {
            return state == TerminalState.Landed || state == TerminalState.Splashed;
        }

        /// <summary>
        /// The first non-empty of two candidate body names, or <c>null</c>.
        /// </summary>
        private static string FirstNonEmpty(string first, string second)
        {
            return string.IsNullOrEmpty(first) ? second : first;
        }

        /// <summary>
        /// Formats the summary emitted instead of per-recording lines when a tree
        /// carries more than <see cref="CommitTerminalLogLimit"/> recordings. Names
        /// the ROOT recording's verdict so the line still identifies where the tree
        /// ended. The line LEADS with the exact per-recording token shape
        /// (<c>CommitTreeFlight terminal: rec=... terminalState=...
        /// terminalOrbitBody=...</c>) because the harness logContract specs
        /// (B11/B12/B13/B14/B16) pin that shape as required commit evidence; a
        /// debris-heavy tree above the cap must not red a green flight with
        /// "required pattern not matched" reading as "commit never happened".
        /// Residual: only the ROOT recording's tokens survive the cap, so specs
        /// pinning token classes for NON-root recordings (the B11/B12 Kerbin-core
        /// and Destroyed rows) still rely on the tree staying under the cap.
        /// </summary>
        internal static string FormatCommitTerminalSummaryLine(RecordingTree tree)
        {
            int total = tree?.Recordings?.Count ?? 0;
            Recording root = null;
            if (tree?.Recordings != null && !string.IsNullOrEmpty(tree.RootRecordingId))
                tree.Recordings.TryGetValue(tree.RootRecordingId, out root);

            string rootId = string.IsNullOrEmpty(root?.RecordingId)
                ? (string.IsNullOrEmpty(tree?.RootRecordingId) ? "(null)" : tree.RootRecordingId)
                : root.RecordingId;
            string rootState = root?.TerminalStateValue?.ToString() ?? "(null)";
            // Same terminal-state-aware precedence as the per-recording line: this
            // summary replaces those lines above the cap, so it must not report a
            // DIFFERENT body for the same root recording.
            string rootBody = IsSurfaceTerminalState(root?.TerminalStateValue)
                ? FirstNonEmpty(root?.TerminalPosition?.body, root?.TerminalOrbitBody)
                : FirstNonEmpty(root?.TerminalOrbitBody, root?.TerminalPosition?.body);
            if (string.IsNullOrEmpty(rootBody))
            {
                rootBody = "(null)";
            }

            return $"CommitTreeFlight terminal: rec={rootId} " +
                $"terminalState={rootState} terminalOrbitBody={rootBody} " +
                $"(root; summary of {total} recordings over the " +
                $"{CommitTerminalLogLimit} per-line cap)";
        }

        /// <summary>
        /// Logs the terminal classification + terminal orbit body of every recording
        /// in a tree that is about to be committed. One line per recording while the
        /// set is bounded; a single summary naming the root recording above the cap.
        /// </summary>
        internal static void LogCommitTerminalVerdicts(RecordingTree tree)
        {
            if (tree?.Recordings == null || tree.Recordings.Count == 0)
            {
                ParsekLog.Info("Flight",
                    "CommitTreeFlight terminal: no recordings in tree " +
                    $"'{tree?.TreeName ?? "(null)"}'");
                return;
            }

            if (tree.Recordings.Count > CommitTerminalLogLimit)
            {
                ParsekLog.Info("Flight", FormatCommitTerminalSummaryLine(tree));
                return;
            }

            foreach (var kvp in tree.Recordings)
                ParsekLog.Info("Flight", FormatCommitTerminalLine(kvp.Value));
        }

        internal static void FinalizeTreeRecordingsAfterFlush(
            RecordingTree tree,
            double commitUT,
            bool isSceneExit,
            Func<Recording, RecordingFinalizationCache> resolveFinalizationCache = null,
            Func<Recording, bool> resolveDestroyedDuringRecording = null,
            Func<uint, Vessel> findVesselByPid = null,
            FinalizationLiveVesselAccess liveVesselAccess = null)
        {
            var sceneExitLifetimeExtendedIds = isSceneExit
                ? new HashSet<string>(StringComparer.Ordinal)
                : null;

            // 3. Process each recording in the tree
            RecordingFinalizationCache activeFinalizationCache = null;
            foreach (var kvp in tree.Recordings)
            {
                Recording recording = kvp.Value;
                RecordingFinalizationCache finalizationCache =
                    resolveFinalizationCache != null
                        ? resolveFinalizationCache(recording)
                        : null;
                if (!string.IsNullOrEmpty(tree.ActiveRecordingId)
                    && string.Equals(recording?.RecordingId, tree.ActiveRecordingId, StringComparison.Ordinal))
                {
                    activeFinalizationCache = finalizationCache;
                }

                bool vesselDestroyedDuringRecording =
                    resolveDestroyedDuringRecording != null
                    && resolveDestroyedDuringRecording(recording);

                if (FinalizeIndividualRecording(
                        recording,
                        commitUT,
                        isSceneExit,
                        finalizationCache,
                        tree,
                        vesselDestroyedDuringRecording,
                        findVesselByPid,
                        liveVesselAccess)
                    && sceneExitLifetimeExtendedIds != null
                    && !string.IsNullOrEmpty(recording?.RecordingId))
                {
                    sceneExitLifetimeExtendedIds.Add(recording.RecordingId);
                }
            }

            // 3b. Ensure active recording has terminalState only when it is a true
            // non-leaf. Active leaves already went through FinalizeIndividualRecording
            // above; running the active fallback again can consume stale scene-exit
            // evidence after the restored-record guard has already cleared.
            if (ShouldEnsureActiveRecordingTerminalState(tree)
                && EnsureActiveRecordingTerminalState(
                        tree,
                        isSceneExit,
                        commitUT,
                        activeFinalizationCache)
                && sceneExitLifetimeExtendedIds != null
                && !string.IsNullOrEmpty(tree.ActiveRecordingId))
            {
                sceneExitLifetimeExtendedIds.Add(tree.ActiveRecordingId);
            }
            RefreshActiveEffectiveLeafSnapshot(tree, isSceneExit, sceneExitLifetimeExtendedIds);

            // 4. Prune zero-point debris leaves (#173) — removes recordings with no
            // trajectory data that were created from same-frame destruction debris.
            PruneZeroPointLeaves(tree);
            PruneSinglePointDebrisLeaves(tree);

            // Phase F: tree-level resource delta capture removed. The ledger is
            // the single source of truth for funds/science/reputation; per-recording
            // ledger actions committed during the tree's lifetime cover the cost.
            ParsekLog.Info("Flight",
                $"FinalizeTreeRecordings: tree '{tree.TreeName}' finalized " +
                $"(resource accounting via ledger; no tree-level delta captured).");
        }

        internal static bool HasFallbackCandidateCache(
            RecordingFinalizationCache cache,
            bool vesselMissing)
        {
            return cache != null && vesselMissing;
        }

        internal static bool ShouldRepairExistingTerminalFromDestroyedCache(
            Recording recording,
            RecordingFinalizationCache cache,
            double commitUT)
        {
            if (recording == null || cache == null)
                return false;
            if (!recording.TerminalStateValue.HasValue)
                return false;
            if (!cache.TerminalState.HasValue
                || cache.TerminalState.Value != TerminalState.Destroyed)
                return false;

            TerminalState existing = recording.TerminalStateValue.Value;
            if (existing == TerminalState.Destroyed)
            {
                ParsekFlightMergeFlow.LogDestroyedCacheRepairRejected(recording, cache, commitUT, existing, "alreadyDestroyed");
                return false;
            }

            // The cache is allowed to correct the in-flight ballistic fallback
            // SubOrbital is the speculative state stamped when the vessel was
            // still on a ballistic path. Orbiting is deliberately excluded:
            // it requires stronger periapsis/situation evidence and is treated
            // as a stable spawn endpoint, not a prediction to repair.
            if (existing != TerminalState.SubOrbital)
            {
                ParsekFlightMergeFlow.LogDestroyedCacheRepairRejected(
                    recording,
                    cache,
                    commitUT,
                    existing,
                    "existingTerminalNotSubOrbital");
                return false;
            }

            if (!ParsekFlightMergeFlow.IsCacheTerminalAtOrBeforeCommit(cache, commitUT))
            {
                ParsekFlightMergeFlow.LogDestroyedCacheRepairRejected(
                    recording,
                    cache,
                    commitUT,
                    existing,
                    "futureOrInvalidTerminalUT");
                return false;
            }

            return true;
        }

        internal static bool ShouldRefreshActiveEffectiveLeafSnapshot(
            RecordingTree tree,
            Recording activeRec)
        {
            if (tree == null || activeRec == null)
                return false;
            if (string.IsNullOrEmpty(tree.ActiveRecordingId)
                || tree.ActiveRecordingId != activeRec.RecordingId)
                return false;
            if (string.IsNullOrEmpty(activeRec.ChildBranchPointId))
                return false;
            if (!activeRec.TerminalStateValue.HasValue
                || !GhostPlaybackLogic.IsSpawnableTerminal(activeRec.TerminalStateValue.Value))
                return false;

            return GhostPlaybackLogic.IsEffectiveLeafForVessel(activeRec, tree);
        }

        internal static void RefreshActiveEffectiveLeafSnapshot(
            RecordingTree tree,
            bool isSceneExit,
            ISet<string> sceneExitLifetimeExtendedIds = null)
        {
            if (tree == null || string.IsNullOrEmpty(tree.ActiveRecordingId))
                return;
            if (!tree.Recordings.TryGetValue(tree.ActiveRecordingId, out var activeRec))
                return;
            if (!ShouldRefreshActiveEffectiveLeafSnapshot(tree, activeRec))
                return;
            if (sceneExitLifetimeExtendedIds != null
                && sceneExitLifetimeExtendedIds.Contains(activeRec.RecordingId))
            {
                ParsekLog.Verbose("Flight",
                    $"FinalizeTreeRecordings: active effective leaf '{activeRec.RecordingId}' " +
                    "uses scene-exit extended lifetime — skipping live re-snapshot");
                return;
            }

            Vessel activeVessel = activeRec.VesselPersistentId != 0
                ? FlightRecorder.FindVesselByPid(activeRec.VesselPersistentId)
                : null;
            if (activeVessel == null)
            {
                ParsekLog.Verbose("Flight",
                    $"FinalizeTreeRecordings: active effective leaf '{activeRec.RecordingId}' " +
                    $"stable terminal={activeRec.TerminalStateValue} but vessel not found " +
                    $"(isSceneExit={isSceneExit}) — keeping existing snapshot");
                return;
            }

            CaptureTerminalOrbit(activeRec, activeVessel);
            CaptureTerminalPosition(activeRec, activeVessel);
            RecordingEndpointResolver.RefreshEndpointDecision(activeRec, "FinalizeTreeRecordings.StableLeaf");
            TryRefreshStableTerminalSnapshot(
                activeRec,
                activeVessel,
                isSceneExit,
                "FinalizeTreeRecordings: re-snapshotted active effective leaf");
        }

        internal static bool ApplyFinalEndpointSegmentPhase(
            Recording rec,
            RecordingTree treeContext,
            bool finalizeVesselFound,
            Vessel finalizeVessel)
        {
            string activeRecordingId = treeContext?.ActiveRecordingId;
            if (!ShouldAllowFinalEndpointSegmentPhase(rec, activeRecordingId, treeContext))
                return false;

            if (!ParsekFlightMergeFlow.TryResolveFinalEndpointSegmentPhase(
                rec,
                finalizeVesselFound,
                finalizeVessel,
                out string phase,
                out string bodyName,
                out string source,
                out string skipReason))
            {
                ParsekLog.Verbose("Flight",
                    $"Final SegmentPhase skipped: rec={rec?.RecordingId ?? "<none>"} " +
                    $"reason={skipReason ?? "unresolved"}");
                return false;
            }

            return ParsekFlightMergeFlow.SetFinalSegmentPhase(rec, phase, bodyName, source);
        }

        /// <summary>
        /// Infers terminal state from a recording's last trajectory point when the
        /// vessel is no longer findable (e.g., unloaded during scene exit). Uses the
        /// last point's altitude to distinguish landed/splashed from in-flight.
        /// Returns SubOrbital as a safe default if no trajectory data is available.
        /// </summary>
        internal static TerminalState InferTerminalStateFromTrajectory(Recording rec)
        {
            if (rec.Points == null || rec.Points.Count == 0)
                return TerminalState.SubOrbital;

            var lastPt = rec.Points[rec.Points.Count - 1];

            // Very low altitude → likely landed or about to land
            if (lastPt.altitude < 50.0)
                return TerminalState.Landed;

            // Check last track section environment for surface indication
            if (rec.TrackSections != null && rec.TrackSections.Count > 0)
            {
                var lastEnv = rec.TrackSections[rec.TrackSections.Count - 1].environment;
                if (lastEnv == SegmentEnvironment.SurfaceMobile
                    || lastEnv == SegmentEnvironment.SurfaceStationary)
                    return TerminalState.Landed;
            }

            // Check orbit segments for stable orbit (ecc < 1, periapsis above body surface).
            if (rec.OrbitSegments != null && rec.OrbitSegments.Count > 0)
            {
                var lastOrbit = rec.OrbitSegments[rec.OrbitSegments.Count - 1];
                if (HasStableOrbitEvidenceForTerminalInference(lastOrbit))
                    return TerminalState.Orbiting;
            }

            // Default: vessel was in flight (atmospheric descent, suborbital, etc.)
            return TerminalState.SubOrbital;
        }

        /// <summary>
        /// Orbiting evidence for a recording whose vessel is gone: a bound last orbit segment
        /// whose periapsis clears the body's periapsis floor (the atmosphere top, or the
        /// highest terrain on an airless body), the same line commit classification and the
        /// terminal-orbit spawn-safety check use. A periapsis inside the atmosphere is a
        /// decaying flight, so it is not Orbiting evidence and the caller falls back to
        /// SubOrbital.
        /// </summary>
        internal static bool HasStableOrbitEvidenceForTerminalInference(OrbitSegment lastOrbit)
        {
            if (lastOrbit.eccentricity >= 1.0 || string.IsNullOrEmpty(lastOrbit.bodyName))
                return false;
            if (!ParsekFlightMergeFlow.TryResolveTerminalInferenceBody(
                    lastOrbit.bodyName, out double bodyRadius, out double periapsisFloor)
                || bodyRadius <= 0.0)
                return false;

            double periapsisRadius = lastOrbit.semiMajorAxis * (1.0 - lastOrbit.eccentricity);
            bool clear = OrbitClearance.IsBoundOrbitClear(
                lastOrbit.eccentricity,
                periapsisRadius,
                bodyRadius,
                periapsisFloor);
            if (!clear
                && OrbitClearance.IsFinite(periapsisRadius)
                && periapsisRadius > bodyRadius)
            {
                ParsekLog.Verbose("Flight", string.Format(CultureInfo.InvariantCulture,
                    "HasStableOrbitEvidenceForTerminalInference: periapsis {0:F0}m on {1} is under " +
                    "the periapsis floor {2:F0}m - not Orbiting evidence",
                    periapsisRadius - bodyRadius,
                    lastOrbit.bodyName,
                    periapsisFloor));
            }
            return clear;
        }

        /// <summary>
        /// Returns true when the only outcome
        /// <see cref="InferTerminalStateFromTrajectory"/> can produce for this
        /// recording is the <see cref="TerminalState.SubOrbital"/> default (i.e.
        /// no altitude&lt;50m point, no SurfaceMobile/Stationary terminal section,
        /// no stable orbit segment) AND the trajectory carries fewer than 2
        /// points. A single high-altitude point is typically a quickload-trim
        /// or optimizer-split survivor whose actual terminal data lived on a
        /// now-removed sibling chain segment — the SubOrbital default would be
        /// a fabricated guess, not measurement.
        /// </summary>
        internal static bool HasOnlySubOrbitalFallbackEvidence(Recording rec)
        {
            string reason;
            return HasOnlySubOrbitalFallbackEvidence(rec, out reason);
        }

        internal static bool HasOnlySubOrbitalFallbackEvidence(Recording rec, out string reason)
        {
            reason = null;
            if (rec == null) return false;
            // Need at least 2 points to corroborate "vessel was actually mid-flight"
            // rather than "single trim-survivor start point".
            if (rec.Points != null && rec.Points.Count >= 2)
                return false;

            // Single low-altitude point = real landed evidence; let the inference run.
            if (rec.Points != null && rec.Points.Count == 1
                && rec.Points[0].altitude < 50.0)
                return false;

            // SurfaceMobile/Stationary last section = real landed evidence.
            if (rec.TrackSections != null && rec.TrackSections.Count > 0)
            {
                var lastEnv = rec.TrackSections[rec.TrackSections.Count - 1].environment;
                if (lastEnv == SegmentEnvironment.SurfaceMobile
                    || lastEnv == SegmentEnvironment.SurfaceStationary)
                    return false;
            }

            // Stable closed orbit = real orbiting evidence, but only for a
            // one-point payload. With zero points, InferTerminalStateFromTrajectory
            // returns SubOrbital before checking orbit segments.
            if (rec.Points != null && rec.Points.Count == 1
                && rec.OrbitSegments != null && rec.OrbitSegments.Count > 0)
            {
                var lastOrbit = rec.OrbitSegments[rec.OrbitSegments.Count - 1];
                if (HasStableOrbitEvidenceForTerminalInference(lastOrbit))
                    return false;
            }

            reason = "trajectory has insufficient points for inference " +
                $"(points={(rec.Points?.Count ?? 0)} < 2 with no Landed/Orbiting signal; " +
                "likely quickload-trim or split survivor whose terminal lived on a " +
                "now-removed sibling chain segment)";
            return true;
        }

        internal static void PopulateTerminalPositionFromLastPoint(Recording rec, TerminalState inferredState)
        {
            if (rec?.Points == null || rec.Points.Count == 0)
                return;
            if (inferredState != TerminalState.Landed && inferredState != TerminalState.Splashed)
                return;

            var lastPt = rec.Points[rec.Points.Count - 1];
            rec.TerminalPosition = new SurfacePosition
            {
                body = string.IsNullOrEmpty(lastPt.bodyName) ? "Kerbin" : lastPt.bodyName,
                latitude = lastPt.latitude,
                longitude = lastPt.longitude,
                altitude = lastPt.altitude,
                rotation = lastPt.rotation,
                rotationRecorded = true,
                situation = inferredState == TerminalState.Splashed
                    ? SurfaceSituation.Splashed
                    : SurfaceSituation.Landed
            };
        }

        private static bool TryRefreshStableTerminalSnapshot(
            Recording rec,
            Vessel vessel,
            bool isSceneExit,
            string logPrefix)
        {
            if (rec == null || vessel == null || !rec.TerminalStateValue.HasValue)
                return false;

            var ts = rec.TerminalStateValue.Value;
            ConfigNode freshSnapshot = FlightRecorder.TryBackupSnapshot(vessel);
            if (freshSnapshot == null)
            {
                ParsekLog.Verbose("Flight",
                    $"{logPrefix}: re-snapshot returned null for '{rec.RecordingId}' " +
                    $"(terminal={ts}, isSceneExit={isSceneExit}) — keeping stale snapshot");
                return false;
            }

            rec.VesselSnapshot = freshSnapshot;
            if (rec.GhostVisualSnapshot == null)
                rec.GhostVisualSnapshot = freshSnapshot.CreateCopy();
            rec.MarkFilesDirty();
            string orbitInfo = "";
            if ((ts == TerminalState.Orbiting || ts == TerminalState.SubOrbital)
                && vessel.orbit != null)
            {
                orbitInfo = string.Format(CultureInfo.InvariantCulture,
                    " orbit[body={0} sma={1:F1} ecc={2:F4} periAlt={3:F1} apoAlt={4:F1} inc={5:F2}]",
                    vessel.orbit.referenceBody?.name ?? vessel.mainBody?.name ?? "?",
                    vessel.orbit.semiMajorAxis,
                    vessel.orbit.eccentricity,
                    vessel.orbit.PeA,
                    vessel.orbit.ApA,
                    vessel.orbit.inclination);
            }
            ParsekLog.Info("Flight",
                $"{logPrefix} '{rec.RecordingId}' with stable terminal state {ts} " +
                $"(vessel.situation={vessel.situation}, isSceneExit={isSceneExit}){orbitInfo} [#289]");
            return true;
        }

        internal static ConfigNode NormalizeStableTerminalSnapshotForPersistence(
            ConfigNode freshSnapshot,
            TerminalState terminalState,
            CelestialBody body = null)
        {
            return LiveVesselSnapshot.NormalizeStableSnapshotForPersistence(
                freshSnapshot,
                terminalState,
                body,
                "stable-terminal snapshot persistence");
        }
    }
}
