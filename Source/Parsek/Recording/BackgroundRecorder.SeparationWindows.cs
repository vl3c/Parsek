using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // Background separation windows (owner ruling Q15, 2026-11-18, "one shape for both"): a
    // decouple or a broken-off part of a BACKGROUND vessel gives the tree shape a focused one
    // gives. The parent recording continues, and the pieces that come off inside one
    // coalescing window become children of ONE JointBreak branch point, exactly as
    // ParsekFlight.ProcessBreakupEvent writes them for the focused vessel.
    internal partial class BackgroundRecorder
    {
        #region Background Separation Windows

        // Open coalescing windows keyed by the background parent's vessel pid, with the
        // parent recording each window belongs to. A window is fed by the deferred new-vessel
        // scan (HandleBackgroundVesselSplit), ticked by ProcessPendingSplitChecks and emitted
        // early whenever the parent stops being a background vessel or the tree is saved,
        // committed or torn down (never drop one, owner ruling 2026-11-18).
        private readonly Dictionary<uint, CrashCoalescer> separationWindows
            = new Dictionary<uint, CrashCoalescer>();
        private readonly Dictionary<uint, string> separationWindowParentRecordingIds
            = new Dictionary<uint, string>();

        /// <summary>
        /// Pure: does a background split of this cause go through a separation window (the
        /// focused decouple / breakup shape: the parent continues)? A decouple and a part that
        /// broke off do; an undock does not (it closes the parent into two children, the
        /// focused undock shape). A null cause is a broken-off part.
        /// </summary>
        internal static bool IsCoalescedBackgroundSeparationCause(string splitCause)
            => !string.Equals(splitCause, "UNDOCK", StringComparison.Ordinal);

        /// <summary>
        /// Feeds the vessels a background split produced into the parent's separation window,
        /// capturing each one's snapshot and pose now (a piece can be destroyed before the
        /// window closes).
        /// </summary>
        private void FeedBackgroundSeparationWindow(
            uint parentPid,
            string parentRecordingId,
            double branchUT,
            string splitCause,
            List<(uint pid, string name, bool hasController)> newVesselInfos)
        {
            double sampleUT = Planetarium.GetUniversalTime();
            for (int i = 0; i < newVesselInfos.Count; i++)
            {
                var info = newVesselInfos[i];
                Vessel childVessel = FlightRecorder.FindVesselByPid(info.pid);
                ConfigNode preSnapshot = childVessel != null
                    ? FlightRecorder.TryBackupSnapshot(childVessel)
                    : null;
                TrajectoryPoint? seedPoint = childVessel != null
                    ? (TrajectoryPoint?)CreateAbsoluteTrajectoryPointFromVessel(
                        childVessel, sampleUT, preferRootPartSurfacePose: true)
                    : null;
                FeedBackgroundSeparationWindowCore(
                    parentPid, parentRecordingId, branchUT, splitCause,
                    info.pid, info.hasController, preSnapshot, seedPoint);
            }
        }

        /// <summary>
        /// Feeds one separated vessel into the parent's window, opening the window when none
        /// is open. A window still open for a different parent recording of the same vessel
        /// is emitted first (it belongs to that recording). Returns true when this call opened
        /// the window.
        /// </summary>
        internal bool FeedBackgroundSeparationWindowCore(
            uint parentPid,
            string parentRecordingId,
            double branchUT,
            string splitCause,
            uint childPid,
            bool childHasController,
            ConfigNode preSnapshot,
            TrajectoryPoint? seedPoint)
        {
            string cause = splitCause ?? SegmentBoundaryLogic.BrokeOffSplitCause;

            // Two background parents splitting in one physics step both see the other's new
            // vessel in their scans (a piece stays out of the BackgroundMap until its window
            // closes). The first window to hold a piece keeps it: one recording per vessel.
            uint holderPid;
            bool holderControlled;
            if (TryFindSeparationWindowHolding(childPid, out holderPid, out holderControlled))
            {
                ParsekLog.Info("BgRecorder",
                    $"Background separation: piece already held by a separation window, not fed again: " +
                    $"childPid={childPid} holderParentPid={holderPid} parentPid={parentPid} " +
                    $"parentRec={parentRecordingId}");
                return false;
            }

            CrashCoalescer window;
            string windowRecordingId;
            if (separationWindows.TryGetValue(parentPid, out window)
                && separationWindowParentRecordingIds.TryGetValue(parentPid, out windowRecordingId)
                && !string.Equals(windowRecordingId, parentRecordingId, StringComparison.Ordinal))
            {
                ParsekLog.Info("BgRecorder",
                    $"Separation window belongs to another recording of the vessel, emitting it first: " +
                    $"parentPid={parentPid} windowRec={windowRecordingId} newRec={parentRecordingId}");
                EmitBackgroundSeparationWindowNow(parentPid, "parent-recording-changed");
                window = null;
            }

            bool opened = window == null;
            if (opened)
            {
                window = new CrashCoalescer();
                separationWindows[parentPid] = window;
                separationWindowParentRecordingIds[parentPid] = parentRecordingId;
            }

            window.OnSplitEvent(
                branchUT, childPid, childHasController, cause, preSnapshot, seedPoint);

            ParsekLog.Info("BgRecorder",
                $"Background separation fed to window: parentPid={parentPid} " +
                $"parentRec={parentRecordingId} childPid={childPid} " +
                $"controlled={(childHasController ? "true" : "false")} cause={cause} " +
                $"branchUT={branchUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"windowStartUT={window.WindowStartUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"opened={(opened ? "true" : "false")} snapshot={(preSnapshot != null ? "true" : "false")}");
            return opened;
        }

        /// <summary>
        /// Emits every window whose coalescing time ran out. Returns the number emitted.
        /// </summary>
        internal int TickBackgroundSeparationWindows(double currentUT)
        {
            if (separationWindows.Count == 0) return 0;

            int emitted = 0;
            var parentPids = new List<uint>(separationWindows.Keys);
            for (int i = 0; i < parentPids.Count; i++)
            {
                CrashCoalescer window;
                if (!separationWindows.TryGetValue(parentPids[i], out window)) continue;
                BranchPoint bp = window.Tick(currentUT);
                if (bp == null) continue;
                string parentRecordingId = TakeSeparationWindow(parentPids[i]);
                EmitBackgroundSeparation(parentPids[i], parentRecordingId, window, bp, "window-expired");
                emitted++;
            }
            return emitted;
        }

        /// <summary>
        /// Emits the parent's open window now, before its coalescing time runs out: what it
        /// collected still becomes ONE branch point. False when the parent has no open window.
        /// </summary>
        internal bool EmitBackgroundSeparationWindowNow(uint parentPid, string reason)
        {
            CrashCoalescer window;
            if (!separationWindows.TryGetValue(parentPid, out window))
                return false;
            string parentRecordingId = TakeSeparationWindow(parentPid);

            double windowStartUT = window.WindowStartUT;
            BranchPoint bp = window.EmitNow();
            if (bp == null)
                return false;

            ParsekLog.Info("BgRecorder",
                $"Separation window emitted early ({reason}): parentPid={parentPid} " +
                $"parentRec={parentRecordingId ?? "(none)"} " +
                $"windowStartUT={windowStartUT.ToString("F2", CultureInfo.InvariantCulture)}");
            EmitBackgroundSeparation(parentPid, parentRecordingId, window, bp, reason);
            return true;
        }

        /// <summary>Closes the parent's window entry; returns the recording it belonged to.</summary>
        private string TakeSeparationWindow(uint parentPid)
        {
            string parentRecordingId;
            separationWindowParentRecordingIds.TryGetValue(parentPid, out parentRecordingId);
            separationWindowParentRecordingIds.Remove(parentPid);
            separationWindows.Remove(parentPid);
            return parentRecordingId;
        }

        /// <summary>
        /// The open window that holds <paramref name="childPid"/> (controlled or debris), if
        /// any. A held piece is not yet a background member.
        /// </summary>
        private bool TryFindSeparationWindowHolding(uint childPid, out uint holderPid, out bool controlled)
        {
            holderPid = 0u;
            controlled = false;
            foreach (var kvp in separationWindows)
            {
                if (ListHolds(kvp.Value.ControlledChildPids, childPid))
                {
                    holderPid = kvp.Key;
                    controlled = true;
                    return true;
                }
                if (ListHolds(kvp.Value.DebrisPids, childPid))
                {
                    holderPid = kvp.Key;
                    return true;
                }
            }
            return false;
        }

        private static bool ListHolds(IReadOnlyList<uint> pids, uint pid)
        {
            for (int i = 0; i < pids.Count; i++)
                if (pids[i] == pid) return true;
            return false;
        }

        /// <summary>
        /// Emits the open window that holds <paramref name="childPid"/> before the piece acts
        /// on its own (it separates again, undocks, or the player switches to it): it can be
        /// recorded doing so only once it is a background member with its own recording.
        /// False when no window holds the piece.
        /// </summary>
        internal bool EmitSeparationWindowHoldingChild(uint childPid, string reason)
        {
            if (separationWindows.Count == 0) return false;

            uint holderPid;
            bool controlled;
            if (!TryFindSeparationWindowHolding(childPid, out holderPid, out controlled))
                return false;

            ParsekLog.Info("BgRecorder",
                $"Held piece acts before its window closed: childPid={childPid} " +
                $"parentPid={holderPid} controlled={(controlled ? "true" : "false")} " +
                $"reason={reason} - emitting the window first");
            return EmitBackgroundSeparationWindowNow(holderPid, reason);
        }

        /// <summary>
        /// Owner ruling 2026-11-18 ("never drop one"): emits every open background separation
        /// window now, before the tree is saved, committed, torn down or the live recording
        /// changes. A split whose deferred scan has not run yet is left to that scan (the new
        /// vessel may not exist before it). Returns the number of windows emitted.
        /// </summary>
        internal int FlushBackgroundSeparationWindows(string reason)
        {
            if (separationWindows.Count == 0) return 0;

            int emitted = 0;
            var parentPids = new List<uint>(separationWindows.Keys);
            for (int i = 0; i < parentPids.Count; i++)
            {
                if (EmitBackgroundSeparationWindowNow(parentPids[i], reason))
                    emitted++;
            }
            ParsekLog.Info("BgRecorder",
                $"Background separation windows flushed ({reason}): emitted={emitted}");
            return emitted;
        }

        /// <summary>
        /// Wires one emitted background separation into the tree in the focused shape: the
        /// branch point hangs off the parent recording, which keeps its BackgroundMap entry
        /// and keeps sampling; each separated vessel gets a child recording anchored to the
        /// parent recording; a split with two or more controllable vessels authors a rewind
        /// point whose parent slot is the parent recording itself.
        /// </summary>
        private void EmitBackgroundSeparation(
            uint parentPid,
            string parentRecordingId,
            CrashCoalescer coalescer,
            BranchPoint bp,
            string reason)
        {
            Recording parentRec;
            if (tree == null
                || string.IsNullOrEmpty(parentRecordingId)
                || !tree.Recordings.TryGetValue(parentRecordingId, out parentRec)
                || parentRec == null)
            {
                ParsekLog.Warn("BgRecorder",
                    $"Background separation not recorded: parent recording missing " +
                    $"parentPid={parentPid} parentRec={parentRecordingId ?? "(none)"} " +
                    $"bp={bp.Id} reason={reason}");
                return;
            }

            ParsekFlight.WireBreakupIntoTree(tree, parentRec.RecordingId, parentRec, bp);
            if (!HasStructuralEventPointAtUT(parentRec, bp.UT))
            {
                ParsekLog.Verbose("BgRecorder",
                    $"Background separation: parent has no structural boundary point at the branch UT " +
                    $"parentRec={parentRec.RecordingId} ut={bp.UT.ToString("R", CultureInfo.InvariantCulture)}");
            }

            // The recording continues past the split, so its spawn snapshot must show the
            // surviving vessel, not the pre-split one (focused ProcessBreakupEvent, #224).
            Vessel parentVessel = FlightRecorder.FindVesselByPid(parentPid);
            if (parentVessel != null && parentVessel.persistentId == parentRec.VesselPersistentId)
            {
                ConfigNode snap = FlightRecorder.TryBackupSnapshot(parentVessel);
                if (snap != null)
                    parentRec.VesselSnapshot = snap;
                ParsekLog.Info("BgRecorder",
                    $"Background separation: refreshed parent VesselSnapshot " +
                    $"parentRec={parentRec.RecordingId} parts={parentVessel.parts?.Count ?? 0} " +
                    $"snapshot={(snap != null ? "true" : "false")}");
            }

            BackgroundVesselState parentLoaded;
            loadedStates.TryGetValue(parentPid, out parentLoaded);
            InheritedEngineState? parentEngineState = InheritedStateFromBackgroundVessel(parentLoaded);

            var pairs = new List<(Vessel vessel, Recording rec)>();
            var candidatePids = new List<uint>();
            if (parentVessel != null && ParsekFlight.IsReFlyControllableVessel(parentVessel))
            {
                pairs.Add((parentVessel, parentRec));
                candidatePids.Add(parentVessel.persistentId);
            }

            int controlledCreated = RegisterBackgroundSeparationChildren(
                bp, coalescer, parentRec, coalescer.LastEmittedControlledChildPids,
                controlled: true, parentEngineState, pairs, candidatePids);
            AuthorBackgroundSplitRewindPoint(bp, pairs, candidatePids);
            int debrisCreated = RegisterBackgroundSeparationChildren(
                bp, coalescer, parentRec, coalescer.LastEmittedDebrisPids,
                controlled: false, parentEngineState, null, null);

            ParsekLog.Info("BgRecorder",
                $"Background separation branch complete: bp={bp.Id} type={bp.Type} " +
                $"cause={bp.SplitCause ?? bp.BreakupCause ?? "?"} parentRec={parentRec.RecordingId} " +
                $"parentContinues=true children={bp.ChildRecordingIds.Count} " +
                $"controlled={controlledCreated} debris={debrisCreated} " +
                $"ut={bp.UT.ToString("F2", CultureInfo.InvariantCulture)} reason={reason}");
        }

        /// <summary>
        /// Creates the child recordings of one emitted separation for the given vessel pids
        /// (all controlled or all debris), registers the live ones with this recorder and,
        /// for controllable vessels, adds their (vessel, recording) pair to the rewind-point
        /// candidates. Returns the number of child recordings created.
        /// </summary>
        private int RegisterBackgroundSeparationChildren(
            BranchPoint bp,
            CrashCoalescer coalescer,
            Recording parentRec,
            IReadOnlyList<uint> childPids,
            bool controlled,
            InheritedEngineState? parentEngineState,
            List<(Vessel vessel, Recording rec)> rewindPairs,
            List<uint> rewindCandidatePids)
        {
            if (childPids == null) return 0;

            int created = 0;
            for (int i = 0; i < childPids.Count; i++)
            {
                uint pid = childPids[i];
                Vessel childVessel = FlightRecorder.FindVesselByPid(pid);
                ConfigNode preSnapshot = coalescer.GetPreCapturedSnapshot(pid);
                if (controlled && ParsekFlight.ShouldSkipDeadOnArrivalControlledChild(
                        childVesselIsAlive: childVessel != null,
                        hasPreCapturedSnapshot: preSnapshot != null))
                {
                    ParsekLog.Info("BgRecorder",
                        $"Background separation: controlled child gone before the window closed, " +
                        $"no recording: pid={pid} bp={bp.Id} preCapturedSnapshot={(preSnapshot != null ? "true" : "false")}");
                    continue;
                }

                TrajectoryPoint? seedPoint = ParsekFlight.ApplyStructuralEventFlagToChildSeed(
                    coalescer.GetPreCapturedTrajectoryPoint(pid), bp.UT);
                Recording child = ParsekFlight.CreateBreakupChildRecording(
                    tree, bp, pid, childVessel, isDebris: !controlled,
                    fallbackName: controlled ? "Unknown" : "Debris",
                    fallbackSnapshot: preSnapshot,
                    fallbackTrajectoryPoint: seedPoint,
                    parentGeneration: parentRec.Generation,
                    parentRecordingId: parentRec.RecordingId);
                created++;

                if (childVessel != null)
                {
                    tree.BackgroundMap[pid] = child.RecordingId;
                    // Debris lifetime keyed on the branch point UT, set before the hand-off
                    // so OnVesselBackgrounded finds it and does not re-arm it from the tree.
                    if (!controlled)
                        debrisTTLExpiry[pid] = bp.UT + DebrisTTLSeconds;
                    TrajectoryPoint? initialPoint = seedPoint.HasValue
                        ? seedPoint
                        : (TrajectoryPoint?)CreateAbsoluteTrajectoryPointFromVessel(
                            childVessel, Planetarium.GetUniversalTime(), preferRootPartSurfacePose: true);
                    OnVesselBackgrounded(pid, parentEngineState, initialTrajectoryPoint: initialPoint);

                    if (controlled && rewindPairs != null)
                    {
                        if (ParsekFlight.IsReFlyControllableVessel(childVessel))
                        {
                            rewindPairs.Add((childVessel, child));
                            rewindCandidatePids.Add(pid);
                        }
                        else
                        {
                            ParsekLog.Verbose("Rewind",
                                $"Background split: child pid={pid} is recorded (crew aboard, or a released " +
                                "asteroid / comet) but not controllable; not a Re-Fly slot");
                        }
                    }
                }

                ParsekLog.Info("BgRecorder",
                    $"Background separation child created ({(controlled ? "controlled" : "debris")}): " +
                    $"recId={child.RecordingId} vesselPid={pid} name='{child.VesselName}' " +
                    $"parentRec={parentRec.RecordingId} alive={(childVessel != null ? "true" : "false")} " +
                    $"debrisExpiry={(controlled ? "none" : FormatDebrisUT(bp.UT + DebrisTTLSeconds))}");
            }
            return created;
        }

        #endregion
    }
}
