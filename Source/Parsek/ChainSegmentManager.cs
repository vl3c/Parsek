using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Owns chain segment state: the active chain identity, pending transitions,
    /// boundary anchors, continuation tracking fields, and continuation sampling.
    /// Always-tree mode commits no chain segment, so no producer sets the chain
    /// identity or starts a continuation; the fields and the committed-list index
    /// subscriber stay until the dead state is removed with a ruling on that contract
    /// (todo CHAIN-STATE-LEFT-BEHIND-BY-THE-CHAIN-COMMIT-REMOVAL).
    /// </summary>
    // [ERS-exempt — Phase 3] ChainSegmentManager stores ContinuationRecordingIdx /
    // UndockContinuationRecIdx as indices into RecordingStore.CommittedRecordings
    // captured at commit time. Converting the bounds checks and `committed[idx]`
    // accesses to EffectiveState.ComputeERS() would shift indices whenever
    // NotCommitted / superseded / session-suppressed recordings change, breaking
    // chain continuation tracking.
    // TODO(phase 6+): migrate continuation tracking to recording-id-keyed refs.
    internal class ChainSegmentManager
    {
        // Tree identity (propagated from ParsekFlight.activeTree)
        internal string ActiveTreeId;

        // Core chain identity
        internal string ActiveChainId;          // null if not building a chain
        internal int ActiveChainNextIndex;      // next segment's ChainIndex
        internal string ActiveChainPrevId;      // previous segment's RecordingId
        internal string ActiveChainCrewName;    // EVA crew name for current segment (null if vessel)

        // Pending chain transition
        internal bool PendingContinuation;      // true when a segment ended and next should start
        internal bool PendingIsBoarding;        // true = boarding (EVA→vessel), false = EVA exit
        internal string PendingEvaName;         // kerbal name for EVA transitions

        // Boundary anchor for chain continuation (copied from previous segment's last point)
        internal TrajectoryPoint? PendingBoundaryAnchor;

        // Continuation sampling: after a vessel chain segment commits (V→EVA),
        // keeps tracking the original vessel so its trajectory extends beyond the EVA point.
        internal uint ContinuationVesselPid;            // 0 = not tracking
        internal int ContinuationRecordingIdx = -1;     // index into CommittedRecordings
        internal string ContinuationRecordingId;        // validates index hasn't gone stale
        internal Vector3 ContinuationLastVelocity;
        internal double ContinuationLastUT = -1;

        // Undock continuation (ghost-only recording for the other vessel)
        internal uint UndockContinuationPid;             // 0 = not tracking
        internal int UndockContinuationRecIdx = -1;
        internal string UndockContinuationRecId;         // validates index hasn't gone stale
        internal Vector3 UndockContinuationLastVel;
        internal double UndockContinuationLastUT = -1;

        /// <summary>Whether a chain is currently being built.</summary>
        internal bool HasActiveChain => ActiveChainId != null;

        /// <summary>Whether vessel continuation sampling is active.</summary>
        internal bool IsTrackingContinuation => ContinuationVesselPid != 0;

        /// <summary>Whether undock continuation sampling is active.</summary>
        internal bool IsTrackingUndockContinuation => UndockContinuationPid != 0;

        /// <summary>
        /// Copies chain identity fields (ChainId, ChainIndex, ParentRecordingId, EvaCrewName)
        /// onto the given recording. No-op if no chain is active.
        /// </summary>
        internal void ApplyChainMetadataTo(Recording rec)
        {
            if (ActiveChainId == null) return;
            rec.ChainId = ActiveChainId;
            rec.ChainIndex = ActiveChainNextIndex;
            rec.ParentRecordingId = ActiveChainPrevId;
            rec.EvaCrewName = ActiveChainCrewName;
        }

        /// <summary>
        /// Returns the continuation recording if the index is valid, or null if stale/unset.
        /// </summary>
        internal bool TryGetContinuationRecording(out Recording rec)
        {
            rec = null;
            if (ContinuationRecordingIdx < 0 ||
                ContinuationRecordingIdx >= RecordingStore.CommittedRecordings.Count)
                return false;
            rec = RecordingStore.CommittedRecordings[ContinuationRecordingIdx];
            if (ContinuationRecordingId != null && rec.RecordingId != ContinuationRecordingId)
            {
                ParsekLog.Warn("Chain",
                    $"Continuation recording ID mismatch at index {ContinuationRecordingIdx}: " +
                    $"expected={ContinuationRecordingId}, actual={rec.RecordingId}");
                rec = null;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Returns the undock continuation recording if the index is valid, or null if stale/unset.
        /// </summary>
        internal bool TryGetUndockContinuationRecording(out Recording rec)
        {
            rec = null;
            if (UndockContinuationRecIdx < 0 ||
                UndockContinuationRecIdx >= RecordingStore.CommittedRecordings.Count)
                return false;
            rec = RecordingStore.CommittedRecordings[UndockContinuationRecIdx];
            if (UndockContinuationRecId != null && rec.RecordingId != UndockContinuationRecId)
            {
                ParsekLog.Warn("Chain",
                    $"Undock continuation recording ID mismatch at index {UndockContinuationRecIdx}: " +
                    $"expected={UndockContinuationRecId}, actual={rec.RecordingId}");
                rec = null;
                return false;
            }
            return true;
        }

        // Optimizer merges seen by OnCommittedRecordingRemoved: absorbed recording id -> the
        // merge target's id. No reader remains since chain segment commits were removed; the
        // map is part of the index-contract subscriber and goes with it (see the class note).
        private readonly Dictionary<string, string> absorbedIntoByRecordingId =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Pure: follows <paramref name="absorbedInto"/> from <paramref name="recordingId"/>
        /// to the recording that carries its trajectory now (itself when never absorbed),
        /// then returns that recording's committed index, or -1 when it is not in the list.
        /// Cycle-safe (bounded by the map size).
        /// </summary>
        internal static int ResolveCommittedIndexThroughAbsorption(
            string recordingId,
            IReadOnlyDictionary<string, string> absorbedInto,
            IReadOnlyList<Recording> committed)
        {
            if (string.IsNullOrEmpty(recordingId) || committed == null) return -1;
            string id = recordingId;
            int hops = 0;
            while (absorbedInto != null && absorbedInto.TryGetValue(id, out string target)
                   && !string.IsNullOrEmpty(target) && hops++ <= absorbedInto.Count)
            {
                id = target;
            }
            for (int i = 0; i < committed.Count; i++)
            {
                if (string.Equals(committed[i]?.RecordingId, id, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// The committed list lost the recording at <paramref name="index"/>. A continuation
        /// tracking that very recording retargets to <paramref name="absorbedInto"/> (the
        /// optimizer merge target now carrying its trajectory) or stops when there is none;
        /// every other tracked slot is rebound by id, because the per-frame sampler indexes
        /// the list directly and has no id guard of its own.
        /// </summary>
        internal void OnCommittedRecordingRemoved(int index, Recording removed, Recording absorbedInto)
        {
            string removedId = removed?.RecordingId;
            if (!string.IsNullOrEmpty(removedId) && !string.IsNullOrEmpty(absorbedInto?.RecordingId))
                absorbedIntoByRecordingId[removedId] = absorbedInto.RecordingId;

            if (ContinuationVesselPid != 0 && ContinuationRecordingIdx == index)
            {
                if (absorbedInto != null && !string.IsNullOrEmpty(absorbedInto.RecordingId))
                {
                    ParsekLog.Info("Chain",
                        $"Continuation recording #{index} (id={ContinuationRecordingId}) merged into " +
                        $"id={absorbedInto.RecordingId} - retargeting continuation sampling");
                    ContinuationRecordingId = absorbedInto.RecordingId;
                }
                else
                {
                    StopContinuation("tracked recording removed from committed list");
                }
            }
            if (UndockContinuationPid != 0 && UndockContinuationRecIdx == index)
            {
                if (absorbedInto != null && !string.IsNullOrEmpty(absorbedInto.RecordingId))
                {
                    ParsekLog.Info("Chain",
                        $"Undock continuation recording #{index} (id={UndockContinuationRecId}) merged into " +
                        $"id={absorbedInto.RecordingId} - retargeting undock continuation sampling");
                    UndockContinuationRecId = absorbedInto.RecordingId;
                }
                else
                {
                    StopUndockContinuation("tracked recording removed from committed list");
                }
            }

            RebindContinuationIndices($"committed removal at #{index}");
        }

        /// <summary>
        /// Insert mirror of <see cref="OnCommittedRecordingRemoved"/>: a recording was inserted
        /// at <paramref name="index"/>, so both tracked slots are rebound by id.
        /// </summary>
        internal void OnCommittedRecordingInserted(int index)
        {
            RebindContinuationIndices($"committed insert at #{index}");
        }

        /// <summary>
        /// Re-derives both continuation indices from their recording ids against the current
        /// committed list. A tracked id that is no longer in the list stops that continuation.
        /// </summary>
        internal void RebindContinuationIndices(string cause)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (ContinuationVesselPid != 0)
            {
                int before = ContinuationRecordingIdx;
                int idx = ResolveCommittedIndexThroughAbsorption(ContinuationRecordingId, null, committed);
                if (idx < 0)
                    StopContinuation($"tracked recording id={ContinuationRecordingId} not found after {cause}");
                else if (idx != before)
                {
                    ContinuationRecordingIdx = idx;
                    ParsekLog.Info("Chain",
                        $"Continuation index rebound after {cause}: #{before} -> #{idx} (id={ContinuationRecordingId})");
                }
            }
            if (UndockContinuationPid != 0)
            {
                int before = UndockContinuationRecIdx;
                int idx = ResolveCommittedIndexThroughAbsorption(UndockContinuationRecId, null, committed);
                if (idx < 0)
                    StopUndockContinuation($"tracked recording id={UndockContinuationRecId} not found after {cause}");
                else if (idx != before)
                {
                    UndockContinuationRecIdx = idx;
                    ParsekLog.Info("Chain",
                        $"Undock continuation index rebound after {cause}: #{before} -> #{idx} (id={UndockContinuationRecId})");
                }
            }
        }

        // Continuation adaptive sampling thresholds (read from settings, same as FlightRecorder)
        private static float ContinuationMinInterval =>
            ParsekSettings.Current?.minSampleInterval ?? ParsekSettings.GetMinSampleInterval(SamplingDensity.Medium);
        private static float ContinuationMaxInterval =>
            ParsekSettings.Current?.maxSampleInterval ?? ParsekSettings.GetMaxSampleInterval(SamplingDensity.Medium);
        private static float ContinuationVelDirThreshold =>
            ParsekSettings.Current?.velocityDirThreshold ?? ParsekSettings.GetVelocityDirThreshold(SamplingDensity.Medium);
        private static float ContinuationSpeedThreshold =>
            (ParsekSettings.Current?.speedChangeThreshold ?? ParsekSettings.GetSpeedChangeThreshold(SamplingDensity.Medium)) / 100f;

        internal ChainSegmentManager()
        {
            ParsekLog.Info("Chain", "ChainSegmentManager created");
        }

        /// <summary>
        /// Clears all chain state. Called from ResetFlightReadyState on flight ready/revert.
        /// </summary>
        internal void ClearAll()
        {
            ActiveTreeId = null;
            ActiveChainId = null;
            ActiveChainNextIndex = 0;
            ActiveChainPrevId = null;
            ActiveChainCrewName = null;
            PendingContinuation = false;
            PendingIsBoarding = false;
            PendingEvaName = null;
            PendingBoundaryAnchor = null;
            ContinuationVesselPid = 0;
            ContinuationRecordingIdx = -1;
            ContinuationRecordingId = null;
            ContinuationLastVelocity = Vector3.zero;
            ContinuationLastUT = -1;
            UndockContinuationPid = 0;
            UndockContinuationRecIdx = -1;
            UndockContinuationRecId = null;
            UndockContinuationLastVel = Vector3.zero;
            UndockContinuationLastUT = -1;
            absorbedIntoByRecordingId.Clear();
            ParsekLog.Verbose("Chain", "ClearAll: all chain state reset");
        }

        /// <summary>
        /// Clears only the chain identity fields (ID, index, prev, crew).
        /// Used on chain termination or abort where continuation state is handled separately.
        /// </summary>
        internal void ClearChainIdentity()
        {
            ActiveChainId = null;
            ActiveChainNextIndex = 0;
            ActiveChainPrevId = null;
            ActiveChainCrewName = null;
        }

        /// <summary>
        /// Clears continuation boundary on a recording, accepting the extended data as canonical.
        /// Called before StopContinuation/StopUndockContinuation on all normal stop paths.
        /// </summary>
        internal static void BakeContinuationData(Recording rec)
        {
            if (rec.ContinuationBoundaryIndex >= 0)
                ParsekLog.Verbose("Chain",
                    $"Baked continuation data for '{rec.VesselName}' " +
                    $"(boundary={rec.ContinuationBoundaryIndex}, points={rec.Points.Count}, id={rec.RecordingId})");
            rec.ContinuationBoundaryIndex = -1;
            rec.PreContinuationVesselSnapshot = null;
            rec.PreContinuationGhostSnapshot = null;
        }

        /// <summary>
        /// Stops vessel continuation tracking. Clears PID and recording index.
        /// </summary>
        internal void StopContinuation(string reason)
        {
            ParsekLog.Verbose("Chain",
                $"Continuation stopped ({reason}): was tracking pid={ContinuationVesselPid}, " +
                $"recording #{ContinuationRecordingIdx}");
            ContinuationVesselPid = 0;
            ContinuationRecordingIdx = -1;
            ContinuationRecordingId = null;
            ContinuationLastVelocity = Vector3.zero;
            ContinuationLastUT = -1;
        }

        /// <summary>
        /// Stops undock continuation tracking. Clears PID, recording index, and last UT.
        /// </summary>
        internal void StopUndockContinuation(string reason)
        {
            ParsekLog.Verbose("Chain",
                $"Undock continuation stopped ({reason}): was tracking pid={UndockContinuationPid}, " +
                $"recording #{UndockContinuationRecIdx}");
            UndockContinuationPid = 0;
            UndockContinuationRecIdx = -1;
            UndockContinuationRecId = null;
            UndockContinuationLastUT = -1;
        }

        #region Continuation Sampling (Group A)

        /// <summary>
        /// Shared implementation for continuation sampling. Both EVA and undock continuations
        /// use identical adaptive-sampling logic -- only the state fields and stop action differ.
        /// </summary>
        internal void SampleContinuationVessel(
            uint pid, ref int recIdx, ref Vector3 lastVel, ref double lastUT,
            Action<string> stopMethod, string label)
        {
            if (pid == 0) return;

            // Guard against stale index (e.g. an internal removal shrank the list)
            if (recIdx < 0 ||
                recIdx >= RecordingStore.CommittedRecordings.Count)
            {
                stopMethod("stale index");
                return;
            }

            Vessel v = FlightRecorder.FindVesselByPid(pid);
            if (v == null)
            {
                stopMethod("vessel null");
                return;
            }

            double ut = Planetarium.GetUniversalTime();
            Vector3 velocity = v.packed
                ? (Vector3)v.obt_velocity
                : (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity());

            if (!TrajectoryMath.ShouldRecordPoint(velocity, lastVel,
                ut, lastUT,
                ContinuationMinInterval, ContinuationMaxInterval,
                ContinuationVelDirThreshold, ContinuationSpeedThreshold))
                return;

            var rec = RecordingStore.CommittedRecordings[recIdx];

            // Carry forward resource values from the last point (vessel doesn't earn
            // resources while flying autonomously after EVA)
            var lastPoint = rec.Points.Count > 0
                ? rec.Points[rec.Points.Count - 1]
                : default(TrajectoryPoint);

            var point = new TrajectoryPoint
            {
                ut = ut,
                latitude = v.latitude,
                longitude = v.longitude,
                altitude = v.altitude,
                rotation = v.srfRelRotation,
                velocity = velocity,
                bodyName = v.mainBody.name,
                funds = lastPoint.funds,
                science = lastPoint.science,
                reputation = lastPoint.reputation,
                // Phase 7: continuation chains spawn after a primary recording's
                // terminal — clearance NaN means "not measured", playback uses
                // legacy altitude path. Continuation sampling never enters a
                // SurfaceMobile section.
                recordedGroundClearance = double.NaN
            };

            rec.Points.Add(point);
            // Mark dirty so the next OnSave rewrites the .prec sidecar with
            // the extended trajectory. Continuation sampling extends a
            // committed recording's Points after commit, so without this the
            // .prec file stays frozen at the pre-continuation state on reload.
            rec.MarkFilesDirty();
            lastUT = ut;
            lastVel = velocity;
        }

        /// <summary>
        /// Samples the continuation vessel's position each frame (adaptive sampling).
        /// Extends the committed recording's trajectory beyond the EVA point.
        /// </summary>
        internal void UpdateContinuationSampling()
        {
            SampleContinuationVessel(
                ContinuationVesselPid, ref ContinuationRecordingIdx,
                ref ContinuationLastVelocity, ref ContinuationLastUT,
                StopContinuation, "continuation");
        }

        /// <summary>
        /// Stops all active continuations (both vessel and undock), refreshing snapshots first.
        /// </summary>
        internal void StopAllContinuations(string reason)
        {
            if (ContinuationVesselPid != 0)
            {
                RefreshContinuationSnapshot();
                if (TryGetContinuationRecording(out var contRec))
                    BakeContinuationData(contRec);
                StopContinuation(reason);
            }
            if (UndockContinuationPid != 0)
            {
                RefreshUndockContinuationSnapshot();
                if (TryGetUndockContinuationRecording(out var undockRec))
                    BakeContinuationData(undockRec);
                StopUndockContinuation(reason);
            }
        }

        /// <summary>
        /// Shared implementation for refreshing a continuation recording's snapshot before stopping.
        /// If the vessel is loaded, takes a fresh snapshot. If unloaded/null,
        /// updates the existing snapshot's position from the last trajectory point.
        /// </summary>
        internal void RefreshContinuationSnapshotCore(
            uint pid, int recIdx, Action<string> stopMethod,
            Func<Recording, ConfigNode> getSnapshot, Action<Recording, ConfigNode> setSnapshot,
            string label)
        {
            if (pid == 0 || recIdx < 0) return;
            if (recIdx >= RecordingStore.CommittedRecordings.Count)
            {
                stopMethod("stale index in snapshot refresh");
                return;
            }

            var rec = RecordingStore.CommittedRecordings[recIdx];
            Vessel v = FlightRecorder.FindVesselByPid(pid);

            if (v != null && v.loaded)
            {
                var snapshot = VesselSpawner.TryBackupSnapshot(v);
                if (snapshot != null)
                {
                    setSnapshot(rec, snapshot);
                    ParsekLog.Verbose("Chain", $"{label}: refreshed snapshot from loaded vessel");
                }
            }
            else if (RecordingEndpointResolver.TryGetRecordingEndpointCoordinates(
                rec, out _, out double latitude, out double longitude, out double altitude))
            {
                var snap = getSnapshot(rec);
                if (snap != null)
                {
                    snap.SetValue("lat",
                        latitude.ToString("R", CultureInfo.InvariantCulture), true);
                    snap.SetValue("lon",
                        longitude.ToString("R", CultureInfo.InvariantCulture), true);
                    snap.SetValue("alt",
                        altitude.ToString("R", CultureInfo.InvariantCulture), true);
                    ParsekLog.Verbose("Chain", $"{label}: updated snapshot position from recording endpoint");
                }
            }
        }

        /// <summary>
        /// Refreshes the continuation recording's VesselSnapshot before stopping.
        /// If the vessel is loaded, takes a fresh snapshot. If unloaded/null,
        /// updates the existing snapshot's position from the last trajectory point.
        /// </summary>
        internal void RefreshContinuationSnapshot()
        {
            RefreshContinuationSnapshotCore(
                ContinuationVesselPid, ContinuationRecordingIdx, StopContinuation,
                r => r.VesselSnapshot, (r, s) => r.VesselSnapshot = s,
                "Continuation");
        }

        /// <summary>
        /// Samples the undock continuation vessel's position each frame (adaptive sampling).
        /// Mirrors UpdateContinuationSampling but for the undocked sibling vessel.
        /// </summary>
        internal void UpdateUndockContinuationSampling()
        {
            SampleContinuationVessel(
                UndockContinuationPid, ref UndockContinuationRecIdx,
                ref UndockContinuationLastVel, ref UndockContinuationLastUT,
                StopUndockContinuation, "undock continuation");
        }

        /// <summary>
        /// Refreshes the undock continuation recording's ghost snapshot before stopping.
        /// </summary>
        internal void RefreshUndockContinuationSnapshot()
        {
            RefreshContinuationSnapshotCore(
                UndockContinuationPid, UndockContinuationRecIdx, StopUndockContinuation,
                r => r.GhostVisualSnapshot, (r, s) => r.GhostVisualSnapshot = s,
                "Undock continuation");
        }

        #endregion
    }
}
