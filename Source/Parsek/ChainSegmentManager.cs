using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Owns chain segment state: the active chain identity, pending transitions,
    /// boundary anchors, continuation tracking fields, and all commit/sampling methods.
    /// Extracted from ParsekFlight to isolate chain lifecycle into a single owner.
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

            // Guard against stale index (e.g. user wiped recordings from UI)
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
        /// Starts ghost-only continuation recording for the "other" vessel after undock.
        /// This vessel gets ChainBranch = 1 so it plays back as a ghost but never spawns.
        /// </summary>
        internal void StartUndockContinuation(uint otherPid)
        {
            // Stop any existing continuation first
            if (UndockContinuationPid != 0)
                StopUndockContinuation("replaced by new undock");

            Vessel otherVessel = FlightRecorder.FindVesselByPid(otherPid);
            if (otherVessel == null)
            {
                ParsekLog.Verbose("Chain", $"Undock continuation: cannot find vessel pid={otherPid} — skipping");
                return;
            }

            // Take snapshot for ghost visuals
            ConfigNode ghostSnapshot = VesselSpawner.TryBackupSnapshot(otherVessel);

            // Create a committed recording for the continuation
            double ut = Planetarium.GetUniversalTime();
            Vector3 velocity = otherVessel.packed
                ? (Vector3)otherVessel.obt_velocity
                : (Vector3)(otherVessel.rb_velocityD + Krakensbane.GetFrameVelocity());

            var seedPoint = new TrajectoryPoint
            {
                ut = ut,
                latitude = otherVessel.latitude,
                longitude = otherVessel.longitude,
                altitude = otherVessel.altitude,
                rotation = otherVessel.srfRelRotation,
                velocity = velocity,
                bodyName = otherVessel.mainBody.name,
                // Phase 7: continuation seed — clearance NaN, playback uses legacy path.
                recordedGroundClearance = double.NaN
            };

            var contRec = new Recording
            {
                VesselName = Recording.ResolveLocalizedName(otherVessel.vesselName) + " (undock continuation)",
                ChainId = ActiveChainId,
                ChainIndex = ActiveChainNextIndex - 1, // same index as the player's new segment
                ChainBranch = 1, // parallel branch — ghost-only, never spawns
                GhostVisualSnapshot = ghostSnapshot,
                RecordingId = Guid.NewGuid().ToString("N"),
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion
            };
            contRec.Points.Add(seedPoint);
            contRec.ContinuationBoundaryIndex = 0; // Bug #95: entire recording is continuation data
            // Mark dirty so the newly-created continuation gets its .prec
            // written on the next OnSave. Without this the recording is
            // added to the committed list with an empty sidecar file.
            contRec.MarkFilesDirty();

            RecordingStore.AddCommittedInternal(contRec);
            UndockContinuationRecIdx = RecordingStore.CommittedRecordings.Count - 1;
            UndockContinuationRecId = contRec.RecordingId;
            UndockContinuationPid = otherPid;
            UndockContinuationLastVel = velocity;
            UndockContinuationLastUT = ut;

            ParsekLog.Verbose("Chain", $"Undock continuation started: tracking vessel pid={otherPid} " +
                $"in recording #{UndockContinuationRecIdx} (chain={ActiveChainId}, branch=1)");
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

        #region Commit Methods (Group B + CommitSegmentCore)

        /// <summary>
        /// Core commit pattern shared by all chain commit methods: stash, tag with chain
        /// metadata, commit, reserve crew, and optionally advance chain state.
        /// Returns false if the segment was too short (abort path — no chain state modified).
        /// </summary>
        private bool CommitSegmentCore(FlightRecorder segmentRecorder, string vesselName,
            Action<Recording> preCommitCustomization, bool advanceChain = true)
        {
            var captured = segmentRecorder.CaptureAtStop;
            string segmentId = captured?.RecordingId;
            int committedIndex = ActiveChainNextIndex;
            ParsekLog.Verbose("Chain",
                $"CommitSegmentCore: creating segment (id={segmentId}, chain={ActiveChainId}, " +
                $"idx={committedIndex}, points={segmentRecorder.Recording.Count})");

            var rec = RecordingStore.CreateRecordingFromFlightData(
                segmentRecorder.Recording,
                vesselName,
                segmentRecorder.OrbitSegments,
                recordingId: segmentId,
                recordingFormatVersion: captured != null ? (int?)captured.RecordingFormatVersion : null,
                partEvents: segmentRecorder.PartEvents,
                flagEvents: segmentRecorder.FlagEvents);

            if (rec == null)
            {
                ParsekLog.Verbose("Chain", "CommitSegmentCore: segment too short — aborting");
                return false;
            }

            if (captured != null)
                rec.ApplyPersistenceArtifactsFrom(captured);

            // Tag with tree ownership so the recording belongs to the active tree
            rec.TreeId = ActiveTreeId;

            // First transition: initialize chain
            if (ActiveChainId == null)
            {
                ActiveChainId = Guid.NewGuid().ToString("N");
                ActiveChainNextIndex = 0;
                committedIndex = 0;
                // Auto-group chain under a group named after the starting vessel.
                // Use GenerateUniqueGroupName to avoid merging multiple launches into one group (bug #104).
                string chainGroupName = RecordingStore.GenerateUniqueGroupName(
                    rec.VesselName ?? "Chain");
                rec.RecordingGroups = new List<string> { chainGroupName };
                RecordingStore.MarkAutoAssignedStandaloneGroup(rec, chainGroupName);
                ParsekLog.Verbose("Chain", $"CommitSegmentCore: started new chain (id={ActiveChainId}, group='{chainGroupName}')");
            }

            // Tag segment with chain metadata
            rec.ChainId = ActiveChainId;
            rec.ChainIndex = ActiveChainNextIndex;
            rec.ParentRecordingId = ActiveChainPrevId;

            // Apply caller-specific customization
            preCommitCustomization?.Invoke(rec);

            string recId = rec.RecordingId;
            double startUT = rec.StartUT;
            double endUT = rec.EndUT;
            RecordingStore.CommitRecordingDirect(rec);
            RecordingStore.RunOptimizationPass();
            double ledgerStartUT = LedgerOrchestrator.ResolveStandaloneCommitWindowStartUt(rec, startUT);
            int pendingBefore = GameStateRecorder.PendingScienceSubjects.Count;
            IReadOnlyList<PendingScienceSubject> pendingForCommit =
                LedgerOrchestrator.BuildPendingScienceSubsetForRecording(
                    GameStateRecorder.PendingScienceSubjects,
                    recId,
                    ledgerStartUT,
                    endUT);
            bool commitSucceeded = false;
            bool scienceAddedToLedger = false;
            try
            {
                LedgerOrchestrator.OnRecordingCommitted(
                    recId,
                    ledgerStartUT,
                    endUT,
                    pendingForCommit,
                    ref scienceAddedToLedger);
                commitSucceeded = true;
            }
            finally
            {
                LedgerOrchestrator.FinalizeScopedPendingScienceCommit(
                    "Chain",
                    "CommitSegmentCore",
                    pendingBefore,
                    pendingForCommit,
                    commitSucceeded,
                    scienceAddedToLedger);
            }
            // #390: prune consumed events after milestone creation + ledger conversion
            GameStateStore.PruneProcessedEvents();

            CrewReservationManager.SwapReservedCrewInFlight();

            if (advanceChain)
            {
                // Prepare boundary anchor from last point of committed segment
                if (segmentRecorder.Recording.Count > 0)
                    PendingBoundaryAnchor = segmentRecorder.Recording[segmentRecorder.Recording.Count - 1];

                // Advance chain state
                ActiveChainNextIndex++;
                ActiveChainPrevId = segmentId;
            }

            ParsekLog.Verbose("Chain",
                $"CommitSegmentCore: committed segment (id={segmentId}, chain={ActiveChainId}, " +
                $"idx={committedIndex}, totalRecordings={RecordingStore.CommittedRecordings.Count})");
            return true;
        }

        /// <summary>
        /// Commits the current chain segment and advances chain state.
        /// Sets up boundary anchor for the next segment.
        /// Mid-chain segments have VesselSnapshot nulled (ghost-only) because the recording
        /// ends at EVA, not at the vessel's actual final position.
        /// Returns false on abort (segment too short).
        /// </summary>
        internal bool CommitChainSegment(FlightRecorder segmentRecorder, string evaCrewName)
        {
            ParsekLog.Verbose("Chain",
                $"CommitChainSegment: committing segment (id={segmentRecorder.CaptureAtStop.RecordingId}, " +
                $"chainIdx={ActiveChainNextIndex})");

            bool committed = CommitSegmentCore(segmentRecorder,
                segmentRecorder.CaptureAtStop.VesselName,
                pending =>
                {
                    pending.EvaCrewName = evaCrewName;
                });

            if (!committed)
            {
                // Abort: clean up chain continuation state
                PendingContinuation = false;
                PendingEvaName = null;
                return false;
            }

            ParsekLog.Verbose("Chain",
                $"CommitChainSegment: VesselSnapshot={RecordingStore.CommittedRecordings[RecordingStore.CommittedRecordings.Count - 1].VesselSnapshot != null}, " +
                $"GhostVisualSnapshot={RecordingStore.CommittedRecordings[RecordingStore.CommittedRecordings.Count - 1].GhostVisualSnapshot != null}");

            // Continuation sampling: track the vessel after mid-chain commit
            if (!segmentRecorder.RecordingStartedAsEva)
            {
                // Vessel segment committed (V→EVA): start continuation to extend trajectory
                ContinuationVesselPid = segmentRecorder.RecordingVesselId;
                ContinuationRecordingIdx = RecordingStore.CommittedRecordings.Count - 1;
                ContinuationRecordingId = RecordingStore.CommittedRecordings[ContinuationRecordingIdx].RecordingId;
                // Bug #95: save boundary for rollback on revert.
                // Deep-copy snapshots because RefreshContinuationSnapshotCore path B
                // mutates the existing ConfigNode in place (SetValue on lat/lon/alt).
                var contRec = RecordingStore.CommittedRecordings[ContinuationRecordingIdx];
                contRec.ContinuationBoundaryIndex = contRec.Points.Count;
                contRec.PreContinuationVesselSnapshot = contRec.VesselSnapshot?.CreateCopy();
                contRec.PreContinuationGhostSnapshot = contRec.GhostVisualSnapshot?.CreateCopy();
                var lastPoints = contRec.Points;
                if (lastPoints.Count > 0)
                {
                    ContinuationLastVelocity = lastPoints[lastPoints.Count - 1].velocity;
                    ContinuationLastUT = lastPoints[lastPoints.Count - 1].ut;
                }
                else
                {
                    ContinuationLastVelocity = Vector3.zero;
                    ContinuationLastUT = -1;
                }
                ParsekLog.Verbose("Chain", $"Continuation started: tracking vessel pid={ContinuationVesselPid} " +
                    $"in recording #{ContinuationRecordingIdx}");
            }
            else if (ContinuationVesselPid != 0)
            {
                // EVA segment committed during boarding (EVA→V): bake + stop continuation.
                // Bug #95: Do NOT null VesselSnapshot on committed recordings.
                // The next chain segment handles spawning, but after revert the snapshot
                // is needed for re-spawn. VesselSnapshot is immutable after commit.
                if (TryGetContinuationRecording(out var boardingRec))
                    BakeContinuationData(boardingRec);
                ParsekLog.Verbose("Chain", $"Continuation stopped (boarding): " +
                    $"VesselSnapshot preserved on recording #{ContinuationRecordingIdx} " +
                    $"(snapshot={RecordingStore.CommittedRecordings[ContinuationRecordingIdx].VesselSnapshot != null})");
                StopContinuation("boarding");
            }

            return true;
        }

        /// <summary>
        /// Commits the current segment as a dock/undock chain boundary.
        /// Initializes the chain if needed, tags segment metadata, and advances chain state.
        /// Returns false on abort (segment too short).
        /// </summary>
        internal bool CommitDockUndockSegment(FlightRecorder segmentRecorder, PartEventType type, uint dockPortPid)
        {
            ParsekLog.Verbose("Chain",
                $"CommitDockUndockSegment: committing segment (id={segmentRecorder.CaptureAtStop.RecordingId}, event={type})");

            bool committed = CommitSegmentCore(segmentRecorder,
                segmentRecorder.CaptureAtStop.VesselName,
                pending =>
                {
                    pending.ChainBranch = 0; // always primary path
                    pending.EvaCrewName = null; // vessel segment, not EVA

                    // Capture dock target vessel PID for Phase 12 route analysis.
                    // dockPortPid is actually the merged vessel PID (confusingly named) —
                    // see ParsekFlight.HandleDockUndockCommitRestart where pendingDockMergedPid
                    // (= data.to.vessel.persistentId) is passed as this parameter.
                    if (type == PartEventType.Docked && dockPortPid != 0)
                    {
                        pending.DockTargetVesselPid = dockPortPid;
                        ParsekLog.Verbose("Chain",
                            $"CommitDockUndockSegment: captured dock target vessel pid={dockPortPid}");
                    }

                    // Add dock/undock part event to the segment
                    if (segmentRecorder.Recording.Count > 0)
                    {
                        double lastUT = segmentRecorder.Recording[segmentRecorder.Recording.Count - 1].ut;
                        pending.PartEvents.Add(new PartEvent
                        {
                            ut = lastUT,
                            partPersistentId = dockPortPid,
                            eventType = type,
                            partName = type.ToString()
                        });
                    }

                    // Mid-chain segments are ghost-only (VesselSnapshot nulled)
                    pending.VesselSnapshot = null;
                });

            return committed;
        }

        /// <summary>
        /// Commits the current segment as an atmosphere boundary chain split.
        /// Returns false on abort (segment too short).
        /// </summary>
        internal bool CommitBoundarySplit(FlightRecorder segmentRecorder, string completedPhase, string bodyName)
        {
            var captured = segmentRecorder.CaptureAtStop;
            string vesselName = captured != null
                ? captured.VesselName
                : (Recording.ResolveLocalizedName(FlightGlobals.ActiveVessel?.vesselName) ?? "Unknown");

            ParsekLog.Info("Chain", $"Boundary split: committing segment " +
                $"(id={captured?.RecordingId}, phase={completedPhase}, body={bodyName}, " +
                $"points={segmentRecorder.Recording.Count}, orbits={segmentRecorder.OrbitSegments.Count})");

            bool committed = CommitSegmentCore(segmentRecorder, vesselName, pending =>
            {
                pending.SegmentPhase = completedPhase;
                pending.SegmentBodyName = bodyName;
                pending.ChainBranch = 0;
                pending.EvaCrewName = ActiveChainCrewName;
                pending.VesselSnapshot = null; // Mid-chain segments are ghost-only
            });

            if (!committed)
            {
                ParsekLog.Warn("Chain", "Boundary split: segment too short — aborting " +
                    $"(points={segmentRecorder.Recording.Count})");
            }

            return committed;
        }

        /// <summary>
        /// Commits the final chain segment on vessel switch (no continuation possible).
        /// Returns false on abort (segment too short). On abort, chain state is cleared.
        /// </summary>
        internal bool CommitVesselSwitchTermination(FlightRecorder segmentRecorder)
        {
            var captured = segmentRecorder.CaptureAtStop;
            string segmentId = captured.RecordingId;
            ParsekLog.Info("Chain", $"Vessel-switch chain termination: committing final segment " +
                $"(id={segmentId}, chain={ActiveChainId}, idx={ActiveChainNextIndex}, " +
                $"points={segmentRecorder.Recording.Count}, orbits={segmentRecorder.OrbitSegments.Count})");

            bool committed = CommitSegmentCore(segmentRecorder,
                captured.VesselName ?? "Unknown",
                pending =>
                {
                    pending.ChainBranch = 0;
                    pending.EvaCrewName = ActiveChainCrewName;
                    pending.VesselPersistentId = segmentRecorder.RecordingVesselId;

                    // Derive segment phase/body from the recorded vessel (not ActiveVessel which changed)
                    Vessel recordedVessel = FlightRecorder.FindVesselByPid(segmentRecorder.RecordingVesselId);
                    if (recordedVessel != null && recordedVessel.mainBody != null)
                    {
                        pending.SegmentBodyName = recordedVessel.mainBody.name;
                        if (recordedVessel.situation == Vessel.Situations.LANDED
                            || recordedVessel.situation == Vessel.Situations.SPLASHED
                            || recordedVessel.situation == Vessel.Situations.PRELAUNCH)
                        {
                            pending.SegmentPhase = "surface";
                        }
                        else if (recordedVessel.mainBody.atmosphere)
                            pending.SegmentPhase = recordedVessel.altitude < recordedVessel.mainBody.atmosphereDepth ? "atmo" : "exo";
                        else
                        {
                            double threshold = FlightRecorder.ComputeApproachAltitude(recordedVessel.mainBody);
                            pending.SegmentPhase = recordedVessel.altitude < threshold ? "approach" : "exo";
                        }

                        pending.SceneExitSituation = (int)recordedVessel.situation;
                        pending.TerminalStateValue =
                            RecordingTree.DetermineTerminalState((int)recordedVessel.situation, recordedVessel);
                        ParsekFlight.CaptureTerminalOrbit(pending, recordedVessel);
                    }
                    // Final chain segment keeps VesselSnapshot for spawning (not ghost-only)
                },
                advanceChain: false);

            if (!committed)
            {
                ParsekLog.Warn("Chain", "Vessel-switch chain termination: segment too short — " +
                    $"aborting, clearing chain state (points={segmentRecorder.Recording.Count})");
                ClearChainIdentity();
                return false;
            }

            // Clean up all continuation sampling if active
            if (ContinuationVesselPid != 0)
            {
                RefreshContinuationSnapshot();
                if (TryGetContinuationRecording(out var contRec2))
                    BakeContinuationData(contRec2);
                StopContinuation("vessel-switch chain termination");
            }
            if (UndockContinuationPid != 0)
            {
                if (TryGetUndockContinuationRecording(out var undockRec2))
                    BakeContinuationData(undockRec2);
                StopUndockContinuation("vessel-switch chain termination");
            }

            // Terminate chain — no continuation possible after vessel switch
            ParsekLog.Info("Chain", $"Chain terminated by vessel switch: chain={ActiveChainId}, " +
                $"finalIdx={ActiveChainNextIndex}, segment={segmentId}, " +
                $"totalRecordings={RecordingStore.CommittedRecordings.Count}");
            ClearChainIdentity();

            return true;
        }

        #endregion
    }
}
