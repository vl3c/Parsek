using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    // The focused recorder's delta-v snapshots (design-mission-profile.md section 8.1): the
    // triggers, the per-tick settle check and the stop read. Every decision is in the pure
    // DeltaVSnapshotTracker / DeltaVSnapshotList; this file holds the live reads and the write
    // into the tree recording.
    public partial class FlightRecorder
    {
        private readonly DeltaVSnapshotTracker deltaVTracker = new DeltaVSnapshotTracker();

        // Set by StartRecording. Until then no delta-v code touches the game, so a recorder a
        // test drives without a live vessel never reaches a live read.
        private bool deltaVTrackingActive;
        private bool deltaVVesselEligible;
        private string deltaVTargetRecordingId;
        private int deltaVWritten;
        private bool deltaVCapWarned;
        private DeltaVSnapshot lastStopDeltaVSnapshot;

        /// <summary>
        /// The trigger a promotion start (<c>StartRecording(isPromotion: true)</c>) records:
        /// the split, merge, switch or resume that bound this recorder. A fresh start always
        /// records <see cref="DeltaVTrigger.Start"/>.
        /// </summary>
        internal DeltaVTrigger DeltaVStartTrigger { get; set; } = DeltaVTrigger.SwitchIn;

        /// <summary>The tree recording this recorder writes its snapshots into.</summary>
        internal string DeltaVTargetRecordingId => deltaVTargetRecordingId;

        /// <summary>Pure: the start trigger of the focused child a branch point binds.</summary>
        internal static DeltaVTrigger DeltaVTriggerForBranch(BranchPointType branchType)
        {
            switch (branchType)
            {
                case BranchPointType.Undock: return DeltaVTrigger.Undock;
                case BranchPointType.Dock: return DeltaVTrigger.Dock;
                case BranchPointType.Board: return DeltaVTrigger.Board;
                case BranchPointType.EVA: return DeltaVTrigger.Eva;
                case BranchPointType.JointBreak:
                case BranchPointType.Breakup:
                    return DeltaVTrigger.Separation;
                default:
                    return DeltaVTrigger.SwitchIn;
            }
        }

        /// <summary>Called at the end of <see cref="StartRecording"/>, after the part tracking reset.</summary>
        private void BeginDeltaVTracking(Vessel v, bool isPromotion)
        {
            deltaVTrackingActive = true;
            deltaVVesselEligible = v != null && !v.isEVA;
            deltaVTargetRecordingId = ActiveTree?.ActiveRecordingId;
            lastStopDeltaVSnapshot = null;
            deltaVTracker.InitializeEngineActivity(activeEngineKeys != null && activeEngineKeys.Count > 0);
            RequestDeltaVSnapshot(isPromotion ? DeltaVStartTrigger : DeltaVTrigger.Start,
                isPromotion ? "promotion-start" : "record-start");
        }

        /// <summary>
        /// Files a snapshot request (staging, the start, a burn edge). The read happens on a
        /// later physics tick, once the game has settled.
        /// </summary>
        internal void RequestDeltaVSnapshot(DeltaVTrigger trigger, string source)
        {
            if (!deltaVTrackingActive)
                return;

            string reason = DeltaVSnapshotTracker.ClassifyRequest(
                IsRecording, ReadDeltaVCalculationsEnabled(), !deltaVVesselEligible);
            if (reason == null)
                reason = deltaVTracker.Request(trigger, ReadDeltaVClockSeconds());

            if (reason != null)
            {
                ParsekLog.VerboseRateLimited("DeltaV", "request-skip|" + reason + "|" + trigger,
                    $"Snapshot request skipped: rec={deltaVTargetRecordingId ?? "(none)"} " +
                    $"trigger={trigger} source={source ?? "(none)"} reason={reason}",
                    5.0);
                return;
            }

            ParsekLog.Verbose("DeltaV",
                $"Snapshot requested: rec={deltaVTargetRecordingId ?? "(none)"} trigger={trigger} " +
                $"source={source ?? "(none)"} pending={deltaVTracker.PendingCount.ToString(CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Per physics tick: turns an engine on / off edge into a burn request, then settles
        /// the pending requests. Runs on rails too (the stock calculation still runs there).
        /// </summary>
        private void TickDeltaVSnapshots(Vessel v)
        {
            if (!deltaVTrackingActive || v == null || v.persistentId != RecordingVesselId)
                return;

            DeltaVTrigger? burnEdge = deltaVTracker.NoteEngineActivity(
                activeEngineKeys != null && activeEngineKeys.Count > 0);
            if (burnEdge.HasValue)
                RequestDeltaVSnapshot(burnEdge.Value, "engine-activity");

            if (deltaVTracker.PendingCount == 0)
                return;

            DeltaVReadiness readiness = ReadDeltaVReadiness(v);
            List<DeltaVDecision> decisions = deltaVTracker.Decide(ReadDeltaVClockSeconds(), readiness);
            if (decisions.Count == 0)
                return;

            double ut = ReadDeltaVUniversalTime();
            bool haveRead = false;
            bool readOk = false;
            DeltaVReading reading = default(DeltaVReading);
            for (int i = 0; i < decisions.Count; i++)
            {
                DeltaVDecision decision = decisions[i];
                DeltaVSnapshot snapshot;
                switch (decision.Outcome)
                {
                    case DeltaVRequestOutcome.Complete:
                        if (!haveRead)
                        {
                            readOk = TryReadLiveDeltaV(v, out reading)
                                && DeltaVSnapshotTracker.IsUsableReading(reading);
                            haveRead = true;
                        }
                        snapshot = readOk
                            ? DeltaVSnapshot.FromReading(ut, decision.Trigger, reading)
                            : DeltaVSnapshot.Unknown(ut, decision.Trigger);
                        break;
                    case DeltaVRequestOutcome.TimedOut:
                        snapshot = DeltaVSnapshot.Unknown(ut, decision.Trigger);
                        break;
                    default:
                        ParsekLog.Verbose("DeltaV",
                            $"Snapshot dropped: rec={deltaVTargetRecordingId ?? "(none)"} " +
                            $"trigger={decision.Trigger} reason=calculations-disabled");
                        continue;
                }
                WriteDeltaVSnapshot(snapshot, decision.SettleTicks, decision.WaitedSeconds);
            }
        }

        /// <summary>
        /// The end of the recording reads at once (no later frame): Ok when the game has
        /// figures for the recorded vessel, Unknown otherwise. Requests still pending are kept,
        /// so a chain-boundary stop that resumes (a debris-only split) still completes them.
        /// </summary>
        private void CaptureDeltaVSnapshotAtStop(Vessel recordedVessel, double stopUT, string logTag)
        {
            lastStopDeltaVSnapshot = null;
            if (!deltaVTrackingActive || !IsRecording)
                return;

            if (deltaVVesselEligible)
            {
                // No FlightGlobals.ready gate here: stock clears it in FlightGlobals.OnSceneChange,
                // which runs on the same onGameSceneLoadRequested that stops the recorder at scene
                // exit, and the End read takes the last computed figures rather than a new run.
                DeltaVReadiness readiness = ReadDeltaVReadiness(recordedVessel, requireFlightReady: false);
                DeltaVRequestOutcome outcome = DeltaVSnapshotTracker.DecideAtStop(readiness);
                if (outcome != DeltaVRequestOutcome.Dropped)
                {
                    DeltaVSnapshot snapshot = DeltaVSnapshot.Unknown(stopUT, DeltaVTrigger.End);
                    if (outcome == DeltaVRequestOutcome.Complete
                        && TryReadLiveDeltaV(recordedVessel, out DeltaVReading reading)
                        && DeltaVSnapshotTracker.IsUsableReading(reading))
                    {
                        snapshot = DeltaVSnapshot.FromReading(stopUT, DeltaVTrigger.End, reading);
                    }
                    if (WriteDeltaVSnapshot(snapshot, 0, 0.0))
                        lastStopDeltaVSnapshot = snapshot;
                }
            }

            LogDeltaVStopSummary(logTag, deltaVTracker.PendingCount);
        }

        /// <summary>
        /// Ends tracking without an End snapshot: focus left the recorded vessel (a vessel
        /// switch or the move to the background recorder), so stock has no figures for it and
        /// this span is not the recording's end. Pending requests are dropped.
        /// </summary>
        private void EndDeltaVTracking(string logTag)
        {
            if (!deltaVTrackingActive)
                return;
            int dropped = deltaVTracker.PendingCount;
            deltaVTrackingActive = false;
            lastStopDeltaVSnapshot = null;
            LogDeltaVStopSummary(logTag, 0);
            if (dropped > 0)
                ParsekLog.Verbose("DeltaV",
                    $"Tracking ended ({logTag}): dropped {dropped.ToString(CultureInfo.InvariantCulture)} " +
                    $"pending request(s) for rec={deltaVTargetRecordingId ?? "(none)"}");
        }

        private void LogDeltaVStopSummary(string logTag, int pendingKept)
        {
            Recording target = ResolveDeltaVTargetRecording();
            List<DeltaVSnapshot> list = target?.DeltaVSnapshots;
            ParsekLog.Info("DeltaV",
                FormatDeltaVStopSummary(
                    deltaVTargetRecordingId, logTag, list?.Count ?? 0,
                    DeltaVSnapshotList.CountUnknown(list), deltaVWritten,
                    pendingKept, deltaVVesselEligible));
        }

        /// <summary>
        /// A chain-boundary stop that turns out to be a false alarm resumes this recording, so
        /// the End snapshot that stop wrote is not its end.
        /// </summary>
        private void RemoveDeltaVStopSnapshotForResume()
        {
            DeltaVSnapshot stopSnapshot = lastStopDeltaVSnapshot;
            lastStopDeltaVSnapshot = null;
            if (stopSnapshot == null)
                return;
            Recording target = ResolveDeltaVTargetRecording();
            if (target == null || !DeltaVSnapshotList.RemoveByReference(target.DeltaVSnapshots, stopSnapshot))
                return;
            if (target.DeltaVSnapshots.Count == 0)
                target.DeltaVSnapshots = null;
            deltaVWritten--;
            ParsekLog.Verbose("DeltaV",
                $"Resume after false alarm: removed the stop snapshot of rec={deltaVTargetRecordingId} " +
                $"ut={stopSnapshot.UT.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        private Recording ResolveDeltaVTargetRecording()
        {
            if (string.IsNullOrEmpty(deltaVTargetRecordingId) || ActiveTree?.Recordings == null)
                return null;
            return ActiveTree.Recordings.TryGetValue(deltaVTargetRecordingId, out Recording rec) ? rec : null;
        }

        private bool WriteDeltaVSnapshot(DeltaVSnapshot snapshot, int settleTicks, double waitedSeconds)
        {
            Recording target = ResolveDeltaVTargetRecording();
            if (target == null)
            {
                ParsekLog.VerboseRateLimited("DeltaV", "no-target",
                    $"Snapshot not written: tree recording '{deltaVTargetRecordingId ?? "(none)"}' not found " +
                    $"(trigger={snapshot.Trigger})", 5.0);
                return false;
            }

            if (!DeltaVSnapshotList.TryInsertByUT(ref target.DeltaVSnapshots, snapshot))
            {
                if (!deltaVCapWarned)
                {
                    deltaVCapWarned = true;
                    ParsekLog.Warn("DeltaV",
                        $"Snapshot cap reached: rec={deltaVTargetRecordingId} holds " +
                        $"{DeltaVSnapshotList.MaxSnapshotsPerRecording.ToString(CultureInfo.InvariantCulture)} " +
                        $"snapshot(s); later ones are not recorded (first dropped trigger={snapshot.Trigger})");
                }
                return false;
            }

            deltaVWritten++;
            ParsekLog.Verbose("DeltaV",
                FormatDeltaVSnapshotLog(deltaVTargetRecordingId, snapshot, settleTicks, waitedSeconds));
            return true;
        }

        /// <summary>Pure: the per-snapshot Verbose line.</summary>
        internal static string FormatDeltaVSnapshotLog(
            string recordingId, DeltaVSnapshot snapshot, int settleTicks, double waitedSeconds)
        {
            var ic = CultureInfo.InvariantCulture;
            return string.Format(ic,
                "Snapshot: rec={0} trigger={1} ut={2} status={3} vac={4} stages={5} settleTicks={6} waited={7}s",
                recordingId ?? "(none)",
                snapshot.Trigger,
                snapshot.UT.ToString("F2", ic),
                snapshot.Status,
                snapshot.IsOk ? snapshot.TotalVacuum.ToString("F1", ic) : "-",
                snapshot.Stages.Count.ToString(ic),
                settleTicks.ToString(ic),
                waitedSeconds.ToString("F2", ic));
        }

        /// <summary>Pure: the per-stop Info summary.</summary>
        internal static string FormatDeltaVStopSummary(
            string recordingId, string logTag, int snapshots, int unknown, int writtenByRecorder,
            int pendingKept, bool vesselEligible)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Recording {0} stopped ({1}): snapshots={2} unknown={3} writtenByRecorder={4} pendingKept={5}{6}",
                recordingId ?? "(none)", logTag ?? "(none)", snapshots, unknown, writtenByRecorder,
                pendingKept, vesselEligible ? "" : " (EVA kerbal: none recorded)");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadDeltaVCalculationsEnabled()
        {
            return GameSettings.DELTAV_CALCULATIONS_ENABLED;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadDeltaVClockSeconds()
        {
            return Time.time;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadDeltaVUniversalTime()
        {
            return Planetarium.GetUniversalTime();
        }

        /// <summary>
        /// Live: whether stock has figures for <paramref name="v"/>. The stock calculation runs
        /// only for the loaded active vessel while <c>FlightGlobals.ready</c>
        /// (<c>VesselDeltaV.CheckDirtyAndRun</c>, KSP 1.12.5). The stop read passes
        /// <paramref name="requireFlightReady"/> false: it reads the last computed figures.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static DeltaVReadiness ReadDeltaVReadiness(Vessel v, bool requireFlightReady = true)
        {
            var readiness = new DeltaVReadiness
            {
                CalculationsEnabled = GameSettings.DELTAV_CALCULATIONS_ENABLED,
                EventDelaySeconds = GameSettings.DELTAV_VESSEL_EVENT_DELAY_SECS,
            };
            try
            {
                bool active = v != null && v.loaded && !v.isEVA
                    && (!requireFlightReady || FlightGlobals.ready)
                    && ReferenceEquals(FlightGlobals.ActiveVessel, v);
                VesselDeltaV dv = active ? v.VesselDeltaV : null;
                readiness.VesselEligible = dv != null;
                readiness.Ready = dv != null && dv.IsReady && !dv.SimulationRunning;
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited("DeltaV", "readiness-threw",
                    $"Readiness read threw {ex.GetType().Name}: {ex.Message}", 5.0);
                readiness.VesselEligible = false;
                readiness.Ready = false;
            }
            return readiness;
        }

        /// <summary>Live: one read of the stock Delta-V figures of <paramref name="v"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadLiveDeltaV(Vessel v, out DeltaVReading reading)
        {
            reading = default(DeltaVReading);
            try
            {
                VesselDeltaV dv = v != null ? v.VesselDeltaV : null;
                if (dv == null)
                    return false;
                reading.TotalVacuum = dv.TotalDeltaVVac;
                reading.TotalSeaLevel = dv.TotalDeltaVASL;
                reading.TotalActual = dv.TotalDeltaVActual;
                reading.Stages = new List<DeltaVStageReading>();
                List<DeltaVStageInfo> stages = dv.OperatingStageInfo;
                if (stages != null)
                {
                    for (int i = 0; i < stages.Count; i++)
                    {
                        DeltaVStageInfo info = stages[i];
                        if (info == null)
                            continue;
                        reading.Stages.Add(new DeltaVStageReading(
                            info.stage, info.deltaVinVac, info.stageBurnTime));
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited("DeltaV", "read-threw",
                    $"Delta-v read threw {ex.GetType().Name}: {ex.Message}", 5.0);
                return false;
            }
        }
    }
}
