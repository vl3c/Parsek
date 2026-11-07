using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Relative-frame anchoring: anchor detection and candidates, the parent-anchor contract, relative offsets and anchor pose resolution.
    internal partial class BackgroundRecorder
    {
        private void UpdateBackgroundAnchorDetection(
            BackgroundVesselState state,
            Vessel bgVessel,
            Recording treeRec,
            double ut)
        {
            if (state == null || bgVessel == null || treeRec == null)
                return;

            // When the recording carries the debris parent-anchor contract,
            // bypass the generic candidate-list / nearest-search entirely and
            // pin the anchor to the parent recording. The recorder still decides
            // Absolute-vs-Relative sections by parent proximity; the parent id is
            // ownership, not a generic live-anchor candidate.
            if (!string.IsNullOrEmpty(treeRec.ParentAnchorRecordingId))
            {
                ApplyParentAnchorContractToState(state, treeRec, bgVessel, ut);
                return;
            }

            bool onSurface = EnvironmentDetector.IsSurfaceForAnchorDetection(
                state.environmentHysteresis?.CurrentEnvironment,
                (int)bgVessel.situation);
            if (onSurface)
            {
                if (state.isRelativeMode)
                    ExitBackgroundRelativeMode(state, bgVessel, ut, "surface");
                return;
            }

            var candidates = BuildBackgroundRecordingAnchorCandidates(
                state,
                bgVessel,
                treeRec,
                ut,
                out int liveScanned,
                out int liveAdded,
                out int ghostScanned,
                out int ghostAdded);

            // Narrowed-gate filter for re-fly provisionals: mirror of the
            // FlightRecorder.UpdateAnchorDetection filter. Drop same-tree
            // candidates so the nearest-search picks only out-of-tree real
            // anchors.
            //
            // We use the pure overload with treeRec.RecordingId explicitly
            // (not the production overload's derived activeTree.ActiveRecordingId)
            // because tree.ActiveRecordingId is the FG-focused recording, and
            // BG recorders run only on unfocused vessels. Using the production
            // overload here would widen the scope to "any BG vessel in the
            // re-fly tree". Passing treeRec.RecordingId explicitly keeps the
            // scope at "this BG vessel IS the re-fly provisional": the filter
            // fires only if marker.ActiveReFlyRecordingId == treeRec.RecordingId.
            // See docs/dev/plans/narrow-refly-relative-gate.md.
            ReFlySessionMarker marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            ICollection<string> sameTreeRecordingIds = tree?.Recordings?.Keys;
            IReadOnlyList<RecordingAnchorCandidate> nearestSearchCandidates =
                ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(
                    marker,
                    treeRec.RecordingId,
                    sameTreeRecordingIds,
                    candidates);

            var result = AnchorDetector.FindNearestRecordingAnchor(
                treeRec.RecordingId,
                state.vesselPid,
                bgVessel.GetWorldPos3D(),
                nearestSearchCandidates,
                AnchorDetector.RelativeFrameRangeLimit(state.isRelativeMode));
            bool shouldBeRelative = result.found
                && AnchorDetector.ShouldUseRelativeFrame(result.distance, state.isRelativeMode);

            if (shouldBeRelative)
            {
                bool anchorChanged = !state.isRelativeMode
                    || !string.Equals(
                        state.currentAnchorRecordingId,
                        result.candidate.RecordingId,
                        StringComparison.Ordinal);
                if (anchorChanged)
                {
                    string previousAnchorRecordingId = state.currentAnchorRecordingId;
                    uint previousDiagnosticPid = state.currentAnchorCandidate.DiagnosticPid;
                    SetBackgroundCurrentAnchor(state, result.candidate);
                    CloseBackgroundTrackSection(state, ut);
                    SegmentEnvironment env = state.environmentHysteresis != null
                        ? state.environmentHysteresis.CurrentEnvironment
                        : SegmentEnvironment.Atmospheric;
                    StartBackgroundTrackSection(state, env, ReferenceFrame.Relative, ut);
                    ApplyBackgroundCurrentAnchorToTrackSection(state);
                    ActivateBackgroundHighFidelitySampling(state, ut, "relative-enter");
                    if (!SeedBackgroundRelativeBoundaryPoint(state, bgVessel, result.candidate, ut))
                    {
                        ExitBackgroundRelativeMode(state, bgVessel, ut, "relative-boundary-seed-failed");
                        return;
                    }

                    var ic = CultureInfo.InvariantCulture;
                    string transition = previousAnchorRecordingId == null ? "entered" : "switched";
                    ParsekLog.Info("BgRecorder",
                        $"RELATIVE mode {transition}: pid={state.vesselPid} " +
                        $"recordingId={treeRec.RecordingId} anchorRecordingId={state.currentAnchorRecordingId} " +
                        $"source={result.candidate.Source} diagnosticPid={result.candidate.DiagnosticPid} " +
                        $"previousAnchorRecordingId={previousAnchorRecordingId ?? "(none)"} " +
                        $"previousDiagnosticPid={previousDiagnosticPid} " +
                        $"dist={result.distance.ToString("F1", ic)}m " +
                        $"sealed={result.candidate.IsSealed} " +
                        $"affinity={AnchorDetector.RecordingAnchorAffinityRank(result.candidate)} " +
                        $"liveCandidates={liveAdded}/{liveScanned} ghostCandidates={ghostAdded}/{ghostScanned}");
                }
                else
                {
                    SetBackgroundCurrentAnchor(state, result.candidate);
                    ApplyBackgroundCurrentAnchorToTrackSection(state);
                }
            }
            else if (state.isRelativeMode)
            {
                if (!result.found)
                {
                    ParsekLog.Info("BgRecorder",
                        $"RELATIVE exit: pid={state.vesselPid} no eligible recording anchor candidates " +
                        $"liveCandidates={liveAdded}/{liveScanned} ghostCandidates={ghostAdded}/{ghostScanned}");
                }
                ExitBackgroundRelativeMode(state, bgVessel, ut, "distance-or-missing-anchor");
            }
        }

        private IReadOnlyList<RecordingAnchorCandidate> BuildBackgroundRecordingAnchorCandidates(
            BackgroundVesselState state,
            Vessel bgVessel,
            Recording focusRecording,
            double ut,
            out int liveScanned,
            out int liveAdded,
            out int ghostScanned,
            out int ghostAdded)
        {
            backgroundAnchorCandidateBuffer.Clear();
            liveScanned = liveAdded = ghostScanned = ghostAdded = 0;
            if (tree?.Recordings == null || focusRecording == null)
                return backgroundAnchorCandidateBuffer;

            AddBackgroundLiveAnchorCandidates(
                state,
                bgVessel,
                focusRecording,
                out liveScanned,
                out liveAdded);
            AddBackgroundGhostAnchorCandidates(
                state,
                focusRecording,
                ut,
                out ghostScanned,
                out ghostAdded);

            ParsekLog.VerboseRateLimited("BgRecorder", "bg-recording-anchor-candidates",
                $"Background recording anchor candidates: pid={state.vesselPid} " +
                $"recordingId={focusRecording.RecordingId} live={liveAdded}/{liveScanned} " +
                $"ghost={ghostAdded}/{ghostScanned} total={backgroundAnchorCandidateBuffer.Count}",
                5.0);

            return backgroundAnchorCandidateBuffer;
        }

        private void AddBackgroundLiveAnchorCandidates(
            BackgroundVesselState state,
            Vessel bgVessel,
            Recording focusRecording,
            out int scanned,
            out int added)
        {
            scanned = added = 0;
            List<Vessel> vessels;
            try { vessels = FlightGlobals.Vessels; }
            catch (TypeInitializationException) { vessels = null; }
            if (vessels == null)
                return;

            // Resolve the active recording's vessel pid so a still-backgrounded
            // recording can anchor to the recording the player just switched to
            // (e.g. the docking target). The active recording lives outside
            // BackgroundMap, so without this it is invisible to the candidate
            // scan and the RELATIVE anchor silently drops to ABSOLUTE mid-
            // approach. DAG-order eligibility still decides whether the anchor
            // is actually allowed.
            string activeRecordingId = tree.ActiveRecordingId;
            uint activeRecordingVesselPid =
                !string.IsNullOrWhiteSpace(activeRecordingId)
                && tree.Recordings != null
                && tree.Recordings.TryGetValue(activeRecordingId, out Recording activeRec)
                && activeRec != null
                    ? activeRec.VesselPersistentId
                    : 0u;

            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel vessel = vessels[i];
                if (vessel == null || !vessel.loaded)
                    continue;
                if (vessel.persistentId == state.vesselPid)
                    continue;
                if (GhostMapPresence.IsGhostMapVessel(vessel.persistentId))
                    continue;
                if (vessel.persistentId == (bgVessel?.persistentId ?? 0u))
                    continue;

                scanned++;
                if (!AnchorDetector.TryResolveLoadedAnchorRecordingId(
                        vessel.persistentId,
                        tree.BackgroundMap,
                        activeRecordingId,
                        activeRecordingVesselPid,
                        out string recordingId,
                        out _))
                {
                    continue;
                }
                if (!TryGetBackgroundEligibleAnchorRecording(
                        recordingId,
                        focusRecording,
                        AnchorCandidateSource.Live,
                        vessel.persistentId,
                        out Recording candidateRecording))
                {
                    continue;
                }
                // Launch-identity gate, mirroring the active-recorder live-anchor scan
                // (FlightRecorder.TryResolveLivePeerRecordingId): the pid that resolved this
                // recording is craft-baked, so a preserved DIFFERENT launch of the same craft
                // would supply the anchor pose and corrupt this recording's Relative frames.
                // Rejects only a conclusive launch-Guid mismatch (unknown Guid = prior behaviour).
                if (!AnchorDetector.LiveAnchorLaunchMatches(
                        candidateRecording, AnchorDetector.TryReadLiveVesselGuid(vessel)))
                {
                    ParsekLog.VerboseRateLimited("BgRecorder", "bg-anchor-launch-mismatch",
                        $"Background anchor candidate rejected: recordingId={recordingId} " +
                        $"reason=live-vessel-different-launch peerPid={vessel.persistentId}",
                        5.0);
                    continue;
                }

                // Use vesselTransform.position to stay symmetric with
                // vessel.transform.rotation -- mixing GetWorldPos3D (CoM) with
                // vesselTransform rotation systematically shifts the recorded
                // offset by the CoM-to-vesselTransform vector (the v0.9 radial-
                // booster ~10 m forward bug). Falls back to CoM only when the
                // Unity transform is unavailable (mid-teardown fake-null).
                Transform candidateTransform = vessel.transform;
                Vector3d candidatePos = candidateTransform != null
                    ? (Vector3d)candidateTransform.position
                    : vessel.GetWorldPos3D();
                Quaternion candidateRot = candidateTransform != null
                    ? candidateTransform.rotation
                    : Quaternion.identity;
                if (AnchorDetector.TryCreateRecordingAnchorCandidate(
                        focusRecording,
                        candidateRecording,
                        candidatePos,
                        candidateRot,
                        AnchorCandidateSource.Live,
                        vessel.persistentId,
                        -1,
                        out RecordingAnchorCandidate candidate))
                {
                    backgroundAnchorCandidateBuffer.Add(candidate);
                    added++;
                }
            }
        }

        private void AddBackgroundGhostAnchorCandidates(
            BackgroundVesselState state,
            Recording focusRecording,
            double ut,
            out int scanned,
            out int added)
        {
            scanned = added = 0;
            if (ParsekFlight.Instance == null)
                return;

            IEnumerable<RecordingAnchorCandidate> candidates =
                ParsekFlight.Instance.BuildGhostRecordingAnchorCandidatesForRecorder(
                    tree,
                    focusRecording.RecordingId,
                    state.vesselPid,
                    ut);
            if (candidates == null)
                return;

            foreach (RecordingAnchorCandidate provided in candidates)
            {
                scanned++;
                if (!TryGetBackgroundEligibleAnchorRecording(
                        provided.RecordingId,
                        focusRecording,
                        provided.Source,
                        provided.DiagnosticPid,
                        out Recording candidateRecording))
                {
                    continue;
                }

                if (AnchorDetector.TryCreateRecordingAnchorCandidate(
                        focusRecording,
                        candidateRecording,
                        provided.WorldPos,
                        provided.WorldRotation,
                        provided.Source,
                        provided.DiagnosticPid,
                        provided.GhostIndex,
                        out RecordingAnchorCandidate candidate))
                {
                    backgroundAnchorCandidateBuffer.Add(candidate);
                    added++;
                }
            }
        }

        private bool TryGetBackgroundEligibleAnchorRecording(
            string recordingId,
            Recording focusRecording,
            AnchorCandidateSource source,
            uint diagnosticPid,
            out Recording candidateRecording)
        {
            candidateRecording = null;
            if (tree?.Recordings == null || string.IsNullOrWhiteSpace(recordingId))
                return false;
            // NOTE: the active recording is intentionally NOT blanket-excluded
            // here. A still-backgrounded recording must be able to anchor to the
            // recording the player just switched to (the docking target the
            // player is now flying) to keep RELATIVE engaged through the close
            // approach. Cycle safety is provided by IsRecordingAnchorDAGOrderEligible
            // (candidate.TreeOrder < focus.TreeOrder) below: the active recording
            // can only anchor a higher-TreeOrder background recording, never the
            // reverse, so no A<->B anchor cycle can form. The re-fly provisional
            // (still-being-appended fork) is still excluded by the marker gate.
            ReFlySessionMarker marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            if (marker != null
                && !string.IsNullOrEmpty(marker.ActiveReFlyRecordingId)
                && string.Equals(marker.ActiveReFlyRecordingId, recordingId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!tree.Recordings.TryGetValue(recordingId, out candidateRecording))
                return false;

            if (!AnchorDetector.IsRecordingAnchorEligible(focusRecording, candidateRecording))
                return false;
            if (!AnchorDetector.IsRecordingAnchorDAGOrderEligible(focusRecording, candidateRecording))
            {
                LogBackgroundRecordingAnchorDagOrderSkip(focusRecording, candidateRecording, source, diagnosticPid);
                return false;
            }

            return true;
        }

        private static void LogBackgroundRecordingAnchorDagOrderSkip(
            Recording focusRecording,
            Recording candidateRecording,
            AnchorCandidateSource source,
            uint diagnosticPid)
        {
            string focusId = focusRecording?.RecordingId ?? "(none)";
            string candidateId = candidateRecording?.RecordingId ?? "(none)";
            ParsekLog.VerboseRateLimited("BgRecorder",
                "bg-recording-anchor-dag-order-skip|" + focusId + "|" + candidateId + "|" + source,
                $"Background recording anchor candidate skipped: reason=dag-order focusRecordingId={focusId} " +
                $"candidateRecordingId={candidateId} focusTreeOrder={focusRecording?.TreeOrder ?? -1} " +
                $"candidateTreeOrder={candidateRecording?.TreeOrder ?? -1} source={source} diagnosticPid={diagnosticPid}",
                5.0);
        }

        private static void SetBackgroundCurrentAnchor(
            BackgroundVesselState state,
            RecordingAnchorCandidate candidate)
        {
            state.isRelativeMode = true;
            state.currentAnchorRecordingId = candidate.RecordingId;
            state.currentAnchorCandidate = candidate;
            state.hasCurrentAnchorCandidate = true;
        }

        /// <summary>
        /// PR 3b (Decision §5 + plan §3b §"Per-frame anchor write" / §"Structural-event
        /// seam"): pins <paramref name="state"/>'s anchor to the parent recording id
        /// carried by the <see cref="Recording.ParentAnchorRecordingId"/> contract.
        /// Skips the candidate-list / nearest-search and lets
        /// <see cref="ApplyBackgroundCurrentAnchorToTrackSection"/> propagate the anchor
        /// id into <see cref="TrackSection.anchorRecordingId"/>. Builds a Live anchor
        /// candidate from the parent vessel when available; falls back to a record-only
        /// candidate that resolves through the recorded-anchor pose path.
        /// </summary>
        private void ApplyParentAnchorContractToState(
            BackgroundVesselState state,
            Recording treeRec,
            Vessel bgVessel,
            double ut)
        {
            if (state == null || treeRec == null) return;
            string parentRecId = treeRec.ParentAnchorRecordingId;
            if (string.IsNullOrEmpty(parentRecId)) return;

            if (!ShouldUseDebrisRelativeSection(state))
            {
                if (state.isRelativeMode
                    || (state.trackSectionActive
                        && state.currentTrackSection.referenceFrame == ReferenceFrame.Relative))
                {
                    ExitBackgroundRelativeMode(
                        state,
                        bgVessel,
                        ut,
                        "debris-parent-proximity-exit");
                }
                else
                {
                    ClearBackgroundCurrentAnchor(state);
                }
                return;
            }

            string previousAnchorRecordingId = state.currentAnchorRecordingId;
            bool wasRelative = state.isRelativeMode;
            var marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            bool markerActive = marker != null && !string.IsNullOrEmpty(marker.ActiveReFlyRecordingId);
            bool mutableActiveReFlyAnchor = IsActiveReFlyParentMatch(marker, parentRecId);

            // Resolve the parent vessel by its persistent id so we can author a Live
            // anchor candidate when the parent is loaded. The recorded-anchor pose
            // path is a graceful fallback when the parent vessel is gone (the
            // CheckDebrisTTL hook ends the recording shortly after this).
            Recording parentRec = null;
            tree?.Recordings?.TryGetValue(parentRecId, out parentRec);
            Vessel parentVessel = parentRec != null
                ? FlightRecorder.FindLaunchMatchedVesselForRecording(parentRec)
                : null;

            if (parentVessel != null && parentVessel.loaded)
            {
                // Use vesselTransform.position (NOT GetWorldPos3D / CoM) to stay
                // symmetric with parentVessel.transform.rotation and with the
                // recorder's seed-time anchor capture (which writes
                // v.latitude/longitude/altitude derived from vesselTransform via
                // KSP's UpdatePosVel). Mixing CoM position with vesselTransform
                // rotation was the v0.9 radial-booster ~10 m forward offset bug
                // -- body-fixed primary masks the inconsistency for normal
                // rendering, but the live candidate also leaks into diagnostics,
                // fallback paths, and future anchor-based render logic. Falls
                // back to CoM when transform is null (mid-teardown fake-null).
                Transform parentTransform = parentVessel.transform;
                Vector3d parentAnchorPos = parentTransform != null
                    ? (Vector3d)parentTransform.position
                    : parentVessel.GetWorldPos3D();
                Quaternion parentAnchorRot = parentTransform != null
                    ? parentTransform.rotation
                    : Quaternion.identity;
                var liveCandidate = new RecordingAnchorCandidate(
                    parentRecId,
                    parentAnchorPos,
                    parentAnchorRot,
                    AnchorCandidateSource.Live,
                    diagnosticPid: parentVessel.persistentId,
                    ghostIndex: -1,
                    isSealed: false,
                    isSameReplayPoint: false,
                    isSameVesselLineage: false);
                SetBackgroundCurrentAnchor(state, liveCandidate);
            }
            else
            {
                // Parent not in scene as a live vessel — pin the recording id; pose
                // resolution falls through to TryResolveBackgroundRecordedAnchorPose
                // (recorded-data path) which the resolver chain handles.
                state.isRelativeMode = true;
                state.currentAnchorRecordingId = parentRecId;
                state.currentAnchorCandidate = default;
                state.hasCurrentAnchorCandidate = false;
            }

            // If we just transitioned into the relative surface or swapped anchors, close
            // the Absolute section we opened (or re-open) and start a Relative one.
            // The contract is proximity-scoped: parent-local Relative sections are
            // only opened while the parent remains in the debris relative range.
            bool needsSectionFlip = state.trackSectionActive
                && state.currentTrackSection.referenceFrame != ReferenceFrame.Relative;
            bool anchorChanged = !wasRelative
                || !string.Equals(previousAnchorRecordingId, parentRecId, StringComparison.Ordinal);
            if (needsSectionFlip || anchorChanged)
            {
                SegmentEnvironment env = state.environmentHysteresis != null
                    ? state.environmentHysteresis.CurrentEnvironment
                    : SegmentEnvironment.Atmospheric;
                StartDebrisParentRelativeTrackSection(state, env, ut);
                if (anchorChanged)
                {
                    string transition = previousAnchorRecordingId == null ? "entered" : "switched";
                    ParsekLog.Info("BgRecorder",
                        $"RELATIVE mode {transition} (debris parent-anchor contract): pid={state.vesselPid} " +
                        $"recordingId={treeRec.RecordingId} parentRecId={parentRecId} " +
                        $"previousAnchorRecordingId={previousAnchorRecordingId ?? "(none)"} " +
                        $"parentVesselLoaded={parentVessel != null && parentVessel.loaded} " +
                        $"mutableActiveReFlyAnchor={(mutableActiveReFlyAnchor ? "true" : "false")} " +
                        $"markerActive={(markerActive ? "true" : "false")} " +
                        $"markerActiveReFlyId={marker?.ActiveReFlyRecordingId ?? "(none)"} " +
                        $"ut={ut.ToString("F2", CultureInfo.InvariantCulture)}");
                }
            }
            else
            {
                ApplyBackgroundCurrentAnchorToTrackSection(state);
            }
        }

        private void StartDebrisParentRelativeTrackSection(
            BackgroundVesselState state,
            SegmentEnvironment env,
            double ut)
        {
            if (state.trackSectionActive)
                CloseBackgroundTrackSection(state, ut);
            StartBackgroundTrackSection(state, env, ReferenceFrame.Relative, ut);
            ApplyBackgroundCurrentAnchorToTrackSection(state);
            ForceNextBackgroundTrajectorySample(
                state,
                ut,
                "debris-parent-relative-enter");
        }

        private static void ForceNextBackgroundTrajectorySample(
            BackgroundVesselState state,
            double ut,
            string reason)
        {
            if (state == null)
                return;

            // TrajectoryMath.ShouldRecordPoint treats negative lastRecordedUT as
            // the first-sample case, bypassing min/max interval gates.
            state.lastRecordedUT = -1.0;
            state.hasLastWorldRotation = false;
            ActivateBackgroundHighFidelitySampling(
                state,
                ut,
                reason ?? "force-immediate-sample");
            ParsekLog.Verbose("BgRecorder",
                $"Immediate background trajectory sample forced: pid={state.vesselPid} " +
                $"reason={reason ?? "unknown"} ut={ut.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        private static void ClearBackgroundCurrentAnchor(BackgroundVesselState state)
        {
            state.isRelativeMode = false;
            state.currentAnchorRecordingId = null;
            state.currentAnchorCandidate = default;
            state.hasCurrentAnchorCandidate = false;
        }

        private static void ApplyBackgroundCurrentAnchorToTrackSection(BackgroundVesselState state)
        {
            if (state == null
                || !state.trackSectionActive
                || state.currentTrackSection.referenceFrame != ReferenceFrame.Relative)
            {
                return;
            }

            state.currentTrackSection.anchorRecordingId = state.currentAnchorRecordingId;
            state.currentTrackSection.anchorVesselId = 0u;
        }

        private bool ApplyBackgroundRelativeOffset(
            BackgroundVesselState state,
            ref TrajectoryPoint point,
            Vessel vessel,
            double ut)
        {
            if (state == null) return false;

            // PR 3b §"Structural-event seam": the structural-event snapshot path
            // bypasses UpdateBackgroundAnchorDetection, so the debris contract must
            // be enforced here too. For parent-anchored debris, pin state to the parent
            // recording id before any pose resolution. Re-runs of the periodic path
            // hit the same helper but it's idempotent when the contract is already
            // applied (anchorChanged=false → no log spam, no section flip).
            Recording treeRec = null;
            if (!string.IsNullOrEmpty(state.recordingId))
                tree?.Recordings?.TryGetValue(state.recordingId, out treeRec);
            if (treeRec != null && !string.IsNullOrEmpty(treeRec.ParentAnchorRecordingId))
            {
                UpdateDebrisProximityState(state, treeRec, vessel);
                ApplyParentAnchorContractToState(state, treeRec, vessel, ut);
            }

            if (!state.isRelativeMode)
                return false;
            if (string.IsNullOrWhiteSpace(state.currentAnchorRecordingId))
            {
                ForceBackgroundRelativeToAbsolute(state, ut, "anchor-recording-id-missing");
                return false;
            }

            if (!TryResolveBackgroundCurrentAnchorPose(
                    state,
                    ut,
                    out AnchorPose anchorPose,
                    out RelativeAnchorResolveFailure failure,
                    out string anchorPoseSourceLabel))
            {
                string reason = RelativeAnchorResolveFailure.ReasonOrFallback(
                    failure,
                    "recorded-anchor-unresolved");
                ParsekLog.Warn("BgRecorder",
                    $"RELATIVE sample unresolved: pid={state.vesselPid} " +
                    $"anchorRecordingId={state.currentAnchorRecordingId} reason={reason} " +
                    $"ut={ut.ToString("F2", CultureInfo.InvariantCulture)} — forcing ABSOLUTE");
                ForceBackgroundRelativeToAbsolute(state, ut, reason);
                return false;
            }

            return ApplyBackgroundRelativeOffsetForAnchorPose(
                state,
                ref point,
                vessel,
                anchorPose,
                state.currentAnchorRecordingId,
                state.currentAnchorCandidate.Source,
                state.currentAnchorCandidate.DiagnosticPid,
                logSample: true,
                sourceLabel: anchorPoseSourceLabel);
        }

        private bool SeedBackgroundRelativeBoundaryPoint(
            BackgroundVesselState state,
            Vessel vessel,
            RecordingAnchorCandidate candidate,
            double ut)
        {
            if (!TryResolveBackgroundAnchorPoseForCandidate(
                    candidate,
                    ut,
                    out AnchorPose anchorPose,
                    out RelativeAnchorResolveFailure failure))
            {
                string reason = RelativeAnchorResolveFailure.ReasonOrFallback(
                    failure,
                    "recorded-anchor-unresolved");
                ParsekLog.Warn("BgRecorder",
                    $"RELATIVE boundary seed skipped: pid={state.vesselPid} " +
                    $"anchorRecordingId={candidate.RecordingId} reason={reason}");
                return false;
            }

            TrajectoryPoint absolutePoint = CreateAbsoluteTrajectoryPointFromVessel(vessel, ut);
            TrajectoryPoint relativePoint = absolutePoint;
            if (!ApplyBackgroundRelativeOffsetForAnchorPose(
                    state,
                    ref relativePoint,
                    vessel,
                    anchorPose,
                    candidate.RecordingId,
                    candidate.Source,
                    candidate.DiagnosticPid,
                    logSample: false))
            {
                return false;
            }

            // Apply v9 surface clearance to both stores. The in-frames
            // relativePoint goes into a Relative section so the helper's
            // frame gate correctly skips it (anchor-local metres in
            // lat/lon/alt would corrupt terrain math), but the body-fixed
            // shadow carries genuine lat/lon/alt and the body-fixed
            // primary playback path applies terrain correction via
            // recordedGroundClearance. Mirrors the periodic-sample,
            // structural-event, and debris-seed wirings.
            ApplySurfaceClearanceToPoint(state, vessel, ref relativePoint);
            ApplySurfaceClearanceToBodyFixedShadow(state, vessel, ref absolutePoint);

            AddFrameToActiveTrackSection(state, relativePoint, absolutePoint);
            ParsekLog.Verbose("BgRecorder",
                $"RELATIVE boundary seeded: pid={state.vesselPid} " +
                $"anchorRecordingId={candidate.RecordingId} source={candidate.Source} " +
                $"diagnosticPid={candidate.DiagnosticPid} ut={ut.ToString("F2", CultureInfo.InvariantCulture)}");
            return true;
        }

        private bool ApplyBackgroundRelativeOffsetForAnchorPose(
            BackgroundVesselState state,
            ref TrajectoryPoint point,
            Vessel vessel,
            AnchorPose anchorPose,
            string anchorRecordingId,
            AnchorCandidateSource source,
            uint diagnosticPid,
            bool logSample,
            string sourceLabel = null)
        {
            if (state == null || vessel == null || string.IsNullOrWhiteSpace(anchorRecordingId))
                return false;

            int recordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            if (!FlightRecorder.TryResolveAbsolutePointWorldForRelativeOffset(
                    point,
                    vessel,
                    out Vector3d focusWorldPos))
            {
                // Fall back to vesselTransform.position (NOT GetWorldPos3D / CoM)
                // to stay symmetric with the rotation fallback below at
                // vessel.transform.rotation. Both pieces must come from the same
                // frame or the recorded offset is shifted by the CoM-to-
                // vesselTransform vector. CoM remains as the last-resort fallback
                // if the Unity transform is mid-teardown (fake-null).
                Transform focusTransform = vessel.transform;
                focusWorldPos = focusTransform != null
                    ? (Vector3d)focusTransform.position
                    : vessel.GetWorldPos3D();
            }
            Vector3d offset = TrajectoryMath.ComputeRelativeLocalOffset(
                focusWorldPos,
                anchorPose.WorldPos,
                anchorPose.WorldRotation);
            point.latitude = offset.x;
            point.longitude = offset.y;
            point.altitude = offset.z;
            Quaternion focusWorldRotation;
            if (!FlightRecorder.TryResolveAbsolutePointWorldRotationForRelativeOffset(
                    point,
                    vessel,
                    out focusWorldRotation))
            {
                focusWorldRotation = vessel.transform.rotation;
            }
            point.rotation = TrajectoryMath.ComputeRelativeLocalRotation(
                focusWorldRotation,
                anchorPose.WorldRotation);

            ApplyBackgroundCurrentAnchorToTrackSection(state);

            // ---- Trace-Sep: log both parent-vs-debris distance measurements ----
            // recorded-relative = |offset|, the magnitude of the anchor-local
            // Cartesian offset just written into frames[].latitude/longitude/altitude.
            // recorded-absolute = |focusWorldPos - anchorPose.WorldPos|, the
            // ground-truth world-space distance between the live debris and
            // the live anchor at this same instant. These two MUST match
            // exactly (the relative offset only rotates the world vector into
            // the anchor's frame, magnitude is preserved). A mismatch here is
            // a smoking gun for ComputeRelativeLocalOffset doing more than
            // rotation (scale, axis-swap, mis-applied anchor rotation, etc.)
            // and would mean the recorded anchor-local distance does not
            // correspond to the real ground-truth distance.
            if (TraceSeparation.RecordingWindowActive || TraceSeparation.PlaybackWindowActive)
            {
                Vector3d worldDelta = focusWorldPos - anchorPose.WorldPos;
                double recordedRelativeDist = offset.magnitude;
                double recordedAbsoluteDist = worldDelta.magnitude;
                TraceSeparation.RecordLog("BG_ApplyRel",
                    "vesselPid=" + state.vesselPid +
                    " anchorRecId=" + (anchorRecordingId ?? "<null>") +
                    " ut=" + point.ut.ToString("R", CultureInfo.InvariantCulture) +
                    " focusWorldPos=" + TraceSeparation.FormatVector3d(focusWorldPos) +
                    " anchorWorldPos=" + TraceSeparation.FormatVector3d(anchorPose.WorldPos) +
                    " worldDelta=" + TraceSeparation.FormatVector3d(worldDelta) +
                    " offset=" + TraceSeparation.FormatVector3d(offset) +
                    " recordedRelativeDist=" + recordedRelativeDist.ToString("F3", CultureInfo.InvariantCulture) +
                    " recordedAbsoluteDist=" + recordedAbsoluteDist.ToString("F3", CultureInfo.InvariantCulture) +
                    " distMismatch=" + System.Math.Abs(recordedRelativeDist - recordedAbsoluteDist).ToString("F3", CultureInfo.InvariantCulture));
            }
            // ---- /Trace-Sep ----

            if (logSample)
            {
                double sampleUT = point.ut;
                ParsekLog.VerboseRateLimited("BgRecorder",
                    "bg-relative-offset|" + state.vesselPid,
                    () =>
                    {
                        string effectiveSource = sourceLabel
                            ?? (state.hasCurrentAnchorCandidate ? source.ToString() : "recorded");
                        string frameContract = ClassifyBackgroundRelativeFrameContract(
                            anchorRecordingId,
                            effectiveSource,
                            source,
                            state.hasCurrentAnchorCandidate,
                            HasActiveReFlyPreReFlyAnchorSnapshot(anchorRecordingId));
                        string parentDrift = FormatActiveReFlyParentDriftFromRecorded(
                            anchorRecordingId,
                            diagnosticPid,
                            sampleUT);
                        string parentDriftField = FormatParentDriftLogField(parentDrift);

                        return $"RELATIVE sample: pid={state.vesselPid} " +
                               $"contract=anchor-local " +
                               $"version={recordingFormatVersion} dx={offset.x:F2} dy={offset.y:F2} dz={offset.z:F2} " +
                               $"anchorRecordingId={anchorRecordingId} source={effectiveSource} " +
                               $"frameContract={frameContract} " +
                               parentDriftField +
                               $"diagnosticPid={diagnosticPid} " +
                               $"|offset|={offset.magnitude:F2}m";
                    },
                    2.0);
            }

            return true;
        }

        private bool TryResolveBackgroundCurrentAnchorPose(
            BackgroundVesselState state,
            double ut,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure,
            out string sourceLabel)
        {
            sourceLabel = null;
            if (state.hasCurrentAnchorCandidate
                && string.Equals(
                    state.currentAnchorCandidate.RecordingId,
                    state.currentAnchorRecordingId,
                    StringComparison.Ordinal))
            {
                return TryResolveBackgroundAnchorPoseForCandidate(
                    state.currentAnchorCandidate,
                    ut,
                    out pose,
                    out failure,
                    out sourceLabel);
            }

            bool resolved = TryResolveBackgroundRecordedAnchorPose(
                state.currentAnchorRecordingId,
                ut,
                out pose,
                out failure);
            if (resolved)
                sourceLabel = "recorded";
            return resolved;
        }

        private bool TryResolveBackgroundAnchorPoseForCandidate(
            RecordingAnchorCandidate candidate,
            double ut,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            return TryResolveBackgroundAnchorPoseForCandidate(
                candidate,
                ut,
                out pose,
                out failure,
                out _);
        }

        private bool TryResolveBackgroundAnchorPoseForCandidate(
            RecordingAnchorCandidate candidate,
            double ut,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure,
            out string sourceLabel)
        {
            pose = default;
            failure = default;
            sourceLabel = null;
            if (string.IsNullOrWhiteSpace(candidate.RecordingId))
            {
                failure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.PreconditionFailed,
                    "anchor-recording-id-missing",
                    null,
                    candidate.RecordingId,
                    ut);
                return false;
            }

            if (candidate.Source == AnchorCandidateSource.Live && candidate.DiagnosticPid != 0u)
            {
                Vessel liveAnchor = FlightRecorder.FindVesselByPid(candidate.DiagnosticPid);
                if (liveAnchor != null && liveAnchor.loaded)
                {
                    // Match the recorded surface-pose frame: KSP's UpdatePosVel writes
                    // Vessel.latitude/longitude/altitude from vesselTransform.position
                    // and srfRelRotation from vesselTransform.rotation, so playback
                    // (body.GetWorldSurfacePosition of the recorded lla) reconstructs
                    // a vesselTransform-aligned anchor world position. The previous
                    // GetWorldPos3D (CoM) source baked the parent's CoM-to-vesselTransform
                    // vector into every relative ordinary offset. Use vessel.transform
                    // explicitly, not GetTransform() — the latter follows
                    // ReferenceTransform ("Control From Here") which is not the surface
                    // pose contract.
                    pose = new AnchorPose(
                        (Vector3d)liveAnchor.transform.position,
                        liveAnchor.transform.rotation,
                        -1,
                        candidate.RecordingId);
                    sourceLabel = "live";
                    return true;
                }

                ParsekLog.WarnRateLimited("BgRecorder",
                    "bg-live-recording-anchor-missing|" + candidate.RecordingId,
                    $"Live background anchor candidate missing; falling back to recorded pose " +
                    $"anchorRecordingId={candidate.RecordingId} diagnosticPid={candidate.DiagnosticPid} " +
                    $"ut={ut.ToString("F2", CultureInfo.InvariantCulture)}",
                    5.0);
            }

            bool resolved = TryResolveBackgroundRecordedAnchorPose(candidate.RecordingId, ut, out pose, out failure);
            if (resolved)
                sourceLabel = "recorded";
            return resolved;
        }

        private bool TryResolveBackgroundRecordedAnchorPose(
            string anchorRecordingId,
            double ut,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            failure = default;
            if (string.IsNullOrWhiteSpace(anchorRecordingId))
            {
                pose = default;
                failure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.PreconditionFailed,
                    "anchor-recording-id-missing",
                    null,
                    anchorRecordingId,
                    ut);
                return false;
            }

            var context = new RelativeAnchorResolverContext(
                tree,
                focusRecordingId: null,
                focusTreeId: tree?.Id,
                activeReFlyMarker: ParsekScenario.Instance?.ActiveReFlySessionMarker,
                pendingTree: PendingTreeSlot.HasPendingTree ? PendingTreeSlot.PendingTree : null,
                absoluteWorldPositionResolver: ResolveBackgroundPointWorldPosition,
                bodyWorldRotationResolver: ResolveBackgroundBodyWorldRotation,
                orbitalCheckpointPoseResolver: TryResolveBackgroundOrbitalAnchorPose);

            HashSet<string> visited = backgroundAnchorVisitedRecordingIds;
            visited.Clear();
            bool resolved;
            try
            {
                resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                    context,
                    anchorRecordingId,
                    ut,
                    visited,
                    out pose,
                    out failure);
            }
            finally
            {
                visited.Clear();
            }

            if (!resolved && !failure.HasFailure)
            {
                failure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.Other,
                    "recorded-anchor-unresolved",
                    null,
                    anchorRecordingId,
                    ut);
            }
            return resolved;
        }

        private static Vector3d ResolveBackgroundPointWorldPosition(TrajectoryPoint point)
        {
            CelestialBody body = FindBackgroundBody(point.bodyName);
            if (body == null)
                return new Vector3d(double.NaN, double.NaN, double.NaN);
            return body.GetWorldSurfacePosition(point.latitude, point.longitude, point.altitude);
        }

        private static Quaternion ResolveBackgroundBodyWorldRotation(TrajectoryPoint point)
        {
            CelestialBody body = FindBackgroundBody(point.bodyName);
            return body != null && body.bodyTransform != null
                ? body.bodyTransform.rotation
                : Quaternion.identity;
        }

        private static bool TryResolveBackgroundOrbitalAnchorPose(
            Recording recording,
            TrackSection section,
            int sectionIndex,
            double ut,
            out AnchorPose pose)
        {
            pose = default;
            if (section.checkpoints == null || section.checkpoints.Count == 0)
                return false;

            for (int i = 0; i < section.checkpoints.Count; i++)
            {
                OrbitSegment segment = section.checkpoints[i];
                if (ut < segment.startUT || ut > segment.endUT)
                    continue;

                CelestialBody body = FindBackgroundBody(segment.bodyName);
                if (body == null)
                    return false;

                Orbit orbit = new Orbit(
                    segment.inclination,
                    segment.eccentricity,
                    segment.semiMajorAxis,
                    segment.longitudeOfAscendingNode,
                    segment.argumentOfPeriapsis,
                    segment.meanAnomalyAtEpoch,
                    segment.epoch,
                    body);
                Vector3d worldPos = orbit.getPositionAtUT(ut);
                Vector3d velocity = orbit.getOrbitalVelocityAtUT(ut);
                var rotation = ParsekFlight.ComputeOrbitalRotation(
                    segment,
                    orbit,
                    ut,
                    velocity,
                    worldPos,
                    body.position,
                    Quaternion.identity,
                    sectionIndex,
                    TrajectoryMath.HasOrbitalFrameRotation(segment),
                    TrajectoryMath.IsSpinning(segment));

                pose = new AnchorPose(worldPos, rotation.ghostRot, sectionIndex, recording?.RecordingId);
                return true;
            }

            return false;
        }

        private static CelestialBody FindBackgroundBody(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName))
                return null;
            try
            {
                return FlightGlobals.Bodies?.Find(b => b != null && b.name == bodyName);
            }
            catch (TypeInitializationException)
            {
                return null;
            }
        }

        private void ExitBackgroundRelativeMode(
            BackgroundVesselState state,
            Vessel vessel,
            double ut,
            string reason)
        {
            TrajectoryPoint boundaryPoint = CreateAbsoluteTrajectoryPointFromVessel(vessel, ut);
            string oldAnchorRecordingId = state.currentAnchorRecordingId;
            uint oldDiagnosticPid = state.currentAnchorCandidate.DiagnosticPid;
            ClearBackgroundCurrentAnchor(state);
            CloseBackgroundTrackSection(state, ut);
            SegmentEnvironment env = state.environmentHysteresis != null
                ? state.environmentHysteresis.CurrentEnvironment
                : SegmentEnvironment.Atmospheric;
            StartBackgroundTrackSection(state, env, ReferenceFrame.Absolute, ut);
            ActivateBackgroundHighFidelitySampling(state, ut, $"relative-exit-{reason ?? "unknown"}");
            SeedBackgroundBoundaryPoint(state, boundaryPoint);
            ParsekLog.Info("BgRecorder",
                $"RELATIVE mode exited: pid={state.vesselPid} " +
                $"previousAnchorRecordingId={oldAnchorRecordingId ?? "(none)"} " +
                $"diagnosticPid={oldDiagnosticPid} reason={reason}");
        }

        private void ForceBackgroundRelativeToAbsolute(
            BackgroundVesselState state,
            double ut,
            string reason)
        {
            string oldAnchorRecordingId = state.currentAnchorRecordingId;
            uint oldDiagnosticPid = state.currentAnchorCandidate.DiagnosticPid;
            ClearBackgroundCurrentAnchor(state);
            CloseBackgroundTrackSection(state, ut);
            SegmentEnvironment env = state.environmentHysteresis != null
                ? state.environmentHysteresis.CurrentEnvironment
                : SegmentEnvironment.Atmospheric;
            StartBackgroundTrackSection(state, env, ReferenceFrame.Absolute, ut);
            ActivateBackgroundHighFidelitySampling(state, ut, $"relative-force-exit-{reason ?? "unknown"}");
            ParsekLog.Info("BgRecorder",
                $"RELATIVE mode force-exited: pid={state.vesselPid} " +
                $"anchorRecordingId={oldAnchorRecordingId ?? "(none)"} " +
                $"diagnosticPid={oldDiagnosticPid} reason={reason} new ABSOLUTE section started");
        }
    }
}
