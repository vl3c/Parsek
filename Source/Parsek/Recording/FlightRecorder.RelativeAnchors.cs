using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Relative-frame anchor detection, anchor candidates, anchor pose resolution and the relative
    // offset / exit path.
    public partial class FlightRecorder
    {
        #region Anchor detection helpers (Phase 3a)

        /// <summary>
        /// Builds the set of vessel PIDs belonging to the current recording tree.
        /// These are excluded from anchor candidates — we only anchor to pre-existing
        /// vessels, not to sibling vessels within the same mission tree.
        /// </summary>
        private HashSet<uint> BuildTreeVesselPids()
        {
            if (ActiveTree == null) return null;

            var pids = new HashSet<uint>();
            foreach (var rec in ActiveTree.Recordings.Values)
            {
                if (rec.VesselPersistentId != 0)
                    pids.Add(rec.VesselPersistentId);
            }
            // Also include background map PIDs (covers vessels tracked by PID before recording creation)
            foreach (var pid in ActiveTree.BackgroundMap.Keys)
            {
                pids.Add(pid);
            }
            // Include the focused vessel itself (FindNearestAnchor already skips it,
            // but this makes the set complete for diagnostics)
            if (RecordingVesselId != 0)
                pids.Add(RecordingVesselId);

            return pids;
        }

        private IReadOnlyList<RecordingAnchorCandidate> BuildRecordingAnchorCandidateList(
            Vessel focusedVessel,
            double ut,
            out int liveScanned,
            out int liveAdded,
            out int ghostScanned,
            out int ghostAdded)
        {
            recordingAnchorCandidateBuffer.Clear();
            liveScanned = liveAdded = ghostScanned = ghostAdded = 0;

            if (!TryGetActiveTreeRecording(out Recording focusRecording)
                || focusedVessel == null)
            {
                ParsekLog.VerboseRateLimited("Anchor", "recording-anchor-no-focus",
                    $"Recording anchor candidate scan skipped: hasTree={ActiveTree != null} " +
                    $"activeRecordingId={ActiveTree?.ActiveRecordingId ?? "(none)"} " +
                    $"hasFocusedVessel={focusedVessel != null}",
                    5.0);
                return recordingAnchorCandidateBuffer;
            }

            Vector3d focusedWorldPosition = focusedVessel.GetWorldPos3D();
            AddLiveRecordingAnchorCandidates(
                focusedVessel,
                focusedWorldPosition,
                focusRecording,
                out liveScanned,
                out liveAdded);
            AddExternalRecordingAnchorCandidates(
                focusRecording,
                focusedWorldPosition,
                ut,
                out ghostScanned,
                out ghostAdded);

            ParsekLog.VerboseRateLimited("Anchor", "recording-anchor-candidates",
                $"Recording anchor candidates: focusRecordingId={focusRecording.RecordingId} " +
                $"live={liveAdded}/{liveScanned} ghost={ghostAdded}/{ghostScanned} " +
                $"total={recordingAnchorCandidateBuffer.Count}",
                5.0);

            return recordingAnchorCandidateBuffer;
        }

        private void AddLiveRecordingAnchorCandidates(
            Vessel focusedVessel,
            Vector3d focusedWorldPosition,
            Recording focusRecording,
            out int scanned,
            out int added)
        {
            scanned = added = 0;
            List<Vessel> vessels = TryGetFlightGlobalsVessels();
            if (vessels == null)
                return;

            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel vessel = vessels[i];
                if (!IsLiveRecordingAnchorVesselCandidate(vessel, focusedVessel))
                    continue;

                scanned++;
                if (!TryResolveLivePeerRecordingId(
                        vessel.persistentId,
                        AnchorDetector.TryReadLiveVesselGuid(vessel),
                        out string recordingId,
                        skipActiveProvisional: false))
                    continue;
                Vector3d candidateWorldPosition = vessel.GetWorldPos3D();
                if (TryGetReFlyTreeSamplingCandidateRecording(recordingId, focusRecording, out _))
                    ConsiderReFlyTreeSamplingProximity(
                        focusedWorldPosition,
                        candidateWorldPosition,
                        "live-anchor");
                if (!TryGetEligibleAnchorRecording(
                        recordingId,
                        focusRecording,
                        AnchorCandidateSource.Live,
                        vessel.persistentId,
                        out Recording candidateRecording))
                {
                    continue;
                }

                if (AnchorDetector.TryCreateRecordingAnchorCandidate(
                        focusRecording,
                        candidateRecording,
                        candidateWorldPosition,
                        vessel.transform.rotation,
                        AnchorCandidateSource.Live,
                        vessel.persistentId,
                        -1,
                        out RecordingAnchorCandidate candidate))
                {
                    recordingAnchorCandidateBuffer.Add(candidate);
                    added++;
                }
            }
        }

        private void AddExternalRecordingAnchorCandidates(
            Recording focusRecording,
            Vector3d focusedWorldPosition,
            double ut,
            out int scanned,
            out int added)
        {
            scanned = added = 0;
            var provider = ExternalRecordingAnchorCandidatesProvider;
            if (provider == null && ParsekFlight.Instance != null)
                provider = ParsekFlight.Instance.BuildGhostRecordingAnchorCandidatesForRecorder;
            if (provider == null)
                return;

            IEnumerable<RecordingAnchorCandidate> candidates =
                provider(ActiveTree, focusRecording.RecordingId, RecordingVesselId, ut);
            if (candidates == null)
                return;

            foreach (RecordingAnchorCandidate provided in candidates)
            {
                scanned++;
                if (TryGetReFlyTreeSamplingCandidateRecording(provided.RecordingId, focusRecording, out _))
                    ConsiderReFlyTreeSamplingProximity(
                        focusedWorldPosition,
                        provided.WorldPos,
                        provided.Source == AnchorCandidateSource.Live ? "live-anchor" : "ghost-anchor");
                if (!TryGetEligibleAnchorRecording(
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
                    recordingAnchorCandidateBuffer.Add(candidate);
                    added++;
                }
            }
        }

        private static bool IsLiveRecordingAnchorVesselCandidate(
            Vessel vessel,
            Vessel focusedVessel)
        {
            if (vessel == null || !vessel.loaded)
                return false;
            if (focusedVessel != null && vessel.persistentId == focusedVessel.persistentId)
                return false;
            if (GhostMapPresence.IsGhostMapVessel(vessel.persistentId))
                return false;
            if (vessel.vesselType == VesselType.Debris
                || vessel.vesselType == VesselType.EVA
                || vessel.vesselType == VesselType.SpaceObject
                || vessel.vesselType == VesselType.Flag)
            {
                return false;
            }
            if (vessel.situation == Vessel.Situations.LANDED
                || vessel.situation == Vessel.Situations.SPLASHED
                || vessel.situation == Vessel.Situations.PRELAUNCH)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Maps a LIVE peer vessel to the tree recording it represents, for the RELATIVE-anchor
        /// candidate scan. <paramref name="peerLaunchGuid"/> is the live vessel's <c>Vessel.id</c>
        /// (null when unknown) and gates the craft-baked pid lookup through
        /// <see cref="AnchorDetector.LiveAnchorLaunchMatches"/>: a preserved DIFFERENT launch of
        /// the same craft carries the same baked pid, and anchoring to it would write this
        /// recording's Relative frames as offsets from a stranger's world pose (silent trajectory
        /// corruption). Unknown Guids fall back to the prior pid-only behaviour.
        /// </summary>
        private bool TryResolveLivePeerRecordingId(
            uint peerPid,
            string peerLaunchGuid,
            out string recordingId,
            bool skipActiveProvisional = true)
        {
            recordingId = null;
            if (peerPid == 0u || peerPid == RecordingVesselId)
                return false;
            if (ActiveTree?.BackgroundMap == null)
                return false;
            if (!ActiveTree.BackgroundMap.TryGetValue(peerPid, out recordingId)
                || string.IsNullOrWhiteSpace(recordingId))
            {
                return false;
            }
            if (ActiveTree.Recordings != null
                && ActiveTree.Recordings.TryGetValue(recordingId, out Recording peerRecording)
                && !AnchorDetector.LiveAnchorLaunchMatches(peerRecording, peerLaunchGuid))
            {
                ParsekLog.VerboseRateLimited("Anchor", "recording-anchor-launch-mismatch",
                    $"Recording anchor candidate rejected: recordingId={recordingId} " +
                    $"reason=live-vessel-different-launch peerPid={peerPid} " +
                    $"recordedGuid={peerRecording.RecordedVesselGuid ?? "(none)"} " +
                    $"liveGuid={peerLaunchGuid ?? "(none)"}",
                    5.0);
                recordingId = null;
                return false;
            }
            if (skipActiveProvisional
                && IsStillBeingAppendedActiveProvisionalRecordingId(recordingId))
            {
                ParsekLog.VerboseRateLimited("Anchor", "recording-anchor-active-provisional-skip",
                    $"Recording anchor candidate skipped: recordingId={recordingId} " +
                    $"reason=active-provisional-out-of-scope peerPid={peerPid}",
                    5.0);
                return false;
            }

            return true;
        }

        private bool TryGetReFlyTreeSamplingCandidateRecording(
            string recordingId,
            Recording focusRecording,
            out Recording candidateRecording)
        {
            candidateRecording = null;
            if (string.IsNullOrWhiteSpace(recordingId))
                return false;
            if (ActiveTree?.Recordings == null
                || !ActiveTree.Recordings.TryGetValue(recordingId, out candidateRecording))
            {
                return false;
            }

            return AnchorDetector.IsRecordingAnchorEligible(focusRecording, candidateRecording);
        }

        private bool TryGetEligibleAnchorRecording(
            string recordingId,
            Recording focusRecording,
            AnchorCandidateSource source,
            uint diagnosticPid,
            out Recording candidateRecording)
        {
            candidateRecording = null;
            if (string.IsNullOrWhiteSpace(recordingId))
                return false;
            if (ActiveTree?.Recordings == null
                || !ActiveTree.Recordings.TryGetValue(recordingId, out candidateRecording))
            {
                return false;
            }
            if (IsStillBeingAppendedActiveProvisionalRecordingId(recordingId))
                return false;

            if (!AnchorDetector.IsRecordingAnchorEligible(focusRecording, candidateRecording))
                return false;
            if (!AnchorDetector.IsRecordingAnchorDAGOrderEligible(focusRecording, candidateRecording))
            {
                LogRecordingAnchorDagOrderSkip(focusRecording, candidateRecording, source, diagnosticPid);
                return false;
            }

            return true;
        }

        private static void LogRecordingAnchorDagOrderSkip(
            Recording focusRecording,
            Recording candidateRecording,
            AnchorCandidateSource source,
            uint diagnosticPid)
        {
            string focusId = focusRecording?.RecordingId ?? "(none)";
            string candidateId = candidateRecording?.RecordingId ?? "(none)";
            ParsekLog.VerboseRateLimited("Anchor",
                "recording-anchor-dag-order-skip|" + focusId + "|" + candidateId + "|" + source,
                $"Recording anchor candidate skipped: reason=dag-order focusRecordingId={focusId} " +
                $"candidateRecordingId={candidateId} focusTreeOrder={focusRecording?.TreeOrder ?? -1} " +
                $"candidateTreeOrder={candidateRecording?.TreeOrder ?? -1} source={source} diagnosticPid={diagnosticPid}",
                5.0);
        }

        private bool TryGetActiveTreeRecording(out Recording activeRecording)
        {
            activeRecording = null;
            return ActiveTree?.Recordings != null
                && !string.IsNullOrWhiteSpace(ActiveTree.ActiveRecordingId)
                && ActiveTree.Recordings.TryGetValue(ActiveTree.ActiveRecordingId, out activeRecording)
                && activeRecording != null;
        }

        private static bool IsStillBeingAppendedActiveProvisionalRecordingId(string recordingId)
        {
            ReFlySessionMarker marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            return marker != null
                && !string.IsNullOrEmpty(marker.ActiveReFlyRecordingId)
                && string.Equals(
                    marker.ActiveReFlyRecordingId,
                    recordingId,
                    StringComparison.Ordinal);
        }

        private void ClearCurrentRecordingAnchor()
        {
            currentAnchorRecordingId = null;
            currentAnchorPid = 0u;
            currentAnchorCandidate = default;
            hasCurrentAnchorCandidate = false;
            currentAnchorDistanceMeters = double.NaN;
        }

        private void SetCurrentRecordingAnchor(RecordingAnchorCandidate candidate, double distanceMeters)
        {
            currentAnchorRecordingId = candidate.RecordingId;
            currentAnchorPid = candidate.DiagnosticPid;
            currentAnchorCandidate = candidate;
            hasCurrentAnchorCandidate = true;
            currentAnchorDistanceMeters = distanceMeters;
        }

        private void ApplyCurrentRecordingAnchorToCurrentTrackSection()
        {
            if (!trackSectionActive || currentTrackSection.referenceFrame != ReferenceFrame.Relative)
                return;

            currentTrackSection.anchorRecordingId = currentAnchorRecordingId;
            currentTrackSection.anchorVesselId = 0u;
        }

        /// <summary>
        /// Per-physics-frame anchor detection state machine for RELATIVE frame (Phase 3a).
        /// Detects proximity to pre-existing vessels, enters/exits RELATIVE mode, and
        /// transitions TrackSections accordingly. Extracted from OnPhysicsFrame.
        /// </summary>
        /// <summary>
        /// Resolves the "on the surface" classification used by anchor detection.
        /// Prefers the debounced environment hysteresis when available (which filters
        /// EVA situation jitter — see #246 for the precedent fix); falls back to the
        /// raw KSP situation enum when the hysteresis instance is null. Extracted as
        /// internal static so the wiring (null branch + CurrentEnvironment read) is
        /// directly testable in xUnit without needing a Vessel instance.
        /// </summary>
        internal static bool ResolveAnchorOnSurface(EnvironmentHysteresis hysteresis, int situation)
        {
            return EnvironmentDetector.IsSurfaceForAnchorDetection(
                envHint: hysteresis?.CurrentEnvironment,
                situation: situation);
        }

        internal static string FormatRelativeExitDistanceForLog(
            bool anchorFound,
            double distanceMeters)
        {
            if (!anchorFound
                || double.IsNaN(distanceMeters)
                || double.IsInfinity(distanceMeters)
                || distanceMeters < 0.0
                || distanceMeters == double.MaxValue)
            {
                return "unresolved";
            }

            return distanceMeters.ToString("F1", CultureInfo.InvariantCulture) + "m";
        }

        internal static string FormatRelativeExitDistanceFieldForLog(
            bool anchorFound,
            double distanceMeters)
        {
            string distance = FormatRelativeExitDistanceForLog(anchorFound, distanceMeters);
            return distance == "unresolved"
                ? "dist=unresolved"
                : "dist=" + distance;
        }

        internal static string BuildRelativeModeExitedLogMessage(
            string previousAnchorRecordingId,
            uint diagnosticPid,
            bool anchorFound,
            double distanceMeters,
            uint vesselPid)
        {
            return $"RELATIVE mode exited: previousAnchorRecordingId={previousAnchorRecordingId ?? "(none)"} " +
                   $"diagnosticPid={diagnosticPid} " +
                   $"{FormatRelativeExitDistanceFieldForLog(anchorFound, distanceMeters)} " +
                   $"vesselPid={vesselPid}";
        }

        private void UpdateAnchorDetection(Vessel v)
        {
            // Skip anchor detection while on the surface — RELATIVE mode is for orbital
            // docking approaches, not pad neighbors. Surface vessels are pinned to the
            // ground and don't need relative positioning. Also handles the case where a
            // vessel lands near a base — exits RELATIVE mode on landing.
            bool onSurface = ResolveAnchorOnSurface(environmentHysteresis, (int)v.situation);
            reFlyTreeSamplingProximityMeters = double.NaN;
            reFlyTreeSamplingProximitySource = null;
            if (onSurface && isRelativeMode)
            {
                // Was flying in RELATIVE mode, just landed — exit RELATIVE
                // Sample boundary point BEFORE closing — adaptive sampler may have
                // skipped frames, so last recorded point could be far from here.
                double boundaryUT = Planetarium.GetUniversalTime();
                SamplePositionAtUT(v, boundaryUT);
                string oldAnchorRecordingId = currentAnchorRecordingId;
                uint oldAnchorPid = currentAnchorPid;
                isRelativeMode = false;
                ClearCurrentRecordingAnchor();
                RotateToNewTrackSection(v, boundaryUT, ReferenceFrame.Absolute,
                    "relative-exit-landing", "relative-exit-landing");
                ParsekLog.Info("Anchor",
                    $"RELATIVE mode exited on landing: previousAnchorRecordingId={oldAnchorRecordingId ?? "(none)"} " +
                    $"diagnosticPid={oldAnchorPid} " +
                    $"situation={v.situation} vesselPid={RecordingVesselId}");
            }
            else if (!onSurface)
            {
                // Build anchor candidates BEFORE the narrowed-gate filter.
                // BuildRecordingAnchorCandidateList has a load-bearing SIDE EFFECT
                // beyond the returned list: ConsiderReFlyTreeSamplingProximity
                // (called from Add{Live,External}RecordingAnchorCandidates)
                // populates reFlyTreeSamplingProximityMeters, which the next
                // OnPhysicsFrame consults via
                // ResolveActiveReFlyTreeSamplingCadence to choose between
                // Full / Half / None sampling tiers (0-250m / 250-500m / 500m+,
                // see ReFlyTree{Full,Half}FidelityProximityRangeMeters). The
                // scan must run before the filter so the proximity is populated
                // even on a re-fly frame where the filter drops every same-tree
                // candidate.
                double detectionUT = Planetarium.GetUniversalTime();
                var candidates = BuildRecordingAnchorCandidateList(
                    v,
                    detectionUT,
                    out int liveScanned,
                    out int liveAdded,
                    out int ghostScanned,
                    out int ghostAdded);

                // Narrowed-gate filter for re-fly provisionals: drop every
                // candidate whose recording id is a member of the same
                // RecordingTree as the provisional. Real persistent vessels
                // (stations, bases, live vessels from other lineages) are
                // out-of-tree and pass through, so a re-fly that starts
                // mid-docking-approach still authors
                // Relative-against-real-station and a loop-anchored re-fly
                // fork still authors Relative-against-live-loop-anchor. See
                // docs/dev/plans/narrow-refly-relative-gate.md.
                IReadOnlyList<RecordingAnchorCandidate> nearestSearchCandidates =
                    ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(
                        ActiveTree,
                        candidates);

                Vector3d focusedWorldPosition = v.GetWorldPos3D();
                var result = AnchorDetector.FindNearestRecordingAnchor(
                    ActiveTree?.ActiveRecordingId,
                    RecordingVesselId,
                    focusedWorldPosition,
                    nearestSearchCandidates,
                    AnchorDetector.RelativeFrameRangeLimit(isRelativeMode));

                bool shouldBeRelative = result.found
                    && AnchorDetector.ShouldUseRelativeFrame(result.distance, isRelativeMode);

                if (shouldBeRelative)
                {
                    bool anchorChanged = !isRelativeMode
                        || !string.Equals(
                            currentAnchorRecordingId,
                            result.candidate.RecordingId,
                            StringComparison.Ordinal);
                    if (anchorChanged)
                    {
                        // Entering RELATIVE mode or switching anchors. Sample the
                        // old section before closing so its final point still uses
                        // the old frame contract.
                        SamplePosition(v);
                        var ic = CultureInfo.InvariantCulture;
                        string oldAnchorRecordingId = currentAnchorRecordingId;
                        uint oldAnchorPid = currentAnchorPid;
                        isRelativeMode = true;
                        SetCurrentRecordingAnchor(result.candidate, result.distance);
                        double boundaryUT = Planetarium.GetUniversalTime();
                        CloseCurrentTrackSection(boundaryUT);
                        var env = environmentHysteresis != null
                            ? environmentHysteresis.CurrentEnvironment
                            : SegmentEnvironment.Atmospheric;
                        StartNewTrackSection(env, ReferenceFrame.Relative, boundaryUT);
                        ApplyCurrentRecordingAnchorToCurrentTrackSection();
                        ActivateHighFidelitySampling(boundaryUT, "relative-enter");
                        bool seeded = SeedRelativeBoundaryPoint(v, result.candidate, boundaryUT);
                        if (!seeded)
                        {
                            ForceExitRelativeToAbsolute(
                                boundaryUT,
                                "relative-boundary-seed-failed");
                            return;
                        }

                        string transition = oldAnchorRecordingId == null
                            ? "entered"
                            : "switched";
                        ParsekLog.Info("Anchor",
                            $"RELATIVE mode {transition}: anchorRecordingId={currentAnchorRecordingId} " +
                            $"source={result.candidate.Source} diagnosticPid={currentAnchorPid} " +
                            $"previousAnchorRecordingId={oldAnchorRecordingId ?? "(none)"} " +
                            $"previousDiagnosticPid={oldAnchorPid} " +
                            $"dist={result.distance.ToString("F1", ic)}m " +
                            $"sealed={result.candidate.IsSealed} " +
                            $"affinity={AnchorDetector.RecordingAnchorAffinityRank(result.candidate)} " +
                            $"liveCandidates={liveAdded}/{liveScanned} ghostCandidates={ghostAdded}/{ghostScanned} " +
                            $"vesselPid={RecordingVesselId}");
                    }
                    else
                    {
                        SetCurrentRecordingAnchor(result.candidate, result.distance);
                        ApplyCurrentRecordingAnchorToCurrentTrackSection();
                    }
                }
                else if (!shouldBeRelative && isRelativeMode)
                {
                    // Exiting RELATIVE mode
                    // Sample boundary point BEFORE closing — adaptive sampler may have
                    // skipped frames, so last recorded point could be far from here.
                    double boundaryUT = Planetarium.GetUniversalTime();
                    SamplePositionAtUT(v, boundaryUT);
                    string oldAnchorRecordingId = currentAnchorRecordingId;
                    uint oldAnchorPid = currentAnchorPid;
                    if (!result.found)
                    {
                        ParsekLog.Info("Anchor",
                            $"RELATIVE exit: no eligible recording anchor candidates " +
                            $"liveCandidates={liveAdded}/{liveScanned} ghostCandidates={ghostAdded}/{ghostScanned}");
                    }
                    isRelativeMode = false;
                    ClearCurrentRecordingAnchor();
                    RotateToNewTrackSection(v, boundaryUT, ReferenceFrame.Absolute,
                        "relative-exit", "relative-exit-distance");
                    ParsekLog.Info("Anchor",
                        BuildRelativeModeExitedLogMessage(
                            oldAnchorRecordingId,
                            oldAnchorPid,
                            result.found,
                            result.distance,
                            RecordingVesselId));
                }
            }
        }

        /// <summary>
        /// Closes the current track section and opens a new one at the boundary UT, resolving
        /// the environment from the hysteresis (defaulting to Atmospheric when absent), then
        /// activates high-fidelity sampling and appends a section-start seam point. Shared tail
        /// of the two RELATIVE-exit cases in <see cref="UpdateAnchorDetection"/> (landing and
        /// distance), which differ only in the activation / seam reason strings.
        /// </summary>
        private void RotateToNewTrackSection(
            Vessel v, double boundaryUT, ReferenceFrame frame,
            string activationReason, string seamReason)
        {
            CloseCurrentTrackSection(boundaryUT);
            var env = environmentHysteresis != null
                ? environmentHysteresis.CurrentEnvironment
                : SegmentEnvironment.Atmospheric;
            StartNewTrackSection(env, frame, boundaryUT);
            ActivateHighFidelitySampling(boundaryUT, activationReason);
            AppendSectionStartSeamPoint(v, boundaryUT, seamReason);
        }

        #endregion

        /// <summary>
        /// In RELATIVE mode, rewrites the trajectory point using the version-specific
        /// RELATIVE-frame contract. If the anchor is no longer loaded, force-exits
        /// RELATIVE mode and starts a new ABSOLUTE TrackSection.
        /// </summary>
        private bool ApplyRelativeOffset(ref TrajectoryPoint point, Vessel v)
        {
            if (!isRelativeMode)
                return false;

            if (string.IsNullOrWhiteSpace(currentAnchorRecordingId))
            {
                ParsekLog.Warn("Anchor",
                    $"RELATIVE sample missing anchorRecordingId — forcing ABSOLUTE " +
                    $"at UT={point.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"diagnosticPid={currentAnchorPid}");
                ForceExitRelativeToAbsolute(point.ut, "anchor-recording-id-missing");
                return false;
            }

            if (!TryResolveCurrentAnchorPose(
                    point.ut,
                    out AnchorPose anchorPose,
                    out RelativeAnchorResolveFailure failure))
            {
                string reason = RelativeAnchorResolveFailure.ReasonOrFallback(
                    failure,
                    "recorded-anchor-unresolved");
                // Anchor recording no longer resolves — force transition out of RELATIVE mode.
                // The point keeps its absolute lat/lon/alt from the vessel (correct for ABSOLUTE).
                // Close the RELATIVE section and start a new ABSOLUTE section so the frame
                // tag matches the data.
                //
                // No explicit AppendSectionStartSeamPoint call here: every caller of
                // ApplyRelativeOffset commits the (now-absolute) `point` immediately
                // after we return. That commit lands at the same UT as the new section's
                // startUT, so the section's first sampled point IS the seam.
                ParsekLog.Warn("Anchor",
                    $"Anchor recording id={currentAnchorRecordingId} unresolved — " +
                    $"forcing transition to ABSOLUTE at UT={point.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"reason={reason} diagnosticPid={currentAnchorPid}");
                ForceExitRelativeToAbsolute(point.ut, reason);
                return false;
            }

            return ApplyRelativeOffsetForAnchorPose(
                ref point,
                v,
                anchorPose,
                currentAnchorRecordingId,
                hasCurrentAnchorCandidate ? currentAnchorCandidate.Source : AnchorCandidateSource.Ghost,
                currentAnchorPid,
                logSample: true);
        }

        private bool ApplyRelativeOffsetForAnchorPose(
            ref TrajectoryPoint point,
            Vessel v,
            AnchorPose anchorPose,
            string anchorRecordingId,
            AnchorCandidateSource source,
            uint diagnosticPid,
            bool logSample)
        {
            if (v == null || string.IsNullOrWhiteSpace(anchorRecordingId))
                return false;

            int recordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            Vector3d offset;
            if (!TryResolveAbsolutePointWorldForRelativeOffset(
                    point,
                    v,
                    out Vector3d focusWorldPos))
            {
                focusWorldPos = v.GetWorldPos3D();
            }
            Vector3d focusWorldPosCaptured = focusWorldPos;
            offset = TrajectoryMath.ComputeRelativeLocalOffset(
                focusWorldPos,
                anchorPose.WorldPos,
                anchorPose.WorldRotation);
            Quaternion focusWorldRotation;
            if (!TryResolveAbsolutePointWorldRotationForRelativeOffset(
                    point,
                    v,
                    out focusWorldRotation))
            {
                focusWorldRotation = v.transform.rotation;
            }
            point.rotation = TrajectoryMath.ComputeRelativeLocalRotation(
                focusWorldRotation,
                anchorPose.WorldRotation);

            point.latitude = offset.x;
            point.longitude = offset.y;
            point.altitude = offset.z;
            ApplyCurrentRecordingAnchorToCurrentTrackSection();

            // ---- Trace-Sep: log both parent-vs-debris distance measurements ----
            // recorded-relative = |offset| (= magnitude of the anchor-local
            // Cartesian offset just written into frames[].latitude/longitude/altitude).
            // recorded-absolute = |focusWorldPosCaptured - anchorPose.WorldPos|
            // (= ground-truth world-space distance at this instant). These
            // MUST agree exactly under the v0 contract (rotation alone
            // preserves magnitude). Divergence indicates the rotation path
            // introduces unexpected scaling / mis-applied anchor frame and
            // would mean the recorded anchor-local distance does not
            // correspond to the real ground-truth distance.
            if (TraceSeparation.RecordingWindowActive || TraceSeparation.PlaybackWindowActive)
            {
                Vector3d worldDelta = focusWorldPosCaptured - anchorPose.WorldPos;
                double recordedRelativeDist = offset.magnitude;
                double recordedAbsoluteDist = worldDelta.magnitude;
                TraceSeparation.RecordLog("FG_ApplyRel",
                    "vesselPid=" + (v?.persistentId ?? 0u) +
                    " anchorRecId=" + (anchorRecordingId ?? "<null>") +
                    " ut=" + point.ut.ToString("R", CultureInfo.InvariantCulture) +
                    " focusWorldPos=" + TraceSeparation.FormatVector3d(focusWorldPosCaptured) +
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
                ParsekLog.VerboseRateLimited("Anchor", "relative-offset",
                    $"RELATIVE sample: contract=anchor-local " +
                    $"version={recordingFormatVersion} dx={offset.x:F2} dy={offset.y:F2} dz={offset.z:F2} " +
                    $"anchorRecordingId={anchorRecordingId} source={source} diagnosticPid={diagnosticPid} " +
                    $"|offset|={offset.magnitude:F2}m",
                    2.0);
            }
            return true;
        }

        private bool SeedRelativeBoundaryPoint(
            Vessel v,
            RecordingAnchorCandidate candidate,
            double boundaryUT)
        {
            if (v == null || string.IsNullOrWhiteSpace(candidate.RecordingId))
                return false;
            if (ShouldSuppressReFlyPostLoadTrajectoryWrite(boundaryUT, "relative-boundary-seed"))
            {
                // Suppression is a handled boundary during the settle window. Returning
                // false would force the caller down the absolute fallback path and write
                // the bad packed-frame sample we are deliberately avoiding.
                return true;
            }
            if (!TryResolveAnchorPoseForCandidate(
                    candidate,
                    boundaryUT,
                    out AnchorPose anchorPose,
                    out RelativeAnchorResolveFailure failure))
            {
                string reason = RelativeAnchorResolveFailure.ReasonOrFallback(
                    failure,
                    "recorded-anchor-unresolved");
                ParsekLog.Warn("Anchor",
                    $"RELATIVE boundary seed skipped: anchorRecordingId={candidate.RecordingId} " +
                    $"source={candidate.Source} diagnosticPid={candidate.DiagnosticPid} " +
                    $"reason={reason} ut={boundaryUT.ToString("F2", CultureInfo.InvariantCulture)}");
                return false;
            }

            TrajectoryPoint absolutePoint = BuildTrajectoryPoint(
                v,
                SampleCurrentVelocity(v),
                boundaryUT);
            TrajectoryPoint relativePoint = absolutePoint;
            if (!ApplyRelativeOffsetForAnchorPose(
                    ref relativePoint,
                    v,
                    anchorPose,
                    candidate.RecordingId,
                    candidate.Source,
                    candidate.DiagnosticPid,
                    logSample: false))
            {
                return false;
            }

            SeedBoundaryPoint(relativePoint, absolutePoint);
            ParsekLog.Verbose("Anchor",
                $"RELATIVE boundary seeded: anchorRecordingId={candidate.RecordingId} " +
                $"source={candidate.Source} diagnosticPid={candidate.DiagnosticPid} " +
                $"ut={boundaryUT.ToString("F2", CultureInfo.InvariantCulture)}");
            return true;
        }

        private bool TryResolveCurrentAnchorPose(
            double ut,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            if (hasCurrentAnchorCandidate
                && string.Equals(
                    currentAnchorCandidate.RecordingId,
                    currentAnchorRecordingId,
                    StringComparison.Ordinal))
            {
                return TryResolveAnchorPoseForCandidate(
                    currentAnchorCandidate,
                    ut,
                    out pose,
                    out failure);
            }

            return TryResolveRecordedAnchorPose(
                currentAnchorRecordingId,
                ut,
                out pose,
                out failure);
        }

        private bool TryResolveAnchorPoseForCandidate(
            RecordingAnchorCandidate candidate,
            double ut,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            pose = default;
            failure = default;
            if (string.IsNullOrWhiteSpace(candidate.RecordingId))
            {
                failure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.PreconditionFailed,
                    "anchor-recording-id-missing",
                    ActiveTree?.ActiveRecordingId,
                    candidate.RecordingId,
                    ut);
                return false;
            }

            if (candidate.Source == AnchorCandidateSource.Live && candidate.DiagnosticPid != 0u)
            {
                Vessel liveAnchor = FindVesselByPid(candidate.DiagnosticPid);
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
                    return true;
                }

                ParsekLog.WarnRateLimited("Anchor",
                    "live-recording-anchor-missing|" + candidate.RecordingId,
                    $"Live recording anchor candidate missing; falling back to recorded pose " +
                    $"anchorRecordingId={candidate.RecordingId} diagnosticPid={candidate.DiagnosticPid} " +
                    $"ut={ut.ToString("F2", CultureInfo.InvariantCulture)}",
                    5.0);
            }

            return TryResolveRecordedAnchorPose(candidate.RecordingId, ut, out pose, out failure);
        }

        private bool TryResolveRecordedAnchorPose(
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
                    ActiveTree?.ActiveRecordingId,
                    anchorRecordingId,
                    ut);
                return false;
            }

            if (RecordedAnchorPoseOverrideForTesting != null)
                return RecordedAnchorPoseOverrideForTesting(anchorRecordingId, ut, out pose, out failure);

            var context = BuildRecorderRelativeAnchorResolverContext();
            recordedAnchorResolverVisited.Clear();
            bool resolved;
            try
            {
                resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                    context,
                    anchorRecordingId,
                    ut,
                    recordedAnchorResolverVisited,
                    out pose,
                    out failure);
            }
            finally
            {
                recordedAnchorResolverVisited.Clear();
            }
            if (!resolved && !failure.HasFailure)
            {
                failure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.Other,
                    "recorded-anchor-unresolved",
                    ActiveTree?.ActiveRecordingId,
                    anchorRecordingId,
                    ut);
            }
            return resolved;
        }

        private RelativeAnchorResolverContext BuildRecorderRelativeAnchorResolverContext()
        {
            return new RelativeAnchorResolverContext(
                ActiveTree,
                focusRecordingId: ActiveTree?.ActiveRecordingId,
                focusTreeId: ActiveTree?.Id,
                activeReFlyMarker: ParsekScenario.Instance?.ActiveReFlySessionMarker,
                pendingTree: PendingTreeSlot.HasPendingTree ? PendingTreeSlot.PendingTree : null,
                absoluteWorldPositionResolver: ResolveRecordedPointWorldPosition,
                bodyWorldRotationResolver: ResolveRecordedPointBodyWorldRotation,
                orbitalCheckpointPoseResolver: ParsekFlight.TryResolveOrbitalCheckpointAnchorPose);
        }

        private static Vector3d ResolveRecordedPointWorldPosition(TrajectoryPoint point)
        {
            CelestialBody body = ParsekFlight.FindBodyForAnchorResolver(point.bodyName);
            if (body == null)
                return new Vector3d(double.NaN, double.NaN, double.NaN);
            return body.GetWorldSurfacePosition(point.latitude, point.longitude, point.altitude);
        }

        private static Quaternion ResolveRecordedPointBodyWorldRotation(TrajectoryPoint point)
        {
            CelestialBody body = ParsekFlight.FindBodyForAnchorResolver(point.bodyName);
            return body != null && body.bodyTransform != null
                ? body.bodyTransform.rotation
                : Quaternion.identity;
        }

        private string ResolveActiveRecordingIdForFrameCanonicalization()
        {
            if (ActiveTree != null && !string.IsNullOrEmpty(ActiveTree.ActiveRecordingId))
                return ActiveTree.ActiveRecordingId;
            return FinalizationCache?.RecordingId;
        }

        private void LogActiveReFlyOrbitSegmentVelocityNonFinite(
            Vessel vessel,
            double startUT,
            string context)
        {
            LogReFlyOrbitSegmentVelocityNonFinite(
                ResolveActiveRecordingIdForFrameCanonicalization(),
                vessel,
                startUT,
                "Recorder",
                context);
        }


        private void ForceExitRelativeToAbsolute(double ut, string reason)
        {
            string oldAnchorRecordingId = currentAnchorRecordingId;
            uint oldAnchorPid = currentAnchorPid;
            isRelativeMode = false;
            ClearCurrentRecordingAnchor();
            CloseCurrentTrackSection(ut);
            var env = environmentHysteresis != null
                ? environmentHysteresis.CurrentEnvironment
                : SegmentEnvironment.Atmospheric;
            StartNewTrackSection(env, ReferenceFrame.Absolute, ut);
            ActivateHighFidelitySampling(ut, $"relative-force-exit-{reason ?? "unknown"}");
            ParsekLog.Info("Anchor",
                $"RELATIVE mode force-exited: anchorRecordingId={oldAnchorRecordingId ?? "(none)"} " +
                $"diagnosticPid={oldAnchorPid} reason={reason} new ABSOLUTE section started");
        }
    }
}
