using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Background vessel split detection: split handling, parent close and trim, child registration, debris TTL and surface clearance.
    internal partial class BackgroundRecorder
    {
        #region Background Vessel Split Detection

        /// <summary>
        /// Called from Update() to process any pending background split checks.
        /// Must be called one frame after the joint break event to allow KSP to
        /// finalize the vessel split.
        /// </summary>
        internal void ProcessPendingSplitChecks()
        {
            if (pendingBackgroundSplitChecks.Count == 0) return;

            // Copy keys to avoid modifying dict during iteration
            var pending = new List<KeyValuePair<uint, (double branchUT, string recordingId, TrajectoryPoint? parentBoundaryPoint, string splitCause)>>(
                pendingBackgroundSplitChecks);
            pendingBackgroundSplitChecks.Clear();

            for (int i = 0; i < pending.Count; i++)
            {
                uint parentPid = pending[i].Key;
                double branchUT = pending[i].Value.branchUT;
                string parentRecordingId = pending[i].Value.recordingId;
                TrajectoryPoint? parentBoundaryPoint = pending[i].Value.parentBoundaryPoint;

                HashSet<uint> preBreakPids;
                preBreakVesselPidSnapshots.TryGetValue(parentPid, out preBreakPids);
                preBreakVesselPidSnapshots.Remove(parentPid);

                HandleBackgroundVesselSplit(
                    parentPid, branchUT, parentRecordingId, preBreakPids, parentBoundaryPoint,
                    pending[i].Value.splitCause);
            }
        }

        /// <summary>
        /// Called when a background vessel may have split. Checks if new vessels appeared
        /// from the split, creates BranchPoint + child recordings for each new vessel.
        /// Spent stages and debris get a TTL (stop recording after DebrisTTLSeconds or on destruction
        /// or when leaving physics bubble).
        /// </summary>
        internal void HandleBackgroundVesselSplit(uint parentPid, double branchUT,
            string parentRecordingId, HashSet<uint> preBreakPids,
            TrajectoryPoint? parentBoundaryPoint = null,
            string splitCause = "DECOUPLE")
        {
            if (tree == null) return;

            Recording parentRec;
            if (!tree.Recordings.TryGetValue(parentRecordingId, out parentRec))
            {
                ParsekLog.Warn("BgRecorder",
                    $"HandleBackgroundVesselSplit: parent recording not found: " +
                    $"parentPid={parentPid} recId={parentRecordingId}");
                return;
            }

            // Destroyed-parent guard: defense-in-depth for the (joint-break → next-frame
            // identity-loss-on-go-on-rails) race. The retirement seam at
            // OnBackgroundVesselGoOnRails drains pendingBackgroundSplitChecks /
            // preBreakVesselPidSnapshots, so ProcessPendingSplitChecks should not
            // normally dispatch here for a sealed parent. This guard pins the
            // invariant: a Destroyed parent does not spawn branch-point children.
            if (parentRec.VesselDestroyed)
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"destroyed-skip.split.{parentPid}",
                    $"HandleBackgroundVesselSplit: skipping destroyed parent " +
                    $"parentPid={parentPid} rec={parentRecordingId}",
                    60.0);
                return;
            }

            var newVesselInfos = CollectNewBackgroundSplitVessels(parentPid, preBreakPids);

            if (newVesselInfos.Count == 0)
            {
                ParsekLog.Verbose("BgRecorder",
                    $"HandleBackgroundVesselSplit: no new vessels from parentPid={parentPid} — " +
                    $"joint break did not cause vessel split");
                return;
            }

            // Cascade depth cap (#284): if the parent recording is already at the
            // generation cap, do not create any branch, child recordings, or parent
            // continuation. The parent stays in BackgroundMap/loadedStates and keeps
            // accumulating samples into its existing recording until destruction.
            // The new fragment vessels remain alive in KSP but become Parsek-orphans.
            // Logged at Info because it represents a real recording-policy decision
            // visible in the user-facing recording count, and only fires for
            // secondary/tertiary breakups (not per frame).
            if (ShouldSkipForCascadeCap(parentRec.Generation))
            {
                ParsekLog.Info("BgRecorder",
                    $"Cascade depth cap fired: skipping background split — " +
                    $"parentRec={parentRec.DebugName} gen={parentRec.Generation} " +
                    $"parentPid={parentPid} skippedChildren={newVesselInfos.Count} " +
                    $"branchUT={branchUT:F1}");
                return;
            }

            // A split occurred. Build the branch structure.
            ParsekLog.Info("BgRecorder",
                $"Background vessel split detected: parentPid={parentPid} " +
                $"childCount={newVesselInfos.Count} branchUT={branchUT:F1} cause={splitCause ?? "BROKE_OFF"}");

            // Determine BranchPoint type: JointBreak (structural separation)
            var branchType = BranchPointType.JointBreak;

            // Build a BranchPoint and child recordings using our pure static method.
            // Children inherit parentRec.Generation + 1.
            var result = BuildBackgroundSplitBranchData(
                parentRecordingId, tree.Id, branchUT, branchType,
                parentPid, newVesselInfos, parentRec.Generation, splitCause);

            var bp = result.bp;
            var childRecordings = result.childRecordings;

            // Bug #298: snapshot parent engine/RCS state before CloseParentRecording
            // destroys loadedStates[parentPid].
            BackgroundVesselState parentLoaded;
            loadedStates.TryGetValue(parentPid, out parentLoaded);
            InheritedEngineState? parentEngineState = InheritedStateFromBackgroundVessel(parentLoaded);

            // Close parent recording: set ChildBranchPointId, close orbit segment/trajectory
            CloseParentRecording(parentRec, parentPid, bp.Id, branchUT, parentBoundaryPoint);

            // Add BranchPoint to tree
            tree.BranchPoints.Add(bp);

            // Only create a parent continuation if the parent vessel still exists.
            // If the parent was destroyed before the deferred split check ran (bug #285),
            // skip the continuation — the parent recording is already closed above with
            // ChildBranchPointId set. Creating a continuation for a dead vessel would
            // produce an empty Recording (zero points) that clutters disk and logs.
            Vessel parentVessel = FlightRecorder.FindVesselByPid(parentPid);
            int continuationCount = 0;
            // Phase 4 (Rewind-to-Staging, §5.1): track the parent continuation recording
            // outside the if-branch so the post-registration RP hook can include it as a
            // candidate controllable child if the parent is still alive and trackable.
            Recording parentContRec = null;

            // Launch-unique identity for the continuation, resolved live-first. See
            // ResolveSplitRecordingLaunchGuid for why the live vessel outranks the
            // closed segment's stored guid. Read once so the log line below can show
            // both inputs. Read only when the parent is alive, matching the gate the
            // helper applies to the continuation itself.
            string liveParentGuid = parentVessel != null
                ? AnchorDetector.TryReadLiveVesselGuid(parentVessel)
                : null;
            parentContRec = TryBuildAndAttachParentContinuation(
                tree, bp, parentRec, parentPid, branchUT,
                parentVesselAlive: parentVessel != null,
                continuationRecordingId: parentVessel != null
                    ? System.Guid.NewGuid().ToString("N")
                    : null,
                liveParentGuid: liveParentGuid,
                // Pin start-of-recording controller identity for the parent continuation
                // from the live post-split parent vessel. Captures the parts that
                // stayed with the parent after the split, so a later destructive
                // crash on the continuation can be detected via identity loss.
                controllers: parentVessel != null
                    ? ControllerInfo.CaptureFromVessel(parentVessel)
                    : null,
                // The continuation records the live post-split parent vessel, so it captures
                // that vessel's current name (not a copy of the closed parent leg's name).
                liveParentName: parentVessel != null
                    ? Recording.ResolveLocalizedName(parentVessel.vesselName)
                    : null);

            if (parentContRec != null)
            {
                string parentContRecId = parentContRec.RecordingId;

                // The guid decision is a state fact the correlator later reads, so it is
                // logged with BOTH inputs. A conclusive divergence (the pid now carries a
                // DIFFERENT launch than the segment being continued) is rare and worth a
                // plain Info: the continuation follows the live vessel, and the chain then
                // correctly reads as more than one launch.
                if (VesselLaunchIdentity.GuidsConclusivelyDiffer(
                        liveParentGuid, parentRec.RecordedVesselGuid))
                {
                    ParsekLog.Info("BgRecorder",
                        $"Parent continuation launch guid diverges from the continued segment: " +
                        $"recId={parentContRecId} parentRecId={parentRecordingId} " +
                        $"live={liveParentGuid} parentRecGuid={parentRec.RecordedVesselGuid} " +
                        $"stamped={parentContRec.RecordedVesselGuid} (live vessel wins)");
                }
                else
                {
                    ParsekLog.Verbose("BgRecorder",
                        $"Parent continuation launch guid stamped: recId={parentContRecId} " +
                        $"guid={parentContRec.RecordedVesselGuid ?? "(none)"} " +
                        $"live={liveParentGuid ?? "(none)"} " +
                        $"parentRecGuid={parentRec.RecordedVesselGuid ?? "(none)"}");
                }

                if (!string.Equals(parentContRec.VesselName, parentRec.VesselName, StringComparison.Ordinal))
                {
                    ParsekLog.Info("BgRecorder",
                        $"Parent continuation captured the live vessel name: recId={parentContRecId} " +
                        $"name='{parentContRec.VesselName}' (closed parent leg '{parentRecordingId}' " +
                        $"keeps '{parentRec.VesselName}')");
                }

                // Re-initialize tracking state for the parent continuation.
                // Seed the continuation with the exact split-time pose so playback can
                // start from the branch boundary instead of waiting for the next sample.
                TrajectoryPoint? parentInitialPoint = parentBoundaryPoint;
                if (!parentInitialPoint.HasValue && parentVessel != null)
                {
                    double sampleUT = Planetarium.GetUniversalTime();
                    parentInitialPoint = CreateAbsoluteTrajectoryPointFromVessel(
                        parentVessel, sampleUT, preferRootPartSurfacePose: true);
                }
                OnVesselBackgrounded(parentPid, parentEngineState,
                    initialTrajectoryPoint: parentInitialPoint);

                // Set TTL for parent continuation if it's debris
                if (parentRec.IsDebris)
                {
                    debrisTTLExpiry[parentPid] = branchUT + DebrisTTLSeconds;
                    ParsekLog.Info("BgRecorder",
                        $"Parent continuation is debris, TTL set: parentPid={parentPid} " +
                        $"expiry={branchUT + DebrisTTLSeconds:F1}");
                }

                continuationCount = 1;
            }
            else
            {
                ParsekLog.Info("BgRecorder",
                    $"Skipping parent continuation — parent vessel destroyed: " +
                    $"parentPid={parentPid} parentRec={parentRec.DebugName}");
            }

            // PR 3b review follow-up: when the parent vessel still exists, the
            // continuation recording (`parentContRec`) is the one that will keep
            // receiving samples for the parent's ongoing trajectory — the original
            // `parentRecordingId` was just closed by `CloseParentRecording` above
            // (its `ChildBranchPointId` is set, `ExplicitEndUT == branchUT`).
            // Anchoring debris to the closed pre-split id would render every
            // post-`branchUT` debris sample unresolvable at playback and trigger an
            // immediate "parent recording finalized" exit from `CheckDebrisTTL`.
            //
            // When no continuation was created (parent vessel destroyed at the same
            // frame as the split, the "Skipping parent continuation" branch above),
            // fall back to the original parent id — debris in that case will be
            // ended quickly by the parent-vessel-destroyed branch in CheckDebrisTTL,
            // and the closed-parent fallback is the only id available.
            string anchorParentRecordingId = parentContRec != null
                ? parentContRec.RecordingId
                : parentRecordingId;
            RegisterChildRecordingsFromSplit(childRecordings, newVesselInfos, branchUT, anchorParentRecordingId, parentEngineState);

            ParsekLog.Info("BgRecorder",
                $"Background split branch complete: bp={bp.Id} type={branchType} " +
                $"parentRecId={parentRecordingId} " +
                $"anchorParentRecId={anchorParentRecordingId} " +
                $"children={bp.ChildRecordingIds.Count} " +
                $"({continuationCount} parent continuation + {newVesselInfos.Count} new vessels)");

            // Phase 4 (Rewind-to-Staging, design §5.1 + §7.1): if the background split
            // produced >=2 controllable outputs (surviving parent continuation + each
            // newVesselInfo with hasController=true), author a RewindPoint. Mirrors
            // the active-vessel path in ParsekFlight.TryAuthorRewindPointForSplit.
            TryAuthorRewindPointForBackgroundSplit(
                bp, parentVessel, parentContRec, newVesselInfos, childRecordings);
        }

        private List<(uint pid, string name, bool hasController)> CollectNewBackgroundSplitVessels(
            uint parentPid,
            HashSet<uint> preBreakPids)
        {
            // Find new vessel PIDs that appeared since the pre-break snapshot
            var newVesselInfos = new List<(uint pid, string name, bool hasController)>();
            if (FlightGlobals.Vessels != null)
            {
                for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
                {
                    Vessel v = FlightGlobals.Vessels[i];
                    if (v == null) continue;
                    if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;
                    if (v.persistentId == parentPid) continue;

                    // Only consider vessels that didn't exist before the break
                    if (preBreakPids != null && preBreakPids.Contains(v.persistentId))
                        continue;

                    // Skip vessels we're already tracking in the tree
                    if (tree.BackgroundMap.ContainsKey(v.persistentId))
                        continue;

                    if (!ParsekFlight.ShouldIncludeVesselInDeferredBreakupScan(v, out string rejectReason))
                    {
                        ParsekLog.Verbose("BgRecorder",
                            $"Background split scan: ignoring unrelated new vessel pid={v.persistentId} " +
                            $"name='{v.vesselName ?? "<unnamed>"}' type={v.vesselType} reason={rejectReason}");
                        continue;
                    }

                    // A crewed vessel with no command module records as its own branch too;
                    // the Rewind Point hook below re-checks controllability for its slots.
                    bool hasController = ParsekFlight.ShouldRecordAsOwnBranch(v);
                    newVesselInfos.Add((v.persistentId, Recording.ResolveLocalizedName(v.vesselName) ?? "Unknown", hasController));
                }
            }

            return newVesselInfos;
        }

        /// <summary>
        /// Phase 4 (Rewind-to-Staging): background-split analogue of
        /// <c>ParsekFlight.TryAuthorRewindPointForSplit</c>. Builds controllable
        /// child slots + a recording-resolver from the live background vessels and
        /// delegates to <see cref="RewindPointAuthor.Begin"/>.
        ///
        /// <para>
        /// Background vessels <i>are</i> in <c>FlightGlobals.Vessels</c> at split
        /// time — the joint-break physics that caused the split only fires when the
        /// vessel is loaded, so <c>rootPart</c> is accessible here just like on the
        /// active-vessel path. If the parent vessel has already been destroyed
        /// (<c>parentVessel == null</c>) the parent continuation slot is skipped;
        /// remaining controllable children still contribute.
        /// </para>
        /// </summary>
        private void TryAuthorRewindPointForBackgroundSplit(
            BranchPoint bp,
            Vessel parentVessel,
            Recording parentContRec,
            List<(uint pid, string name, bool hasController)> newVesselInfos,
            List<Recording> childRecordings)
        {
            if (bp == null)
            {
                ParsekLog.Warn("Rewind",
                    "Background split: RP skipped (bp is null) — this is a bug");
                return;
            }

            // Collect candidate (vessel, recording) pairs for controllable outputs.
            var pairs = new List<(Vessel vessel, Recording rec)>();
            var candidatePids = new List<uint>();

            if (parentVessel != null && parentContRec != null
                && ParsekFlight.IsTrackableVessel(parentVessel))
            {
                pairs.Add((parentVessel, parentContRec));
                candidatePids.Add(parentVessel.persistentId);
            }

            // Children: walk newVesselInfos in parallel with childRecordings (same
            // order — BuildBackgroundSplitBranchData iterates newVesselInfos directly).
            int childCount = childRecordings != null ? childRecordings.Count : 0;
            for (int i = 0; i < childCount; i++)
            {
                if (i >= newVesselInfos.Count) break;
                var info = newVesselInfos[i];
                if (!info.hasController) continue;

                Vessel childVessel = FlightRecorder.FindVesselByPid(info.pid);
                if (childVessel == null)
                {
                    // Vessel vanished between split detection and RP hook. Skip — the
                    // RP would be unusable without a live vessel to correlate against.
                    ParsekLog.Warn("Rewind",
                        $"Background split: controllable child pid={info.pid} not in " +
                        $"FlightGlobals.Vessels at RP-hook time; skipping slot");
                    continue;
                }
                if (!ParsekFlight.IsTrackableVessel(childVessel))
                {
                    ParsekLog.Verbose("Rewind",
                        $"Background split: child pid={info.pid} is recorded (crew aboard) but not " +
                        "controllable; not a Re-Fly slot");
                    continue;
                }
                pairs.Add((childVessel, childRecordings[i]));
                candidatePids.Add(info.pid);
            }

            if (!SegmentBoundaryLogic.IsMultiControllableSplit(candidatePids.Count))
            {
                ParsekLog.Info("Rewind",
                    $"Background split: single-controllable, no RP " +
                    $"(bp={bp.Id} controllable={candidatePids.Count})");
                return;
            }

            // Build ChildSlots + recId->PID resolver (same contract as
            // ParsekFlight.AuthorRewindPointFromVesselRecordings).
            var slots = new List<ChildSlot>();
            var recIdToPid = new Dictionary<string, uint>(StringComparer.Ordinal);
            int slotIndex = 0;
            for (int i = 0; i < pairs.Count; i++)
            {
                var (vessel, rec) = pairs[i];
                if (vessel == null || rec == null) continue;
                if (string.IsNullOrEmpty(rec.RecordingId)) continue;

                slots.Add(new ChildSlot
                {
                    SlotIndex = slotIndex++,
                    OriginChildRecordingId = rec.RecordingId,
                    Controllable = true,
                    Disabled = false,
                    DisabledReason = null
                });
                recIdToPid[rec.RecordingId] = vessel.persistentId;
            }

            if (slots.Count < 2)
            {
                ParsekLog.Info("Rewind",
                    $"Background split: multi-controllable classifier passed " +
                    $"({candidatePids.Count}) but slot build produced only " +
                    $"{slots.Count} entries (bp={bp.Id}); skipping RP");
                return;
            }

            ParsekLog.Info("Rewind",
                $"Background split: authoring RP (bp={bp.Id} slots={slots.Count})");

            var ctx = new RewindPointAuthorContext
            {
                RecordingResolver = recId =>
                    recIdToPid.TryGetValue(recId ?? "", out var pid) ? (uint?)pid : null
            };

            RewindPointAuthor.Begin(bp, slots, candidatePids, ctx);
        }

        /// <summary>
        /// Closes the parent recording at a branch point: sets ChildBranchPointId, ExplicitEndUT,
        /// closes any open orbit/loaded track section, samples a final boundary point, and removes from tracking dicts.
        /// </summary>
        private void CloseParentRecording(Recording parentRec, uint parentPid, string branchPointId,
            double branchUT, TrajectoryPoint? parentBoundaryPoint = null)
        {
            parentRec.ChildBranchPointId = branchPointId;
            parentRec.ExplicitEndUT = branchUT;

            // Close parent's orbit segment if open
            BackgroundOnRailsState parentRails;
            if (onRailsStates.TryGetValue(parentPid, out parentRails) && parentRails.hasOpenOrbitSegment)
            {
                CloseOrbitSegment(parentRails, branchUT);
            }

            // Sample a final boundary point for the parent if loaded
            BackgroundVesselState parentLoaded;
            if (loadedStates.TryGetValue(parentPid, out parentLoaded))
            {
                TrimParentAtBranchBoundary(parentRec, parentLoaded, parentPid, branchUT);

                if (parentBoundaryPoint.HasValue)
                {
                    TrajectoryPoint boundary = parentBoundaryPoint.Value;
                    if (HasStructuralEventPointAtUT(parentRec, boundary.ut))
                    {
                        ParsekLog.Verbose("BgRecorder",
                            string.Format(CultureInfo.InvariantCulture,
                                "CloseParentRecording: structural boundary already recorded " +
                                "parentPid={0} recId={1} ut={2:R}",
                                parentPid,
                                parentRec.RecordingId,
                                boundary.ut));
                    }
                    else
                    {
                        ApplyTrajectoryPointToRecording(parentRec, boundary);
                    }
                }
                else
                {
                    Vessel parentVessel = FlightRecorder.FindVesselByPid(parentPid);
                    if (parentVessel != null)
                    {
                        SampleBoundaryPoint(parentVessel, parentRec, branchUT);
                    }
                }

                CloseBackgroundTrackSection(parentLoaded, branchUT);
                FlushTrackSectionsToRecording(parentLoaded, parentRec);
            }

            // Remove parent from BackgroundMap (it has branched, no longer a live recording)
            tree.BackgroundMap.Remove(parentPid);
            onRailsStates.Remove(parentPid);
            loadedStates.Remove(parentPid);
            railsSpanPartStates.Remove(parentPid);
            debrisTTLExpiry.Remove(parentPid);
            finalizationCaches.Remove(parentPid);
        }

        // Walks rec.Points in reverse, breaking once we step earlier than
        // (ut - tolerance). Relies on Points being sorted ascending by ut —
        // every callsite (CloseParentRecording, the structural-event helpers)
        // appends in UT order, and TrimParentAtBranchBoundary preserves that
        // ordering by removing tail entries only. If a future caller appends
        // out of order, this scan will exit too early and miss matches.
        private static bool HasStructuralEventPointAtUT(Recording rec, double ut)
        {
            if (rec?.Points == null) return false;
            for (int i = rec.Points.Count - 1; i >= 0; i--)
            {
                TrajectoryPoint point = rec.Points[i];
                if (Math.Abs(point.ut - ut) > BranchBoundaryUTTolerance)
                {
                    if (point.ut < ut) break;
                    continue;
                }

                if (((TrajectoryPointFlags)point.flags & TrajectoryPointFlags.StructuralEventSnapshot) != 0)
                    return true;
            }

            return false;
        }

        private static void TrimParentAtBranchBoundary(
            Recording parentRec, BackgroundVesselState parentLoaded, uint parentPid, double branchUT)
        {
            int flatRemoved = TrimTrajectoryPointsAfterUT(parentRec?.Points, branchUT);
            int sectionFramesRemoved = 0;
            int bodyFixedFramesRemoved = 0;
            if (parentLoaded != null && parentLoaded.trackSectionActive)
            {
                // Trim the section frames and keep the per-section warp-flag list
                // index-aligned with them in one step (see the helper for why).
                sectionFramesRemoved = TrimSectionFramesAndWarpFlagsAfterUT(
                    parentLoaded.currentTrackSection.frames,
                    parentLoaded.sectionFrameWarpFlags,
                    branchUT);
                bodyFixedFramesRemoved = TrimTrajectoryPointsAfterUT(
                    parentLoaded.currentTrackSection.bodyFixedFrames, branchUT);
                if (sectionFramesRemoved > 0 || bodyFixedFramesRemoved > 0)
                    RecomputeCurrentTrackSectionAltitudeRange(parentLoaded);
            }

            int removed = flatRemoved + sectionFramesRemoved + bodyFixedFramesRemoved;
            if (removed == 0) return;

            if (parentRec != null)
            {
                parentRec.MarkFilesDirty();
                parentRec.CachedStats = null;
                parentRec.CachedStatsPointCount = 0;
            }

            ParsekLog.Warn("BgRecorder",
                string.Format(CultureInfo.InvariantCulture,
                    "CloseParentRecording: trimmed post-branch parent samples " +
                    "parentPid={0} recId={1} branchUT={2:R} flatRemoved={3} " +
                    "sectionFramesRemoved={4} bodyFixedFramesRemoved={5}",
                    parentPid,
                    parentRec?.RecordingId ?? "<null>",
                    branchUT,
                    flatRemoved,
                    sectionFramesRemoved,
                    bodyFixedFramesRemoved));
        }

        private static int TrimTrajectoryPointsAfterUT(List<TrajectoryPoint> points, double maxUT)
        {
            if (points == null || points.Count == 0) return 0;

            int removed = 0;
            double limit = maxUT + BranchBoundaryUTTolerance;
            for (int i = points.Count - 1; i >= 0; i--)
            {
                if (points[i].ut <= limit) continue;
                points.RemoveAt(i);
                removed++;
            }

            return removed;
        }

        /// <summary>
        /// Trims a section's <paramref name="frames"/> after <paramref name="maxUT"/>
        /// and truncates the index-aligned <paramref name="warpFlags"/> list back to
        /// the new frame count, restoring the 1:1 alignment the sparse-sampling
        /// per-gap classifier relies on.
        ///
        /// Frames are appended in UT order, so <see cref="TrimTrajectoryPointsAfterUT"/>
        /// removes a contiguous post-<paramref name="maxUT"/> tail; truncating the
        /// flags down to <c>frames.Count</c> drops the warp flags for exactly those
        /// removed tail samples. Without this the flags list stays longer than frames
        /// for the rest of the section's life and
        /// <see cref="FlightRecorder.ComputeSectionGapStats"/> falls back to its
        /// length-mismatch path (every gap counted as 1x =&gt; over-WARN) instead of
        /// per-gap warp classification. Mirrors the active-side
        /// <see cref="FlightRecorder.TrimRecordingToUT"/> lockstep trim.
        ///
        /// Pure (no recorder/Unity state) for direct unit testing. Returns the number
        /// of frames removed.
        /// </summary>
        internal static int TrimSectionFramesAndWarpFlagsAfterUT(
            List<TrajectoryPoint> frames, List<bool> warpFlags, double maxUT)
        {
            int framesRemoved = TrimTrajectoryPointsAfterUT(frames, maxUT);
            if (framesRemoved > 0 && warpFlags != null)
            {
                int frameCount = frames?.Count ?? 0;
                if (warpFlags.Count > frameCount)
                    warpFlags.RemoveRange(frameCount, warpFlags.Count - frameCount);
            }

            return framesRemoved;
        }

        private static void RecomputeCurrentTrackSectionAltitudeRange(BackgroundVesselState state)
        {
            if (state == null)
                return;

            if (state.currentTrackSection.frames == null
                || state.currentTrackSection.frames.Count == 0)
            {
                state.currentTrackSection.minAltitude = float.NaN;
                state.currentTrackSection.maxAltitude = float.NaN;
                return;
            }

            float min = float.NaN;
            float max = float.NaN;
            for (int i = 0; i < state.currentTrackSection.frames.Count; i++)
            {
                double altitude = state.currentTrackSection.frames[i].altitude;
                if (double.IsNaN(altitude)) continue;
                if (float.IsNaN(min) || altitude < min)
                    min = (float)altitude;
                if (float.IsNaN(max) || altitude > max)
                    max = (float)altitude;
            }

            state.currentTrackSection.minAltitude = min;
            state.currentTrackSection.maxAltitude = max;
        }

        /// <summary>
        /// Registers child recordings from a vessel split: captures snapshots, adds to tree
        /// and BackgroundMap, initializes tracking state, and sets TTL for debris children.
        ///
        /// Per plan §3b §"Primary creation sites — invocation and ordering" (PR 3b),
        /// `IsDebris` and the <see cref="Recording.ParentAnchorRecordingId"/> contract
        /// are set on the Recording BEFORE <see cref="RecordingTree.AddOrReplaceRecording"/>
        /// and BEFORE <see cref="OnVesselBackgrounded"/>. The seed-point orchestration in
        /// <see cref="InitializeLoadedState"/> reads these fields to decide whether to open
        /// the first track section as Relative-to-parent or as Absolute.
        /// </summary>
        private void RegisterChildRecordingsFromSplit(
            List<Recording> childRecordings,
            List<(uint pid, string name, bool hasController)> newVesselInfos,
            double branchUT,
            string parentRecordingId,
            InheritedEngineState? inherited = null)
        {
            // Capture vessel snapshots so ghosts can be built during playback
            // (without snapshots, GhostVisualBuilder returns null → no ghost appears).
            for (int i = 0; i < childRecordings.Count; i++)
            {
                var child = childRecordings[i];

                // Stamp IsDebris + ParentAnchorRecordingId BEFORE the tree write and the
                // OnVesselBackgrounded dispatch so InitializeLoadedState can read the contract
                // and open the first track section as Relative-to-parent. The helper is
                // caller-decides since the controlled-child extension: both debris
                // (IsDebris=true) and controlled-decoupled children (IsDebris=false) receive
                // the same parent-anchor surface while close to the parent.
                bool hasController = newVesselInfos[i].hasController;
                child.IsDebris = !hasController;
                Recording.ApplyParentAnchorContract(child, parentRecordingId);
                if (!string.IsNullOrEmpty(child.ParentAnchorRecordingId))
                {
                    string population = child.IsDebris ? "debris" : "controlled-child";
                    ParsekLog.Verbose("BgRecorder",
                        $"Parent-anchor contract applied at split: childRecId={child.RecordingId} " +
                        $"childPid={child.VesselPersistentId} population={population} " +
                        $"parentRecId={parentRecordingId ?? "(none)"}");
                }

                // Capture snapshot for ghost building — the vessel is still loaded at this point
                Vessel childVessel = FlightRecorder.FindVesselByPid(child.VesselPersistentId);
                if (childVessel != null)
                {
                    child.VesselSnapshot = LiveVesselSnapshot.TryBackupSnapshot(childVessel);
                    child.GhostVisualSnapshot = child.VesselSnapshot != null
                        ? child.VesselSnapshot.CreateCopy() : null;
                    child.StartResources = VesselSnapshotOps.ExtractResourceManifest(child.VesselSnapshot);
                    int bgChildInvSlots;
                    child.StartInventory = VesselSnapshotOps.ExtractInventoryManifest(child.VesselSnapshot, out bgChildInvSlots);
                    child.StartInventorySlots = bgChildInvSlots;
                    child.StartCrew = VesselSpawner.ExtractCrewManifest(child.VesselSnapshot);
                    // Pin start-of-recording controller identity from the live child vessel
                    // at split moment. Forwarded by Recording copy/clone paths so a later
                    // BG go-on-rails identity-loss check can detect destructive crash.
                    child.Controllers = ControllerInfo.CaptureFromVessel(childVessel);
                    // Launch-unique identity from the CHILD's own live vessel. A split child
                    // is a NEW KSP vessel with a fresh Vessel.id, so the parent's guid is
                    // never a valid answer here - inheriting it would assert that the child
                    // and the parent are one launch, which is the opposite of what the split
                    // just established. The fallback is the child's OWN snapshot `pid`, the
                    // same value the RecordingSidecarStore load-time backfill would have read
                    // (doing it here means the guid exists before the first save, which is
                    // what the recovery correlator's corroboration needs).
                    child.RecordedVesselGuid = ResolveSplitRecordingLaunchGuid(
                        AnchorDetector.TryReadLiveVesselGuid(childVessel),
                        VesselLaunchIdentity.TryReadVesselGuid(child.VesselSnapshot));
                    ParsekLog.Verbose("BgRecorder",
                        $"Captured snapshot for child vessel: pid={child.VesselPersistentId} " +
                        $"name='{child.VesselName}' hasSnapshot={child.VesselSnapshot != null} " +
                        $"startResources={child.StartResources?.Count ?? 0} type(s) " +
                        $"startInventory={child.StartInventory?.Count ?? 0} item(s) " +
                        $"startCrew={child.StartCrew?.Count ?? 0} trait(s) " +
                        $"controllers={child.Controllers?.Count ?? 0} part(s) " +
                        $"launchGuid={child.RecordedVesselGuid ?? "(none)"}");
                }
                else
                {
                    ParsekLog.Warn("BgRecorder",
                        $"Could not capture snapshot for child vessel: pid={child.VesselPersistentId} " +
                        $"name='{child.VesselName}' — vessel not found, ghost will not render");
                }

                tree.AddOrReplaceRecording(child);
                tree.BackgroundMap[child.VesselPersistentId] = child.RecordingId;

                // Initialize tracking state for new child vessel. This check runs
                // one frame after the joint break, so any child seed captured here
                // must use the actual sample UT rather than the earlier branchUT.
                double sampleUT = Planetarium.GetUniversalTime();
                TrajectoryPoint? childInitialPoint = childVessel != null
                    ? (TrajectoryPoint?)CreateAbsoluteTrajectoryPointFromVessel(
                        childVessel, sampleUT, preferRootPartSurfacePose: true)
                    : null;
                OnVesselBackgrounded(child.VesselPersistentId, inherited,
                    initialTrajectoryPoint: childInitialPoint);

                if (!hasController)
                {
                    // Debris child: TTL keyed on branch UT, distinct logging
                    debrisTTLExpiry[child.VesselPersistentId] = branchUT + DebrisTTLSeconds;
                    ParsekLog.Info("BgRecorder",
                        $"Child recording created (debris, TTL={DebrisTTLSeconds:F0}s): " +
                        $"recId={child.RecordingId} vesselPid={child.VesselPersistentId} " +
                        $"name='{child.VesselName}' parentRecId={parentRecordingId ?? "(none)"}");
                }
                else
                {
                    // Controlled child: no TTL
                    ParsekLog.Info("BgRecorder",
                        $"Child recording created (controlled, no TTL): " +
                        $"recId={child.RecordingId} vesselPid={child.VesselPersistentId} " +
                        $"name='{child.VesselName}'");
                }
            }
        }

        /// <summary>
        /// The launch guid a recording created AT A BACKGROUND SPLIT carries. Pure; the two
        /// live call sites differ only in what they pass.
        ///
        /// <para>
        /// <b>Live-first, and that ordering is the point.</b>
        /// <see cref="Recording.RecordedVesselGuid"/> names the launch of the vessel THIS
        /// recording records, and a split recording records the vessel that exists after the
        /// split. The parent CONTINUATION passes the live post-split parent vessel's guid
        /// with the closed pre-split segment's stored guid as the fallback: in the ordinary
        /// case they are the same value and the chain therefore reads as ONE launch, which
        /// is what the recovery correlator's positive corroboration requires
        /// (<c>RecoveryPickAmbiguity.ClassifySurvivorLaunches</c>). In the pid-reuse case
        /// they differ, and the live vessel must win - stamping the continued segment's guid
        /// onto a recording of a DIFFERENT launch would assert "one launch" falsely, and the
        /// XP leg would then write an irreversible row against a wrong pick. Preferring the
        /// stored guid is fail-open in exactly the direction stage 2 exists to close.
        /// </para>
        ///
        /// <para>
        /// The split CHILD is a NEW KSP vessel with a fresh <c>Vessel.id</c>, so it passes
        /// its own live guid with its own snapshot's <c>pid</c> as the fallback and NEVER the
        /// parent's - see the call site in <c>RegisterChildRecordingsFromSplit</c>.
        /// </para>
        ///
        /// <para>
        /// Returns null when neither input is usable (the recording then behaves exactly as
        /// every pre-guid recording does: <c>VesselLaunchIdentity</c> degrades to pid-only).
        /// </para>
        /// </summary>
        internal static string ResolveSplitRecordingLaunchGuid(
            string liveVesselGuid, string fallbackGuid)
        {
            if (!string.IsNullOrEmpty(liveVesselGuid))
                return liveVesselGuid;
            return string.IsNullOrEmpty(fallbackGuid) ? null : fallbackGuid;
        }

        /// <summary>
        /// Pure static method: the bug #285 parent-continuation decision and the tree
        /// wiring that follows it. Returns null when the parent vessel is gone (the parent
        /// recording is already closed with ChildBranchPointId set; a continuation for a
        /// dead vessel would be an empty Recording that clutters disk and logs); otherwise
        /// builds the continuation, inserts it as the FIRST child of the branch point,
        /// registers it on the tree and re-points BackgroundMap[parentPid] at it.
        /// Every live-scene input (the alive verdict, the fresh id, the live launch guid,
        /// the captured controllers) is supplied by the caller so the decision is testable
        /// without Unity.
        /// </summary>
        internal static Recording TryBuildAndAttachParentContinuation(
            RecordingTree tree, BranchPoint bp, Recording parentRec, uint parentPid,
            double branchUT, bool parentVesselAlive, string continuationRecordingId,
            string liveParentGuid, List<ControllerInfo> controllers,
            string liveParentName = null)
        {
            if (!parentVesselAlive) return null;

            // Create a continuation recording for the parent vessel itself
            // (it keeps existing with potentially fewer parts).
            // The continuation stays at the same Generation as the parent - it's
            // the same logical vessel, just with fewer parts. Only spinoff children
            // get parentGeneration + 1.
            // NOTE: with MaxRecordingGeneration=1 the cap above this point already
            // returned for any parentRec.Generation >= 1, so today this assignment
            // is always Generation=0. Kept explicit so future cap bumps (e.g.
            // MaxRecordingGeneration=2) just work without re-auditing this site.
            var parentContRec = new Recording
            {
                RecordingId = continuationRecordingId,
                TreeId = tree.Id,
                VesselPersistentId = parentPid,
                VesselName = ResolveParentContinuationName(liveParentName, parentRec.VesselName),
                ParentBranchPointId = bp.Id,
                ExplicitStartUT = branchUT,
                IsDebris = parentRec.IsDebris,
                // Forward-compat propagation (plan Decision section 10): MaxRecordingGeneration=1
                // means parent continuations of *debris* recordings are never created
                // today, but if Step 4a raises the cap, the contract must already be in
                // place - see plan section "`IsDebris` propagation surface" site #8.
                ParentAnchorRecordingId = parentRec.ParentAnchorRecordingId,
                Generation = parentRec.Generation,
                Controllers = controllers,
                // Launch-unique identity, stamped AT THE WRITE SITE rather than left to
                // the RecordingSidecarStore load-time backfill: the continuation captures
                // no VesselSnapshot (nothing below assigns one), so the backfill has
                // nothing to read and a continuation left blank here stays blank. A
                // guid-less continuation chained to a stamped pre-split segment is a
                // MIXED survivor set at the recovery correlator, which reads
                // corroboration=unknown-launch-guid and makes the KERBAL XP leg refuse an
                // ordinary single-launch recovery
                // (KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY stage 2).
                RecordedVesselGuid = ResolveSplitRecordingLaunchGuid(
                    liveParentGuid, parentRec.RecordedVesselGuid)
            };
            bp.ChildRecordingIds.Insert(0, continuationRecordingId);
            tree.AddOrReplaceRecording(parentContRec);
            tree.BackgroundMap[parentPid] = continuationRecordingId;
            return parentContRec;
        }

        /// <summary>
        /// Pure: the name a background parent continuation captures. A new leg captures its
        /// vessel's CURRENT name, so the continuation reads the live post-split parent vessel
        /// (the vessel it records) rather than copying the closed parent leg's stored name:
        /// stock may have renamed the parent after the split, and a copied name would carry
        /// the old one onto a leg recorded after the rename. Falls back to the stored name only
        /// when no live name is available.
        /// </summary>
        internal static string ResolveParentContinuationName(string liveParentName, string storedParentName)
        {
            return !string.IsNullOrEmpty(liveParentName) ? liveParentName : storedParentName;
        }

        /// <summary>
        /// Pure static method: creates a BranchPoint and child Recording objects for a
        /// background vessel split. Testable without Unity.
        /// The parent vessel may get a continuation recording (added by the caller
        /// if the parent vessel still exists — see bug #285);
        /// this method creates recordings for the NEW child vessels only.
        /// Children inherit Generation = parentGeneration + 1. The cascade-depth cap
        /// is NOT applied here — the caller (HandleBackgroundVesselSplit) decides
        /// whether to invoke this method based on ShouldSkipForCascadeCap.
        /// </summary>
        internal static (BranchPoint bp, List<Recording> childRecordings)
            BuildBackgroundSplitBranchData(
                string parentRecordingId, string treeId, double branchUT,
                BranchPointType branchType, uint parentVesselPid,
                List<(uint pid, string name, bool hasController)> newVesselInfos,
                int parentGeneration = 0,
                string splitCause = "DECOUPLE")
        {
            string bpId = System.Guid.NewGuid().ToString("N");

            var bp = new BranchPoint
            {
                Id = bpId,
                UT = branchUT,
                Type = branchType,
                ParentRecordingIds = new List<string> { parentRecordingId },
                ChildRecordingIds = new List<string>(),
                SplitCause = splitCause
            };

            var childRecordings = new List<Recording>(newVesselInfos.Count);
            for (int i = 0; i < newVesselInfos.Count; i++)
            {
                string childId = System.Guid.NewGuid().ToString("N");
                bp.ChildRecordingIds.Add(childId);

                var child = new Recording
                {
                    RecordingId = childId,
                    TreeId = treeId,
                    VesselPersistentId = newVesselInfos[i].pid,
                    VesselName = newVesselInfos[i].name,
                    ParentBranchPointId = bpId,
                    ExplicitStartUT = branchUT,
                    IsDebris = !newVesselInfos[i].hasController,
                    Generation = parentGeneration + 1
                };
                // PR 3b: stamp the debris parent-anchor contract on the new
                // child Recording adjacent to the IsDebris assignment. String
                // overload because this static factory is invoked with the
                // parent recording id.
                //
                // PR 3b review follow-up note (intentional double-application):
                // RegisterChildRecordingsFromSplit on the live path re-stamps
                // the contract a moment later, AFTER `parentContRec` is created,
                // using the continuation's recording id so the debris anchors to
                // the still-open continuation rather than the closed pre-split
                // parent. Stamping here too is harmless (idempotent) and serves
                // two purposes: (1) it preserves the propagation invariant for
                // test fixtures that call this static factory without going
                // through RegisterChildRecordingsFromSplit; (2) it keeps the
                // contract local to "primary creation sites" per plan §"`IsDebris`
                // propagation surface" — a future caller that bypasses
                // RegisterChildRecordingsFromSplit still gets a contract-stamped
                // debris recording (pointing at the original parent id, which
                // is the right answer when no continuation exists).
                Recording.ApplyParentAnchorContract(child, parentRecordingId);
                childRecordings.Add(child);
            }

            return (bp, childRecordings);
        }

        /// <summary>
        /// Checks debris TTL expiry for all tracked debris vessels.
        /// Called once per frame from Update(). When a debris vessel's TTL has expired,
        /// its recording is finalized and tracking state is cleaned up.
        /// Also checks for destroyed/out-of-bubble vessels.
        /// </summary>
        internal void CheckDebrisTTL(double currentUT)
        {
            if (debrisTTLExpiry.Count == 0) return;

            // Collect expired entries (can't modify dict during iteration)
            List<(uint vesselPid, BackgroundRecordingEndReason reason)> expired = null;

            foreach (var kvp in debrisTTLExpiry)
            {
                uint vesselPid = kvp.Key;
                double expiry = kvp.Value;

                if (double.IsNaN(expiry)) continue; // no TTL

                // Check if vessel is destroyed or out of bubble
                Vessel v = FlightRecorder.FindVesselByPid(vesselPid);
                if (v == null)
                {
                    // Vessel gone (destroyed or despawned)
                    if (expired == null)
                        expired = new List<(uint vesselPid, BackgroundRecordingEndReason reason)>();
                    expired.Add((vesselPid, BackgroundRecordingEndReason.VesselDestroyedOrDespawned));

                    ParsekLog.Info("BgRecorder",
                        $"Debris TTL: vessel destroyed/despawned, ending recording: " +
                        $"vesselPid={vesselPid}");
                    continue;
                }

                // Check if TTL expired
                if (currentUT >= expiry)
                {
                    if (expired == null)
                        expired = new List<(uint vesselPid, BackgroundRecordingEndReason reason)>();
                    expired.Add((vesselPid, BackgroundRecordingEndReason.TtlExpired));

                    ParsekLog.Info("BgRecorder",
                        $"Debris TTL expired, ending recording: " +
                        $"vesselPid={vesselPid} expiry={expiry:F1} currentUT={currentUT:F1}");
                    continue;
                }

                // Check if vessel went out of physics bubble (packed + on rails)
                if (!v.loaded)
                {
                    if (expired == null)
                        expired = new List<(uint vesselPid, BackgroundRecordingEndReason reason)>();
                    expired.Add((vesselPid, BackgroundRecordingEndReason.LeftPhysicsBubble));

                    ParsekLog.Info("BgRecorder",
                        $"Debris TTL: vessel left physics bubble, ending recording: " +
                        $"vesselPid={vesselPid}");
                    continue;
                }

                // PR 3b §"Parent on-rails hook" (Decision §10): for debris
                // carrying the parent-anchor contract, end the debris recording when
                // the parent's recorded data is gone or the parent vessel is no
                // longer live. Three end-conditions, one for each failure mode.
                // Legacy v11 debris (no ParentAnchorRecordingId) keeps original lifetime.
                if (tree == null
                    || tree.BackgroundMap == null
                    || !tree.BackgroundMap.TryGetValue(vesselPid, out string childRecId))
                {
                    continue;
                }
                Recording childRec;
                if (tree.Recordings == null
                    || !tree.Recordings.TryGetValue(childRecId, out childRec))
                {
                    continue;
                }
                if (string.IsNullOrEmpty(childRec.ParentAnchorRecordingId))
                    continue; // legacy v11 — keep original behavior

                Recording parentRec;
                if (!tree.Recordings.TryGetValue(childRec.ParentAnchorRecordingId, out parentRec))
                {
                    if (expired == null)
                        expired = new List<(uint vesselPid, BackgroundRecordingEndReason reason)>();
                    expired.Add((vesselPid, BackgroundRecordingEndReason.ParentRecordingMissing));
                    ParsekLog.Info("BgRecorder",
                        $"Debris TTL: parent recording missing from tree, ending: " +
                        $"parentRecId={childRec.ParentAnchorRecordingId} childPid={vesselPid}");
                    continue;
                }

                // Parent recording closed/superseded while the vessel may still be
                // loaded (the §"Bug evidence" closed-coverage-hole case at UT
                // 1213.398/1228.435): end the debris too — its Relative samples
                // beyond the parent's recorded UT range cannot resolve at playback.
                //
                // PR 3b review follow-up: predicate extracted to
                // DebrisParentStateGate.IsParentRecordingClosedOrSuperseded so xUnit
                // can validate the truth table. Do NOT regress to
                // parentRec.ExplicitEndUT < currentUT — active background recordings
                // update ExplicitEndUT only at sample boundaries, so that signal
                // lags by the sample interval and would end every parent-anchored debris on
                // the next TTL tick.
                if (DebrisParentStateGate.IsParentRecordingClosedOrSuperseded(parentRec, tree))
                {
                    if (expired == null)
                        expired = new List<(uint vesselPid, BackgroundRecordingEndReason reason)>();
                    expired.Add((vesselPid, BackgroundRecordingEndReason.ParentRecordingClosedOrSuperseded));
                    bool parentClosedAtSplit = !string.IsNullOrEmpty(parentRec.ChildBranchPointId);
                    ParsekLog.Info("BgRecorder",
                        $"Debris TTL: parent recording closed/superseded, ending recording: " +
                        $"parentRecId={childRec.ParentAnchorRecordingId} " +
                        $"parentClosedAtSplit={parentClosedAtSplit} " +
                        $"currentUT={currentUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                        $"childPid={vesselPid}");
                    continue;
                }

                // Launch-matched lookup: a preserved relaunch of the same craft carries the
                // parent's craft-baked pid, and treating it as the live parent would keep a
                // debris recording anchored to a stranger instead of ending it here.
                Vessel parentVessel = FlightRecorder.FindLaunchMatchedVesselForRecording(parentRec);
                if (parentVessel == null || parentVessel.packed || !parentVessel.loaded)
                {
                    if (expired == null)
                        expired = new List<(uint vesselPid, BackgroundRecordingEndReason reason)>();
                    expired.Add((vesselPid, BackgroundRecordingEndReason.ParentOnRailsOrDestroyed));
                    ParsekLog.Info("BgRecorder",
                        $"Debris TTL: parent on-rails or destroyed, ending recording: " +
                        $"parentRecId={childRec.ParentAnchorRecordingId} " +
                        $"parentPid={parentRec.VesselPersistentId} " +
                        $"parentLoaded={parentVessel?.loaded ?? false} " +
                        $"parentPacked={parentVessel?.packed ?? false} " +
                        $"childPid={vesselPid}");
                    continue;
                }
            }

            if (expired == null) return;

            for (int i = 0; i < expired.Count; i++)
            {
                EndDebrisRecording(expired[i].vesselPid, currentUT, expired[i].reason);
            }
        }

        /// <summary>
        /// Sets the debris TTL expiry for an externally-created debris recording.
        /// Called by ParsekFlight.ProcessBreakupEvent when adding debris child
        /// recordings to the active tree.
        /// </summary>
        public void SetDebrisExpiry(uint vesselPid, double expiryUT)
        {
            debrisTTLExpiry[vesselPid] = expiryUT;
            ParsekLog.Info("BgRecorder",
                "Debris expiry set: pid=" + vesselPid +
                ", expiryUT=" + expiryUT.ToString("F1", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Ends a debris recording: finalizes the recording with a terminal state,
        /// cleans up tracking state, removes from BackgroundMap.
        /// </summary>
        private void EndDebrisRecording(
            uint vesselPid,
            double endUT,
            BackgroundRecordingEndReason endReason)
        {
            debrisTTLExpiry.Remove(vesselPid);

            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(vesselPid, out recordingId)) return;

            Recording rec = null;
            tree.Recordings.TryGetValue(recordingId, out rec);

            Vessel v = FlightRecorder.FindVesselByPid(vesselPid);
            if (v != null)
            {
                RefreshFinalizationCacheForVessel(
                    v,
                    recordingId,
                    v.loaded && !v.packed
                        ? FinalizationCacheOwner.BackgroundLoaded
                        : FinalizationCacheOwner.BackgroundOnRails,
                    "background_debris_end",
                    force: true);
            }
            else
            {
                RecordingFinalizationCache existingCache;
                BackgroundOnRailsState railsState;
                if ((!finalizationCaches.TryGetValue(vesselPid, out existingCache)
                        || !IsPotentiallyApplicableFinalizationCache(existingCache))
                    && onRailsStates.TryGetValue(vesselPid, out railsState))
                {
                    RefreshOnRailsFinalizationCache(
                        railsState,
                        endUT,
                        "background_debris_end_missing",
                        force: true);
                }
            }

            if (rec != null)
            {
                rec.ExplicitEndUT = endUT;
            }

            RecordingFinalizationCache cacheForApply = null;
            finalizationCaches.TryGetValue(vesselPid, out cacheForApply);

            // Clean up tracking state — also flushes any accumulated TrackSections
            // to rec via OnVesselRemovedFromBackground → FlushTrackSectionsToRecording.
            OnVesselRemovedFromBackground(vesselPid, endUT);
            if (rec != null)
            {
                DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording(
                    rec,
                    $"EndDebrisRecording pid={vesselPid}");
            }

            RecordingFinalizationCacheApplyResult cacheResult;
            bool cacheApplied = rec != null
                && TryApplyFinalizationCacheForBackgroundEnd(
                    rec,
                    cacheForApply,
                    vesselPid,
                    endUT,
                    "EndDebrisRecording",
                    allowStale: true,
                    requireDestroyedTerminal: false,
                    confirmedDestroyed: IsConfirmedDestroyedBackgroundEnd(endReason),
                    out cacheResult);

            if (rec != null && !cacheApplied && !rec.TerminalStateValue.HasValue)
            {
                if (v == null)
                {
                    rec.StampTerminalState(TerminalState.Destroyed, "EndDebrisRecording.vesselGone");
                }
                else
                {
                    rec.StampTerminalState(
                        RecordingTree.DetermineTerminalState((int)v.situation, v),
                        "EndDebrisRecording.situation");
                    ParsekFlight.CaptureTerminalOrbit(rec, v);
                }
            }

            tree.BackgroundMap.Remove(vesselPid);

            // Bug #280 follow-up (PR #177): persist the flushed data + snapshot to
            // disk IMMEDIATELY at finalization. The #280 fix (PR #167) wired
            // PersistFinalizedRecording into OnBackgroundVesselWillDestroy and
            // Shutdown but left EndDebrisRecording — the CheckDebrisTTL termination
            // site for both the v == null "destroyed/despawned" branch and the 60s
            // TTL expiry branch — uncovered. PR #176's #278 fix removed the
            // user-visible symptom by stopping the blanket-Destroyed stamp in
            // FinalizePendingLimboTreeForRevert, but the underlying #280 wiring
            // gap remains and would re-surface as data loss the moment a future
            // change reintroduces a code path that depends on the TTL/destroyed
            // sub-path persisting at finalization. Mirroring the call here closes
            // the gap so the TTL path matches the destroy/shutdown paths.
            if (rec != null)
            {
                PersistFinalizedRecording(rec, $"EndDebrisRecording pid={vesselPid}");
            }

            ParsekLog.Info("BgRecorder",
                $"Debris recording ended: vesselPid={vesselPid} recId={recordingId} " +
                $"terminal={rec?.TerminalStateValue?.ToString() ?? "null"}");
        }

        /// <summary>
        /// Pure static method: determines whether a debris vessel's TTL has expired,
        /// or whether it should stop recording for another reason.
        /// Returns the reason string if should stop, null if should continue.
        /// </summary>
        internal static string ShouldStopDebrisRecording(
            double currentUT, double ttlExpiry, bool vesselExists, bool vesselLoaded)
        {
            if (!vesselExists)
                return "destroyed";

            if (!double.IsNaN(ttlExpiry) && currentUT >= ttlExpiry)
                return "ttl_expired";

            if (!vesselLoaded)
                return "out_of_bubble";

            return null;
        }

        /// <summary>
        /// Phase 7 (review pass 3): pure gate predicate for the background
        /// surface-clearance computation. Mirrors the foreground gate in
        /// <see cref="FlightRecorder.ShouldEmitSurfaceClearance"/> so xUnit can
        /// pin the four-condition AND without needing a live <c>Vessel</c> /
        /// <c>FlightGlobals</c> runtime. The Unity-only
        /// <c>TerrainAltitude</c> call is isolated in
        /// <see cref="ApplySurfaceClearanceToPoint"/>.
        ///
        /// <para>All four conditions must hold for the BG sampler to
        /// populate <c>recordedGroundClearance</c>: a section must be
        /// active (no orphaned samples), the section must be
        /// <see cref="ReferenceFrame.Absolute"/> (RELATIVE sections store
        /// metres in lat/lon/alt — terrain math would be nonsense), the
        /// environment must be a surface-clearance environment, and the body
        /// must have a PQS controller (gas giants without surface mesh return
        /// zero from <c>TerrainAltitude</c>, which would produce a wrong-sign
        /// clearance).</para>
        /// </summary>
        internal static bool ShouldEmitBackgroundSurfaceClearance(
            bool trackSectionActive,
            ReferenceFrame frame,
            SegmentEnvironment env,
            bool hasPqsController)
        {
            return FlightRecorder.ShouldEmitSurfaceClearance(
                trackSectionActive, frame, env, hasPqsController);
        }

        /// <summary>
        /// Pure gate predicate for the body-fixed-shadow clearance path.
        /// Mirrors
        /// <see cref="ShouldEmitBackgroundSurfaceClearance"/> but forces
        /// <see cref="ReferenceFrame.Absolute"/> -- the shadow carries
        /// genuine body-fixed lat/lon/alt regardless of the active
        /// section's frame. The remaining three conditions
        /// (<c>trackSectionActive</c>, surface env, PQS) still apply.
        /// </summary>
        internal static bool ShouldEmitBackgroundBodyFixedShadowClearance(
            bool trackSectionActive,
            SegmentEnvironment env,
            bool hasPqsController)
        {
            return FlightRecorder.ShouldEmitSurfaceClearance(
                trackSectionActive,
                ReferenceFrame.Absolute,
                env,
                hasPqsController);
        }

        /// <summary>
        /// Apply v9 surface clearance to the body-fixed shadow
        /// copy that gets stored in <c>TrackSection.bodyFixedFrames</c>
        /// alongside a RELATIVE section. The in-frames <c>point</c> stays
        /// NaN-clearance because anchor-local metre offsets in
        /// <c>lat/lon/alt</c> are not body-fixed coordinates -- but the shadow
        /// carries genuine body-fixed lat/lon/alt and the body-fixed
        /// primary playback path applies terrain correction via
        /// <c>recordedGroundClearance</c>. Without this helper, parent-
        /// anchored surface debris recorded inside a parent-relative section
        /// would replay at raw recorded altitude and bury or pop on terrain
        /// differences.
        ///
        /// <para>Per-section stats (<c>surfaceMobileMin/MaxClearanceThisSection</c>,
        /// etc.) are intentionally NOT updated here: those track the
        /// section's own primary <c>frames</c> samples, not the parallel
        /// body-fixed shadow store.</para>
        /// </summary>
        private static void ApplySurfaceClearanceToBodyFixedShadow(
            BackgroundVesselState state,
            Vessel bgVessel,
            ref TrajectoryPoint shadow)
        {
            bool hasPqs = bgVessel != null && bgVessel.mainBody != null
                && bgVessel.mainBody.pqsController != null;
            if (!ShouldEmitBackgroundBodyFixedShadowClearance(
                    state != null && state.trackSectionActive,
                    state != null && state.trackSectionActive
                        ? state.currentTrackSection.environment
                        : SegmentEnvironment.SurfaceMobile,
                    hasPqs))
            {
                return;
            }
            double terrainHeight = bgVessel.mainBody.TerrainAltitude(
                shadow.latitude, shadow.longitude, true);
            shadow.recordedGroundClearance = shadow.altitude - terrainHeight;
        }

        private static void ApplySurfaceClearanceToPoint(
            BackgroundVesselState state,
            Vessel bgVessel,
            ref TrajectoryPoint point)
        {
            bool hasPqs = bgVessel != null && bgVessel.mainBody != null && bgVessel.mainBody.pqsController != null;
            if (!ShouldEmitBackgroundSurfaceClearance(
                    state != null && state.trackSectionActive,
                    state != null && state.trackSectionActive
                        ? state.currentTrackSection.referenceFrame
                        : ReferenceFrame.Absolute,
                    state != null && state.trackSectionActive
                        ? state.currentTrackSection.environment
                        : SegmentEnvironment.SurfaceMobile,
                    hasPqs))
            {
                return;
            }

            double terrainHeight = bgVessel.mainBody.TerrainAltitude(point.latitude, point.longitude, true);
            point.recordedGroundClearance = point.altitude - terrainHeight;
            state.surfaceMobileSamplesThisSection++;
            if (double.IsNaN(state.surfaceMobileMinClearanceThisSection)
                || point.recordedGroundClearance < state.surfaceMobileMinClearanceThisSection)
                state.surfaceMobileMinClearanceThisSection = point.recordedGroundClearance;
            if (double.IsNaN(state.surfaceMobileMaxClearanceThisSection)
                || point.recordedGroundClearance > state.surfaceMobileMaxClearanceThisSection)
                state.surfaceMobileMaxClearanceThisSection = point.recordedGroundClearance;
            state.surfaceMobileClearanceSumThisSection += point.recordedGroundClearance;
        }

        /// <summary>
        /// Pure decision: should a background vessel split be skipped because
        /// the parent recording's generation already meets the cascade-depth cap?
        /// True means HandleBackgroundVesselSplit returns without creating any
        /// branch, child recordings, or parent continuation; the parent's
        /// existing recording continues sampling unchanged.
        /// </summary>
        internal static bool ShouldSkipForCascadeCap(int parentGeneration)
            => parentGeneration >= MaxRecordingGeneration;

        #endregion
    }
}
