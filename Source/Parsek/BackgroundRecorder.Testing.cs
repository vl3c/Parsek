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

        internal void InjectCurrentTrackSectionFrameForTesting(uint vesselPid, TrajectoryPoint point)
        {
            BackgroundVesselState state;
            if (!loadedStates.TryGetValue(vesselPid, out state))
                return;

            AddFrameToActiveTrackSection(state, point);
            loadedStates[vesselPid] = state;
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
            loadedStates.Remove(vesselPid);
        }

        #endregion
    }
}
