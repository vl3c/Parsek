using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    internal partial class BackgroundRecorder
    {
        #region Testing Support

        // Expose internal state for testing
        internal int OnRailsStateCount => onRailsStates.Count;
        internal int LoadedStateCount => loadedStates.Count;
        internal int FinalizationCacheCount => finalizationCaches.Count;

        internal bool HasOnRailsState(uint pid) => onRailsStates.ContainsKey(pid);
        internal bool HasLoadedState(uint pid) => loadedStates.ContainsKey(pid);
        internal bool HasFinalizationCache(uint pid) => finalizationCaches.ContainsKey(pid);

        internal RecordingFinalizationCache GetFinalizationCacheForTesting(uint pid)
        {
            RecordingFinalizationCache cache;
            return finalizationCaches.TryGetValue(pid, out cache) ? cache : null;
        }

        internal void CloseParentRecordingForTesting(Recording parentRec, uint parentPid, string branchPointId,
            double branchUT, TrajectoryPoint? parentBoundaryPoint = null)
        {
            CloseParentRecording(parentRec, parentPid, branchPointId, branchUT, parentBoundaryPoint);
        }

        internal bool GetOnRailsHasOpenSegment(uint pid)
        {
            BackgroundOnRailsState state;
            return onRailsStates.TryGetValue(pid, out state) && state.hasOpenOrbitSegment;
        }

        internal bool GetOnRailsIsLanded(uint pid)
        {
            BackgroundOnRailsState state;
            return onRailsStates.TryGetValue(pid, out state) && state.isLanded;
        }

        internal double GetOnRailsLastExplicitEndUpdate(uint pid)
        {
            BackgroundOnRailsState state;
            if (onRailsStates.TryGetValue(pid, out state))
                return state.lastExplicitEndUpdate;
            return -1;
        }

        internal bool IsPartEventsSubscribed => partEventsSubscribed;

        /// <summary>
        /// For testing: injects a loaded state for a vessel PID so that
        /// OnBackgroundPartDie / OnBackgroundPartJointBreak can find it
        /// without needing a real KSP Vessel.
        /// </summary>
        internal void InjectLoadedStateForTesting(uint vesselPid, string recordingId)
        {
            loadedStates[vesselPid] = new BackgroundVesselState
            {
                vesselPid = vesselPid,
                recordingId = recordingId,
            };
        }

        /// <summary>
        /// For testing: gets the decoupledPartIds set for a given vessel PID.
        /// Returns null if no loaded state exists.
        /// </summary>
        internal HashSet<uint> GetDecoupledPartIdsForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.decoupledPartIds;
            return null;
        }

        /// <summary>
        /// For testing: injects an open orbit segment into the on-rails state for a vessel PID.
        /// The vessel must already have an on-rails state (created by constructor or OnVesselBackgrounded).
        /// </summary>
        internal void InjectOpenOrbitSegmentForTesting(uint vesselPid, OrbitSegment segment)
        {
            BackgroundOnRailsState state;
            if (onRailsStates.TryGetValue(vesselPid, out state))
            {
                state.currentOrbitSegment = segment;
                state.hasOpenOrbitSegment = true;
            }
        }

        internal bool RefreshOnRailsFinalizationCacheForTesting(
            uint vesselPid,
            double currentUT,
            bool force = false)
        {
            BackgroundOnRailsState state;
            return onRailsStates.TryGetValue(vesselPid, out state)
                && RefreshOnRailsFinalizationCache(state, currentUT, "test_on_rails", force);
        }

        internal void AdoptFinalizationCacheForTesting(
            uint vesselPid,
            string recordingId,
            RecordingFinalizationCache cache)
        {
            AdoptInheritedFinalizationCache(vesselPid, recordingId, cache);
        }

        /// <summary>
        /// For testing: overrides the vessel finder used by CheckpointAllVessels.
        /// Set to null to restore default behavior (FlightRecorder.FindVesselByPid).
        /// </summary>
        internal void SetVesselFinderForTesting(System.Func<uint, Vessel> finder)
        {
            vesselFinderOverride = finder;
        }

        /// <summary>
        /// For testing: overrides the distance-to-focused-vessel computation.
        /// Set to null to restore default behavior (FlightGlobals.ActiveVessel distance).
        /// </summary>
        internal void SetDistanceOverrideForTesting(System.Func<Vessel, double> distanceFunc)
        {
            distanceOverrideForTesting = distanceFunc;
        }

        /// <summary>
        /// For testing: gets the current proximity sample interval for a loaded vessel.
        /// Returns double.MaxValue if no loaded state exists.
        /// </summary>
        internal double GetCurrentSampleIntervalForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.currentSampleInterval;
            return double.MaxValue;
        }

        /// <summary>
        /// For testing: gets the debris TTL expiry for a vessel PID.
        /// Returns double.NaN if not tracked.
        /// </summary>
        internal double GetDebrisTTLExpiryForTesting(uint vesselPid)
        {
            double expiry;
            if (debrisTTLExpiry.TryGetValue(vesselPid, out expiry))
                return expiry;
            return double.NaN;
        }

        /// <summary>
        /// For testing: injects a debris TTL expiry for a vessel PID.
        /// </summary>
        internal void InjectDebrisTTLForTesting(uint vesselPid, double expiry)
        {
            debrisTTLExpiry[vesselPid] = expiry;
        }

        /// <summary>
        /// For testing: schedules a deferred split check the way the background joint-break
        /// handler does, without the live joint or the pre-break vessel snapshot.
        /// </summary>
        internal void InjectPendingSplitCheckForTesting(uint parentPid, double branchUT, string recordingId)
        {
            pendingBackgroundSplitChecks[parentPid] = (branchUT, recordingId, (TrajectoryPoint?)null, "DECOUPLE");
        }

        /// <summary>
        /// For testing: gets the count of pending background split checks.
        /// </summary>
        internal int PendingSplitCheckCount => pendingBackgroundSplitChecks.Count;

        /// <summary>
        /// For testing: gets the count of debris TTL entries.
        /// </summary>
        internal int DebrisTTLCount => debrisTTLExpiry.Count;

        internal int PendingInitialEnvironmentOverrideCount => pendingInitialEnvironmentOverrides.Count;
        internal int PendingInitialTrajectoryPointCount => pendingInitialTrajectoryPoints.Count;

        internal SegmentEnvironment? PeekPendingInitialEnvironmentOverrideForTesting(uint vesselPid)
        {
            SegmentEnvironment env;
            if (pendingInitialEnvironmentOverrides.TryGetValue(vesselPid, out env))
                return env;
            return null;
        }

        internal TrajectoryPoint? PeekPendingInitialTrajectoryPointForTesting(uint vesselPid)
        {
            TrajectoryPoint point;
            if (pendingInitialTrajectoryPoints.TryGetValue(vesselPid, out point))
                return point;
            return null;
        }

        internal SegmentEnvironment? ConsumePendingInitialEnvironmentOverrideForTesting(uint vesselPid)
        {
            SegmentEnvironment env;
            if (TryConsumePendingInitialEnvironmentOverride(vesselPid, out env))
                return env;
            return null;
        }

        internal TrajectoryPoint? ConsumePendingInitialTrajectoryPointForTesting(uint vesselPid)
        {
            TrajectoryPoint point;
            if (TryConsumePendingInitialTrajectoryPoint(vesselPid, out point))
                return point;
            return null;
        }

        /// <summary>
        /// For testing: gets the accumulated TrackSections for a loaded vessel.
        /// Returns null if no loaded state exists.
        /// </summary>
        internal List<TrackSection> GetTrackSectionsForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.trackSections;
            return null;
        }

        /// <summary>
        /// For testing: checks if a loaded vessel has an active TrackSection.
        /// Returns false if no loaded state exists.
        /// </summary>
        internal bool GetTrackSectionActiveForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.trackSectionActive;
            return false;
        }

        /// <summary>
        /// For testing: gets the current TrackSection for a loaded vessel.
        /// Returns null/default if no loaded state exists.
        /// </summary>
        internal TrackSection? GetCurrentTrackSectionForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.currentTrackSection;
            return null;
        }

        /// <summary>
        /// For testing: gets the last recorded UT baseline for a loaded vessel.
        /// Returns double.NaN if no loaded state exists.
        /// </summary>
        internal double GetLastRecordedUTForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.lastRecordedUT;
            return double.NaN;
        }

        /// <summary>
        /// For testing: gets the last recorded velocity baseline for a loaded vessel.
        /// Returns Vector3.zero if no loaded state exists.
        /// </summary>
        internal Vector3 GetLastRecordedVelocityForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.lastRecordedVelocity;
            return Vector3.zero;
        }

        /// <summary>
        /// For testing: injects a loaded state with environment tracking initialized.
        /// Creates the state, sets up EnvironmentHysteresis, and opens the first TrackSection.
        /// </summary>
        internal void InjectLoadedStateWithEnvironmentForTesting(
            uint vesselPid, string recordingId, SegmentEnvironment initialEnv, double ut,
            TrajectoryPoint? initialPoint = null,
            ReferenceFrame initialReferenceFrame = ReferenceFrame.Absolute,
            string anchorRecordingId = null,
            TrajectoryPoint? bodyFixedInitialPoint = null)
        {
            var state = new BackgroundVesselState
            {
                vesselPid = vesselPid,
                recordingId = recordingId,
            };
            state.environmentHysteresis = new EnvironmentHysteresis(initialEnv);
            if (initialReferenceFrame == ReferenceFrame.Relative
                && !string.IsNullOrWhiteSpace(anchorRecordingId))
            {
                SetBackgroundCurrentAnchor(
                    state,
                    new RecordingAnchorCandidate(
                        anchorRecordingId,
                        Vector3d.zero,
                        Quaternion.identity,
                        AnchorCandidateSource.Ghost));
            }
            StartBackgroundTrackSection(state, initialEnv, initialReferenceFrame,
                initialPoint.HasValue ? initialPoint.Value.ut : ut);
            if (initialReferenceFrame == ReferenceFrame.Relative)
                ApplyBackgroundCurrentAnchorToTrackSection(state);
            if (initialPoint.HasValue)
            {
                Recording treeRec;
                if (tree != null && tree.Recordings.TryGetValue(recordingId, out treeRec))
                    ApplyInitialTrajectoryPoint(
                        state,
                        treeRec,
                        initialPoint.Value,
                        bodyFixedInitialPoint);
                else
                    AppendFrameToCurrentTrackSection(
                        state,
                        initialPoint.Value,
                        bodyFixedInitialPoint);
            }
            loadedStates[vesselPid] = state;
        }

        /// <summary>
        /// For testing: injects an on-rails state and consumes any queued initial
        /// trajectory point into the target recording, mirroring InitializeOnRailsState's
        /// seed-persistence behavior without requiring a live KSP Vessel.
        /// </summary>
        internal void InjectOnRailsStateForTesting(uint vesselPid, string recordingId, double ut)
        {
            TrajectoryPoint point;
            if (TryConsumePendingInitialTrajectoryPoint(vesselPid, out point))
            {
                if (point.ut > ut)
                    point.ut = ut;

                Recording treeRec;
                if (tree != null && tree.Recordings.TryGetValue(recordingId, out treeRec))
                    ApplyTrajectoryPointToRecording(treeRec, point);
            }

            onRailsStates[vesselPid] = new BackgroundOnRailsState
            {
                vesselPid = vesselPid,
                recordingId = recordingId,
                hasOpenOrbitSegment = false,
                isLanded = false,
                lastExplicitEndUpdate = ut
            };
        }

        internal void NoteProximitySilenceTickForTesting(uint vesselPid, double ut)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                NoteProximitySilenceTick(state, ut);
        }

        internal void NoteInRangeTickForTesting(uint vesselPid, double ut)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                CloseProximitySilence(state, ut);
        }

        internal void InjectCurrentTrackSectionFrameForTesting(uint vesselPid, TrajectoryPoint point)
        {
            BackgroundVesselState state;
            if (!loadedStates.TryGetValue(vesselPid, out state))
                return;

            AddFrameToActiveTrackSection(state, point);
            loadedStates[vesselPid] = state;
        }

        internal void ArmLoadedPhysicsSoiSampleForTesting(uint vesselPid, string fromBody, string toBody, double ut)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                ArmBackgroundPhysicsSoiSample(state, fromBody, toBody, ut);
        }

        internal bool GetLoadedPhysicsSoiSamplePendingForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            return loadedStates.TryGetValue(vesselPid, out state) && state.physicsSoiSamplePending;
        }

        /// <summary>
        /// For testing: the vessel-free split a loaded vessel's physics sample runs before it
        /// is appended. The tree recording is the flat list the closing sample goes to.
        /// </summary>
        internal bool TrySplitLoadedSectionAtPhysicsBodyChangeForTesting(
            uint vesselPid,
            TrajectoryPoint sample,
            TrajectoryPoint? closingPoint)
        {
            BackgroundVesselState state;
            if (!loadedStates.TryGetValue(vesselPid, out state))
                return false;
            Recording treeRec = null;
            if (tree != null && !string.IsNullOrEmpty(state.recordingId))
                tree.Recordings.TryGetValue(state.recordingId, out treeRec);
            return TrySplitBackgroundSectionAtPhysicsBodyChange(state, treeRec, sample, closingPoint);
        }

        /// <summary>For testing: the proximity interval bookkeeping of one loaded tick.</summary>
        internal void UpdateBackgroundSampleIntervalForTesting(
            uint vesselPid, double distance, double rawProximityInterval, double proximityInterval)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                UpdateBackgroundSampleInterval(state, vesselPid, distance, rawProximityInterval, proximityInterval);
        }

        internal int FixedStepSampleSkipCountForTesting => fixedStepSampleSkipCount;

        /// <summary>For testing: sets the loaded state's last-sample baseline the triggers compare against.</summary>
        internal void SetLoadedSampleBaselineForTesting(
            uint vesselPid, double ut, Vector3 velocity, Quaternion worldRotation,
            bool hasWorldRotation = true)
        {
            BackgroundVesselState state;
            if (!loadedStates.TryGetValue(vesselPid, out state))
                return;
            state.lastRecordedUT = ut;
            state.lastRecordedVelocity = velocity;
            state.lastWorldRotation = worldRotation;
            state.hasLastWorldRotation = hasWorldRotation;
        }

        /// <summary>For testing: the motion and attitude triggers of one loaded tick.</summary>
        internal void ResolveBackgroundSampleTriggersForTesting(
            uint vesselPid,
            double ut,
            Vector3 currentVelocity,
            Quaternion currentWorldRotation,
            bool highFidelityActive,
            float effectiveMinSampleInterval,
            float effectiveMaxSampleInterval,
            float minSampleInterval,
            float velocityDirThreshold,
            float speedChangeThreshold,
            out bool motionTriggered,
            out bool attitudeTriggered)
        {
            ResolveBackgroundSampleTriggers(
                loadedStates[vesselPid],
                ut,
                currentVelocity,
                currentWorldRotation,
                highFidelityActive,
                effectiveMinSampleInterval,
                effectiveMaxSampleInterval,
                minSampleInterval,
                velocityDirThreshold,
                speedChangeThreshold,
                out motionTriggered,
                out attitudeTriggered);
        }

        /// <summary>For testing: the initial environment a loaded state would open in (fresh state).</summary>
        internal SegmentEnvironment ResolveLoadedInitialEnvironmentForTesting(Vessel v, uint vesselPid)
        {
            return ResolveLoadedInitialEnvironment(v, vesselPid, new BackgroundVesselState
            {
                vesselPid = vesselPid,
            });
        }

        internal Recording ResolveDebrisSeedRecordingForTesting(
            bool hasTreeRecording, Recording treeRecForSeed, string recordingId)
        {
            return ResolveDebrisSeedRecording(hasTreeRecording, treeRecForSeed, recordingId);
        }

        /// <summary>For testing: seeds the debris proximity tier of an injected loaded state.</summary>
        internal void SeedDebrisProximityTierForTesting(uint vesselPid, Recording treeRecForDebris, double distance)
        {
            SeedDebrisProximityTier(loadedStates[vesselPid], treeRecForDebris, distance);
        }

        internal void GetDebrisProximityForTesting(
            uint vesselPid, out ProximitySamplingTier tier, out double distance, out string reason)
        {
            BackgroundVesselState state = loadedStates[vesselPid];
            tier = state.debrisProximityTier;
            distance = state.debrisProximityDistanceMeters;
            reason = state.debrisProximityReason;
        }

        internal bool HasPendingDebrisSeedParentAnchorPointForTesting(uint vesselPid) =>
            pendingDebrisSeedParentAnchorPoints.ContainsKey(vesselPid);

        internal void FallBackToAbsoluteDebrisSeedForTesting(
            Vessel v, uint vesselPid, string recordingId, Recording treeRecForDebris, Recording parentRec,
            Vessel parentVessel, double seedParentDistance, double ut, TrajectoryPoint initialTrajectoryPoint)
        {
            FallBackToAbsoluteDebrisSeed(v, vesselPid, recordingId, treeRecForDebris, parentRec, parentVessel,
                seedParentDistance, ut, initialTrajectoryPoint);
        }

        internal void ResolveDebrisSeedAnchorPoseForTesting(
            uint vesselPid, string recordingId, Recording treeRecForDebris, TrajectoryPoint initialTrajectoryPoint,
            double ut, AnchorPose liveAnchorPose, out AnchorPose seedAnchorPose, out string seedAnchorSource,
            out string seedRecordedFallbackFrameContract)
        {
            ResolveDebrisSeedAnchorPose(vesselPid, recordingId, treeRecForDebris, initialTrajectoryPoint, ut,
                liveAnchorPose, out seedAnchorPose, out seedAnchorSource, out seedRecordedFallbackFrameContract);
        }

        internal static void LogDebrisSeedContractAppliedForTesting(
            uint vesselPid, string recordingId, Recording treeRecForDebris, Vessel parentVessel,
            string seedFrameContract, string seedParentDriftField, string seedAnchorSource,
            string seedFocusRecordingId, string seedRecordedFallbackFrameContract,
            TrajectoryPoint initialTrajectoryPoint)
        {
            LogDebrisSeedContractApplied(vesselPid, recordingId, treeRecForDebris, parentVessel, seedFrameContract,
                seedParentDriftField, seedAnchorSource, seedFocusRecordingId, seedRecordedFallbackFrameContract,
                initialTrajectoryPoint);
        }

        /// <summary>For testing: opens the first section Absolute on a fresh loaded state for the pid.</summary>
        internal void OpenAbsoluteInitialTrackSectionForTesting(
            uint vesselPid, string recordingId, SegmentEnvironment initialEnv, bool hasInitialTrajectoryPoint,
            TrajectoryPoint initialTrajectoryPoint, double ut, bool hasTreeRecording, Recording treeRecForSeed)
        {
            var state = new BackgroundVesselState { vesselPid = vesselPid, recordingId = recordingId };
            state.environmentHysteresis = new EnvironmentHysteresis(initialEnv);
            OpenAbsoluteInitialTrackSection(state, initialEnv, hasInitialTrajectoryPoint, initialTrajectoryPoint,
                ut, hasTreeRecording, treeRecForSeed);
            loadedStates[vesselPid] = state;
        }

        internal double GetHighFidelitySamplingUntilUTForTesting(uint vesselPid) =>
            loadedStates.TryGetValue(vesselPid, out BackgroundVesselState state)
                ? state.highFidelitySamplingUntilUT
                : double.NaN;

        internal Recording BuildBackgroundSplitParentContinuationForTesting(
            BranchPoint bp, Recording parentRec, uint parentPid, double branchUT, Vessel parentVessel,
            out string liveParentGuid)
        {
            return BuildBackgroundSplitParentContinuation(bp, parentRec, parentPid, branchUT, parentVessel,
                out liveParentGuid);
        }

        internal void ReseedParentContinuationAfterSplitForTesting(
            Recording parentRec, uint parentPid, double branchUT, TrajectoryPoint? parentBoundaryPoint,
            Vessel parentVessel, InheritedEngineState? parentEngineState)
        {
            ReseedParentContinuationAfterSplit(parentRec, parentPid, branchUT, parentBoundaryPoint, parentVessel,
                parentEngineState);
        }

        /// <summary>For testing: the sampler's append of one sample (flat list + open section).</summary>
        internal void AppendLoadedSampleForTesting(uint vesselPid, TrajectoryPoint point)
        {
            BackgroundVesselState state;
            if (!loadedStates.TryGetValue(vesselPid, out state))
                return;
            Recording treeRec;
            if (tree != null && tree.Recordings.TryGetValue(state.recordingId, out treeRec))
            {
                treeRec.Points.Add(point);
                treeRec.MarkFilesDirty();
            }
            state.lastRecordedUT = point.ut;
            AddFrameToActiveTrackSection(state, point);
        }

        internal void CloseCurrentTrackSectionForTesting(uint vesselPid, double ut)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                CloseBackgroundTrackSection(state, ut);
        }

        internal int GetSectionFrameWarpFlagCountForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            return loadedStates.TryGetValue(vesselPid, out state) ? state.sectionFrameWarpFlags.Count : -1;
        }

        internal void StartRelativeTrackSectionForTesting(
            uint vesselPid,
            string anchorRecordingId,
            SegmentEnvironment env,
            double ut)
        {
            BackgroundVesselState state;
            if (!loadedStates.TryGetValue(vesselPid, out state))
                return;

            SetBackgroundCurrentAnchor(
                state,
                new RecordingAnchorCandidate(
                    anchorRecordingId,
                    Vector3d.zero,
                    Quaternion.identity,
                    AnchorCandidateSource.Ghost));
            if (state.trackSectionActive)
                CloseBackgroundTrackSection(state, ut);

            StartBackgroundTrackSection(state, env, ReferenceFrame.Relative, ut);
            ApplyBackgroundCurrentAnchorToTrackSection(state);
            loadedStates[vesselPid] = state;
        }

        internal void StartDebrisParentRelativeTrackSectionForTesting(
            uint vesselPid,
            string parentRecordingId,
            SegmentEnvironment env,
            double ut)
        {
            BackgroundVesselState state;
            if (!loadedStates.TryGetValue(vesselPid, out state))
                return;

            state.isRelativeMode = true;
            state.currentAnchorRecordingId = parentRecordingId;
            state.currentAnchorCandidate = default;
            state.hasCurrentAnchorCandidate = false;
            StartDebrisParentRelativeTrackSection(state, env, ut);
            loadedStates[vesselPid] = state;
        }

        /// <summary>
        /// Injected loaded→on-rails transition. Mirrors the tail of
        /// <c>OnBackgroundVesselGoOnRails</c> EXACTLY: flush, then the M6 terminal emit, then the
        /// rails-span snapshot, then drop <c>loadedStates</c>. The seam ran only the flush until
        /// 2026-08-11, so every injected fixture exercised the OLD transition — a fixture that
        /// packs mid-burn would have shown a latched plume and a silently re-synced re-entry as
        /// PASSING, which is the precise divergence M6 exists to close.
        /// </summary>
        internal void FlushLoadedStateForOnRailsTransitionForTesting(
            uint vesselPid,
            SegmentEnvironment nextEnv,
            bool willHavePlayableOnRailsPayload,
            TrajectoryPoint boundaryPoint,
            double ut)
        {
            BackgroundVesselState loadedState;
            Recording flushRec;
            if (!loadedStates.TryGetValue(vesselPid, out loadedState)
                || !tree.Recordings.TryGetValue(loadedState.recordingId, out flushRec))
            {
                return;
            }

            FlushLoadedStateForOnRailsTransition(
                loadedState,
                flushRec,
                nextEnv,
                willHavePlayableOnRailsPayload,
                boundaryPoint,
                ut);

            EmitBackgroundRailsTerminalEvents(loadedState, ut);
            CaptureRailsSpanPartStates(vesselPid, loadedState);

            loadedStates.Remove(vesselPid);
        }

        #endregion
    }
}
