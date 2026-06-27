using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    public partial class ParsekFlight
    {
        /// <summary>
        /// OnFlightReady fallback that dispatches the tree merge dialog or
        /// logs the appropriate skip line. Extracted so the in-game test
        /// harness can drive the wiring directly via reflection without
        /// having to fire <see cref="GameEvents.onFlightReady"/> and pull in
        /// the rest of OnFlightReady's side effects.
        ///
        /// <para>
        /// On non-revert scene changes, pending trees are auto-committed by
        /// <see cref="ParsekScenario"/>. Reaching here means either a revert
        /// or a fallback (auto-commit missed). #293: skip when restore
        /// coroutine is running — it owns the pending tree and will either
        /// resume recording or leave it in Limbo. Re-Fly guard: skip when an
        /// in-place-continuation Re-Fly session owns the pending tree, or
        /// when a Re-Fly invocation is mid-write (<see cref="RewindInvokeContext.Pending"/>).
        /// Placeholder-mode Re-Fly markers do NOT skip the dialog — the
        /// recorder-restore carve-out cannot bind a recorder in that mode
        /// (the marker swap returns <c>placeholder-pattern</c> and the wait
        /// loop times out), so the merge-dialog fallback is the player's
        /// only recovery path.
        /// </para>
        /// </summary>
        internal void MaybeShowPendingTreeMergeDialogOnFlightReady()
        {
            bool reFlyOwnsPendingTree =
                ParsekScenario.IsReFlyInPlaceContinuationActive()
                || RewindInvokeContext.Pending;
            if (ShouldShowOnFlightReadyMergeDialog(
                    hasPendingTree: RecordingStore.HasPendingTree,
                    restoringActiveTree: restoringActiveTree,
                    reFlyInPlaceContinuationActive: reFlyOwnsPendingTree))
            {
                var pt = RecordingStore.PendingTree;
                ParsekLog.Warn("Flight",
                    $"Pending tree '{pt.TreeName}' reached OnFlightReady — showing tree merge dialog (fallback)");
                MergeDialog.ShowTreeDialog(pt);
            }
            else if (RecordingStore.HasPendingTree && restoringActiveTree)
            {
                ParsekLog.Info("Flight",
                    $"Pending tree '{RecordingStore.PendingTree.TreeName}' skipped — " +
                    "restore coroutine in progress (#293)");
            }
            else if (RecordingStore.HasPendingTree && reFlyOwnsPendingTree)
            {
                ParsekLog.Info("Flight",
                    $"Pending tree '{RecordingStore.PendingTree.TreeName}' reached OnFlightReady — " +
                    "skipping merge dialog: in-place Re-Fly session owns the pending tree (Retry/initial invoke)");
            }
        }

        internal static bool ShouldEnsureActiveRecordingTerminalState(RecordingTree tree)
        {
            if (tree == null || string.IsNullOrEmpty(tree.ActiveRecordingId))
                return false;

            Recording activeRec;
            if (!tree.Recordings.TryGetValue(tree.ActiveRecordingId, out activeRec) || activeRec == null)
                return false;

            if (string.IsNullOrEmpty(activeRec.ChildBranchPointId))
                return false;

            return !GhostPlaybackLogic.IsEffectiveLeafForVessel(activeRec, tree);
        }

        private RecordingFinalizationCache ResolveFinalizationCacheForRecording(
            RecordingTree tree,
            Recording recording)
        {
            if (recording == null)
                return null;

            RecordingFinalizationCache cache = null;
            if (recorder != null
                && IsActiveRecorderCacheCandidate(tree, recording, recorder))
            {
                cache = recorder.GetFinalizationCacheForRecording(recording);
                if (CacheIdentityMatchesRecording(recording, cache))
                    return cache;
            }

            if (backgroundRecorder != null)
            {
                cache = backgroundRecorder.GetFinalizationCacheForRecording(recording);
                if (CacheIdentityMatchesRecording(recording, cache))
                    return cache;
            }

            return null;
        }

        /// <summary>
        /// Sets terminal state on the active recording even if it is a non-leaf node.
        /// FinalizeIndividualRecording skips non-leaves, but the active recording needs
        /// terminal state for the optimizer's SplitAtSection propagation. On scene-exit
        /// paths, falls back to trajectory-based inference when the live vessel is no
        /// longer available.
        /// </summary>
        internal static bool EnsureActiveRecordingTerminalState(
            RecordingTree tree,
            bool isSceneExit = false,
            double commitUT = double.NaN,
            RecordingFinalizationCache finalizationCache = null)
        {
            if (string.IsNullOrEmpty(tree.ActiveRecordingId))
                return false;

            Recording activeRec;
            if (!tree.Recordings.TryGetValue(tree.ActiveRecordingId, out activeRec))
                return false;

            if (activeRec.TerminalStateValue.HasValue)
            {
                ParsekLog.Verbose("Flight",
                    $"FinalizeTreeRecordings: active recording '{activeRec.RecordingId}' " +
                    $"already has terminalState={activeRec.TerminalStateValue} — skipping");
                return false;
            }

            Vessel v = activeRec.VesselPersistentId != 0
                ? FlightRecorder.FindVesselByPid(activeRec.VesselPersistentId)
                : null;
            bool sceneExitLifetimeExtended = false;
            bool sceneExitSuppliedSnapshots = false;
            bool sceneExitSuppliedTerminalOrbit = false;
            bool preserveRestoredSceneExitTerminalState =
                ShouldPreserveRestoredSceneExitTerminalState(
                    activeRec,
                    isSceneExit,
                    vesselMissing: v == null,
                    out string restoredSceneExitReason);

            if (isSceneExit && !preserveRestoredSceneExitTerminalState)
            {
                ConfigNode vesselSnapshotBefore = activeRec.VesselSnapshot;
                TerminalOrbitMetadataSnapshot terminalOrbitBefore =
                    CaptureTerminalOrbitMetadataSnapshot(activeRec);
                sceneExitLifetimeExtended = IncompleteBallisticSceneExitFinalizer.TryApply(
                    activeRec,
                    v,
                    commitUT,
                    "EnsureActiveRecordingTerminalState",
                    tree);
                sceneExitSuppliedSnapshots =
                    !ReferenceEquals(vesselSnapshotBefore, activeRec.VesselSnapshot)
                    && activeRec.VesselSnapshot != null;
                sceneExitSuppliedTerminalOrbit =
                    sceneExitLifetimeExtended
                    && DidSceneExitUpdateTerminalOrbitMetadata(terminalOrbitBefore, activeRec);
                if (sceneExitLifetimeExtended)
                {
                    if (activeRec.TerminalStateValue.HasValue
                        && UsesTerminalOrbitMetadata(activeRec.TerminalStateValue.Value))
                    {
                        if (sceneExitSuppliedTerminalOrbit)
                        {
                            ParsekLog.Verbose("Flight",
                                $"EnsureActiveRecordingTerminalState: preserving scene-exit terminal orbit for " +
                                $"'{activeRec.RecordingId}' (body={activeRec.TerminalOrbitBody}, " +
                                $"terminal={activeRec.TerminalStateValue})");
                        }
                        else
                        {
                            PopulateTerminalOrbitFromLastSegment(activeRec);
                            if (string.IsNullOrEmpty(activeRec.TerminalOrbitBody))
                            {
                                ParsekLog.Warn("Flight",
                                    $"EnsureActiveRecordingTerminalState: scene-exit terminal orbit remains empty for " +
                                    $"'{activeRec.RecordingId}' (terminal={activeRec.TerminalStateValue}, " +
                                    $"orbitSegments={activeRec.OrbitSegments?.Count ?? 0})");
                            }
                        }
                    }
                    return sceneExitSuppliedSnapshots;
                }
            }

            if (v != null)
            {
                activeRec.TerminalStateValue =
                    RecordingTree.DetermineTerminalState((int)v.situation, v);
                RecordingEndpointResolver.RefreshEndpointDecision(activeRec, "FinalizeTreeRecordings.LiveNonLeaf");
                ParsekLog.Info("Flight",
                    $"FinalizeTreeRecordings: set terminalState=" +
                    $"{activeRec.TerminalStateValue} on active recording " +
                    $"'{activeRec.RecordingId}' (non-leaf, vessel situation={v.situation})");
                return false;
            }

            if (!activeRec.TerminalStateValue.HasValue
                && !preserveRestoredSceneExitTerminalState
                && HasFallbackCandidateCache(finalizationCache, v == null)
                && TryApplyFinalizationCacheFallback(
                    activeRec,
                    finalizationCache,
                    "EnsureActiveRecordingTerminalState",
                    allowStale: v == null,
                    out _))
            {
                return false;
            }

            if (isSceneExit)
            {
                // PR #572 follow-up: same gate as the leaf path — when the
                // active recording was just repaired from the committed tree
                // this frame, do not overwrite its (intentionally unset)
                // terminal state with a Landed/Splashed inference based on
                // the last trajectory point. The gate's only clause is
                // RestoredFromCommittedTreeThisFrame; an additional
                // orbital-evidence clause was considered (Option D in the
                // design plan) and rejected because the legitimate
                // orbit-then-land case shares the same shape (high
                // MaxDistanceFromLaunch + stable orbit segment + low-altitude
                // last point) — see ShouldSkipSceneExitSurfaceInferenceForRestoredRecording's
                // doc comment.
                //
                // Bug 2 follow-up (2026-04-30 STASH probe regression): also skip the
                // inference when the surviving payload would only support the
                // SubOrbital fallback default — see HasOnlySubOrbitalFallbackEvidence's
                // doc.
                string subOrbitalFallbackReason;
                bool subOrbitalFallbackOnly =
                    HasOnlySubOrbitalFallbackEvidence(activeRec, out subOrbitalFallbackReason);
                if (preserveRestoredSceneExitTerminalState
                    || ShouldSkipSceneExitSurfaceInferenceForRestoredRecording(
                        activeRec, out restoredSceneExitReason)
                    || subOrbitalFallbackOnly)
                {
                    activeRec.RestoredFromCommittedTreeThisFrame = false;
                    PopulateTerminalOrbitFromLastSegment(activeRec);
                    string skipReason = restoredSceneExitReason ?? subOrbitalFallbackReason;
                    ParsekLog.Info("Flight",
                        $"FinalizeTreeRecordings: skipping Landed/Splashed inference " +
                        $"for active recording '{activeRec.RecordingId}' " +
                        $"(vessel pid={activeRec.VesselPersistentId}) — {skipReason} " +
                        $"(lastPtAlt={(activeRec.Points.Count > 0 ? activeRec.Points[activeRec.Points.Count - 1].altitude : double.NaN):F1}m " +
                        $"maxDist={activeRec.MaxDistanceFromLaunch:F0}m " +
                        $"orbitSegs={activeRec.OrbitSegments?.Count ?? 0})");
                    RecordingEndpointResolver.RefreshEndpointDecision(activeRec, "FinalizeTreeRecordings.SceneExitNonLeafSkipInfer");
                    return false;
                }

                var inferredState = InferTerminalStateFromTrajectory(activeRec);
                activeRec.TerminalStateValue = inferredState;
                ParsekLog.Info("Flight",
                    $"FinalizeTreeRecordings: active recording '{activeRec.RecordingId}' " +
                    $"vessel pid={activeRec.VesselPersistentId} not found on scene exit — " +
                    $"inferred {inferredState} from trajectory");
                PopulateTerminalOrbitFromLastSegment(activeRec);
                if (inferredState == TerminalState.Landed || inferredState == TerminalState.Splashed)
                {
                    PopulateTerminalPositionFromLastPoint(activeRec, inferredState);
                    TryCaptureTerrainHeightFromLastTrajectoryPoint(activeRec);
                }
                RecordingEndpointResolver.RefreshEndpointDecision(activeRec, "FinalizeTreeRecordings.SceneExitNonLeaf");
                return false;
            }

            ParsekLog.Verbose("Flight",
                $"FinalizeTreeRecordings: active recording '{activeRec.RecordingId}' " +
                $"vessel pid={activeRec.VesselPersistentId} not found before terminal-state " +
                $"assignment (isSceneExit={isSceneExit})");
            return false;
        }

        internal sealed class FinalizationLiveVesselAccess
        {
            internal static readonly FinalizationLiveVesselAccess Default =
                new FinalizationLiveVesselAccess();

            private readonly Func<Vessel, bool> isFound;
            private readonly Func<Vessel, TerminalState> determineTerminalState;
            private readonly Action<Recording, Vessel> captureTerminalOrbit;
            private readonly Action<Recording, Vessel> captureTerminalPosition;
            private readonly Func<Vessel, ConfigNode> tryBackupSnapshot;
            private readonly Func<Recording, Vessel, bool, string, bool> tryRefreshStableTerminalSnapshot;

            internal FinalizationLiveVesselAccess(
                Func<Vessel, bool> isFound = null,
                Func<Vessel, TerminalState> determineTerminalState = null,
                Action<Recording, Vessel> captureTerminalOrbit = null,
                Action<Recording, Vessel> captureTerminalPosition = null,
                Func<Vessel, ConfigNode> tryBackupSnapshot = null,
                Func<Recording, Vessel, bool, string, bool> tryRefreshStableTerminalSnapshot = null)
            {
                this.isFound = isFound ?? (vessel => vessel != null);
                this.determineTerminalState = determineTerminalState
                    ?? (vessel => RecordingTree.DetermineTerminalState((int)vessel.situation, vessel));
                this.captureTerminalOrbit = captureTerminalOrbit ?? ParsekFlight.CaptureTerminalOrbit;
                this.captureTerminalPosition = captureTerminalPosition ?? ParsekFlight.CaptureTerminalPosition;
                this.tryBackupSnapshot = tryBackupSnapshot ?? VesselSpawner.TryBackupSnapshot;
                this.tryRefreshStableTerminalSnapshot =
                    tryRefreshStableTerminalSnapshot ?? ParsekFlight.TryRefreshStableTerminalSnapshot;
            }

            internal bool IsFound(Vessel vessel) => isFound(vessel);

            internal TerminalState DetermineTerminalState(Vessel vessel) =>
                determineTerminalState(vessel);

            internal void CaptureTerminalOrbit(Recording rec, Vessel vessel) =>
                captureTerminalOrbit(rec, vessel);

            internal void CaptureTerminalPosition(Recording rec, Vessel vessel) =>
                captureTerminalPosition(rec, vessel);

            internal ConfigNode TryBackupSnapshot(Vessel vessel) =>
                tryBackupSnapshot(vessel);

            internal bool TryRefreshStableTerminalSnapshot(
                Recording rec,
                Vessel vessel,
                bool isSceneExit,
                string logPrefix) =>
                tryRefreshStableTerminalSnapshot(rec, vessel, isSceneExit, logPrefix);
        }

        internal static bool FinalizeIndividualRecording(
            Recording rec,
            double commitUT,
            bool isSceneExit,
            RecordingFinalizationCache finalizationCache = null,
            RecordingTree treeContext = null,
            bool vesselDestroyedDuringRecording = false,
            Func<uint, Vessel> findVesselByPid = null,
            FinalizationLiveVesselAccess liveVesselAccess = null)
        {
            var vesselAccess = liveVesselAccess ?? FinalizationLiveVesselAccess.Default;

            // Set ExplicitStartUT if not already set
            if (double.IsNaN(rec.ExplicitStartUT))
            {
                if (rec.Points.Count > 0)
                    rec.ExplicitStartUT = rec.Points[0].ut;
                else if (rec.OrbitSegments.Count > 0)
                    rec.ExplicitStartUT = rec.OrbitSegments[0].startUT;
            }

            // Set ExplicitEndUT on leaf recordings without one
            if (double.IsNaN(rec.ExplicitEndUT))
            {
                if (rec.Points.Count > 0)
                    rec.ExplicitEndUT = rec.Points[rec.Points.Count - 1].ut;
                else
                    rec.ExplicitEndUT = commitUT;
            }

            // Look up the live vessel once — shared by the terminal-determination block below
            // and the #289 re-snapshot block that follows. Avoids a double FindVesselByPid
            // for recordings that enter !HasValue, get terminal set, then hit the re-snapshot path.
            //
            // A recording also counts as a leaf when it carries a ChildBranchPointId but no
            // child of that BP shares its VesselPersistentId — i.e. the recording is the
            // effective continuation of its own PID across a side-off split (debris-only or
            // split-off-sibling-with-different-pid). Without this, the original LU recording
            // that kept its PID after a stage separation never gets a terminal state and
            // disappears from the Unfinished Flights list (#224 follow-up).
            bool isLeaf = rec.ChildBranchPointId == null
                || GhostPlaybackLogic.IsEffectiveLeafForVessel(rec, treeContext);
            Func<uint, Vessel> vesselFinder = findVesselByPid ?? FlightRecorder.FindVesselByPid;
            Vessel finalizeVessel = (isLeaf && rec.VesselPersistentId != 0)
                ? vesselFinder(rec.VesselPersistentId)
                : null;
            // Headless tests may pass uninitialized Vessel stubs; use the access seam
            // so Unity's overloaded == does not collapse them to null.
            bool finalizeVesselFound = vesselAccess.IsFound(finalizeVessel);
            string restoredSceneExitReason = null;
            bool preserveRestoredSceneExitTerminalState =
                isLeaf
                && ShouldPreserveRestoredSceneExitTerminalState(
                    rec,
                    isSceneExit,
                    vesselMissing: !finalizeVesselFound,
                    out restoredSceneExitReason);
            bool sceneExitLifetimeExtended = false;
            bool sceneExitSuppliedSnapshots = false;
            bool sceneExitSuppliedTerminalOrbit = false;
            bool cacheFinalizationApplied = false;
            bool cacheSuppliedTerminalOrbit = false;

            if (isLeaf && rec.VesselPersistentId != 0 && !finalizeVesselFound)
                ParsekLog.Verbose("Flight",
                    $"FinalizeIndividualRecording: vessel pid={rec.VesselPersistentId} not found " +
                    $"for '{rec.RecordingId}' (isSceneExit={isSceneExit}) — re-snapshot will be skipped");

            if (preserveRestoredSceneExitTerminalState && rec.TerminalStateValue.HasValue)
            {
                rec.RestoredFromCommittedTreeThisFrame = false;
                ParsekLog.Info("Flight",
                    $"FinalizeTreeRecordings: preserving repaired terminalState={rec.TerminalStateValue} " +
                    $"for '{rec.RecordingId}' (vessel pid={rec.VesselPersistentId}) — " +
                    $"{restoredSceneExitReason}");
            }

            if (isLeaf
                && isSceneExit
                && !preserveRestoredSceneExitTerminalState
                && !rec.TerminalStateValue.HasValue)
            {
                ConfigNode vesselSnapshotBefore = rec.VesselSnapshot;
                TerminalOrbitMetadataSnapshot terminalOrbitBefore =
                    CaptureTerminalOrbitMetadataSnapshot(rec);
                sceneExitLifetimeExtended = IncompleteBallisticSceneExitFinalizer.TryApply(
                    rec,
                    finalizeVessel,
                    commitUT,
                    "FinalizeIndividualRecording",
                    treeContext);
                sceneExitSuppliedSnapshots =
                    !ReferenceEquals(vesselSnapshotBefore, rec.VesselSnapshot)
                    && rec.VesselSnapshot != null;
                sceneExitSuppliedTerminalOrbit =
                    sceneExitLifetimeExtended
                    && DidSceneExitUpdateTerminalOrbitMetadata(terminalOrbitBefore, rec);
            }

            if (isLeaf
                && !preserveRestoredSceneExitTerminalState
                && ShouldRepairExistingTerminalFromDestroyedCache(
                    rec,
                    finalizationCache,
                    commitUT)
                && TryApplyFinalizationCacheFallback(
                    rec,
                    finalizationCache,
                    "FinalizeIndividualRecordingRepair",
                    allowStale: !finalizeVesselFound,
                    out _,
                    allowAlreadyFinalizedRepair: true))
            {
                cacheFinalizationApplied = true;
                cacheSuppliedTerminalOrbit = false;
            }

            // Determine terminal state for recordings that don't have one yet
            if (isLeaf && !rec.TerminalStateValue.HasValue)
            {
                if (finalizeVesselFound)
                {
                    if (isSceneExit)
                        rec.SceneExitSituation = (int)finalizeVessel.situation;
                    rec.TerminalStateValue = vesselAccess.DetermineTerminalState(finalizeVessel);
                    vesselAccess.CaptureTerminalOrbit(rec, finalizeVessel);
                    vesselAccess.CaptureTerminalPosition(rec, finalizeVessel);

                    // Re-snapshot live vessels for Commit Flight path (fresh state)
                    if (!isSceneExit)
                    {
                        ConfigNode freshSnapshot = vesselAccess.TryBackupSnapshot(finalizeVessel);
                        if (freshSnapshot != null)
                        {
                            rec.VesselSnapshot = freshSnapshot;
                            if (rec.GhostVisualSnapshot == null)
                                rec.GhostVisualSnapshot = freshSnapshot.CreateCopy();
                        }
                    }
                }
                else if (!preserveRestoredSceneExitTerminalState
                    && HasFallbackCandidateCache(
                    finalizationCache,
                    vesselMissing: true)
                    && TryApplyFinalizationCacheFallback(
                        rec,
                        finalizationCache,
                        "FinalizeIndividualRecording",
                        allowStale: true,
                        out _))
                {
                    cacheFinalizationApplied = true;
                    cacheSuppliedTerminalOrbit =
                        rec.TerminalStateValue.HasValue
                        && UsesTerminalOrbitMetadata(rec.TerminalStateValue.Value)
                        && !string.IsNullOrEmpty(rec.TerminalOrbitBody);
                }
                else if (isSceneExit)
                {
                    // Scene exit: vessel unloaded (alive) but not findable. Recordings
                    // with a recorder-observed destruction event are overridden after this
                    // terminal-assignment block. Reaching here without that flag means the
                    // vessel was alive when unloaded. Infer terminal state from the last
                    // trajectory point.
                    //
                    // PR #572 follow-up: skip the surface inference when the recording was
                    // just repaired from the committed tree this frame (the trajectory
                    // came from a copy that already lacked a terminal state, so the
                    // "vessel was alive when unloaded" heuristic does not apply — typically
                    // means the live pid was a deliberate Re-Fly strip casualty). The gate's
                    // only clause is RestoredFromCommittedTreeThisFrame; an additional
                    // orbital-evidence clause was considered (Option D in the design plan)
                    // and rejected because the legitimate orbit-then-land case shares the
                    // same shape (high MaxDistanceFromLaunch + stable orbit segment + low-
                    // altitude last point) and adding such a clause would regress
                    // EnsureActiveRecordingTerminalState_NoLiveVesselOnSceneExit_InfersFromTrajectory
                    // and SceneExitInferredActiveNonLeaf_DefaultsToPersistInMergeDialog.
                    //
                    // Bug 2 follow-up (2026-04-30 STASH probe regression): also skip the
                    // inference when the surviving payload would only support the SubOrbital
                    // fallback default — see HasOnlySubOrbitalFallbackEvidence's doc.
                    string subOrbitalFallbackReason;
                    bool subOrbitalFallbackOnly =
                        HasOnlySubOrbitalFallbackEvidence(rec, out subOrbitalFallbackReason);
                    if (preserveRestoredSceneExitTerminalState
                        || ShouldSkipSceneExitSurfaceInferenceForRestoredRecording(
                            rec, out restoredSceneExitReason)
                        || subOrbitalFallbackOnly)
                    {
                        rec.RestoredFromCommittedTreeThisFrame = false;
                        // Recover terminal orbit metadata from the last orbit segment if
                        // available — preserves the orbital fingerprint for ghost-map
                        // playback even though the terminal state remains unset.
                        PopulateTerminalOrbitFromLastSegment(rec);
                        string skipReason = restoredSceneExitReason ?? subOrbitalFallbackReason;
                        ParsekLog.Info("Flight",
                            $"FinalizeTreeRecordings: skipping Landed/Splashed inference " +
                            $"for '{rec.RecordingId}' (vessel pid={rec.VesselPersistentId}) — " +
                            $"{skipReason} " +
                            $"(lastPtAlt={(rec.Points.Count > 0 ? rec.Points[rec.Points.Count - 1].altitude : double.NaN):F1}m " +
                            $"maxDist={rec.MaxDistanceFromLaunch:F0}m " +
                            $"orbitSegs={rec.OrbitSegments?.Count ?? 0})");
                    }
                    else
                    {
                        var inferredState = InferTerminalStateFromTrajectory(rec);
                        rec.TerminalStateValue = inferredState;
                        ParsekLog.Info("Flight", $"FinalizeTreeRecordings: vessel pid={rec.VesselPersistentId} " +
                            $"not found on scene exit for recording '{rec.RecordingId}' — " +
                            $"inferred {inferredState} from trajectory (vessel was alive when unloaded)");
                        PopulateTerminalOrbitFromLastSegment(rec);

                        // Bug #290d: capture terrain height from last trajectory point for
                        // landed/splashed recordings whose vessel was unloaded at scene exit.
                        // Without this, TerrainHeightAtEnd stays NaN and the spawn safety net
                        // uses PQS terrain height, which is below KSP static structures (runway,
                        // launchpad), causing the spawned vessel to clip through and explode.
                        if (inferredState == TerminalState.Landed || inferredState == TerminalState.Splashed)
                        {
                            PopulateTerminalPositionFromLastPoint(rec, inferredState);
                            TryCaptureTerrainHeightFromLastTrajectoryPoint(rec);
                        }
                    }
                }
                else
                {
                    // Not a scene exit — vessel genuinely missing. Mark as destroyed.
                    rec.TerminalStateValue = TerminalState.Destroyed;
                    rec.VesselSnapshot = null;
                    ParsekLog.Warn("Flight", $"FinalizeTreeRecordings: vessel pid={rec.VesselPersistentId} " +
                        $"not found for recording '{rec.RecordingId}' — marking Destroyed");

                    // Recover terminal orbit from last orbit segment if available (#219).
                    // Orbital debris often has orbit segments from BackgroundRecorder sampling
                    // but the vessel is destroyed by finalization time.
                    PopulateTerminalOrbitFromLastSegment(rec);
                }
            }

            if (isLeaf && !preserveRestoredSceneExitTerminalState)
                ApplyDestroyedFallback(vesselDestroyedDuringRecording, rec);

            // #289: Re-snapshot the vessel whenever the recording has reached a stable terminal
            // state (Landed/Splashed/Orbiting) AND the live vessel is still findable. Without
            // this, the snapshot's `sit` field stays stale from recording-start (FLYING/SUB_ORBITAL),
            // and after the OnSave→scene-change→OnLoad round-trip the spawn-at-end safety check
            // blocks vessel materialization at "snapshot situation unsafe (FLYING/SUB_ORBITAL)".
            //
            // Runs OUTSIDE the !TerminalStateValue.HasValue gate above because the user's case
            // is precisely "terminal state was already set (e.g. by ChainSegmentManager during
            // active recording) so the gate above is skipped — but the snapshot is still stale".
            //
            // Reuses finalizeVessel from the lookup above — no double FindVesselByPid.
            if (!(sceneExitLifetimeExtended && sceneExitSuppliedSnapshots)
                && !cacheFinalizationApplied
                && isLeaf
                && rec.TerminalStateValue.HasValue
                && finalizeVesselFound)
            {
                var ts = rec.TerminalStateValue.Value;
                if (GhostPlaybackLogic.IsSpawnableTerminal(ts))
                    vesselAccess.TryRefreshStableTerminalSnapshot(
                        rec,
                        finalizeVessel,
                        isSceneExit,
                        "FinalizeIndividualRecording");
            }

            // Refresh terminal orbit for orbital leaf recordings even if a body was
            // captured earlier. A mid-transition capture can stamp the wrong SOI body,
            // so TerminalOrbit* behaves as a healable cache. Explicit point/surface
            // endpoint data still wins when it already anchors the recording; otherwise
            // we only preserve cached orbit data when the full tuple already matches the
            // endpoint-aligned last orbit segment. (#475/#484)
            if (isLeaf && rec.TerminalStateValue.HasValue
                && UsesTerminalOrbitMetadata(rec.TerminalStateValue.Value))
            {
                bool preserveSceneExitTerminalOrbit =
                    sceneExitLifetimeExtended && sceneExitSuppliedTerminalOrbit;
                bool preserveFinalizationCacheTerminalOrbit =
                    cacheFinalizationApplied && cacheSuppliedTerminalOrbit;
                bool preserveFinalizerSuppliedTerminalOrbit =
                    preserveSceneExitTerminalOrbit
                    || preserveFinalizationCacheTerminalOrbit;
                string bodyBeforeRefresh = rec.TerminalOrbitBody;
                if (!sceneExitLifetimeExtended
                    && !cacheFinalizationApplied
                    && finalizeVesselFound)
                    vesselAccess.CaptureTerminalOrbit(rec, finalizeVessel);
                else if (preserveFinalizerSuppliedTerminalOrbit)
                    ParsekLog.Verbose("Flight",
                        $"FinalizeIndividualRecording: preserving " +
                        $"{(preserveFinalizationCacheTerminalOrbit ? "cache" : "scene-exit")} " +
                        $"terminal orbit for '{rec.RecordingId}' (body={rec.TerminalOrbitBody}, " +
                        $"terminal={rec.TerminalStateValue})");

                if (!string.IsNullOrEmpty(rec.TerminalOrbitBody)
                    && !string.Equals(rec.TerminalOrbitBody, bodyBeforeRefresh, StringComparison.Ordinal))
                {
                    ParsekLog.Info("Flight",
                        $"FinalizeIndividualRecording: refreshed TerminalOrbitBody {bodyBeforeRefresh ?? "(empty)"} " +
                        $"-> {rec.TerminalOrbitBody} for '{rec.RecordingId}' from live vessel " +
                        $"(terminal={rec.TerminalStateValue})");
                }

                // Evaluate the same-UT point-anchor guard after any live-vessel
                // refresh. If CaptureTerminalOrbit just rewrote TerminalOrbitBody,
                // that refreshed body should stay authoritative instead of letting
                // stale point metadata from a prior SOI suppress the finalize path.
                bool handledSameUtPointAnchor = !preserveFinalizerSuppliedTerminalOrbit
                    && TryHandleFinalizeSameUtPointAnchoredTerminalOrbit(rec);
                if (!handledSameUtPointAnchor
                    && !preserveFinalizerSuppliedTerminalOrbit
                    && ShouldPopulateTerminalOrbitFromLastSegment(rec))
                {
                    string bodyBeforeFallback = rec.TerminalOrbitBody;
                    PopulateTerminalOrbitFromLastSegment(rec);
                    if (!string.Equals(rec.TerminalOrbitBody, bodyBeforeFallback, StringComparison.Ordinal))
                    {
                        ParsekLog.Info("Flight",
                            $"FinalizeIndividualRecording: backfilled TerminalOrbitBody={rec.TerminalOrbitBody} " +
                            $"for '{rec.RecordingId}' via orbit-segment fallback " +
                            $"(terminal={rec.TerminalStateValue}, vesselFound={finalizeVesselFound}, " +
                            $"previousBody={bodyBeforeFallback ?? "(empty)"})");
                    }
                }

                if (string.IsNullOrEmpty(rec.TerminalOrbitBody))
                {
                    ParsekLog.Warn("Flight",
                        $"FinalizeIndividualRecording: terminal orbit refresh declined for '{rec.RecordingId}' " +
                        $"(terminal={rec.TerminalStateValue}, vesselFound={finalizeVesselFound}, " +
                        $"orbitSegments={rec.OrbitSegments?.Count ?? 0}) — TerminalOrbitBody remains empty");
                }
            }

            RecordingEndpointResolver.RefreshEndpointDecision(rec, "FinalizeIndividualRecording");
            ApplyFinalEndpointSegmentPhase(
                rec,
                treeContext,
                finalizeVesselFound,
                finalizeVessel);

            // Bug #290d: backfill MaxDistanceFromLaunch if not yet computed.
            // Tree recordings reach finalization via ForceStop which skips BuildCaptureRecording
            // (where MaxDistanceFromLaunch is normally computed). Without this, all recordings
            // have maxDist=0.0 and IsTreeIdleOnPad falsely discards the entire tree.
            if (rec.MaxDistanceFromLaunch <= 0.0 && rec.Points.Count >= 2)
            {
                VesselSpawner.BackfillMaxDistance(rec);
            }

            // Warn if leaf has no playback data
            if (isLeaf && rec.Points.Count == 0 && rec.OrbitSegments.Count == 0 && !rec.SurfacePos.HasValue)
            {
                if (rec.SidecarLoadFailed)
                {
                    ParsekLog.Warn("Flight",
                        $"FinalizeTreeRecordings: leaf '{rec.RecordingId}' has no playback data " +
                        $"because sidecar hydration failed ({rec.SidecarLoadFailureReason ?? "unknown"})");
                }
                else
                {
                    ParsekLog.Warn("Flight", $"FinalizeTreeRecordings: leaf '{rec.RecordingId}' has no playback data");
                }
            }

            string termOrbitInfo = "";
            if ((rec.TerminalStateValue == TerminalState.Orbiting
                    || rec.TerminalStateValue == TerminalState.SubOrbital)
                && !string.IsNullOrEmpty(rec.TerminalOrbitBody)
                && rec.TerminalOrbitSemiMajorAxis > 0.0)
            {
                double termPeriR = rec.TerminalOrbitSemiMajorAxis * (1.0 - rec.TerminalOrbitEccentricity);
                double termApoR = rec.TerminalOrbitSemiMajorAxis * (1.0 + rec.TerminalOrbitEccentricity);
                termOrbitInfo = string.Format(CultureInfo.InvariantCulture,
                    " termOrbit[body={0} epoch={1:F2} sma={2:F1} ecc={3:F4} periR={4:F1} apoR={5:F1} inc={6:F2}]",
                    rec.TerminalOrbitBody,
                    rec.TerminalOrbitEpoch,
                    rec.TerminalOrbitSemiMajorAxis,
                    rec.TerminalOrbitEccentricity,
                    termPeriR,
                    termApoR,
                    rec.TerminalOrbitInclination);
            }
            ParsekLog.Verbose("Flight",
                $"FinalizeTreeRecordings: rec='{rec.RecordingId}' vessel='{rec.VesselName}' " +
                $"points={rec.Points.Count} orbitSegs={rec.OrbitSegments.Count} " +
                $"terminal={rec.TerminalStateValue?.ToString() ?? "none"} " +
                $"maxDist={rec.MaxDistanceFromLaunch:F0}m " +
                $"snapshot={rec.VesselSnapshot != null} leaf={isLeaf}{termOrbitInfo}");
            return sceneExitLifetimeExtended && sceneExitSuppliedSnapshots;
        }
    }
}
