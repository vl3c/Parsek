using System;
using System.Collections;
using System.Collections.Generic;

namespace Parsek
{
    public partial class ParsekFlight
    {
        #region Terminal Event Detection (Destruction)

        /// <summary>
        /// Pure decision method: determines whether a deferred destruction check should be started.
        /// Returns true if the vessel is in the BackgroundMap, not in dockingInProgress, and a tree exists.
        /// </summary>
        internal static bool ShouldDeferDestructionCheck(
            uint vesselPid,
            bool hasTree,
            HashSet<uint> dockingInProgress,
            Dictionary<uint, string> backgroundMap)
        {
            if (!hasTree) return false;
            if (dockingInProgress.Contains(vesselPid)) return false;
            return backgroundMap.ContainsKey(vesselPid);
        }

        internal enum DestructionMode { None, TreeDeferred, TreeAllLeavesCheck }

        /// <summary>
        /// Pure classification of vessel destruction handling mode.
        /// Matches the branching order in OnVesselWillDestroy: TreeDeferred is checked first,
        /// then TreeAllLeavesCheck (tree with active vessel).
        ///
        /// TreeDeferred requires shouldDeferForTree (vessel in BackgroundMap),
        /// while TreeAllLeavesCheck requires isActiveVessel. The active vessel
        /// is never in BackgroundMap, so at most one branch fires.
        ///
        /// TreeAllLeavesCheck intentionally does not require isRecording — the original checked
        /// recorder != null (embedded in vesselDestroyedDuringRecording) but not IsRecording.
        /// </summary>
        internal static DestructionMode ClassifyVesselDestruction(
            bool hasActiveTree,
            bool isRecording,
            bool vesselDestroyedDuringRecording,
            bool isActiveVessel,
            bool shouldDeferForTree,
            bool treeDestructionDialogPending)
        {
            if (hasActiveTree && shouldDeferForTree)
                return DestructionMode.TreeDeferred;

            if (hasActiveTree && vesselDestroyedDuringRecording && isActiveVessel
                && !treeDestructionDialogPending)
                return DestructionMode.TreeAllLeavesCheck;

            return DestructionMode.None;
        }

        internal enum PostDestructionMergeResolution
        {
            FinalizeNow,
            WaitForPendingCrashResolution,
            AbortAndKeepRecording,
        }

        internal static bool HasPendingPostDestructionCrashResolution(
            bool activeDestroyed,
            bool pendingSplitInProgress,
            bool hasPendingBreakup)
        {
            return activeDestroyed && (pendingSplitInProgress || hasPendingBreakup);
        }

        internal static PostDestructionMergeResolution ClassifyPostDestructionMergeResolution(
            bool activeDestroyed,
            bool allLeavesTerminal,
            bool onlyDebrisBlockersRemain,
            bool pendingCrashResolution)
        {
            if (activeDestroyed && pendingCrashResolution)
                return PostDestructionMergeResolution.WaitForPendingCrashResolution;

            if (allLeavesTerminal || (activeDestroyed && onlyDebrisBlockersRemain))
                return PostDestructionMergeResolution.FinalizeNow;

            return PostDestructionMergeResolution.AbortAndKeepRecording;
        }

        /// <summary>
        /// Pure decision method: determines whether a vessel is truly destroyed (not just unloaded
        /// or absorbed by docking) after the one-frame deferral.
        /// </summary>
        internal static bool IsTrulyDestroyed(
            uint vesselPid,
            HashSet<uint> dockingInProgress,
            bool vesselStillExists)
        {
            return ClassifyDeferredDestruction(vesselPid, dockingInProgress, vesselStillExists)
                == DeferredDestructionOutcome.ConfirmedDestroyed;
        }

        internal static DeferredDestructionOutcome ClassifyDeferredDestruction(
            uint vesselPid,
            HashSet<uint> dockingInProgress,
            bool vesselStillExists)
        {
            if (dockingInProgress != null && dockingInProgress.Contains(vesselPid))
                return DeferredDestructionOutcome.DockingInProgress;
            return vesselStillExists
                ? DeferredDestructionOutcome.FalseDestroyReattach
                : DeferredDestructionOutcome.ConfirmedDestroyed;
        }

        internal static bool ShouldReattachBackgroundRecorderAfterDeferredDestruction(
            DeferredDestructionOutcome outcome)
        {
            return outcome == DeferredDestructionOutcome.FalseDestroyReattach;
        }

        internal static bool TryHandleDeferredDestructionAbort(
            uint vesselPid,
            DeferredDestructionOutcome outcome,
            Action<uint> reattachBackgroundRecorder,
            Action<string> debugLog,
            Action<string> infoLog)
        {
            if (outcome == DeferredDestructionOutcome.ConfirmedDestroyed)
                return false;

            if (ShouldReattachBackgroundRecorderAfterDeferredDestruction(outcome))
            {
                debugLog?.Invoke(
                    $"DeferredDestructionCheck: pid={vesselPid} still exists — vessel unloaded, not destroyed");
                reattachBackgroundRecorder?.Invoke(vesselPid);
                infoLog?.Invoke(
                    $"DeferredDestructionCheck: reattached background recorder state for " +
                    $"pid={vesselPid} after false destruction signal");
            }
            else
            {
                debugLog?.Invoke(
                    $"DeferredDestructionCheck: pid={vesselPid} now in dockingInProgress — aborting");
            }

            return true;
        }

        /// <summary>
        /// Pure decision method: detects phantom terrain crashes. KSP sometimes crashes
        /// EVA vessels through terrain during pack/unload. Returns true if the vessel was
        /// in a safe situation (LANDED/SPLASHED) when packed and was destroyed within 5s.
        /// </summary>
        internal static bool IsPhantomTerrainCrash(
            string evaCrewName, double packUT, double destructionUT, Vessel.Situations prePackSituation)
        {
            if (string.IsNullOrEmpty(evaCrewName)) return false;
            bool wasSafe = prePackSituation == Vessel.Situations.LANDED
                        || prePackSituation == Vessel.Situations.SPLASHED;
            if (!wasSafe) return false;
            double elapsed = destructionUT - packUT;
            return elapsed >= 0 && elapsed < PhantomCrashWindowSeconds;
        }

        /// <summary>
        /// Pure decision method: if the vessel was destroyed during recording, ensures
        /// TerminalStateValue is Destroyed regardless of what situation-based inference produced.
        /// Returns true if the terminal state was overridden.
        /// </summary>
        internal static bool ApplyDestroyedFallback(
            bool wasDestroyed, Recording rec)
        {
            if (rec == null) return false;
            if (!wasDestroyed) return false;

            // Propagate the recorder's destruction knowledge to the VesselDestroyed
            // bool, which is the field the no-op auto-discard classifier reads
            // (SwitchSegmentNoOpClassifier gates on VesselDestroyed, not the
            // terminal). Without this, a destruction that does NOT also emit an
            // onPartDie Destroyed PartEvent (e.g. NaN/Kraken Vessel.Die cleanup)
            // would leave a destroyed resume looking like a boring no-op coast and
            // get auto-discarded. Set unconditionally (even when the terminal is
            // already Destroyed) so the bool can never drift from the terminal.
            rec.VesselDestroyed = true;

            if (rec.TerminalStateValue == TerminalState.Destroyed) return false;

            var prev = rec.TerminalStateValue;
            rec.TerminalStateValue = TerminalState.Destroyed;
            ParsekLog.Info("Flight",
                $"Finalization override: active-recorder destruction override for " +
                $"'{rec.RecordingId ?? "(null)"}' — overriding TerminalState " +
                $"from {prev?.ToString() ?? "null"} to Destroyed");
            return true;
        }

        /// <summary>
        /// Pure static method: applies terminal destruction state to a recording.
        /// Sets TerminalStateValue = Destroyed, ExplicitEndUT, and copies orbital/surface data.
        /// </summary>
        internal static void ApplyTerminalDestruction(
            PendingDestruction pending,
            Recording rec)
        {
            rec.TerminalStateValue = TerminalState.Destroyed;
            rec.ExplicitEndUT = pending.capturedUT;
            ApplyTerminalData(pending, rec);
            ParsekLog.Verbose("Flight", $"Applied terminal destruction to recording {rec.RecordingId}: Destroyed at UT={pending.capturedUT:F1}");
        }

        /// <summary>
        /// Pure static helper: copies orbital/surface data from a PendingDestruction to a recording.
        /// </summary>
        internal static void ApplyTerminalData(
            PendingDestruction data,
            Recording rec)
        {
            if (data.hasOrbit)
            {
                rec.TerminalOrbitInclination = data.inclination;
                rec.TerminalOrbitEccentricity = data.eccentricity;
                rec.TerminalOrbitSemiMajorAxis = data.semiMajorAxis;
                rec.TerminalOrbitLAN = data.lan;
                rec.TerminalOrbitArgumentOfPeriapsis = data.argumentOfPeriapsis;
                rec.TerminalOrbitMeanAnomalyAtEpoch = data.meanAnomalyAtEpoch;
                rec.TerminalOrbitEpoch = data.epoch;
                rec.TerminalOrbitBody = data.bodyName;
            }
            if (data.hasSurface)
            {
                rec.TerminalPosition = data.surfacePosition;
            }
            ParsekLog.Verbose("Flight", $"Applied terminal data to recording {rec.RecordingId}: orbit={data.hasOrbit}, surface={data.hasSurface}");
        }

        /// <summary>
        /// Phase 1 capture: extracts vessel state before destruction for deferred processing.
        /// Called synchronously from OnVesselWillDestroy while the Vessel object is still valid.
        /// Handles ORBITING/SUB_ORBITAL/ESCAPING (orbit capture), LANDED/SPLASHED/PRELAUNCH
        /// (surface capture), and FLYING (orbit as best-effort approximation).
        /// </summary>
        PendingDestruction CaptureVesselStateForTerminal(Vessel v, string recordingId)
        {
            var pending = new PendingDestruction
            {
                vesselPid = v.persistentId,
                recordingId = recordingId,
                capturedUT = Planetarium.GetUniversalTime(),
                situation = v.situation
            };

            switch (v.situation)
            {
                case Vessel.Situations.ORBITING:
                case Vessel.Situations.SUB_ORBITAL:
                case Vessel.Situations.ESCAPING:
                case Vessel.Situations.FLYING:
                    // Capture orbit data (FLYING uses orbit as best-effort approximation)
                    if (v.orbit != null)
                    {
                        pending.hasOrbit = true;
                        pending.inclination = v.orbit.inclination;
                        pending.eccentricity = v.orbit.eccentricity;
                        pending.semiMajorAxis = v.orbit.semiMajorAxis;
                        pending.lan = v.orbit.LAN;
                        pending.argumentOfPeriapsis = v.orbit.argumentOfPeriapsis;
                        pending.meanAnomalyAtEpoch = v.orbit.meanAnomalyAtEpoch;
                        pending.epoch = v.orbit.epoch;
                        pending.bodyName = v.mainBody?.name ?? "Kerbin";
                    }
                    break;

                case Vessel.Situations.LANDED:
                case Vessel.Situations.SPLASHED:
                case Vessel.Situations.PRELAUNCH:
                    // Capture surface position (PRELAUNCH treated as LANDED)
                    pending.hasSurface = true;
                    pending.surfacePosition = new SurfacePosition
                    {
                        body = v.mainBody?.name ?? "Kerbin",
                        latitude = v.latitude,
                        longitude = v.longitude,
                        altitude = v.altitude,
                        rotation = v.srfRelRotation,
                        situation = v.situation == Vessel.Situations.SPLASHED
                            ? SurfaceSituation.Splashed
                            : SurfaceSituation.Landed
                    };
                    break;
            }

            ParsekLog.Verbose("Flight", $"Captured terminal state for {v.vesselName}: situation={v.situation}, body={v.mainBody?.name ?? "unknown"}");
            return pending;
        }

        /// <summary>
        /// Deferred destruction check coroutine. After one frame, verifies whether the vessel
        /// was truly destroyed (not just unloaded or absorbed by docking). If confirmed destroyed,
        /// applies terminal state and removes from BackgroundMap.
        /// </summary>
        IEnumerator DeferredDestructionCheck(PendingDestruction pending)
        {
            yield return null; // defer one frame for KSP to finalize the destroy

            // Fix 2: activeTree could become null during scene change
            if (activeTree == null) yield break;

            // Fix 3: another handler (merge, promotion) may have already processed this vessel
            if (!activeTree.BackgroundMap.ContainsKey(pending.vesselPid)) yield break;

            // Use pure decision method to determine if vessel is truly destroyed
            bool vesselStillExists = FlightRecorder.FindVesselByPid(pending.vesselPid) != null;
            DeferredDestructionOutcome destructionOutcome = ClassifyDeferredDestruction(
                pending.vesselPid,
                dockingInProgress,
                vesselStillExists);
            if (TryHandleDeferredDestructionAbort(
                    pending.vesselPid,
                    destructionOutcome,
                    pid => backgroundRecorder?.OnVesselBackgrounded(pid),
                    Log,
                    message => ParsekLog.Info("Flight", message)))
            {
                yield break;
            }

            // Vessel is truly destroyed — apply terminal state
            Recording rec;
            if (!activeTree.Recordings.TryGetValue(pending.recordingId, out rec))
            {
                ParsekLog.Warn("Flight", $"DeferredDestructionCheck: recording '{pending.recordingId}' not found in tree");
                yield break;
            }

            // Phantom terrain crash detection: KSP sometimes crashes EVA vessels
            // through terrain during pack/unload. Detect by checking if the vessel
            // was in a safe situation (LANDED/SPLASHED) when packed and was destroyed
            // within a short time window. Override Destroyed → Landed/Splashed.
            bool isPhantomCrash = false;
            if (!string.IsNullOrEmpty(rec.EvaCrewName))
            {
                (double packUT, Vessel.Situations preSit) packState;
                if (packStates.TryGetValue(pending.vesselPid, out packState))
                {
                    isPhantomCrash = IsPhantomTerrainCrash(
                        rec.EvaCrewName, packState.packUT, pending.capturedUT, packState.preSit);
                    if (isPhantomCrash)
                    {
                        var safeTerm = packState.preSit == Vessel.Situations.LANDED
                            ? TerminalState.Landed : TerminalState.Splashed;
                        ParsekLog.Warn("Flight",
                            $"Suspected phantom terrain crash for EVA '{rec.VesselName}': " +
                            $"was {packState.preSit}, packed {pending.capturedUT - packState.packUT:F1}s " +
                            $"before destruction. Using {safeTerm} instead of Destroyed");
                        rec.TerminalStateValue = safeTerm;
                        rec.ExplicitEndUT = pending.capturedUT;
                        ApplyTerminalData(pending, rec);
                    }
                }
            }

            bool cacheApplied = false;
            if (!isPhantomCrash && backgroundRecorder != null)
            {
                RecordingFinalizationCacheApplyResult cacheResult;
                cacheApplied = backgroundRecorder.TryApplyFinalizationCacheForBackgroundEnd(
                    rec,
                    pending.vesselPid,
                    pending.capturedUT,
                    "DeferredDestructionCheck",
                    allowStale: true,
                    requireDestroyedTerminal: true,
                    confirmedDestroyed: true,
                    out cacheResult);
            }

            if (!isPhantomCrash && !cacheApplied)
                ApplyTerminalDestruction(pending, rec);

            packStates.Remove(pending.vesselPid);
            activeTree.BackgroundMap.Remove(pending.vesselPid);

            BackgroundRecorder.PersistFinalizedRecording(
                rec,
                $"DeferredDestructionCheck pid={pending.vesselPid}");
            backgroundRecorder?.ForgetFinalizationCache(pending.vesselPid);

            if (!string.IsNullOrEmpty(rec.EvaCrewName))
                ParsekLog.Info("Flight", $"Background EVA vessel ended: pid={pending.vesselPid} recId={pending.recordingId}");
            else
                ParsekLog.Warn("Flight", $"Background vessel destroyed: pid={pending.vesselPid} recId={pending.recordingId}");

            // Check if all tree vessels are now destroyed — trigger merge dialog
            if (!treeDestructionDialogPending)
            {
                bool activeDestroyed = recorder == null || !recorder.IsRecording
                    || recorder.VesselDestroyedDuringRecording;
                if (RecordingTree.AreAllLeavesTerminal(activeTree.Recordings,
                    activeTree.ActiveRecordingId, activeDestroyed))
                {
                    treeDestructionDialogPending = true;
                    ParsekLog.Info("Flight",
                        "All tree leaves now terminal after background destruction — triggering tree merge");
                    StartCoroutine(ShowPostDestructionTreeMergeDialog());
                }
            }
        }

        #endregion
    }
}
