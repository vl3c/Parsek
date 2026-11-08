using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Tracking-station ghost lifecycle: per-tick refresh, startup creation, removal reasons and
    // the end-of-recording spawn handoff.
    internal static partial class GhostMapPresence
    {
        private sealed class TrackingStationGhostSourceBatch
        {
            private readonly string context;
            private readonly Dictionary<string, int> countByKey = new Dictionary<string, int>();
            private readonly Dictionary<string, string> firstDetailByKey = new Dictionary<string, string>();
            private int segmentCount;
            private int terminalCount;
            private int endpointTailCount;
            private int stateVectorCount;
            private int skippedCount;

            internal TrackingStationGhostSourceBatch(string context)
            {
                this.context = string.IsNullOrEmpty(context) ? "unspecified" : context;
            }

            internal void Observe(
                int recordingIndex,
                Recording rec,
                double currentUT,
                TrackingStationGhostSource source,
                string reason,
                string detail)
            {
                if (source == TrackingStationGhostSource.Segment)
                    segmentCount++;
                else if (source == TrackingStationGhostSource.TerminalOrbit)
                    terminalCount++;
                else if (source == TrackingStationGhostSource.EndpointTail)
                    endpointTailCount++;
                else if (IsStateVectorGhostSource(source))
                    stateVectorCount++;
                else
                    skippedCount++;

                string key = BuildTrackingStationGhostSourceSummaryKey(source, reason);
                if (!countByKey.TryGetValue(key, out int count))
                    count = 0;
                countByKey[key] = count + 1;

                if (!firstDetailByKey.ContainsKey(key))
                {
                    firstDetailByKey[key] = BuildTrackingStationGhostSourceLogLine(
                        context,
                        recordingIndex,
                        rec,
                        currentUT,
                        source,
                        reason,
                        detail);
                }
            }

            internal void Log(string action, int recordingCount, int created, int alreadyTracked)
            {
                if (countByKey.Count == 0 && alreadyTracked == 0)
                    return;

                foreach (var kvp in firstDetailByKey)
                {
                    ParsekLog.VerboseRateLimited(
                        Tag,
                        string.Format(ic, "ts-orbit-source-first-{0}-{1}", context, kvp.Key),
                        kvp.Value,
                        10.0);
                }

                ParsekLog.VerboseRateLimited(
                    Tag,
                    "ts-orbit-source-summary-" + context,
                    string.Format(ic,
                        "Tracking-station orbit-source summary: context={0} action={1} recordings={2} " +
                        "created={3} alreadyTracked={4} sources(visibleSegment={5} terminalOrbit={6} endpointTail={7} stateVector={8}) " +
                        "skipped={9} skipCounts={10}",
                        context,
                        action ?? "(null)",
                        recordingCount,
                        created,
                        alreadyTracked,
                        segmentCount,
                        terminalCount,
                        endpointTailCount,
                        stateVectorCount,
                        skippedCount,
                        FormatTrackingStationGhostSourceCounts()),
                    10.0);
            }

            private string FormatTrackingStationGhostSourceCounts()
            {
                if (countByKey.Count == 0)
                    return "{}";

                var sb = new StringBuilder();
                sb.Append('{');
                bool first = true;
                foreach (var kvp in countByKey)
                {
                    if (!first)
                        sb.Append(',');
                    first = false;
                    sb.Append(kvp.Key);
                    sb.Append('=');
                    sb.Append(kvp.Value.ToString(ic));
                }
                sb.Append('}');
                return sb.ToString();
            }
        }

        internal static bool IsTrackingStationRecordingMaterialized(
            Recording rec, bool realVesselExists)
        {
            return rec != null
                && (rec.VesselSpawned
                    || rec.SpawnedVesselPersistentId != 0
                    || (rec.VesselPersistentId != 0 && realVesselExists));
        }

        /// <summary>
        /// Per-frame lifecycle for tracking station ghost ProtoVessels.
        /// Creates ghosts when UT enters a map-visible orbit range (handles time warp),
        /// carries them across brief same-body gaps, and removes them when the visible
        /// orbit range is truly exhausted.
        /// Called periodically from ParsekTrackingStation.Update.
        /// In the flight scene, this lifecycle is handled by ParsekPlaybackPolicy.CheckPendingMapVessels;
        /// in the tracking station, this method provides the equivalent.
        /// </summary>
        /// <summary>
        /// Back-compat / inert overload (no Mission loop units). Used by in-game tests and any
        /// caller that has no cached <see cref="GhostPlaybackLogic.LoopUnitSet"/>. Passes
        /// <see cref="GhostPlaybackLogic.LoopUnitSet.Empty"/>, so behavior is identical to before
        /// Phase F: every recording resolves to the live UT and nothing is hidden.
        /// </summary>
        internal static void UpdateTrackingStationGhostLifecycle(bool refreshStockList = true)
        {
            UpdateTrackingStationGhostLifecycle(
                GhostPlaybackLogic.LoopUnitSet.Empty, refreshStockList);
        }

        /// <summary>
        /// Phase F: tracking-station ProtoVessel ghost lifecycle under the shared Mission span
        /// clock. <paramref name="loopUnits"/> is the per-frame cached set built once by
        /// <c>ParsekTrackingStation.DriveMissionLoopUnits</c>. For each committed index the
        /// effective sample UT is resolved via
        /// <see cref="GhostPlaybackLogic.ResolveTrackingStationSampleUT"/>; a member outside its
        /// loop window this cycle is skipped on create and torn down on refresh. With
        /// <see cref="GhostPlaybackLogic.LoopUnitSet.Empty"/> this is byte-identical to the
        /// pre-Phase-F behavior (effUT == liveUT, renderHidden never true).
        /// </summary>
        internal static void UpdateTrackingStationGhostLifecycle(
            GhostPlaybackLogic.LoopUnitSet loopUnits,
            bool refreshStockList = true)
        {
            if (loopUnits == null)
                loopUnits = GhostPlaybackLogic.LoopUnitSet.Empty;
            RefreshGhostProtoNamesIfDisplayNamesMoved("trackstation");
            double currentUT = CurrentUTNow();
            var committed = RecordingStore.CommittedRecordings;
            bool hasCommittedRecordings = committed != null && committed.Count > 0;
            int createdBefore = lifecycleCreatedThisTick;
            int destroyedBefore = lifecycleDestroyedThisTick;

            if (hasCommittedRecordings)
                TryRunTrackingStationSpawnHandoffs(committed, currentUT, loopUnits);

            // Phase F v1 simplification: suppression is resolved at the LIVE currentUT, not the
            // per-member span-clock effUT. Loop-unit members are already gated by the per-recording
            // render decision (ResolveTrackingStationSampleUT) in the create / refresh / atmospheric
            // passes below, so the chain-filter suppression set never needs the loop UT here.
            var suppressed = hasCommittedRecordings
                ? FindTrackingStationSuppressedRecordingIds(committed, currentUT)
                : new HashSet<string>();
            AddActiveSessionSuppressedRecordingIds(suppressed, committed);
            CachedTrackingStationSuppressedIds = suppressed;

            if (hasCommittedRecordings)
                GhostPlaybackLogic.InvalidateVesselCache();

            RefreshTrackingStationGhosts(committed, suppressed, currentUT, loopUnits);

            // Per-instance overlap sweep (slice i): SOLE create/destroy authority for overlap
            // recordings in the Tracking Station. The schedule resolves through the same pure
            // GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule the flight + KSC paths use, so the
            // cycle set is identical to flight (the flight engine does not exist in this scene). The
            // per-index create loop below skips overlap indices. Run before the empty-return so a
            // gate-off transition reaps any leftover instances even when no committed recordings exist.
            // loopUnits is the TS span-clock set so a Mission-tab loop (source b) drives per-instance.
            RunOverlapPerInstanceSweep(currentUT, committed, loopUnits);

            if (!hasCommittedRecordings)
            {
                RefreshTrackingStationVesselListAfterLifecycleMutation(
                    createdBefore,
                    destroyedBefore,
                    refreshStockList,
                    "tracking-station-lifecycle-empty");
                EmitLifecycleSummary("tracking-station", currentUT);
                return;
            }

            // --- Phase 2: create ghosts for recordings that just entered visible orbit range ---
            var sourceBatch = new TrackingStationGhostSourceBatch("tracking-station-lifecycle");
            int lifecycleCreated = 0;
            int alreadyTracked = 0;
            int loopMemberHidden = 0; // Phase F: members outside their loop window this cycle (skip create)
            for (int i = 0; i < committed.Count; i++)
            {
                // Skip recordings that already have a ghost
                if (vesselsByRecordingIndex.ContainsKey(i))
                {
                    alreadyTracked++;
                    continue;
                }

                var rec = committed[i];

                // Slice (i): overlap recordings are created by the per-instance sweep above, not here.
                if (ShouldDriveOverlapPerInstance(rec, i, committed, loopUnits))
                    continue;

                // Phase F: substitute the shared Mission span-clock loopUT for the live UT when this
                // committed index is a loop-unit member, and a Recordings-tab loop's own loop-clock UT
                // when it is not. Inert (effUT == currentUT, renderHidden false) for every other
                // recording and when loopUnits is Empty.
                double effUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                    i, rec.StartUT, rec.EndUT, currentUT, loopUnits, out bool renderHidden);
                if (renderHidden)
                {
                    loopMemberHidden++;
                    continue; // member outside its window this cycle: do not create a ghost
                }

                bool isSuppressed = suppressed.Contains(rec.RecordingId);
                bool realVesselExists = rec.VesselPersistentId != 0
                    && GhostPlaybackLogic.RealVesselExistsForRecording(rec);
                // Step 2: suppress this rec's OWN loop-ghost double ONLY while its
                // guid-gated launch-matched live vessel is loaded AND it was the LIVE
                // docking anchor of an in-window relative member this-or-last frame (the
                // Step-1 live-bind event), NOT for the whole loop (which over-suppressed
                // every parked route craft). realVesselExists is the same guid-gated
                // RealVesselExistsForRecording, so a same-craft different-launch vessel
                // never suppresses; a member watched from afar with no live vessel draws.
                // Best-effort on this map/TS path: the live-bind is stamped at member
                // resolution (ghost (re)creation), not per on-rails frame, so the
                // duplicate can briefly show until the next resolve (see
                // WasLiveBoundThisOrLastFrame).
                bool liveLaunchMatchedAnchorOfActiveMember = realVesselExists
                    && RelativeAnchorResolver.WasLiveBoundThisOrLastFrame(rec.RecordingId);
                int cachedStateVectorIndex = trackingStationStateVectorCachedIndices.TryGetValue(i, out int cached)
                    ? cached
                    : -1;
                TrackingStationGhostSource source = ResolveTrackingStationGhostSourceCore(
                    rec,
                    isSuppressed,
                    realVesselExists,
                    effUT,
                    ref cachedStateVectorIndex,
                    out OrbitSegment segment,
                    out TrajectoryPoint stateVectorPoint,
                    out _,
                    sourceBatch,
                    i,
                    "tracking-station-lifecycle",
                    // Loop member replaying in its window (effUT != live currentUT): allow the
                    // map ghost to be created alongside any persisted real terminal vessel.
                    loopMemberInWindow: (currentUT - effUT) != 0.0,
                    liveLaunchMatchedAnchorOfActiveMember: liveLaunchMatchedAnchorOfActiveMember,
                    // COSMETIC fix: retire ONLY the descent-trigger DESTINATION transfer member's ghost
                    // (TransferMemberIndex) once the shared descent has handed off / landed (phase Descent/Done, at
                    // the LIVE currentUT - NOT effUT) so it does not synthesize the sub-surface endpoint-tail
                    // looping ghost. False (byte-identical) for the owner, every ride-along in a different/unshifted
                    // frame (e.g. a launch-body-orbit probe - it must keep rendering), every descent-set member,
                    // and any non-re-aim unit, plus Inert/Loiter (loiter conic preserved).
                    transferMemberDescentContinuation:
                        GhostPlaybackLogic.IsTransferMemberDescentContinuation(
                            loopUnits, i, currentUT, rec.StartUT, rec.EndUT));
                trackingStationStateVectorCachedIndices[i] = cachedStateVectorIndex;
                if (source == TrackingStationGhostSource.None) continue;

                // Loop-shift the orbit + body-frame cache at create-time so the orbit-line
                // and arc-clip patches see correctly-shifted bounds on the very first frame.
                // Without this the create writes shift=0 bounds and the next lifecycle pass
                // (up to LifecycleCheckIntervalSec later) refreshes them, producing an
                // orbit-line blackout at TS entry for any loop-shifted member.
                double tsLoopEpochShift = currentUT - effUT;
                Vessel v = CreateGhostVesselFromSource(
                    i,
                    rec,
                    source,
                    segment,
                    stateVectorPoint,
                    effUT,
                    loopEpochShiftSeconds: tsLoopEpochShift);

                if (v != null)
                {
                    lifecycleCreated++;
                    if (IsStateVectorGhostSource(source))
                        trackingStationStateVectorOrbitTrajectories[i] = rec;
                    else
                        trackingStationStateVectorOrbitTrajectories.Remove(i);
                    // Ensure orbit renderer exists (MapView.fetch should be available by now)
                    EnsureGhostOrbitRenderers();
                    ParsekLog.Info(Tag,
                        string.Format(ic,
                            "Deferred ghost creation for #{0} \"{1}\" — UT {2:F1} entered visible orbit range source={3}",
                            i, rec.VesselName ?? "(null)", effUT, FormatTrackingStationGhostSource(source)));
                }
            }

            sourceBatch.Log(
                "UpdateTrackingStationGhostLifecycle",
                committed.Count,
                lifecycleCreated,
                alreadyTracked);

            // Phase F: per-frame member render/hide summary (rate-limited, shared key — per the
            // project's per-frame logging convention). Only meaningful when a Mission loops.
            if (loopUnits.Count > 0)
            {
                ParsekLog.VerboseRateLimited(Tag,
                    "ts-loop-member-render",
                    string.Format(ic,
                        "TS Mission loop active: units={0} created={1} hiddenOutOfWindow={2} UT {3:F1}",
                        loopUnits.Count, lifecycleCreated, loopMemberHidden, currentUT),
                    2.0);
            }

            RefreshTrackingStationVesselListAfterLifecycleMutation(
                createdBefore,
                destroyedBefore,
                refreshStockList,
                "tracking-station-lifecycle");
            EmitLifecycleSummary("tracking-station", currentUT);
        }

        private static void RefreshTrackingStationVesselListAfterLifecycleMutation(
            int createdBefore,
            int destroyedBefore,
            bool refreshStockList,
            string reason)
        {
            if (!refreshStockList)
                return;

            if (!ShouldRefreshTrackingStationVesselListAfterLifecycleMutation(
                    createdBefore,
                    destroyedBefore,
                    lifecycleCreatedThisTick,
                    lifecycleDestroyedThisTick))
            {
                return;
            }

            int created = lifecycleCreatedThisTick - createdBefore;
            int destroyed = lifecycleDestroyedThisTick - destroyedBefore;

            TryRefreshLiveTrackingStationVesselList(
                string.Format(ic,
                    "{0} created={1} destroyed={2}",
                    string.IsNullOrEmpty(reason) ? "tracking-station-lifecycle" : reason,
                    created,
                    destroyed));
        }

        internal static bool ShouldRefreshTrackingStationVesselListAfterLifecycleMutation(
            int createdBefore,
            int destroyedBefore,
            int createdAfter,
            int destroyedAfter)
        {
            return createdAfter - createdBefore > 0
                || destroyedAfter - destroyedBefore > 0;
        }

        private static void RefreshTrackingStationGhosts(
            IReadOnlyList<Recording> committed,
            HashSet<string> suppressed,
            double currentUT,
            GhostPlaybackLogic.LoopUnitSet loopUnits)
        {
            if (vesselsByRecordingIndex.Count == 0)
                return;
            if (loopUnits == null)
                loopUnits = GhostPlaybackLogic.LoopUnitSet.Empty;

            List<(int idx, string reason)> toRemove = null;
            foreach (var kvp in vesselsByRecordingIndex)
            {
                int idx = kvp.Key;
                if (committed == null)
                {
                    // Bookkeeping teardown (no committed list this tick), NOT a deorbit handoff. Use a
                    // distinct reason so it never matches ShouldAssertTerminalOrbitBoundClamp and
                    // mis-fires the positive "clamped at the deorbit bound" assertion.
                    if (toRemove == null) toRemove = new List<(int, string)>();
                    toRemove.Add((idx, "tracking-station-committed-null"));
                    continue;
                }

                if (idx < 0 || idx >= committed.Count)
                {
                    // Bookkeeping teardown (stale index after the committed list shrank), NOT a deorbit
                    // handoff. Distinct reason so it stays out of the terminal-orbit-clamp gate.
                    if (toRemove == null) toRemove = new List<(int, string)>();
                    toRemove.Add((idx, "tracking-station-index-stale"));
                    continue;
                }

                var rec = committed[idx];

                // Slice (i): if this index became an overlap recording under the director-drive gate,
                // it is now owned by the per-instance store. Tear down its leftover per-index ghost so
                // there is no duplicate (the per-instance sweep created the N-per-cycle vessels).
                if (ShouldDriveOverlapPerInstance(rec, idx, committed, loopUnits))
                {
                    if (toRemove == null) toRemove = new List<(int, string)>();
                    toRemove.Add((idx, "overlap-handoff-to-per-instance"));
                    continue;
                }

                // Phase F: resolve the effective sample UT for this member under the shared Mission
                // span clock. A member outside its loop window this cycle is torn down (queued for
                // removal) so it stops rendering, exactly like the create pass skips it. Inert when
                // idx is not a unit member or loopUnits is Empty (effUT == currentUT, renderHidden
                // false): every live-UT read below becomes the unchanged live UT.
                double effUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                    idx,
                    rec != null ? rec.StartUT : currentUT,
                    rec != null ? rec.EndUT : currentUT,
                    currentUT,
                    loopUnits,
                    out bool renderHidden);
                if (renderHidden)
                {
                    if (toRemove == null) toRemove = new List<(int, string)>();
                    // Enrich the teardown reason with the loop role. This is the TRACKING-STATION teardown
                    // site (where the descent-revert bug was observed): a descent member tearing down here is
                    // the descent ghost being destroyed as its head leaves the descent clip and the loiter
                    // member takes the icon, so name it for correlation with the [ReaimDescent] DESCENT
                    // REVERTED line. Empty (byte-identical reason) for every non-descent member.
                    toRemove.Add((idx, "mission-loop-out-of-window"
                        + GhostPlaybackLogic.DescribeLoopMemberRoleForTeardown(idx, loopUnits)));
                    continue;
                }

                // Live-frame epoch shift for loop members (0 off the loop path). The orbit / point
                // were sampled at effUT, but the icon + arc patches run against the live tracking
                // station clock, so the seeded orbit epoch + stored arc bounds are pushed forward by
                // (currentUT - effUT) to put the icon at the replayed phase. See ApplyOrbitToVessel /
                // UpdateGhostOrbitFromStateVectors loopEpochShiftSeconds.
                double tsLoopEpochShift = currentUT - effUT;

                // Re-aim: for a re-aim loop owner, the map orbit line / icon follow the per-window
                // re-aimed transfer instead of the recorded geometry. Resolved ONCE here from the LIVE
                // clock (the same window the flight engine uses) and threaded through every effUT-based
                // orbit-source read below; identical to rec.OrbitSegments for every non-re-aim member.
                List<OrbitSegment> effectiveSegments =
                    ResolveEffectiveMapOrbitSegments(idx, rec, currentUT, loopUnits);

                bool isSuppressed = rec != null
                    && !string.IsNullOrEmpty(rec.RecordingId)
                    && suppressed != null
                    && suppressed.Contains(rec.RecordingId);
                bool realVesselExists = rec != null
                    && rec.VesselPersistentId != 0
                    && GhostPlaybackLogic.RealVesselExistsForRecording(rec);
                bool alreadyMaterialized =
                    IsTrackingStationRecordingMaterialized(rec, realVesselExists);
                uint pid = kvp.Value.persistentId;
                bool hasOrbitBounds = ghostOrbitBounds.TryGetValue(pid, out var bounds);
                bool isStateVector =
                    trackingStationStateVectorOrbitTrajectories.ContainsKey(idx);

                // Mirror the create-path (ResolveTrackingStationGhostSource) and flight-scene
                // (ParsekPlaybackPolicy.CheckPendingMapVessels) precedence: a wrapped/closed
                // OrbitSegment covering effUT is the trusted source and wins over the distrusted
                // OrbitalCheckpoint state-vector. `fromCheckpoint` (below) is re-derived from the
                // section at effUT every tick; without this guard a Segment-created looped ghost is
                // misrouted into the OrbitalCheckpoint state-vector refusal
                // (state-vector-from-orbital-checkpoint) the moment effUT advances into a
                // transfer/coast OrbitalCheckpoint section, freezing the orbit on its last segment
                // instead of advancing through the mission's later segments. The checkpoint path
                // stays available only for a genuine segment gap (no covering segment at effUT).
                // LOITER-GAP map-presence clamp (re-aim looped LANDING destination loiter), Tracking-Station
                // mirror of the flight path: the descent-trigger TRANSFER member's recorded loop clock (effUT)
                // sweeps the parking conic up to its end (ParkingConicEndUT = the destination loiter run end =
                // the deorbit point). PAST that point an UNCLAMPED lookup walks INTO the CONTIGUOUS
                // deorbit-transition OrbitSegment and draws that deorbit arc as the loiter orbit (the user sees
                // ~1/3 of an ellipse), then past the deorbit-arc end no segment covers effUT and the ghost is
                // removed ("tracking-station-expired") mid-loiter, so the parking conic stops rendering until the
                // descent fires. HOLD the segment-lookup sample UT at ParkingConicEndUT inside the gap (for the
                // covering-segment query AND the removal-reason query) so both keep returning the real recorded
                // PARKING-conic segment and the ghost stays alive on it. The clamp applies ONLY to these two
                // segment lookups; every other effUT read below (checkpoint, gap-glide, logging, downstream orbit
                // apply) still uses the live effUT. Layer B then stamps a per-pid line hold so GhostOrbitLinePatch
                // keeps the FULL parking ellipse drawn until the live descent trigger. False (byte-identical) for
                // every member except the destination transfer member (the owner, every ride-along in a
                // different/unshifted frame, and every descent-set member), for non-re-aim units, and once the
                // descent trigger fires (the transfer member then retires elsewhere).
                double segmentLookupUT = effUT;
                if (GhostPlaybackLogic.IsDescentTransferMemberInLoiterGap(loopUnits, idx, effUT))
                {
                    segmentLookupUT = GhostPlaybackLogic.ResolveLoiterGapConicEndUT(loopUnits, idx);
                    ParsekLog.VerboseRateLimited(Tag,
                        "ts-loiter-gap-clamp-" + idx,
                        string.Format(ic,
                            "TS loiter-gap clamp: member={0} effUT={1:F1} held segment-lookup UT at parkingConicEnd={2:F1} "
                            + "(re-aim descent transfer member in captureShift loiter gap; parking conic kept rendering)",
                            idx, effUT, segmentLookupUT),
                        5.0);
                    // Layer B: hold the parking-conic LINE visible through the loiter (past the live parking-conic
                    // bound) until the live descent trigger; GhostOrbitLinePatch consults the stamp.
                    StampParkingConicLineHoldForLoiterGap(
                        idx, pid, currentUT, loopUnits,
                        rec != null ? rec.StartUT : currentUT,
                        rec != null ? rec.EndUT : currentUT,
                        "TS", "ts-parking-conic-line-hold-");
                }
                else
                {
                    // Not in the gap (still on the conic, or the descent fired / loop wrapped): tear down any
                    // line hold for this ghost so the stamp never lingers into the next phase.
                    ClearParkingConicLineHold(pid);
                }

                // Same-body carry: a brief drop between two non-orbit-equivalent segments
                // in the same body frame (e.g., capture burn between two Mun orbits) used to
                // tear down and recreate the ProtoVessel, flashing the icon and orbit line.
                // Carry the previous segment across same-body intra-block gaps so the ghost
                // stays alive and only blinks when the body actually changes (SOI crossing).
                OrbitSegment? coveringSegment = (rec != null && effectiveSegments != null)
                    ? TrajectoryMath.FindOrbitSegmentOrSameBodyCarry(effectiveSegments, segmentLookupUT)
                    : (OrbitSegment?)null;
                bool segmentCoversEffUT = coveringSegment.HasValue;

                // Resolve the gap-points glide decision up-front so the covering-segment log
                // mirrors the flight scene: during the raise-gap glide the icon is driven from
                // recorded body-fixed POINTS (source=StateVector), not the carried segment, even
                // though FindOrbitSegmentOrSameBodyCarry still returns the carried previous segment.
                // Reporting covered=false / source=StateVector here (instead of covered=true /
                // source=Segment) makes the documented success signature (Segment -> StateVector ->
                // Segment across the gap) actually appear in the TS log, matching the flight trace.
                bool tsDriveGapFromPoints =
                    coveringSegment.HasValue
                    && !isStateVector
                    && ShouldDriveGapFromPoints(effectiveSegments, rec, effUT);
                LogMapCoveringSegmentChange("TS", idx, effUT, coveringSegment,
                    segmentCoversEffUT && !tsDriveGapFromPoints,
                    isStateVector || tsDriveGapFromPoints,
                    effectiveSegments != null ? effectiveSegments.Count : 0);

                int cachedStateVectorIndex = trackingStationStateVectorCachedIndices.TryGetValue(idx, out int cached)
                    ? cached
                    : -1;
                bool fromCheckpoint = TryResolveCheckpointStateVectorMapPoint(
                    rec,
                    effUT,
                    ref cachedStateVectorIndex,
                    out TrajectoryPoint checkpointPoint,
                    out _,
                    out _);
                if (fromCheckpoint && segmentCoversEffUT && !isStateVector)
                {
                    // Deferring to the trusted segment path below. Logged (rate-limited, shared key)
                    // so a log capture can confirm the precedence guard is active for looped ghosts
                    // crossing from a parking-orbit segment into a transfer OrbitalCheckpoint section.
                    ParsekLog.VerboseRateLimited(Tag,
                        "ts-checkpoint-defer-to-segment",
                        string.Format(ic,
                            "TS orbit source: OrbitalCheckpoint section at effUT={0:F1} deferring to covering OrbitSegment "
                            + "(idx={1} pid={2} segUT={3:F1}-{4:F1} body={5})",
                            effUT, idx, pid,
                            coveringSegment.Value.startUT, coveringSegment.Value.endUT,
                            coveringSegment.Value.bodyName ?? "(null)"),
                        2.0);
                }

                // LOITER-GAP clamp: pass segmentLookupUT (= conicEnd inside the gap, effUT otherwise) so the
                // removal-reason's internal FindOrbitSegmentOrSameBodyCarry keeps finding the parking conic and
                // does not expire the ghost in the captureShift gap. In the gap conicEnd < rec.EndUT, so the
                // state-vector branch (currentUT > rec.EndUT) is unaffected (effUT is also < rec.EndUT there).
                string removeReason = GetTrackingStationGhostRemovalReason(
                    rec,
                    isSuppressed,
                    alreadyMaterialized,
                    hasOrbitBounds,
                    isStateVector || fromCheckpoint,
                    segmentLookupUT,
                    // Loop member replaying in its window: keep the ghost alongside any
                    // persisted real terminal vessel (mirrors the create-path bypass).
                    loopMemberInWindow: tsLoopEpochShift != 0.0,
                    effectiveOrbitSegments: effectiveSegments);
                // Rescue path: a "tracking-station-expired" removal is suppressed if the
                // synthesizer can still produce a terminal-orbit seed. For a non-loop member
                // this is the unchanged contract (loop-aware flag false). For a same-body
                // loop member with no recorded OrbitSegments we allow the terminal-orbit
                // source path here too -- otherwise idx 18 (the no-segment Kerbin-return loop
                // member) loses its proto-vessel on the first refresh tick after creation.
                bool acceptTerminalOrbitForLoopSynthesis =
                    tsLoopEpochShift != 0.0
                    && IsTerminalOrbitSynthesisSafeForLoopMember(rec);
                if (string.Equals(removeReason, "tracking-station-expired", StringComparison.Ordinal)
                    && TryResolveEndpointTailForMapPresence(
                        rec,
                        effUT,
                        selectedSegment: null,
                        terminalMapPresenceRegion: IsTerminalMapPresenceRegion(rec, effUT),
                        out _,
                        out _,
                        out _,
                        acceptTerminalOrbitSource: acceptTerminalOrbitForLoopSynthesis))
                {
                    removeReason = null;
                }
                if (removeReason != null)
                {
                    if (toRemove == null) toRemove = new List<(int, string)>();
                    toRemove.Add((idx, removeReason));
                    continue;
                }

                if (ShouldConsumeCheckpointStateVectorForExistingGhost(fromCheckpoint, segmentCoversEffUT))
                {
                    trackingStationStateVectorCachedIndices[idx] = cachedStateVectorIndex;
                    if (UpdateGhostOrbitFromStateVectors(idx, rec, checkpointPoint, effUT,
                        loopEpochShiftSeconds: tsLoopEpochShift))
                    {
                        if (toRemove == null) toRemove = new List<(int, string)>();
                        toRemove.Add((idx, TrackingStationGhostSkipActiveReFlyRelativeUpdate));
                    }
                    continue;
                }

                // Gap-points glide (Tracking Station mirror of the flight-scene branch in
                // ParsekPlaybackPolicy.CheckPendingMapVessels): FindOrbitSegmentOrSameBodyCarry carried
                // the PREVIOUS segment forward across a same-body inter-segment gap, holding coveringSegment
                // true. For a real orbit RAISE (parking -> higher loiter) that freezes the icon on the stale
                // parking orbit and then teleports it ~1318 km onto the loiter orbit when effUT enters the
                // next segment. This ghost was created as a Segment ghost (isStateVector is false for an
                // Absolute gap in a HasOrbitSegments recording), so the isStateVector branch below never
                // reaches it -- a dedicated branch is required. Drive it from the recorded body-fixed POINTS
                // so it glides the raise arc. Gated to the NON-orbit-equivalent gap so the equivalent-orbit
                // carry (capture burn between two same orbits) stays byte-identical on the carry. Placed
                // AFTER the removeReason block so a genuine expiry still wins, and the continue bypasses the
                // segment-apply tail.
                if (tsDriveGapFromPoints)
                {
                    TrajectoryPoint? gapPoint = TrajectoryMath.BracketPointAtUT(
                        rec.Points, effUT, ref cachedStateVectorIndex);
                    trackingStationStateVectorCachedIndices[idx] = cachedStateVectorIndex;
                    if (gapPoint.HasValue)
                    {
                        // UpdateGhostOrbitFromStateVectors returns true ONLY in the active-re-fly
                        // suppression case (the ghost must be torn down this tick); false on the normal
                        // success path (ghost updated in place). Mirror the isStateVector branch below.
                        bool reFlySuppressed = UpdateGhostOrbitFromStateVectors(
                            idx, rec, gapPoint.Value, effUT,
                            stateVectorUpdateReason: "orbit-raise-gap-points",
                            loopEpochShiftSeconds: tsLoopEpochShift);
                        ParsekLog.VerboseRateLimited(Tag,
                            "ts-gap-points-glide-" + idx,
                            string.Format(ic,
                                "TS gap-points glide: member={0} effUT={1:F1} body={2} alt={3:F0} reFlySuppressed={4} " +
                                "(orbit-raise gap, icon glides recorded ascent instead of segment carry)",
                                idx, effUT, gapPoint.Value.bodyName ?? "(null)", gapPoint.Value.altitude, reFlySuppressed),
                            2.0);
                        if (reFlySuppressed)
                        {
                            if (toRemove == null) toRemove = new List<(int, string)>();
                            toRemove.Add((idx, TrackingStationGhostSkipActiveReFlyRelativeUpdate));
                        }
                        continue;
                    }
                    // No bracketing point (effUT precedes first / past last recorded point): fall through
                    // to the unchanged same-body carry / segment-apply path. Never force a single stale point.
                    ParsekLog.VerboseRateLimited(Tag,
                        "ts-gap-points-nobracket-" + idx,
                        string.Format(ic,
                            "TS gap-points glide skipped (no bracketing point): member={0} effUT={1:F1} " +
                            "(falling through to same-body segment carry)",
                            idx, effUT),
                        5.0);
                }

                if (isStateVector)
                {
                    TrajectoryPoint? pt = TrajectoryMath.BracketPointAtUT(
                        rec.Points,
                        effUT,
                        ref cachedStateVectorIndex);
                    trackingStationStateVectorCachedIndices[idx] = cachedStateVectorIndex;

                    if (!pt.HasValue)
                    {
                        if (toRemove == null) toRemove = new List<(int, string)>();
                        toRemove.Add((idx, "tracking-station-state-vector-expired"));
                        continue;
                    }

                    // PR #556 follow-up: a Relative-frame state-vector ghost
                    // must NOT be expired/removed just because the section is
                    // Relative — pre-#583 the only way to get a Relative
                    // currentUT here was a stale ghost the resolver would
                    // never recreate, so killing it was safe. After #583 the
                    // resolver creates these on purpose; the threshold check
                    // is meaningless for Relative-frame points (point.altitude
                    // is anchor-local dz, not geographic altitude) and would
                    // tear the ghost down every cycle, then the create path
                    // re-adds it next tick → flicker. Mirror the flight-scene
                    // gate in ParsekPlaybackPolicy.CheckPendingMapVessels and
                    // skip the altitude floor for Relative-frame points (the
                    // speed floor stays, see ShouldRemoveStateVectorOrbitForFrame);
                    // UpdateGhostOrbitFromStateVectors already dispatches on
                    // referenceFrame and resolves world position via the
                    // anchor for that branch.
                    bool inRelativeFrame = IsInRelativeFrame(rec, effUT);
                    // The atmosphere read stays behind the frame check: it touches
                    // FlightGlobals, which a Relative-frame point must not pay for.
                    double atmosphereDepth = inRelativeFrame
                        ? 0.0
                        : GetAtmosphereDepth(pt.Value.bodyName);
                    if (ShouldRemoveStateVectorOrbitForFrame(
                        inRelativeFrame,
                        pt.Value.altitude,
                        pt.Value.velocity.magnitude,
                        atmosphereDepth))
                    {
                        if (toRemove == null) toRemove = new List<(int, string)>();
                        toRemove.Add((idx, "below-state-vector-threshold"));
                        continue;
                    }

                    if (UpdateGhostOrbitFromStateVectors(idx, rec, pt.Value, effUT,
                        loopEpochShiftSeconds: tsLoopEpochShift))
                    {
                        if (toRemove == null) toRemove = new List<(int, string)>();
                        toRemove.Add((idx, TrackingStationGhostSkipActiveReFlyRelativeUpdate));
                    }
                    continue;
                }

                // A ghost with no stored orbit bounds is normally skipped (it is not a live
                // segment ghost). EXCEPTION: when a covering OrbitSegment exists at effUT we must
                // still (re)apply it. This is the recovery frame after a gap-points glide cleared
                // ghostOrbitBounds via UpdateGhostOrbitFromStateVectors: as soon as effUT enters
                // the next real segment (e.g. the loiter orbit after the raise gap) the segment-apply
                // below re-seeds the orbit + re-populates ghostOrbitBounds, snapping the icon onto the
                // loiter orbit instead of stranding it on the stale state-vector seed. With no covering
                // segment the original skip stands. bounds==(0,0) here forces the apply below.
                if (!hasOrbitBounds && !coveringSegment.HasValue)
                    continue;

                // Reuse the covering segment resolved above (same effUT, same OrbitSegments list).
                OrbitSegment? seg = coveringSegment;
                TrackingStationGhostSource orbitUpdateSource = TrackingStationGhostSource.Segment;

                // The synthetic endpoint-tail (terminal-orbit) seed is a NON-LOOP concept: it shows
                // where a finished flight ended up, seeded from the recorded historical body
                // rotation/position at the recording's end. For a LOOP member that seed does not
                // survive the loop epoch shift in the cross-body case: a cross-body terminal
                // (e.g. a Mun orbit seeded from the Mun's RECORDED position) lands the
                // proto-vessel tens of millions of metres off (~181 Mm in the Mun case) instead
                // of beside the live Mun.
                //
                // Branch rules under plan section 1.5 (refresh path):
                //
                // - FIRST endpoint-tail branch (covering-segment OVERRIDE, below): stays
                //   UNCONDITIONALLY suppressed for loop members (the 181 Mm bug class). When a
                //   covering OrbitSegment exists at effUT, that segment IS the correct loop
                //   replay orbit and the historical-rotation override would corrupt it.
                //   Non-loop members keep the unchanged behaviour.
                //
                // - SECOND endpoint-tail branch (no-covering-segment FALLBACK, below): now
                //   CONDITIONALLY enabled for same-body loop members via
                //   IsTerminalOrbitSynthesisSafeForLoopMember. A recording whose last sampled
                //   point body matches its TerminalOrbitBody (e.g. idx 18, Kerbin-return after
                //   Mun takeoff, no recorded OrbitSegments) is shift-safe because the seeded
                //   inertial Keplerian elements propagate body-rotation-frame-independently
                //   under Orbit.SetOrbit(epoch + shift) (see plan section 1.5: Planetarium OrbitalFrame
                //   is built from LAN/inc/argPe; historical body rotation enters at SEED
                //   CONSTRUCTION only). Cross-body terminals (mismatched last-point body and
                //   TerminalOrbitBody) stay suppressed via the predicate.
                //
                // hasFutureOrbitSegment additionally gates the no-covering-segment fallback for
                // NON-loop members: a mid-recording gap (orbit segments still ahead, reachable
                // via rewind/warp) must remove the ghost rather than jump to the terminal orbit;
                // the endpoint tail stays reserved for the genuine terminal region (no future
                // segment).
                bool hasFutureOrbitSegment = HasOrbitSegmentStartingAfter(effectiveSegments, effUT);
                bool endpointTailOverrideAllowed = EndpointTailAllowedInTrackingStationUpdate(tsLoopEpochShift);
                // acceptTerminalOrbitForLoopSynthesis is computed above (rescue-path block).

                if (seg.HasValue
                    && endpointTailOverrideAllowed
                    && TryResolveEndpointTailForMapPresence(
                        rec,
                        effUT,
                        seg.Value,
                        IsTerminalMapPresenceRegion(rec, effUT),
                        out OrbitSegment endpointTailSegment,
                        out _,
                        out _,
                        acceptTerminalOrbitSource: false))
                {
                    seg = endpointTailSegment;
                    orbitUpdateSource = TrackingStationGhostSource.EndpointTail;
                }
                else if (!seg.HasValue
                    && (endpointTailOverrideAllowed || acceptTerminalOrbitForLoopSynthesis)
                    && !hasFutureOrbitSegment
                    && TryResolveEndpointTailForMapPresence(
                        rec,
                        effUT,
                        selectedSegment: null,
                        terminalMapPresenceRegion: IsTerminalMapPresenceRegion(rec, effUT),
                        out endpointTailSegment,
                        out _,
                        out _,
                        acceptTerminalOrbitSource: acceptTerminalOrbitForLoopSynthesis))
                {
                    seg = endpointTailSegment;
                    orbitUpdateSource = TrackingStationGhostSource.EndpointTail;
                }
                if (!seg.HasValue)
                {
                    if (toRemove == null) toRemove = new List<(int, string)>();
                    toRemove.Add((idx, hasFutureOrbitSegment
                        ? "gap-between-orbit-segments"
                        : "tracking-station-expired"));
                    continue;
                }
                // For loop members the stored bounds are shifted into the live frame while
                // seg.Value carries the raw recorded UTs, so this comparison stays true and the
                // orbit re-applies each tick (re-snapping the per-cycle shift); for non-loop members
                // (shift 0) it skips an unchanged orbit exactly as before.
                if (bounds.startUT != seg.Value.startUT + tsLoopEpochShift
                    || bounds.endUT != seg.Value.endUT + tsLoopEpochShift)
                    UpdateGhostOrbitForRecording(idx, seg.Value, orbitUpdateSource,
                        loopEpochShiftSeconds: tsLoopEpochShift,
                        effectiveOrbitSegments: effectiveSegments);
            }

            if (toRemove == null)
                return;

            for (int i = 0; i < toRemove.Count; i++)
            {
                int idx = toRemove[i].idx;
                string reason = toRemove[i].reason;
                RemoveGhostVesselForRecording(idx, reason);
                trackingStationStateVectorOrbitTrajectories.Remove(idx);
                trackingStationStateVectorCachedIndices.Remove(idx);
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Removed tracking-station ghost #{0} — UT {1:F1} reason={2}",
                        idx, currentUT, reason));
            }
        }

        internal static void TryRunTrackingStationSpawnHandoffs(
            IReadOnlyList<Recording> committed,
            double currentUT,
            GhostPlaybackLogic.LoopUnitSet loopUnits = null)
        {
            if (committed == null || committed.Count == 0)
                return;
            if (loopUnits == null)
                loopUnits = GhostPlaybackLogic.LoopUnitSet.Empty;

            var chains = GhostChainWalker.ComputeAllGhostChains(RecordingStore.CommittedTrees, currentUT);
            var timelineInactiveIds = CurrentTimelineInactiveRecordingIds(committed);
            List<int> eligibleIndices = null;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                var (needsSpawn, _) = ShouldSpawnAtTrackingStationEnd(
                    rec,
                    currentUT,
                    chains,
                    timelineInactiveIds);
                if (!needsSpawn)
                    continue;

                // BUG-B: in the unconditional per-frame Tracking Station lifecycle, do NOT
                // auto-spawn a NEW duplicate vessel for a purely-historical recording (the
                // player progressed past it in normal forward time and never rewound to
                // replay it). The adoption path (the recorded vessel already exists live)
                // is harmless and still runs; the active re-fly session is exempt. A looping
                // / mission-unit member is NOT exempt here: this is its first-run terminal
                // spawn, a real spawn gated like any recording's (only its render ignores
                // the scope gate). Explicit "Warp to Spawn"
                // (TryRunTrackingStationSpawnHandoffForIndex / ForRecordingId) does not route
                // through here, so a user-initiated spawn is never gated. Mirrors the flight
                // and Space Center spawn gates.
                bool realVesselExists = rec.VesselPersistentId != 0
                    && GhostPlaybackLogic.RealVesselExistsForRecording(rec);
                bool wouldAdoptExistingVessel =
                    ShouldSkipTrackingStationDuplicateSpawn(rec, realVesselExists);
                if (!wouldAdoptExistingVessel
                    && SessionSuppressionState.ActiveMarker == null)
                {
                    double activationStartUT = GhostPlaybackEngine.ResolveGhostActivationStartUT(rec);
                    PlaybackScopeTracker.NotePlayhead(rec.RecordingId, currentUT, activationStartUT);
                    bool loopingLike = rec.LoopPlayback || loopUnits.IsMember(i);
                    if (GhostPlaybackLogic.ResolveHistoricalNeverReplayed(
                            loopingLike,
                            reFlyActive: false,
                            scopeHistorical: PlaybackScopeTracker.IsHistoricalNeverReplayed(
                                rec.RecordingId, currentUT, activationStartUT),
                            forSpawn: true))
                    {
                        ParsekLog.VerboseRateLimited(Tag,
                            "ts-spawn-historical-" + i.ToString(ic),
                            string.Format(ic,
                                "Tracking-station spawn handoff skipped #{0} \"{1}\": historical "
                                + "(never replayed during normal forward play)",
                                i, rec.VesselName ?? "(null)"),
                            5.0);
                        continue;
                    }
                }

                if (eligibleIndices == null)
                    eligibleIndices = new List<int>();
                eligibleIndices.Add(i);
            }

            if (eligibleIndices == null || eligibleIndices.Count == 0)
                return;

            GhostPlaybackLogic.InvalidateVesselCache();

            for (int i = 0; i < eligibleIndices.Count; i++)
            {
                RunTrackingStationSpawnHandoffForEligibleIndex(
                    committed,
                    eligibleIndices[i],
                    currentUT,
                    chains);
            }
        }

        internal static bool TryRunTrackingStationSpawnHandoffForIndex(
            IReadOnlyList<Recording> committed,
            int index,
            double currentUT,
            bool reselectSpawnedVessel = false)
        {
            if (committed == null || index < 0 || index >= committed.Count)
                return false;

            var chains = GhostChainWalker.ComputeAllGhostChains(RecordingStore.CommittedTrees, currentUT);
            var timelineInactiveIds = CurrentTimelineInactiveRecordingIds(committed);
            var (needsSpawn, _) = ShouldSpawnAtTrackingStationEnd(
                committed[index],
                currentUT,
                chains,
                timelineInactiveIds);
            if (!needsSpawn)
                return false;

            GhostPlaybackLogic.InvalidateVesselCache();
            RunTrackingStationSpawnHandoffForEligibleIndex(
                committed,
                index,
                currentUT,
                chains,
                reselectSpawnedVessel);
            return true;
        }

        internal static bool TryRunTrackingStationSpawnHandoffForRecordingId(
            IReadOnlyList<Recording> committed,
            string recordingId,
            double currentUT,
            bool reselectSpawnedVessel = false)
        {
            if (!TryGetCommittedRecordingById(
                    committed,
                    recordingId,
                    out int index,
                    out Recording _))
            {
                return false;
            }

            return TryRunTrackingStationSpawnHandoffForIndex(
                committed,
                index,
                currentUT,
                reselectSpawnedVessel);
        }

        private static void RunTrackingStationSpawnHandoffForEligibleIndex(
            IReadOnlyList<Recording> committed,
            int index,
            double currentUT,
            Dictionary<uint, GhostChain> chains,
            bool reselectSpawnedVessel = false)
        {
            Recording rec = committed[index];
            TrackingStationSpawnHandoffState handoffState =
                CaptureTrackingStationSpawnHandoffState(index);
            bool realVesselExists = rec.VesselPersistentId != 0
                && GhostPlaybackLogic.RealVesselExistsForRecording(rec);
            bool alreadyMaterialized = ShouldSkipTrackingStationDuplicateSpawn(
                rec,
                realVesselExists);

            // The Ghost Chain Rule here: a live vessel with a chain's claimed pid after a rewind
            // to before the chain's tip is the claimed vessel in its pre-claim form, not the
            // tip's result. It is replaced by the tip snapshot instead of adopted (same pid) or
            // left beside the tip (the tip got a new pid on undock).
            StaleVesselReplacement staleReplacement = ChainTipStaleVessel.TryReplaceStaleLiveVessel(
                rec, chains, "TRACKSTATION", index);
            bool replacedStaleVessel = staleReplacement != null;
            if (replacedStaleVessel)
            {
                realVesselExists = rec.VesselPersistentId != 0
                    && GhostPlaybackLogic.RealVesselExistsForRecording(rec);
                alreadyMaterialized = ShouldSkipTrackingStationDuplicateSpawn(rec, realVesselExists);
                if (alreadyMaterialized)
                {
                    // The tip's own identity is live after all (this read covers loaded vessels,
                    // the replacement's also proto ones): the exit below adopts it, so the
                    // removed claimed vessel goes back first. No exit may leave it removed.
                    ChainTipStaleVessel.AbortReplacement(rec, staleReplacement, "tip identity live in the Tracking Station");
                    staleReplacement = null;
                    replacedStaleVessel = false;
                }
            }

            if (alreadyMaterialized)
            {
                rec.VesselSpawned = true;
                rec.SpawnedVesselPersistentId = rec.VesselPersistentId;
                RemoveAllGhostPresenceForIndex(
                    index,
                    rec.VesselPersistentId,
                    "tracking-station-existing-real-vessel");
                RestoreTrackingStationSpawnHandoffState(
                    rec.VesselPersistentId,
                    handoffState,
                    "tracking-station-existing-real-vessel",
                    reselectSpawnedVessel);
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Tracking-station handoff skipped duplicate spawn for #{0} \"{1}\" — real vessel pid={2} already exists",
                        index,
                        rec.VesselName ?? "(null)",
                        rec.VesselPersistentId));
                return;
            }

            bool preserveIdentity = replacedStaleVessel
                || ShouldPreserveIdentityForTrackingStationSpawn(
                    chains,
                    rec,
                    realVesselExists);
            if (replacedStaleVessel)
            {
                // The removed vessel's map focus, navigation target and selection move to the
                // tip that replaces it, as they would from the tip's own ghost.
                StaleChainVesselFocus staleFocus = staleReplacement.Focus;
                handoffState = new TrackingStationSpawnHandoffState(
                    handoffState.GhostPid,
                    handoffState.WasNavigationTarget || staleFocus.WasNavigationTarget,
                    handoffState.WasMapFocus || staleFocus.WasMapFocus);
                reselectSpawnedVessel = reselectSpawnedVessel || staleFocus.WasTrackingStationSelected;
            }
            GhostPlaybackLogic.LogChainLoopFirstRunSpawn(
                Tag, "TRACKSTATION", index, rec, currentUT,
                !string.IsNullOrEmpty(rec.ChainId) && RecordingChainQueries.IsChainLooping(rec.ChainId));
            try
            {
                if (TrackingStationSpawnOverrideForTesting != null)
                    TrackingStationSpawnOverrideForTesting(rec, index, preserveIdentity);
                else
                    VesselSpawner.SpawnOrRecoverIfTooClose(rec, index, preserveIdentity);
            }
            finally
            {
                // A replacement whose tip spawned no vessel puts the removed vessel back.
                ChainTipStaleVessel.CompleteReplacement(rec, staleReplacement);
            }
            if (!rec.VesselSpawned)
                return;

            RemoveAllGhostPresenceForIndex(
                index,
                rec.VesselPersistentId,
                "tracking-station-spawn-handoff");

            if (rec.SpawnedVesselPersistentId != 0)
            {
                GhostPlaybackLogic.InvalidateVesselCache();
                RestoreTrackingStationSpawnHandoffState(
                    rec.SpawnedVesselPersistentId,
                    handoffState,
                    "tracking-station-spawn-handoff",
                    reselectSpawnedVessel);
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Tracking-station handoff spawned #{0} \"{1}\" pid={2} preserveIdentity={3}",
                        index,
                        rec.VesselName ?? "(null)",
                        rec.SpawnedVesselPersistentId,
                        preserveIdentity));
            }
            else if (rec.SpawnAbandoned)
            {
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Tracking-station handoff resolved #{0} \"{1}\" without spawning a vessel ({2})",
                        index,
                        rec.VesselName ?? "(null)",
                        VesselSpawner.IsSettledAsKscRetirement(rec) ? "retired at KSC" : "abandoned"));
            }
        }

        internal static (bool needsSpawn, string reason) ShouldSpawnAtTrackingStationEnd(
            Recording rec,
            double currentUT)
        {
            return ShouldSpawnAtTrackingStationEnd(rec, currentUT, chains: null);
        }

        internal static (bool needsSpawn, string reason) ShouldSpawnAtTrackingStationEnd(
            Recording rec,
            double currentUT,
            Dictionary<uint, GhostChain> chains)
        {
            // [ERS-exempt] Tracking Station materialization predicates operate
            // on raw committed recordings because action selections and map
            // ghosts carry raw recording ids/indices. Subtract explicit
            // supersede relations here so callers using the convenience
            // overload get the same display-effective spawn eligibility as
            // the batch handoff path.
            var timelineInactiveIds =
                CurrentTimelineInactiveRecordingIds(RecordingStore.CommittedRecordings);
            return ShouldSpawnAtTrackingStationEnd(
                rec,
                currentUT,
                chains,
                timelineInactiveIds);
        }

        internal static (bool needsSpawn, string reason) ShouldSpawnAtTrackingStationEnd(
            Recording rec,
            double currentUT,
            Dictionary<uint, GhostChain> chains,
            HashSet<string> relationSupersededIds)
        {
            IReadOnlyDictionary<string, TimelineInactiveReason> timelineInactiveIds = null;
            if (relationSupersededIds != null && relationSupersededIds.Count > 0)
            {
                var dict = new Dictionary<string, TimelineInactiveReason>(
                    relationSupersededIds.Count,
                    StringComparer.Ordinal);
                foreach (var id in relationSupersededIds)
                {
                    if (!string.IsNullOrEmpty(id))
                        dict[id] = TimelineInactiveReason.SupersededByRelation;
                }
                timelineInactiveIds = dict;
            }

            return ShouldSpawnAtTrackingStationEnd(
                rec,
                currentUT,
                chains,
                timelineInactiveIds);
        }

        private static (bool needsSpawn, string reason) ShouldSpawnAtTrackingStationEnd(
            Recording rec,
            double currentUT,
            Dictionary<uint, GhostChain> chains,
            IReadOnlyDictionary<string, TimelineInactiveReason> timelineInactiveIds)
        {
            if (rec == null)
                return (false, "null");

            if (RecordingStore.RewindUTAdjustmentPending)
                return (false, TrackingStationSpawnSkipRewindPending);

            if (currentUT < rec.EndUT)
                return (false, TrackingStationSpawnSkipBeforeEnd);

            if (!string.IsNullOrEmpty(rec.RecordingId)
                && timelineInactiveIds != null
                && timelineInactiveIds.TryGetValue(rec.RecordingId, out var inactiveReason))
            {
                return (false, inactiveReason == TimelineInactiveReason.RewindRetired
                    ? TrackingStationSpawnSkipRewindRetired
                    : TrackingStationSpawnSkipSupersededByRelation);
            }

            // Scene-agnostic #573 rewind lift: a standalone Rewind-to-Launch target the
            // player did not re-fly (no live same-craft vessel present) still spawns its
            // recorded terminal in the Tracking Station, identical to Flight and KSC.
            var spawnResult = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, false,
                treeContext: null,
                GhostPlaybackLogic.ResolveRewindSuppressionLiveLaunchPresence(rec));
            if (!spawnResult.needsSpawn)
                return spawnResult;

            if (RecordingChainQueries.IsChainMidSegment(rec))
                return (false, TrackingStationSpawnSkipIntermediateChainSegment);
            var chainSuppressed = GhostPlaybackLogic.ShouldSuppressSpawnForChain(chains, rec);
            if (chainSuppressed.suppressed)
                return (false, NormalizeTrackingStationSpawnSuppressionReason(chainSuppressed.reason));

            return spawnResult;
        }

        private static IReadOnlyDictionary<string, TimelineInactiveReason> CurrentTimelineInactiveRecordingIds(
            IReadOnlyList<Recording> committed)
        {
            var scenario = ParsekScenario.Instance;
            return EffectiveState.ComputeTimelineInactiveRecordingIds(
                committed,
                object.ReferenceEquals(null, scenario) ? null : scenario.RecordingSupersedes,
                object.ReferenceEquals(null, scenario) ? null : scenario.RecordingRewindRetirements);
        }

        internal static bool ShouldPreserveIdentityForTrackingStationSpawn(
            Dictionary<uint, GhostChain> chains,
            Recording rec,
            bool realVesselExists)
        {
            if (realVesselExists || rec == null || rec.VesselPersistentId == 0)
                return false;

            GhostChain chain = GhostChainWalker.FindChainForVessel(chains, rec.VesselPersistentId);
            return chain != null
                && !chain.IsTerminated
                && chain.TipRecordingId == rec.RecordingId;
        }

        internal static bool ShouldSkipTrackingStationDuplicateSpawn(
            Recording rec,
            bool realVesselExists)
        {
            // Unlike FLIGHT, Tracking Station never treats the last FLIGHT scene-entry PID
            // as permission to spawn over an already-live real vessel.
            return rec != null
                && rec.VesselPersistentId != 0
                && realVesselExists;
        }

        internal static string NormalizeTrackingStationSpawnSuppressionReason(string reason)
        {
            switch (reason)
            {
                case "intermediate ghost chain link":
                    return TrackingStationSpawnSkipIntermediateGhostChainLink;
                default:
                    return reason;
            }
        }

        /// <summary>
        /// Pure: should a tracking station ghost ProtoVessel be created for this recording?
        /// Only recordings that are not currently hidden by an already-started child and that
        /// have an active visible orbit segment, state-vector fallback, or a reached terminal
        /// orbital endpoint qualify.
        /// Returns a reason string for logging when skipped.
        /// </summary>
        internal static (bool shouldCreate, string skipReason) ShouldCreateTrackingStationGhost(
            Recording rec, bool isSuppressed, double currentUT)
        {
            TrackingStationGhostSource source = ResolveTrackingStationGhostSource(
                rec,
                isSuppressed,
                currentUT,
                out _,
                out string skipReason);
            return (source != TrackingStationGhostSource.None, skipReason);
        }

        /// <summary>
        /// Create ghost ProtoVessels for committed recordings suitable for tracking station display.
        /// Chain-aware: only creates ghosts for recordings that are not currently hidden by an
        /// already-started child recording. Skips debris, non-orbital terminal states, and
        /// recordings without orbital data at the current UT.
        /// </summary>
        /// <summary>
        /// Build the Mission <see cref="GhostPlaybackLogic.LoopUnitSet"/> for the Tracking Station
        /// one-shot startup create path (<see cref="CreateGhostVesselsFromCommittedRecordings"/>).
        /// That path runs before the addon's per-frame <c>ParsekTrackingStation.DriveMissionLoopUnits</c>
        /// has populated its cached set (the <c>SpaceTracking.Awake</c> precreate runs before
        /// <c>ParsekTrackingStation.Start</c>), and the Harmony precreate is a static method with no
        /// addon instance to read a cached set from, so the set must be built here. Mirrors the exact
        /// builder inputs of <c>DriveMissionLoopUnits</c> (auto-loop interval, live-body phase-lock seam,
        /// transited-body rotation mode) so startup and the per-frame lifecycle phase-lock identically,
        /// INCLUDING the supply-route render union (REN-1): the route-driving backing missions
        /// (<see cref="Parsek.Logistics.RouteGhostDriverSelector.SelectGhostDrivingBackingMissions"/>) are
        /// folded onto <c>MissionStore.Missions</c> here exactly as the per-frame seam does, so a route
        /// member is sampled at its loop-shifted effUT on the very first TS-entry tick instead of flashing
        /// at its raw-live-UT terminal/endpoint state for one tick.
        /// Returns <see cref="GhostPlaybackLogic.LoopUnitSet.Empty"/> when there are no looping missions AND
        /// no ghost-driving routes; <see cref="GhostPlaybackLogic.ResolveTrackingStationSampleUT"/> is then a
        /// no-op (effUT == liveUT, renderHidden false) and the startup create is byte-identical to its
        /// pre-loop-aware behavior.
        /// </summary>
        internal static GhostPlaybackLogic.LoopUnitSet BuildStartupTrackingStationLoopUnits(
            IReadOnlyList<Recording> committed)
        {
            // Supply-route render union (REN-1): fold the route-driving backing missions into the
            // startup builder input, mirroring the per-frame seam (ParsekTrackingStation.DriveMissionLoopUnits).
            // Without this, the one-shot TS startup create samples a route member at its raw live UT (far
            // past its recorded window) for ~one tick before the first lifecycle pass corrects it, flashing
            // the ghost at its terminal/endpoint state. Reads only RouteStore.CommittedRoutes (outside the
            // ERS/ELS grep gate, mirrors the selector). The selector is pure w.r.t. Unity: the UT is only
            // threaded into RouteBackingMission.BuildMission's diagnostic log; render phase is loop-clock
            // owned. Use the SAME UT source as the startup-create pass (CurrentUTNow), not a fresh
            // Planetarium read, since SpaceTracking.Awake can precreate before Planetarium is ready.
            // Skip the UT read + selector entirely when there are no committed routes: the selector
            // returns an empty list regardless of UT in that case, and the UT is only threaded into a
            // per-route diagnostic log. This keeps the no-route path from touching the live UT seam
            // (Planetarium) before any route exists.
            IReadOnlyList<Parsek.Logistics.Route> committedRoutes =
                Parsek.Logistics.RouteStore.CommittedRoutes;
            int committedRouteCount = committedRoutes != null ? committedRoutes.Count : 0;
            IReadOnlyList<Mission> routeMissions;
            if (committedRouteCount == 0)
            {
                routeMissions = System.Array.Empty<Mission>();
            }
            else
            {
                double routeSelectUT = CurrentUTNow();
                routeMissions =
                    Parsek.Logistics.RouteGhostDriverSelector.SelectGhostDrivingBackingMissions(
                        committedRoutes, routeSelectUT);
            }

            // No missions AND no route missions => no loop units (MissionLoopUnitBuilder.Build would
            // return Empty anyway). Early-out before touching settings / the live-body seam so the common
            // no-loop case is a pure no-op and the result is byte-identical to the pre-loop-aware startup
            // create.
            IReadOnlyList<Mission> baseMissions = MissionStore.Missions;
            int baseMissionCount = baseMissions != null ? baseMissions.Count : 0;
            // Recordings-tab loops replay on their own loop clock from the first TS tick too (the
            // per-frame set carries the same source via MissionLoopUnitBuilder.ResolveLiveUnits).
            if (baseMissionCount == 0 && routeMissions.Count == 0)
                return GhostPlaybackLogic.LoopUnitSet.Empty.WithStandaloneLoopSource(committed);

            // Union the route missions onto a fresh list alongside MissionStore.Missions, exactly like the
            // three per-frame host push seams, so the route members fold into the existing signature +
            // owner/member collision logging through the UNCHANGED builder (no edit to any locked file).
            var missions = new List<Mission>(baseMissionCount + routeMissions.Count);
            if (baseMissions != null)
                missions.AddRange(baseMissions);
            missions.AddRange(routeMissions);

            ParsekLog.Verbose("Mission",
                $"TS startup loop units: baseMissions={baseMissionCount.ToString(CultureInfo.InvariantCulture)} " +
                $"routeMissions={routeMissions.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"unioned={missions.Count.ToString(CultureInfo.InvariantCulture)}");

            double autoLoopIntervalSeconds = ParsekSettings.Current?.autoLoopIntervalSeconds
                                             ?? LoopTiming.DefaultLoopIntervalSeconds;
            // Phase-lock (mission periodicity): the same live-body seam the flight engine + KSC use.
            IBodyInfo bodyInfo = FlightGlobalsBodyInfo.Instance;
            // Pass the mode explicitly: MissionLoopUnitBuilder.Build defaults to Tight, but the
            // pinned product mode (Loose) is what every per-frame pass uses, so do not rely on the
            // builder's parameter default here.
            TransitedBodyRotationMode tbrMode = ParsekSettings.LandingBodyAlignmentMode;
            // Force-faithful product knob: same explicit-pass rule as tbrMode (the builder default is
            // false, but read the running setting so this startup pass matches the per-frame passes).
            bool forceFaithful = ParsekSettings.Current?.forceFaithfulLoopPlayback ?? false;
            return MissionLoopUnitBuilder.Build(
                missions,
                RecordingStore.CommittedTrees,
                committed,
                autoLoopIntervalSeconds,
                bodyInfo,
                tbrMode,
                forceFaithful).WithStandaloneLoopSource(committed);
        }

        internal static int CreateGhostVesselsFromCommittedRecordings()
        {
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || committed.Count == 0)
            {
                CachedTrackingStationSuppressedIds = new HashSet<string>();
                return 0;
            }

            // Step 1: identify recordings currently hidden by a started child.
            double currentUT = CurrentUTNow();
            var suppressed = FindTrackingStationSuppressedRecordingIds(committed, currentUT);
            AddActiveSessionSuppressedRecordingIds(suppressed, committed);
            CachedTrackingStationSuppressedIds = suppressed;
            GhostPlaybackLogic.InvalidateVesselCache();

            // Loop-aware startup: substitute the shared Mission span-clock loopUT (effUT) for the raw
            // live UT per loop-unit member, exactly like the per-frame UpdateTrackingStationGhostLifecycle
            // create pass. Without this a looped member is sampled at the raw live UT (far past its
            // recorded window), so the source resolver misclassifies it as "at its terminal" and seeds the
            // historical, unshifted terminal orbit (EndpointTail) off-position for ~one tick before the
            // first lifecycle pass corrects it (deferred item C / TS-entry wrong-position icon flash).
            // Built here because both startup call sites (ParsekTrackingStation.Start and the
            // SpaceTracking.Awake precreate) run before the addon's per-frame DriveMissionLoopUnits.
            // The LIVE view, like the per-frame pass: a unit still before its first loop instance is
            // left out, so its first run seeds as an ordinary recording.
            GhostPlaybackLogic.LoopUnitSet loopUnits =
                BuildStartupTrackingStationLoopUnits(committed).LiveAt(currentUT);

            int created = 0, skippedDebris = 0, skippedSuppressed = 0, skippedSpawned = 0;
            int skippedTerminal = 0, skippedBeforeActivation = 0;
            int skippedBeforeTerminalOrbit = 0, skippedNoOrbit = 0;
            int skippedEndpointConflict = 0, skippedUnseedableTerminalOrbit = 0;
            int loopMemberHidden = 0;
            int sourceVisibleSegment = 0, sourceTerminalOrbit = 0, sourceEndpointTail = 0, sourceStateVector = 0;
            var sourceBatch = new TrackingStationGhostSourceBatch("tracking-station-startup");

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];

                // Phase F: substitute the shared Mission span-clock loopUT for the live UT when this
                // committed index is a loop-unit member, and a Recordings-tab loop's own loop-clock UT
                // when it is not. Inert (effUT == currentUT, renderHidden false) for every other
                // recording and when loopUnits is Empty. A member outside its loop window this
                // cycle is skipped (no ghost created); the first lifecycle tick after TS entry creates /
                // removes it as its window dictates.
                double effUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                    i, rec.StartUT, rec.EndUT, currentUT, loopUnits, out bool renderHidden);
                if (renderHidden)
                {
                    loopMemberHidden++;
                    continue;
                }

                bool isSuppressed = suppressed.Contains(rec.RecordingId);
                bool realVesselExists = rec.VesselPersistentId != 0
                    && GhostPlaybackLogic.RealVesselExistsForRecording(rec);
                // Step 2: suppress this rec's OWN loop-ghost double ONLY while its
                // guid-gated launch-matched live vessel is loaded AND it was the LIVE
                // docking anchor of an in-window relative member this-or-last frame (the
                // Step-1 live-bind event), NOT for the whole loop. Mirrors the lifecycle
                // pass; realVesselExists is guid-gated so a same-craft different-launch
                // vessel never suppresses. Best-effort on this map/TS path: the
                // live-bind is stamped at member resolution (ghost (re)creation), not
                // per on-rails frame, so the duplicate can briefly show until the next
                // resolve (see WasLiveBoundThisOrLastFrame).
                bool liveLaunchMatchedAnchorOfActiveMember = realVesselExists
                    && RelativeAnchorResolver.WasLiveBoundThisOrLastFrame(rec.RecordingId);
                int cachedStateVectorIndex = trackingStationStateVectorCachedIndices.TryGetValue(i, out int cached)
                    ? cached
                    : -1;

                TrackingStationGhostSource source = ResolveTrackingStationGhostSourceCore(
                    rec,
                    isSuppressed,
                    realVesselExists,
                    effUT,
                    ref cachedStateVectorIndex,
                    out OrbitSegment segment,
                    out TrajectoryPoint stateVectorPoint,
                    out string skipReason,
                    sourceBatch,
                    i,
                    "tracking-station-startup",
                    // Loop member replaying in its window (effUT != live currentUT): allow the map ghost
                    // to be created alongside any persisted real terminal vessel, matching the lifecycle pass.
                    loopMemberInWindow: (currentUT - effUT) != 0.0,
                    liveLaunchMatchedAnchorOfActiveMember: liveLaunchMatchedAnchorOfActiveMember,
                    // COSMETIC fix: retire ONLY the descent-trigger DESTINATION transfer member's ghost
                    // (TransferMemberIndex) once the shared descent has handed off / landed (phase Descent/Done at
                    // the LIVE currentUT) so the one-shot startup create never seeds the sub-surface endpoint-tail
                    // looping ghost. Byte-identical off for the owner / ride-alongs (different/unshifted frame) /
                    // descent-set members / non-re-aim unit, and pre-seam.
                    transferMemberDescentContinuation:
                        GhostPlaybackLogic.IsTransferMemberDescentContinuation(
                            loopUnits, i, currentUT, rec.StartUT, rec.EndUT));
                trackingStationStateVectorCachedIndices[i] = cachedStateVectorIndex;
                if (source == TrackingStationGhostSource.None)
                {
                    if (skipReason == "debris") skippedDebris++;
                    else if (skipReason == TrackingStationGhostSkipSuppressed) skippedSuppressed++;
                    else if (skipReason == TrackingStationGhostSkipAlreadySpawned) skippedSpawned++;
                    // Step-2 live-anchor double (live-bind event) is an already-materialized
                    // duplicate; bucket it with skippedSpawned so it is not mis-attributed
                    // to skippedNoOrbit in the startup summary.
                    else if (skipReason == TrackingStationGhostSkipLiveAnchorDouble) skippedSpawned++;
                    else if (skipReason == TrackingStationGhostSkipEndpointConflict) skippedEndpointConflict++;
                    else if (skipReason == TrackingStationGhostSkipUnseedableTerminalOrbit) skippedUnseedableTerminalOrbit++;
                    else if (skipReason != null && skipReason.StartsWith("terminal")) skippedTerminal++;
                    else if (skipReason == "before-activation") skippedBeforeActivation++;
                    else if (skipReason == "before-terminal-orbit") skippedBeforeTerminalOrbit++;
                    else skippedNoOrbit++;
                    continue;
                }

                if (source == TrackingStationGhostSource.TerminalOrbit)
                    sourceTerminalOrbit++;
                else if (source == TrackingStationGhostSource.EndpointTail)
                    sourceEndpointTail++;
                else if (source == TrackingStationGhostSource.Segment)
                    sourceVisibleSegment++;
                else if (IsStateVectorGhostSource(source))
                    sourceStateVector++;

                // Loop-shift the orbit + body-frame cache at create-time (effUT seed + tsLoopEpochShift)
                // so the orbit-line and arc-clip patches see correctly-shifted bounds on the very first
                // frame, matching the lifecycle create pass. Zero shift for non-loop members (effUT ==
                // currentUT), so their seed is byte-identical to before.
                double tsLoopEpochShift = currentUT - effUT;
                Vessel v = CreateGhostVesselFromSource(
                    i,
                    rec,
                    source,
                    segment,
                    stateVectorPoint,
                    effUT,
                    loopEpochShiftSeconds: tsLoopEpochShift);

                if (v != null)
                {
                    created++;
                    if (IsStateVectorGhostSource(source))
                    {
                        trackingStationStateVectorOrbitTrajectories[i] = rec;
                    }
                    else
                    {
                        trackingStationStateVectorOrbitTrajectories.Remove(i);
                    }
                }
            }

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "CreateGhostVesselsFromCommittedRecordings: created={0} from {1} recordings " +
                    "sources(visibleSegment={2} terminalOrbit={3} endpointTail={4} stateVector={5}) " +
                    "(skipped: debris={6} suppressed={7} spawned={8} terminal={9} beforeActivation={10} " +
                    "beforeTerminalOrbit={11} noOrbit={12} endpointConflict={13} terminalUnseedable={14} " +
                    "loopMemberHidden={15})",
                    created, committed.Count,
                    sourceVisibleSegment, sourceTerminalOrbit, sourceEndpointTail, sourceStateVector,
                    skippedDebris, skippedSuppressed, skippedSpawned, skippedTerminal,
                    skippedBeforeActivation, skippedBeforeTerminalOrbit, skippedNoOrbit,
                    skippedEndpointConflict, skippedUnseedableTerminalOrbit, loopMemberHidden));

            sourceBatch.Log(
                "CreateGhostVesselsFromCommittedRecordings",
                committed.Count,
                created,
                alreadyTracked: 0);

            return created;
        }

        /// <summary>
        /// Decide whether an already-materialized Tracking Station ghost should retire on this
        /// lifecycle tick, and if so return the removal reason string to log/store.
        /// </summary>
        internal static string GetTrackingStationGhostRemovalReason(
            Recording rec, bool isSuppressed, bool hasOrbitBounds, double currentUT)
        {
            return GetTrackingStationGhostRemovalReason(
                rec,
                isSuppressed,
                false,
                hasOrbitBounds,
                false,
                currentUT);
        }

        internal static string GetTrackingStationGhostRemovalReason(
            Recording rec,
            bool isSuppressed,
            bool alreadyMaterialized,
            bool hasOrbitBounds,
            bool isStateVector,
            double currentUT,
            bool loopMemberInWindow = false,
            List<OrbitSegment> effectiveOrbitSegments = null)
        {
            // Degenerate teardown (no recording behind this ghost index), NOT a genuine end-of-orbit
            // handoff. Distinct reason so it never matches ShouldAssertTerminalOrbitBoundClamp; the
            // genuine terminal is the no-covering-segment return at the bottom of this method.
            if (rec == null)
                return "tracking-station-recording-missing";

            // GUI-P1 retire side of the create gate in ResolveMapPresenceGhostSource: the
            // player unticked this recording's playback box, so an already-materialized
            // Tracking Station ghost retires on this lifecycle tick (icon + orbit line + the
            // stock TS row all go with the proto). Pure: reads only the recording's own flag,
            // so re-ticking the box stops returning a removal reason immediately and the
            // create path re-seeds the proto on the same tick.
            if (IsMapPresenceHiddenByPlaybackToggle(rec))
                return TrackingStationGhostSkipPlaybackDisabled;

            if (isSuppressed)
                return "tracking-station-child-started";

            // Mirror the create-path bypass: a Mission-loop member replaying inside its
            // window keeps its map ghost even when a persisted real terminal vessel exists,
            // so the looped leg's trajectory follows the ghost. Without this the very next
            // refresh tick after creation would tear the ghost down again (create/remove
            // churn) for a member like the Mun-return leg whose mission left a real craft
            // parked at its terminal. Non-loop members are unaffected (flag false).
            if (alreadyMaterialized && !loopMemberInWindow)
                return "tracking-station-spawned-real-vessel";

            if (isStateVector)
                return currentUT > rec.EndUT
                    ? "tracking-station-state-vector-expired"
                    : null;

            if (!hasOrbitBounds)
                return null;

            // Same-body carry: don't expire the ghost in a brief intra-body-frame gap
            // between two non-orbit-equivalent segments (e.g., capture burn). Only an
            // actual body change or end-of-recording should remove the ghost.
            OrbitSegment? seg = TrajectoryMath.FindOrbitSegmentOrSameBodyCarry(
                effectiveOrbitSegments ?? rec.OrbitSegments, currentUT);
            return seg.HasValue ? null : "tracking-station-expired";
        }

        private static bool HasTrackingStationChildStarted(Recording child, double currentUT)
        {
            return child != null
                && child.TryGetGhostActivationStartUT(out double startUT)
                && currentUT >= startUT;
        }
    }
}
