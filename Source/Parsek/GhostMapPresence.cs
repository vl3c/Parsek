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
    /// <summary>
    /// Manages lightweight ProtoVessel-based map presence for ghost vessels.
    /// Creates tracking station entries, orbit lines, and navigation targeting
    /// for ghost chains with orbital data. Ghost ProtoVessels are transient —
    /// created on chain init, destroyed on chain resolve, stripped from saves.
    ///
    /// The canonical ghost identification check is IsGhostMapVessel(persistentId).
    /// Every FlightGlobals.Vessels iteration and vessel GameEvent handler in Parsek
    /// must check this before processing a vessel.
    /// </summary>
    // [ERS-exempt — Phase 3] GhostMapPresence keys its ghost-vessel dictionaries
    // (vesselsByRecordingIndex, vesselPidToRecordingIndex) by committed recording
    // index; callers pass raw indices through this contract. Routing through
    // EffectiveState.ComputeERS() here would de-align the index space and break
    // every consumer. The file stays on the grep-audit allowlist pending a
    // recording-id-keyed refactor.
    // TODO(phase 6+): migrate GhostMapPresence to recording-id-keyed storage.
    internal static partial class GhostMapPresence
    {
        internal enum TrackingStationGhostSource
        {
            None = 0,
            Segment = 1,
            TerminalOrbit = 2,
            StateVector = 3,
            StateVectorSoiGap = 4,
            EndpointTail = 5
        }

        internal static bool IsStateVectorGhostSource(TrackingStationGhostSource source)
        {
            return source == TrackingStationGhostSource.StateVector
                || source == TrackingStationGhostSource.StateVectorSoiGap;
        }

        /// <summary>
        /// Pure: a resolved ghost source the deferred map-create pass is willing to materialize
        /// (everything except <see cref="TrackingStationGhostSource.None"/>): a covering Segment,
        /// either state-vector flavor, the loop-synthesis TerminalOrbit fallback, or the EndpointTail
        /// consistency fix. Extracted byte-for-byte from the inline create-acceptance check.
        /// </summary>
        internal static bool IsMapCreateAcceptedSource(TrackingStationGhostSource source)
        {
            return source == TrackingStationGhostSource.Segment
                || IsStateVectorGhostSource(source)
                || source == TrackingStationGhostSource.TerminalOrbit
                || source == TrackingStationGhostSource.EndpointTail;
        }

        /// <summary>
        /// Pure: a resolved ghost source that populates a valid <c>segment</c> out-param (Segment,
        /// the loop-synthesis no-segment TerminalOrbit fallback, or EndpointTail, which populates the
        /// segment exactly like TerminalOrbit). Used by the state-vector update pass to decide whether
        /// to consume the segment branch instead of falling through to the flat point path.
        /// </summary>
        internal static bool IsSegmentBearingGhostSource(TrackingStationGhostSource source)
        {
            return source == TrackingStationGhostSource.Segment
                || source == TrackingStationGhostSource.TerminalOrbit
                || source == TrackingStationGhostSource.EndpointTail;
        }

        /// <summary>
        /// Tracking-station per-tick orbit-source precedence for an already-created ghost:
        /// a wrapped/closed OrbitSegment covering the effective UT is the trusted source and
        /// wins over a co-located OrbitalCheckpoint state-vector. The checkpoint state-vector is
        /// distrusted (stale-frame interpretation reintroduced the #571/#584 wrong-position class)
        /// and is only valid for a genuine segment gap, so it is consumed only when no segment
        /// covers effUT. This mirrors the create-path <see cref="ResolveTrackingStationGhostSource"/>
        /// and the flight-scene <c>ParsekPlaybackPolicy.CheckPendingMapVessels</c> precedence;
        /// without it a Segment-created looped ghost freezes on its last segment the moment effUT
        /// advances into a transfer/coast OrbitalCheckpoint section.
        /// </summary>
        internal static bool ShouldConsumeCheckpointStateVectorForExistingGhost(
            bool fromCheckpoint, bool segmentCoversEffUT)
        {
            return fromCheckpoint && !segmentCoversEffUT;
        }

        /// <summary>
        /// True when at least one OrbitSegment begins strictly after <paramref name="ut"/>, i.e. the
        /// recording still has orbital playback AHEAD of the current effective UT. Used by the
        /// tracking-station update path to distinguish a mid-recording gap (between the parking
        /// orbit and a later destination orbit, e.g. during a transfer burn / coast) from the
        /// genuine terminal region. The endpoint-tail (terminal-orbit) fallback must fire only at
        /// the terminal region; firing it in a mid-recording gap reseeds the looped proto-vessel
        /// onto the FINAL orbit for the whole rest of the replay and suppresses the non-proto
        /// atmospheric position marker. Mirrors the flight-scene gap check in
        /// <c>ParsekPlaybackPolicy.CheckPendingMapVessels</c>.
        /// </summary>
        internal static bool HasOrbitSegmentStartingAfter(List<OrbitSegment> segments, double ut)
        {
            if (segments == null)
                return false;
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].startUT > ut)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// True when the synthetic endpoint-tail (terminal-orbit) fallback may be applied in the
        /// tracking-station per-tick update for this ghost. The endpoint tail is a NON-LOOP concept
        /// (it shows where a finished flight ended up, seeded from the recorded historical body
        /// rotation/position); for a loop member it does not survive the loop epoch shift for a
        /// cross-body terminal (a Mun orbit seeded from the Mun's RECORDED position lands tens of
        /// millions of metres off the live Mun). A loop member's effUT is always inside the
        /// recording, so its covering OrbitSegment is the correct current orbit and must win; when
        /// none covers effUT the loop removes the proto-vessel instead, matching the flight scene's
        /// segment-update path. Loop membership is read from the per-tick epoch shift
        /// (<c>liveUT - effUT</c>): zero for non-loop members, non-zero for loop replay.
        ///
        /// INVARIANT (load-bearing): this "shift == 0 means non-loop" signal is sound only because
        /// the first-play floor in <c>MissionLoopUnitBuilder.TryBuildMissionUnit</c> clamps every
        /// looping mission's <c>phaseAnchorUT</c> to at least <c>spanEndUT</c> (&gt; spanStartUT), so a
        /// loop member's effUT can never equal liveUT (it always maps to a past recorded UT, giving a
        /// non-zero shift). If that floor is ever weakened/removed, a cycle-0 member with
        /// <c>phaseAnchorUT == spanStartUT</c> would produce shift 0 and silently re-enable the
        /// endpoint tail here. Keep the two in sync.
        /// </summary>
        internal static bool EndpointTailAllowedInTrackingStationUpdate(double loopEpochShiftSeconds)
        {
            return loopEpochShiftSeconds == 0.0;
        }

        internal struct TrackingStationSpawnHandoffState
        {
            internal readonly uint GhostPid;
            internal readonly bool WasNavigationTarget;
            internal readonly bool WasMapFocus;

            internal TrackingStationSpawnHandoffState(
                uint ghostPid,
                bool wasNavigationTarget,
                bool wasMapFocus)
            {
                GhostPid = ghostPid;
                WasNavigationTarget = wasNavigationTarget;
                WasMapFocus = wasMapFocus;
            }
        }

        internal enum GhostTargetVerificationStatus
        {
            Accepted,
            VerificationUnavailable,
            MissingFlightGlobals,
            NullTarget,
            CurrentMainBody,
            ParentBody,
            WrongVessel,
            WrongObject
        }

        private const string Tag = "GhostMap";
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;
        private static long ghostTargetRequestSequence;

        // -----------------------------------------------------------------
        // Observability (#582 follow-up): every create/position/update/destroy
        // decision in this file emits a single structured line via
        // BuildGhostMapDecisionLine so a future KSP.log filtered on
        // "[Parsek][INFO][GhostMap]" / "[Parsek][VERBOSE][GhostMap]" reconstructs
        // the full per-recording lifecycle without cross-file lookups.
        //
        // Standard fields (always present): action, rec, idx, vessel, source,
        // branch, body, worldPos, scene. Optional fields appear only when the
        // source/branch implies them (segment*, terminal*, stateVec*, anchor*,
        // localOffset). Producers fill GhostMapDecisionFields and call
        // EmitGhostMapDecision(level, fields) — the builder formats and logs.
        // -----------------------------------------------------------------

        /// <summary>
        /// Last-known per-recording ghost frame, captured on every successful
        /// position/update so RemoveGhostVesselForRecording can include the
        /// trailing context in its destroy log line. Cleared on remove.
        /// </summary>
        internal struct LastKnownGhostFrame
        {
            public string RecordingId;
            public string VesselName;
            public uint GhostPid;
            public string Source;       // Segment / TerminalOrbit / StateVector / Chain
            public string Branch;       // Absolute / Relative / OrbitalCheckpoint / no-section / (n/a)
            public string Body;
            public Vector3d WorldPos;
            public uint AnchorPid;
            public double LastUT;
        }

        private static readonly Dictionary<int, LastKnownGhostFrame>
            lastKnownByRecordingIndex = new Dictionary<int, LastKnownGhostFrame>();

        private static readonly Dictionary<uint, LastKnownGhostFrame>
            lastKnownByChainPid = new Dictionary<uint, LastKnownGhostFrame>();

        // Per-tick lifecycle counters; flushed via VerboseRateLimited so map-mode
        // sessions get one summary line every ~5s rather than per-frame.
        internal static int lifecycleCreatedThisTick;
        internal static int lifecycleDestroyedThisTick;
        internal static int lifecycleUpdatedThisTick;

        // Orbit-raise gap-glide seed-frame counters: how many gap frames seeded the
        // icon in the reconstructed INERTIAL frame vs fell back to the body-fixed
        // (live-rotation) frame. Emitted once per ~5s window via a shared-key summary
        // so a KSP.log capture proves the inertial path actually fired (inertial>0)
        // rather than silently compiling out (the PR-#885 no-op trap). Cumulative
        // across the session; reset only for testing.
        internal static int gapGlideInertialSeedCount;
        internal static int gapGlideBodyFixedFallbackCount;

        internal static void ResetGapGlideCountersForTesting()
        {
            gapGlideInertialSeedCount = 0;
            gapGlideBodyFixedFallbackCount = 0;
        }

        // Seam CoMD-refresh counters (Bug A loop-icon-warp-lag): how many state-vector reseed
        // frames forced a synchronous VesselPrecalculate.CalculatePhysicsStats() to re-snap the
        // ghost's cached Vessel.CoMD (what GetWorldPos3D and the map icon read) onto the
        // just-reseeded conic the line draws, vs how many were steady-state (conic unchanged
        // since the last reseed, so CoMD already tracks it via the normal FixedUpdate path).
        // Emitted via a shared-key summary so a KSP.log capture proves the refresh actually fired
        // (refreshed>0) rather than silently no-op'ing (the PR-#885 trap). maxOffDeg is the
        // tracing-gated post-refresh icon-vs-conic angle (should collapse to ~0). Cumulative across
        // the session; reset only for testing.
        internal static int seamComdRefreshCount;
        internal static int seamComdSteadyCount;
        internal static double seamComdMaxOffOrbitDegPostRefresh;

        internal static void ResetSeamComdCountersForTesting()
        {
            seamComdRefreshCount = 0;
            seamComdSteadyCount = 0;
            seamComdMaxOffOrbitDegPostRefresh = 0.0;
        }


        /// <summary>
        /// Snapshot the resolved frame so RemoveGhostVesselForRecording can later
        /// emit the trailing context. Called from every create/update success path.
        /// </summary>
        private static void StashLastKnownFrame(int recordingIndex, LastKnownGhostFrame frame)
        {
            lastKnownByRecordingIndex[recordingIndex] = frame;
        }

        /// <summary>
        /// Try to read the last-known frame for a recording index. Returns true
        /// when a frame is available; the caller is responsible for converting
        /// any partial fields to the decision-line shape.
        /// </summary>
        private static bool TryGetLastKnownFrame(int recordingIndex, out LastKnownGhostFrame frame)
        {
            return lastKnownByRecordingIndex.TryGetValue(recordingIndex, out frame);
        }

        private static bool IsEndpointTailRecordingGhost(uint vesselPid, int recordingIndex)
        {
            return recordingIndex >= 0
                && TryGetLastKnownFrame(recordingIndex, out LastKnownGhostFrame frame)
                && frame.GhostPid == vesselPid
                && string.Equals(frame.Source, "EndpointTail", StringComparison.Ordinal);
        }

        internal const string TrackingStationGhostSkipSuppressed = "suppressed";
        internal const string TrackingStationGhostSkipAlreadySpawned = "already-spawned";
        internal const string TrackingStationGhostSkipLiveAnchorDouble = "live-anchor-double";
        internal const string TrackingStationGhostSkipEndpointConflict = "endpoint-conflict";
        internal const string TrackingStationGhostSkipUnseedableTerminalOrbit = "terminal-orbit-unseedable";
        internal const string TrackingStationGhostSkipStateVectorThreshold = "state-vector-threshold";
        internal const string TrackingStationGhostSkipRelativeFrame = "relative-frame";
        internal const string TrackingStationGhostSkipRelativeStateVectorSegmentGap =
            "relative-state-vector-segment-gap";
        internal const string TrackingStationGhostSkipBodyFixedPrimaryUnavailable = "body-fixed-primary-unavailable";
        // #583: Relative-frame state-vector ghost CREATION reaches the resolver
        // when the first map-visible UT lies inside a Relative section. Creation
        // flows through the existing StateVector source kind when the section's
        // recorded anchor chain can resolve for the target UT. If it cannot, defer
        // with this dedicated skip reason so the pending-create queue retries on
        // the next tick. Distinct from `relative-frame`, the legacy "always defer"
        // reason kept for Relative sections that cannot attempt recorded-anchor
        // state-vector resolution.
        internal const string TrackingStationGhostSkipRelativeAnchorUnresolved = "relative-anchor-unresolved";
        internal const string TrackingStationGhostSkipActiveReFlyRelativeLookahead =
            "active-refly-relative-anchor-lookahead";
        internal const string TrackingStationGhostSkipActiveReFlyRelativeUpdate =
            "active-refly-relative-anchor-update";
        internal const string TrackingStationSpawnSkipRewindPending = "rewind-ut-adjustment-pending";
        internal const string TrackingStationSpawnSkipBeforeEnd = "before-recording-end";
        internal const string TrackingStationSpawnSkipIntermediateChainSegment = "intermediate-chain-segment";
        internal const string TrackingStationSpawnSkipIntermediateGhostChainLink = "intermediate-ghost-chain-link";
        internal const string TrackingStationSpawnSkipTerminatedGhostChain = "terminated-ghost-chain";
        internal const string TrackingStationSpawnSkipSupersededByRelation = "superseded-by-relation";
        internal const string TrackingStationSpawnSkipRewindRetired = "rewind-retired";
        internal const string SoiGapStateVectorFallbackReason = "soi-gap-state-vector-fallback";
        internal const string OrbitalCheckpointStateVectorRejectSaferSegment = "orbital-checkpoint-state-vector-safer-segment-source";
        internal const string OrbitalCheckpointStateVectorRejectNotSoiGap = "orbital-checkpoint-state-vector-not-soi-gap-recovery";
        internal const string OrbitalCheckpointStateVectorRejectBodyMismatch = "orbital-checkpoint-state-vector-body-mismatch";
        internal const string OrbitalCheckpointStateVectorRejectOutsideWindow = "orbital-checkpoint-state-vector-outside-window";
        internal const double StateVectorCreateAltitude = 1500;   // meters (airless bodies only)
        internal const double StateVectorCreateSpeed = 60;        // m/s
        internal const double StateVectorRemoveAltitude = 500;    // meters (airless bodies only)
        internal const double StateVectorRemoveSpeed = 30;        // m/s
        private const double LegacyPointCoverageMaxGapSeconds = 30.0;
        internal static Func<double> CurrentUTNow = GetCurrentUTSafe;
        internal static Func<string, CelestialBody> FindBodyByNameForTesting;

        internal struct GhostProtoOrbitSeedDiagnostics
        {
            public string Source;
            public string EndpointBodyName;
            public string FailureReason;
            public string FallbackReason;
            public bool TailSeedConsidered;
            public bool TailSeedAccepted;
            public string TailDeclineReason;
            public double TailUT;
            public double TailSma;
            public double TailEcc;
            public double LatestSegmentEndUT;
            public double RotationDriftSeconds;
            public string TailFrameSource;
        }

        internal struct OrbitalCheckpointStateVectorFallbackDecision
        {
            public bool Accepted;
            public string Reason;
            public string ExpectedBody;
            public string StateVectorBody;
            public bool SegmentSourceAvailable;
            public bool IsSoiGapRecovery;
            public bool GapBodyTransition;
            public bool BodyMatches;
            public bool WithinPlaybackWindow;
            public string GapPreviousBody;
            public string GapNextBody;
            public double ActivationStartUT;
            public double EndUT;
        }

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

        /// <summary>
        /// PID tracking set — the canonical ghost vessel identification.
        /// Every guard in the codebase checks this for O(1) exclusion.
        /// </summary>
        internal static readonly HashSet<uint> ghostMapVesselPids = new HashSet<uint>();

        /// <summary>
        /// Ghost ProtoVessels whose native icon is currently suppressed by
        /// GhostOrbitLinePatch (below atmosphere). DrawMapMarkers checks this
        /// to draw our custom icon at the ghost mesh position instead.
        /// </summary>
        internal static readonly HashSet<uint> ghostsWithSuppressedIcon = new HashSet<uint>();

        /// <summary>
        /// Per-ghost orbit-line "grace deadline" (a render-frame count). When
        /// <see cref="Parsek.Patches.GhostOrbitLinePatch"/> genuinely shows the
        /// orbit line (`visible-body-frame`), it stamps a short grace window
        /// here. While the current render frame is still inside that window, a TRANSIENT off
        /// reason (`stale-segment-awaiting-reseed` or `polyline-owns-phase`) is
        /// deferred for a few frames so the line does not blink off at a short
        /// phase-boundary segment while the per-frame reseed catches up.
        /// Durable off reasons (below-atmosphere / out-of-body-frame) are never
        /// graced. The deadline is a RENDER FRAME count (Time.frameCount), NOT a
        /// UT window: the blink is a per-render-frame chatter, and under time warp
        /// UT advances by hundreds-to-thousands of seconds per frame, so a UT
        /// window (the old 1.5 s) collapses below a single frame's UT step and
        /// defers nothing. A frame count is warp-independent: it defers a few-frame
        /// transient dip at any warp, while a SUSTAINED phase (more than the grace
        /// frames of consecutive off, e.g. the polyline owning a whole below-surface
        /// descent) still expires and hides. Cleared alongside
        /// <see cref="ghostsWithSuppressedIcon"/> on every ghost teardown / scene change.
        /// </summary>
        internal static readonly Dictionary<uint, int> ghostOrbitLineGraceUntilFrame =
            new Dictionary<uint, int>();

        /// <summary>
        /// Stamps the orbit-line grace deadline for <paramref name="pid"/> at render
        /// frame <paramref name="graceUntilFrame"/>. Called by
        /// <see cref="Parsek.Patches.GhostOrbitLinePatch"/> on every frame the
        /// line is genuinely shown so a subsequent transient off-dip can be
        /// deferred until the deadline frame.
        /// </summary>
        internal static void StampOrbitLineGrace(uint pid, int graceUntilFrame)
        {
            ghostOrbitLineGraceUntilFrame[pid] = graceUntilFrame;
        }

        /// <summary>
        /// Returns the orbit-line grace deadline (render frame) for <paramref name="pid"/>,
        /// or <see cref="int.MinValue"/> when none is stamped (so any
        /// `currentFrame &lt;= graceUntil` test is false and grace is inactive).
        /// </summary>
        internal static int GetOrbitLineGraceUntilFrame(uint pid)
        {
            return ghostOrbitLineGraceUntilFrame.TryGetValue(pid, out int until)
                ? until
                : int.MinValue;
        }

        /// <summary>
        /// Per-pid real-time stamp of the last frame the trajectory polyline was actively rendering
        /// this ghost's non-orbital leg. Used by both GhostOrbitLinePatch (to defer the stock orbit
        /// icon from showing at a STALE OrbitDriver mesh position right after polyline release - the
        /// "icon teleported to the wrong position on the loiter" symptom: the seg-drive dispatcher
        /// runs on a ~0.5s cadence so the new orbital segment is not applied on the same frame as
        /// polyline release, and the OrbitDriver.pos is still at the pre-polyline segment's
        /// endpoint for ~12 frames at 60Hz) and by the ParsekUI labeled-marker draw (to keep using
        /// trajPos through the same brief window). Cleared in <see cref="ResetForTesting"/>.
        /// </summary>
        private static readonly Dictionary<uint, float> lastPolylineOwningRealTimePerPid =
            new Dictionary<uint, float>();

        /// <summary>
        /// Stamps "polyline is currently rendering this ghost's non-orbital leg" at the current
        /// real-time clock. Called by <see cref="Parsek.Patches.GhostOrbitLinePatch"/> on every
        /// frame the polyline-owns-phase branch fires.
        /// </summary>
        internal static void StampPolylineOwning(uint pid)
        {
            lastPolylineOwningRealTimePerPid[pid] = UnityEngine.Time.realtimeSinceStartup;
        }

        /// <summary>
        /// True when the trajectory polyline is currently rendering this ghost's non-orbital leg
        /// OR was rendering it within <paramref name="graceSeconds"/> ago. The grace covers the
        /// post-release window where the seg-drive dispatcher hasn't yet applied the next orbital
        /// segment and the OrbitDriver mesh transform is still stale. Pure read; no side effects.
        /// </summary>
        internal static bool IsPolylineRecentlyOwningGhostPhase(uint pid, float graceSeconds)
        {
            if (IsPolylineOwningGhostPhase(pid))
                return true;
            return lastPolylineOwningRealTimePerPid.TryGetValue(pid, out float last)
                && UnityEngine.Time.realtimeSinceStartup - last < graceSeconds;
        }

        /// <summary>Test-only: clear the polyline-owning real-time stamps.</summary>
        internal static void ClearPolylineOwningStampsForTesting()
        {
            lastPolylineOwningRealTimePerPid.Clear();
        }

        /// <summary>
        /// LOITER-GAP LINE HOLD (Layer B of the re-aim descent parking-conic render fix): per-pid LIVE-frame UT
        /// the parking-conic orbit line must stay visible until (= the live descent trigger UT). Stamped by the
        /// flight + TS reseed sites whenever the descent-trigger TRANSFER member is in the loiter gap
        /// (GhostPlaybackLogic.IsDescentTransferMemberInLoiterGap true), and REMOVED the instant the predicate
        /// goes false (descent fired OR the loop wrapped). GhostOrbitLinePatch consults it in the
        /// past-body-frame-end branch (via <see cref="TryGetParkingConicLineHold"/>) to HOLD the full parking
        /// ellipse drawn through the seg-6 window and the post-seg-6 gap instead of retiring the line because the
        /// LIVE currentUT is past the parking conic's live-UT upper bound. Cleared on every scene reset / teardown
        /// alongside <see cref="vesselPidToRecordingId"/>. Empty for every non-held ghost, so the past-end retire
        /// is byte-identical for all other ghosts.
        /// </summary>
        private static readonly Dictionary<uint, double> ghostParkingConicLineHoldUntilUT =
            new Dictionary<uint, double>();

        /// <summary>
        /// Stamps the parking-conic line-hold deadline for <paramref name="pid"/> at LIVE UT
        /// <paramref name="holdUntilUT"/> (the live descent trigger UT). Called by the flight + TS reseed sites
        /// while the descent-trigger transfer member is in the loiter gap.
        /// </summary>
        internal static void StampParkingConicLineHold(uint pid, double holdUntilUT)
        {
            ghostParkingConicLineHoldUntilUT[pid] = holdUntilUT;
        }

        /// <summary>
        /// Clears any parking-conic line-hold stamp for <paramref name="pid"/>. Called by the reseed sites the
        /// instant the loiter-gap predicate goes false for the held pid, so a stale hold never keeps the parking
        /// conic drawn into the next phase.
        /// </summary>
        internal static void ClearParkingConicLineHold(uint pid)
        {
            ghostParkingConicLineHoldUntilUT.Remove(pid);
        }

        /// <summary>
        /// True iff a parking-conic line-hold stamp exists for <paramref name="pid"/> AND
        /// <paramref name="currentUT"/> is at or before the stamped hold deadline (<paramref name="holdUntilUT"/>
        /// = the live descent trigger UT). False once the trigger UT is reached, so the line retires cleanly the
        /// same frame the descent set takes over. Pure read; no side effects.
        /// </summary>
        internal static bool TryGetParkingConicLineHold(uint pid, double currentUT, out double holdUntilUT)
        {
            if (ghostParkingConicLineHoldUntilUT.TryGetValue(pid, out holdUntilUT)
                && !double.IsNaN(holdUntilUT)
                && currentUT <= holdUntilUT)
                return true;
            holdUntilUT = double.NaN;
            return false;
        }

        /// <summary>Test-only: clear the parking-conic line-hold stamps.</summary>
        internal static void ClearParkingConicLineHoldsForTesting()
        {
            ghostParkingConicLineHoldUntilUT.Clear();
        }

        /// <summary>
        /// Shared flight + TS reseed-site helper for the parking-conic LINE HOLD (Layer B). Resolves the LIVE
        /// descent-trigger UT for the in-loiter-gap transfer member <paramref name="idx"/> (via
        /// <see cref="GhostPlaybackLogic.TryResolveLoiterGapHoldTriggerUT"/>, using the SAME cycle the reseed loop
        /// is on this frame) and STAMPS the per-pid line hold so GhostOrbitLinePatch keeps the full parking
        /// ellipse drawn through the loiter until that trigger. Call ONLY when the loiter-gap predicate is true
        /// for <paramref name="idx"/>. Keeps the flight + TS sites byte-identical to each other. Logs via the
        /// caller-supplied rate-limit key/tag so the flight vs TS lines stay distinguishable.
        /// </summary>
        private static void StampParkingConicLineHoldForLoiterGap(
            int idx, uint ghostPid, double currentUT,
            GhostPlaybackLogic.LoopUnitSet loopUnits,
            double recStartUT, double recEndUT,
            string sceneTag, string logKeyPrefix)
        {
            if (ghostPid == 0)
                return;
            if (GhostPlaybackLogic.TryResolveLoiterGapHoldTriggerUT(
                    loopUnits, idx, currentUT, recStartUT, recEndUT, out double triggerUT))
            {
                StampParkingConicLineHold(ghostPid, triggerUT);
                ParsekLog.VerboseRateLimited(sceneTag,
                    logKeyPrefix + idx,
                    string.Format(ic,
                        "{0} parking-conic line hold: member={1} pid={2} currentUT={3:F1} holdUntilUT(triggerUT)={4:F1} "
                        + "(parking ellipse held visible through loiter until live descent trigger)",
                        sceneTag, idx, ghostPid, currentUT, triggerUT),
                    5.0);
            }
            else
            {
                // Defensive: in the gap but the trigger could not resolve (span clock unresolved this frame).
                // Drop any prior hold so a stale deadline never lingers; the past-end retire then runs normally.
                ClearParkingConicLineHold(ghostPid);
            }
        }

        /// <summary>
        /// Map from chain PID (OriginalVesselPid) to the ghost Vessel object.
        /// Used for orbit updates, cleanup, and target transfer.
        /// </summary>
        private static readonly Dictionary<uint, Vessel> vesselsByChainPid = new Dictionary<uint, Vessel>();

        /// <summary>
        /// Map from recording index (engine ghost key) to the ghost Vessel object.
        /// Used for timeline playback ghosts that are not part of a ghost chain.
        /// </summary>
        private static readonly Dictionary<int, Vessel> vesselsByRecordingIndex = new Dictionary<int, Vessel>();

        /// <summary>
        /// Reverse lookup: ghost vessel PID → recording index.
        /// Kept in sync with vesselsByRecordingIndex to make FindRecordingIndexByVesselPid O(1).
        /// </summary>
        private static readonly Dictionary<uint, int> vesselPidToRecordingIndex = new Dictionary<uint, int>();

        /// <summary>
        /// Per-(recording index, loop cycle) ghost map ProtoVessels for OVERLAP-looped recordings
        /// (slice i of the per-instance overlap render, design
        /// docs/dev/plans/maprender-overlap-per-instance.md). When a looped recording's period is
        /// shorter than its duration, several staggered replays run at once (overlap); flight shows
        /// N meshes but the map historically showed ONE icon (the newest cycle). This store holds
        /// ONE ProtoVessel per LIVE overlap cycle so the map shows N icons matching the N flight
        /// meshes. Each instance is a fresh ProtoVessel with a KSP-minted unique pid (CreateVesselNode
        /// vesselID:0), so the N siblings never collide. Non-overlap recordings keep using the
        /// one-per-recording <see cref="vesselsByRecordingIndex"/> store untouched: this store is the
        /// SOLE create/destroy/reseed authority for overlap recordings (single-ownership rule), and
        /// the legacy passes branch overlap indices to <see cref="EnsureOverlapInstances"/> + continue.
        /// Mirrors the KSC <c>kscOverlapGhosts</c> + <c>kscGhosts</c> model in <see cref="ParsekKSC"/>
        /// (the proven per-instance overlap map ghost host, which uses no flight engine and resolves
        /// the schedule through the pure <see cref="GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule"/>
        /// so flight + Tracking Station resolve identically).
        /// </summary>
        private static readonly Dictionary<(int recIdx, long cycle), Vessel> overlapInstanceVessels =
            new Dictionary<(int, long), Vessel>();

        // BOUNDARY-OVERLAP secondary instance pids (launch->escape seam render, review M1). The
        // launch->escape boundary-overlap secondary (the early-launching N+1 instance) is stored in
        // overlapInstanceVessels like any per-instance map ghost, but it must NOT shadow the launch
        // recording's identity for the vesselsByRecordingIndex-reader fall-through (watch-camera focus,
        // TS-Fly, UI marker suppression): a launch-hold member is NON-overlap, so its ONLY
        // overlapInstanceVessels entries ARE boundary-secondaries, and when its primary is renderHidden (the
        // chain case, no vesselsByRecordingIndex entry) GetGhostVesselPidForRecording would otherwise fall
        // through to the secondary's pid. Membership here lets that fall-through skip the secondary and report
        // pid 0 (no ghost) instead. The polyline's DIRECT GetNewestOverlapInstancePidForRecording call (which
        // WANTS the secondary pid for the second-head leg) passes excludeBoundarySecondary:false, so it is
        // unaffected.
        private static readonly HashSet<uint> boundaryOverlapSecondaryPids = new HashSet<uint>();

        private static readonly Dictionary<int, IPlaybackTrajectory> trackingStationStateVectorOrbitTrajectories =
            new Dictionary<int, IPlaybackTrajectory>();

        private static readonly Dictionary<int, int> trackingStationStateVectorCachedIndices =
            new Dictionary<int, int>();

        private static readonly Dictionary<int, string> activeReFlyDeferredStateVectorGhostSessions =
            new Dictionary<int, string>();

        /// <summary>
        /// Stable reverse lookup: ghost vessel PID -> recording ID.
        /// Selection actions use this instead of the raw index because the committed
        /// list can be reordered while a Tracking Station ghost remains selected.
        /// </summary>
        private static readonly Dictionary<uint, string> vesselPidToRecordingId = new Dictionary<uint, string>();

        // ---- Phase 8e S0: coverage-closure accounting (PURELY ADDITIVE diagnostics) ----
        // These two per-frame RecordingId-keyed sets let the MapRenderProbe prove, before any
        // legacy deletion, that the Director's accounted set is a SUPERSET of what the autonomous
        // polyline walk actually draws. Both are populated by the polyline Driver's per-frame decide
        // walk (the ONLY producer) and cleared each frame at the top of that walk; the probe reads
        // them at end-of-frame (exec-order 10000, same frame, after the -50 decide pass). They are
        // purely instrumentation: nothing in the live render/draw path reads them. Gated by the
        // Driver on MapRenderTrace.IsEnabled so default play never populates them.

        /// <summary>
        /// S0 Instrument 1 (DRAWN set, RecordingId domain): every committed recording the autonomous
        /// polyline walk decided to draw a non-orbital leg for THIS frame (the will-draw == actual-draw
        /// set the <c>PendingLegDraw</c> queue is built from). Populated by
        /// <see cref="NoteDrawnRecordingCoverage"/> from the Driver's decide walk; cleared by
        /// <see cref="ClearFrameCoverageSets"/> at the top of every Driver LateUpdate. The probe iterates
        /// this set to assert each drawn recording is ACCOUNTED. Diagnostic-only.
        /// </summary>
        private static readonly HashSet<string> drawnRecordingIdsThisFrame =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// S0 Instrument 1 (proto-less COVERAGE set, RecordingId domain): the subset of
        /// <see cref="drawnRecordingIdsThisFrame"/> whose committed recording has NO ProtoVessel ghost
        /// (<see cref="GetGhostVesselPidForRecording"/> == 0) - i.e. the pid-0 atmospheric/ascent
        /// recordings the Director's enumerated <see cref="ghostMapVesselPids"/> set cannot see, drawn
        /// ONLY by the autonomous <c>CommittedRecordings</c> walk. This is the Director's GENUINE
        /// accounting of those recordings via the non-proto path (it acknowledges "this proto-less
        /// recording is being rendered"). NOT "all committed recordings": a proto-BEARING drawn recording
        /// is deliberately excluded here (it is accounted via the pid bridge instead), so the assertion
        /// stays non-vacuous. Populated + cleared on the same lifecycle as the drawn set. Diagnostic-only.
        /// </summary>
        private static readonly HashSet<string> protoLessCoverageRecordingIdsThisFrame =
            new HashSet<string>(StringComparer.Ordinal);

        // Scratch holding the proto-bearing RecordingId set built once per assertion pass from the live
        // vesselPidToRecordingId values, so the per-drawn-recording check is O(1). Reused (cleared, not
        // re-allocated) to keep the gated path allocation-light.
        private static readonly HashSet<string> protoBearingRecordingIdScratch =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Orbit segment time bounds per ghost vessel PID. Used by GhostOrbitArcPatch
        /// to clip the orbit line to only the visible arc (between segment startUT and endUT).
        /// Only populated for segment-based ghosts — terminal-orbit ghosts render the full ellipse.
        /// </summary>
        internal static readonly Dictionary<uint, (double startUT, double endUT)> ghostOrbitBounds
            = new Dictionary<uint, (double, double)>();

        /// <summary>
        /// Body-frame orbit time bounds per ghost vessel PID. These cover the entire run of
        /// consecutive same-body OrbitSegments around the playback head (already shifted into
        /// the live frame for loop-shifted ghosts). Used by <see cref="Parsek.Patches.GhostOrbitLinePatch"/>
        /// to keep <c>line.active</c> True across inter-segment burns / sparse-physics gaps inside
        /// one body frame, blinking off only at an SOI / body change. Computed at the same time
        /// as <see cref="ghostOrbitBounds"/> in <see cref="UpdateGhostOrbitForRecording"/>, so the
        /// shift is consistent and the loop-shifted edge cases handled identically to per-segment
        /// arc clipping.
        /// </summary>
        internal static readonly Dictionary<uint, (double startUT, double endUT)> ghostBodyFrameOrbitBounds
            = new Dictionary<uint, (double, double)>();

        /// <summary>
        /// Ghost PIDs whose orbit + <see cref="ghostOrbitBounds"/> are currently shifted into the
        /// live frame by a Mission-loop epoch shift (loopEpochShiftSeconds != 0). For these,
        /// <see cref="TryGetVisibleOrbitBoundsForGhostVessel"/> MUST return the stored (already
        /// shifted) bounds instead of re-deriving them from the raw recorded OrbitSegments at the
        /// live UT: the re-derivation would hand back raw recorded UTs that no longer match the
        /// shifted orbit epoch, mis-clipping the arc / icon in the (rewind/warp) edge where the
        /// live clock falls inside the member's recorded window. Empty off the loop path, so
        /// non-loop ghosts keep the existing segment re-derivation.
        /// </summary>
        internal static readonly HashSet<uint> ghostOrbitLoopShiftedPids = new HashSet<uint>();

        /// <summary>
        /// Per-ghost loop-epoch shift (seconds), i.e. <c>liveUT - effUT</c>, written every
        /// reseed by <see cref="ApplyOrbitToVessel"/>. The ghost OrbitDriver is now seeded with
        /// the RAW recorded epoch (no shift baked into the elements), and the icon-drive +
        /// arc-clip patches map the live Planetarium clock back to the recorded sample clock with
        /// <c>effUT = liveUT - shift</c> every frame, so the icon glides continuously at the
        /// loop-mapped <c>effUT</c> rate instead of being re-pinned at each rate-limited reseed
        /// (the frozen-icon-on-short-arc bug). 0 (absent) off the loop path, so non-loop ghosts
        /// keep <c>effUT == liveUT</c> and behave exactly as before.
        ///
        /// This is the AUTHORITY for the per-frame icon clock: the value is constant within a loop
        /// cycle (effUT tracks liveUT 1:1) and is re-snapped at the next reseed, so propagation
        /// between reseeds matches the replay. Cleared on every ghost teardown / scene change
        /// alongside <see cref="ghostOrbitBounds"/>.
        /// </summary>
        internal static readonly Dictionary<uint, double> ghostOrbitEpochShift =
            new Dictionary<uint, double>();

        /// <summary>
        /// Returns the loop-epoch shift (<c>liveUT - effUT</c>) currently applied to
        /// <paramref name="vesselPid"/>, or <c>0</c> when none is recorded (non-loop ghost).
        /// </summary>
        internal static double GetGhostOrbitEpochShift(uint vesselPid)
        {
            return ghostOrbitEpochShift.TryGetValue(vesselPid, out double shift) ? shift : 0.0;
        }

        /// <summary>The exact UT a ghost's OrbitDriver was propagated at by the icon-drive Prefix,
        /// plus the frame it was recorded on. See <see cref="ghostIconDrivePropagation"/>.</summary>
        internal struct IconDrivePropagation
        {
            public double PropagateUT;
            public int Frame;
        }

        /// <summary>
        /// The UT each ghost's OrbitDriver was last actually propagated at by
        /// <see cref="Parsek.Patches.GhostOrbitIconDrivePatch"/> when it set the icon position
        /// (the <c>propagateUT</c>: the legacy recorded-clock <c>effUT = liveUT - shift</c>, an
        /// off-arc / window clamp, or the live-clock <c>liveDriveUT</c> under the director epoch-bake),
        /// frame-stamped. <see cref="MapRenderProbe"/> reads THIS as the icon's resolved phase clock
        /// for the <c>icon-off-orbit</c> check, instead of independently re-deriving it via
        /// <see cref="Parsek.MapRender.ShadowRenderDriver.IsDirectorDriveActive"/>. The drive
        /// (OrbitDriver LateUpdate, exec-order 0) and the probe (exec-order 10000) used to evaluate
        /// that predicate separately; the shadow's StockConic seed can flip to "fresh" BETWEEN them
        /// within one frame, so the drive placed the icon at the legacy shifted phase while the probe
        /// assumed the director unshifted phase, producing a spurious icon-off-orbit angle (the
        /// transient creation-frame / reseed residual). Reading the recorded propagateUT makes the
        /// reference conic match where the icon was ACTUALLY placed, by construction, while still
        /// flagging a REAL off-orbit (icon NOT at its driven phase). Frame-stamped + freshness-gated
        /// on read so a stale record (a frame on which the icon-drive did not run, e.g. stock re-took
        /// the drive at a stale-segment transition) falls back to the legacy derivation rather than
        /// comparing against a phase the icon has since left. Cleared on ghost teardown / scene change
        /// alongside <see cref="ghostOrbitEpochShift"/>.
        /// </summary>
        internal static readonly Dictionary<uint, IconDrivePropagation> ghostIconDrivePropagation =
            new Dictionary<uint, IconDrivePropagation>();

        /// <summary>Records the UT the icon-drive Prefix propagated <paramref name="vesselPid"/>'s
        /// OrbitDriver at this frame (see <see cref="ghostIconDrivePropagation"/>).</summary>
        internal static void RecordIconDrivePropagateUT(uint vesselPid, double propagateUT, int frame)
        {
            ghostIconDrivePropagation[vesselPid] =
                new IconDrivePropagation { PropagateUT = propagateUT, Frame = frame };
        }

        /// <summary>
        /// Returns the UT the icon-drive last propagated <paramref name="vesselPid"/> at, but only
        /// when that record is within <paramref name="freshnessFrames"/> of
        /// <paramref name="currentFrame"/>. A stale or absent record returns <c>false</c> so the
        /// caller falls back to its own derivation.
        /// </summary>
        internal static bool TryGetFreshIconDrivePropagateUT(
            uint vesselPid, int currentFrame, int freshnessFrames, out double propagateUT)
        {
            if (ghostIconDrivePropagation.TryGetValue(vesselPid, out IconDrivePropagation rec)
                && System.Math.Abs(currentFrame - rec.Frame) <= freshnessFrames)
            {
                propagateUT = rec.PropagateUT;
                return true;
            }
            propagateUT = 0.0;
            return false;
        }

        /// <summary>
        /// Pure: map the live Planetarium clock to the loop-mapped recorded-sample clock for a
        /// ghost map vessel. <c>effUT = liveUT - shift</c>. With the RAW-epoch seed this is the
        /// UT the ghost OrbitDriver must be propagated at so the icon lands on the replayed phase,
        /// and the UT the arc-clip patch must use for its eccentric-anomaly bounds so the line
        /// shape stays in exact lockstep with the icon. Identity (returns <paramref name="liveUT"/>)
        /// when <paramref name="shift"/> is 0.
        /// </summary>
        internal static double MapLiveUTToEffUT(double liveUT, double shift)
        {
            return liveUT - shift;
        }

        /// <summary>
        /// The CelestialBody name Parsek last applied to each ghost's OrbitDriver, keyed by persistentId.
        /// The orbit-renderer rebuild (and the "SOI change" log) is gated on a change measured against
        /// THIS, the body WE last applied, rather than <c>vessel.orbitDriver.referenceBody</c> (a
        /// KSP-owned field). This is the correct invariant: the disruptive
        /// <c>orbitRenderer.enabled</c> off/on rebuild should fire once per genuine Parsek-driven body
        /// change, not whenever some other actor touches the driver's reference body. (KSP's own per-frame
        /// SOI transition used to flip the reference body of a ghost mid-transfer and trip this every
        /// frame; that is now prevented at the source by
        /// <see cref="Parsek.Patches.GhostOrbitDominantBodyPatch"/>, so this gate is defense-in-depth that
        /// also avoids a redundant full-renderer rebuild on each reseed.) After the rebuild, drawMode stays
        /// REDRAW_AND_RECALCULATE so the line keeps tracking the reseeded orbit without re-toggling.
        /// </summary>
        internal static readonly Dictionary<uint, string> ghostLastAppliedOrbitBody
            = new Dictionary<uint, string>();

        /// <summary>
        /// Last (sma, ecc, epoch) triple Parsek drove onto each ghost via a state-vector reseed.
        /// <see cref="GhostOrbitElementsChanged"/> compares against it so the seam CoMD re-snap in
        /// <see cref="UpdateGhostOrbitFromStateVectors"/> fires only on the frame the reseed actually
        /// changes the conic (the director &lt;-&gt; gap-glide seam), not on a steady-state re-apply of
        /// the same recorded point. Cleared per-pid alongside <see cref="ghostLastAppliedOrbitBody"/>
        /// (a stale triple surviving a ghost-pid reuse would falsely short-circuit the first post-reuse
        /// seam refresh).
        /// </summary>
        internal static readonly Dictionary<uint, (double sma, double ecc, double epoch)>
            ghostLastAppliedOrbitElements
            = new Dictionary<uint, (double sma, double ecc, double epoch)>();

        /// <summary>
        /// True if any live map-ghost's playback head (<paramref name="currentUT"/>, live frame) has moved
        /// outside the orbit-segment bounds currently applied to it. When that happens the applied orbit is
        /// stale and <see cref="Parsek.Patches.GhostOrbitLinePatch"/>'s stale-segment guard blanks the line
        /// until the next reseed; under time warp the head sprints through short segments faster than the
        /// real-time-rate-limited reseed, so the line blinks off every frame (the warped Duna-approach
        /// blink). The flight policy (<c>CheckPendingMapVessels</c>) uses this to drive a WARP-AWARE reseed:
        /// re-apply the moment the head leaves its segment instead of waiting for the timer. This fixes the
        /// lag WITHOUT weakening the stale-segment guard (which still suppresses the genuine pre-burn arc on
        /// a propulsive->orbital handoff). Cheap allocation-free dict scan over the live bounds.
        /// </summary>
        internal static bool AnyGhostHeadLeftAppliedSegment(double currentUT)
        {
            foreach (var kvp in ghostOrbitBounds)
            {
                var b = kvp.Value;
                if (currentUT > b.endUT || currentUT < b.startUT)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// O(1) check used by all guard code throughout the codebase.
        /// Returns true if the given persistentId belongs to a ghost map ProtoVessel.
        /// </summary>
        internal static bool IsGhostMapVessel(uint persistentId)
        {
            return ghostMapVesselPids.Contains(persistentId);
        }

        /// <summary>
        /// O(1) check: is this ghost's native icon currently suppressed (below atmosphere)?
        /// When true, DrawMapMarkers draws our custom icon at the ghost mesh position instead.
        /// </summary>
        internal static bool IsIconSuppressed(uint persistentId)
        {
            return ghostsWithSuppressedIcon.Contains(persistentId);
        }

        /// <summary>
        /// Bug 3 burn-seam observability: the LAST render frame each ghost pid had its proto icon
        /// SUPPRESSED by the icon-drive Prefix in <see cref="Parsek.Patches.GhostOrbitIconDrivePatch"/>.
        /// This covers BOTH suppress paths: the Director-traced early-return (the path the headline
        /// burn-seam teleport actually traverses, confirmed in the captured KSP.log) and the no-bounds
        /// branch. The headline "followed icon teleported for one frame at a burn" symptom is the
        /// segment -> state-vector (burn) seam: at a loiter/transfer -> burn transition the state-vector
        /// reseed clears this ghost's segment-drive bounds (<see cref="ghostOrbitBounds"/> etc.) and the
        /// polyline owns the leg, so for the frame(s) between "suppression opens" and "the next
        /// StockConic (hyperbolic) segment re-establishes a drive" the proto icon is suppressed and its
        /// <c>worldPos</c> sits STALE; when the drive re-establishes, the icon un-suppresses and snaps
        /// by the warp-advanced amount. This dict lets the Prefix detect the ENTER (first suppressed
        /// frame) and EXIT (first driven frame after a suppressed run) transitions so the next playtest
        /// log captures the exact stale-window length + the snap magnitude from one grep, BEFORE any
        /// behavioral continuity fix is chosen (the fix is gated on this read; see
        /// docs/dev/todo-and-known-bugs.md). Pure observability: it never feeds a decision.
        /// Cleared per-pid on every ghost teardown (the three <c>RemoveGhostVessel*</c> sites, alongside
        /// <see cref="ghostsWithSuppressedIcon"/>) and in bulk on scene change / test reset; a stale
        /// stamp that survives an earlier Prefix early-return self-corrects to a clean ENTER on reuse
        /// (the strict <c>currentFrame-1</c> adjacency in
        /// <see cref="ClassifyNoBoundsSuppressionTransition"/> never reads it as a continuing run).
        /// </summary>
        internal static readonly Dictionary<uint, int> ghostNoBoundsSuppressLastFrame =
            new Dictionary<uint, int>();

        /// <summary>
        /// The no-bounds suppression transition for a ghost pid this frame, for Bug-3 burn-seam
        /// instrumentation. See <see cref="ClassifyNoBoundsSuppressionTransition"/>.
        /// </summary>
        internal enum NoBoundsSuppressTransition
        {
            /// <summary>The Prefix found bounds this frame and the pid had no recent no-bounds
            /// suppression run, so there is no burn-seam transition to log.</summary>
            None,
            /// <summary>First no-bounds-suppressed frame of a run (the icon was being driven last
            /// frame and is now suppressed because the segment bounds were just cleared). Log the
            /// stale worldPos carried into the suppressed window.</summary>
            Enter,
            /// <summary>A continuing no-bounds-suppressed frame within an open run. Not logged
            /// individually (the per-frame snapshot of the stale position is rate-limited).</summary>
            Sustain,
            /// <summary>First driven frame AFTER a no-bounds-suppressed run: the icon un-suppresses
            /// and snaps. Log the resolved worldPos so the snap magnitude is greppable.</summary>
            Exit
        }

        /// <summary>
        /// PURE: classify this frame's no-bounds suppression transition for a ghost pid, given whether
        /// the Prefix took the no-bounds suppress path this frame and the frame the pid was last
        /// no-bounds-suppressed on. <paramref name="suppressedThisFrame"/> is the no-bounds suppress
        /// decision (Director tracking AND no segment bounds). <paramref name="lastSuppressedFrame"/>
        /// is <see cref="int.MinValue"/> when the pid has never been no-bounds-suppressed.
        ///
        /// Enter   = suppressed this frame, NOT suppressed on the immediately-preceding frame.
        /// Sustain = suppressed this frame AND suppressed on the immediately-preceding frame.
        /// Exit    = NOT suppressed this frame, but suppressed on the immediately-preceding frame
        ///           (the un-suppress snap boundary).
        /// None    = NOT suppressed this frame and no immediately-preceding suppressed frame.
        ///
        /// "Immediately-preceding" is a strict <c>currentFrame - 1</c> match: a non-suppressed frame
        /// gap between two suppressed frames is its own Exit then Enter, which is exactly the burn-seam
        /// chatter we want to see frame-accurately in the log.
        /// </summary>
        internal static NoBoundsSuppressTransition ClassifyNoBoundsSuppressionTransition(
            bool suppressedThisFrame,
            int currentFrame,
            int lastSuppressedFrame)
        {
            bool suppressedLastFrame = lastSuppressedFrame == currentFrame - 1;
            if (suppressedThisFrame)
                return suppressedLastFrame
                    ? NoBoundsSuppressTransition.Sustain
                    : NoBoundsSuppressTransition.Enter;
            return suppressedLastFrame
                ? NoBoundsSuppressTransition.Exit
                : NoBoundsSuppressTransition.None;
        }

        /// <summary>
        /// PURE marker-draw / proto-icon-suppression decision (Phase 8c). True when the Parsek
        /// non-proto trajectory marker MUST draw for a ghost pid this frame because the stock proto
        /// icon is hidden; false when the stock proto icon is the visible indicator (skip our marker).
        /// This is the dual of "proto icon hidden", so it preserves the no-double-marker / no-gap
        /// invariant: exactly one of {proto icon, our marker} draws per ghost per frame.
        ///
        /// <para>The Director is the AUTHORITATIVE source (8e S4 dropped the director-drive gate, so this
        /// is now unconditional): proto suppressed when the Director's TracedPath DECISION owns the leg
        /// (<paramref name="directorTracedPathActive"/>, repointing the context-(a) icon-suppression that
        /// previously rode <c>ghostsWithSuppressedIcon</c>) OR the polyline actually owns the phase
        /// (<paramref name="polylineOwning"/>, sourced from 8b.2's / 8e S3a.1's actual-draw set, any leg that
        /// drew) OR the legacy <paramref name="iconSuppressedLegacy"/> is set. The legacy disjunct is KEPT as
        /// the fallback so the Director-no-bounds transient (context (b)), below-atmosphere, and off-arc clamp
        /// - none of which the Director owns yet - still draw the marker; retiring it is Phase 8f.</para>
        ///
        /// <para>No marker gap: the decision is a SUPERSET of the legacy decision (it adds the
        /// <paramref name="directorTracedPathActive"/> disjunct), so it can never be false on a frame the
        /// proto icon is hidden. No double marker: whenever <paramref name="directorTracedPathActive"/> is
        /// true the orbit-line Postfix's first branch has set the proto <c>drawIcons=NONE</c>, so the proto
        /// icon is not also drawn.</para>
        /// </summary>
        internal static bool ResolveMarkerDrawDecision(
            bool directorTracedPathActive,
            bool polylineOwning,
            bool iconSuppressedLegacy)
            => directorTracedPathActive || polylineOwning || iconSuppressedLegacy;

        /// <summary>
        /// Unity-coupled wrapper over <see cref="ResolveMarkerDrawDecision"/> (Phase 8c): resolves the
        /// three per-pid signals and returns whether the Parsek non-proto marker must draw for
        /// <paramref name="ghostPid"/> this frame (proto icon hidden). Both marker call sites
        /// (<c>ParsekUI.DrawMapMarkers</c> flight-map, <c>ParsekTrackingStation
        /// .ClassifyAtmosphericMarkerSkip</c> TS) route through this single source so they cannot diverge.
        /// </summary>
        internal static bool ShouldDrawNonProtoMarkerForGhost(uint ghostPid)
        {
            return ShouldDrawNonProtoMarkerForGhost(
                ghostPid, out _, out _, out _);
        }

        /// <summary>
        /// Diagnostics overload of <see cref="ShouldDrawNonProtoMarkerForGhost(uint)"/> that ALSO
        /// surfaces the three decision inputs the marker tracer logs (the
        /// <see cref="ResolveMarkerDrawDecision"/> disjuncts) WITHOUT changing the decision: the
        /// parameterless overload above delegates here, so the returned bool is byte-identical. The
        /// <c>out</c> values let the call site emit a per-pid change-based trace line explaining WHY
        /// the marker drew or was skipped. (8e S4 dropped the director-drive gate, so the former
        /// <c>gateOn</c> out is gone.)
        /// </summary>
        internal static bool ShouldDrawNonProtoMarkerForGhost(
            uint ghostPid,
            out bool directorTracedPathActive,
            out bool polylineOwning,
            out bool iconSuppressed)
        {
            directorTracedPathActive = Parsek.MapRender.ShadowRenderDriver.IsDirectorTracedPathActive(
                ghostPid, UnityEngine.Time.frameCount);
            polylineOwning = IsPolylineOwningGhostPhase(ghostPid);
            iconSuppressed = IsIconSuppressed(ghostPid);
            return ResolveMarkerDrawDecision(
                directorTracedPathActive,
                polylineOwning,
                iconSuppressed);
        }

        /// <summary>
        /// Phase 7 of Rewind-to-Staging (design §3.3): shared helper — is the
        /// recording at this index in the active session's SessionSuppressedSubtree?
        /// Returns false when no session is active or the index is out of range.
        /// </summary>
        internal static bool IsSuppressedByActiveSession(int recordingIndex)
        {
            return SessionSuppressionState.IsSuppressedRecordingIndex(recordingIndex);
        }

        /// <summary>
        /// Bug #587 third facet (2026-04-25 playtest): the in-place continuation
        /// Re-Fly path leaves the *parent* of the active Re-Fly recording outside
        /// the SessionSuppressedSubtree closure (the closure walks child-ward
        /// from <c>OriginChildRecordingId</c>). When that parent recording's
        /// playback is mid-flight in a <see cref="ReferenceFrame.Relative"/>
        /// section anchored to the *active Re-Fly target's* persistent id, the
        /// state-vector-fallback code path in
        /// <see cref="CreateGhostVesselFromStateVectors"/> resolves a world
        /// position right next to the live active vessel and feeds it (with
        /// the recording's atmospheric-ascent velocity) to
        /// <see cref="Orbit.UpdateFromStateVectors"/>. The result is a
        /// degenerate orbit (sma=2 ecc=0.999999) AND a real registered
        /// <see cref="Vessel"/> colocated with the player's vessel — the
        /// "doubled upper-stage" the user reported.
        ///
        /// <para>This predicate gates that one create site without touching any
        /// other source path. Pure-static so xUnit can pin every branch
        /// without a live KSP scene.</para>
        ///
        /// <para><b>Scope (PR #574 review P2):</b> the suppression is also
        /// gated on the *recording relationship* between the recording being
        /// mapped (<paramref name="victimRecordingId"/>) and the active Re-Fly
        /// recording. The bug only manifests when the victim is a
        /// <em>parent</em> (in the BranchPoint topology sense) of the active
        /// Re-Fly recording — that is, the recording from which the active
        /// was decoupled or otherwise branched. A docking-target recording
        /// or any unrelated recording that happens to be Relative-anchored to
        /// the active vessel (legitimate #583/#584 docking/rendezvous map
        /// ghosts) is *not* suppressed. The parent walk uses
        /// <see cref="Recording.ParentBranchPointId"/> +
        /// <see cref="BranchPoint.ParentRecordingIds"/> via the committed
        /// trees — the same edge data
        /// <see cref="EffectiveState.ComputeSessionSuppressedSubtree"/> walks
        /// child-ward, traversed in the opposite direction.</para>
        ///
        /// <para><b>Retry semantics (PR #574 review P2):</b> when the
        /// predicate returns true at the call site
        /// <see cref="CreateGhostVesselFromStateVectors"/>, the function
        /// returns null. The flight-scene caller
        /// <c>ParsekPlaybackPolicy.CheckPendingMapVessels</c> normally drops
        /// the pending-map entry on null return — that would mean the
        /// recording never re-attempts after the Re-Fly session ends. The
        /// caller now consults <paramref name="retryLater"/> via
        /// <see cref="CreateGhostVesselFromSource(int, IPlaybackTrajectory,
        /// TrackingStationGhostSource, OrbitSegment, TrajectoryPoint, double,
        /// out bool)"/> and keeps the pending entry alive when this gate
        /// fires, so suppression is "skip-this-tick", not "permanent-reject".</para>
        ///
        /// <para>Sister fixes (#587 and the #587 follow-up) targeted the
        /// strip-side leftover (a pre-existing in-scene <c>Vessel</c> the
        /// PostLoadStripper missed). This third facet targets the GhostMap-side
        /// *creation* of a fresh ProtoVessel during the same Re-Fly invocation
        /// — the strip side never sees this vessel because it is born after
        /// strip runs.</para>
        /// </summary>
        /// <param name="marker">Live re-fly marker, or null.</param>
        /// <param name="resolutionBranch">Branch label from
        /// <see cref="StateVectorWorldFrame.Branch"/>: <c>"relative"</c> AND
        /// <c>"body-fixed-primary"</c> both suppress, because both describe a
        /// RELATIVE track section. The body-fixed-primary branch is a retained
        /// v7 compatibility branch for callers that already selected the
        /// recorded shadow point; create-time lookahead no longer performs a
        /// live-PID anchor scan. Suppressing both labels preserves the
        /// doubled-ProtoVessel guard without reintroducing non-loop live-anchor
        /// map resolution (PR #613 review P2).</param>
        /// <param name="resolutionAnchorPid">Anchor pid from the resolution.</param>
        /// <param name="victimRecordingId">RecordingId of the recording being
        /// mapped. Suppression is rejected with
        /// <c>not-suppressed-not-parent-of-refly-target</c> when the victim is
        /// not in the active Re-Fly recording's parent chain.</param>
        /// <param name="committedRecordings">Snapshot of <see cref="RecordingStore.CommittedRecordings"/>
        /// (the flat committed list). Used as a secondary lookup source for
        /// the active Re-Fly recording's PID. At Re-Fly load time the active
        /// recording's tree has been moved into <c>PendingTree</c> so its
        /// recordings are NOT in this list (#611 P1 follow-up); the primary
        /// lookup now walks <paramref name="committedTrees"/>, which production
        /// composes from CommittedTrees ++ PendingTree via
        /// <see cref="ComposeSearchTreesForReFlySuppression"/>. Tests pass a
        /// list directly; production passes the live property.</param>
        /// <param name="committedTrees">Trees searched for both BranchPoint
        /// topology lookup AND the active Re-Fly recording's PID. Production
        /// composes <see cref="RecordingStore.CommittedTrees"/> ++
        /// <see cref="RecordingStore.PendingTree"/> via
        /// <see cref="ComposeSearchTreesForReFlySuppression"/>; tests pass a
        /// list directly. Despite the legacy parameter name, this list MUST
        /// include the pending tree for the load-window predicate to fire
        /// (#611 P1 follow-up).</param>
        /// <param name="suppressReason">On true, a structured human-readable
        /// reason for the log line including the relationship scope. On false,
        /// set to "not-suppressed-..." describing which gate clause rejected
        /// the suppression.</param>
        /// <returns>True iff the state-vector ProtoVessel should NOT be
        /// created because its world position would land on top of the
        /// active Re-Fly target AND the victim recording is in the active
        /// Re-Fly recording's parent chain.</returns>
        internal static bool ShouldSuppressStateVectorProtoVesselForActiveReFly(
            ReFlySessionMarker marker,
            string resolutionBranch,
            uint resolutionAnchorPid,
            string victimRecordingId,
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<RecordingTree> committedTrees,
            out string suppressReason)
        {
            if (marker == null)
            {
                suppressReason = "not-suppressed-no-marker";
                return false;
            }

            if (string.IsNullOrEmpty(marker.ActiveReFlyRecordingId)
                || string.IsNullOrEmpty(marker.OriginChildRecordingId))
            {
                suppressReason = "not-suppressed-marker-fields-empty";
                return false;
            }

            // Placeholder pattern (active != origin AND no InPlaceContinuation
            // flag): the live vessel in scene is a fresh strip-spawned
            // vessel different from origin, not the same physical craft, so
            // the parent-chain doubled-ProtoVessel risk does not apply.
            // The post-#734 fork case (InPlaceContinuation=true with active
            // != origin) DOES need suppression -- the player is still flying
            // the same physical vessel as origin, just routed through a
            // separate provisional Recording.
            if (!ReFlySessionMarker.IsInPlaceContinuation(marker))
            {
                suppressReason = "not-suppressed-placeholder-pattern";
                return false;
            }

            // Accept both "relative" and "body-fixed-primary" for legacy
            // suppression decisions. Phase D keeps this helper for caller
            // compatibility, but create-time lookahead no longer performs a
            // live-PID anchor scan.
            bool branchSuppresses =
                string.Equals(resolutionBranch, "relative", StringComparison.Ordinal)
                || string.Equals(resolutionBranch, "body-fixed-primary", StringComparison.Ordinal);
            if (!branchSuppresses)
            {
                suppressReason = "not-suppressed-not-relative-frame";
                return false;
            }

            if (resolutionAnchorPid == 0u)
            {
                suppressReason = "not-suppressed-no-anchor-pid";
                return false;
            }

            // #611 P1 follow-up: the PID lookup MUST search the composed trees
            // (committed ++ pending) and not just the flat CommittedRecordings
            // list. At Re-Fly load time TryRestoreActiveTreeNode has just
            // detached this tree from CommittedTrees (and therefore from
            // CommittedRecordings) and re-stashed it as PendingTree, so the
            // flat list lookup that ran first would silently bail with
            // not-suppressed-active-rec-pid-unknown -- before the new
            // pending-tree topology walk got a chance to run -- and the
            // doubled ProtoVessel still got created.
            uint activeReFlyPid = 0u;
            string activePidSource = "<not-found>";
            if (committedTrees != null)
            {
                // If the marker tree contains no usable PID, keep the legacy
                // fallback so older/partial marker state can still suppress
                // via the first tree that has a complete active recording.
                if (TryFindActiveReFlyRecordingInSearchTrees(
                        committedTrees,
                        marker.ActiveReFlyRecordingId,
                        marker.TreeId,
                        requireNonZeroPid: true,
                        out RecordingTree _,
                        out Recording activePidRecording,
                        out string treePidSource,
                        out int _))
                {
                    activeReFlyPid = activePidRecording.VesselPersistentId;
                    activePidSource = treePidSource;
                }
            }
            if (activeReFlyPid == 0u && committedRecordings != null)
            {
                for (int i = 0; i < committedRecordings.Count; i++)
                {
                    Recording rec = committedRecordings[i];
                    if (rec == null) continue;
                    if (!string.Equals(rec.RecordingId, marker.ActiveReFlyRecordingId,
                            StringComparison.Ordinal))
                        continue;
                    activeReFlyPid = rec.VesselPersistentId;
                    activePidSource = "committed-recordings-flat-list";
                    break;
                }
            }

            if (activeReFlyPid == 0u)
            {
                int treeCount = committedTrees != null ? committedTrees.Count : 0;
                int recCount = committedRecordings != null ? committedRecordings.Count : 0;
                suppressReason = "not-suppressed-active-rec-pid-unknown searchTrees="
                    + treeCount + " committedRecordings=" + recCount
                    + " activeRecId=" + (marker.ActiveReFlyRecordingId ?? "<null>");
                return false;
            }

            if (resolutionAnchorPid != activeReFlyPid)
            {
                suppressReason = "not-suppressed-anchor-not-active-refly";
                return false;
            }

            // PR #574 review P2: the anchor-equality predicate is necessary
            // but not sufficient. A docking-target / rendezvous recording or
            // any sibling recording could legitimately be Relative-anchored
            // to the active vessel (cf. #583 / #584). Restrict suppression
            // to the user's actual case: the recording being mapped is in
            // the active Re-Fly recording's *parent* chain (i.e. the
            // recording from which the active was decoupled or otherwise
            // branched).
            if (string.IsNullOrEmpty(victimRecordingId))
            {
                suppressReason = "not-suppressed-no-victim-id";
                return false;
            }

            if (string.Equals(victimRecordingId, marker.ActiveReFlyRecordingId,
                    StringComparison.Ordinal))
            {
                // The active recording itself is already covered by the
                // SessionSuppressedSubtree gate (IsSuppressedByActiveSession);
                // this branch keeps the predicate idempotent for the unlikely
                // case that the active recording reaches this code path.
                suppressReason = "not-suppressed-victim-is-active";
                return false;
            }

            if (!IsRecordingInParentChainOfActiveReFly(
                    victimRecordingId,
                    marker.ActiveReFlyRecordingId,
                    committedTrees,
                    marker.TreeId,
                    out string walkTrace))
            {
                // #611: append the BFS walk trace so the rejection log line
                // carries enough detail to diagnose missing-active-tree /
                // missing-parent-BP / topology-mismatch cases.
                suppressReason = "not-suppressed-not-parent-of-refly-target walkTrace=("
                    + walkTrace + ")";
                return false;
            }

            // Bubble the trace into the success reason too so the
            // create-state-vector-suppressed log line can show the chain
            // that was matched. Helps reviewers / playtest log scrapers
            // confirm the gate fired for the right relationship.
            // #611 P1 follow-up: also include where the active PID was
            // resolved (which tree, or the flat fallback list) so the
            // load-window vs steady-state distinction is auditable.
            suppressReason = "refly-relative-anchor=active relationship=parent activePidSource="
                + activePidSource + " walkTrace=(" + walkTrace + ")";
            return true;
        }

        internal static bool ShouldSuppressStateVectorProtoVesselForActiveReFlyAtCreateTime(
            ReFlySessionMarker marker,
            string resolutionBranch,
            uint resolutionAnchorPid,
            IPlaybackTrajectory traj,
            double currentUT,
            string victimRecordingId,
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<RecordingTree> committedTrees,
            out string suppressReason)
        {
            if (ShouldSuppressStateVectorProtoVesselForActiveReFly(
                    marker,
                    resolutionBranch,
                    resolutionAnchorPid,
                    victimRecordingId,
                    committedRecordings,
                    committedTrees,
                    out suppressReason))
            {
                return true;
            }

            string directReason = suppressReason;
            if (TryFindActiveReFlyRelativeLookaheadSuppression(
                    marker,
                    traj,
                    currentUT,
                    victimRecordingId,
                    committedRecordings,
                    committedTrees,
                    out string lookaheadReason))
            {
                suppressReason = string.Format(ic,
                    "{0} currentBranch={1} direct=({2})",
                    lookaheadReason,
                    resolutionBranch ?? "(null)",
                    directReason ?? "(none)");
                return true;
            }

            suppressReason = string.Format(ic,
                "{0} lookahead=({1})",
                directReason ?? "not-suppressed",
                lookaheadReason ?? "(none)");
            return false;
        }

        internal static bool ShouldRemoveStateVectorProtoVesselForActiveReFlyOnUpdate(
            ReFlySessionMarker marker,
            string resolutionBranch,
            uint resolutionAnchorPid,
            string victimRecordingId,
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<RecordingTree> committedTrees,
            out string suppressReason)
        {
            if (!ShouldSuppressStateVectorProtoVesselForActiveReFly(
                    marker,
                    resolutionBranch,
                    resolutionAnchorPid,
                    victimRecordingId,
                    committedRecordings,
                    committedTrees,
                    out suppressReason))
            {
                return false;
            }

            suppressReason = TrackingStationGhostSkipActiveReFlyRelativeUpdate
                + " " + suppressReason;
            return true;
        }

        private static bool TryFindActiveReFlyRelativeLookaheadSuppression(
            ReFlySessionMarker marker,
            IPlaybackTrajectory traj,
            double currentUT,
            string victimRecordingId,
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<RecordingTree> committedTrees,
            out string suppressReason)
        {
            suppressReason = "lookahead-disabled-recorded-anchor-chain";
            return false;
        }

        private static string GetActiveReFlyDeferredSessionKey(ReFlySessionMarker marker)
        {
            if (marker == null)
                return null;
            if (!string.IsNullOrEmpty(marker.SessionId))
                return marker.SessionId;
            return marker.ActiveReFlyRecordingId;
        }

        private static void MarkStateVectorGhostDeferredForActiveReFly(int recordingIndex)
        {
            string sessionKey = GetActiveReFlyDeferredSessionKey(SessionSuppressionState.ActiveMarker);
            if (string.IsNullOrEmpty(sessionKey))
                return;
            activeReFlyDeferredStateVectorGhostSessions[recordingIndex] = sessionKey;
        }

        private static bool IsStateVectorGhostDeferredForActiveReFlySession(
            int recordingIndex,
            out string sessionKey)
        {
            sessionKey = null;
            if (!activeReFlyDeferredStateVectorGhostSessions.TryGetValue(
                    recordingIndex,
                    out string storedSessionKey))
            {
                return false;
            }

            string activeSessionKey =
                GetActiveReFlyDeferredSessionKey(SessionSuppressionState.ActiveMarker);
            if (string.IsNullOrEmpty(activeSessionKey)
                || !string.Equals(storedSessionKey, activeSessionKey, StringComparison.Ordinal))
            {
                activeReFlyDeferredStateVectorGhostSessions.Remove(recordingIndex);
                return false;
            }

            sessionKey = storedSessionKey;
            return true;
        }

        private static bool TryFindActiveReFlyRecordingInSearchTrees(
            IReadOnlyList<RecordingTree> searchTrees,
            string activeRecordingId,
            string activeTreeId,
            bool requireNonZeroPid,
            out RecordingTree matchedTree,
            out Recording matchedRecording,
            out string source,
            out int treesSearched)
        {
            matchedTree = null;
            matchedRecording = null;
            source = "<not-found>";
            treesSearched = 0;

            if (searchTrees == null || string.IsNullOrEmpty(activeRecordingId))
                return false;

            RecordingTree fallbackTree = null;
            Recording fallbackRecording = null;
            string fallbackSource = null;
            bool hasActiveTreeId = !string.IsNullOrEmpty(activeTreeId);

            for (int i = 0; i < searchTrees.Count; i++)
            {
                RecordingTree tree = searchTrees[i];
                if (tree?.Recordings == null) continue;
                treesSearched++;
                bool isMarkerTree = hasActiveTreeId
                    && string.Equals(tree.Id, activeTreeId, StringComparison.Ordinal);
                if (TryAcceptActiveReFlyRecordingTree(
                        tree,
                        activeRecordingId,
                        requireNonZeroPid,
                        isMarkerTree ? "marker-tree" : "search-tree",
                        out matchedRecording,
                        out source))
                {
                    if (isMarkerTree)
                    {
                        matchedTree = tree;
                        return true;
                    }

                    if (fallbackTree == null)
                    {
                        fallbackTree = tree;
                        fallbackRecording = matchedRecording;
                        fallbackSource = source;
                    }

                    if (!hasActiveTreeId)
                        break;
                }
            }

            if (fallbackTree == null)
                return false;

            matchedTree = fallbackTree;
            matchedRecording = fallbackRecording;
            source = fallbackSource;
            return true;
        }

        private static bool TryAcceptActiveReFlyRecordingTree(
            RecordingTree tree,
            string activeRecordingId,
            bool requireNonZeroPid,
            string sourcePrefix,
            out Recording matchedRecording,
            out string source)
        {
            matchedRecording = null;
            source = "<not-found>";

            if (tree == null || tree.Recordings == null)
                return false;
            if (!tree.Recordings.TryGetValue(activeRecordingId, out Recording recording)
                || recording == null)
            {
                return false;
            }
            if (requireNonZeroPid && recording.VesselPersistentId == 0u)
                return false;

            matchedRecording = recording;
            source = sourcePrefix + ":" + (tree.Id ?? "<no-id>");
            return true;
        }

        /// <summary>
        /// Pure: walks the BranchPoint topology parent-ward from the active
        /// Re-Fly recording and returns true when <paramref name="victimRecordingId"/>
        /// is encountered in any parent BP's <see cref="BranchPoint.ParentRecordingIds"/>.
        /// Mirror of the child-ward closure in
        /// <see cref="EffectiveState.ComputeSessionSuppressedSubtree"/>, traversed
        /// in the opposite direction. Returns false on any structural defect
        /// (null trees / missing tree / missing recording / cycle), erring on
        /// the side of NOT suppressing.
        ///
        /// <para><b>Observability (#611):</b> emits a single Verbose
        /// <c>[GhostMap] parent-chain-walk</c> log line per call describing
        /// the search outcome (active-found-in tree id / source label,
        /// visitedBPs count + ids, parents-encountered count + ids, terminate
        /// reason). Mirrors the existing <c>SessionSuppressedSubtree</c>
        /// summary line so the same structured-grep tooling works for both
        /// closures.</para>
        ///
        /// <para><b>#611 fix:</b> at Re-Fly load time the active recording
        /// lives in <see cref="RecordingStore.PendingTree"/>, NOT
        /// <see cref="RecordingStore.CommittedTrees"/> (the committed copy is
        /// removed by <c>TryRestoreActiveTreeNode</c>'s post-splice
        /// <c>RemoveCommittedTreeById</c>). Callers must therefore compose
        /// <paramref name="searchTrees"/> from BOTH committed + pending so
        /// the active-tree lookup succeeds whether the load just happened
        /// or the player is in steady-state flight.</para>
        ///
        /// <para><b>#614 fix:</b> the walk now follows BOTH BranchPoint-parents
        /// AND chain-predecessors. Optimizer splits
        /// (<see cref="RecordingStore"/> RunOptimizationSplitPass) connect
        /// chain segments via shared <see cref="Recording.ChainId"/> alone — the
        /// second half receives no <see cref="Recording.ParentBranchPointId"/>.
        /// The pre-#614 BP-only walk silently terminated at the first chain
        /// segment it reached, missing the root and any earlier segments. The
        /// fix seeds the walk with the active recording's chain predecessor
        /// (when present) and enqueues a chain predecessor for every recording
        /// reached during fan-out. <c>chainHops</c> in the walk trace counts
        /// these enqueues so a future regression shows up as <c>chainHops=0</c>
        /// on a topology that should have chain links.</para>
        /// </summary>
        /// <param name="searchTrees">Trees to search. Production callers
        /// compose <see cref="RecordingStore.CommittedTrees"/> ++
        /// <see cref="RecordingStore.PendingTree"/> (when present). Tests
        /// pass an explicit list.</param>
        /// <param name="walkTrace">Diagnostic summary for the caller's
        /// structured log line. Always populated, even on early-return.</param>
        internal static bool IsRecordingInParentChainOfActiveReFly(
            string victimRecordingId,
            string activeRecordingId,
            IReadOnlyList<RecordingTree> searchTrees,
            out string walkTrace)
        {
            return IsRecordingInParentChainOfActiveReFly(
                victimRecordingId,
                activeRecordingId,
                searchTrees,
                activeTreeId: null,
                out walkTrace);
        }

        internal static bool IsRecordingInParentChainOfActiveReFly(
            string victimRecordingId,
            string activeRecordingId,
            IReadOnlyList<RecordingTree> searchTrees,
            string activeTreeId,
            out string walkTrace)
        {
            walkTrace = "no-input";
            if (string.IsNullOrEmpty(victimRecordingId)
                || string.IsNullOrEmpty(activeRecordingId)
                || searchTrees == null)
            {
                return false;
            }

            // Locate the tree containing the active recording. Search every
            // input tree so a Pending-Limbo-stashed tree (Re-Fly load window)
            // is found alongside committed trees.
            if (!TryFindActiveReFlyRecordingInSearchTrees(
                    searchTrees,
                    activeRecordingId,
                    activeTreeId,
                    requireNonZeroPid: false,
                    out RecordingTree tree,
                    out Recording active,
                    out string _,
                    out int treesSearched))
            {
                walkTrace = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "active-not-found activeId={0} treeId={1} treesSearched={2}",
                    activeRecordingId, activeTreeId ?? "<none>", treesSearched);
                return false;
            }
            // #614: walk both BranchPoint-parents AND chain-predecessors. Optimizer
            // splits (RecordingStore.RunOptimizationSplitPass) connect chain segments
            // via shared ChainId / ChainIndex, NOT via ParentBranchPointId — so the
            // older BP-only walk silently terminated when it reached a chain segment
            // whose parent was a previous chain segment with no BP link, missing the
            // root and any earlier chain segments. Seed the walk with the active
            // recording's parent BP AND its chain predecessor; for every recording
            // reached, enqueue its parent BP AND its chain predecessor.
            var pendingBPs = new Queue<string>();
            var pendingRecs = new Queue<string>();
            var visitedBPs = new HashSet<string>();
            var visitedRecs = new HashSet<string>();
            var bpTrace = new List<string>();
            var parentTrace = new List<string>();
            int bpsNotFound = 0;
            int chainHopsEnqueued = 0;
            int chainHopsAncestorPredecessor = 0;

            // Seed: parent BP of the active recording (may be empty for chain
            // mid-segments and tree roots).
            if (!string.IsNullOrEmpty(active.ParentBranchPointId))
                pendingBPs.Enqueue(active.ParentBranchPointId);

            // Seed: chain predecessor of the active recording. The active itself
            // is not a victim (the victim-is-active short-circuit fires above),
            // but the active may sit mid-chain whose previous segment IS a victim.
            string activePredecessorId = TryFindChainPredecessor(tree, active);
            if (!string.IsNullOrEmpty(activePredecessorId))
            {
                pendingRecs.Enqueue(activePredecessorId);
                chainHopsEnqueued++;
            }

            // Bail if neither seed produced any work: the active is a true root
            // with no chain predecessor.
            if (pendingBPs.Count == 0 && pendingRecs.Count == 0)
            {
                walkTrace = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "active-has-no-parent activeId={0} treeId={1}",
                    activeRecordingId, tree.Id ?? "<no-id>");
                return false;
            }

            // Helper-style local: process a parent recording id (whether reached
            // via BP-parent fan-out or chain-predecessor link). Returns true when
            // the victim is found.
            bool VisitRecordingId(string recId, string discoverySource)
            {
                if (string.IsNullOrEmpty(recId) || !visitedRecs.Add(recId))
                    return false;

                parentTrace.Add(recId);
                if (string.Equals(recId, victimRecordingId, StringComparison.Ordinal))
                    return true;

                if (!tree.Recordings.TryGetValue(recId, out Recording rec) || rec == null)
                    return false;

                // BP link to next ancestor up.
                if (!string.IsNullOrEmpty(rec.ParentBranchPointId))
                    pendingBPs.Enqueue(rec.ParentBranchPointId);

                // Chain link to previous segment (no BP). #614: this is the leg
                // the old walk missed — optimizer-split chain predecessors share
                // ChainId but have no ParentBranchPointId on the second half.
                string predId = TryFindChainPredecessor(tree, rec);
                if (!string.IsNullOrEmpty(predId))
                {
                    pendingRecs.Enqueue(predId);
                    chainHopsEnqueued++;
                    chainHopsAncestorPredecessor++;
                }

                return false;
            }

            while (pendingBPs.Count > 0 || pendingRecs.Count > 0)
            {
                // Drain any chain-predecessor recordings first so the walk
                // surfaces direct chain ancestry before fanning out via BPs.
                while (pendingRecs.Count > 0)
                {
                    string recId = pendingRecs.Dequeue();
                    if (VisitRecordingId(recId, "chain-predecessor"))
                    {
                        walkTrace = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "found-victim-in-parent-chain activeId={0} treeId={1} victim={2} " +
                            "visitedBPs={3} parentsEncountered={4} chainHops={5} bps=[{6}] parents=[{7}]",
                            activeRecordingId, tree.Id ?? "<no-id>", victimRecordingId,
                            visitedBPs.Count, visitedRecs.Count, chainHopsEnqueued,
                            string.Join(",", bpTrace.ToArray()),
                            string.Join(",", parentTrace.ToArray()));
                        return true;
                    }
                }

                if (pendingBPs.Count == 0) break;

                string bpId = pendingBPs.Dequeue();
                if (string.IsNullOrEmpty(bpId) || !visitedBPs.Add(bpId))
                    continue;

                BranchPoint bp = null;
                for (int i = 0; i < tree.BranchPoints.Count; i++)
                {
                    if (tree.BranchPoints[i] != null
                        && string.Equals(tree.BranchPoints[i].Id, bpId, StringComparison.Ordinal))
                    {
                        bp = tree.BranchPoints[i];
                        break;
                    }
                }
                if (bp == null)
                {
                    bpsNotFound++;
                    bpTrace.Add(bpId + ":not-found");
                    continue;
                }
                bpTrace.Add(bpId + ":parents=" + (bp.ParentRecordingIds?.Count ?? 0));
                if (bp.ParentRecordingIds == null) continue;

                for (int i = 0; i < bp.ParentRecordingIds.Count; i++)
                {
                    if (VisitRecordingId(bp.ParentRecordingIds[i], "bp-parent"))
                    {
                        walkTrace = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "found-victim-in-parent-chain activeId={0} treeId={1} victim={2} " +
                            "visitedBPs={3} parentsEncountered={4} chainHops={5} bps=[{6}] parents=[{7}]",
                            activeRecordingId, tree.Id ?? "<no-id>", victimRecordingId,
                            visitedBPs.Count, visitedRecs.Count, chainHopsEnqueued,
                            string.Join(",", bpTrace.ToArray()),
                            string.Join(",", parentTrace.ToArray()));
                        return true;
                    }
                }
            }

            // Ancestor-walk summary: chainHops counts every chain-predecessor
            // enqueue (active-seed + every parent recording's chain predecessor)
            // so a future regression of the #614-style bug shows up here as a
            // suspicious chainHops=0 on a topology that should have chain links.
            walkTrace = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "exhausted-without-victim activeId={0} treeId={1} victim={2} " +
                "visitedBPs={3} parentsEncountered={4} chainHops={5} chainHopsViaAncestors={6} " +
                "bpsNotFound={7} bps=[{8}] parents=[{9}]",
                activeRecordingId, tree.Id ?? "<no-id>", victimRecordingId,
                visitedBPs.Count, visitedRecs.Count, chainHopsEnqueued, chainHopsAncestorPredecessor,
                bpsNotFound,
                string.Join(",", bpTrace.ToArray()),
                string.Join(",", parentTrace.ToArray()));
            return false;
        }

        /// <summary>
        /// #614: returns the recording-id of <paramref name="rec"/>'s chain
        /// predecessor in <paramref name="tree"/>, or null when the recording
        /// is standalone, is the first chain segment (ChainIndex == 0), or
        /// when no matching predecessor exists in the tree.
        /// </summary>
        /// <remarks>
        /// Optimizer splits (<see cref="RecordingStore"/> RunOptimizationSplitPass)
        /// link chain segments via shared <see cref="Recording.ChainId"/> and
        /// monotonically increasing <see cref="Recording.ChainIndex"/> with the
        /// same <see cref="Recording.ChainBranch"/>. The second half does NOT
        /// receive a <see cref="Recording.ParentBranchPointId"/> — the chain id
        /// is the only ancestry link.
        /// </remarks>
        internal static string TryFindChainPredecessor(RecordingTree tree, Recording rec)
        {
            if (tree?.Recordings == null || rec == null) return null;
            if (string.IsNullOrEmpty(rec.ChainId)) return null;
            if (rec.ChainIndex <= 0) return null;

            int targetIdx = rec.ChainIndex - 1;
            foreach (var kvp in tree.Recordings)
            {
                Recording other = kvp.Value;
                if (other == null) continue;
                if (!string.Equals(other.ChainId, rec.ChainId, StringComparison.Ordinal))
                    continue;
                if (other.ChainBranch != rec.ChainBranch) continue;
                if (other.ChainIndex != targetIdx) continue;
                return other.RecordingId;
            }
            return null;
        }

        private static double GetCurrentUTSafe()
        {
            return Planetarium.GetUniversalTime();
        }

        private static string BuildTrackingStationGhostSourceSummaryKey(
            TrackingStationGhostSource source,
            string reason)
        {
            if (source == TrackingStationGhostSource.None)
                return "skip-" + (string.IsNullOrEmpty(reason) ? "unspecified" : reason);

            return "source-" + FormatTrackingStationGhostSource(source);
        }

        private static string FormatTrackingStationGhostSource(TrackingStationGhostSource source)
        {
            switch (source)
            {
                case TrackingStationGhostSource.Segment:
                    return "visible-segment";
                case TrackingStationGhostSource.TerminalOrbit:
                    return "terminal-orbit";
                case TrackingStationGhostSource.EndpointTail:
                    return "endpoint-tail";
                case TrackingStationGhostSource.StateVector:
                    return "state-vector";
                case TrackingStationGhostSource.StateVectorSoiGap:
                    return "soi-gap-state-vector";
                default:
                    return "none";
            }
        }

        private static string BuildTrackingStationGhostSourceLogLine(
            string context,
            int recordingIndex,
            Recording rec,
            double currentUT,
            TrackingStationGhostSource source,
            string reason,
            string detail)
        {
            string recId = rec?.RecordingId ?? "(null)";
            string vesselName = rec?.VesselName ?? "(null)";
            string terminal = rec?.TerminalStateValue?.ToString() ?? "(null)";
            string endpointPhase = rec != null ? rec.EndpointPhase.ToString() : "(null)";
            string endpointBody = string.IsNullOrEmpty(rec?.EndpointBodyName)
                ? "(none)"
                : rec.EndpointBodyName;
            string terminalBody = string.IsNullOrEmpty(rec?.TerminalOrbitBody)
                ? "(none)"
                : rec.TerminalOrbitBody;

            return string.Format(ic,
                "ResolveTrackingStationGhostSource: Tracking-station orbit source: context={0} recIndex={1} rec={2} vessel=\"{3}\" " +
                "currentUT={4:F1} source={5} orbitSource={6} reason={7} terminal={8} terminalBody={9} terminalSma={10:F0} " +
                "endpoint=({11},{12}) hasSegments={13} vesselSpawned={14} spawnedPid={15}{16}",
                string.IsNullOrEmpty(context) ? "unspecified" : context,
                recordingIndex,
                recId,
                vesselName,
                currentUT,
                source,
                FormatTrackingStationGhostSource(source),
                string.IsNullOrEmpty(reason) ? "(none)" : reason,
                terminal,
                terminalBody,
                rec?.TerminalOrbitSemiMajorAxis ?? 0.0,
                endpointPhase,
                endpointBody,
                rec?.HasOrbitSegments ?? false,
                rec?.VesselSpawned ?? false,
                rec?.SpawnedVesselPersistentId ?? 0u,
                string.IsNullOrEmpty(detail) ? string.Empty : " " + detail);
        }

        private static void LogTrackingStationGhostSourceDecision(
            string context,
            int recordingIndex,
            Recording rec,
            double currentUT,
            TrackingStationGhostSource source,
            string reason,
            string detail,
            TrackingStationGhostSourceBatch batch)
        {
            if (batch != null)
            {
                batch.Observe(recordingIndex, rec, currentUT, source, reason, detail);
                return;
            }

            ParsekLog.Verbose(
                Tag,
                BuildTrackingStationGhostSourceLogLine(
                    context,
                    recordingIndex,
                    rec,
                    currentUT,
                    source,
                    reason,
                    detail));
        }

        private static bool HasTerminalOrbitData(IPlaybackTrajectory traj)
        {
            return traj != null
                && !string.IsNullOrEmpty(traj.TerminalOrbitBody)
                && traj.TerminalOrbitSemiMajorAxis > 0;
        }

        // ------------------------------------------------------------------
        // Pure data layer (unchanged from original)
        // ------------------------------------------------------------------

        /// <summary>
        /// Pure: does this recording have orbital data suitable for map presence?
        /// True if terminal orbit body is set and SMA > 0.
        /// </summary>
        /// <remarks>
        /// Logs via <see cref="ParsekLog.VerboseOnChange"/> keyed on
        /// <c>(recordingId, body, smaBucket, result)</c> so per-frame stable
        /// callers do not flood KSP.log: the 2026-04-25 playtest recorded
        /// ~1678 redundant emissions in a 27-minute session before this gate.
        /// </remarks>
        internal static bool HasOrbitData(Recording rec)
        {
            if (rec == null)
            {
                ParsekLog.VerboseOnChange(
                    Tag,
                    "has-orbit-data-rec|null",
                    "null",
                    "HasOrbitData(Recording): null recording — returning false");
                return false;
            }

            bool hasOrbit = HasTerminalOrbitData(rec);

            string recId = rec.RecordingId ?? "(null)";
            string body = rec.TerminalOrbitBody ?? "(null)";
            double sma = rec.TerminalOrbitSemiMajorAxis;
            ParsekLog.VerboseOnChange(
                Tag,
                string.Format(ic, "has-orbit-data-rec|{0}", recId),
                BuildHasOrbitDataStateKey(body, sma, hasOrbit),
                string.Format(ic,
                    "HasOrbitData(Recording): rec={0} body={1} sma={2} result={3}",
                    recId,
                    body,
                    sma,
                    hasOrbit));

            return hasOrbit;
        }

        /// <summary>
        /// Pure: does this trajectory have orbital data suitable for map presence?
        /// Overload accepting IPlaybackTrajectory for engine-side use.
        /// </summary>
        /// <remarks>
        /// Per-frame map-view callers (<see cref="ParsekPlaybackPolicy"/> + the
        /// shared map-presence resolver) hammered this method ~11/sec in the
        /// 2026-04-25 playtest. Logging is gated through
        /// <see cref="ParsekLog.VerboseOnChange"/> so a stable
        /// <c>(recordingId, body, smaBucket)</c> tuple emits exactly once per
        /// state change with <c>| suppressed=N</c> on the next flip.
        /// </remarks>
        internal static bool HasOrbitData(IPlaybackTrajectory traj)
        {
            if (traj == null)
            {
                ParsekLog.VerboseOnChange(
                    Tag,
                    "has-orbit-data-traj|null",
                    "null",
                    "HasOrbitData(IPlaybackTrajectory): null trajectory — returning false");
                return false;
            }

            bool hasOrbit = HasTerminalOrbitData(traj);

            if (hasOrbit)
            {
                string recId = traj.RecordingId;
                string identityScope = !string.IsNullOrEmpty(recId)
                    ? string.Format(ic, "has-orbit-data-traj|rec={0}", recId)
                    : string.Format(ic, "has-orbit-data-traj|name={0}",
                        traj.VesselName ?? "(unnamed)");
                ParsekLog.VerboseOnChange(
                    Tag,
                    identityScope,
                    BuildHasOrbitDataStateKey(traj.TerminalOrbitBody, traj.TerminalOrbitSemiMajorAxis, true),
                    string.Format(ic,
                        "HasOrbitData(IPlaybackTrajectory): body={0} sma={1} result=True",
                        traj.TerminalOrbitBody,
                        traj.TerminalOrbitSemiMajorAxis));
            }

            return hasOrbit;
        }

        /// <summary>
        /// Stable state-key builder for HasOrbitData log emissions. Buckets
        /// SMA to 1km so trivial floating-point drift between frames does not
        /// pop the on-change gate; <see cref="HasTerminalOrbitData"/>
        /// classifies "has orbit" as <c>SMA &gt; 0</c>, so a 1km bucket is
        /// far below any meaningful Recording terminal orbit.
        /// </summary>
        private static string BuildHasOrbitDataStateKey(string body, double sma, bool hasOrbit)
        {
            string safeBody = string.IsNullOrEmpty(body) ? "(null)" : body;
            long smaBucket = double.IsNaN(sma) || double.IsInfinity(sma)
                ? long.MinValue
                : (long)Math.Round(sma / 1000.0);
            return string.Format(ic, "{0}|sma={1}|res={2}",
                safeBody, smaBucket, hasOrbit);
        }

        /// <summary>
        /// Pure: compute display info for tracking station / map view.
        /// Returns vessel name, status string, and spawn UT for the chain.
        /// </summary>
        internal static (string name, string status, double spawnUT)
            ComputeGhostDisplayInfo(GhostChain chain, string vesselName)
        {
            string safeName = vesselName ?? "(unnamed)";

            if (chain == null)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "ComputeGhostDisplayInfo: null chain for vessel '{0}' — returning defaults",
                        safeName));
                return (safeName, "Ghost — no chain data", 0);
            }

            if (chain.IsTerminated)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "ComputeGhostDisplayInfo: terminated chain for vessel '{0}' pid={1}",
                        safeName, chain.OriginalVesselPid));
                return (safeName, "Ghost — terminated", chain.SpawnUT);
            }

            if (chain.SpawnBlocked)
            {
                string blockedStatus = string.Format(ic,
                    "Ghost — spawn blocked (since UT={0:F1})",
                    chain.BlockedSinceUT);
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "ComputeGhostDisplayInfo: spawn blocked for vessel '{0}' pid={1} since UT={2:F1}",
                        safeName, chain.OriginalVesselPid, chain.BlockedSinceUT));
                return (safeName, blockedStatus, chain.SpawnUT);
            }

            string activeStatus = string.Format(ic,
                "Ghost — spawns at UT={0:F1}",
                chain.SpawnUT);

            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "ComputeGhostDisplayInfo: active chain for vessel '{0}' pid={1} spawnUT={2:F1} tip={3}",
                    safeName, chain.OriginalVesselPid, chain.SpawnUT,
                    chain.TipRecordingId ?? "(null)"));

            return (safeName, activeStatus, chain.SpawnUT);
        }

        // ------------------------------------------------------------------
        // ProtoVessel lifecycle
        // ------------------------------------------------------------------

        /// <summary>
        /// Create a ghost ProtoVessel for a chain with orbital data.
        /// Gives the ghost tracking station entry, orbit line, and targeting.
        /// Returns the Vessel, or null if no orbit data or creation failed.
        /// </summary>
        internal static Vessel CreateGhostVessel(
            GhostChain chain, IPlaybackTrajectory traj)
        {
            if (chain == null || traj == null)
            {
                ParsekLog.Warn(Tag, "CreateGhostVessel: null chain or trajectory");
                return null;
            }

            if (!HasOrbitData(traj))
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "CreateGhostVessel: no orbit data for chain pid={0} — skipping",
                        chain.OriginalVesselPid));
                return null;
            }

            // Already have a ghost vessel for this chain?
            if (vesselsByChainPid.ContainsKey(chain.OriginalVesselPid))
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "CreateGhostVessel: ghost already exists for chain pid={0}",
                        chain.OriginalVesselPid));
                return vesselsByChainPid[chain.OriginalVesselPid];
            }

            // Intent-line: chain-pid terminal-orbit creation about to fire.
            {
                var intent = NewDecisionFields("create-chain-intent");
                intent.RecordingId = traj.RecordingId;
                intent.RecordingIndex = -1;
                intent.VesselName = traj.VesselName;
                intent.Source = "Chain";
                intent.Branch = "(n/a)";
                intent.Body = traj.TerminalOrbitBody;
                intent.TerminalBody = traj.TerminalOrbitBody;
                intent.TerminalSma = traj.TerminalOrbitSemiMajorAxis;
                intent.TerminalEcc = traj.TerminalOrbitEccentricity;
                intent.Reason = string.Format(ic, "chainPid={0}", chain.OriginalVesselPid);
                ParsekLog.Verbose(Tag, BuildGhostMapDecisionLine(intent));
            }

            string logContext = string.Format(ic, "chain pid={0}", chain.OriginalVesselPid);
            Vessel vessel = BuildAndLoadGhostProtoVessel(traj, logContext);
            if (vessel != null)
            {
                vesselsByChainPid[chain.OriginalVesselPid] = vessel;
                // Complete the pid -> recordingId reverse map for chain (terminal-orbit) ghosts
                // too, mirroring the timeline path's TrackRecordingGhostVessel write. Without this,
                // chain-tip ghost pids had no reverse entry, so recordingId-keyed consumers
                // (MapRenderTrace second-cut window/correlation, polyline-ownership, visibility
                // checks) silently dropped them. Keyed by the LIVE ghost vessel.persistentId (the
                // map world's native key), NOT chain.OriginalVesselPid. Removed in RemoveGhostVessel.
                // No else-remove branch (unlike TrackRecordingGhostVessel) is needed: we only reach
                // here on a fresh create (the vesselsByChainPid.ContainsKey early-return above blocks
                // re-entry), and the live pid is a fresh KSP-unique spawn pid, so no stale entry exists.
                if (!string.IsNullOrEmpty(traj.RecordingId))
                    vesselPidToRecordingId[vessel.persistentId] = traj.RecordingId;
                lifecycleCreatedThisTick++;

                Vector3d worldPos = vessel.GetWorldPos3D();
                string body = vessel.orbitDriver?.referenceBody?.name ?? traj.TerminalOrbitBody;

                var done = NewDecisionFields("create-chain-done");
                done.RecordingId = traj.RecordingId;
                done.RecordingIndex = -1;
                done.VesselName = traj.VesselName;
                done.Source = "Chain";
                done.Branch = "(n/a)";
                done.Body = body;
                done.WorldPos = worldPos;
                done.GhostPid = vessel.persistentId;
                done.TerminalBody = traj.TerminalOrbitBody;
                done.TerminalSma = traj.TerminalOrbitSemiMajorAxis;
                done.TerminalEcc = traj.TerminalOrbitEccentricity;
                done.Reason = string.Format(ic, "chainPid={0}", chain.OriginalVesselPid);
                ParsekLog.Info(Tag, BuildGhostMapDecisionLine(done));

                lastKnownByChainPid[chain.OriginalVesselPid] = new LastKnownGhostFrame
                {
                    RecordingId = traj.RecordingId,
                    VesselName = traj.VesselName,
                    GhostPid = vessel.persistentId,
                    Source = "Chain",
                    Branch = "(n/a)",
                    Body = body,
                    WorldPos = worldPos,
                    AnchorPid = 0u,
                    LastUT = double.NaN
                };
            }

            return vessel;
        }

        /// <summary>
        /// Update orbit when ghost traverses an OrbitSegment boundary.
        /// Changes the ProtoVessel's orbit and reference body if needed.
        /// </summary>
        internal static void UpdateGhostOrbit(uint chainPid, OrbitSegment segment)
        {
            if (!vesselsByChainPid.TryGetValue(chainPid, out Vessel vessel))
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic, "UpdateGhostOrbit: no ghost vessel for chain pid={0}", chainPid));
                return;
            }
            ApplyOrbitToVessel(vessel, segment, string.Format(ic, "chain pid={0}", chainPid));
            lifecycleUpdatedThisTick++;

            Vector3d worldPos = vessel.GetWorldPos3D();
            string updateKey = string.Format(ic, "chain-orbit-update-{0}", chainPid);
            var done = NewDecisionFields("update-chain-segment");
            done.RecordingIndex = -1;
            done.GhostPid = vessel.persistentId;
            done.Source = "Chain";
            done.Branch = "(n/a)";
            done.Body = segment.bodyName;
            done.WorldPos = worldPos;
            done.Segment = segment;
            done.Reason = string.Format(ic, "chainPid={0}", chainPid);
            ParsekLog.VerboseRateLimited(Tag, updateKey, BuildGhostMapDecisionLine(done), 5.0);

            // Keep last-known frame fresh for the destroy log.
            if (lastKnownByChainPid.TryGetValue(chainPid, out var prev))
            {
                prev.Body = segment.bodyName;
                prev.WorldPos = worldPos;
                prev.LastUT = segment.startUT;
                lastKnownByChainPid[chainPid] = prev;
            }
        }

        /// <summary>
        /// Remove a single ghost vessel. Captures target state before Die().
        /// Returns true if the ghost was the current navigation target (caller
        /// should set the newly spawned real vessel as target).
        /// </summary>
        internal static bool RemoveGhostVessel(uint chainPid, string reason)
        {
            if (!vesselsByChainPid.TryGetValue(chainPid, out Vessel vessel))
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "RemoveGhostVessel: no ghost vessel for chain pid={0} reason={1}",
                        chainPid, reason));
                return false;
            }

            // Capture target state BEFORE Die() clears it
            bool wasTarget = FlightGlobals.fetch != null
                && FlightGlobals.fetch.VesselTarget != null
                && FlightGlobals.fetch.VesselTarget.GetVessel() == vessel;

            uint ghostPid = vessel.persistentId;
            bool hadLastKnown = lastKnownByChainPid.TryGetValue(chainPid, out LastKnownGhostFrame last);

            try
            {
                vessel.Die();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "RemoveGhostVessel: Die() threw for chain pid={0}: {1}",
                        chainPid, ex.Message));
            }

            ghostMapVesselPids.Remove(ghostPid);
            ghostsWithSuppressedIcon.Remove(ghostPid);
            ghostNoBoundsSuppressLastFrame.Remove(ghostPid);
            ghostOrbitLineGraceUntilFrame.Remove(ghostPid);
            ghostOrbitBounds.Remove(ghostPid);
            ghostBodyFrameOrbitBounds.Remove(ghostPid);
            ghostLastAppliedOrbitBody.Remove(ghostPid);
            ghostLastAppliedOrbitElements.Remove(ghostPid);
            ghostOrbitLoopShiftedPids.Remove(ghostPid);
            ghostOrbitEpochShift.Remove(ghostPid);
            ghostIconDrivePropagation.Remove(ghostPid);
            ghostParkingConicLineHoldUntilUT.Remove(ghostPid);
            vesselPidToRecordingId.Remove(ghostPid);
            vesselsByChainPid.Remove(chainPid);
            lastKnownByChainPid.Remove(chainPid);
            lifecycleDestroyedThisTick++;

            var destroy = NewDecisionFields("destroy-chain");
            destroy.RecordingId = hadLastKnown ? last.RecordingId : null;
            destroy.RecordingIndex = -1;
            destroy.VesselName = hadLastKnown ? last.VesselName : null;
            destroy.Source = hadLastKnown ? last.Source : "Chain";
            destroy.Branch = hadLastKnown ? last.Branch : "(n/a)";
            destroy.Body = hadLastKnown ? last.Body : null;
            destroy.WorldPos = hadLastKnown ? (Vector3d?)last.WorldPos : null;
            destroy.GhostPid = ghostPid;
            destroy.AnchorPid = hadLastKnown ? last.AnchorPid : 0u;
            destroy.UT = hadLastKnown ? last.LastUT : double.NaN;
            destroy.Reason = string.Format(ic, "{0} chainPid={1} wasTarget={2}",
                reason ?? "(none)", chainPid, wasTarget);
            ParsekLog.Info(Tag, BuildGhostMapDecisionLine(destroy));

            // MapRenderTrace Tier-A: structural GhostDestroyed event, keyed by the
            // live ghost persistentId. Gated at the call site so disabled play pays
            // no formatting cost; reads only last-known fields already snapshotted
            // above (the vessel is dead, so world position comes from lastKnown).
            if (MapRenderTrace.IsEnabled)
            {
                double destroyUT = hadLastKnown ? last.LastUT : CurrentUTNow();
                MapRenderTrace.EmitStructural(
                    "GhostDestroyed",
                    MapRenderTrace.RenderSurface.ProtoIcon,
                    ghostPid.ToString(ic),
                    destroyUT,
                    destroyUT,
                    MapRenderTrace.DestroyWindowSeconds,
                    MapRenderTrace.BuildLifecycleDetails(
                        hadLastKnown ? last.VesselName : null,
                        hadLastKnown ? last.Body : null,
                        HighLogic.LoadedScene.ToString(),
                        hadLastKnown ? (Vector3d?)last.WorldPos : null,
                        // Carry the recordingId for pid<->recordingId correlation (see the index-keyed destroy).
                        string.Format(ic, "{0} chainPid={1} rec={2}", reason ?? "(none)", chainPid,
                            hadLastKnown ? (last.RecordingId ?? "<none>") : "<none>")));
            }

            return wasTarget;
        }

        internal static bool ShouldTransferTrackingStationNavigationTarget(
            uint ghostPid,
            uint currentTargetPid)
        {
            return ghostPid != 0
                && currentTargetPid != 0
                && ghostPid == currentTargetPid;
        }

        internal static bool ShouldTransferTrackingStationMapFocus(
            bool mapViewEnabled,
            bool hasGhostMapObject,
            bool mapCameraAlreadyFocusedGhost)
        {
            return mapViewEnabled
                && hasGhostMapObject
                && mapCameraAlreadyFocusedGhost;
        }

        internal static bool IsTrackingStationMapFocusSceneActive(
            bool mapViewEnabled,
            bool isTrackingStationScene)
        {
            return mapViewEnabled || isTrackingStationScene;
        }

        /// <summary>
        /// Set a ghost as KSP's navigation target and log success only after
        /// stock target validation has had frames to reject invalid targetables.
        /// <paramref name="recordingIndex"/> is diagnostic-only; callers may pass
        /// -1 when a selection path cannot resolve the recording index.
        /// </summary>
        internal static void SetGhostMapNavigationTarget(
            Vessel vessel,
            int recordingIndex,
            string source)
        {
            string safeSource = string.IsNullOrEmpty(source) ? "unknown" : source;
            string vesselName = vessel != null ? (vessel.vesselName ?? "Ghost") : "Ghost";
            if (vessel == null)
            {
                LogGhostTargetVerificationOutcome(
                    vesselName,
                    recordingIndex,
                    safeSource,
                    GhostTargetVerificationStatus.WrongObject,
                    "ghost-vessel-null",
                    "target-request-vessel=null",
                    "(not-captured)",
                    "(not-captured)",
                    "preflight");
                return;
            }

            if (FlightGlobals.fetch == null)
            {
                LogGhostTargetVerificationOutcome(
                    vesselName,
                    recordingIndex,
                    safeSource,
                    GhostTargetVerificationStatus.MissingFlightGlobals,
                    "FlightGlobals.fetch=null",
                    CaptureGhostTargetState(vessel, "missing-flightglobals"),
                    "(not-captured)",
                    "(not-captured)",
                    "preflight");
                return;
            }

            NormalizeGhostOrbitDriverTargetIdentity(vessel, safeSource);
            long requestId;
            unchecked
            {
                requestId = ++ghostTargetRequestSequence;
            }

            string before = CaptureGhostTargetState(vessel, "before");
            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "Ghost target request via {0}: requestId={1} recIndex={2} ghost='{3}' before=[{4}]",
                    safeSource,
                    requestId,
                    recordingIndex,
                    vesselName,
                    before));

            FlightGlobals.fetch.SetVesselTarget(vessel, overrideInputLock: true);

            string immediate = CaptureGhostTargetState(vessel, "after-set");
            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "Ghost target request via {0}: requestId={1} recIndex={2} ghost='{3}' afterSet=[{4}]",
                    safeSource,
                    requestId,
                    recordingIndex,
                    vesselName,
                    immediate));

            ParsekScenario host = ParsekScenario.Instance;
            if (host != null)
            {
                host.StartCoroutine(VerifyGhostMapNavigationTargetAfterKspValidation(
                    vessel,
                    recordingIndex,
                    safeSource,
                    vesselName,
                    before,
                    immediate,
                    requestId));
                return;
            }

            LogGhostTargetVerificationOutcome(
                vesselName,
                recordingIndex,
                safeSource,
                GhostTargetVerificationStatus.VerificationUnavailable,
                "ParsekScenario.Instance=null; post-validation target state cannot be observed",
                CaptureGhostTargetState(vessel, "unverified-no-coroutine"),
                before,
                immediate,
                "unverified-no-coroutine");
        }

        private static IEnumerator VerifyGhostMapNavigationTargetAfterKspValidation(
            Vessel vessel,
            int recordingIndex,
            string source,
            string vesselName,
            string before,
            string immediate,
            long requestId)
        {
            yield return null;
            yield return null;

            if (requestId != ghostTargetRequestSequence)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "Ghost target verification superseded via {0}: requestId={1} latestRequestId={2} recIndex={3} ghost='{4}'",
                        source ?? "unknown",
                        requestId,
                        ghostTargetRequestSequence,
                        recordingIndex,
                        vesselName ?? "Ghost"));
                yield break;
            }

            GhostTargetVerificationStatus status = EvaluateGhostTargetVerification(vessel, out string reason);
            string final = CaptureGhostTargetState(vessel, "after-ksp-validation");
            LogGhostTargetVerificationOutcome(
                vesselName,
                recordingIndex,
                source,
                status,
                reason,
                final,
                before,
                immediate,
                "after-ksp-validation");
        }

        internal static bool LogGhostTargetVerificationForTesting(
            string vesselName,
            int recordingIndex,
            string source,
            GhostTargetVerificationStatus status,
            string reason,
            string finalState)
        {
            return LogGhostTargetVerificationOutcome(
                vesselName,
                recordingIndex,
                source,
                status,
                reason,
                finalState,
                "(test-before)",
                "(test-immediate)",
                "test");
        }

        private static bool LogGhostTargetVerificationOutcome(
            string vesselName,
            int recordingIndex,
            string source,
            GhostTargetVerificationStatus status,
            string reason,
            string finalState,
            string beforeState,
            string immediateState,
            string phase)
        {
            string safeName = string.IsNullOrEmpty(vesselName) ? "Ghost" : vesselName;
            string safeSource = string.IsNullOrEmpty(source) ? "unknown" : source;
            string safeReason = string.IsNullOrEmpty(reason) ? "(none)" : reason;
            string safeFinal = string.IsNullOrEmpty(finalState) ? "(none)" : finalState;

            if (status == GhostTargetVerificationStatus.Accepted)
            {
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Ghost '{0}' set as target via {1} (verified {2}; recIndex={3}; final=[{4}])",
                        safeName,
                        safeSource,
                        phase ?? "(unknown)",
                        recordingIndex,
                        safeFinal));
                return true;
            }

            if (status == GhostTargetVerificationStatus.VerificationUnavailable)
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "Ghost '{0}' target verification unavailable via {1}: status={2} reason={3} recIndex={4} phase={5} before=[{6}] immediate=[{7}] final=[{8}]",
                        safeName,
                        safeSource,
                        status,
                        safeReason,
                        recordingIndex,
                        phase ?? "(unknown)",
                        string.IsNullOrEmpty(beforeState) ? "(none)" : beforeState,
                        string.IsNullOrEmpty(immediateState) ? "(none)" : immediateState,
                        safeFinal));
                return false;
            }

            ParsekLog.Warn(Tag,
                string.Format(ic,
                    "Ghost '{0}' target rejected via {1}: status={2} reason={3} recIndex={4} phase={5} before=[{6}] immediate=[{7}] final=[{8}]",
                    safeName,
                    safeSource,
                    status,
                    safeReason,
                    recordingIndex,
                    phase ?? "(unknown)",
                    string.IsNullOrEmpty(beforeState) ? "(none)" : beforeState,
                    string.IsNullOrEmpty(immediateState) ? "(none)" : immediateState,
                    safeFinal));
            return false;
        }

        private static GhostTargetVerificationStatus EvaluateGhostTargetVerification(
            Vessel ghost,
            out string reason)
        {
            reason = null;
            if (FlightGlobals.fetch == null)
            {
                reason = "FlightGlobals.fetch=null";
                return GhostTargetVerificationStatus.MissingFlightGlobals;
            }

            ITargetable target = FlightGlobals.fetch.VesselTarget;
            if (target == null)
            {
                reason = "FlightGlobals.fetch.VesselTarget=null";
                return GhostTargetVerificationStatus.NullTarget;
            }

            Vessel targetVessel = SafeGetTargetVessel(target);
            if (targetVessel != null)
            {
                if (IsSameVessel(targetVessel, ghost))
                {
                    reason = "target-vessel-matches-ghost";
                    return GhostTargetVerificationStatus.Accepted;
                }

                reason = string.Format(ic,
                    "target-vessel-mismatch ghostPid={0} targetPid={1} targetName=\"{2}\"",
                    ghost != null ? ghost.persistentId : 0u,
                    targetVessel.persistentId,
                    targetVessel.vesselName ?? "(null)");
                return GhostTargetVerificationStatus.WrongVessel;
            }

            OrbitDriver targetDriver = SafeGetTargetOrbitDriver(target);
            CelestialBody targetBody = targetDriver != null ? targetDriver.celestialBody : null;
            CelestialBody currentBody = ResolveCurrentMainBody();
            if (targetBody != null)
            {
                if (IsSameBody(targetBody, currentBody))
                {
                    reason = string.Format(ic,
                        "target-driver-celestialBody-is-current-main-body body={0}",
                        targetBody.name ?? "(null)");
                    return GhostTargetVerificationStatus.CurrentMainBody;
                }

                if (currentBody != null && currentBody.HasParent(targetBody))
                {
                    reason = string.Format(ic,
                        "target-driver-celestialBody-is-parent-body targetBody={0} currentBody={1}",
                        targetBody.name ?? "(null)",
                        currentBody.name ?? "(null)");
                    return GhostTargetVerificationStatus.ParentBody;
                }
            }

            reason = "target-is-not-the-ghost-vessel: " + DescribeTargetable(target);
            return GhostTargetVerificationStatus.WrongObject;
        }

        private static string CaptureGhostTargetState(Vessel ghost, string phase)
        {
            FlightGlobals globals = FlightGlobals.fetch;
            Vessel active = globals != null ? FlightGlobals.ActiveVessel : null;
            CelestialBody currentBody = ResolveCurrentMainBody();
            string mode = globals != null
                ? globals.vesselTargetMode.ToString()
                : "(no-flightglobals)";

            return string.Format(ic,
                "phase={0} mode={1} currentMainBody={2} active={3} target={4} activeTargetObject={5} ghost={6} ghostRegistered={7}",
                phase ?? "(null)",
                mode,
                currentBody != null ? (currentBody.name ?? "(unnamed-body)") : "(null)",
                DescribeVessel(active),
                globals != null ? DescribeTargetable(globals.VesselTarget) : "(no-flightglobals)",
                active != null ? DescribeTargetable(active.targetObject) : "(no-active-vessel)",
                DescribeVessel(ghost) + " " + BuildGhostOrbitDriverIdentity(ghost),
                IsVesselRegistered(ghost));
        }

        internal static void NormalizeGhostOrbitDriverTargetIdentity(
            Vessel vessel,
            string context)
        {
            if (vessel == null || vessel.orbitDriver == null)
                return;

            OrbitDriver driver = vessel.orbitDriver;
            bool changed = false;
            string before = BuildGhostOrbitDriverIdentity(vessel);

            if (!IsSameVessel(driver.vessel, vessel))
            {
                driver.vessel = vessel;
                changed = true;
            }

            if (driver.celestialBody != null)
            {
                driver.celestialBody = null;
                changed = true;
            }

            if (changed)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "Normalized ghost target identity for {0}: before=[{1}] after=[{2}]",
                        string.IsNullOrEmpty(context) ? "(none)" : context,
                        before,
                        BuildGhostOrbitDriverIdentity(vessel)));
            }
        }

        internal static string BuildGhostOrbitDriverIdentity(Vessel vessel)
        {
            if (vessel == null)
                return "driver=(ghost-null)";
            OrbitDriver driver = vessel.orbitDriver;
            if (driver == null)
                return "driver=(null)";

            string driverVessel = driver.vessel != null
                ? string.Format(ic,
                    "{0}/pid={1}",
                    driver.vessel.vesselName ?? "(null)",
                    driver.vessel.persistentId)
                : "(null)";
            string driverBody = driver.celestialBody != null
                ? (driver.celestialBody.name ?? "(unnamed-body)")
                : "(null)";
            string referenceBody = driver.referenceBody != null
                ? (driver.referenceBody.name ?? "(unnamed-body)")
                : "(null)";

            return string.Format(ic,
                "driverVessel={0} driverCelestialBody={1} referenceBody={2} orbit={3} mapObj={4} orbitRenderer={5}",
                driverVessel,
                driverBody,
                referenceBody,
                driver.orbit != null,
                vessel.mapObject != null,
                vessel.orbitRenderer != null);
        }

        private static string DescribeTargetable(ITargetable target)
        {
            if (target == null)
                return "null";

            string typeName = target.GetType().Name;
            Vessel vessel = SafeGetTargetVessel(target);
            OrbitDriver driver = SafeGetTargetOrbitDriver(target);
            string name = SafeGetTargetName(target);
            string vesselText = vessel != null
                ? DescribeVessel(vessel)
                : "(null)";
            string driverVessel = driver != null && driver.vessel != null
                ? DescribeVessel(driver.vessel)
                : "(null)";
            string driverBody = driver != null && driver.celestialBody != null
                ? (driver.celestialBody.name ?? "(unnamed-body)")
                : "(null)";
            string referenceBody = driver != null && driver.referenceBody != null
                ? (driver.referenceBody.name ?? "(unnamed-body)")
                : "(null)";

            return string.Format(ic,
                "type={0} name=\"{1}\" vessel={2} driverVessel={3} driverCelestialBody={4} referenceBody={5} transform={6}",
                typeName,
                name ?? "(null)",
                vesselText,
                driverVessel,
                driverBody,
                referenceBody,
                SafeHasTransform(target));
        }

        private static string DescribeVessel(Vessel vessel)
        {
            if (vessel == null)
                return "null";
            return string.Format(ic,
                "\"{0}\"/pid={1}/ghost={2}",
                vessel.vesselName ?? "(null)",
                vessel.persistentId,
                IsGhostMapVessel(vessel.persistentId));
        }

        private static Vessel SafeGetTargetVessel(ITargetable target)
        {
            if (target == null) return null;
            try { return target.GetVessel(); }
            catch { return null; }
        }

        private static OrbitDriver SafeGetTargetOrbitDriver(ITargetable target)
        {
            if (target == null) return null;
            try { return target.GetOrbitDriver(); }
            catch { return null; }
        }

        private static string SafeGetTargetName(ITargetable target)
        {
            if (target == null) return null;
            try { return target.GetName(); }
            catch { return null; }
        }

        private static bool SafeHasTransform(ITargetable target)
        {
            if (target == null) return false;
            try { return target.GetTransform() != null; }
            catch { return false; }
        }

        private static CelestialBody ResolveCurrentMainBody()
        {
            if (FlightGlobals.currentMainBody != null)
                return FlightGlobals.currentMainBody;
            if (FlightGlobals.fetch == null)
                return null;
            Vessel active = FlightGlobals.ActiveVessel;
            if (active != null && active.mainBody != null)
                return active.mainBody;
            return null;
        }

        internal static bool IsVesselRegistered(Vessel vessel)
        {
            if (vessel == null || FlightGlobals.fetch == null)
                return false;

            // O(1) persistent-id lookup against FlightGlobals.PersistentVesselIds
            // (stock FlightGlobals.FindVessel is a thin wrapper around it).
            if (vessel.persistentId != 0u)
                return FlightGlobals.FindVessel(vessel.persistentId, out _);

            // pid==0 vessels (unregistered / mid-construction) fall back to an
            // identity scan so callers still get a meaningful answer.
            if (FlightGlobals.Vessels == null)
                return false;
            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                if (ReferenceEquals(FlightGlobals.Vessels[i], vessel))
                    return true;
            }
            return false;
        }

        private static bool IsSameVessel(Vessel a, Vessel b)
        {
            if (a == null || b == null)
                return false;
            return ReferenceEquals(a, b)
                || (a.persistentId != 0u && a.persistentId == b.persistentId);
        }

        private static bool IsSameBody(CelestialBody a, CelestialBody b)
        {
            if (a == null || b == null)
                return false;
            return ReferenceEquals(a, b)
                || string.Equals(a.name, b.name, StringComparison.Ordinal);
        }

        private static TrackingStationSpawnHandoffState CaptureTrackingStationSpawnHandoffState(
            int recordingIndex)
        {
            if (!vesselsByRecordingIndex.TryGetValue(recordingIndex, out Vessel ghostVessel)
                || ghostVessel == null)
            {
                return default(TrackingStationSpawnHandoffState);
            }

            Vessel currentTargetVessel = FlightGlobals.fetch != null
                && FlightGlobals.fetch.VesselTarget != null
                    ? FlightGlobals.fetch.VesselTarget.GetVessel()
                    : null;

            bool wasNavigationTarget = ShouldTransferTrackingStationNavigationTarget(
                ghostVessel.persistentId,
                currentTargetVessel != null ? currentTargetVessel.persistentId : 0u);
            bool wasMapFocus = ShouldTransferTrackingStationMapFocus(
                IsTrackingStationMapFocusSceneActive(
                    MapView.MapIsEnabled,
                    HighLogic.LoadedScene == GameScenes.TRACKSTATION),
                ghostVessel.mapObject != null,
                PlanetariumCamera.fetch != null
                    && ReferenceEquals(PlanetariumCamera.fetch.target, ghostVessel.mapObject));

            return new TrackingStationSpawnHandoffState(
                ghostVessel.persistentId,
                wasNavigationTarget,
                wasMapFocus);
        }

        private static void RestoreTrackingStationSpawnHandoffState(
            uint spawnedPid,
            TrackingStationSpawnHandoffState handoffState,
            string reason,
            bool reselectSpawnedVessel)
        {
            if (spawnedPid == 0
                || (!reselectSpawnedVessel
                    && !handoffState.WasNavigationTarget
                    && !handoffState.WasMapFocus))
            {
                return;
            }

            Vessel spawned = FlightRecorder.FindVesselByPid(spawnedPid);
            if (spawned == null)
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "Tracking-station handoff could not restore focus/target for ghostPid={0} spawnedPid={1} reason={2} (spawned vessel not found)",
                        handoffState.GhostPid,
                        spawnedPid,
                        reason));
                return;
            }

            if (reselectSpawnedVessel)
                RestoreTrackingStationSelectedVessel(spawned, reason);

            if (handoffState.WasNavigationTarget && FlightGlobals.fetch != null)
            {
                FlightGlobals.fetch.SetVesselTarget(spawned);
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Tracking-station handoff restored nav target from ghostPid={0} to spawnedPid={1} reason={2}",
                        handoffState.GhostPid,
                        spawnedPid,
                        reason));
            }

            if (!handoffState.WasMapFocus)
                return;

            if (IsTrackingStationMapFocusSceneActive(
                    MapView.MapIsEnabled,
                    HighLogic.LoadedScene == GameScenes.TRACKSTATION)
                && PlanetariumCamera.fetch != null
                && spawned.mapObject != null)
            {
                if (TrySetReadyMapObjectTarget(
                        () => spawned.mapObject.GetName(),
                        () => PlanetariumCamera.fetch.SetTarget(spawned.mapObject),
                        out string mapObjectName,
                        out string mapObjectError))
                {
                    ParsekLog.Info(Tag,
                        string.Format(ic,
                            "Tracking-station handoff restored map focus from ghostPid={0} to spawnedPid={1} mapObject='{2}' reason={3}",
                            handoffState.GhostPid,
                            spawnedPid,
                            mapObjectName ?? "(null)",
                            reason));
                    return;
                }

                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "Tracking-station handoff could not restore map focus for ghostPid={0} spawnedPid={1} reason={2}: {3}",
                        handoffState.GhostPid,
                        spawnedPid,
                        reason,
                        mapObjectError ?? "map object probe failed"));
                return;
            }

            ParsekLog.Warn(Tag,
                string.Format(ic,
                    "Tracking-station handoff could not restore map focus for ghostPid={0} spawnedPid={1} reason={2} mapView={3} camera={4} mapObject={5}",
                    handoffState.GhostPid,
                    spawnedPid,
                    reason,
                    MapView.MapIsEnabled,
                    PlanetariumCamera.fetch != null,
                    spawned.mapObject != null));
        }

        internal static bool TrySetReadyMapObjectTarget(
            Func<string> getName,
            Action setTarget,
            out string name,
            out string error)
        {
            if (!TryProbeMapObjectName(getName, out name, out error))
                return false;

            if (setTarget == null)
            {
                error = "setTarget-null";
                return false;
            }

            try
            {
                setTarget();
                return true;
            }
            catch (Exception ex)
            {
                error = string.Format(ic,
                    "SetTarget threw {0}: {1}",
                    ex.GetType().Name,
                    ex.Message);
                return false;
            }
        }

        internal static bool TryProbeMapObjectName(
            Func<string> getName,
            out string name,
            out string error)
        {
            name = null;
            error = null;

            if (getName == null)
            {
                error = "getName-null";
                return false;
            }

            try
            {
                name = getName();
                return true;
            }
            catch (Exception ex)
            {
                error = string.Format(ic,
                    "GetName threw {0}: {1}",
                    ex.GetType().Name,
                    ex.Message);
                return false;
            }
        }

        internal static bool TrySelectTrackingStationVessel(
            object trackingInstance,
            object vesselSelection,
            out string error)
        {
            error = null;

            if (trackingInstance == null || vesselSelection == null)
                return false;

            try
            {
                MethodInfo setVesselMethod = FindTrackingStationSetVesselMethod(
                    trackingInstance.GetType(),
                    vesselSelection.GetType());
                if (setVesselMethod != null)
                {
                    object[] args = BuildTrackingStationSetVesselArguments(
                        setVesselMethod,
                        vesselSelection,
                        keepFocus: false);
                    setVesselMethod.Invoke(trackingInstance, args);
                    return true;
                }

                FieldInfo selectedField = trackingInstance.GetType().GetField(
                    "selectedVessel",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (selectedField == null)
                {
                    error = "selectedVessel field and SetVessel method not found";
                    return false;
                }

                selectedField.SetValue(trackingInstance, vesselSelection);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        internal static object[] BuildTrackingStationSetVesselArguments(
            MethodInfo setVesselMethod,
            object vesselSelection,
            bool keepFocus)
        {
            if (setVesselMethod == null)
                return null;

            ParameterInfo[] parameters = setVesselMethod.GetParameters();
            if (parameters.Length == 2
                && parameters[1].ParameterType == typeof(bool))
            {
                return new[] { vesselSelection, (object)keepFocus };
            }

            return new[] { vesselSelection };
        }

        internal static bool TryRefreshLiveTrackingStationVesselList(string reason)
        {
            if (!IsTrackingStationSceneForVesselListRefresh())
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "Tracking Station vessel list refresh skipped: reason={0} scene={1}",
                        string.IsNullOrEmpty(reason) ? "(none)" : reason,
                        GetCurrentSceneName()));
                return false;
            }

            SpaceTracking tracking = UnityEngine.Object.FindObjectOfType<SpaceTracking>();
            return TryInvokeTrackingStationVesselListRefresh(
                tracking,
                reason,
                out _);
        }

        private static bool IsTrackingStationSceneForVesselListRefresh()
        {
            try
            {
                return HighLogic.LoadedScene == GameScenes.TRACKSTATION;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryInvokeTrackingStationVesselListRefresh(
            object trackingInstance,
            string reason,
            out string error)
        {
            error = null;
            string safeReason = string.IsNullOrEmpty(reason) ? "(none)" : reason;

            if (trackingInstance == null)
            {
                error = "SpaceTracking instance not found";
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "Tracking Station vessel list refresh failed: reason={0} error={1}",
                        safeReason,
                        error));
                return false;
            }

            try
            {
                MethodInfo buildMethod = FindTrackingStationNoArgMethod(
                    trackingInstance.GetType(),
                    "buildVesselsList");
                if (buildMethod == null)
                {
                    error = "buildVesselsList method not found";
                    ParsekLog.Warn(Tag,
                        string.Format(ic,
                            "Tracking Station vessel list refresh failed: reason={0} error={1}",
                            safeReason,
                            error));
                    return false;
                }

                buildMethod.Invoke(trackingInstance, null);
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Tracking Station vessel list refreshed: reason={0}",
                        safeReason));
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                error = string.Format(ic,
                    "buildVesselsList threw {0}: {1}",
                    inner.GetType().Name,
                    inner.Message);
            }
            catch (Exception ex)
            {
                error = string.Format(ic,
                    "buildVesselsList reflection failed {0}: {1}",
                    ex.GetType().Name,
                    ex.Message);
            }

            ParsekLog.Warn(Tag,
                string.Format(ic,
                    "Tracking Station vessel list refresh failed: reason={0} error={1}",
                    safeReason,
                    error ?? "(none)"));
            return false;
        }

        private static MethodInfo FindTrackingStationNoArgMethod(
            Type trackingType,
            string methodName)
        {
            if (trackingType == null || string.IsNullOrEmpty(methodName))
                return null;

            MethodInfo[] methods = trackingType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method == null || method.Name != methodName)
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 0)
                    return method;
            }

            return null;
        }

        internal static bool IsTrackingStationRecordingAlreadyMaterialized(Recording rec)
        {
            if (rec == null)
                return false;

            bool realVesselExists = false;
            if (rec.VesselPersistentId != 0)
            {
                try
                {
                    realVesselExists = GhostPlaybackLogic.RealVesselExistsForRecording(rec);
                }
                catch (Exception)
                {
                    realVesselExists = false;
                }
            }

            return ShouldSkipTrackingStationDuplicateSpawn(rec, realVesselExists);
        }

        internal static MethodInfo FindTrackingStationSetVesselMethod(
            Type trackingType,
            Type selectionType)
        {
            if (trackingType == null || selectionType == null)
                return null;

            MethodInfo[] methods = trackingType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo oneArgumentFallback = null;
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method == null || method.Name != "SetVessel")
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (!IsTrackingStationSetVesselSignature(parameters, selectionType))
                    continue;

                if (parameters.Length == 2)
                    return method;
                if (oneArgumentFallback == null)
                    oneArgumentFallback = method;
            }

            return oneArgumentFallback;
        }

        private static bool IsTrackingStationSetVesselSignature(
            ParameterInfo[] parameters,
            Type selectionType)
        {
            if (parameters == null || selectionType == null)
                return false;
            if (parameters.Length != 1 && parameters.Length != 2)
                return false;
            if (!parameters[0].ParameterType.IsAssignableFrom(selectionType))
                return false;

            return parameters.Length == 1
                || parameters[1].ParameterType == typeof(bool);
        }

        private static void RestoreTrackingStationSelectedVessel(
            Vessel spawned,
            string reason)
        {
            if (spawned == null)
                return;

            SpaceTracking tracking = UnityEngine.Object.FindObjectOfType<SpaceTracking>();
            if (tracking == null)
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "Tracking-station handoff could not restore selected vessel for spawnedPid={0} reason={1} (SpaceTracking instance not found)",
                        spawned.persistentId,
                        reason));
                return;
            }

            if (TrySelectTrackingStationVessel(tracking, spawned, out string error))
            {
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Tracking-station handoff restored selected vessel to spawnedPid={0} reason={1}",
                        spawned.persistentId,
                        reason));
                return;
            }

            if (!string.IsNullOrEmpty(error))
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "Tracking-station handoff could not restore selected vessel for spawnedPid={0} reason={1}: {2}",
                        spawned.persistentId,
                        reason,
                        error));
            }
        }

        /// <summary>
        /// Remove all ghost vessels (rewind or scene cleanup).
        /// </summary>
        internal static void RemoveAllGhostVessels(string reason)
        {
            int chainCount = vesselsByChainPid.Count;
            int indexCount = vesselsByRecordingIndex.Count;
            int overlapInstanceCount = overlapInstanceVessels.Count;
            if (chainCount == 0 && indexCount == 0 && overlapInstanceCount == 0)
            {
                ParsekLog.VerboseRateLimited(Tag,
                    "remove-all-empty|" + (reason ?? "(none)"),
                    string.Format(ic,
                        "RemoveAllGhostVessels: no ghost vessels to remove (reason={0})",
                        reason),
                    30.0);
                return;
            }

            // Collect all vessels to destroy (chain + recording index + overlap instances)
            var vessels = new List<Vessel>(chainCount + indexCount + overlapInstanceCount);
            vessels.AddRange(vesselsByChainPid.Values);
            vessels.AddRange(vesselsByRecordingIndex.Values);
            vessels.AddRange(overlapInstanceVessels.Values);

            foreach (var vessel in vessels)
            {
                try
                {
                    vessel.Die();
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag,
                        string.Format(ic,
                            "RemoveAllGhostVessels: Die() threw for '{0}': {1}",
                            vessel.vesselName, ex.Message));
                }
            }

            ghostMapVesselPids.Clear();
            ghostsWithSuppressedIcon.Clear();
            ghostOrbitLineGraceUntilFrame.Clear();
            ghostNoBoundsSuppressLastFrame.Clear();
            ghostOrbitBounds.Clear();
            ghostBodyFrameOrbitBounds.Clear();
            ghostLastAppliedOrbitBody.Clear();
            ghostLastAppliedOrbitElements.Clear();
            ghostOrbitLoopShiftedPids.Clear();
            ghostOrbitEpochShift.Clear();
            ghostIconDrivePropagation.Clear();
            vesselsByChainPid.Clear();
            vesselsByRecordingIndex.Clear();
            overlapInstanceVessels.Clear();
            boundaryOverlapSecondaryPids.Clear(); // M1
            vesselPidToRecordingIndex.Clear();
            vesselPidToRecordingId.Clear();
            ghostParkingConicLineHoldUntilUT.Clear();
            trackingStationStateVectorOrbitTrajectories.Clear();
            trackingStationStateVectorCachedIndices.Clear();
            activeReFlyDeferredStateVectorGhostSessions.Clear();
            lastKnownByRecordingIndex.Clear();
            lastKnownByChainPid.Clear();

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Removed all {0} ghost vessel(s) reason={1} (chain={2} index={3} overlapInstances={4})",
                    chainCount + indexCount + overlapInstanceCount, reason,
                    chainCount, indexCount, overlapInstanceCount));
        }

        // ------------------------------------------------------------------
        // Tracking Station "Fly" ghost-index-drift fix (BUG #1)
        // ------------------------------------------------------------------

        // Reason string handed to RemoveAllGhostVessels on the pre-stock TS Fly
        // strip so the GhostMap summary line ties the removal to this code path.
        internal const string TsFlyBeforeStockIndexofReason = "ts-fly-before-stock-indexof";

        // ComputeFlyIndexDrift sentinel: the Fly target pid was not present in the
        // live FlightGlobals.Vessels list when the strip ran (defensive, e.g. another
        // mod removed it). Logged distinctly via a Warn; removal still proceeds.
        internal const int FlyIndexDriftTargetNotInLiveList = -1;

        /// <summary>
        /// PURE, Unity-free, unit-testable. Returns the index drift the player would
        /// have suffered on a Tracking Station "Fly" if the ghost map vessels were
        /// stripped from the saved persistent.sfs (current StripFromSave behaviour)
        /// but NOT from the live FlightGlobals.Vessels list — i.e. the off-by amount
        /// stock FlightDriver.StartAndFocusVessel(file, IndexOf(v)) would land wrong by.
        ///
        /// Stock SpaceTracking.FlyVessel identifies the target purely by its index in
        /// the live FlightGlobals.Vessels list, then focuses the vessel at that same
        /// index in the (ghost-free) loaded file. The drift is exactly the number of
        /// ghost pids that occupy a live index strictly LESS than the target's live
        /// index, because each such ghost shifts the target one slot earlier in the
        /// ghost-free file.
        ///
        /// Contract:
        ///   - target not present in liveVesselPids -> FlyIndexDriftTargetNotInLiveList (-1).
        ///   - target present at live index t -> count of i in [0, t) where
        ///     liveVesselPids[i] is in ghostPids.
        ///   - drift == 0: the Fly would have landed correctly even without the fix.
        ///   - drift > 0: the bug magnitude the strip prevents.
        /// </summary>
        internal static int ComputeFlyIndexDrift(
            IReadOnlyList<uint> liveVesselPids,
            ISet<uint> ghostPids,
            uint targetPid)
        {
            if (liveVesselPids == null)
                return FlyIndexDriftTargetNotInLiveList;

            int targetIndex = -1;
            for (int i = 0; i < liveVesselPids.Count; i++)
            {
                if (liveVesselPids[i] == targetPid)
                {
                    targetIndex = i;
                    break;
                }
            }

            if (targetIndex < 0)
                return FlyIndexDriftTargetNotInLiveList;

            if (ghostPids == null || ghostPids.Count == 0)
                return 0;

            int drift = 0;
            for (int i = 0; i < targetIndex; i++)
            {
                if (ghostPids.Contains(liveVesselPids[i]))
                    drift++;
            }

            return drift;
        }

        /// <summary>
        /// PURE log-line formatter for the pre-stock TS Fly ghost strip diagnostic.
        /// Returned string is the message body (subsystem tag added by the caller via
        /// ParsekLog.Info). Kept separate from the Unity-touching removal so the
        /// diagnostic numbers can be asserted in xUnit without a live FlightGlobals.
        /// </summary>
        internal static string FormatFlyStripDiagnostic(
            uint targetPid,
            int ghostCount,
            int liveVesselsBefore,
            int flyIndexDriftAvoided)
        {
            return string.Format(ic,
                "TS Fly pre-stock ghost strip: targetPid={0} ghostCount={1} " +
                "liveVesselsBefore={2} flyIndexDriftAvoided={3} - removed all ghost map " +
                "vessels before stock IndexOf/SaveGame so the live list and persistent.sfs " +
                "are ghost-consistent",
                targetPid,
                ghostCount,
                liveVesselsBefore,
                flyIndexDriftAvoided);
        }

        /// <summary>
        /// Tracking Station "Fly" root-cause fix (BUG #1). Removes all ghost map
        /// vessels from the live FlightGlobals.Vessels list BEFORE stock
        /// SpaceTracking.FlyVessel computes IndexOf(v) + SaveGame, so the live list and
        /// the persistent.sfs StripFromSave produces are both ghost-free and stock
        /// StartAndFocusVessel lands on the intended vessel the first time.
        ///
        /// Called only for a real (non-ghost) Fly target by
        /// SwitchIntentTrackingStationFlyPatch (the ghost-block path keeps ghosts
        /// alive — the player stays in the Tracking Station). Removal itself is always
        /// safe: it is what ParsekTrackingStation.OnDestroy -> RemoveAllGhostVessels
        /// does a moment later; on a real Fly the scene is transitioning out, so the
        /// icons vanishing a few frames early is invisible.
        ///
        /// Captures the live pid list and the would-be index drift via the pure
        /// ComputeFlyIndexDrift BEFORE removal so the logged number reflects the bug
        /// the strip just prevented.
        /// </summary>
        internal static void RemoveAllGhostVesselsBeforeStockFly(uint targetPid)
        {
            int ghostCount = ghostMapVesselPids.Count;

            // Build the live pid list + target drift from the still-polluted list
            // captured BEFORE RemoveAllGhostVessels, so the logged drift is the
            // off-by amount the strip avoided (post-strip the list is ghost-free).
            var livePids = new List<uint>();
            var liveVessels = FlightGlobals.Vessels;
            if (liveVessels != null)
            {
                for (int i = 0; i < liveVessels.Count; i++)
                {
                    Vessel vessel = liveVessels[i];
                    if (vessel != null)
                        livePids.Add(vessel.persistentId);
                }
            }

            if (ghostCount == 0)
            {
                // Common no-ghost Fly: nothing to strip, no index drift possible.
                // Single Verbose line so the case is observable without spam.
                ParsekLog.Verbose("SwitchIntentPatch",
                    string.Format(ic,
                        "TS Fly pre-stock ghost strip: no ghost map vessels to strip " +
                        "targetPid={0} liveVessels={1} - stock IndexOf/SaveGame already ghost-consistent",
                        targetPid,
                        livePids.Count));
                return;
            }

            int drift = ComputeFlyIndexDrift(livePids, ghostMapVesselPids, targetPid);
            if (drift == FlyIndexDriftTargetNotInLiveList)
            {
                // Defensive: another mod removed the target from the live list before
                // the strip ran. Removal is still safe and is what TS-cleanup does,
                // so proceed; the warn flags that the drift could not be measured.
                ParsekLog.Warn("SwitchIntentPatch",
                    string.Format(ic,
                        "TS Fly pre-stock ghost strip: targetPid={0} not found in live " +
                        "FlightGlobals.Vessels (ghostCount={1} liveVessels={2}) - drift " +
                        "unmeasurable; proceeding with ghost removal anyway (always safe)",
                        targetPid,
                        ghostCount,
                        livePids.Count));
            }
            else
            {
                ParsekLog.Info("SwitchIntentPatch",
                    FormatFlyStripDiagnostic(targetPid, ghostCount, livePids.Count, drift));
            }

            RemoveAllGhostVessels(TsFlyBeforeStockIndexofReason);
        }

        // ------------------------------------------------------------------
        // Recording-index-based ghost map (for timeline playback ghosts)
        // ------------------------------------------------------------------

        /// <summary>
        /// Create a ghost map ProtoVessel for a timeline playback ghost.
        /// Called when the engine spawns a ghost (OnGhostCreated).
        /// </summary>
        internal static Vessel CreateGhostVesselForRecording(int recordingIndex, IPlaybackTrajectory traj)
        {
            if (traj == null || !HasOrbitData(traj))
                return null;

            // Phase 7 of Rewind-to-Staging (design §3.3): during an active re-fly
            // session, suppressed recordings must NOT own map presence. Destroy
            // any leftover entry from before the session started and skip.
            if (IsSuppressedByActiveSession(recordingIndex))
            {
                RemoveGhostVesselForRecording(recordingIndex, "session-suppressed subtree");
                return null;
            }

            // Already exists?
            if (vesselsByRecordingIndex.ContainsKey(recordingIndex))
                return vesselsByRecordingIndex[recordingIndex];

            // Intent-line: terminal-orbit creation about to fire.
            {
                var intent = NewDecisionFields("create-terminal-orbit-intent");
                intent.RecordingId = traj.RecordingId;
                intent.RecordingIndex = recordingIndex;
                intent.VesselName = traj.VesselName;
                intent.Source = "TerminalOrbit";
                intent.Branch = "(n/a)";
                intent.Body = traj.TerminalOrbitBody;
                intent.TerminalBody = traj.TerminalOrbitBody;
                intent.TerminalSma = traj.TerminalOrbitSemiMajorAxis;
                intent.TerminalEcc = traj.TerminalOrbitEccentricity;
                ParsekLog.Verbose(Tag, BuildGhostMapDecisionLine(intent));
            }

            string logContext = string.Format(ic, "recording index={0}", recordingIndex);
            Vessel vessel = BuildAndLoadGhostProtoVessel(traj, logContext);
            if (vessel != null)
            {
                TrackRecordingGhostVessel(recordingIndex, traj, vessel);
                lifecycleCreatedThisTick++;

                Vector3d worldPos = vessel.GetWorldPos3D();
                string body = vessel.orbitDriver?.referenceBody?.name ?? traj.TerminalOrbitBody;

                var done = NewDecisionFields("create-terminal-orbit-done");
                done.RecordingId = traj.RecordingId;
                done.RecordingIndex = recordingIndex;
                done.VesselName = traj.VesselName;
                done.Source = "TerminalOrbit";
                done.Branch = "(n/a)";
                done.Body = body;
                done.WorldPos = worldPos;
                done.GhostPid = vessel.persistentId;
                done.TerminalBody = traj.TerminalOrbitBody;
                done.TerminalSma = traj.TerminalOrbitSemiMajorAxis;
                done.TerminalEcc = traj.TerminalOrbitEccentricity;
                ParsekLog.Info(Tag, BuildGhostMapDecisionLine(done));

                StashLastKnownFrame(recordingIndex, new LastKnownGhostFrame
                {
                    RecordingId = traj.RecordingId,
                    VesselName = traj.VesselName,
                    GhostPid = vessel.persistentId,
                    Source = "TerminalOrbit",
                    Branch = "(n/a)",
                    Body = body,
                    WorldPos = worldPos,
                    AnchorPid = 0u,
                    LastUT = double.NaN
                });
            }

            return vessel;
        }

        /// <summary>
        /// Create a ghost map ProtoVessel for a recording that has orbit segments but
        /// no terminal orbit data (intermediate chain segments). Uses the provided
        /// OrbitSegment for the initial orbit. Called from CheckPendingMapVessels when
        /// the ghost enters its first orbital segment.
        /// </summary>
        internal static Vessel CreateGhostVesselFromSegment(
            int recordingIndex, IPlaybackTrajectory traj, OrbitSegment segment,
            double loopEpochShiftSeconds = 0.0)
        {
            return CreateGhostVesselFromSegment(
                recordingIndex,
                traj,
                segment,
                TrackingStationGhostSource.Segment,
                loopEpochShiftSeconds);
        }

        private static Vessel CreateGhostVesselFromSegment(
            int recordingIndex,
            IPlaybackTrajectory traj,
            OrbitSegment segment,
            TrackingStationGhostSource source,
            double loopEpochShiftSeconds = 0.0)
        {
            if (traj == null) return null;
            string sourceLabel = source == TrackingStationGhostSource.EndpointTail
                ? "EndpointTail"
                : "Segment";
            string protoSource = source == TrackingStationGhostSource.EndpointTail
                ? "endpoint-tail"
                : "visible-segment";

            // Phase 7 session-suppression gate (design §3.3).
            if (IsSuppressedByActiveSession(recordingIndex))
            {
                RemoveGhostVesselForRecording(recordingIndex, "session-suppressed subtree");
                return null;
            }

            if (vesselsByRecordingIndex.ContainsKey(recordingIndex))
                return vesselsByRecordingIndex[recordingIndex];

            // Intent-line: full input shape before BuildAndLoadGhostProtoVessel.
            {
                var intent = NewDecisionFields("create-segment-intent");
                intent.RecordingId = traj.RecordingId;
                intent.RecordingIndex = recordingIndex;
                intent.VesselName = traj.VesselName;
                intent.Source = sourceLabel;
                intent.Branch = "(n/a)";
                intent.Body = segment.bodyName;
                intent.Segment = segment;
                ParsekLog.Verbose(Tag, BuildGhostMapDecisionLine(intent));
            }

            string logContext = string.Format(ic,
                "recording index={0} (from {1})",
                recordingIndex,
                protoSource);
            Vessel vessel = BuildAndLoadGhostProtoVessel(
                traj, segment, logContext, protoSource, loopEpochShiftSeconds);
            if (vessel != null)
            {
                TrackRecordingGhostVessel(recordingIndex, traj, vessel);
                ghostOrbitBounds[vessel.persistentId] = (segment.startUT, segment.endUT);
                lifecycleCreatedThisTick++;

                Vector3d worldPos = vessel.GetWorldPos3D();

                var done = NewDecisionFields("create-segment-done");
                done.RecordingId = traj.RecordingId;
                done.RecordingIndex = recordingIndex;
                done.VesselName = traj.VesselName;
                done.Source = sourceLabel;
                done.Branch = "(n/a)";
                done.Body = segment.bodyName;
                done.WorldPos = worldPos;
                done.GhostPid = vessel.persistentId;
                done.Segment = segment;
                ParsekLog.Info(Tag, BuildGhostMapDecisionLine(done));

                StashLastKnownFrame(recordingIndex, new LastKnownGhostFrame
                {
                    RecordingId = traj.RecordingId,
                    VesselName = traj.VesselName,
                    GhostPid = vessel.persistentId,
                    Source = sourceLabel,
                    Branch = "(n/a)",
                    Body = segment.bodyName,
                    WorldPos = worldPos,
                    AnchorPid = 0u,
                    LastUT = segment.startUT
                });
            }

            return vessel;
        }

        /// <summary>
        /// PURE: should a ghost removal emit the positive "orbit proto retired AT its terminal/deorbit
        /// bound" assertion, and (if so) the overshoot past that bound? Fires only for a segment-driven
        /// orbit proto (<paramref name="hadOrbitBounds"/>) retired at a TERMINAL orbit handoff - the flight
        /// <c>left-orbit-segments</c> / tracking-station <c>tracking-station-expired</c> reasons, where
        /// effUT ran off the last recorded OrbitSegment with none ahead (e.g. a parking-orbit proto yielding
        /// to the descent polyline at the deorbit). <paramref name="overshootSeconds"/> = liveUT - boundEndUT
        /// (both in the live/loop-shifted frame): ~0 positively confirms the segment drive was clamped at
        /// the bound and the proto was retired there rather than propagating past the deorbit; a large value
        /// would flag a genuine overshoot. Replaces the old "prove by ABSENCE of past-window lines + the
        /// proto being gone" inference with a single positive line. Pure / xUnit-testable.
        /// </summary>
        internal static bool ShouldAssertTerminalOrbitBoundClamp(
            string reason, bool hadOrbitBounds, double boundEndUT, double liveUT, out double overshootSeconds)
        {
            overshootSeconds = double.NaN;
            if (!hadOrbitBounds)
                return false;
            if (!string.Equals(reason, "left-orbit-segments", StringComparison.Ordinal)
                && !string.Equals(reason, "tracking-station-expired", StringComparison.Ordinal))
                return false;
            if (double.IsNaN(boundEndUT) || double.IsInfinity(boundEndUT)
                || double.IsNaN(liveUT) || double.IsInfinity(liveUT))
                return false;
            overshootSeconds = liveUT - boundEndUT;
            return true;
        }

        /// <summary>
        /// Remove a ghost map ProtoVessel for a timeline playback ghost.
        /// Called when the engine destroys a ghost (OnGhostDestroyed).
        /// </summary>
        internal static void RemoveGhostVesselForRecording(int recordingIndex, string reason)
        {
            if (!vesselsByRecordingIndex.TryGetValue(recordingIndex, out Vessel vessel))
                return;

            uint ghostPid = vessel.persistentId;

            // Capture the segment-drive orbit bound BEFORE the dictionary purge below so a terminal
            // handoff (parking-orbit proto yielding to the descent at the deorbit) can positively assert
            // the drive was clamped at the bound (see ShouldAssertTerminalOrbitBoundClamp).
            bool hadOrbitBounds = ghostOrbitBounds.TryGetValue(ghostPid, out var removedOrbitBounds);
            double removedBoundEndUT = hadOrbitBounds ? removedOrbitBounds.endUT : double.NaN;

            // Snapshot last-known frame before Die() destroys the vessel.
            bool hadLastKnown = TryGetLastKnownFrame(recordingIndex, out LastKnownGhostFrame last);

            try { vessel.Die(); }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "RemoveGhostVesselForRecording: Die() threw for index={0}: {1}",
                        recordingIndex, ex.Message));
            }

            ghostMapVesselPids.Remove(ghostPid);
            ghostsWithSuppressedIcon.Remove(ghostPid);
            ghostNoBoundsSuppressLastFrame.Remove(ghostPid);
            ghostOrbitLineGraceUntilFrame.Remove(ghostPid);
            ghostOrbitBounds.Remove(ghostPid);
            ghostBodyFrameOrbitBounds.Remove(ghostPid);
            ghostLastAppliedOrbitBody.Remove(ghostPid);
            ghostLastAppliedOrbitElements.Remove(ghostPid);
            ghostOrbitLoopShiftedPids.Remove(ghostPid);
            ghostOrbitEpochShift.Remove(ghostPid);
            ghostIconDrivePropagation.Remove(ghostPid);
            ghostParkingConicLineHoldUntilUT.Remove(ghostPid);
            vesselPidToRecordingIndex.Remove(ghostPid);
            vesselPidToRecordingId.Remove(ghostPid);
            vesselsByRecordingIndex.Remove(recordingIndex);
            activeReFlyDeferredStateVectorGhostSessions.Remove(recordingIndex);
            trackingStationStateVectorOrbitTrajectories.Remove(recordingIndex);
            trackingStationStateVectorCachedIndices.Remove(recordingIndex);
            lastKnownByRecordingIndex.Remove(recordingIndex);
            lifecycleDestroyedThisTick++;

            var destroy = NewDecisionFields("destroy");
            destroy.RecordingId = hadLastKnown ? last.RecordingId : null;
            destroy.RecordingIndex = recordingIndex;
            destroy.VesselName = hadLastKnown ? last.VesselName : null;
            destroy.Source = hadLastKnown ? last.Source : "None";
            destroy.Branch = hadLastKnown ? last.Branch : "(n/a)";
            destroy.Body = hadLastKnown ? last.Body : null;
            destroy.WorldPos = hadLastKnown ? (Vector3d?)last.WorldPos : null;
            destroy.GhostPid = ghostPid;
            destroy.AnchorPid = hadLastKnown ? last.AnchorPid : 0u;
            destroy.UT = hadLastKnown ? last.LastUT : double.NaN;
            destroy.Reason = reason ?? "(none)";
            ParsekLog.Info(Tag, BuildGhostMapDecisionLine(destroy));

            // Positive deorbit-clamp assertion: when a segment-driven orbit proto is retired at its
            // terminal orbit handoff (e.g. a parking-orbit proto yielding to the descent polyline at the
            // deorbit), log that the drive was clamped AT the last recorded orbit's end bound rather than
            // driving past-window past it. Makes "the proto did not overshoot the deorbit" a positive,
            // greppable line instead of an inference from the ABSENCE of past-window drive lines.
            double removeLiveUT = CurrentUTNow();
            if (ShouldAssertTerminalOrbitBoundClamp(
                    reason, hadOrbitBounds, removedBoundEndUT, removeLiveUT, out double boundOvershootSeconds))
            {
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Orbit proto retired AT terminal orbit bound: idx={0} pid={1} reason={2} "
                        + "boundEndUT={3:F1} liveUT={4:F1} overshoot={5:F1}s (segment drive clamped at the "
                        + "last recorded orbit's end; proto retired here, NOT driven past-window past the "
                        + "bound - e.g. a parking-orbit proto yielding to the descent at the deorbit)",
                        recordingIndex, ghostPid, reason, removedBoundEndUT, removeLiveUT,
                        boundOvershootSeconds));
            }

            // MapRenderTrace Tier-A: structural GhostDestroyed event, keyed by the
            // live ghost persistentId. Gated at the call site; reads only fields
            // already snapshotted above (the vessel is dead by this point).
            if (MapRenderTrace.IsEnabled)
            {
                double destroyUT = hadLastKnown ? last.LastUT : CurrentUTNow();
                MapRenderTrace.EmitStructural(
                    "GhostDestroyed",
                    MapRenderTrace.RenderSurface.ProtoIcon,
                    ghostPid.ToString(ic),
                    destroyUT,
                    destroyUT,
                    MapRenderTrace.DestroyWindowSeconds,
                    MapRenderTrace.BuildLifecycleDetails(
                        hadLastKnown ? last.VesselName : null,
                        hadLastKnown ? last.Body : null,
                        HighLogic.LoadedScene.ToString(),
                        hadLastKnown ? (Vector3d?)last.WorldPos : null,
                        // Carry the recordingId so a GhostDestroyed line is greppable back to its recording
                        // (and thus to the [ReaimDescent] descent-member lines and the always-on [GhostMap]
                        // destroy decision line, which already names the recordingId + the descent-member tag).
                        string.Format(ic, "{0} index={1} rec={2}", reason ?? "(none)", recordingIndex,
                            hadLastKnown ? (last.RecordingId ?? "<none>") : "<none>")));
            }
        }

        /// <summary>
        /// Remove all ghost map presence for a given recording index: both the
        /// recording-index-based ProtoVessel AND any chain-based ProtoVessel for
        /// the same vessel PID. Centralizes the dual-dict cleanup so callers
        /// don't need to reach into RecordingStore to find the chain PID.
        /// </summary>
        internal static void RemoveAllGhostPresenceForIndex(int recordingIndex, uint vesselPersistentId, string reason)
        {
            // 1. Remove recording-index-based ghost (if any)
            RemoveGhostVesselForRecording(recordingIndex, reason);

            // 2. Remove chain-based ghost (if any) — keyed by vessel PID
            if (vesselPersistentId != 0 && vesselsByChainPid.ContainsKey(vesselPersistentId))
                RemoveGhostVessel(vesselPersistentId, reason);
        }

        /// <summary>
        /// Cached tracking-station-suppressed recording IDs from the last lifecycle tick.
        /// Reused by ParsekTrackingStation.OnGUI to avoid recomputing.
        /// </summary>
        internal static HashSet<string> CachedTrackingStationSuppressedIds { get; private set; }
            = new HashSet<string>();

        internal static bool IsTerminalStateEligibleForMapPresence(TerminalState? terminal)
        {
            return !terminal.HasValue
                || terminal.Value == TerminalState.Orbiting
                || terminal.Value == TerminalState.Docked
                || terminal.Value == TerminalState.SubOrbital;
        }

        private static bool IsTerminalStateEligibleForTerminalOrbitMapPresence(TerminalState? terminal)
        {
            return !terminal.HasValue
                || terminal.Value == TerminalState.Orbiting
                || terminal.Value == TerminalState.Docked;
        }

        internal static bool ShouldCreateStateVectorOrbit(double altitude, double speed, double atmosphereDepth)
        {
            double minAltitude = atmosphereDepth > 0 ? atmosphereDepth : StateVectorCreateAltitude;
            return altitude > minAltitude && speed > StateVectorCreateSpeed;
        }

        internal static bool ShouldRemoveStateVectorOrbit(double altitude, double speed, double atmosphereDepth)
        {
            if (atmosphereDepth > 0 && altitude < atmosphereDepth)
                return true;
            return altitude < StateVectorRemoveAltitude || speed < StateVectorRemoveSpeed;
        }

        internal static double GetAtmosphereDepth(string bodyName)
        {
            try
            {
                var bodies = FlightGlobals.Bodies;
                if (bodies == null) return 0;
                for (int i = 0; i < bodies.Count; i++)
                {
                    if (bodies[i].name == bodyName && bodies[i].atmosphere)
                        return bodies[i].atmosphereDepth;
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "GetAtmosphereDepth: FlightGlobals unavailable for body={0} ({1}) — using 0",
                        bodyName ?? "(null)",
                        ex.GetType().Name));
            }
            return 0;
        }

        internal static bool IsInRelativeFrame(IPlaybackTrajectory traj, double ut)
        {
            if (traj.TrackSections == null || traj.TrackSections.Count == 0)
                return false;
            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, ut);
            return sectionIdx >= 0
                && traj.TrackSections[sectionIdx].referenceFrame == ReferenceFrame.Relative;
        }

        /// <summary>
        /// True iff the track section covering <paramref name="ut"/> is specifically
        /// <see cref="ReferenceFrame.Absolute"/> (body-fixed lat/lon/alt). The flat
        /// <c>Recording.Points</c> list (driven by the gap-points glide via
        /// <see cref="TrajectoryMath.BracketPointAtUT"/>) carries geographic lat/lon/alt
        /// only inside Absolute sections: Relative sections store anchor-local metre
        /// offsets in those same fields, and OrbitalCheckpoint coast sections are
        /// represented by sparse / empty flat points (the on-rails coast is a Keplerian
        /// bridge, not a per-frame body-fixed sample stream). Requiring an Absolute
        /// covering section makes the "has real lat/lon/alt to drive from" contract
        /// explicit, so the gap glide never brackets a stale flat point on an
        /// OrbitalCheckpoint or Relative gap and mis-positions the icon.
        /// </summary>
        internal static bool IsInAbsoluteFrame(IPlaybackTrajectory traj, double ut)
        {
            if (traj == null || traj.TrackSections == null || traj.TrackSections.Count == 0)
                return false;
            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, ut);
            return sectionIdx >= 0
                && traj.TrackSections[sectionIdx].referenceFrame == ReferenceFrame.Absolute;
        }

        internal static bool HasRecordedTrackCoverageAtUT(IPlaybackTrajectory traj, double ut)
        {
            if (traj == null)
                return false;

            if (traj.TrackSections != null && traj.TrackSections.Count > 0)
            {
                for (int i = 0; i < traj.TrackSections.Count; i++)
                {
                    TrackSection section = traj.TrackSections[i];
                    if (ut >= section.startUT && ut <= section.endUT)
                        return true;
                }

                return false;
            }

            if (traj.Points != null && traj.Points.Count > 0)
            {
                for (int i = 0; i < traj.Points.Count; i++)
                {
                    double pointUT = traj.Points[i].ut;
                    if (Math.Abs(pointUT - ut) <= 0.001)
                        return true;

                    if (pointUT > ut)
                    {
                        if (i == 0)
                            return false;

                        double previousUT = traj.Points[i - 1].ut;
                        double bracketGap = pointUT - previousUT;
                        return ut >= previousUT && bracketGap <= LegacyPointCoverageMaxGapSeconds;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Finds the immediate orbit-segment gap bracketing <paramref name="ut"/>.
        /// <paramref name="segments"/> must be sorted by increasing UT, matching the
        /// playback trajectory invariant.
        /// </summary>
        internal static bool TryFindOrbitSegmentGap(
            IReadOnlyList<OrbitSegment> segments,
            double ut,
            out OrbitSegment previous,
            out OrbitSegment next)
        {
            previous = default(OrbitSegment);
            next = default(OrbitSegment);
            if (segments == null || segments.Count < 2)
                return false;

            int previousIndex = -1;
            for (int i = 0; i < segments.Count; i++)
            {
                OrbitSegment candidate = segments[i];
                if (candidate.endUT <= ut)
                {
                    previousIndex = i;
                    continue;
                }

                if (previousIndex < 0 || candidate.startUT <= ut)
                    return false;

                previous = segments[previousIndex];
                next = candidate;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Pure decision: should the ghost map icon be driven from recorded trajectory
        /// POINTS (the body-fixed state-vector path) across this UT instead of carrying
        /// the previous OrbitSegment forward?
        ///
        /// <para>
        /// Context: <see cref="TrajectoryMath.FindOrbitSegmentOrSameBodyCarry"/> keeps a
        /// ghost's orbit alive across a same-body inter-segment gap by returning the
        /// PREVIOUS segment (the "capture-burn between two Mun orbits" case it was built
        /// for). That carry is correct when the two bracketing orbits are essentially the
        /// same shape. But when the gap is a real orbit RAISE (parking orbit -> higher
        /// loiter orbit, sma 671928 -> 731230 across a ~205s burn arc), carrying the stale
        /// parking segment freezes the icon on the OLD orbit, and when UT finally enters
        /// the next segment the icon TELEPORTS to the new orbit (the ~1318 km jump the
        /// playtest showed). The recorder actually captured that raise arc as ~84 body-fixed
        /// Absolute trajectory POINTS; this predicate routes the icon onto those points so
        /// it glides continuously across the gap.
        /// </para>
        ///
        /// <para>
        /// Returns true iff ALL hold:
        /// (1) <paramref name="effUT"/> falls in a genuine orbit-segment gap (no segment
        ///     contains it) bracketed by a previous + next segment;
        /// (2) the recording has track coverage at effUT (real lat/lon/alt POINTs to
        ///     drive from);
        /// (3) the covering track section is specifically Absolute (body-fixed lat/lon/alt).
        ///     This excludes Relative sections (which store anchor-local metre offsets in
        ///     the lat/lon/alt fields, not geographic coordinates -- the state-vector
        ///     positioner would produce a position deep inside the planet) AND
        ///     OrbitalCheckpoint coast sections (which are sparse / empty in the flat
        ///     Points list -- bracketing them would mis-position the icon). The flat
        ///     Recording.Points list driven by the glide only carries geographic
        ///     coordinates inside Absolute sections;
        /// (4) the bracketing segments are NOT orbit-equivalent for map display -- so the
        ///     genuine same-orbit carry case (capture burn between two equivalent orbits)
        ///     that the carry was built for stays on the carry path, byte-identical.
        /// </para>
        /// </summary>
        internal static bool ShouldDriveGapFromPoints(
            IReadOnlyList<OrbitSegment> effectiveSegments,
            IPlaybackTrajectory traj,
            double effUT)
        {
            if (traj == null)
                return false;

            if (!TryFindOrbitSegmentGap(effectiveSegments, effUT,
                    out OrbitSegment previous, out OrbitSegment next))
                return false;

            // Same-body gap only (self-protecting contract): the points glide is for a raise / plane
            // change within one SOI. A cross-body gap is an SOI crossing, handled by the
            // OrbitalCheckpoint state-vector path, not here. The flight + TS dispatchers already pre-gate
            // on FindOrbitSegmentOrSameBodyCarry (which is same-body), so this is belt-and-suspenders, but
            // it keeps the predicate correct in isolation - TryFindOrbitSegmentGap, unlike the carry, has
            // no body check - and robust against a future caller that does not pre-gate.
            if (!string.Equals(previous.bodyName, next.bodyName, StringComparison.Ordinal))
                return false;

            if (!HasRecordedTrackCoverageAtUT(traj, effUT))
                return false;

            // Require an Absolute covering section. This is strictly tighter than the
            // old "not Relative" check: it also rejects OrbitalCheckpoint coast gaps,
            // where the flat Points list is sparse / empty and bracketing it would
            // strand the icon on a stale clamped point. The gap glide reads geographic
            // lat/lon/alt from the flat Points list, which is only populated and
            // geographic inside Absolute (atmospheric / maneuver) regions.
            if (!IsInAbsoluteFrame(traj, effUT))
                return false;

            // Preserve the carry's original purpose: when the two bracketing segments
            // describe the same orbit shape, the carry (previous segment held forward)
            // is correct and the icon stays put with no teleport. Only the NON-equivalent
            // gap (a real raise / plane change) needs the points glide.
            if (TrajectoryMath.AreOrbitSegmentsEquivalentForMapDisplay(previous, next))
                return false;

            return true;
        }

        /// <summary>
        /// Evaluates the narrow OrbitalCheckpoint state-vector carve-out for SOI gaps.
        /// <paramref name="expectedSoiGapBody"/> is a caller hint; when the segment
        /// list contains the bracketing gap, the actual post-gap segment body wins.
        /// </summary>
        internal static OrbitalCheckpointStateVectorFallbackDecision EvaluateOrbitalCheckpointStateVectorFallback(
            IPlaybackTrajectory traj,
            double currentUT,
            TrajectoryPoint point,
            bool segmentSourceAvailable,
            bool allowSoiGapRecovery,
            string expectedSoiGapBody)
        {
            double activationStartUT = PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(traj);
            double endUT = traj?.EndUT ?? double.NaN;
            bool withinPlaybackWindow =
                traj != null
                && currentUT >= activationStartUT
                && currentUT <= endUT
                && point.ut >= activationStartUT
                && point.ut <= endUT;

            string expectedBody = expectedSoiGapBody;
            bool hasGap = false;
            bool gapBodyTransition = false;
            string gapPreviousBody = null;
            string gapNextBody = null;
            if (traj?.OrbitSegments != null
                && TryFindOrbitSegmentGap(traj.OrbitSegments, currentUT, out OrbitSegment previousSegment, out OrbitSegment nextSegment))
            {
                hasGap = true;
                gapPreviousBody = previousSegment.bodyName;
                gapNextBody = nextSegment.bodyName;
                gapBodyTransition = !string.Equals(
                    previousSegment.bodyName,
                    nextSegment.bodyName,
                    StringComparison.Ordinal);
                expectedBody = nextSegment.bodyName;
            }

            bool isSoiGapRecovery = allowSoiGapRecovery && hasGap && gapBodyTransition;
            bool bodyMatches = !string.IsNullOrEmpty(expectedBody)
                && string.Equals(point.bodyName, expectedBody, StringComparison.Ordinal);

            string reason;
            bool accepted = false;
            if (segmentSourceAvailable)
                reason = OrbitalCheckpointStateVectorRejectSaferSegment;
            else if (!isSoiGapRecovery)
                reason = OrbitalCheckpointStateVectorRejectNotSoiGap;
            else if (!bodyMatches)
                reason = OrbitalCheckpointStateVectorRejectBodyMismatch;
            else if (!withinPlaybackWindow)
                reason = OrbitalCheckpointStateVectorRejectOutsideWindow;
            else
            {
                accepted = true;
                reason = SoiGapStateVectorFallbackReason;
            }

            return new OrbitalCheckpointStateVectorFallbackDecision
            {
                Accepted = accepted,
                Reason = reason,
                ExpectedBody = expectedBody,
                StateVectorBody = point.bodyName,
                SegmentSourceAvailable = segmentSourceAvailable,
                IsSoiGapRecovery = isSoiGapRecovery,
                GapBodyTransition = gapBodyTransition,
                BodyMatches = bodyMatches,
                WithinPlaybackWindow = withinPlaybackWindow,
                GapPreviousBody = gapPreviousBody,
                GapNextBody = gapNextBody,
                ActivationStartUT = activationStartUT,
                EndUT = endUT
            };
        }

        private static string FormatOrbitalCheckpointStateVectorFallbackDecision(
            OrbitalCheckpointStateVectorFallbackDecision decision)
        {
            return string.Format(ic,
                "orbitalCheckpointFallback={0} fallbackReason={1} isSoiGapRecovery={2} gapBodyTransition={3} gapPreviousBody={4} gapNextBody={5} segmentSourceAvailable={6} bodyMatches={7} stateVectorBody={8} expectedBody={9} withinPlaybackWindow={10} activationStartUT={11:F1} endUT={12:F1}",
                decision.Accepted ? "accept" : "reject",
                decision.Reason ?? "(none)",
                decision.IsSoiGapRecovery,
                decision.GapBodyTransition,
                decision.GapPreviousBody ?? "(none)",
                decision.GapNextBody ?? "(none)",
                decision.SegmentSourceAvailable,
                decision.BodyMatches,
                decision.StateVectorBody ?? "(null)",
                decision.ExpectedBody ?? "(none)",
                decision.WithinPlaybackWindow,
                decision.ActivationStartUT,
                decision.EndUT);
        }

        internal static bool StartsInOrbit(IPlaybackTrajectory traj, double ut)
        {
            if (!traj.HasOrbitSegments)
                return false;
            if (traj.Points == null || traj.Points.Count == 0)
                return true;
            return TrajectoryMath.FindOrbitSegment(traj.OrbitSegments, ut) != null;
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
        /// Emit the structured "source-resolve" decision line. Pulled out of
        /// <see cref="ResolveMapPresenceGhostSource"/>'s inner local function
        /// because C# 7 forbids closing over <c>out</c>/<c>ref</c> parameters,
        /// and the segment / state-vector data we want to log are passed in
        /// through such parameters.
        /// </summary>
        private static void EmitSourceResolveLine(
            IPlaybackTrajectory traj,
            string recId,
            int recordingIndex,
            TrackingStationGhostSource source,
            string reason,
            double currentUT,
            string logOperationName,
            OrbitSegment resolvedSegment,
            TrajectoryPoint resolvedStatePoint)
        {
            var srf = NewDecisionFields("source-resolve");
            srf.RecordingId = recId;
            // `recordingIndex` is `-1` when the caller did not (or could not)
            // know the index — that's an explicit "unknown" sentinel rather
            // than the misleading `idx=0` `NewDecisionFields` would produce.
            srf.RecordingIndex = recordingIndex;
            srf.VesselName = traj?.VesselName;
            srf.Source = source.ToString();
            srf.Branch = "(n/a)";
            srf.UT = currentUT;
            srf.Reason = string.IsNullOrEmpty(reason) ? logOperationName : reason;
            if (source == TrackingStationGhostSource.Segment
                || source == TrackingStationGhostSource.EndpointTail)
            {
                srf.Body = resolvedSegment.bodyName;
                srf.Segment = resolvedSegment;
            }
            else if (IsStateVectorGhostSource(source))
            {
                srf.Body = resolvedStatePoint.bodyName;
                srf.StateVecAlt = resolvedStatePoint.altitude;
                srf.StateVecSpeed = resolvedStatePoint.velocity.magnitude;
            }
            else if (source == TrackingStationGhostSource.TerminalOrbit)
            {
                srf.Body = traj?.TerminalOrbitBody;
                srf.TerminalBody = traj?.TerminalOrbitBody;
                srf.TerminalSma = traj?.TerminalOrbitSemiMajorAxis ?? double.NaN;
                srf.TerminalEcc = traj?.TerminalOrbitEccentricity ?? double.NaN;
            }
            // State-change-driven so a stuck (source, reason) decision doesn't
            // re-emit on a wall-clock cadence. Identity scopes the cache per
            // (operation, recording); the state key encodes (source, reason)
            // so any decision flip reopens the gate.
            ParsekLog.VerboseOnChange(
                Tag,
                string.Format(ic,
                    "gm-source-resolve-{0}-{1}",
                    logOperationName ?? "(none)",
                    recId),
                string.Format(ic,
                    "{0}|{1}",
                    source,
                    string.IsNullOrEmpty(reason) ? "none" : reason),
                BuildGhostMapDecisionLine(srf));
        }

        internal static bool ShouldOverrideVisibleSegmentWithEndpointTail(
            OrbitSegment selectedSegment,
            string preferredEndpointBody,
            string endpointSeedSource,
            TailDerivedOrbitSeed tailSeed,
            bool terminalMapPresenceRegion,
            bool loopMemberInWindow = false)
        {
            // A loop member replaying INSIDE its window is mid-flight by definition: the covering
            // segment at the loop effUT is the truth, and the endpoint-tail "fresher than the
            // stored segments" staleness comparison is meaningless against a loop-mapped sample
            // UT (the 2026-06-12 playtest: a docked-ending recording's garbage tail orbit -
            // ecc 0.94, retrograde - overrode the correct first parking segment at every proto
            // re-create, snapping the map icon off its line at warp transitions). This makes the
            // long-documented "unconditionally suppressed for loop members" contract REAL on the
            // create-resolver path; the TS update pass already enforced it via
            // EndpointTailAllowedInTrackingStationUpdate.
            if (loopMemberInWindow)
                return false;
            if (!terminalMapPresenceRegion)
                return false;
            if (!tailSeed.Accepted || !tailSeed.UsedHistoricalBodyRotation)
                return false;
            if (string.IsNullOrEmpty(preferredEndpointBody)
                || !string.Equals(selectedSegment.bodyName, preferredEndpointBody, StringComparison.Ordinal)
                || !string.Equals(tailSeed.BodyName, preferredEndpointBody, StringComparison.Ordinal))
            {
                return false;
            }
            if (!string.Equals(endpointSeedSource, "endpoint-segment", StringComparison.Ordinal))
                return false;
            if (!IsFinite(tailSeed.TailUT)
                || !IsFinite(tailSeed.LatestStoredSegmentEndUT)
                || tailSeed.TailUT <= tailSeed.LatestStoredSegmentEndUT + OrbitSeedResolver.TailDerivedOrbitFreshnessEpsilon)
            {
                return false;
            }
            if (selectedSegment.endUT > tailSeed.LatestStoredSegmentEndUT + OrbitSeedResolver.TailDerivedOrbitFreshnessEpsilon)
                return false;
            if (SegmentContainsUT(selectedSegment, tailSeed.TailUT))
                return false;

            return true;
        }

        private static bool IsTerminalMapPresenceRegion(
            IPlaybackTrajectory traj,
            double currentUT)
        {
            if (traj == null)
                return false;
            return IsTerminalStateEligibleForTerminalOrbitMapPresence(traj.TerminalStateValue)
                && currentUT >= PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(traj);
        }

        /// <summary>
        /// Pure: should the terminal-orbit synthesis be allowed for this recording
        /// under a non-zero loop epoch shift?
        ///
        /// Compares the recording's last sampled point's body against the
        /// terminal-orbital reference body (<see cref="IPlaybackTrajectory.TerminalOrbitBody"/>
        /// DIRECTLY, not via <c>TryGetPreferredEndpointBodyName</c> which for a
        /// no-OrbitSegment recording would return the last-point body and make this
        /// predicate tautological).
        ///
        /// Same-body terminals (recording ended in body B's orbit having last
        /// sampled in body B's SOI) are safe: the synthesized orbit is around the
        /// same body the recording is anchored to at its end, so the loop epoch
        /// shift propagates inertially (inertial Keplerian elements survive the
        /// shift; only the epoch advances). Cross-body terminals (last sampled in
        /// body A but TerminalOrbitBody = B) are the 181 Mm bug class and stay
        /// suppressed.
        ///
        /// Defensive guard: a recording could have non-empty
        /// <see cref="IPlaybackTrajectory.TerminalOrbitBody"/> but
        /// <see cref="IPlaybackTrajectory.TerminalOrbitSemiMajorAxis"/> = 0
        /// (uninitialised). The downstream <c>endpoint-terminal-orbit</c> source
        /// path requires <c>HasRecordedTerminalOrbit</c>; suppress the
        /// loop-accept here so the predicate's truth value matches downstream
        /// behaviour.
        /// </summary>
        internal static bool IsTerminalOrbitSynthesisSafeForLoopMember(IPlaybackTrajectory traj)
        {
            // This is a PURE per-frame predicate called from the flight pending-create and
            // TS lifecycle passes for every loop member every tick. Its result is stable for
            // a given recording, so all six log paths are rate-limited per recording (shared
            // key) to one line per interval — without this it produces thousands of identical
            // lines per session (5212 in the 2026-05-29 capture). Callers that care about the
            // bool log the acted-on decision separately.
            if (traj == null)
            {
                ParsekLog.VerboseRateLimited(Tag,
                    "terminal-orbit-safe-null",
                    "IsTerminalOrbitSynthesisSafeForLoopMember: result=false reason=null-trajectory",
                    10.0);
                return false;
            }
            string recId = traj.RecordingId ?? "(null)";
            string safeKey = "terminal-orbit-safe-" + recId;
            if (traj.Points == null || traj.Points.Count == 0)
            {
                ParsekLog.VerboseRateLimited(Tag, safeKey,
                    string.Format(ic,
                        "IsTerminalOrbitSynthesisSafeForLoopMember: rec={0} result=false reason=empty-points",
                        recId),
                    10.0);
                return false;
            }
            string terminalBody = traj.TerminalOrbitBody;
            if (string.IsNullOrEmpty(terminalBody))
            {
                ParsekLog.VerboseRateLimited(Tag, safeKey,
                    string.Format(ic,
                        "IsTerminalOrbitSynthesisSafeForLoopMember: rec={0} result=false reason=no-terminal-orbit-body",
                        recId),
                    10.0);
                return false;
            }
            if (traj.TerminalOrbitSemiMajorAxis <= 0.0)
            {
                ParsekLog.VerboseRateLimited(Tag, safeKey,
                    string.Format(ic,
                        "IsTerminalOrbitSynthesisSafeForLoopMember: rec={0} result=false reason=zero-terminal-sma terminalBody={1}",
                        recId,
                        terminalBody),
                    10.0);
                return false;
            }
            string lastPointBody = traj.Points[traj.Points.Count - 1].bodyName;
            if (string.IsNullOrEmpty(lastPointBody))
            {
                ParsekLog.VerboseRateLimited(Tag, safeKey,
                    string.Format(ic,
                        "IsTerminalOrbitSynthesisSafeForLoopMember: rec={0} result=false reason=no-last-point-body terminalBody={1}",
                        recId,
                        terminalBody),
                    10.0);
                return false;
            }
            bool sameBody = string.Equals(lastPointBody, terminalBody, StringComparison.Ordinal);
            ParsekLog.VerboseRateLimited(Tag, safeKey,
                string.Format(ic,
                    "IsTerminalOrbitSynthesisSafeForLoopMember: rec={0} result={1} reason={2} terminalBody={3} lastPointBody={4}",
                    recId,
                    sameBody,
                    sameBody ? "same-body" : "cross-body-terminal",
                    terminalBody,
                    lastPointBody),
                10.0);
            return sameBody;
        }

        /// <summary>
        /// Defense-in-depth check used inside <see cref="TryResolveEndpointTailForMapPresence"/>
        /// when the persisted-phase relaxation accepts a <c>TrajectoryPoint</c> phase.
        /// Compares the persisted endpoint body against the recording's terminal-orbit
        /// reference body. The outer call site predicate
        /// (<see cref="IsTerminalOrbitSynthesisSafeForLoopMember"/>) compares last-point body
        /// against terminal-orbit body; this inner check compares the persisted endpoint body
        /// against terminal-orbit body. Both must agree before the relaxed source is accepted
        /// so a future <c>RefreshEndpointDecision</c> that diverges the persisted body from the
        /// last-point body cannot cause cross-body synthesis.
        /// </summary>
        internal static bool IsTrackingTerminalOrbitBody(IPlaybackTrajectory traj, string endpointBodyName)
        {
            if (traj == null) return false;
            string terminalBody = traj.TerminalOrbitBody;
            if (string.IsNullOrEmpty(terminalBody) || string.IsNullOrEmpty(endpointBodyName))
                return false;
            return string.Equals(endpointBodyName, terminalBody, StringComparison.Ordinal);
        }

        internal static bool TryResolveEndpointTailForMapPresence(
            IPlaybackTrajectory traj,
            double currentUT,
            OrbitSegment? selectedSegment,
            bool terminalMapPresenceRegion,
            out OrbitSegment endpointTailSegment,
            out TailDerivedOrbitSeed tailSeed,
            out string detail,
            bool acceptTerminalOrbitSource = false,
            bool loopMemberInWindow = false)
        {
            endpointTailSegment = default(OrbitSegment);
            tailSeed = default(TailDerivedOrbitSeed);
            detail = null;

            if (traj == null || !terminalMapPresenceRegion)
                return false;

            if (!RecordingEndpointResolver.TryGetPreferredEndpointBodyName(
                    traj,
                    out string preferredEndpointBody))
            {
                return false;
            }

            if (OrbitSeedResolver.TailSeedResolverForTesting == null
                && !OrbitSeedResolver.TryFindLatestCoastTrajectoryFrame(
                    traj,
                    preferredEndpointBody,
                    out _,
                    out _))
            {
                return false;
            }

            CelestialBody body = FindBodyByName(preferredEndpointBody);
            bool tailAccepted = OrbitSeedResolver.TryDeriveTailOrbitSeed(
                traj,
                body,
                currentUT,
                TailSeedUse.MapPresence,
                out tailSeed);
            if (!tailAccepted)
            {
                detail = FormatEndpointTailSeedDetail(tailSeed, accepted: false);
                return false;
            }

            string endpointSeedSource = null;
            RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed(
                traj,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out RecordingEndpointResolver.EndpointOrbitSeedDiagnostics endpointDiagnostics);
            endpointSeedSource = endpointDiagnostics.Source;

            RecordingEndpointResolver.TryGetPersistedEndpointDecision(
                traj,
                out RecordingEndpointPhase endpointPhase,
                out string endpointBodyName);

            // section 1.1 relaxed inner gate (split into source-half and persisted-phase-half).
            // Default (acceptTerminalOrbitSource=false) collapses to the original
            // "endpoint-segment source AND OrbitSegment persisted phase with matching body"
            // contract; passing true additionally accepts the same-body
            // "endpoint-terminal-orbit" source plus a TrajectoryPoint persisted phase whose
            // body matches the recording's terminal-orbit body. Defence in depth: the
            // persisted-phase relaxation requires IsTrackingTerminalOrbitBody so a future
            // RefreshEndpointDecision that diverges the persisted body from the last-point
            // body cannot accidentally cross-body-synthesise.
            bool sourceAccepted =
                string.Equals(endpointSeedSource, "endpoint-segment", StringComparison.Ordinal)
                || (acceptTerminalOrbitSource
                    && string.Equals(endpointSeedSource, "endpoint-terminal-orbit", StringComparison.Ordinal));

            bool persistedPhaseAccepted =
                (endpointPhase == RecordingEndpointPhase.OrbitSegment
                    && string.Equals(endpointBodyName, preferredEndpointBody, StringComparison.Ordinal))
                || (acceptTerminalOrbitSource
                    && endpointPhase == RecordingEndpointPhase.TrajectoryPoint
                    && IsTrackingTerminalOrbitBody(traj, endpointBodyName));

            if (!sourceAccepted || !persistedPhaseAccepted)
            {
                TailDerivedOrbitSeed declinedSeed = tailSeed;
                declinedSeed.DeclineReason =
                    !sourceAccepted
                        ? "endpoint-seed-not-segment"
                        : "endpoint-family-not-segment";
                detail = CombineSourceDetails(
                    FormatEndpointTailSeedDetail(declinedSeed, accepted: false),
                    string.Format(ic,
                        "endpointSeedSource={0} endpointPhase={1} endpointBody={2} acceptTerminalOrbitSource={3}",
                        endpointSeedSource ?? "(null)",
                        endpointPhase,
                        endpointBodyName ?? "(null)",
                        acceptTerminalOrbitSource));
                return false;
            }

            if (acceptTerminalOrbitSource
                && (!string.Equals(endpointSeedSource, "endpoint-segment", StringComparison.Ordinal)
                    || endpointPhase == RecordingEndpointPhase.TrajectoryPoint))
            {
                // Log the relaxation acceptance so a KSP.log capture can confirm the
                // loop-aware path actually fired (vs. the legacy endpoint-segment route).
                // Rate-limited per recording: a non-zero loop epoch shift is the STEADY
                // STATE for a member's whole loop window (not an edge), and this resolver
                // runs every refresh tick (now 4 Hz in the TS), so a raw Verbose here
                // would fire every frame for a same-body loop member sitting in its
                // terminal-orbit phase.
                ParsekLog.VerboseRateLimited(Tag,
                    "endpoint-tail-loop-accept-" + (traj.RecordingId ?? "(null)"),
                    string.Format(ic,
                        "endpoint-tail-synthesis-loop-accept rec={0} terminalBody={1} endpointSeedSource={2} endpointPhase={3} endpointBody={4} tailUT={5:F2} tailSma={6:F1}",
                        traj.RecordingId ?? "(null)",
                        traj.TerminalOrbitBody ?? "(null)",
                        endpointSeedSource ?? "(null)",
                        endpointPhase,
                        endpointBodyName ?? "(null)",
                        tailSeed.TailUT,
                        tailSeed.Segment.semiMajorAxis),
                    5.0);
            }

            if (selectedSegment.HasValue
                && !ShouldOverrideVisibleSegmentWithEndpointTail(
                    selectedSegment.Value,
                    preferredEndpointBody,
                    endpointSeedSource,
                    tailSeed,
                    terminalMapPresenceRegion,
                    loopMemberInWindow))
            {
                TailDerivedOrbitSeed declinedSeed = tailSeed;
                declinedSeed.DeclineReason = "visible-segment-not-stale-endpoint";
                detail = CombineSourceDetails(
                    FormatEndpointTailSeedDetail(declinedSeed, accepted: false),
                    string.Format(ic,
                        "endpointSeedSource={0} endpointPhase={1} endpointBody={2}",
                        endpointSeedSource ?? "(null)",
                        endpointPhase,
                        endpointBodyName ?? "(null)"));
                return false;
            }

            endpointTailSegment = tailSeed.Segment;
            detail = FormatEndpointTailSeedDetail(tailSeed, accepted: true);
            return true;
        }

        private static bool SegmentContainsUT(OrbitSegment segment, double ut)
        {
            return IsFinite(ut)
                && ut >= segment.startUT - OrbitSeedResolver.TailDerivedOrbitFreshnessEpsilon
                && ut <= segment.endUT + OrbitSeedResolver.TailDerivedOrbitFreshnessEpsilon;
        }

        private static string FormatEndpointTailSeedDetail(
            TailDerivedOrbitSeed seed,
            bool accepted)
        {
            return string.Format(ic,
                "endpointTailSeed={0} tailDecline={1} tailUT={2:F2} tailSma={3:F1} tailEcc={4:F6} latestSegmentEndUT={5:F2} drift={6:F2}s tailFrame={7} historicalRotation={8} historicalLon={9:F4}",
                accepted ? "accept" : "decline",
                seed.DeclineReason ?? "(none)",
                seed.TailUT,
                seed.Segment.semiMajorAxis,
                seed.Segment.eccentricity,
                seed.LatestStoredSegmentEndUT,
                seed.RotationDriftSeconds,
                seed.TailFrameSource ?? "(none)",
                seed.UsedHistoricalBodyRotation,
                seed.HistoricalLongitude);
        }

        private static string FormatEndpointTailBypassDetail(
            string reason,
            string endpointBodyName,
            RecordingEndpointPhase endpointPhase,
            string persistedEndpointBodyName)
        {
            return string.Format(ic,
                "endpointTailSeed=bypass tailDecline={0} tailUT=NaN tailSma=NaN tailEcc=NaN latestSegmentEndUT=NaN drift=NaNs tailFrame=(none) historicalRotation=False endpointPhase={1} endpointBody={2} preferredEndpointBody={3}",
                reason ?? "(none)",
                endpointPhase,
                persistedEndpointBodyName ?? "(null)",
                endpointBodyName ?? "(none)");
        }

        private static string BuildEndpointTailBypassDetail(
            IPlaybackTrajectory traj,
            double currentUT,
            bool terminalMapPresenceRegion)
        {
            if (traj == null)
            {
                return FormatEndpointTailBypassDetail(
                    "null-trajectory",
                    null,
                    RecordingEndpointPhase.Unknown,
                    null);
            }

            RecordingEndpointResolver.TryGetPersistedEndpointDecision(
                traj,
                out RecordingEndpointPhase endpointPhase,
                out string persistedEndpointBodyName);

            if (!terminalMapPresenceRegion)
            {
                return FormatEndpointTailBypassDetail(
                    "not-terminal-map-presence",
                    null,
                    endpointPhase,
                    persistedEndpointBodyName);
            }

            if (!RecordingEndpointResolver.TryGetPreferredEndpointBodyName(
                    traj,
                    out string preferredEndpointBody))
            {
                return FormatEndpointTailBypassDetail(
                    "no-endpoint-body",
                    null,
                    endpointPhase,
                    persistedEndpointBodyName);
            }

            if (OrbitSeedResolver.TailSeedResolverForTesting == null
                && !OrbitSeedResolver.TryFindLatestCoastTrajectoryFrame(
                    traj,
                    preferredEndpointBody,
                    out _,
                    out _))
            {
                return FormatEndpointTailBypassDetail(
                    "no-absolute-coast-tail",
                    preferredEndpointBody,
                    endpointPhase,
                    persistedEndpointBodyName);
            }

            return FormatEndpointTailBypassDetail(
                "not-evaluated",
                preferredEndpointBody,
                endpointPhase,
                persistedEndpointBodyName);
        }

        private static string BuildEndpointTailTrackingStationDetail(
            Recording rec,
            double currentUT,
            TrackingStationGhostSource source,
            OrbitSegment segment)
        {
            bool terminalMapPresenceRegion = IsTerminalMapPresenceRegion(rec, currentUT);
            OrbitSegment? selectedSegment = source == TrackingStationGhostSource.EndpointTail
                ? (OrbitSegment?)null
                : segment;

            TryResolveEndpointTailForMapPresence(
                rec,
                currentUT,
                selectedSegment,
                terminalMapPresenceRegion,
                out _,
                out _,
                out string endpointTailDetail);

            if (!string.IsNullOrEmpty(endpointTailDetail))
                return endpointTailDetail;

            return BuildEndpointTailBypassDetail(
                rec,
                currentUT,
                terminalMapPresenceRegion);
        }

        internal static TrackingStationGhostSource ResolveMapPresenceGhostSource(
            IPlaybackTrajectory traj,
            bool isSuppressed,
            bool alreadyMaterialized,
            double currentUT,
            bool allowTerminalOrbitFallback,
            string logOperationName,
            ref int stateVectorCachedIndex,
            out OrbitSegment segment,
            out TrajectoryPoint stateVectorPoint,
            out string skipReason,
            int recordingIndex = -1,
            bool allowSoiGapStateVectorFallback = false,
            string expectedSoiGapBody = null,
            bool acceptTerminalOrbitForLoopSynthesis = false,
            bool loopMemberInWindow = false,
            bool liveLaunchMatchedAnchorOfActiveMember = false,
            bool transferMemberDescentContinuation = false)
        {
            segment = default(OrbitSegment);
            stateVectorPoint = default(TrajectoryPoint);
            skipReason = null;
            string recId = traj?.RecordingId ?? "(null)";

            // These mirrors of the out parameters exist solely so the ReturnDecision
            // local function can read the resolved segment / state point at the
            // moment a decision is finalised. C# 7 forbids capturing `out` /
            // `ref` parameters in nested closures, so we keep these locals in sync
            // immediately before each ReturnDecision call. Both the unstructured
            // diagnostic summary (CombineSourceDetails / BuildGhostSourceStructuredDetail)
            // and the structured GhostMap decision-line emission
            // (EmitSourceResolveLine) read these mirrors.
            OrbitSegment resolvedSegment = default(OrbitSegment);
            TrajectoryPoint resolvedStatePoint = default(TrajectoryPoint);

            TrackingStationGhostSource ReturnDecision(
                TrackingStationGhostSource source,
                string reason,
                string detail = null)
            {
                string combinedDetail = CombineSourceDetails(
                    detail,
                    BuildGhostSourceStructuredDetail(
                        traj,
                        currentUT,
                        source,
                        resolvedSegment,
                        resolvedStatePoint));
                if (!string.IsNullOrEmpty(logOperationName))
                {
                    // Per-frame caller — emit only when the (source, reason) decision
                    // flips for this (operation, recording) pair. A stable
                    // (None, state-vector-threshold) loop in the pending-create queue
                    // would otherwise pour ~one line per recording per second; here
                    // we emit once on entry into the state and again only when the
                    // state actually changes. Suppressed-count is preserved on the
                    // next emission so post-hoc audits can reconstruct the volume.
                    ParsekLog.VerboseOnChange(
                        Tag,
                        string.Format(ic,
                            "map-ghost-source-{0}-{1}",
                            logOperationName,
                            recId),
                        string.Format(ic,
                            "{0}|{1}",
                            source,
                            reason ?? "none"),
                        string.Format(ic,
                            "{0}: rec={1} currentUT={2:F1} source={3} reason={4}{5}",
                            logOperationName,
                            recId,
                            currentUT,
                            source,
                            reason ?? "(none)",
                            string.IsNullOrEmpty(combinedDetail) ? string.Empty : " " + combinedDetail));

                    // Structured-line emission so the post-hoc reader can grep one
                    // canonical shape across every decision in this file. Local
                    // copies of segment / stateVectorPoint are captured into
                    // resolvedSegment / resolvedStatePoint at each ReturnDecision
                    // call site since the C# spec forbids closing over `out`/`ref`
                    // parameters.
                    EmitSourceResolveLine(
                        traj,
                        recId,
                        recordingIndex,
                        source,
                        reason,
                        currentUT,
                        logOperationName,
                        resolvedSegment,
                        resolvedStatePoint);
                }
                return source;
            }

            if (traj == null)
            {
                skipReason = "null";
                return ReturnDecision(TrackingStationGhostSource.None, skipReason, "null trajectory");
            }

            if (traj.IsDebris)
            {
                skipReason = "debris";
                return ReturnDecision(TrackingStationGhostSource.None, skipReason, "isDebris=True");
            }

            if (isSuppressed)
            {
                skipReason = TrackingStationGhostSkipSuppressed;
                return ReturnDecision(TrackingStationGhostSource.None, skipReason, "isSuppressed=True");
            }

            // A persisted real vessel (a completed mission's craft materialized at its
            // terminal, e.g. parked in orbit) normally suppresses a duplicate map ghost.
            // But when this recording is a Mission-loop member replaying inside its loop
            // window (loopMemberInWindow), that real vessel sits frozen at the mission's
            // FINAL state while the loop replays an EARLIER phase somewhere else, so
            // suppressing here leaves the looped leg with no map trajectory following the
            // ghost (the "no trajectory after the Mun takeoff" report). Per the loop-render
            // decision we draw the animated loop ghost ALONGSIDE the real vessel in that
            // case (both icons may show). Re-fly / active-session duplicates stay blocked
            // because isSuppressed is evaluated above this gate.
            // Step 2 (Logistics route live-anchor bind): when this recording's
            // guid-gated launch-matched live vessel is loaded AND it is the LIVE docking
            // anchor a relative member is binding to this-or-last frame (the Step-1
            // live-bind event), its own loop ghost is a pure duplicate of the live
            // station at that moment (the dependent member docks against the live vessel,
            // not this recorded position ~20 km away). Suppress it like a non-loop
            // already-spawned vessel even though loopMemberInWindow is true (the caller
            // feeds the live-bind-event term here, not a whole-loop existence check).
            // Every other loopMemberInWindow case still draws alongside the real vessel.
            if (alreadyMaterialized && (!loopMemberInWindow || liveLaunchMatchedAnchorOfActiveMember))
            {
                skipReason = liveLaunchMatchedAnchorOfActiveMember
                    ? TrackingStationGhostSkipLiveAnchorDouble
                    : TrackingStationGhostSkipAlreadySpawned;
                if (liveLaunchMatchedAnchorOfActiveMember)
                {
                    ParsekLog.VerboseRateLimited(Tag,
                        string.Format(ic, "anchor-double-suppressed-{0}", recId),
                        string.Format(ic,
                            "anchor-double-suppressed (live-bind event): rec={0} anchorPid={1} "
                            + "reason=launch-matched-live-vessel-loaded-and-live-bound",
                            recId,
                            (traj as Recording)?.VesselPersistentId ?? 0u),
                        5.0);
                    return ReturnDecision(
                        TrackingStationGhostSource.None,
                        skipReason,
                        "live-anchor double suppressed");
                }
                return ReturnDecision(TrackingStationGhostSource.None, skipReason, "already spawned");
            }
            if (alreadyMaterialized)
            {
                ParsekLog.VerboseRateLimited(Tag,
                    string.Format(ic, "loop-ghost-alongside-real-{0}", recId),
                    string.Format(ic,
                        "ResolveMapPresenceGhostSource: loop member rec={0} drawing map ghost "
                        + "alongside persisted real vessel (loopMemberInWindow)",
                        recId),
                    5.0);
            }

            var terminal = traj.TerminalStateValue;
            if (!IsTerminalStateEligibleForMapPresence(terminal))
            {
                skipReason = "terminal-" + terminal.Value;
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    string.Format(ic, "terminal={0}", terminal.Value));
            }

            double activationStartUT = PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(traj);
            bool terminalMapPresenceRegion = IsTerminalMapPresenceRegion(traj, currentUT);

            // COSMETIC fix (re-aim descent "post-landing suborbital looping ghost"): the descent-trigger
            // TRANSFER member, once the shared descent has handed off / landed (descent phase Descent or Done,
            // computed at the LIVE clock by the caller via
            // GhostPlaybackLogic.IsTransferMemberDescentContinuation), has finished its recorded journey at the
            // shifted parking deorbit point. With no covering OrbitSegment past that conic it would otherwise
            // synthesize an EndpointTail coast from the recorded deorbit endpoint = a sub-surface ellipse drawn
            // as a closed loop. RETIRE the ghost cleanly here, BEFORE the covering-segment / EndpointTail /
            // Segment resolution below, so there is NOTHING to fall back to (this is the J2 trap the reverted
            // 9fecdfcb6 hit: a decline INSIDE the endpoint-tail resolver fell through to a stale launch/transfer
            // Segment - Sun/Ike - and put a clickable ghost on the wrong body). The pre-seam loiter parking conic
            // is preserved because the flag is FALSE in Inert/Loiter, so the normal covering-segment branch runs
            // unchanged. Default false keeps every non-opted-in caller byte-identical.
            if (transferMemberDescentContinuation)
            {
                skipReason = "transfer-member-descent-continuation";
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    "descent-trigger transfer member handed off / landed: retire cleanly; "
                        + "the descent set owns the visual (no sub-surface endpoint-tail, no wrong-segment fallback)");
            }

            string checkpointFallbackDetail = null;
            string checkpointFallbackRejectReason = null;
            if (traj.HasOrbitSegments)
            {
                OrbitSegment? currentSegment =
                    TrajectoryMath.FindOrbitSegmentForMapDisplay(traj.OrbitSegments, currentUT);
                if (currentSegment.HasValue)
                {
                    segment = currentSegment.Value;
                    resolvedSegment = segment;

                    if (TryResolveCheckpointStateVectorMapPoint(
                        traj,
                        currentUT,
                        ref stateVectorCachedIndex,
                        out TrajectoryPoint checkpointPoint,
                        out _,
                        out TrackSection checkpointSection))
                    {
                        OrbitalCheckpointStateVectorFallbackDecision checkpointDecision =
                            EvaluateOrbitalCheckpointStateVectorFallback(
                                traj,
                                currentUT,
                                checkpointPoint,
                                segmentSourceAvailable: true,
                                allowSoiGapRecovery: allowSoiGapStateVectorFallback,
                                expectedSoiGapBody: expectedSoiGapBody);
                        checkpointFallbackDetail = string.Format(ic,
                            "stateVectorSource=OrbitalCheckpoint sectionUT={0:F1}-{1:F1} pointUT={2:F1} stateVectorBody={3} alt={4:F0} speed={5:F1} {6}",
                            checkpointSection.startUT,
                            checkpointSection.endUT,
                            checkpointPoint.ut,
                            checkpointPoint.bodyName ?? "(null)",
                            checkpointPoint.altitude,
                            checkpointPoint.velocity.magnitude,
                            FormatOrbitalCheckpointStateVectorFallbackDecision(checkpointDecision));
                    }

                    OrbitSegment visibleSegment = segment;
                    string endpointTailDetail = null;
                    // FIRST endpoint-tail branch (covering-segment override). This is the
                    // actual 181 Mm cross-body bug class and must stay suppressed for loop
                    // members - which the loopMemberInWindow argument now ENFORCES (the prior
                    // comment claimed unconditional suppression, but no gate existed on this
                    // create-resolver path; only the TS update pass had one. 2026-06-12
                    // playtest: a docked-ending loop member's garbage endpoint-tail orbit
                    // overrode its correct covering segment at every proto re-create). The
                    // loop-aware ACCEPTANCE hint is still not threaded here -- the relaxation
                    // only applies to the no-covering-segment fallback (the SECOND branch and
                    // the create-path's terminal-orbit fallback below). See plan section 1.5.
                    if (TryResolveEndpointTailForMapPresence(
                            traj,
                            currentUT,
                            visibleSegment,
                            terminalMapPresenceRegion,
                            out OrbitSegment endpointTailSegment,
                            out _,
                            out endpointTailDetail,
                            loopMemberInWindow: loopMemberInWindow))
                    {
                        segment = endpointTailSegment;
                        resolvedSegment = segment;
                        return ReturnDecision(
                            TrackingStationGhostSource.EndpointTail,
                            skipReason,
                            CombineSourceDetails(string.Format(ic,
                                "endpointTailOverride=stale-visible-segment visibleSegmentBody={0} visibleSegmentUT={1:F1}-{2:F1}",
                                visibleSegment.bodyName ?? "(null)",
                                visibleSegment.startUT,
                                visibleSegment.endUT),
                                CombineSourceDetails(endpointTailDetail, checkpointFallbackDetail)));
                    }

                    return ReturnDecision(
                        TrackingStationGhostSource.Segment,
                        skipReason,
                        CombineSourceDetails(string.Format(ic,
                            "segmentBody={0} segmentUT={1:F1}-{2:F1}",
                            segment.bodyName ?? "(null)",
                            segment.startUT,
                            segment.endUT),
                            CombineSourceDetails(endpointTailDetail, checkpointFallbackDetail)));
                }
            }

            // Dense OrbitalCheckpoint frames are only safe as a map-presence
            // state-vector source when a caller explicitly marks the create/update
            // as recovery from an orbit-segment gap. Normal checkpoint windows keep
            // using segment or terminal-orbit sources so stale/wrong-frame checkpoint
            // data cannot reintroduce the #571 / #584 wrong-position class.
            if (TryResolveCheckpointStateVectorMapPoint(
                traj,
                currentUT,
                ref stateVectorCachedIndex,
                out stateVectorPoint,
                out _,
                out TrackSection fallbackCheckpointSection))
            {
                OrbitalCheckpointStateVectorFallbackDecision checkpointDecision =
                    EvaluateOrbitalCheckpointStateVectorFallback(
                        traj,
                        currentUT,
                        stateVectorPoint,
                        segmentSourceAvailable: false,
                        allowSoiGapRecovery: allowSoiGapStateVectorFallback,
                        expectedSoiGapBody: expectedSoiGapBody);
                string detail = string.Format(ic,
                    "stateVectorSource=OrbitalCheckpoint sectionUT={0:F1}-{1:F1} pointUT={2:F1} stateVectorBody={3} alt={4:F0} speed={5:F1} {6}",
                    fallbackCheckpointSection.startUT,
                    fallbackCheckpointSection.endUT,
                    stateVectorPoint.ut,
                    stateVectorPoint.bodyName ?? "(null)",
                    stateVectorPoint.altitude,
                    stateVectorPoint.velocity.magnitude,
                    FormatOrbitalCheckpointStateVectorFallbackDecision(checkpointDecision));

                if (checkpointDecision.Accepted)
                {
                    resolvedStatePoint = stateVectorPoint;
                    skipReason = checkpointDecision.Reason;
                    return ReturnDecision(
                        TrackingStationGhostSource.StateVectorSoiGap,
                        checkpointDecision.Reason,
                        detail);
                }

                checkpointFallbackRejectReason = checkpointDecision.Reason;
                checkpointFallbackDetail = detail;
            }

            string stateVectorSkipReason = null;
            // #583: try state-vector resolution when there are no orbit segments
            // (the original physics-only-suborbital case) OR when the current UT
            // sits inside a Relative-frame section. The latter widens the gate
            // so a recording with OrbitalCheckpoint segments elsewhere can still
            // get a map ghost while the playback head is in the docking /
            // rendezvous section; CreateGhostVesselFromStateVectors handles
            // Relative world-position resolution against the recorded anchor
            // pose for the target UT.
            bool inRelativeFrame = IsInRelativeFrame(traj, currentUT);
            bool hasPastOrbitSegment = false;
            bool hasFutureOrbitSegment = false;
            if (traj.HasOrbitSegments && inRelativeFrame && traj.OrbitSegments != null)
            {
                for (int i = 0; i < traj.OrbitSegments.Count; i++)
                {
                    OrbitSegment candidate = traj.OrbitSegments[i];
                    if (candidate.endUT < currentUT)
                        hasPastOrbitSegment = true;
                    if (candidate.startUT > currentUT)
                        hasFutureOrbitSegment = true;
                    if (hasPastOrbitSegment && hasFutureOrbitSegment)
                        break;
                }
            }

            bool deferRelativeStateVectorForSegmentGap =
                traj.HasOrbitSegments
                && inRelativeFrame
                && hasPastOrbitSegment
                && hasFutureOrbitSegment;
            if (deferRelativeStateVectorForSegmentGap)
            {
                // A Relative section with pending bounded orbit coverage should not
                // create a no-bounds state-vector ProtoVessel. During rewind/map
                // time warp that briefly exposed stock's full proto-orbit instead
                // of waiting for the next Parsek-bounded segment. The past+future
                // bracket is intentional: future-only Relative windows still use
                // the #583 state-vector path until a recorded segment gap exists.
                stateVectorSkipReason = TrackingStationGhostSkipRelativeStateVectorSegmentGap;
            }
            string relativeSegmentGapDetail = deferRelativeStateVectorForSegmentGap
                ? "relativeFrame=True orbitSegmentGap=True"
                : null;

            bool considerStateVector =
                !traj.HasOrbitSegments
                || (inRelativeFrame && !deferRelativeStateVectorForSegmentGap);
            if (considerStateVector)
            {
                if (TryResolveStateVectorMapPoint(
                    traj,
                    currentUT,
                    ref stateVectorCachedIndex,
                    out stateVectorPoint,
                    out stateVectorSkipReason))
                {
                    resolvedStatePoint = stateVectorPoint;
                    return ReturnDecision(
                        TrackingStationGhostSource.StateVector,
                        skipReason,
                        string.Format(ic,
                            "stateVectorBody={0} alt={1:F0} speed={2:F1}",
                            stateVectorPoint.bodyName ?? "(null)",
                            stateVectorPoint.altitude,
                            stateVectorPoint.velocity.magnitude));
                }
            }

            if (!allowTerminalOrbitFallback)
            {
                skipReason = checkpointFallbackRejectReason
                    ?? ResolveStateVectorOrSegmentSkipReason(
                        traj,
                        considerStateVector || deferRelativeStateVectorForSegmentGap,
                        stateVectorSkipReason);
                string detail = string.Format(ic,
                    "terminalFallback=False hasOrbitSegments={0}",
                    traj.HasOrbitSegments);
                detail = CombineSourceDetails(detail, relativeSegmentGapDetail);
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    CombineSourceDetails(detail, checkpointFallbackDetail));
            }

            if (!HasOrbitData(traj))
            {
                skipReason = checkpointFallbackRejectReason
                    ?? ResolveStateVectorOrSegmentSkipReason(
                        traj,
                        considerStateVector || deferRelativeStateVectorForSegmentGap,
                        stateVectorSkipReason);
                string detail = string.Format(ic, "hasOrbitSegments={0}", traj.HasOrbitSegments);
                detail = CombineSourceDetails(detail, relativeSegmentGapDetail);
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    CombineSourceDetails(detail, checkpointFallbackDetail));
            }

            if (!IsTerminalStateEligibleForTerminalOrbitMapPresence(terminal))
            {
                skipReason = "terminal-" + terminal.Value;
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    CombineSourceDetails(
                        string.Format(ic, "terminal={0} terminalOrbitFallback=True", terminal.Value),
                        checkpointFallbackDetail));
            }

            if (currentUT < activationStartUT)
            {
                skipReason = checkpointFallbackRejectReason ?? "before-activation";
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    CombineSourceDetails(
                        string.Format(ic, "activationStartUT={0:F1}", activationStartUT),
                        checkpointFallbackDetail));
            }

            bool allowSparseOrbitGapFallback =
                traj.HasOrbitSegments
                && currentUT < traj.EndUT
                && !HasRecordedTrackCoverageAtUT(traj, currentUT);
            if (currentUT < traj.EndUT && !allowSparseOrbitGapFallback)
            {
                skipReason = checkpointFallbackRejectReason
                    ?? (deferRelativeStateVectorForSegmentGap
                        ? stateVectorSkipReason
                        : "before-terminal-orbit");
                string detail = string.Format(ic, "endUT={0:F1}", traj.EndUT);
                detail = CombineSourceDetails(detail, relativeSegmentGapDetail);
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    CombineSourceDetails(detail, checkpointFallbackDetail));
            }

            // Create-path no-covering-segment terminal-orbit fallback. The
            // acceptTerminalOrbitForLoopSynthesis hint propagates here so a loop-aware
            // caller (non-zero loop epoch shift on a same-body terminal recording) can
            // synthesise a terminal orbit even when the recording has no recorded
            // OrbitSegments and its persisted endpoint phase resolved to TrajectoryPoint
            // (the no-segment terminal-Orbiting case). Default false preserves
            // byte-identical behaviour for every existing call site that does not opt
            // in. See plan section 1.4 and section 1.5.
            if (TryResolveEndpointTailForMapPresence(
                    traj,
                    currentUT,
                    selectedSegment: null,
                    terminalMapPresenceRegion: terminalMapPresenceRegion,
                    out OrbitSegment terminalEndpointTailSegment,
                    out _,
                    out string terminalEndpointTailDetail,
                    acceptTerminalOrbitSource: acceptTerminalOrbitForLoopSynthesis))
            {
                segment = terminalEndpointTailSegment;
                resolvedSegment = segment;
                return ReturnDecision(
                    TrackingStationGhostSource.EndpointTail,
                    skipReason,
                    CombineSourceDetails(terminalEndpointTailDetail, checkpointFallbackDetail));
            }

            if (!TryResolveGhostProtoOrbitSeed(
                traj,
                out double inclination,
                out double eccentricity,
                out double semiMajorAxis,
                out double lan,
                out double argumentOfPeriapsis,
                out double meanAnomalyAtEpoch,
                out double epoch,
                out string seedBodyName,
                out GhostProtoOrbitSeedDiagnostics seedDiagnostics))
            {
                PopulateTailSeedDiagnostics(traj, currentUT, ref seedDiagnostics);
                skipReason = seedDiagnostics.FailureReason == TrackingStationGhostSkipEndpointConflict
                    ? TrackingStationGhostSkipEndpointConflict
                    : TrackingStationGhostSkipUnseedableTerminalOrbit;
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    CombineSourceDetails(string.Format(ic,
                        "terminalBody={0} endUT={1:F1} seedFailure={2} endpointBody={3} {4}",
                        traj.TerminalOrbitBody ?? "(null)",
                        traj.EndUT,
                        seedDiagnostics.FailureReason ?? "(none)",
                        seedDiagnostics.EndpointBodyName ?? "(none)",
                        FormatGhostProtoOrbitSeedDiagnostics(seedDiagnostics)),
                        checkpointFallbackDetail));
            }
            PopulateTailSeedDiagnostics(traj, currentUT, ref seedDiagnostics);

            segment = new OrbitSegment
            {
                startUT = activationStartUT,
                endUT = traj.EndUT,
                inclination = inclination,
                eccentricity = eccentricity,
                semiMajorAxis = semiMajorAxis,
                longitudeOfAscendingNode = lan,
                argumentOfPeriapsis = argumentOfPeriapsis,
                meanAnomalyAtEpoch = meanAnomalyAtEpoch,
                epoch = epoch,
                bodyName = seedBodyName
            };
            resolvedSegment = segment;

            return ReturnDecision(
                TrackingStationGhostSource.TerminalOrbit,
                skipReason,
                CombineSourceDetails(string.Format(ic,
                    "terminalBody={0} endUT={1:F1} seedBody={2} seedSource={3} endpointBody={4} seedFallback={5} {6}",
                    traj.TerminalOrbitBody ?? "(null)",
                    traj.EndUT,
                    seedBodyName ?? "(null)",
                    seedDiagnostics.Source ?? "(none)",
                    seedDiagnostics.EndpointBodyName ?? "(none)",
                    seedDiagnostics.FallbackReason ?? "(none)",
                    FormatGhostProtoOrbitSeedDiagnostics(seedDiagnostics)),
                    checkpointFallbackDetail));
        }

        private static string BuildTrackingStationGhostSourceDetail(
            Recording rec,
            double currentUT,
            TrackingStationGhostSource source,
            string skipReason,
            OrbitSegment segment,
            TrajectoryPoint stateVectorPoint)
        {
            if (rec == null)
                return "null recording sourceKind=None rec=(null) body=(none) sourceUT=(none) world=(none)";

            string structuredDetail = BuildGhostSourceStructuredDetail(
                rec,
                currentUT,
                source,
                segment,
                stateVectorPoint);
            string WithStructured(string detail)
            {
                return CombineSourceDetails(detail, structuredDetail);
            }

            switch (source)
            {
                case TrackingStationGhostSource.Segment:
                case TrackingStationGhostSource.EndpointTail:
                    return WithStructured(CombineSourceDetails(
                        string.Format(ic,
                            "segmentBody={0} segmentUT={1:F1}-{2:F1}",
                            segment.bodyName ?? "(null)",
                            segment.startUT,
                            segment.endUT),
                        BuildEndpointTailTrackingStationDetail(
                            rec,
                            currentUT,
                            source,
                            segment)));

                case TrackingStationGhostSource.StateVector:
                case TrackingStationGhostSource.StateVectorSoiGap:
                    return WithStructured(string.Format(ic,
                        "stateVectorBody={0} alt={1:F0} speed={2:F1}",
                        stateVectorPoint.bodyName ?? "(null)",
                        stateVectorPoint.altitude,
                        stateVectorPoint.velocity.magnitude));

                case TrackingStationGhostSource.TerminalOrbit:
                    if (TryResolveGhostProtoOrbitSeed(
                        rec,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out string seedBodyName,
                        out GhostProtoOrbitSeedDiagnostics seedDiagnostics))
                    {
                        PopulateTailSeedDiagnostics(rec, currentUT, ref seedDiagnostics);
                        return WithStructured(string.Format(ic,
                            "terminalBody={0} endUT={1:F1} seedBody={2} seedSource={3} endpointBody={4} seedFallback={5} {6}",
                            rec.TerminalOrbitBody ?? "(null)",
                            rec.EndUT,
                            seedBodyName ?? "(null)",
                            seedDiagnostics.Source ?? "(none)",
                            seedDiagnostics.EndpointBodyName ?? "(none)",
                            seedDiagnostics.FallbackReason ?? "(none)",
                            FormatGhostProtoOrbitSeedDiagnostics(seedDiagnostics)));
                    }
                    break;
            }

            switch (skipReason)
            {
                case "debris":
                    return WithStructured("isDebris=True");
                case TrackingStationGhostSkipSuppressed:
                    return WithStructured("isSuppressed=True");
                case TrackingStationGhostSkipAlreadySpawned:
                    return WithStructured("already spawned");
                case "before-activation":
                    return WithStructured(string.Format(ic, "activationStartUT={0:F1}",
                        PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(rec)));
                case "before-terminal-orbit":
                    return WithStructured(string.Format(ic, "endUT={0:F1}", rec.EndUT));
                case TrackingStationGhostSkipEndpointConflict:
                case TrackingStationGhostSkipUnseedableTerminalOrbit:
                    TryResolveGhostProtoOrbitSeed(
                        rec,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out GhostProtoOrbitSeedDiagnostics seedDiagnostics);
                    PopulateTailSeedDiagnostics(rec, currentUT, ref seedDiagnostics);
                    return WithStructured(string.Format(ic,
                        "terminalBody={0} endUT={1:F1} seedFailure={2} endpointBody={3} {4}",
                        rec.TerminalOrbitBody ?? "(null)",
                        rec.EndUT,
                        seedDiagnostics.FailureReason ?? "(none)",
                        seedDiagnostics.EndpointBodyName ?? "(none)",
                        FormatGhostProtoOrbitSeedDiagnostics(seedDiagnostics)));
            }

            if (skipReason != null && skipReason.StartsWith("terminal"))
                return WithStructured(string.Format(ic, "terminal={0}",
                    rec.TerminalStateValue.HasValue ? rec.TerminalStateValue.Value.ToString() : "(none)"));

            if (skipReason == "no-current-segment" || skipReason == "no-orbit-data")
                return WithStructured(string.Format(ic, "hasOrbitSegments={0}", rec.HasOrbitSegments));

            if (skipReason == TrackingStationGhostSkipStateVectorThreshold)
                return WithStructured(string.Format(ic, "hasOrbitSegments={0} stateVectorThreshold=True", rec.HasOrbitSegments));

            if (skipReason == TrackingStationGhostSkipRelativeFrame)
                return WithStructured("relativeFrame=True");

            if (skipReason == TrackingStationGhostSkipRelativeStateVectorSegmentGap)
                return WithStructured(string.Format(ic,
                    "relativeFrame=True orbitSegmentGap=True endUT={0:F1}",
                    rec.EndUT));

            if (skipReason == "no-state-vector-point")
                return WithStructured(string.Format(ic, "stateVectorPointMissing=True hasOrbitSegments={0}", rec.HasOrbitSegments));

            return structuredDetail;
        }

        private static string CombineSourceDetails(string detail, string structuredDetail)
        {
            if (string.IsNullOrEmpty(detail))
                return structuredDetail;
            if (string.IsNullOrEmpty(structuredDetail))
                return detail;
            return detail + " " + structuredDetail;
        }

        private static string BuildGhostSourceStructuredDetail(
            IPlaybackTrajectory traj,
            double currentUT,
            TrackingStationGhostSource source,
            OrbitSegment segment,
            TrajectoryPoint stateVectorPoint)
        {
            string recId = traj?.RecordingId ?? "(null)";
            switch (source)
            {
                case TrackingStationGhostSource.Segment:
                    return BuildOrbitSourceStructuredDetail("Segment", recId, currentUT, segment);

                case TrackingStationGhostSource.TerminalOrbit:
                    return BuildOrbitSourceStructuredDetail("TerminalOrbit", recId, currentUT, segment);

                case TrackingStationGhostSource.EndpointTail:
                    return BuildOrbitSourceStructuredDetail("EndpointTail", recId, currentUT, segment);

                case TrackingStationGhostSource.StateVector:
                    return BuildStateVectorSourceStructuredDetail("StateVector", recId, stateVectorPoint);

                case TrackingStationGhostSource.StateVectorSoiGap:
                    return BuildStateVectorSourceStructuredDetail("SoiGapStateVector", recId, stateVectorPoint);

                default:
                    return string.Format(ic,
                        "sourceKind=None rec={0} body=(none) sourceUT=(none) world=(none)",
                        recId);
            }
        }

        private static string BuildOrbitSourceStructuredDetail(
            string sourceKind,
            string recId,
            double currentUT,
            OrbitSegment segment)
        {
            string world = TryResolveOrbitWorldPosition(segment, currentUT, out Vector3d worldPos)
                ? FormatWorldPosition(worldPos)
                : "(unresolved)";
            return string.Format(ic,
                "sourceKind={0} rec={1} body={2} sourceUT={3:F1}-{4:F1} epoch={5:F1} sma={6:F0} ecc={7:F6} world={8}",
                sourceKind,
                recId,
                segment.bodyName ?? "(null)",
                segment.startUT,
                segment.endUT,
                segment.epoch,
                segment.semiMajorAxis,
                segment.eccentricity,
                world);
        }

        private static string BuildStateVectorSourceStructuredDetail(
            string sourceKind,
            string recId,
            TrajectoryPoint point)
        {
            string world = TryResolvePointWorldPosition(point, out Vector3d worldPos)
                ? FormatWorldPosition(worldPos)
                : "(unresolved)";
            return string.Format(ic,
                "sourceKind={0} rec={1} body={2} sourceUT={3:F1} pointUT={4:F1} alt={5:F0} speed={6:F1} world={7}",
                sourceKind,
                recId,
                point.bodyName ?? "(null)",
                point.ut,
                point.ut,
                point.altitude,
                point.velocity.magnitude,
                world);
        }

        private static bool TryResolveOrbitWorldPosition(
            OrbitSegment segment,
            double currentUT,
            out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            try
            {
                CelestialBody body = FindBodyByName(segment.bodyName);
                if (body == null)
                    return false;

                Orbit orbit = new Orbit(
                    segment.inclination,
                    segment.eccentricity,
                    segment.semiMajorAxis,
                    segment.longitudeOfAscendingNode,
                    segment.argumentOfPeriapsis,
                    segment.meanAnomalyAtEpoch,
                    segment.epoch,
                    body);
                worldPos = orbit.getPositionAtUT(currentUT);
                return IsFinite(worldPos);
            }
            catch (Exception)
            {
                worldPos = Vector3d.zero;
                return false;
            }
        }

        private static bool TryResolvePointWorldPosition(
            TrajectoryPoint point,
            out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            try
            {
                CelestialBody body = FindBodyByName(point.bodyName);
                if (body == null)
                    return false;

                worldPos = body.GetWorldSurfacePosition(
                    point.latitude,
                    point.longitude,
                    point.altitude);
                return IsFinite(worldPos);
            }
            catch (Exception)
            {
                worldPos = Vector3d.zero;
                return false;
            }
        }

        private static string FormatWorldPosition(Vector3d value)
        {
            return string.Format(ic,
                "({0:F1},{1:F1},{2:F1})",
                value.x,
                value.y,
                value.z);
        }

        private static bool IsFinite(Vector3d value)
        {
            return !(double.IsNaN(value.x) || double.IsNaN(value.y) || double.IsNaN(value.z)
                || double.IsInfinity(value.x) || double.IsInfinity(value.y) || double.IsInfinity(value.z));
        }

        private static bool IsFinite(double value)
        {
            return !(double.IsNaN(value) || double.IsInfinity(value));
        }

        private static string NormalizeStateVectorSkipReasonForNoOrbit(string stateVectorSkipReason)
        {
            return string.IsNullOrEmpty(stateVectorSkipReason)
                || stateVectorSkipReason == "no-points"
                    ? "no-orbit-data"
                    : stateVectorSkipReason;
        }

        private static bool TryResolveCheckpointStateVectorMapPoint(
            IPlaybackTrajectory traj,
            double currentUT,
            ref int cachedIndex,
            out TrajectoryPoint point,
            out string skipReason,
            out TrackSection section)
        {
            point = default(TrajectoryPoint);
            skipReason = null;
            section = default(TrackSection);

            if (traj?.TrackSections == null || traj.TrackSections.Count == 0)
            {
                skipReason = "no-track-sections";
                return false;
            }

            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, currentUT);
            if (sectionIdx < 0)
            {
                skipReason = "no-current-section";
                return false;
            }

            section = traj.TrackSections[sectionIdx];
            if (section.referenceFrame != ReferenceFrame.OrbitalCheckpoint)
            {
                skipReason = "not-orbital-checkpoint";
                return false;
            }

            if (section.frames == null || section.frames.Count == 0)
            {
                skipReason = "no-checkpoint-points";
                return false;
            }

            TrajectoryPoint? pt = TrajectoryMath.BracketPointAtUT(
                section.frames,
                currentUT,
                ref cachedIndex);
            if (!pt.HasValue)
            {
                skipReason = "no-checkpoint-state-vector-point";
                return false;
            }

            point = pt.Value;
            return true;
        }

        // #583: when state-vector resolution was attempted (either because
        // there are no orbit segments OR because we're in a Relative section)
        // and produced a meaningful skip reason — relative-frame /
        // relative-anchor-unresolved / relative-state-vector-segment-gap /
        // state-vector-threshold — surface that reason. Otherwise fall back
        // to the legacy split between
        // `no-current-segment` (orbit-bearing recording) and `no-orbit-data`
        // (state-vector-only recording with no points). Preserves the prior
        // log-shape contract for callers that don't reach the new Relative
        // gate while making the new defer-and-retry skip reasons visible in
        // the structured source-resolve line.
        private static string ResolveStateVectorOrSegmentSkipReason(
            IPlaybackTrajectory traj,
            bool considerStateVector,
            string stateVectorSkipReason)
        {
            if (considerStateVector
                && !string.IsNullOrEmpty(stateVectorSkipReason)
                && stateVectorSkipReason != "no-points"
                && stateVectorSkipReason != "no-state-vector-point")
            {
                return stateVectorSkipReason;
            }
            return traj.HasOrbitSegments
                ? "no-current-segment"
                : NormalizeStateVectorSkipReasonForNoOrbit(stateVectorSkipReason);
        }

        private static bool TryResolveStateVectorMapPoint(
            IPlaybackTrajectory traj,
            double currentUT,
            ref int cachedIndex,
            out TrajectoryPoint point,
            out string skipReason)
        {
            return TryResolveStateVectorMapPointPure(
                traj,
                currentUT,
                ref cachedIndex,
                out point,
                out skipReason);
        }

        /// <summary>
        /// Resolve whether a state-vector trajectory point exists at the current
        /// UT and is suitable for ghost map creation. #583: when the current UT
        /// lies inside a Relative-frame section, the recorded
        /// <c>point.altitude</c> is the anchor-local dz offset (metres), not
        /// geographic altitude, so the create/remove altitude thresholds are
        /// meaningless and are skipped. v11 Relative sections must name an
        /// anchor recording; the later state-vector world-frame resolver owns
        /// recorded-pose resolution and skips unresolved chains without live
        /// vessel PID lookups.
        /// </summary>
        internal static bool TryResolveStateVectorMapPointPure(
            IPlaybackTrajectory traj,
            double currentUT,
            ref int cachedIndex,
            out TrajectoryPoint point,
            out string skipReason)
        {
            point = default(TrajectoryPoint);
            skipReason = null;

            if (traj.Points == null || traj.Points.Count == 0)
            {
                skipReason = "no-points";
                return false;
            }

            double activationStartUT = PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(traj);
            if (currentUT < activationStartUT || currentUT > traj.EndUT)
            {
                skipReason = "no-state-vector-point";
                return false;
            }

            // Resolve the section covering currentUT once; the Relative branch
            // needs the reference-frame metadata, the Absolute branch needs
            // nothing more.
            TrackSection? currentSection = null;
            if (traj.TrackSections != null && traj.TrackSections.Count > 0)
            {
                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, currentUT);
                if (sectionIdx >= 0 && sectionIdx < traj.TrackSections.Count)
                    currentSection = traj.TrackSections[sectionIdx];
            }

            bool inRelative = currentSection.HasValue
                && currentSection.Value.referenceFrame == ReferenceFrame.Relative;

            if (inRelative)
            {
                if (ShouldUseBodyFixedPrimaryForParentAnchoredDebris(
                        traj,
                        currentSection.Value,
                        currentUT))
                {
                    if (!TrySelectBodyFixedPrimaryStateVectorPoint(
                            traj,
                            currentSection.Value,
                            currentUT,
                            out TrajectoryPoint bodyFixedPoint,
                            out skipReason))
                    {
                        return false;
                    }

                    double bodyFixedAtmosphereDepth = GetAtmosphereDepth(bodyFixedPoint.bodyName);
                    if (!ShouldCreateStateVectorOrbit(
                            bodyFixedPoint.altitude,
                            bodyFixedPoint.velocity.magnitude,
                            bodyFixedAtmosphereDepth))
                    {
                        skipReason = TrackingStationGhostSkipStateVectorThreshold;
                        return false;
                    }

                    point = bodyFixedPoint;
                    return true;
                }

                if (string.IsNullOrWhiteSpace(currentSection.Value.anchorRecordingId))
                {
                    skipReason = TrackingStationGhostSkipRelativeAnchorUnresolved;
                    return false;
                }

                TrajectoryPoint? relPt = TrajectoryMath.BracketPointAtUT(traj.Points, currentUT, ref cachedIndex);
                if (!relPt.HasValue)
                {
                    skipReason = "no-state-vector-point";
                    return false;
                }

                // Threshold check is intentionally skipped: point.altitude is
                // the anchor-local dz (metres along the anchor's local z), not
                // geographic altitude. Symmetric to the PR #547 P1 update-path
                // gate in ParsekPlaybackPolicy.CheckPendingMapVessels (lines
                // 1042-1056) which skips ShouldRemoveStateVectorOrbit for the
                // same reason.
                point = relPt.Value;
                return true;
            }

            TrajectoryPoint? pt = TrajectoryMath.BracketPointAtUT(traj.Points, currentUT, ref cachedIndex);
            if (!pt.HasValue)
            {
                skipReason = "no-state-vector-point";
                return false;
            }

            double atmosphereDepth = GetAtmosphereDepth(pt.Value.bodyName);
            if (!ShouldCreateStateVectorOrbit(
                pt.Value.altitude,
                pt.Value.velocity.magnitude,
                atmosphereDepth))
            {
                skipReason = TrackingStationGhostSkipStateVectorThreshold;
                return false;
            }

            point = pt.Value;
            return true;
        }

        private static bool ShouldUseBodyFixedPrimaryForParentAnchoredDebris(
            IPlaybackTrajectory traj,
            TrackSection section,
            double playbackUT)
        {
            return section.referenceFrame == ReferenceFrame.Relative
                && DebrisRelativePlaybackPolicy.ShouldRetireOnRecordedParentAnchorMiss(traj)
                && !GhostPlaybackEngine.ShouldUseLoopAnchoredDebrisChain(traj, playbackUT);
        }

        internal static uint ResolveBodyFixedPrimaryAnchorPid(
            IPlaybackTrajectory traj,
            TrackSection section)
        {
            string anchorRecordingId = !string.IsNullOrWhiteSpace(section.anchorRecordingId)
                ? section.anchorRecordingId.Trim()
                : traj?.ParentAnchorRecordingId;
            if (string.IsNullOrWhiteSpace(anchorRecordingId))
                return 0u;

            if (TryFindRecordingByIdForBodyFixedAnchorPid(
                    anchorRecordingId,
                    out Recording anchorRecording))
            {
                return anchorRecording?.VesselPersistentId ?? 0u;
            }

            return 0u;
        }

        private static bool TryFindRecordingByIdForBodyFixedAnchorPid(
            string recordingId,
            out Recording recording)
        {
            // [ERS-exempt] Body-fixed-primary anchor pid lookup correlates a
            // section's stored anchorRecordingId / ParentAnchorRecordingId to a
            // VesselPersistentId for state-vector telemetry only. The walk is
            // string-id keyed (recording-id, not chain semantics) so ERS filtering
            // by supersede/visibility would mask the very anchor recording an
            // active Re-Fly provisional may need to resolve. Read-only; no ledger.
            recording = null;
            if (string.IsNullOrWhiteSpace(recordingId))
                return false;

            List<RecordingTree> committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    RecordingTree tree = committedTrees[i];
                    if (tree?.Recordings != null
                        && tree.Recordings.TryGetValue(recordingId, out recording)
                        && recording != null)
                    {
                        return true;
                    }
                }
            }

            RecordingTree pending = RecordingStore.HasPendingTree
                ? RecordingStore.PendingTree
                : null;
            if (pending?.Recordings != null
                && pending.Recordings.TryGetValue(recordingId, out recording)
                && recording != null)
            {
                return true;
            }

            IReadOnlyList<Recording> committedRecordings = RecordingStore.CommittedRecordings;
            if (committedRecordings != null)
            {
                for (int i = 0; i < committedRecordings.Count; i++)
                {
                    Recording candidate = committedRecordings[i];
                    if (candidate != null
                        && string.Equals(candidate.RecordingId, recordingId, StringComparison.Ordinal))
                    {
                        recording = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TrySelectBodyFixedPrimaryStateVectorPoint(
            IPlaybackTrajectory traj,
            TrackSection section,
            double playbackUT,
            out TrajectoryPoint point,
            out string skipReason)
        {
            point = default(TrajectoryPoint);
            skipReason = TrackingStationGhostSkipBodyFixedPrimaryUnavailable;
            if (!ParsekFlight.BodyFixedPrimaryCoversPlaybackUT(
                    section,
                    playbackUT,
                    out _,
                    out _))
            {
                if (DebrisRelativePlaybackPolicy.ShouldRetireOutsideAuthoredRelativeCoverage(
                        traj,
                        playbackUT,
                        out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic)
                    && !string.IsNullOrWhiteSpace(diagnostic.Reason))
                {
                    skipReason = diagnostic.Reason;
                }
                return false;
            }

            int bodyFixedIndex = 0;
            TrajectoryPoint? bodyFixedPoint = TrajectoryMath.BracketPointAtUT(
                section.bodyFixedFrames,
                playbackUT,
                ref bodyFixedIndex);
            if (!bodyFixedPoint.HasValue)
                return false;

            point = bodyFixedPoint.Value;
            skipReason = null;
            return true;
        }

        internal static Vessel CreateGhostVesselFromSource(
            int recordingIndex,
            IPlaybackTrajectory traj,
            TrackingStationGhostSource source,
            OrbitSegment segment,
            TrajectoryPoint stateVectorPoint,
            double currentUT,
            double loopEpochShiftSeconds = 0.0)
        {
            return CreateGhostVesselFromSource(
                recordingIndex,
                traj,
                source,
                segment,
                stateVectorPoint,
                currentUT,
                out _,
                loopEpochShiftSeconds);
        }

        /// <summary>
        /// Overload that propagates <paramref name="retryLater"/> from
        /// <see cref="CreateGhostVesselFromStateVectors(int, IPlaybackTrajectory,
        /// TrajectoryPoint, double, out bool, bool, string)"/>. Non-state-vector
        /// branches always set it false. Callers that maintain a pending-map
        /// queue use this overload to decide whether to drop the pending entry
        /// on null return or keep it for the next tick (PR #574 review P2:
        /// retry-later semantics for transient map ghost creation misses).
        /// </summary>
        internal static Vessel CreateGhostVesselFromSource(
            int recordingIndex,
            IPlaybackTrajectory traj,
            TrackingStationGhostSource source,
            OrbitSegment segment,
            TrajectoryPoint stateVectorPoint,
            double currentUT,
            out bool retryLater,
            double loopEpochShiftSeconds = 0.0)
        {
            retryLater = false;
            // Dispatcher-line: which sub-create is about to fire? Single line
            // gives the post-hoc reader the routing decision.
            var dispatch = NewDecisionFields("create-dispatch");
            dispatch.RecordingId = traj?.RecordingId;
            dispatch.RecordingIndex = recordingIndex;
            dispatch.VesselName = traj?.VesselName;
            dispatch.Source = source.ToString();
            dispatch.Branch = "(n/a)";
            dispatch.UT = currentUT;
            switch (source)
            {
                case TrackingStationGhostSource.Segment:
                case TrackingStationGhostSource.EndpointTail:
                    dispatch.Body = segment.bodyName;
                    dispatch.Segment = segment;
                    break;
                case TrackingStationGhostSource.StateVector:
                case TrackingStationGhostSource.StateVectorSoiGap:
                    dispatch.Body = stateVectorPoint.bodyName;
                    dispatch.StateVecAlt = stateVectorPoint.altitude;
                    dispatch.StateVecSpeed = stateVectorPoint.velocity.magnitude;
                    if (source == TrackingStationGhostSource.StateVectorSoiGap)
                        dispatch.Reason = SoiGapStateVectorFallbackReason;
                    break;
                case TrackingStationGhostSource.TerminalOrbit:
                    dispatch.Body = traj?.TerminalOrbitBody;
                    dispatch.TerminalBody = traj?.TerminalOrbitBody;
                    dispatch.TerminalSma = traj?.TerminalOrbitSemiMajorAxis ?? double.NaN;
                    dispatch.TerminalEcc = traj?.TerminalOrbitEccentricity ?? double.NaN;
                    break;
            }
            ParsekLog.Verbose(Tag, BuildGhostMapDecisionLine(dispatch));

            switch (source)
            {
                case TrackingStationGhostSource.TerminalOrbit:
                    return CreateGhostVesselForRecording(recordingIndex, traj);

                case TrackingStationGhostSource.Segment:
                    Vessel segmentGhost = CreateGhostVesselFromSegment(
                        recordingIndex, traj, segment, loopEpochShiftSeconds);
                    if (segmentGhost != null)
                    {
                        UpdateGhostOrbitForRecording(
                            recordingIndex,
                            segment,
                            loopEpochShiftSeconds: loopEpochShiftSeconds);
                        ForceImmediateIconDrive(segmentGhost, "create-segment");
                    }
                    return segmentGhost;

                case TrackingStationGhostSource.EndpointTail:
                    Vessel endpointTailGhost = CreateGhostVesselFromSegment(
                        recordingIndex,
                        traj,
                        segment,
                        TrackingStationGhostSource.EndpointTail,
                        loopEpochShiftSeconds);
                    if (endpointTailGhost != null)
                    {
                        UpdateGhostOrbitForRecording(
                            recordingIndex,
                            segment,
                            TrackingStationGhostSource.EndpointTail,
                            loopEpochShiftSeconds: loopEpochShiftSeconds);
                        ForceImmediateIconDrive(endpointTailGhost, "create-endpoint-tail");
                    }
                    return endpointTailGhost;

                case TrackingStationGhostSource.StateVector:
                case TrackingStationGhostSource.StateVectorSoiGap:
                    return CreateGhostVesselFromStateVectors(
                        recordingIndex,
                        traj,
                        stateVectorPoint,
                        currentUT,
                        out retryLater,
                        allowOrbitalCheckpointStateVector: source == TrackingStationGhostSource.StateVectorSoiGap,
                        stateVectorCreateReason: source == TrackingStationGhostSource.StateVectorSoiGap
                            ? SoiGapStateVectorFallbackReason
                            : null);

                default:
                    return null;
            }
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
                // committed index is a loop-unit member. Inert (effUT == currentUT, renderHidden
                // false) for every non-member and when loopUnits is Empty.
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

        // Resolves the OrbitSegment list the map orbit line / icon should follow for a committed
        // recording this frame: for a re-aim loop unit member that carries a heliocentric leg, the
        // per-window list with that leg RE-AIMED; otherwise the recorded segments. Applies to ANY member
        // of a re-aim unit (the resolver's heliocentric pre-check leaves launch / arrival / debris
        // members on the faithful path, since only the inertial-fixed heliocentric leg needs re-aiming).
        // The window is mapped from the LIVE <paramref name="liveCurrentUT"/> via the shared resolver
        // (the SAME window the flight engine uses), and the returned list is in recorded-span time, so
        // the caller searches it at the recorded-span effUT exactly as it would the recorded list.
        // Recording overload (tracking-station path).
        internal static List<OrbitSegment> ResolveEffectiveMapOrbitSegments(
            int committedIndex, Recording rec, double liveCurrentUT,
            GhostPlaybackLogic.LoopUnitSet loopUnits)
            => ResolveEffectiveMapOrbitSegments(
                committedIndex,
                rec != null ? rec.RecordingId : null,
                rec != null ? rec.OrbitSegments : null,
                liveCurrentUT,
                loopUnits);

        // Raw-pieces overload so the FLIGHT map path (ParsekPlaybackPolicy, which holds an
        // IPlaybackTrajectory, not a Recording) resolves the SAME re-aimed window list the tracking
        // station does. Both flight create + per-frame refresh and the tracking-station refresh must
        // draw the re-aimed transfer; the flight path previously read the recorded segments directly,
        // so its map orbit line pointed at the target's RECORDED position (wrong place in the target's
        // orbit). Returns the recorded list unchanged for every non-re-aim member / faithful window.
        internal static List<OrbitSegment> ResolveEffectiveMapOrbitSegments(
            int committedIndex, string recordingId, List<OrbitSegment> recorded,
            double liveCurrentUT, GhostPlaybackLogic.LoopUnitSet loopUnits)
            => ResolveEffectiveMapOrbitSegments(
                committedIndex, recordingId, recorded, liveCurrentUT, loopUnits, out long _);

        // Window-exposing overload (Phase 8d re-aim Director wiring): forwards the resolver's synodic
        // WINDOW INDEX so the MapRender ShadowRenderDriver can key its chain cache on it (a window advance
        // changes the re-aimed geometry without changing the RECORDED OrbitSegments.Count, so the window is
        // the load-bearing cache discriminator). Reference-equality contract preserved exactly: a non-re-aim
        // member / declined window / pre-first-window returns the RECORDED list UNCHANGED (Same reference)
        // with windowIndex = -1 (the caller's ReferenceEquals(effective, recorded) test at :6260 and the
        // ShadowRenderDriver's chainHasReaimedSegments flag both rely on this). A re-aimed window returns the
        // synthesized list with windowIndex set to the resolver's cycle index.
        internal static List<OrbitSegment> ResolveEffectiveMapOrbitSegments(
            int committedIndex, string recordingId, List<OrbitSegment> recorded,
            double liveCurrentUT, GhostPlaybackLogic.LoopUnitSet loopUnits, out long windowIndex)
        {
            windowIndex = -1;
            if (string.IsNullOrEmpty(recordingId) || loopUnits == null)
                return recorded;
            if (!loopUnits.TryGetUnitForMember(committedIndex, out GhostPlaybackLogic.LoopUnit unit))
                return recorded;
            if (!unit.IsReaim)
                return recorded;
            if (Parsek.Reaim.ReaimPlaybackResolver.Shared.TryResolveWindowSegments(
                    recordingId, recorded, unit.ReaimPlan.Value, unit.ReaimSchedule.Value,
                    unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
                    liveCurrentUT, out List<OrbitSegment> reaimed, out long resolvedWindow))
            {
                windowIndex = resolvedWindow;
                LogMapEffectiveSegments(committedIndex, "RE-AIMED", reaimed, unit.ReaimPlan.Value.CommonAncestor);
                return reaimed;
            }
            LogMapEffectiveSegments(committedIndex, "RECORDED-resolver-miss", recorded, unit.ReaimPlan.Value.CommonAncestor);
            return recorded; // no heliocentric leg / window miss / pre-first-window -> faithful (windowIndex = -1)
        }

        // Re-aim covering-segment substitution for the FLIGHT create paths: when a Segment-source map
        // ghost is about to be created for a re-aim owner, swap the recorded covering segment for the
        // re-aimed one (transfer elements aimed at the target's CURRENT position, trimmed to the
        // interplanetary span). Returns FALSE (and the caller must NOT create the ghost) when the member
        // is a re-aim owner whose re-aimed list has NO covering segment at <paramref name="sampleUT"/> -
        // a TRIM GAP between a recorded body-relative leg (escape / capture) and the trimmed transfer, or
        // a window the resolver declined. Falling back to the recorded segment there (the old behavior)
        // re-created the ghost from the recorded sub-segments at random orbit positions while the
        // per-frame refresh kept removing it (the create/destroy icon flicker at the SOI boundaries).
        // Returns TRUE with the caller's recorded segment unchanged for a non-re-aim member / faithful
        // window (effective list reference-identical to the recorded one). <paramref name="liveCurrentUT"/>
        // maps the synodic window; <paramref name="sampleUT"/> is the recorded-span effUT searched at.
        internal static bool TryResolveReaimedCoveringSegment(
            int committedIndex, string recordingId, List<OrbitSegment> recorded,
            double liveCurrentUT, double sampleUT,
            GhostPlaybackLogic.LoopUnitSet loopUnits, OrbitSegment recordedSegment,
            out OrbitSegment segment)
        {
            List<OrbitSegment> effective = ResolveEffectiveMapOrbitSegments(
                committedIndex, recordingId, recorded, liveCurrentUT, loopUnits);
            if (effective == null || ReferenceEquals(effective, recorded))
            {
                segment = recordedSegment; // non-re-aim / faithful window: keep the recorded segment
                return true;
            }
            OrbitSegment? coveringReaimed = TrajectoryMath.FindOrbitSegmentForMapDisplay(effective, sampleUT);
            if (coveringReaimed.HasValue)
            {
                segment = coveringReaimed.Value;
                return true;
            }
            segment = recordedSegment;
            return false; // re-aim owner in a trim gap -> caller keeps the ghost hidden (no create)
        }

        // Diagnostic: log whether the map got the RE-AIMED or RECORDED segments, plus the heliocentric
        // (common-ancestor) leg's sma + count, so a map line still on the recorded transfer (wrong place in
        // the target's orbit) is caught. The re-aimed list has ONE synthesized common-ancestor segment; the
        // recorded list has the original (typically several). Verbose, rate-limited per member.
        private static void LogMapEffectiveSegments(int idx, string kind, List<OrbitSegment> segs, string ancestor)
        {
            if (segs == null)
                return;
            int helioCount = 0;
            double firstHelioSma = double.NaN;
            for (int i = 0; i < segs.Count; i++)
            {
                if (segs[i].bodyName == ancestor)
                {
                    if (helioCount == 0) firstHelioSma = segs[i].semiMajorAxis;
                    helioCount++;
                }
            }
            ParsekLog.VerboseRateLimited("ReaimSeam", "map-eff-" + idx.ToString(ic),
                string.Format(ic, "map effective segments: {0} member={1} segs={2} helioSegs={3} firstHelioSma={4:F0}",
                    kind, idx, segs.Count, helioCount, firstHelioSma), 2.0);
        }

        // MAP/TS TRAJECTORY DIAGNOSTIC: the covering OrbitSegment the map icon + orbit line follow for this
        // member THIS frame, logged ON CHANGE (keyed per member+scene) so the reader sees discrete transition
        // events rather than per-frame spam. This is the chokepoint that decides "inside Kerbin SOI" (a
        // Kerbin-bodied segment) vs "heliocentric" (the Sun-bodied transfer). It catches: the SOI-exit blink
        // (body flip-flops Kerbin<->Sun back and forth => rapid alternating lines), the orbit line going dark
        // (covered -> GAP(no-segment)), and the Segment<->StateVector route flip. A reader greps tag MapTraj
        // for one member to see the exact sequence of what the map drew. <paramref name="scene"/> = "TS" or
        // "FLIGHT". Pairs with the gated MapRenderProbe (which subsumed the old icon-pos-delta stall
        // diagnostic) and the existing "SOI change" orbit-line log.
        internal static void LogMapCoveringSegmentChange(
            string scene, int idx, double effUT, OrbitSegment? coveringSegment,
            bool segmentCoversEffUT, bool isStateVector, int effectiveSegmentCount)
        {
            string coveringBody = coveringSegment.HasValue
                ? (coveringSegment.Value.bodyName ?? "(null)")
                : "GAP(no-segment)";
            string source = isStateVector ? "StateVector" : "Segment";
            string segSpan = coveringSegment.HasValue
                ? string.Format(ic, "[{0:F1},{1:F1}] sma={2:F0} ecc={3:F3}",
                    coveringSegment.Value.startUT, coveringSegment.Value.endUT,
                    coveringSegment.Value.semiMajorAxis, coveringSegment.Value.eccentricity)
                : "n/a";
            // Include the covering segment's start UT (coarse, whole-second) in the on-change key so a
            // same-body segment->segment advance (e.g. several consecutive Kerbin-frame segments, or a
            // same-body carry stepping to the next segment) registers as a CHANGE. Without it the key
            // body|covered|source stays constant across same-body advances and the transition - the exact
            // thing this diagnostic exists to trace - would never log. Segment startUT is a fixed per-segment
            // value, so F0 introduces no float-jitter churn.
            string segKey = coveringSegment.HasValue
                ? coveringSegment.Value.startUT.ToString("F0", ic)
                : "none";
            ParsekLog.VerboseOnChange("MapTraj",
                string.Format(ic, "covering-{0}-{1}", scene, idx),
                string.Format(ic, "{0}|{1}|{2}|{3}", coveringBody, segmentCoversEffUT, source, segKey),
                string.Format(ic,
                    "{0} covering-segment CHANGED: member={1} effUT={2:F1} body={3} covered={4} source={5} " +
                    "effSegs={6} seg={7}",
                    scene, idx, effUT, coveringBody, segmentCoversEffUT, source, effectiveSegmentCount, segSpan));
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
                    // skip the threshold for Relative-frame points;
                    // UpdateGhostOrbitFromStateVectors already dispatches on
                    // referenceFrame and resolves world position via the
                    // anchor for that branch.
                    bool inRelativeFrame = IsInRelativeFrame(rec, effUT);
                    if (!inRelativeFrame)
                    {
                        double atmosphereDepth = GetAtmosphereDepth(pt.Value.bodyName);
                        if (ShouldRemoveStateVectorOrbit(
                            pt.Value.altitude,
                            pt.Value.velocity.magnitude,
                            atmosphereDepth))
                        {
                            if (toRemove == null) toRemove = new List<(int, string)>();
                            toRemove.Add((idx, "below-state-vector-threshold"));
                            continue;
                        }
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
                // is harmless and still runs; looping / mission-unit members (explicit live
                // opt-in) and the active re-fly session are exempt. Explicit "Warp to Spawn"
                // (TryRunTrackingStationSpawnHandoffForIndex / ForRecordingId) does not route
                // through here, so a user-initiated spawn is never gated. Mirrors the flight
                // and Space Center spawn gates.
                bool realVesselExists = rec.VesselPersistentId != 0
                    && GhostPlaybackLogic.RealVesselExistsForRecording(rec);
                bool wouldAdoptExistingVessel =
                    ShouldSkipTrackingStationDuplicateSpawn(rec, realVesselExists);
                if (!wouldAdoptExistingVessel
                    && SessionSuppressionState.ActiveMarker == null
                    && !rec.LoopPlayback
                    && !loopUnits.IsMember(i))
                {
                    double activationStartUT = GhostPlaybackEngine.ResolveGhostActivationStartUT(rec);
                    PlaybackScopeTracker.NotePlayhead(rec.RecordingId, currentUT, activationStartUT);
                    if (PlaybackScopeTracker.IsHistoricalNeverReplayed(
                            rec.RecordingId, currentUT, activationStartUT))
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

            bool preserveIdentity = ShouldPreserveIdentityForTrackingStationSpawn(
                chains,
                rec,
                realVesselExists);
            VesselSpawner.SpawnOrRecoverIfTooClose(rec, index, preserveIdentity);
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
                        "Tracking-station handoff resolved #{0} \"{1}\" without spawning a vessel (abandoned)",
                        index,
                        rec.VesselName ?? "(null)"));
            }
        }

        /// <summary>
        /// True when the map-view trajectory polyline is currently drawing the
        /// non-orbital leg of the recording that owns ghost vessel
        /// <paramref name="pid"/>. <c>GhostOrbitLinePatch</c> uses this to hide the
        /// proto-vessel orbit LINE (via <c>line.active = false</c>, keeping the
        /// renderer enabled so it re-shows automatically) while the polyline owns
        /// the phase, so the two visuals do not overlap and the orbit does not
        /// churn under warp. Maps pid to RecordingId via <see cref="vesselPidToRecordingId"/>
        /// and queries the polyline renderer's published ownership (8b.2: behind the
        /// director-drive gate the authoritative source is the treatment-published
        /// actual-draw set, falling back to the legacy autonomous-Driver set when the
        /// gate is off - see <c>GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg</c>).
        /// No-op when nothing draws (the sets are empty), so stock orbit behaviour is unchanged.
        /// </summary>
        internal static bool IsPolylineOwningGhostPhase(uint pid)
        {
            return vesselPidToRecordingId.TryGetValue(pid, out string recId)
                && Parsek.Display.GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg(recId);
        }

        /// <summary>
        /// Update orbit for a recording-index ghost when the ghost traverses orbit segments.
        /// </summary>
        internal static void UpdateGhostOrbitForRecording(
            int recordingIndex,
            OrbitSegment segment,
            TrackingStationGhostSource source = TrackingStationGhostSource.Segment,
            double loopEpochShiftSeconds = 0.0,
            List<OrbitSegment> effectiveOrbitSegments = null)
        {
            if (!vesselsByRecordingIndex.TryGetValue(recordingIndex, out Vessel vessel))
                return;
            ApplyOrbitToVessel(vessel, segment, string.Format(ic, "recording #{0}", recordingIndex),
                loopEpochShiftSeconds);
            // Diagnostic: which segment was actually applied to the map orbit (source + sma), and whether
            // the re-aimed list was threaded. A re-aim transfer should apply the synthesized sma; a
            // recorded sma here means the re-aimed segment was bypassed (e.g. a state-vector/checkpoint
            // source winning over the re-aimed OrbitSegment).
            ParsekLog.VerboseRateLimited("ReaimSeam", "map-apply-" + recordingIndex.ToString(ic),
                string.Format(ic, "map orbit applied: rec={0} source={1} body={2} segSma={3:F0} hasEffective={4}",
                    recordingIndex, source, segment.bodyName ?? "(null)", segment.semiMajorAxis,
                    effectiveOrbitSegments != null), 2.0);
            lifecycleUpdatedThisTick++;

            // Cache body-frame bounds (consecutive same-body OrbitSegments around this
            // segment, shifted into the live frame) so GhostOrbitLinePatch can keep
            // line.active True across inter-segment burns in one body frame. Resolved
            // from the recording's raw OrbitSegments at the segment's raw start UT, then
            // shifted by the same loop epoch offset applied to ghostOrbitBounds above,
            // keeping the loop-shifted edge cases identical to per-segment arc clipping.
            var committed = RecordingStore.CommittedRecordings;
            // Re-aim: a re-aim owner's body-frame bounds must come from the SAME re-aimed segment list
            // the orbit was applied from (effectiveOrbitSegments), not the recorded segments, so the
            // orbit-line draw window matches the re-aimed arc. Null for non-re-aim => recorded segments.
            List<OrbitSegment> bodyFrameSource = effectiveOrbitSegments
                ?? (committed != null && recordingIndex >= 0 && recordingIndex < committed.Count
                    && committed[recordingIndex] != null
                    ? committed[recordingIndex].OrbitSegments
                    : null);
            if (bodyFrameSource != null
                && bodyFrameSource.Count > 0
                && TrajectoryMath.TryGetBodyFrameBoundsForMapDisplay(
                    bodyFrameSource,
                    // Probe just inside the active segment so the resolver always finds
                    // it (endUT is exclusive for non-last segments).
                    segment.startUT,
                    out double bfStart,
                    out double bfEnd,
                    out string bfBody,
                    out int bfFirstIdx,
                    out int bfLastIdx))
            {
                ghostBodyFrameOrbitBounds[vessel.persistentId] =
                    (bfStart + loopEpochShiftSeconds, bfEnd + loopEpochShiftSeconds);
                ParsekLog.VerboseOnChange(Tag,
                    string.Format(ic, "body-frame-cache|{0}", vessel.persistentId),
                    string.Format(ic, "{0}|{1:F3}-{2:F3}|shift={3:F3}",
                        bfBody ?? "(null)", bfStart, bfEnd, loopEpochShiftSeconds),
                    string.Format(ic,
                        "Cached body-frame bounds pid={0} recIndex={1} body={2} " +
                        "rawBodyFrameUT={3:F2}-{4:F2} segIndices={5}-{6} loopShift={7:F2}",
                        vessel.persistentId,
                        recordingIndex,
                        bfBody ?? "(null)",
                        bfStart,
                        bfEnd,
                        bfFirstIdx,
                        bfLastIdx,
                        loopEpochShiftSeconds));
            }
            else
            {
                // Fallback: the resolver couldn't seed (e.g., orbit-segments missing in
                // a degenerate test scenario, or an EndpointTail synthetic segment whose
                // startUT is not in the committed OrbitSegments). Use the single-segment
                // bounds so the patch still has something to gate on rather than tearing
                // the line down. Same key as the success branch so the cache write
                // transitions visibly between body-frame and single-segment fallback.
                ghostBodyFrameOrbitBounds[vessel.persistentId] =
                    (segment.startUT + loopEpochShiftSeconds, segment.endUT + loopEpochShiftSeconds);
                ParsekLog.VerboseOnChange(Tag,
                    string.Format(ic, "body-frame-cache|{0}", vessel.persistentId),
                    string.Format(ic, "fallback|{0:F3}-{1:F3}|shift={2:F3}",
                        segment.startUT, segment.endUT, loopEpochShiftSeconds),
                    string.Format(ic,
                        "Cached body-frame bounds pid={0} recIndex={1} source=single-segment-fallback " +
                        "rawSegmentUT={2:F2}-{3:F2} loopShift={4:F2}",
                        vessel.persistentId,
                        recordingIndex,
                        segment.startUT,
                        segment.endUT,
                        loopEpochShiftSeconds));
            }

            Vector3d worldPos = vessel.GetWorldPos3D();
            bool hasPrev = TryGetLastKnownFrame(recordingIndex, out var prev);
            string recId = hasPrev ? prev.RecordingId : null;
            string vesselName = prev.VesselName;
            string updateKey = string.Format(ic, "rec-orbit-update-{0}",
                recId ?? recordingIndex.ToString(ic));
            string sourceLabel = source == TrackingStationGhostSource.EndpointTail
                ? "EndpointTail"
                : "Segment";

            var done = NewDecisionFields("update-segment");
            done.RecordingId = recId;
            done.RecordingIndex = recordingIndex;
            done.VesselName = vesselName;
            done.Source = sourceLabel;
            done.Branch = "(n/a)";
            done.Body = segment.bodyName;
            done.WorldPos = worldPos;
            done.GhostPid = vessel.persistentId;
            done.Segment = segment;
            ParsekLog.VerboseRateLimited(Tag, updateKey, BuildGhostMapDecisionLine(done), 5.0);

            // The per-frame icon-pos-delta "frozen icon" stall diagnostic that used to live here is
            // now subsumed by the gated MapRenderProbe (per-pid per-frame world-position jump tracking
            // + Tier-B body-orbit on-change truth), behind the mapRenderTracing setting. See
            // docs/dev/design-map-ts-render-tracer.md.

            // Refresh last-known so destroy can read the current orbit shape.
            StashLastKnownFrame(recordingIndex, new LastKnownGhostFrame
            {
                RecordingId = recId,
                VesselName = vesselName,
                GhostPid = vessel.persistentId,
                Source = sourceLabel,
                Branch = "(n/a)",
                Body = segment.bodyName,
                WorldPos = worldPos,
                AnchorPid = 0u,
                LastUT = segment.startUT
            });
        }

        /// <summary>
        /// Outcome of resolving a state-vector trajectory point to a world-space
        /// position. The reference frame of the originating <see cref="TrackSection"/>
        /// determines whether <c>point.latitude/longitude/altitude</c> are body-fixed
        /// surface coordinates or anchor-local XYZ offsets — the wrong interpretation
        /// silently places the ghost roughly at the body surface but at a horizontally
        /// meaningless lat/lon (#582 / #571 contributor). This struct centralises the
        /// branch so both call sites in <see cref="CreateGhostVesselFromStateVectors"/>
        /// and <see cref="UpdateGhostOrbitFromStateVectors"/> stay in sync.
        /// </summary>
        internal struct StateVectorWorldFrame
        {
            public bool Resolved;
            public Vector3d WorldPos;
            public string Branch;          // "absolute", "relative", "orbital-checkpoint", "no-section"
            public string FailureReason;   // null on success
            public uint AnchorPid;         // 0 unless Branch == "relative"
        }

        internal static bool ShouldRetryStateVectorCreationAfterResolutionMiss(
            StateVectorWorldFrame resolution)
        {
            return !resolution.Resolved
                && string.Equals(resolution.Branch, "relative", StringComparison.Ordinal)
                && string.Equals(resolution.FailureReason, "anchor-not-found", StringComparison.Ordinal);
        }

        /// <summary>
        /// Pure-static resolution: given a trajectory point, the originating section
        /// (or null), the body, and pre-resolved anchor data, return the world-space
        /// position the state-vector orbit should be seeded at. No KSP API calls —
        /// callers do the body / anchor lookups and pass results in. Pure for testability.
        /// </summary>
        internal static StateVectorWorldFrame ResolveStateVectorWorldPositionPure(
            TrajectoryPoint point,
            TrackSection? section,
            int recordingFormatVersion,
            Func<double, double, double, Vector3d> absoluteSurfaceLookup,
            bool anchorFound,
            Vector3d anchorWorldPos,
            Quaternion anchorWorldRot,
            uint anchorVesselId,
            bool allowOrbitalCheckpointStateVector = false,
            TrajectoryPoint? bodyFixedPrimaryPoint = null)
        {
            // Parent-anchored debris uses `bodyFixedFrames` as the primary
            // surface for ordinary debris. Callers pass the selected body-fixed
            // point here after deciding the recording is not a loop-anchored
            // chain. Resolved through the standard surface lookup it yields the
            // recorded world position directly. Returns Branch="body-fixed-primary"
            // so call-site logs and tests can distinguish it from Absolute.
            if (bodyFixedPrimaryPoint.HasValue)
            {
                TrajectoryPoint shadow = bodyFixedPrimaryPoint.Value;
                Vector3d pos = absoluteSurfaceLookup(shadow.latitude, shadow.longitude, shadow.altitude);
                return new StateVectorWorldFrame
                {
                    Resolved = true,
                    WorldPos = pos,
                    Branch = "body-fixed-primary",
                    FailureReason = null,
                    AnchorPid = anchorVesselId,
                };
            }
            // No track sections at all — fall back to the original Absolute interpretation.
            // This preserves behaviour for legacy / synthetic recordings that have not yet
            // been split into sections, where the lat/lon/alt fields are still surface coords.
            if (!section.HasValue)
            {
                Vector3d pos = absoluteSurfaceLookup(point.latitude, point.longitude, point.altitude);
                return new StateVectorWorldFrame
                {
                    Resolved = true,
                    WorldPos = pos,
                    Branch = "no-section",
                    FailureReason = null,
                    AnchorPid = 0
                };
            }

            ReferenceFrame frame = section.Value.referenceFrame;
            if (frame == ReferenceFrame.Absolute)
            {
                Vector3d pos = absoluteSurfaceLookup(point.latitude, point.longitude, point.altitude);
                return new StateVectorWorldFrame
                {
                    Resolved = true,
                    WorldPos = pos,
                    Branch = "absolute",
                    FailureReason = null,
                    AnchorPid = 0
                };
            }

            if (frame == ReferenceFrame.Relative)
            {
                if (!anchorFound)
                {
                    return new StateVectorWorldFrame
                    {
                        Resolved = false,
                        WorldPos = default(Vector3d),
                        Branch = "relative",
                        FailureReason = "anchor-not-found",
                        AnchorPid = anchorVesselId
                    };
                }

                // The lat/lon/alt fields are reused as anchor-local XYZ offsets in
                // RELATIVE sections (TrajectoryPoint.cs:13-15 docstring). Resolve via
                // the same canonical helper used by the flight-scene playback path.
                Vector3d worldPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                    anchorWorldPos,
                    anchorWorldRot,
                    point.latitude,
                    point.longitude,
                    point.altitude);

                return new StateVectorWorldFrame
                {
                    Resolved = true,
                    WorldPos = worldPos,
                    Branch = "relative",
                    FailureReason = null,
                    AnchorPid = anchorVesselId
                };
            }

            // OrbitalCheckpoint state-vector entry points are normally refused:
            // checkpoint sections are supposed to have segment/terminal-orbit
            // sources available, and stale checkpoint-frame interpretation can
            // reintroduce wrong-position bugs. The only caller that may opt in is
            // the explicit SOI/orbit-segment-gap recovery path after it has already
            // proven there is no safer source and the body/window match.
            if (allowOrbitalCheckpointStateVector)
            {
                Vector3d pos = absoluteSurfaceLookup(point.latitude, point.longitude, point.altitude);
                return new StateVectorWorldFrame
                {
                    Resolved = true,
                    WorldPos = pos,
                    Branch = "orbital-checkpoint",
                    FailureReason = null,
                    AnchorPid = 0
                };
            }

            return new StateVectorWorldFrame
            {
                Resolved = false,
                WorldPos = default(Vector3d),
                Branch = "orbital-checkpoint",
                FailureReason = "state-vector-from-orbital-checkpoint",
                AnchorPid = 0
            };
        }

        /// <summary>
        /// KSP-dependent wrapper over <see cref="ResolveStateVectorWorldPositionPure"/>.
        /// Looks up the section/body and resolves recorded Relative anchors
        /// through <see cref="RecordedRelativeAnchorPoseResolver"/>, then
        /// delegates to the pure helper.
        /// </summary>
        private static StateVectorWorldFrame ResolveStateVectorWorldPosition(
            IPlaybackTrajectory traj,
            TrajectoryPoint point,
            CelestialBody body,
            bool allowOrbitalCheckpointStateVector = false)
        {
            TrackSection? section = null;
            if (traj?.TrackSections != null && traj.TrackSections.Count > 0)
            {
                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, point.ut);
                if (sectionIdx >= 0 && sectionIdx < traj.TrackSections.Count)
                    section = traj.TrackSections[sectionIdx];
            }

            bool anchorFound = false;
            Vector3d anchorPos = default(Vector3d);
            Quaternion anchorRot = Quaternion.identity;
            uint anchorPid = 0u;
            if (section.HasValue
                && section.Value.referenceFrame == ReferenceFrame.Relative
                && ShouldUseBodyFixedPrimaryForParentAnchoredDebris(
                    traj,
                    section.Value,
                    point.ut))
            {
                uint bodyFixedAnchorPid = ResolveBodyFixedPrimaryAnchorPid(traj, section.Value);
                if (!TrySelectBodyFixedPrimaryStateVectorPoint(
                        traj,
                        section.Value,
                        point.ut,
                        out TrajectoryPoint bodyFixedPoint,
                        out string bodyFixedSkipReason))
                {
                    return new StateVectorWorldFrame
                    {
                        Resolved = false,
                        WorldPos = default(Vector3d),
                        Branch = "body-fixed-primary",
                        FailureReason = bodyFixedSkipReason,
                        AnchorPid = bodyFixedAnchorPid
                    };
                }

                return ResolveStateVectorWorldPositionPure(
                    point,
                    section,
                    traj?.RecordingFormatVersion ?? 0,
                    (lat, lon, alt) => body.GetWorldSurfacePosition(lat, lon, alt),
                    anchorFound: false,
                    anchorWorldPos: default(Vector3d),
                    anchorWorldRot: Quaternion.identity,
                    anchorVesselId: bodyFixedAnchorPid,
                    allowOrbitalCheckpointStateVector: allowOrbitalCheckpointStateVector,
                    bodyFixedPrimaryPoint: bodyFixedPoint);
            }

            if (section.HasValue
                && section.Value.referenceFrame == ReferenceFrame.Relative
                && RecordedRelativeAnchorPoseResolver.TryFindFocusRecording(traj, out Recording focusRecording)
                && RecordedRelativeAnchorPoseResolver.TryResolveSectionAnchorPose(
                    focusRecording,
                    section.Value,
                    point.ut,
                    out AnchorPose anchorPose))
            {
                anchorFound = true;
                anchorPos = anchorPose.WorldPos;
                anchorRot = anchorPose.WorldRotation;
            }

            int formatVersion = traj?.RecordingFormatVersion ?? 0;
            return ResolveStateVectorWorldPositionPure(
                point,
                section,
                formatVersion,
                (lat, lon, alt) => body.GetWorldSurfacePosition(lat, lon, alt),
                anchorFound,
                anchorPos,
                anchorRot,
                anchorPid,
                allowOrbitalCheckpointStateVector,
                bodyFixedPrimaryPoint: null);
        }

        /// <summary>
        /// Create a ghost map ProtoVessel from interpolated trajectory state vectors.
        /// Used for physics-only suborbital recordings that have no orbit segments.
        /// Constructs a Keplerian orbit from position + velocity at the given UT.
        /// Honours the originating TrackSection's <see cref="ReferenceFrame"/>: Absolute
        /// uses surface lat/lon/alt; Relative resolves through TrackSection.anchorRecordingId
        /// via the recorded-anchor resolver, then applies the anchor-local offset.
        /// </summary>
        internal static Vessel CreateGhostVesselFromStateVectors(
            int recordingIndex, IPlaybackTrajectory traj,
            TrajectoryPoint point,
            double ut,
            bool allowOrbitalCheckpointStateVector = false,
            string stateVectorCreateReason = null)
        {
            return CreateGhostVesselFromStateVectors(
                recordingIndex,
                traj,
                point,
                ut,
                out _,
                allowOrbitalCheckpointStateVector,
                stateVectorCreateReason);
        }

        /// <summary>
        /// Overload that exposes <paramref name="retryLater"/> = true when a
        /// transient state-vector deferral or active-Re-Fly suppression path fires.
        /// Callers that maintain a "pending map vessel" queue (cf.
        /// <c>ParsekPlaybackPolicy.CheckPendingMapVessels</c>) keep the pending
        /// entry alive on (<c>null</c>, <c>retryLater = true</c>) so the recording
        /// is retried next tick rather than silently dropped forever.
        /// </summary>
        internal static Vessel CreateGhostVesselFromStateVectors(
            int recordingIndex, IPlaybackTrajectory traj,
            TrajectoryPoint point,
            double ut,
            out bool retryLater,
            bool allowOrbitalCheckpointStateVector = false,
            string stateVectorCreateReason = null)
        {
            retryLater = false;
            if (traj == null) return null;

            // Phase 7 session-suppression gate (design §3.3).
            if (IsSuppressedByActiveSession(recordingIndex))
            {
                RemoveGhostVesselForRecording(recordingIndex, "session-suppressed subtree");
                return null;
            }

            if (IsStateVectorGhostDeferredForActiveReFlySession(
                    recordingIndex,
                    out string deferredSessionKey))
            {
                retryLater = true;
                var deferred = NewDecisionFields("create-state-vector-deferred");
                deferred.RecordingId = traj.RecordingId;
                deferred.RecordingIndex = recordingIndex;
                deferred.VesselName = traj.VesselName;
                deferred.Source = "StateVector";
                deferred.Body = point.bodyName;
                deferred.StateVecAlt = point.altitude;
                deferred.StateVecSpeed = point.velocity.magnitude;
                deferred.UT = ut;
                deferred.Reason = string.Format(ic,
                    "active-refly-deferred-session session={0} retryLater=true",
                    deferredSessionKey);
                ParsekLog.Info(Tag, BuildGhostMapDecisionLine(deferred));
                return null;
            }

            if (vesselsByRecordingIndex.ContainsKey(recordingIndex))
                return vesselsByRecordingIndex[recordingIndex];

            // Intent-line: log the input shape before any work — easy filter
            // for "what was Parsek asked to create at this UT?".
            {
                var intent = NewDecisionFields("create-state-vector-intent");
                intent.RecordingId = traj.RecordingId;
                intent.RecordingIndex = recordingIndex;
                intent.VesselName = traj.VesselName;
                intent.Source = "StateVector";
                intent.Body = point.bodyName;
                intent.StateVecAlt = point.altitude;
                intent.StateVecSpeed = point.velocity.magnitude;
                intent.UT = ut;
                intent.Reason = stateVectorCreateReason;
                ParsekLog.Verbose(Tag, BuildGhostMapDecisionLine(intent));
            }

            CelestialBody body = FindBodyByName(point.bodyName);
            if (body == null)
            {
                var miss = NewDecisionFields("create-state-vector-miss");
                miss.RecordingId = traj.RecordingId;
                miss.RecordingIndex = recordingIndex;
                miss.VesselName = traj.VesselName;
                miss.Source = "StateVector";
                miss.Body = point.bodyName;
                miss.UT = ut;
                miss.Reason = "body-not-found";
                ParsekLog.Error(Tag, BuildGhostMapDecisionLine(miss));
                return null;
            }

            StateVectorWorldFrame resolution =
                ResolveStateVectorWorldPosition(
                    traj,
                    point,
                    body,
                    allowOrbitalCheckpointStateVector);
            if (!resolution.Resolved)
            {
                bool retryResolutionLater = ShouldRetryStateVectorCreationAfterResolutionMiss(resolution);
                if (retryResolutionLater)
                    retryLater = true;

                var skip = NewDecisionFields("create-state-vector-skip");
                skip.RecordingId = traj.RecordingId;
                skip.RecordingIndex = recordingIndex;
                skip.VesselName = traj.VesselName;
                skip.Source = "StateVector";
                skip.Branch = MapResolutionBranch(resolution.Branch);
                skip.Body = point.bodyName;
                skip.AnchorPid = resolution.AnchorPid;
                skip.StateVecAlt = point.altitude;
                skip.StateVecSpeed = point.velocity.magnitude;
                skip.UT = ut;
                skip.Reason = retryResolutionLater
                    ? string.Format(ic, "{0} retryLater=true", resolution.FailureReason ?? "(null)")
                    : (resolution.FailureReason ?? "(null)");
                ParsekLog.Warn(Tag, BuildGhostMapDecisionLine(skip));
                return null;
            }

            // Compute optional recorded-relative metadata for the structured line.
            Vector3d? anchorPosForLog = null;
            Vector3d? localOffsetForLog = null;
            if (resolution.Branch == "relative")
            {
                localOffsetForLog = new Vector3d(point.latitude, point.longitude, point.altitude);
            }

            // Bug #587 third facet: during in-place continuation Re-Fly, the
            // state-vector-fallback path can synthesize a ProtoVessel right
            // next to the active Re-Fly target when the recording is in a
            // Relative-frame section anchored to that very vessel. The result
            // is a "doubled upper-stage" the user sees in scene. Suppress
            // creation here; GhostPlaybackEngine still renders the legitimate
            // in-physics-zone ghost. See
            // docs/dev/plans/refly-doubled-ghostmap-protovessel-fix.md.
            //
            // PR #574 review P2: scope this to the *parent* recording chain of
            // the active Re-Fly recording — a docking-target recording that
            // is Relative-anchored to the active vessel for legitimate #583 /
            // #584 reasons must NOT be suppressed. PR #574 review P2 also
            // adds retry-later semantics: on suppression we set retryLater =
            // true so the flight-scene caller leaves its pending-map entry
            // intact (otherwise the recording would never get a map ghost
            // again after the Re-Fly session ends).
            // #611: at Re-Fly load time the active recording lives in
            // PendingTree (not CommittedTrees, which the load-side
            // RemoveCommittedTreeById call has just emptied for this tree).
            // Compose the search list so the parent-chain walk can find the
            // active recording in either committed or pending state.
            IReadOnlyList<RecordingTree> searchTrees = ComposeSearchTreesForReFlySuppression(
                RecordingStore.CommittedTrees,
                RecordingStore.HasPendingTree ? RecordingStore.PendingTree : null);
            if (ShouldSuppressStateVectorProtoVesselForActiveReFlyAtCreateTime(
                    SessionSuppressionState.ActiveMarker,
                    resolution.Branch,
                    resolution.AnchorPid,
                    traj,
                    ut,
                    traj.RecordingId,
                    RecordingStore.CommittedRecordings,
                    searchTrees,
                    out string activeReFlySuppressReason))
            {
                MarkStateVectorGhostDeferredForActiveReFly(recordingIndex);
                retryLater = true;
                var suppressed = NewDecisionFields("create-state-vector-suppressed");
                suppressed.RecordingId = traj.RecordingId;
                suppressed.RecordingIndex = recordingIndex;
                suppressed.VesselName = traj.VesselName;
                suppressed.Source = "StateVector";
                suppressed.Branch = MapResolutionBranch(resolution.Branch);
                suppressed.Body = point.bodyName;
                suppressed.AnchorPid = resolution.AnchorPid;
                suppressed.AnchorPos = anchorPosForLog;
                suppressed.LocalOffset = localOffsetForLog;
                suppressed.StateVecAlt = point.altitude;
                suppressed.StateVecSpeed = point.velocity.magnitude;
                suppressed.UT = ut;
                suppressed.Reason = string.Format(ic,
                    "{0} sess={1} retryLater=true",
                    activeReFlySuppressReason,
                    SessionSuppressionState.ActiveMarker?.SessionId ?? "<no-id>");
                ParsekLog.Info(Tag, BuildGhostMapDecisionLine(suppressed));
                return null;
            }

            // #611: when a Re-Fly session is active but the predicate
            // declined to suppress, emit a Verbose decision line so the
            // playtest log records WHY the gate didn't fire — without this,
            // a successful create-state-vector-done line is the only signal
            // and we can't tell whether the predicate ran or skipped which
            // reject branch it took. Skip when no Re-Fly session is active
            // (the gate is trivially a no-op then; logging would spam every
            // ProtoVessel create).
            if (SessionSuppressionState.ActiveMarker != null
                && !string.IsNullOrEmpty(activeReFlySuppressReason))
            {
                var notSuppressed = NewDecisionFields("create-state-vector-not-suppressed-during-refly");
                notSuppressed.RecordingId = traj.RecordingId;
                notSuppressed.RecordingIndex = recordingIndex;
                notSuppressed.VesselName = traj.VesselName;
                notSuppressed.Source = "StateVector";
                notSuppressed.Branch = MapResolutionBranch(resolution.Branch);
                notSuppressed.Body = point.bodyName;
                notSuppressed.AnchorPid = resolution.AnchorPid;
                notSuppressed.StateVecAlt = point.altitude;
                notSuppressed.StateVecSpeed = point.velocity.magnitude;
                notSuppressed.UT = ut;
                notSuppressed.Reason = string.Format(ic,
                    "{0} sess={1}",
                    activeReFlySuppressReason,
                    SessionSuppressionState.ActiveMarker?.SessionId ?? "<no-id>");
                ParsekLog.Verbose(Tag, BuildGhostMapDecisionLine(notSuppressed));
            }

            Vector3d worldPos = resolution.WorldPos;
            Vector3d vel = new Vector3d(point.velocity.x, point.velocity.y, point.velocity.z);

            Orbit orbit = new Orbit();
            OrbitReseed.FromWorldPosAndRecordedVelocity(orbit, body, worldPos, vel, ut);

            string logContext = string.Format(ic,
                "recording #{0} (state vectors alt={1:F0} spd={2:F1} frame={3})",
                recordingIndex, point.altitude, point.velocity.magnitude, resolution.Branch);
            string protoSource = string.IsNullOrEmpty(stateVectorCreateReason)
                ? "state-vector-fallback"
                : stateVectorCreateReason;
            Vessel vessel = BuildAndLoadGhostProtoVesselCore(
                traj,
                orbit,
                body,
                logContext,
                protoSource,
                string.Format(ic,
                    "stateBody={0} stateUT={1:F1} stateAlt={2:F0} stateSpeed={3:F1} frame={4} anchorPid={5}",
                    point.bodyName ?? "(null)",
                    ut,
                    point.altitude,
                    point.velocity.magnitude,
                    resolution.Branch,
                    resolution.AnchorPid));
            if (vessel != null)
            {
                TrackRecordingGhostVessel(recordingIndex, traj, vessel);
                lifecycleCreatedThisTick++;

                // Exit-line: full input/output now that the vessel is bound.
                var done = NewDecisionFields("create-state-vector-done");
                done.RecordingId = traj.RecordingId;
                done.RecordingIndex = recordingIndex;
                done.VesselName = traj.VesselName;
                done.Source = "StateVector";
                done.Branch = MapResolutionBranch(resolution.Branch);
                done.Body = body.name;
                done.WorldPos = worldPos;
                done.GhostPid = vessel.persistentId;
                done.AnchorPid = resolution.AnchorPid;
                done.AnchorPos = anchorPosForLog;
                done.LocalOffset = localOffsetForLog;
                done.StateVecAlt = point.altitude;
                done.StateVecSpeed = point.velocity.magnitude;
                done.UT = ut;
                done.Reason = string.IsNullOrEmpty(stateVectorCreateReason)
                    ? string.Format(ic, "formatV={0}", traj.RecordingFormatVersion)
                    : string.Format(ic, "{0} formatV={1}", stateVectorCreateReason, traj.RecordingFormatVersion);
                ParsekLog.Info(Tag, BuildGhostMapDecisionLine(done));

                StashLastKnownFrame(recordingIndex, new LastKnownGhostFrame
                {
                    RecordingId = traj.RecordingId,
                    VesselName = traj.VesselName,
                    GhostPid = vessel.persistentId,
                    Source = "StateVector",
                    Branch = MapResolutionBranch(resolution.Branch),
                    Body = body.name,
                    WorldPos = worldPos,
                    AnchorPid = resolution.AnchorPid,
                    LastUT = ut
                });
            }

            return vessel;
        }

        /// <summary>
        /// Translate the lowercase <see cref="StateVectorWorldFrame.Branch"/>
        /// strings ("absolute" / "relative" / "orbital-checkpoint" /
        /// "no-section") into the capitalised user-facing branch names used by
        /// the structured log lines.
        /// </summary>
        internal static string MapResolutionBranch(string resolutionBranch)
        {
            switch (resolutionBranch)
            {
                case "absolute": return "Absolute";
                case "relative": return "Relative";
                case "body-fixed-primary": return "BodyFixedPrimary";
                case "orbital-checkpoint": return "OrbitalCheckpoint";
                case "no-section": return "no-section";
                default: return resolutionBranch ?? "(n/a)";
            }
        }

        /// <summary>
        /// Update a ghost map ProtoVessel's orbit from interpolated trajectory state vectors.
        /// Used for per-frame orbit updates of physics-only suborbital ghosts.
        /// Handles SOI transitions (body change + orbit renderer rebuild).
        /// Honours the originating TrackSection's <see cref="ReferenceFrame"/> — see
        /// <see cref="CreateGhostVesselFromStateVectors"/> for the contract.
        ///
        /// <paramref name="loopEpochShiftSeconds"/> (default 0) is the Mission-loop epoch shift
        /// <c>liveUT - effUT</c>: the world position/velocity in <paramref name="point"/> were
        /// sampled at the loop-mapped <c>effUT</c> (a past recorded UT), but the map icon renders
        /// at <c>orbit.getPositionAtUT(liveUT)</c>, so the reseeded orbit epoch is pushed forward
        /// by the shift to <c>ut + loopEpochShiftSeconds == liveUT</c>. The icon then sits exactly
        /// at the replayed position now instead of being propagated a fraction of an orbit. Zero
        /// for non-loop ghosts (effUT == liveUT), so behavior is unchanged off the loop path.
        /// </summary>
        internal static bool UpdateGhostOrbitFromStateVectors(
            int recordingIndex, IPlaybackTrajectory traj,
            TrajectoryPoint point,
            double ut,
            bool allowOrbitalCheckpointStateVector = false,
            string stateVectorUpdateReason = null,
            double loopEpochShiftSeconds = 0.0)
        {
            if (!vesselsByRecordingIndex.TryGetValue(recordingIndex, out Vessel vessel))
                return false;

            if (vessel.orbitDriver == null)
            {
                var miss = NewDecisionFields("update-state-vector-miss");
                miss.RecordingId = traj?.RecordingId;
                miss.RecordingIndex = recordingIndex;
                miss.VesselName = traj?.VesselName;
                miss.Source = "StateVector";
                miss.Body = point.bodyName;
                miss.GhostPid = vessel.persistentId;
                miss.UT = ut;
                miss.Reason = "no-orbit-driver";
                ParsekLog.Error(Tag, BuildGhostMapDecisionLine(miss));
                return false;
            }

            CelestialBody body = FindBodyByName(point.bodyName);
            if (body == null)
            {
                var miss = NewDecisionFields("update-state-vector-miss");
                miss.RecordingId = traj?.RecordingId;
                miss.RecordingIndex = recordingIndex;
                miss.VesselName = traj?.VesselName;
                miss.Source = "StateVector";
                miss.Body = point.bodyName;
                miss.GhostPid = vessel.persistentId;
                miss.UT = ut;
                miss.Reason = "body-not-found";
                ParsekLog.Error(Tag, BuildGhostMapDecisionLine(miss));
                return false;
            }

            StateVectorWorldFrame resolution =
                ResolveStateVectorWorldPosition(
                    traj,
                    point,
                    body,
                    allowOrbitalCheckpointStateVector);
            if (!resolution.Resolved)
            {
                var skip = NewDecisionFields("update-state-vector-skip");
                skip.RecordingId = traj?.RecordingId;
                skip.RecordingIndex = recordingIndex;
                skip.VesselName = traj?.VesselName;
                skip.Source = "StateVector";
                skip.Branch = MapResolutionBranch(resolution.Branch);
                skip.Body = point.bodyName;
                skip.GhostPid = vessel.persistentId;
                skip.AnchorPid = resolution.AnchorPid;
                skip.StateVecAlt = point.altitude;
                skip.StateVecSpeed = point.velocity.magnitude;
                skip.UT = ut;
                skip.Reason = resolution.FailureReason ?? "(null)";
                ParsekLog.Warn(Tag, BuildGhostMapDecisionLine(skip));
                return false;
            }

            // Compute optional recorded-relative metadata for the structured line.
            Vector3d? anchorPosForLog = null;
            Vector3d? localOffsetForLog = null;
            if (resolution.Branch == "relative")
            {
                localOffsetForLog = new Vector3d(point.latitude, point.longitude, point.altitude);
            }

            IReadOnlyList<RecordingTree> searchTrees = ComposeSearchTreesForReFlySuppression(
                RecordingStore.CommittedTrees,
                RecordingStore.HasPendingTree ? RecordingStore.PendingTree : null);
            if (ShouldRemoveStateVectorProtoVesselForActiveReFlyOnUpdate(
                    SessionSuppressionState.ActiveMarker,
                    resolution.Branch,
                    resolution.AnchorPid,
                    traj?.RecordingId,
                    RecordingStore.CommittedRecordings,
                    searchTrees,
                    out string activeReFlySuppressReason))
            {
                MarkStateVectorGhostDeferredForActiveReFly(recordingIndex);
                var suppressed = NewDecisionFields("update-state-vector-suppressed");
                suppressed.RecordingId = traj?.RecordingId;
                suppressed.RecordingIndex = recordingIndex;
                suppressed.VesselName = traj?.VesselName;
                suppressed.Source = "StateVector";
                suppressed.Branch = MapResolutionBranch(resolution.Branch);
                suppressed.Body = point.bodyName;
                suppressed.WorldPos = resolution.WorldPos;
                suppressed.GhostPid = vessel.persistentId;
                suppressed.AnchorPid = resolution.AnchorPid;
                suppressed.AnchorPos = anchorPosForLog;
                suppressed.LocalOffset = localOffsetForLog;
                suppressed.StateVecAlt = point.altitude;
                suppressed.StateVecSpeed = point.velocity.magnitude;
                suppressed.UT = ut;
                suppressed.Reason = string.Format(ic,
                    "{0} sess={1} removeReason={2}",
                    activeReFlySuppressReason,
                    SessionSuppressionState.ActiveMarker?.SessionId ?? "<no-id>",
                    TrackingStationGhostSkipActiveReFlyRelativeUpdate);
                ParsekLog.Info(Tag, BuildGhostMapDecisionLine(suppressed));
                return true;
            }

            // SOI transition handling (same pattern as ApplyOrbitToVessel): compare against the body
            // PARSEK last applied, not orbitDriver.referenceBody (a KSP-owned field), so the renderer
            // rebuild fires once per genuine Parsek-driven body change. (KSP's per-frame SOI transition
            // is now blocked for ghosts by GhostOrbitDominantBodyPatch; this stays as the correct
            // invariant + avoids a redundant rebuild.) OrbitDriver.celestialBody is only for real
            // CelestialBody drivers; vessel targets must keep identity in OrbitDriver.vessel.
            ghostLastAppliedOrbitBody.TryGetValue(vessel.persistentId, out string lastAppliedBody);
            bool soiChanged = GhostOrbitBodyChanged(lastAppliedBody, body.name);
            if (soiChanged)
            {
                var soi = NewDecisionFields("update-state-vector-soi-change");
                soi.RecordingId = traj?.RecordingId;
                soi.RecordingIndex = recordingIndex;
                soi.VesselName = traj?.VesselName;
                soi.Source = "StateVector";
                soi.Branch = MapResolutionBranch(resolution.Branch);
                soi.Body = body.name;
                soi.GhostPid = vessel.persistentId;
                soi.UT = ut;
                soi.Reason = stateVectorUpdateReason;
                ParsekLog.Info(Tag, BuildGhostMapDecisionLine(soi));
            }

            Vector3d worldPos = resolution.WorldPos;
            Vector3d vel = new Vector3d(point.velocity.x, point.velocity.y, point.velocity.z);

            // Orbit-raise gap glide: reconstruct the recorded INERTIAL position. The body-fixed
            // FromWorldPosAndRecordedVelocity seed below resolves the gap point via
            // body.GetWorldSurfacePosition at the LIVE body orientation; on a loop replayed ~1e9 s
            // later the body has rotated, so the body-fixed gap rendering sits rotated off the
            // inertial orbit lines drawn from recorded Keplerian elements. Reconstruct the point's
            // inertial position from its recorded UT + body rotation (incl. initialRotation) so the
            // parking -> raise -> loiter -> escape arc lives in one inertial frame (no teleport at the
            // raise->loiter seam). Gated to the orbit-raise-gap-points reason on an Absolute branch
            // ONLY; the launch ascent (no preceding OrbitSegment) never reaches this path via
            // ShouldDriveGapFromPoints, and SOI-gap / checkpoint / terminal callers pass other reasons.
            bool inertialPathFired = false;
            string inertialDeclineReason = null;
            double inertialLonForLog = double.NaN;
            if (ShouldSeedGapGlideInertial(stateVectorUpdateReason, resolution.Branch))
            {
                if (OrbitSeedResolver.TryResolveRotationPeriod(body, out double gapRotationPeriod))
                {
                    double gapInitialRotation = OrbitSeedResolver.ResolveInitialRotation(body);
                    if (OrbitReseed.TryFromHistoricalLatLonAltAndRecordedVelocityWithEpoch(
                            vessel.orbitDriver.orbit,
                            body,
                            point.latitude,
                            point.longitude,
                            point.altitude,
                            vel,
                            point.ut,
                            point.ut + loopEpochShiftSeconds,
                            gapRotationPeriod,
                            gapInitialRotation,
                            out double inertialLon,
                            out string gapFailReason))
                    {
                        inertialPathFired = true;
                        inertialLonForLog = inertialLon;
                        gapGlideInertialSeedCount++;
                    }
                    else
                    {
                        inertialDeclineReason = gapFailReason ?? "historical-helper-declined";
                    }
                }
                else
                {
                    inertialDeclineReason = "historical-rotation-unavailable";
                }

                if (!inertialPathFired)
                {
                    gapGlideBodyFixedFallbackCount++;
                    ParsekLog.Warn(Tag,
                        string.Format(ic,
                            "Gap-glide inertial seed declined for ghost pid={0} recIndex={1} body={2} "
                            + "recordedUT={3:F2} shift={4:F1} reason={5}: falling back to body-fixed seed "
                            + "(icon may rotate off the inertial orbit lines this frame)",
                            vessel.persistentId, recordingIndex, body.name, point.ut,
                            loopEpochShiftSeconds, inertialDeclineReason ?? "(null)"));
                }

                // Aggregate proof-of-fire summary (shared key, ~5s window): a KSP.log capture must
                // show inertial>0 for the looped re-aim playtest; inertial==0 means the gate never
                // engaged (the no-op trap). bodyFixed counts the safety fallbacks.
                ParsekLog.VerboseRateLimited(Tag,
                    "gap-glide-inertial-summary",
                    string.Format(ic,
                        "Gap-glide seed summary: inertial={0} bodyFixed={1} "
                        + "(orbit-raise gaps reconstructed in inertial frame)",
                        gapGlideInertialSeedCount, gapGlideBodyFixedFallbackCount),
                    5.0);
            }

            // The state-vector path keeps the OLD contract: a SHIFTED-epoch orbit (seeded at
            // ut + loopEpochShiftSeconds == liveUT) that stock propagates at the LIVE Planetarium
            // clock. The segment-driven raw-epoch + effUT-drive contract (GhostOrbitIconDrivePatch)
            // must therefore NOT engage for this ghost. If this pid was previously a covering
            // OrbitSegment ghost (a loop mission crossing from a parking-orbit segment into a
            // transfer-coast OrbitalCheckpoint gap updates the SAME ghost in place, especially in
            // the Tracking Station where the dispatcher does not remove+recreate it), the prior
            // segment phase left stale segment bounds + loop-shift + epoch-shift entries. The
            // now-authoritative drive patch would read those stale dicts and re-subtract a shift
            // from this already-shifted state-vector orbit, mis-positioning or freezing the icon and
            // suppressing it. Clear them here (BEFORE updateFromParameters so even the in-call frame
            // defers to stock) so TryGetVisibleOrbitBoundsForGhostVessel returns false, the drive
            // patch returns true, and stock correctly propagates the shifted-epoch orbit at live UT.
            bool hadStaleSegmentDrive =
                ghostOrbitBounds.Remove(vessel.persistentId)
                | ghostBodyFrameOrbitBounds.Remove(vessel.persistentId)
                | ghostOrbitLoopShiftedPids.Remove(vessel.persistentId)
                | ghostOrbitEpochShift.Remove(vessel.persistentId);
            // Drop the icon-drive propagateUT record at this segment->state-vector handoff too: the
            // drive patch now returns true (stock re-propagates the orbit at live UT), so the icon-drive
            // Prefix stops recording, and a record left from the pre-reseed segment frames would let the
            // probe trust a stale phase for up to SeedFreshnessFrames after the icon has moved to live.
            // Separate statement (NOT folded into the hadStaleSegmentDrive OR-chain) so it never flips
            // that flag on a propagateUT-only entry.
            ghostIconDrivePropagation.Remove(vessel.persistentId);
            if (hadStaleSegmentDrive)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "State-vector reseed cleared stale segment-drive state for ghost pid={0} "
                        + "recIndex={1} (segment->state-vector transition, shift={2:F1}, scene={3}): "
                        + "icon now stock-propagated at live UT off the effUT drive path",
                        vessel.persistentId, recordingIndex, loopEpochShiftSeconds,
                        HighLogic.LoadedScene));
            }

            // Loop replay: push the orbit epoch forward by (liveUT - effUT) so the icon, drawn at
            // getPositionAtUT(liveUT), lands on the world position recorded at effUT instead of
            // being propagated forward along the ellipse. Zero shift for non-loop ghosts. Skipped
            // when the orbit-raise inertial branch above already seeded the orbit (it seeded at the
            // same loop-shifted epoch via the reconstructed inertial position) so we don't re-seed
            // it back into the body-fixed frame; the body-fixed seed still runs for every other
            // reason and as the safety fallback when the inertial reconstruction declined.
            if (!inertialPathFired)
            {
                OrbitReseed.FromWorldPosAndRecordedVelocity(
                    vessel.orbitDriver.orbit,
                    body,
                    worldPos,
                    vel,
                    ut + loopEpochShiftSeconds);
            }
            vessel.orbitDriver.updateFromParameters();
            NormalizeGhostOrbitDriverTargetIdentity(vessel, "update-state-vector");

            // Seam CoMD re-snap (Bug A loop-icon-warp-lag): this state-vector reseed runs in a Parsek
            // per-frame pass AFTER this frame's VesselPrecalculate.FixedUpdate, so the cached
            // Vessel.CoMD (the value Vessel.GetWorldPos3D returns, and what the map icon renders at)
            // still holds the PRE-reseed conic phase - while the orbit LINE is drawn from the
            // freshly-reseeded elements. updateFromParameters() above propagated orbitDriver.pos to the
            // live clock and moved the vessel TRANSFORM via SetPosition, but SetPosition does NOT touch
            // CoMD, and GetWorldPos3D returns the cached CoMD (only self-recomputing while
            // !firstStatsRunComplete - true only at creation, which is why ForceImmediateIconDrive
            // works at creation but a steady-state re-drive does not). So at high warp the
            // (currentUT - reseed-epoch) gap leaves the icon up to ~168 deg off its own line for one
            // frame until the NEXT FixedUpdate refreshes CoMD (the loopShift=0.0 icon-off-orbit /
            // icon-teleport anomaly, rec 04177 idx 43). Force the same CalculatePhysicsStats() refresh
            // GetWorldPos3D itself uses (it sets CoMD = mainBody.position + orbitDriver.pos for a packed
            // orbiting ghost) so the icon lands on the conic THIS frame. The director-drive path needs
            // no equivalent: its reseed runs INSIDE FixedUpdate alongside CalculatePhysicsStats, so its
            // CoMD is never stale (its frames stay < 3 deg in the capture). Gated to the conic-change
            // seam (cheap sma/ecc/epoch compare) so steady-state reseeds add no per-ghost cost.
            Orbit svOrbit = vessel.orbitDriver.orbit;
            if (svOrbit != null)
            {
                bool svHadLast = ghostLastAppliedOrbitElements.TryGetValue(
                    vessel.persistentId, out var svLast);
                // The reseed seeds orbit.epoch = ut + loopEpochShiftSeconds, which advances every live
                // frame at warp, so the epoch facet (1e-3 s) trips on every genuine reseed - exactly the
                // frames CoMD is stale - and an unchanged conic (same recorded point re-applied) skips.
                bool svSeam = GhostOrbitElementsChanged(
                    svHadLast, svLast.sma, svLast.ecc, svLast.epoch,
                    svOrbit.semiMajorAxis, svOrbit.eccentricity, svOrbit.epoch);
                if (svSeam)
                {
                    if (vessel.precalc != null)
                    {
                        vessel.precalc.CalculatePhysicsStats();
                        seamComdRefreshCount++;
                        if (MapRenderTrace.IsEnabled)
                        {
                            double postAngle = SeamIconOffOrbitAngleDeg(
                                vessel, svOrbit, Planetarium.GetUniversalTime());
                            if (!double.IsNaN(postAngle))
                                seamComdMaxOffOrbitDegPostRefresh = System.Math.Max(
                                    seamComdMaxOffOrbitDegPostRefresh, postAngle);
                        }
                    }
                    ghostLastAppliedOrbitElements[vessel.persistentId] =
                        (svOrbit.semiMajorAxis, svOrbit.eccentricity, svOrbit.epoch);
                }
                else
                {
                    seamComdSteadyCount++;
                }

                // Shared-key proof-of-fire (no-op trap guard, PR-#885 lesson): a KSP.log capture of a
                // warp re-fly must show refreshed>0 (the seam path engaged) and, with mapRenderTracing
                // on, maxOffDeg<1 (the icon re-snapped onto its conic). steady>0 confirms the seam gate
                // is filtering rather than refreshing every frame. The IsVerboseEnabled guard keeps the
                // string.Format off the per-ghost per-frame path in normal (verbose-off) play - the
                // counters above are int/double increments, the only steady-state cost.
                if (ParsekLog.IsVerboseEnabled)
                    ParsekLog.VerboseRateLimited(Tag, "seam-comd-refresh-summary",
                        string.Format(ic,
                            "Seam CoMD re-snap summary: refreshed={0} steady={1} maxOffDegPostRefresh={2:F2} "
                            + "(state-vector reseed re-snaps the icon CoMD onto its conic at live UT)",
                            seamComdRefreshCount, seamComdSteadyCount, seamComdMaxOffOrbitDegPostRefresh),
                        5.0);
            }

            if (soiChanged && vessel.orbitRenderer != null)
            {
                vessel.orbitRenderer.drawMode = OrbitRendererBase.DrawMode.REDRAW_AND_RECALCULATE;
                vessel.orbitRenderer.enabled = false;
                vessel.orbitRenderer.enabled = true;
            }

            // Remember the body we just applied (see GhostOrbitBodyChanged / ghostLastAppliedOrbitBody).
            ghostLastAppliedOrbitBody[vessel.persistentId] = body.name;

            lifecycleUpdatedThisTick++;

            // Per-recording rate-limited line — one entry per recording per ~5s
            // window so a long warp pass leaves a readable trace without spam.
            string updateKey = string.Format(ic,
                "state-vector-update-{0}",
                traj?.RecordingId ?? recordingIndex.ToString(ic));
            var done = NewDecisionFields("update-state-vector");
            done.RecordingId = traj?.RecordingId;
            done.RecordingIndex = recordingIndex;
            done.VesselName = traj?.VesselName;
            done.Source = "StateVector";
            done.Branch = MapResolutionBranch(resolution.Branch);
            done.Body = body.name;
            done.WorldPos = worldPos;
            done.GhostPid = vessel.persistentId;
            done.AnchorPid = resolution.AnchorPid;
            done.AnchorPos = anchorPosForLog;
            done.LocalOffset = localOffsetForLog;
            done.StateVecAlt = point.altitude;
            done.StateVecSpeed = point.velocity.magnitude;
            done.UT = ut;
            // For the orbit-raise gap glide, annotate which frame seeded the orbit (inertial
            // reconstruction vs body-fixed live-rotation fallback) and the reconstructed inertial
            // longitude, so each driven frame self-identifies in the post-hoc log.
            done.Reason = ShouldSeedGapGlideInertial(stateVectorUpdateReason, resolution.Branch)
                ? string.Format(ic,
                    "{0} seedFrame={1} inertialLon={2:F4}{3}",
                    stateVectorUpdateReason,
                    inertialPathFired ? "inertial" : "body-fixed",
                    inertialLonForLog,
                    inertialPathFired ? "" : (" decline=" + (inertialDeclineReason ?? "(null)")))
                : stateVectorUpdateReason;
            ParsekLog.VerboseRateLimited(Tag, updateKey, BuildGhostMapDecisionLine(done), 5.0);

            StashLastKnownFrame(recordingIndex, new LastKnownGhostFrame
            {
                RecordingId = traj?.RecordingId,
                VesselName = traj?.VesselName,
                GhostPid = vessel.persistentId,
                Source = "StateVector",
                Branch = MapResolutionBranch(resolution.Branch),
                Body = body.name,
                WorldPos = worldPos,
                AnchorPid = resolution.AnchorPid,
                LastUT = ut
            });
            return false;
        }

        /// <summary>
        /// True when the ghost orbit body Parsek is about to apply differs from the body it last applied
        /// to this ghost (or nothing has been applied yet). Gates the orbit-renderer rebuild + "SOI
        /// change" log so they fire once per genuine Parsek-driven body change, not every frame KSP
        /// transiently re-transitions the unloaded ghost's <c>OrbitDriver.referenceBody</c> between
        /// reseeds (the cause of the transfer-leg orbit-line blink). Pure. See
        /// <see cref="ghostLastAppliedOrbitBody"/>.
        /// </summary>
        internal static bool GhostOrbitBodyChanged(string lastAppliedBodyName, string newBodyName)
        {
            return string.IsNullOrEmpty(lastAppliedBodyName)
                || !string.Equals(lastAppliedBodyName, newBodyName, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// True when the orbit elements about to be applied to a ghost differ from the last-applied
        /// triple for it (or nothing was applied yet, or any NEW element is non-finite). Gates the
        /// seam CoMD re-snap in <see cref="UpdateGhostOrbitFromStateVectors"/> so it fires once per
        /// genuine conic change, not on every steady-state reseed of the same recorded point - keeping
        /// the per-ghost per-frame cost flat (the visual-efficiency invariant). Pure / Unity-free.
        /// Epsilons: sma relative 1e-6 (metre-scale orbits), ecc absolute 1e-9, epoch absolute 1e-3 s.
        /// A non-finite NEW element counts as CHANGED so the gate never suppresses and strands a stale
        /// icon (the downstream CalculatePhysicsStats tolerates it and the next FixedUpdate corrects).
        /// </summary>
        internal static bool GhostOrbitElementsChanged(
            bool hadLast, double lastSma, double lastEcc, double lastEpoch,
            double newSma, double newEcc, double newEpoch)
        {
            if (!hadLast)
                return true;
            if (double.IsNaN(newSma) || double.IsInfinity(newSma)
                || double.IsNaN(newEcc) || double.IsInfinity(newEcc)
                || double.IsNaN(newEpoch) || double.IsInfinity(newEpoch))
                return true;
            double smaTol = System.Math.Max(1.0, System.Math.Abs(lastSma) * 1e-6);
            return System.Math.Abs(newSma - lastSma) > smaTol
                || System.Math.Abs(newEcc - lastEcc) > 1e-9
                || System.Math.Abs(newEpoch - lastEpoch) > 1e-3;
        }

        /// <summary>
        /// Pure angle (deg) between the icon's body-relative world position and the conic's
        /// body-relative position at the same UT - the SAME quantity MapRenderProbe's icon-off-orbit
        /// anomaly measures. Returns NaN for a degenerate / unresolved input (either vector below a
        /// 1 m floor) so the proof summary skips it instead of logging a phantom 90 deg (the angle of
        /// a zero vector). Unity-math only (<see cref="Vector3d.Angle"/>); xUnit-tested directly.
        /// </summary>
        internal static double IconVsOrbitAngleDeg(Vector3d bodyRelIcon, Vector3d bodyRelOrbitAtUT)
        {
            if (bodyRelIcon.sqrMagnitude <= 1.0 || bodyRelOrbitAtUT.sqrMagnitude <= 1.0)
                return double.NaN;
            double a = Vector3d.Angle(bodyRelIcon, bodyRelOrbitAtUT);
            return double.IsNaN(a) ? double.NaN : a;
        }

        /// <summary>
        /// Post-refresh proof metric: angle between the ghost icon's actual body-relative world
        /// position (<see cref="Vessel.GetWorldPos3D"/> - referenceBody.position, i.e. the cached CoMD
        /// just refreshed by CalculatePhysicsStats) and the conic's body-relative position at
        /// <paramref name="currentUT"/> (the phase the line draws). ~0 after the re-snap. Mirrors
        /// MapRenderProbe.OrbitRelativePositionYup (getRelativePositionAtUT + Swizzle) so the metric
        /// agrees with the probe's anomaly. NaN-safe; called only under MapRenderTrace.IsEnabled.
        /// </summary>
        internal static double SeamIconOffOrbitAngleDeg(Vessel vessel, Orbit orbit, double currentUT)
        {
            if (vessel == null || orbit == null || orbit.referenceBody == null)
                return double.NaN;
            Vector3d bodyRelIcon = vessel.GetWorldPos3D() - orbit.referenceBody.position;
            Vector3d rel = orbit.getRelativePositionAtUT(currentUT);
            rel.Swizzle();
            return IconVsOrbitAngleDeg(bodyRelIcon, rel);
        }

        /// <summary>
        /// Pure gate for the orbit-raise gap glide's inertial-frame reconstruction inside
        /// <see cref="UpdateGhostOrbitFromStateVectors"/>. The inertial branch fires ONLY when the
        /// state-vector update came from the gap-glide call sites (reason
        /// <c>"orbit-raise-gap-points"</c>, passed only by the flight + TS gap branches) AND the
        /// world position was resolved through the Absolute branch (body-fixed geographic
        /// lat/lon/alt). Every other reason (SOI-gap checkpoint, terminal fallback, null) and every
        /// other branch (relative anchor-local offsets, body-fixed-primary debris, orbital-checkpoint)
        /// keeps the unchanged body-fixed live-rotation seed. The orbital-vacuum-vs-launch-ascent
        /// discrimination is enforced upstream by <see cref="ShouldDriveGapFromPoints"/> (which
        /// requires an OrbitSegment bracket on both sides); the launch ascent has no preceding orbit
        /// segment so it never reaches the gap-glide reason. Pure.
        /// </summary>
        internal static bool ShouldSeedGapGlideInertial(string stateVectorUpdateReason, string resolutionBranch)
        {
            return string.Equals(stateVectorUpdateReason, "orbit-raise-gap-points", System.StringComparison.Ordinal)
                && string.Equals(resolutionBranch, "absolute", System.StringComparison.Ordinal);
        }

        /// <summary>
        /// Shared: apply an OrbitSegment's Keplerian elements to a ghost vessel's OrbitDriver.
        /// Handles body resolution, orbit construction, SOI transitions, and logging.
        /// </summary>
        /// <summary>
        /// Creation-frame icon fix (2026-06-12 retest): pv.Load positions the proto at the RAW
        /// recorded-epoch orbit, and the per-frame effUT icon drive (GhostOrbitIconDrivePatch on
        /// OrbitDriver.updateFromParameters) only repositions it on the NEXT OrbitDriver tick - so
        /// the icon's first rendered frame sat up to ~143 degrees off its line (a visible blip at
        /// high warp, flagged by the tracer's creation-frame icon-off-orbit/teleport anomalies).
        /// One explicit drive call AFTER the orbit + loop epoch shift are registered routes through
        /// the same Harmony patch and lands the icon at the loop effUT before the frame renders.
        /// </summary>
        private static void ForceImmediateIconDrive(Vessel vessel, string context)
        {
            if (vessel == null || vessel.orbitDriver == null)
                return;
            vessel.orbitDriver.updateFromParameters();
            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "Forced first icon drive after {0} create: pid={1}",
                    context, vessel.persistentId));
        }

        private static void ApplyOrbitToVessel(Vessel vessel, OrbitSegment segment, string logContext,
            double loopEpochShiftSeconds = 0.0)
        {
            if (vessel.orbitDriver == null)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic, "ApplyOrbitToVessel: no OrbitDriver for {0}", logContext));
                return;
            }

            CelestialBody body = FindBodyByName(segment.bodyName);
            if (body == null)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic, "ApplyOrbitToVessel: body '{0}' not found for {1}",
                        segment.bodyName, logContext));
                return;
            }

            // SOI transition: compare against the body PARSEK last applied to this ghost, NOT
            // vessel.orbitDriver.referenceBody. KSP re-transitions an unloaded ghost's referenceBody
            // between our per-frame reseeds (escape hyperbola kicked out to the parent star; a
            // center-aimed transfer's endpoints fall inside the launch / target SOI), so the driver-field
            // compare trips every frame near an SOI boundary and toggles the renderer off/on each frame
            // -> the orbit line blinks at the start / end of a transfer leg. (OrbitDriver.celestialBody
            // must stay null for vessel targets; stock OrbitTargeter treats a non-null celestialBody on a
            // target driver as a body target and drops same-body ghosts.)
            ghostLastAppliedOrbitBody.TryGetValue(vessel.persistentId, out string lastAppliedBody);
            bool soiChanged = GhostOrbitBodyChanged(lastAppliedBody, body.name);
            if (soiChanged)
            {
                ParsekLog.Info(Tag,
                    string.Format(ic, "SOI change for {0} — new body={1}", logContext, body.name));
            }

            // Direct element assignment via SetOrbit — bypasses the lossy
            // state-vector roundtrip in UpdateFromOrbitAtUT (#172).
            //
            // RAW recorded epoch (no shift baked in): the loop-mapped sample clock (effUT) can
            // advance FASTER than the live Planetarium clock (e.g. under time warp, the loop maps
            // a short live span to a huge recorded span). Stock OrbitDriver propagates the icon at
            // the LIVE clock every FixedUpdate, so a shifted epoch only lands the right phase at
            // the instant the (rate-limited) reseed re-snaps the shift; between reseeds the live
            // clock is too slow and the icon stalls on a near-fixed anomaly until the next reseed
            // jumps it forward (the frozen-icon-on-short-arc sawtooth). Instead we seed the raw
            // epoch and record the loop shift in ghostOrbitEpochShift; GhostOrbitIconDrivePatch
            // drives the OrbitDriver at effUT = liveUT - shift every frame so the icon glides at
            // the effUT rate (matching the mesh path and the wide-segment case), and
            // GhostOrbitArcPatch evaluates its arc bounds at the same effUT so the line stays in
            // lockstep. The stored arc bounds stay in the LIVE frame (shifted) so the line-toggle
            // Postfix + AnyGhostHeadLeftAppliedSegment comparisons (which use the live clock) are
            // byte-identical to before; the two prefixes subtract the shift where they need the
            // recorded clock.
            //
            // NOTE: a loop-shift body-fixed LAN rotation was tried here (rotate the node by the body's
            // rotation over the shift so the inertial orbit projects to its recorded body-fixed
            // appearance, to close the parking->loiter line-vs-raise desync). It was REVERTED: it
            // rotates ONLY the map OrbitDriver's orbit (line + map icon), not the flight-scene ghost
            // mesh, the recorded trajectory points, or the other stages' rendering, so the rotated
            // line ended up inconsistent with everything else (the ghost on its old recorded path, the
            // other stage's trajectory in the wrong place, the hyperbolic escape line rotated off its
            // own icon). The transformation would have to apply to the entire ghost rendering
            // consistently to be valid; rotating one element in isolation breaks more than it fixes.
            // See docs/dev/todo-and-known-bugs.md.
            Orbit orb = vessel.orbitDriver.orbit;
            orb.SetOrbit(
                segment.inclination,
                segment.eccentricity,
                segment.semiMajorAxis,
                segment.longitudeOfAscendingNode,
                segment.argumentOfPeriapsis,
                segment.meanAnomalyAtEpoch,
                segment.epoch,
                body);

            // Record the per-frame icon clock state BEFORE the first propagation so the
            // OrbitDriver tick that updateFromParameters() triggers reads the right shift + bounds
            // through GhostOrbitIconDrivePatch (otherwise that in-call frame would propagate at the
            // live clock against stale/absent bounds for one frame).
            ghostOrbitEpochShift[vessel.persistentId] = loopEpochShiftSeconds;

            // Store orbit segment time bounds for arc clipping (GhostOrbitArcPatch) + the
            // line-toggle Postfix, shifted into the LIVE frame for loop replay so the live-clock
            // comparisons in those consumers are unchanged. The arc-clip + icon-drive patches
            // subtract the shift (via ghostOrbitEpochShift) to recover the recorded UTs for their
            // eccentric-anomaly / propagation math against the raw-epoch orbit.
            ghostOrbitBounds[vessel.persistentId] =
                (segment.startUT + loopEpochShiftSeconds, segment.endUT + loopEpochShiftSeconds);
            // Mark/unmark this ghost as loop-shifted so TryGetVisibleOrbitBoundsForGhostVessel
            // returns these (shifted) bounds rather than re-deriving raw recorded bounds from the
            // OrbitSegments at the live UT, which would desync the arc/icon clip from the live
            // frame when the live clock falls inside the member's recorded window.
            if (loopEpochShiftSeconds != 0.0)
                ghostOrbitLoopShiftedPids.Add(vessel.persistentId);
            else
                ghostOrbitLoopShiftedPids.Remove(vessel.persistentId);

            vessel.orbitDriver.updateFromParameters();
            NormalizeGhostOrbitDriverTargetIdentity(vessel, logContext);

            // After SOI change, force the orbit renderer to recalculate for the new body.
            // Without this, the orbit line stays clipped to the old body's SOI radius.
            // DrawOrbit is protected, so toggle the renderer off/on to force a full rebuild.
            if (soiChanged && vessel.orbitRenderer != null)
            {
                vessel.orbitRenderer.drawMode = OrbitRendererBase.DrawMode.REDRAW_AND_RECALCULATE;
                vessel.orbitRenderer.enabled = false;
                vessel.orbitRenderer.enabled = true;
                ParsekLog.Verbose(Tag,
                    string.Format(ic, "Forced orbit renderer redraw for {0} after SOI change", logContext));
            }

            // Remember the body we just applied so the next frame's soiChanged compares against it
            // (KSP may transiently re-transition orbitDriver.referenceBody before then).
            ghostLastAppliedOrbitBody[vessel.persistentId] = body.name;

            // Diagnostic logging: orbit elements + hyperbola extent
            Orbit drv = vessel.orbitDriver.orbit;
            double periapsis = drv.PeR;
            double semiMinorAxis = drv.semiMinorAxis;
            // For hyperbolic: max eccentric anomaly = acos(-1/e)
            double maxE = drv.eccentricity >= 1.0
                ? System.Math.Acos(-1.0 / drv.eccentricity) : System.Math.PI;
            // Position at max eccentric anomaly = furthest point
            Vector3d farPos = drv.eccentricity >= 1.0
                ? drv.getPositionFromEccAnomaly(maxE * 0.99) : Vector3d.zero; // 0.99 to avoid singularity
            double farDist = farPos.magnitude;

            // This apply runs every frame the orbit is re-applied (per ghost), so an unconditional
            // Verbose floods the log (it was ~31% of all Parsek output in a 2-minute capture). Log ON
            // CHANGE per ghost instead, keyed on the orbit SHAPE (body + sma + ecc + renderer state):
            // every distinct orbit applied still logs, a frozen/steady orbit logs once and then reports a
            // suppressed=N count (which is clearer than thousands of identical lines for spotting a stuck
            // orbit), and SOI/body changes and renderer flips register as discrete events. No debugging
            // signal is lost; the per-frame repeat noise is. Ghost create/recreate is still logged loudly
            // at INFO ("Created ghost vessel" / "create-segment-done"). sma:F0/ecc:F4 in the key are coarse
            // enough to avoid float-jitter churn (these come from a fixed segment, so they are stable).
            ParsekLog.VerboseOnChange(Tag,
                "orbit-applied-" + vessel.persistentId.ToString(ic),
                string.Format(ic, "{0}|{1:F0}|{2:F4}|{3}|{4}",
                    body.name, segment.semiMajorAxis, drv.eccentricity,
                    vessel.orbitRenderer?.enabled, vessel.orbitRenderer?.drawMode),
                string.Format(ic,
                    "Orbit updated for {0} body={1} sma={2:F0} ecc={3:F6} " +
                    "periapsis={4:F0} semiMinor={5:F0} maxE={6:F2}rad farDist={7:F0}m " +
                    "rendererEnabled={8} rendererDrawMode={9}",
                    logContext, body.name, segment.semiMajorAxis,
                    drv.eccentricity, periapsis, semiMinorAxis, maxE, farDist,
                    vessel.orbitRenderer?.enabled, vessel.orbitRenderer?.drawMode));
        }

        /// <summary>
        /// Strip ghost ProtoVessels from flightState before save.
        /// Ghost vessels are transient — reconstructed from recording data on load.
        /// Called from ParsekScenario.OnSave.
        /// </summary>
        internal static int StripFromSave(FlightState flightState)
        {
            if (flightState == null || ghostMapVesselPids.Count == 0)
                return 0;

            int stripped = flightState.protoVessels.RemoveAll(
                pv => ghostMapVesselPids.Contains(pv.persistentId));

            if (stripped > 0)
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Stripped {0} ghost ProtoVessel(s) from save", stripped));

            return stripped;
        }

        /// <summary>
        /// Pure: find recording IDs that are superseded by a later recording in the same chain.
        /// A recording is superseded if another recording's ParentRecordingId points to it.
        /// Chain-tip recordings are NOT in the returned set.
        /// </summary>
        internal static HashSet<string> FindSupersededRecordingIds(IReadOnlyList<Recording> recordings)
        {
            var superseded = new HashSet<string>();
            if (recordings == null) return superseded;
            for (int i = 0; i < recordings.Count; i++)
            {
                string parentId = recordings[i].ParentRecordingId;
                if (!string.IsNullOrEmpty(parentId))
                    superseded.Add(parentId);
            }
            return superseded;
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

            bool isChainLooping = !string.IsNullOrEmpty(rec.ChainId)
                && RecordingStore.IsChainLooping(rec.ChainId);
            // Scene-agnostic #573 rewind lift: a standalone Rewind-to-Launch target the
            // player did not re-fly (no live same-craft vessel present) still spawns its
            // recorded terminal in the Tracking Station, identical to Flight and KSC.
            var spawnResult = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, false, isChainLooping,
                treeContext: null,
                GhostPlaybackLogic.ResolveRewindSuppressionLiveLaunchPresence(rec));
            if (!spawnResult.needsSpawn)
                return spawnResult;

            if (RecordingStore.IsChainMidSegment(rec))
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
                case "terminated ghost chain":
                    return TrackingStationSpawnSkipTerminatedGhostChain;
                default:
                    return reason;
            }
        }

        /// <summary>
        /// Tracking Station visibility suppression is time-aware: a recording is hidden only
        /// after one of its child recordings has actually started by the current UT. This keeps
        /// the current atmospheric continuation visible even when a later future leg already
        /// exists in the committed chain.
        /// </summary>
        internal static HashSet<string> FindTrackingStationSuppressedRecordingIds(
            IReadOnlyList<Recording> recordings, double currentUT)
        {
            var scenario = ParsekScenario.Instance;
            var supersedes = object.ReferenceEquals(null, scenario)
                ? null
                : scenario.RecordingSupersedes;
            var retirements = object.ReferenceEquals(null, scenario)
                ? null
                : scenario.RecordingRewindRetirements;
            return FindTrackingStationSuppressedRecordingIds(recordings, currentUT, supersedes, retirements);
        }

        internal static HashSet<string> FindTrackingStationSuppressedRecordingIds(
            IReadOnlyList<Recording> recordings, double currentUT,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            return FindTrackingStationSuppressedRecordingIds(
                recordings,
                currentUT,
                supersedes,
                retirements: null);
        }

        internal static HashSet<string> FindTrackingStationSuppressedRecordingIds(
            IReadOnlyList<Recording> recordings, double currentUT,
            IReadOnlyList<RecordingSupersedeRelation> supersedes,
            IReadOnlyList<RecordingRewindRetirement> retirements)
        {
            var suppressed = new HashSet<string>();
            if (recordings == null)
                return suppressed;

            for (int i = 0; i < recordings.Count; i++)
            {
                Recording child = recordings[i];
                string parentId = child?.ParentRecordingId;
                if (string.IsNullOrEmpty(parentId))
                    continue;

                if (HasTrackingStationChildStarted(child, currentUT))
                    suppressed.Add(parentId);
            }

            AddSupersedeRelationSuppressedRecordingIds(suppressed, recordings, supersedes);
            AddRewindRetiredSuppressedRecordingIds(suppressed, recordings, retirements);
            return suppressed;
        }

        private static void AddSupersedeRelationSuppressedRecordingIds(
            HashSet<string> suppressed,
            IReadOnlyList<Recording> recordings,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (suppressed == null || recordings == null || supersedes == null || supersedes.Count == 0)
                return;

            for (int i = 0; i < recordings.Count; i++)
            {
                Recording rec = recordings[i];
                if (!EffectiveState.IsSupersededByRelation(rec, supersedes))
                    continue;
                suppressed.Add(rec.RecordingId);
            }
        }

        private static void AddRewindRetiredSuppressedRecordingIds(
            HashSet<string> suppressed,
            IReadOnlyList<Recording> recordings,
            IReadOnlyList<RecordingRewindRetirement> retirements)
        {
            if (suppressed == null || recordings == null || retirements == null || retirements.Count == 0)
                return;

            // Cascade overload: parent-anchored debris of a retired recording
            // inherits the retirement so the orphan debris ghost does not
            // render at the tracking station alongside the restored parent's
            // own debris.
            var retiredIds = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);
            for (int i = 0; i < recordings.Count; i++)
            {
                Recording rec = recordings[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (!retiredIds.Contains(rec.RecordingId))
                    continue;
                suppressed.Add(rec.RecordingId);
            }
        }

        private static void AddActiveSessionSuppressedRecordingIds(
            HashSet<string> suppressed, IReadOnlyList<Recording> recordings)
        {
            if (suppressed == null || recordings == null)
                return;

            for (int i = 0; i < recordings.Count; i++)
            {
                if (!IsSuppressedByActiveSession(i))
                    continue;

                string recordingId = recordings[i]?.RecordingId;
                if (!string.IsNullOrEmpty(recordingId))
                    suppressed.Add(recordingId);
            }
        }

        /// <summary>
        /// Pure: resolve which orbital representation, if any, should back a
        /// tracking-station ghost at the current UT. Current visible orbit
        /// segments win over eventual terminal-orbit data so the tracking
        /// station never advertises a future body/SOI before the recording
        /// has actually reached that phase.
        /// </summary>
        internal static TrackingStationGhostSource ResolveTrackingStationGhostSource(
            Recording rec,
            bool isSuppressed,
            double currentUT,
            out OrbitSegment segment,
            out string skipReason)
        {
            int stateVectorCachedIndex = -1;
            return ResolveTrackingStationGhostSource(
                rec,
                isSuppressed,
                false,
                currentUT,
                ref stateVectorCachedIndex,
                out segment,
                out _,
                out skipReason);
        }

        internal static TrackingStationGhostSource ResolveTrackingStationGhostSource(
            Recording rec,
            bool isSuppressed,
            bool realVesselExists,
            double currentUT,
            ref int stateVectorCachedIndex,
            out OrbitSegment segment,
            out TrajectoryPoint stateVectorPoint,
            out string skipReason)
        {
            return ResolveTrackingStationGhostSourceCore(
                rec,
                isSuppressed,
                realVesselExists,
                currentUT,
                ref stateVectorCachedIndex,
                out segment,
                out stateVectorPoint,
                out skipReason,
                batch: null,
                recordingIndex: -1,
                context: "direct");
        }

        private static TrackingStationGhostSource ResolveTrackingStationGhostSourceCore(
            Recording rec,
            bool isSuppressed,
            bool realVesselExists,
            double currentUT,
            ref int stateVectorCachedIndex,
            out OrbitSegment segment,
            out TrajectoryPoint stateVectorPoint,
            out string skipReason,
            TrackingStationGhostSourceBatch batch,
            int recordingIndex,
            string context,
            bool loopMemberInWindow = false,
            bool liveLaunchMatchedAnchorOfActiveMember = false,
            bool transferMemberDescentContinuation = false)
        {
            // Both create callers (the per-frame lifecycle pass AND the one-shot startup create)
            // now sample at the loop-mapped effUT and pass loopMemberInWindow, so for an in-window
            // loop member the resolver's currentUT < EndUT gate returns before-terminal-orbit and
            // never reaches a terminal-orbit branch. acceptTerminalOrbitForLoopSynthesis stays false
            // for BOTH create paths per plan section 1.4: a no-segment terminal-orbit synthesis at
            // a raw recorded UT would seed at the wrong position (the PR #967 class), and a
            // no-segment loop member's proto-vessel instead comes up on a later loop-aware refresh
            // tick (the refresh path is the one that opts into the synthesis when it is safe).
            // loopMemberInWindow (true whenever effUT != live currentUT) lets a looped member draw
            // its map ghost alongside a persisted real terminal vessel; false on the pure-predicate
            // wrapper keeps it unchanged.
            TrackingStationGhostSource source = ResolveMapPresenceGhostSource(
                rec,
                isSuppressed,
                IsTrackingStationRecordingMaterialized(rec, realVesselExists),
                currentUT,
                true,
                logOperationName: null,
                ref stateVectorCachedIndex,
                out segment,
                out stateVectorPoint,
                out skipReason,
                recordingIndex: recordingIndex,
                acceptTerminalOrbitForLoopSynthesis: false,
                loopMemberInWindow: loopMemberInWindow,
                liveLaunchMatchedAnchorOfActiveMember: liveLaunchMatchedAnchorOfActiveMember,
                transferMemberDescentContinuation: transferMemberDescentContinuation);

            // BUG-B: in the live tracking-station create paths (batch != null), a committed recording
            // that WOULD seed a map icon (source != None) but is purely historical (the player only ever
            // progressed past it in normal forward time and never rewound to replay it) is suppressed so
            // it does not draw a duplicate ghost of a still-live vessel. Placed AFTER source resolution
            // so the existing, more-specific skip reasons (already-spawned, no-orbit, before-activation)
            // are preserved; only an otherwise-renderable historical recording is overridden. The
            // pure-predicate "direct" wrapper passes batch == null and is left unchanged. Loop members
            // (loopMemberInWindow) and the active re-fly session are exempt, and rec.LoopPlayback covers
            // a per-recording loop sampling at live UT; for every other recording reaching here in a
            // create path currentUT is the live UT (effUT == currentUT), so the latch sees live time.
            if (source != TrackingStationGhostSource.None
                && batch != null
                && !loopMemberInWindow
                && !rec.LoopPlayback
                && SessionSuppressionState.ActiveMarker == null)
            {
                double historicalActivationStartUT = GhostPlaybackEngine.ResolveGhostActivationStartUT(rec);
                PlaybackScopeTracker.NotePlayhead(rec.RecordingId, currentUT, historicalActivationStartUT);
                if (PlaybackScopeTracker.IsHistoricalNeverReplayed(
                        rec.RecordingId, currentUT, historicalActivationStartUT))
                {
                    source = TrackingStationGhostSource.None;
                    segment = default(OrbitSegment);
                    stateVectorPoint = default(TrajectoryPoint);
                    skipReason = "historical-not-replayed";
                }
            }

            LogTrackingStationGhostSourceDecision(
                context,
                recordingIndex,
                rec,
                currentUT,
                source,
                skipReason,
                BuildTrackingStationGhostSourceDetail(
                    rec,
                    currentUT,
                    source,
                    skipReason,
                    segment,
                    stateVectorPoint),
                batch);

            return source;
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
            if (baseMissionCount == 0 && routeMissions.Count == 0)
                return GhostPlaybackLogic.LoopUnitSet.Empty;

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
            // Pass the resolved mode explicitly: MissionLoopUnitBuilder.Build defaults to Tight, but the
            // running setting (Loose default) is what every per-frame pass uses, so do not rely on the
            // builder's parameter default here.
            TransitedBodyRotationMode tbrMode = ParsekSettings.Current?.TransitedBodyRotationMode
                                                ?? TransitedBodyRotationMode.Loose;
            return MissionLoopUnitBuilder.Build(
                missions,
                RecordingStore.CommittedTrees,
                committed,
                autoLoopIntervalSeconds,
                bodyInfo,
                tbrMode);
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
            GhostPlaybackLogic.LoopUnitSet loopUnits = BuildStartupTrackingStationLoopUnits(committed);

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
                // committed index is a loop-unit member. Inert (effUT == currentUT, renderHidden false)
                // for every non-member and when loopUnits is Empty. A member outside its loop window this
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

        /// <summary>
        /// Ensure all tracked ghost vessels have mapObject and orbitRenderer.
        /// During SpaceTracking.Awake prefix, MapView.fetch may not be initialized yet
        /// (Unity doesn't guarantee Awake ordering), causing Vessel.AddOrbitRenderer()
        /// to silently skip creation. This method calls AddOrbitRenderer via Traverse
        /// on ghosts missing their renderer. Must be called after all Awake methods
        /// complete (e.g., from a buildVesselsList Prefix or from Start). (#195)
        /// </summary>
        internal static int EnsureGhostOrbitRenderers()
        {
            if (MapView.fetch == null)
            {
                ParsekLog.Warn(Tag, "EnsureGhostOrbitRenderers: MapView.fetch is null — cannot create orbit renderers");
                return 0;
            }

            // Collect unique ghost vessels from both dictionaries
            var ghosts = new HashSet<Vessel>();
            foreach (var v in vesselsByChainPid.Values)
                if (v != null) ghosts.Add(v);
            foreach (var v in vesselsByRecordingIndex.Values)
                if (v != null) ghosts.Add(v);

            int fixedCount = 0;
            foreach (var v in ghosts)
            {
                bool needsMapObj = v.mapObject == null;
                bool needsRenderer = v.orbitRenderer == null;

                if (!needsMapObj && !needsRenderer)
                    continue;

                // Call the private AddOrbitRenderer — it creates both mapObject and
                // orbitRenderer if missing. Now that MapView.fetch is available, the
                // guard inside AddOrbitRenderer passes. The method is idempotent.
                Traverse.Create(v).Method("AddOrbitRenderer").GetValue();

                if (v.orbitRenderer == null && needsRenderer)
                {
                    ParsekLog.Warn(Tag, string.Format(ic,
                        "EnsureGhostOrbitRenderers: AddOrbitRenderer via Traverse had no effect on '{0}' pid={1} — " +
                        "method may have been renamed in a KSP update",
                        v.vesselName, v.persistentId));
                }

                // Configure rendering (same as post-Load block)
                if (v.orbitRenderer != null)
                {
                    v.orbitRenderer.drawMode = OrbitRendererBase.DrawMode.REDRAW_AND_RECALCULATE;
                    v.orbitRenderer.drawIcons = OrbitRendererBase.DrawIcons.ALL;
                    if (!v.orbitRenderer.enabled)
                        v.orbitRenderer.enabled = true;
                }

                fixedCount++;
                ParsekLog.Info(Tag, string.Format(ic,
                    "EnsureGhostOrbitRenderers: fixed ghost '{0}' pid={1} (mapObj was null={2}, renderer was null={3}, " +
                    "now mapObj={4} renderer={5})",
                    v.vesselName, v.persistentId, needsMapObj, needsRenderer,
                    v.mapObject != null, v.orbitRenderer != null));
            }

            if (fixedCount > 0)
                ParsekLog.Info(Tag, string.Format(ic,
                    "EnsureGhostOrbitRenderers: fixed {0} of {1} ghost vessel(s)", fixedCount, ghosts.Count));
            else if (ghosts.Count > 0)
                ParsekLog.Verbose(Tag, string.Format(ic,
                    "EnsureGhostOrbitRenderers: all {0} ghost vessel(s) already have orbit renderers", ghosts.Count));

            return fixedCount;
        }

        /// <summary>
        /// Get the ghost Vessel for a chain PID, or null if none exists.
        /// Used for target transfer when chain resolves.
        /// </summary>
        internal static Vessel GetGhostVessel(uint chainPid)
        {
            vesselsByChainPid.TryGetValue(chainPid, out Vessel vessel);
            return vessel;
        }

        /// <summary>
        /// Find the recording index for a ghost map vessel by its PID.
        /// O(1) via reverse lookup dictionary. Returns -1 if not found.
        /// </summary>
        internal static int FindRecordingIndexByVesselPid(uint vesselPid)
        {
            if (vesselPidToRecordingIndex.TryGetValue(vesselPid, out int index))
                return index;
            return -1;
        }

        /// <summary>
        /// Physical-identity correlation for the map-render shadow (Phase 4): resolve a live ghost
        /// vessel PID to its SOURCE committed trajectory + index (the recording the ghost was created
        /// from and is rendering). This is NOT an ERS visibility query — it is the same raw committed
        /// read the existing map-presence path makes for this pid, kept inside this already-allowlisted
        /// file so the shadow assembles the chain from exactly the trajectory the old path renders. The
        /// pid is already a live, visible map ghost, so no ComputeERS gate applies. Returns false when
        /// the pid is not a tracked ghost or its recording is gone.
        /// </summary>
        internal static bool TryGetCommittedTrajectoryForPid(
            uint vesselPid, out IPlaybackTrajectory trajectory, out int index)
        {
            trajectory = null;
            index = FindRecordingIndexByVesselPid(vesselPid);
            if (index < 0)
                return false;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || index >= committed.Count)
                return false;
            trajectory = committed[index];
            return trajectory != null;
        }

        /// <summary>
        /// Find the stable recording ID captured when a recording-index ghost was created.
        /// Returns null if the vessel is not a recording-index ghost or the source
        /// trajectory did not expose a recording ID.
        /// </summary>
        internal static string FindRecordingIdByVesselPid(uint vesselPid)
        {
            return vesselPidToRecordingId.TryGetValue(vesselPid, out string recordingId)
                ? recordingId
                : null;
        }

        /// <summary>
        /// Returns true if a ghost map ProtoVessel exists for the given recording index.
        /// Used by ParsekUI to suppress the green dot marker when the native KSP icon is active.
        /// </summary>
        internal static bool HasGhostVesselForRecording(int recordingIndex)
        {
            if (vesselsByRecordingIndex.ContainsKey(recordingIndex))
                return true;
            // Overlap recordings live in overlapInstanceVessels (single-ownership rule), NOT
            // vesselsByRecordingIndex. Fall through to the newest live instance so watch-camera
            // focus / TS-Fly / UI marker suppression / polyline owner resolution see a ghost
            // (matching the legacy "one icon == newest cycle" behavior) instead of "no ghost".
            // M1: EXCLUDE the boundary-overlap secondary so a launch-hold member whose primary is
            // renderHidden reports "no ghost" rather than the secondary's identity.
            return GetNewestOverlapInstancePidForRecording(recordingIndex, excludeBoundarySecondary: true) != 0;
        }

        /// <summary>
        /// Returns the persistentId of the ghost map vessel for a recording index, or 0 if none.
        /// Used to check ghostsWithSuppressedIcon for the below-atmosphere icon handoff.
        /// </summary>
        internal static uint GetGhostVesselPidForRecording(int recordingIndex)
        {
            if (vesselsByRecordingIndex.TryGetValue(recordingIndex, out Vessel v))
                return v.persistentId;
            // Overlap recordings: fall through to the NEWEST live overlap instance's pid (the cycle
            // the legacy single-icon path represented). FindRecordingIndexByVesselPid still works for
            // EVERY instance (vesselPidToRecordingIndex is populated per instance).
            // M1: EXCLUDE the boundary-overlap secondary so it never shadows the launch recording's pid
            // (the chain case where the launch member's primary has no vesselsByRecordingIndex entry).
            return GetNewestOverlapInstancePidForRecording(recordingIndex, excludeBoundarySecondary: true);
        }

        // ---- Phase 8e S0 Instrument 1: coverage-closure accounting (PURELY ADDITIVE) ----

        /// <summary>
        /// Clears this frame's coverage-closure sets. Called by the polyline Driver at the TOP of every
        /// LateUpdate decide walk (the same lifecycle the ownership-publish sets clear on) so the sets
        /// reflect ONLY the recordings the walk drew this frame. Diagnostic-only; nothing in the live
        /// render path reads these.
        /// </summary>
        internal static void ClearFrameCoverageSets()
        {
            drawnRecordingIdsThisFrame.Clear();
            protoLessCoverageRecordingIdsThisFrame.Clear();
        }

        /// <summary>
        /// Records that the autonomous polyline walk decided to DRAW a non-orbital leg for
        /// <paramref name="recordingId"/> this frame (the will-draw == actual-draw signal). When
        /// <paramref name="ghostPid"/> is 0 the recording has NO ProtoVessel ghost (pid-0
        /// atmospheric/ascent), so it is ALSO added to the proto-less coverage set - the Director's
        /// genuine accounting of a recording its enumerated <see cref="ghostMapVesselPids"/> set cannot
        /// see. Called from the Driver's decide walk, already gated by the Driver on
        /// <see cref="MapRenderTrace.IsEnabled"/>. Diagnostic-only; no render/draw effect.
        /// </summary>
        internal static void NoteDrawnRecordingCoverage(string recordingId, uint ghostPid)
        {
            if (string.IsNullOrEmpty(recordingId))
                return;
            drawnRecordingIdsThisFrame.Add(recordingId);
            // pid 0 => no proto vessel => the Director's enumerated set (ghostMapVesselPids) misses it;
            // the non-proto polyline path is the ONLY thing rendering it, so account for it here. A
            // proto-BEARING recording is intentionally NOT added (it is accounted via the pid bridge),
            // which keeps the assertion non-vacuous.
            if (ghostPid == 0)
                protoLessCoverageRecordingIdsThisFrame.Add(recordingId);
        }

        /// <summary>
        /// PURE accounting predicate (S0 Instrument 1): is a DRAWN recording ACCOUNTED FOR by the
        /// Director's coverage? Expressed entirely in the RecordingId domain (the draw domain). A drawn
        /// recording is accounted when EITHER it maps to a proto-bearing ghost pid (the Director's
        /// enumerated <see cref="ghostMapVesselPids"/> set, bridged back to RecordingIds via
        /// <see cref="vesselPidToRecordingId"/>) OR it is in the proto-less coverage set (the non-proto
        /// path's accounting). A drawn recording in NEITHER is the deletion-blocker: the autonomous walk
        /// drew it but the Director's accounted set does not cover it, so deleting the legacy ownership
        /// would silently drop it. Unit-testable (no Unity / no live state - all three inputs are
        /// passed in), so it can FIRE for a genuinely-unaccounted recording and PASS for an accounted one.
        /// </summary>
        internal static bool IsDrawnRecordingAccounted(
            string drawnRecordingId,
            ICollection<string> protoBearingRecordingIds,
            ICollection<string> protoLessCoverageRecordingIds)
        {
            if (string.IsNullOrEmpty(drawnRecordingId))
                return true; // a null/empty id is not a real drawn recording; never flag it
            if (protoBearingRecordingIds != null && protoBearingRecordingIds.Contains(drawnRecordingId))
                return true;
            if (protoLessCoverageRecordingIds != null
                && protoLessCoverageRecordingIds.Contains(drawnRecordingId))
                return true;
            return false;
        }

        /// <summary>
        /// Runs the S0 coverage-closure assertion over this frame's drawn-recording set, invoking
        /// <paramref name="onUnaccounted"/> once per drawn recording that is NEITHER proto-bearing NOR in
        /// the proto-less coverage set. Builds the proto-bearing RecordingId set ONCE (from the live
        /// <see cref="vesselPidToRecordingId"/> values - the Director's enumerated pid set bridged to the
        /// RecordingId domain) so the per-recording check is O(1). The callback receives
        /// <c>(recordingId, protoBearingCount, protoLessCoverageCount, drawnCount)</c> for the anomaly
        /// line. Diagnostic-only; the caller gates on <see cref="MapRenderTrace.IsEnabled"/>. No-ops when
        /// nothing drew this frame.
        /// </summary>
        internal static void AssertDrawnRecordingsAccounted(
            System.Action<string, int, int, int> onUnaccounted)
        {
            if (drawnRecordingIdsThisFrame.Count == 0)
                return;

            // Bridge the proto-bearing pids back into the RecordingId DOMAIN (the draw domain). Comparing
            // a pid-0 recording against a pid SET would always report "absent" - the task's explicit
            // false-positive trap. The values of vesselPidToRecordingId ARE the proto-bearing RecordingIds.
            protoBearingRecordingIdScratch.Clear();
            foreach (var kv in vesselPidToRecordingId)
                if (!string.IsNullOrEmpty(kv.Value))
                    protoBearingRecordingIdScratch.Add(kv.Value);

            foreach (string recId in drawnRecordingIdsThisFrame)
            {
                if (IsDrawnRecordingAccounted(
                        recId, protoBearingRecordingIdScratch, protoLessCoverageRecordingIdsThisFrame))
                    continue;
                onUnaccounted?.Invoke(
                    recId,
                    protoBearingRecordingIdScratch.Count,
                    protoLessCoverageRecordingIdsThisFrame.Count,
                    drawnRecordingIdsThisFrame.Count);
            }
        }

        /// <summary>Test-only seam: stamp this frame's drawn / proto-less coverage sets so
        /// <see cref="AssertDrawnRecordingsAccounted"/> and the pid-bridge can be exercised end-to-end
        /// from xUnit (the real producer is the Unity-coupled Driver walk). Mirrors the live
        /// <see cref="NoteDrawnRecordingCoverage"/> semantics.</summary>
        internal static void SetFrameCoverageForTesting(string recordingId, bool drawn, bool protoLess)
        {
            if (string.IsNullOrEmpty(recordingId))
                return;
            if (drawn) drawnRecordingIdsThisFrame.Add(recordingId);
            else drawnRecordingIdsThisFrame.Remove(recordingId);
            if (protoLess) protoLessCoverageRecordingIdsThisFrame.Add(recordingId);
            else protoLessCoverageRecordingIdsThisFrame.Remove(recordingId);
        }

        /// <summary>Test-only seam: stamp the proto-bearing pid bridge (pid -> recordingId) so the
        /// assertion's RecordingId-domain bridge can be exercised from xUnit.</summary>
        internal static void SetProtoBearingPidForTesting(uint pid, string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) vesselPidToRecordingId.Remove(pid);
            else vesselPidToRecordingId[pid] = recordingId;
        }

        /// <summary>Test-only: clear all coverage-closure state (the S0 frame sets + the scratch), so a
        /// test starts from a known-empty accounting.</summary>
        internal static void ResetCoverageSetsForTesting()
        {
            drawnRecordingIdsThisFrame.Clear();
            protoLessCoverageRecordingIdsThisFrame.Clear();
            protoBearingRecordingIdScratch.Clear();
        }

        internal static bool TryGetCommittedRecordingById(
            string recordingId,
            out int recordingIndex,
            out Recording recording)
        {
            return TryGetCommittedRecordingById(
                RecordingStore.CommittedRecordings,
                recordingId,
                out recordingIndex,
                out recording);
        }

        internal static bool TryGetCommittedRecordingById(
            IReadOnlyList<Recording> committed,
            string recordingId,
            out int recordingIndex,
            out Recording recording)
        {
            recordingIndex = -1;
            recording = null;

            if (committed == null || string.IsNullOrEmpty(recordingId))
                return false;

            for (int i = 0; i < committed.Count; i++)
            {
                Recording current = committed[i];
                if (current == null
                    || !string.Equals(current.RecordingId, recordingId, StringComparison.Ordinal))
                {
                    continue;
                }

                recordingIndex = i;
                recording = current;
                return true;
            }

            return false;
        }

        internal static Recording GetCommittedRecordingByRawIndex(int recordingIndex)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || recordingIndex < 0 || recordingIndex >= committed.Count)
                return null;
            return committed[recordingIndex];
        }

        /// <summary>
        /// Resolve the visible orbit time window for a ghost vessel at the current UT.
        /// Recording-index ghosts use dynamic same-body gap carry; other segment-based
        /// ghosts fall back to their stored bounds.
        /// </summary>
        internal static bool TryGetVisibleOrbitBoundsForGhostVessel(
            uint vesselPid, double currentUT, out double startUT, out double endUT)
        {
            startUT = 0;
            endUT = 0;

            // Resolved once up front so both single-segment short-circuit branches below can pass it to
            // ExpandStoredBoundsAcrossEquivalentSegments. O(1) dictionary lookup, no side effects.
            int recordingIndex = FindRecordingIndexByVesselPid(vesselPid);

            // Loop-shifted ghost: the stored bounds are already in the live frame and match the
            // shifted orbit epoch, so use them directly. Re-deriving from the raw recorded
            // OrbitSegments at the live UT (below) would return raw recorded UTs that desync from
            // the shifted orbit when the live clock falls inside the member's recorded window.
            // Empty set off the loop path, so this never fires for non-loop ghosts.
            if (ghostOrbitLoopShiftedPids.Contains(vesselPid)
                && TryGetStoredOrbitBoundsForGhostVessel(
                    vesselPid,
                    currentUT,
                    "loop-shifted",
                    out startUT,
                    out endUT))
            {
                // The stored bounds are a SINGLE applied OrbitSegment, so the arc clip would draw only
                // that fragment of a multi-fragment same-orbit coast (the loop SOI-approach hyperbola
                // splits into adjacent equivalent fragments). Expand the arc-clip window across those
                // element-equivalent fragments (read-only; ghostOrbitBounds is untouched) so the line
                // draws the whole approach in one frame instead of one piece then the rest seconds later.
                ExpandStoredBoundsAcrossEquivalentSegments(
                    vesselPid, recordingIndex, ref startUT, ref endUT, "loop-shifted");
                return true;
            }

            if (IsEndpointTailRecordingGhost(vesselPid, recordingIndex)
                && TryGetStoredOrbitBoundsForGhostVessel(
                    vesselPid,
                    currentUT,
                    "endpoint-tail",
                    out startUT,
                    out endUT))
            {
                // Same single-segment clip structure as the loop-shifted branch. An EndpointTail
                // synthetic segment's startUT may not exist verbatim in the recorded OrbitSegments;
                // when no seed matches the expansion is a no-op (keeps stored bounds, behaviour-identical
                // to today). Otherwise it widens across the equivalent recorded fragments.
                ExpandStoredBoundsAcrossEquivalentSegments(
                    vesselPid, recordingIndex, ref startUT, ref endUT, "endpoint-tail");
                return true;
            }

            var committed = RecordingStore.CommittedRecordings;
            if (recordingIndex >= 0
                && committed != null
                && recordingIndex < committed.Count
                && committed[recordingIndex].HasOrbitSegments
                && TrajectoryMath.TryGetOrbitWindowForMapDisplay(
                    committed[recordingIndex].OrbitSegments, currentUT,
                    out OrbitSegment segment,
                    out startUT,
                    out endUT,
                    out int firstVisibleIndex,
                    out int lastVisibleIndex,
                    out bool carriedAcrossGap))
            {
                ParsekLog.VerboseOnChange(Tag,
                    string.Format(ic, "visible-window-segment|{0}", vesselPid),
                    string.Format(ic,
                        "segment|rec={0}|segment={1:F3}-{2:F3}|gap={3}",
                        recordingIndex,
                        segment.startUT,
                        segment.endUT,
                        carriedAcrossGap ? "gap" : "segment"),
                    string.Format(ic,
                        "Map-visible orbit window pid={0} recIndex={1} ut={2:F2} body={3} " +
                        "segmentUT={4:F2}-{5:F2} windowUT={6:F2}-{7:F2} windowIndices={8}-{9} gapCarry={10}",
                        vesselPid,
                        recordingIndex,
                        currentUT,
                        segment.bodyName,
                        segment.startUT,
                        segment.endUT,
                        startUT,
                        endUT,
                        firstVisibleIndex,
                        lastVisibleIndex,
                        carriedAcrossGap));

                if (PlaybackOrbitDiagnostics.TryBuildMapPredictedTailLog(
                    recordingIndex,
                    vesselPid,
                    committed[recordingIndex],
                    segment,
                    currentUT,
                    startUT,
                    endUT,
                    carriedAcrossGap,
                    out string mapRenderKey,
                    out string mapRenderMessage))
                {
                    ParsekLog.VerboseRateLimited("MapRender", mapRenderKey, mapRenderMessage, 1.0);
                }

                return true;
            }

            if (recordingIndex >= 0
                && committed != null
                && recordingIndex < committed.Count
                && committed[recordingIndex].HasOrbitSegments)
            {
                ParsekLog.VerboseOnChange(Tag,
                    string.Format(ic, "visible-window-none|{0}", vesselPid),
                    string.Format(ic, "none|rec={0}", recordingIndex),
                    string.Format(ic,
                        "Map-visible orbit window unavailable source=none reason=no-active-equivalent-segment " +
                        "pid={0} recIndex={1} ut={2:F2} — " +
                        "no active or equivalent same-orbit segment chain",
                        vesselPid, recordingIndex, currentUT));
            }

            if (TryGetStoredOrbitBoundsForGhostVessel(
                    vesselPid,
                    currentUT,
                    "fallback",
                    out startUT,
                    out endUT))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Resolve the body-frame time window for a ghost vessel at the current UT — the
        /// bounds covering all consecutive same-body OrbitSegments around the playback head.
        /// This is the wider window used by <see cref="Parsek.Patches.GhostOrbitLinePatch"/>
        /// to decide whether the rendered orbit line should stay on. Inside a body frame
        /// the line stays continuously visible across inter-segment burns / dropouts; only
        /// the SOI / body change at a frame boundary blanks it. Arc clipping and icon
        /// clamping still use <see cref="TryGetVisibleOrbitBoundsForGhostVessel"/> (segment
        /// bounds), so the line shape and icon position update per-segment as before.
        /// Reads the per-PID cache <see cref="ghostBodyFrameOrbitBounds"/>, which is populated
        /// by <see cref="UpdateGhostOrbitForRecording"/> at the same time the segment is applied
        /// (so the loop-epoch shift is baked in identically to <see cref="ghostOrbitBounds"/>).
        /// </summary>
        internal static bool TryGetBodyFrameOrbitBoundsForGhostVessel(
            uint vesselPid, double currentUT, out double startUT, out double endUT)
        {
            startUT = 0;
            endUT = 0;

            if (ghostBodyFrameOrbitBounds.TryGetValue(vesselPid, out var bf))
            {
                startUT = bf.startUT;
                endUT = bf.endUT;
                ParsekLog.VerboseOnChange(Tag,
                    string.Format(ic, "body-frame-bounds|{0}", vesselPid),
                    string.Format(ic, "body-frame|cached|{0:F3}-{1:F3}", startUT, endUT),
                    string.Format(ic,
                        "Body-frame orbit bounds pid={0} source=cached ut={1:F2} bodyFrameUT={2:F2}-{3:F2}",
                        vesselPid, currentUT, startUT, endUT));
                return true;
            }

            // Fallback to the stored per-segment bounds: a ghost that was created with
            // a segment but never had body-frame bounds cached (e.g., a chain-pid ghost
            // taking a different code path) still gets gated by its existing arc-clip
            // bounds rather than rendering perpetually.
            return TryGetStoredOrbitBoundsForGhostVessel(
                vesselPid,
                currentUT,
                "fallback-body-frame",
                out startUT,
                out endUT);
        }

        private static bool TryGetStoredOrbitBoundsForGhostVessel(
            uint vesselPid,
            double currentUT,
            string reason,
            out double startUT,
            out double endUT)
        {
            startUT = 0;
            endUT = 0;

            if (!ghostOrbitBounds.TryGetValue(vesselPid, out var bounds))
                return false;

            startUT = bounds.startUT;
            endUT = bounds.endUT;
            string source;
            if (string.Equals(reason, "endpoint-tail", StringComparison.Ordinal))
                source = "stored-bounds-endpoint-tail";
            else if (string.Equals(reason, "loop-shifted", StringComparison.Ordinal))
                source = "stored-bounds-loop-shifted";
            else if (string.Equals(reason, "loop-shifted-body-frame", StringComparison.Ordinal))
                source = "stored-bounds-loop-shifted-body-frame";
            else if (string.Equals(reason, "endpoint-tail-body-frame", StringComparison.Ordinal))
                source = "stored-bounds-endpoint-tail-body-frame";
            else if (string.Equals(reason, "fallback-body-frame", StringComparison.Ordinal))
                source = "stored-bounds-fallback-body-frame";
            else
                source = "stored-bounds-fallback";
            ParsekLog.VerboseOnChange(Tag,
                string.Format(ic, "visible-window-stored-bounds|{0}|{1}", vesselPid, reason),
                string.Format(ic, "stored-bounds|{0}|{1:F3}-{2:F3}", reason, startUT, endUT),
                string.Format(ic,
                    "Map-visible orbit window pid={0} source={1} ut={2:F2} windowUT={3:F2}-{4:F2}",
                    vesselPid, source, currentUT, startUT, endUT));
            return true;
        }

        /// <summary>
        /// Expand a stored SINGLE-segment arc-clip window (loop-shifted / endpoint-tail branches) across
        /// element-equivalent adjacent recorded OrbitSegments so the orbit line draws one continuous
        /// same-orbit arc instead of a single fragment. Mirrors the non-loop merge
        /// (<see cref="TrajectoryMath.TryGetOrbitWindowForMapDisplay"/>) the loop path short-circuits past.
        ///
        /// <para>The stored bounds (<paramref name="startUT"/>/<paramref name="endUT"/>) are in the LIVE
        /// frame (the orbit epoch + bounds were shifted by <c>loopEpochShiftSeconds</c> when applied); the
        /// recorded OrbitSegments are RAW. To compare/merge in ONE consistent frame this un-shifts the
        /// stored bounds to raw via <see cref="MapLiveUTToEffUT"/>, expands on the RAW segments, then
        /// re-applies +shift to the merged result. Writes back to the ref params ONLY when more than one
        /// fragment coalesced; a no-seed-match or single-fragment result leaves them at the stored values
        /// (behaviour-identical to before). Reads <c>RecordingStore.CommittedRecordings</c> only (the same
        /// raw committed read the adjacent non-loop branch makes); NEVER mutates <c>ghostOrbitBounds</c>,
        /// so the icon-drive / SetOrbit epoch stay on the single applied segment and only the drawn arc
        /// sweep widens.</para>
        /// </summary>
        private static void ExpandStoredBoundsAcrossEquivalentSegments(
            uint vesselPid, int recordingIndex, ref double startUT, ref double endUT, string reason)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (recordingIndex < 0
                || committed == null
                || recordingIndex >= committed.Count
                || committed[recordingIndex] == null
                || !committed[recordingIndex].HasOrbitSegments)
            {
                return;
            }

            double shift = GetGhostOrbitEpochShift(vesselPid);
            double rawStart = MapLiveUTToEffUT(startUT, shift);
            double rawEnd = MapLiveUTToEffUT(endUT, shift);

            if (!TrajectoryMath.TryExpandStoredSingleSegmentWindow(
                    committed[recordingIndex].OrbitSegments,
                    rawStart,
                    rawEnd,
                    out double rawExpandedStart,
                    out double rawExpandedEnd,
                    out int firstVisibleIndex,
                    out int lastVisibleIndex,
                    out int fragmentCount))
            {
                return;
            }

            if (fragmentCount <= 1)
                return;

            // The expansion is purely ADDITIVE: it widens one applied fragment across its element-
            // equivalent recorded neighbours so the same-orbit coast draws in one frame. It must NEVER
            // shrink the stored window. A re-aimed loop transfer stores the FULL synthesized
            // [departure, arrival] span, but the RAW recorded segments walked above were split mid-coast
            // (a recorded mid-course element change > the equivalence tolerance), so the walk stops at the
            // split and would otherwise truncate the drawn arc partway to the target. Union with the stored
            // window (startUT/endUT still hold the caller's stored bounds here) so the expansion can only
            // ever widen, never truncate.
            TrajectoryMath.UnionArcWindowWithStored(
                startUT, endUT,
                rawExpandedStart + shift, rawExpandedEnd + shift,
                out double widenedStartUT, out double widenedEndUT, out bool clampedToStored);
            startUT = widenedStartUT;
            endUT = widenedEndUT;

            // This resolves every render frame while a loop ghost approaches an SOI; emit only when the
            // coalesced window changes (VerboseOnChange) so the merge decision is captured without
            // per-frame spam, mirroring the stored-bounds VerboseOnChange site just above.
            ParsekLog.VerboseOnChange(Tag,
                string.Format(ic, "loop-arc-coalesce|{0}|{1}", vesselPid, reason),
                string.Format(ic, "coalesce|{0}|{1}-{2}|{3:F3}-{4:F3}|{5}",
                    fragmentCount, firstVisibleIndex, lastVisibleIndex, startUT, endUT, clampedToStored),
                string.Format(ic,
                    "Loop arc-window coalesced pid={0} recIndex={1} reason={2} fragments={3} " +
                    "segIndices={4}-{5} rawWindowUT={6:F2}-{7:F2} shiftedWindowUT={8:F2}-{9:F2} " +
                    "clampedToStored={10} loopShift={11:F2}",
                    vesselPid, recordingIndex, reason, fragmentCount,
                    firstVisibleIndex, lastVisibleIndex,
                    rawExpandedStart, rawExpandedEnd,
                    startUT, endUT, clampedToStored, shift));
        }

        /// <summary>
        /// Reset all state for testing (avoids Debug.Log crash outside Unity).
        /// </summary>
        internal static void ResetForTesting()
        {
            CurrentUTNow = GetCurrentUTSafe;
            FindBodyByNameForTesting = null;
            OrbitSeedResolver.ResetForTesting();
            ghostMapVesselPids.Clear();
            ghostsWithSuppressedIcon.Clear();
            ghostOrbitLineGraceUntilFrame.Clear();
            ghostNoBoundsSuppressLastFrame.Clear();
            ghostOrbitBounds.Clear();
            ghostBodyFrameOrbitBounds.Clear();
            ghostLastAppliedOrbitBody.Clear();
            ghostLastAppliedOrbitElements.Clear();
            ghostOrbitLoopShiftedPids.Clear();
            ghostOrbitEpochShift.Clear();
            ghostIconDrivePropagation.Clear();
            ClearPolylineOwningStampsForTesting();
            vesselsByChainPid.Clear();
            vesselsByRecordingIndex.Clear();
            overlapInstanceVessels.Clear();
            boundaryOverlapSecondaryPids.Clear(); // M1
            vesselPidToRecordingIndex.Clear();
            vesselPidToRecordingId.Clear();
            ClearParkingConicLineHoldsForTesting();
            ResetCoverageSetsForTesting();
            trackingStationStateVectorOrbitTrajectories.Clear();
            trackingStationStateVectorCachedIndices.Clear();
            activeReFlyDeferredStateVectorGhostSessions.Clear();
            ClearCachedReFlySuppressionSearchTrees();
            lastKnownByRecordingIndex.Clear();
            lastKnownByChainPid.Clear();
            lifecycleCreatedThisTick = 0;
            lifecycleDestroyedThisTick = 0;
            lifecycleUpdatedThisTick = 0;
            ghostTargetRequestSequence = 0;
        }

        /// <summary>
        /// Synchronous bookkeeping reset for the in-game test runner's between-run cleanup
        /// path (#417/#418). Clears the PID tracking HashSet, orbit bounds, and
        /// recording-index maps in one shot without calling vessel.Die(), under the
        /// assumption that the caller has already invoked GhostPlaybackEngine.DestroyAllGhosts
        /// (or RemoveAllGhostVessels) so the Vessel-layer destruction ran first. This closes
        /// the carryover window where a ProtoVessel was already killed by an engine-driven
        /// overlap/loop end path but its PID lingered in ghostMapVesselPids, causing the
        /// GhostPidsResolveToProtoVessels test to see an orphan on the second Run All.
        ///
        /// Idempotent: safe to call when all dictionaries are already empty (emits a
        /// verbose no-op log). Does NOT call Die() on any vessel — those destructions
        /// are the caller's responsibility via RemoveAllGhostVessels or engine cleanup.
        /// </summary>
        internal static void ResetBetweenTestRuns(string reason)
        {
            ghostTargetRequestSequence = 0;
            ClearCachedReFlySuppressionSearchTrees();

            int pidCount = ghostMapVesselPids.Count;
            int suppressedIconCount = ghostsWithSuppressedIcon.Count;
            int orbitBoundsCount = ghostOrbitBounds.Count;
            int chainCount = vesselsByChainPid.Count;
            int indexCount = vesselsByRecordingIndex.Count;
            int overlapInstanceCount = overlapInstanceVessels.Count;
            int reverseCount = vesselPidToRecordingIndex.Count;
            int reverseIdCount = vesselPidToRecordingId.Count;
            int tsStateVectorCount = trackingStationStateVectorOrbitTrajectories.Count;
            int tsStateVectorCacheCount = trackingStationStateVectorCachedIndices.Count;
            int noBoundsSuppressStampCount = ghostNoBoundsSuppressLastFrame.Count;

            int totalTracked = pidCount + suppressedIconCount + orbitBoundsCount
                + chainCount + indexCount + overlapInstanceCount + reverseCount + reverseIdCount
                + tsStateVectorCount + tsStateVectorCacheCount + noBoundsSuppressStampCount;

            if (totalTracked == 0)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "ResetBetweenTestRuns: all dictionaries already empty (reason={0}) — noop",
                        reason ?? "(null)"));
                return;
            }

            ghostMapVesselPids.Clear();
            ghostsWithSuppressedIcon.Clear();
            ghostOrbitLineGraceUntilFrame.Clear();
            ghostNoBoundsSuppressLastFrame.Clear();
            ghostOrbitBounds.Clear();
            ghostBodyFrameOrbitBounds.Clear();
            ghostLastAppliedOrbitBody.Clear();
            ghostLastAppliedOrbitElements.Clear();
            ghostOrbitLoopShiftedPids.Clear();
            ghostOrbitEpochShift.Clear();
            ghostIconDrivePropagation.Clear();
            vesselsByChainPid.Clear();
            vesselsByRecordingIndex.Clear();
            overlapInstanceVessels.Clear();
            boundaryOverlapSecondaryPids.Clear(); // M1
            vesselPidToRecordingIndex.Clear();
            vesselPidToRecordingId.Clear();
            ghostParkingConicLineHoldUntilUT.Clear();
            trackingStationStateVectorOrbitTrajectories.Clear();
            trackingStationStateVectorCachedIndices.Clear();
            activeReFlyDeferredStateVectorGhostSessions.Clear();
            lastKnownByRecordingIndex.Clear();
            lastKnownByChainPid.Clear();

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "ResetBetweenTestRuns: cleared bookkeeping reason={0} " +
                    "pids={1} suppressedIcons={2} orbitBounds={3} chainVessels={4} " +
                    "indexVessels={5} overlapInstances={6} reverseLookup={7} reverseIdLookup={8} " +
                    "tsStateVectors={9} tsStateVectorCache={10}",
                    reason ?? "(null)",
                    pidCount, suppressedIconCount, orbitBoundsCount,
                    chainCount, indexCount, overlapInstanceCount, reverseCount, reverseIdCount,
                    tsStateVectorCount, tsStateVectorCacheCount));
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static void TrackRecordingGhostVessel(
            int recordingIndex,
            IPlaybackTrajectory traj,
            Vessel vessel)
        {
            if (vessel == null)
                return;

            vesselsByRecordingIndex[recordingIndex] = vessel;
            vesselPidToRecordingIndex[vessel.persistentId] = recordingIndex;

            string recordingId = traj?.RecordingId;
            if (!string.IsNullOrEmpty(recordingId))
                vesselPidToRecordingId[vessel.persistentId] = recordingId;
            else
                vesselPidToRecordingId.Remove(vessel.persistentId);
        }

        internal static void TrackRecordingGhostIdentityForTesting(
            uint ghostPid,
            int recordingIndex,
            string recordingId)
        {
            vesselPidToRecordingIndex[ghostPid] = recordingIndex;
            if (!string.IsNullOrEmpty(recordingId))
                vesselPidToRecordingId[ghostPid] = recordingId;
            else
                vesselPidToRecordingId.Remove(ghostPid);
        }

        internal static void TrackEndpointTailGhostBoundsForTesting(
            uint ghostPid,
            int recordingIndex,
            string recordingId,
            string bodyName,
            double startUT,
            double endUT)
        {
            TrackRecordingGhostIdentityForTesting(ghostPid, recordingIndex, recordingId);
            ghostOrbitBounds[ghostPid] = (startUT, endUT);
            StashLastKnownFrame(recordingIndex, new LastKnownGhostFrame
            {
                RecordingId = recordingId,
                VesselName = "(test)",
                GhostPid = ghostPid,
                Source = "EndpointTail",
                Branch = "(n/a)",
                Body = bodyName,
                WorldPos = default(Vector3d),
                AnchorPid = 0u,
                LastUT = startUT
            });
        }

        /// <summary>
        /// Find a CelestialBody by name without LINQ allocation.
        /// Returns null if FlightGlobals.Bodies is null or name not found.
        /// </summary>
        private static CelestialBody FindBodyByName(string bodyName)
        {
            if (FindBodyByNameForTesting != null)
            {
                CelestialBody testBody = FindBodyByNameForTesting(bodyName);
                if (!object.ReferenceEquals(testBody, null))
                    return testBody;
            }

            var bodies = FlightGlobals.Bodies;
            if (bodies == null) return null;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].name == bodyName || bodies[i].bodyName == bodyName) return bodies[i];
            return null;
        }

        /// <summary>
        /// Shared ProtoVessel creation: resolves body, builds orbit + vessel node,
        /// creates ProtoVessel, pre-registers PID, loads into flightState.
        /// Returns the Vessel or null on failure. Handles full cleanup on error.
        /// </summary>
        /// <summary>
        /// Overload that creates a ProtoVessel using an OrbitSegment instead of terminal orbit data.
        /// Used for intermediate chain segments that have orbit segments but no terminal orbit.
        /// </summary>
        private static Vessel BuildAndLoadGhostProtoVessel(
            IPlaybackTrajectory traj,
            OrbitSegment segment,
            string logContext,
            string protoSource = "visible-segment",
            double loopEpochShiftSeconds = 0.0)
        {
            CelestialBody body = FindBodyByName(segment.bodyName);
            if (body == null)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "BuildAndLoadGhostProtoVessel(segment): body '{0}' not found for {1}",
                        segment.bodyName, logContext));
                return null;
            }

            // Bug 3 (creation-frame icon-off-orbit): BAKE the loop epoch shift into the orbit the proto is
            // LOADED with. ProtoVessel.Load propagates the packed icon at the LIVE Planetarium clock against
            // the node's epoch; with the raw recorded epoch (loopEpochShiftSeconds=0) that lands the icon
            // shift-worth-of-mean-anomaly off the recorded phase at creation, snapping onto the line only on
            // the next physics tick (off EVERY frame at warp, where the proto is recreated each frame).
            // epoch = segment.epoch + shift makes pv.Load's live-clock propagation land on the recorded
            // phase at creation, with no settle pass. The per-frame steady-state drive
            // (StockConicTreatment.SeedAndDriveLive) re-seeds the whole orbit from the RAW segment.epoch +
            // shift every FixedUpdate, so the baked node epoch governs ONLY the single creation frame and
            // never compounds. Non-loop / terminal / state-vector ghosts pass shift 0 -> byte-identical to
            // before. The recorded OrbitSegment is NOT mutated; only this live Orbit instance carries the bake.
            Orbit orbit = new Orbit(
                segment.inclination,
                segment.eccentricity,
                segment.semiMajorAxis,
                segment.longitudeOfAscendingNode,
                segment.argumentOfPeriapsis,
                segment.meanAnomalyAtEpoch,
                segment.epoch + loopEpochShiftSeconds,
                body);

            return BuildAndLoadGhostProtoVesselCore(
                traj,
                orbit,
                body,
                logContext,
                string.IsNullOrEmpty(protoSource) ? "visible-segment" : protoSource,
                string.Format(ic,
                    "segmentBody={0} segmentUT={1:F1}-{2:F1}",
                    segment.bodyName ?? "(null)",
                    segment.startUT,
                    segment.endUT));
        }

        private static Vessel BuildAndLoadGhostProtoVessel(IPlaybackTrajectory traj, string logContext)
        {
            if (!TryResolveGhostProtoOrbitSeed(
                traj,
                out double inclination,
                out double eccentricity,
                out double semiMajorAxis,
                out double lan,
                out double argumentOfPeriapsis,
                out double meanAnomalyAtEpoch,
                out double epoch,
                out string orbitBodyName,
                out GhostProtoOrbitSeedDiagnostics seedDiagnostics))
            {
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "BuildAndLoadGhostProtoVessel: no endpoint-aligned orbit seed for {0} " +
                        "seedFailure={1} endpointBody={2} seedFallback={3} {4}",
                        logContext,
                        seedDiagnostics.FailureReason ?? "(none)",
                        seedDiagnostics.EndpointBodyName ?? "(none)",
                        seedDiagnostics.FallbackReason ?? "(none)",
                        FormatGhostProtoOrbitSeedDiagnostics(seedDiagnostics)));
                return null;
            }

            CelestialBody body = FindBodyByName(orbitBodyName);
            if (body == null)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "BuildAndLoadGhostProtoVessel: body '{0}' not found for {1}",
                        orbitBodyName, logContext));
                return null;
            }

            Orbit orbit = new Orbit(
                inclination,
                eccentricity,
                semiMajorAxis,
                lan,
                argumentOfPeriapsis,
                meanAnomalyAtEpoch,
                epoch,
                body);

            return BuildAndLoadGhostProtoVesselCore(
                traj,
                orbit,
                body,
                logContext,
                string.IsNullOrEmpty(seedDiagnostics.Source) ? "terminal-orbit" : seedDiagnostics.Source,
                string.Format(ic,
                    "seedBody={0} endpointBody={1} seedFallback={2} {3}",
                    orbitBodyName ?? "(null)",
                    seedDiagnostics.EndpointBodyName ?? "(none)",
                    seedDiagnostics.FallbackReason ?? "(none)",
                    FormatGhostProtoOrbitSeedDiagnostics(seedDiagnostics)));
        }

        internal static bool TryResolveGhostProtoOrbitSeed(
            IPlaybackTrajectory traj,
            out double inclination,
            out double eccentricity,
            out double semiMajorAxis,
            out double lan,
            out double argumentOfPeriapsis,
            out double meanAnomalyAtEpoch,
            out double epoch,
            out string bodyName)
        {
            return TryResolveGhostProtoOrbitSeed(
                traj,
                out inclination,
                out eccentricity,
                out semiMajorAxis,
                out lan,
                out argumentOfPeriapsis,
                out meanAnomalyAtEpoch,
                out epoch,
                out bodyName,
                out _);
        }

        internal static bool TryResolveGhostProtoOrbitSeed(
            IPlaybackTrajectory traj,
            out double inclination,
            out double eccentricity,
            out double semiMajorAxis,
            out double lan,
            out double argumentOfPeriapsis,
            out double meanAnomalyAtEpoch,
            out double epoch,
            out string bodyName,
            out GhostProtoOrbitSeedDiagnostics diagnostics)
        {
            if (RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed(
                traj,
                out inclination,
                out eccentricity,
                out semiMajorAxis,
                out lan,
                out argumentOfPeriapsis,
                out meanAnomalyAtEpoch,
                out epoch,
                out bodyName,
                out RecordingEndpointResolver.EndpointOrbitSeedDiagnostics endpointDiagnostics))
            {
                diagnostics = new GhostProtoOrbitSeedDiagnostics
                {
                    Source = endpointDiagnostics.Source,
                    EndpointBodyName = endpointDiagnostics.EndpointBodyName,
                    FailureReason = null,
                    FallbackReason = null,
                    TailUT = double.NaN,
                    TailSma = double.NaN,
                    TailEcc = double.NaN,
                    LatestSegmentEndUT = double.NaN,
                    RotationDriftSeconds = double.NaN
                };
                PopulateTailSeedDiagnostics(traj, double.NaN, ref diagnostics);
                return true;
            }

            bool fallbackResolved = TryResolveTerminalOrbitGhostSeed(
                traj,
                out inclination,
                out eccentricity,
                out semiMajorAxis,
                out lan,
                out argumentOfPeriapsis,
                out meanAnomalyAtEpoch,
                out epoch,
                out bodyName);

            diagnostics = new GhostProtoOrbitSeedDiagnostics
            {
                Source = fallbackResolved ? "terminal-orbit" : "none",
                EndpointBodyName = endpointDiagnostics.EndpointBodyName,
                FailureReason = fallbackResolved
                    ? null
                    : (endpointDiagnostics.FailureReason ?? TrackingStationGhostSkipUnseedableTerminalOrbit),
                FallbackReason = endpointDiagnostics.FailureReason,
                TailUT = double.NaN,
                TailSma = double.NaN,
                TailEcc = double.NaN,
                LatestSegmentEndUT = double.NaN,
                RotationDriftSeconds = double.NaN
            };

            if (fallbackResolved && string.IsNullOrEmpty(diagnostics.EndpointBodyName)
                && RecordingEndpointResolver.TryGetPreferredEndpointBodyName(traj, out string preferredEndpointBody))
            {
                diagnostics.EndpointBodyName = preferredEndpointBody;
            }

            PopulateTailSeedDiagnostics(traj, double.NaN, ref diagnostics);
            return fallbackResolved;
        }

        private static void PopulateTailSeedDiagnostics(
            IPlaybackTrajectory traj,
            double currentUT,
            ref GhostProtoOrbitSeedDiagnostics diagnostics)
        {
            diagnostics.TailSeedConsidered = false;
            diagnostics.TailSeedAccepted = false;
            diagnostics.TailDeclineReason = null;
            diagnostics.TailFrameSource = null;

            if (!RecordingEndpointResolver.TryGetPreferredEndpointBodyName(
                    traj,
                    out string preferredEndpointBody))
            {
                return;
            }

            diagnostics.EndpointBodyName = diagnostics.EndpointBodyName ?? preferredEndpointBody;
            diagnostics.TailSeedConsidered = true;
            if (OrbitSeedResolver.TailSeedResolverForTesting == null
                && !OrbitSeedResolver.TryFindLatestCoastTrajectoryFrame(
                    traj,
                    preferredEndpointBody,
                    out _,
                    out _))
            {
                diagnostics.TailDeclineReason = "no-absolute-coast-tail";
                return;
            }

            CelestialBody body = FindBodyByName(preferredEndpointBody);
            if (body == null)
            {
                diagnostics.TailDeclineReason = "body-not-found";
                return;
            }

            bool accepted = OrbitSeedResolver.TryDeriveTailOrbitSeed(
                traj,
                body,
                currentUT,
                TailSeedUse.MapPresence,
                out TailDerivedOrbitSeed seed);
            diagnostics.TailSeedAccepted = accepted;
            diagnostics.TailDeclineReason = seed.DeclineReason;
            diagnostics.TailUT = seed.TailUT;
            diagnostics.TailSma = seed.Segment.semiMajorAxis;
            diagnostics.TailEcc = seed.Segment.eccentricity;
            diagnostics.LatestSegmentEndUT = seed.LatestStoredSegmentEndUT;
            diagnostics.RotationDriftSeconds = seed.RotationDriftSeconds;
            diagnostics.TailFrameSource = seed.TailFrameSource;
        }

        private static string FormatGhostProtoOrbitSeedDiagnostics(
            GhostProtoOrbitSeedDiagnostics diagnostics)
        {
            return string.Format(ic,
                "tailConsidered={0} tailAccepted={1} tailDecline={2} tailUT={3:F2} tailSma={4:F1} tailEcc={5:F6} latestSegmentEndUT={6:F2} drift={7:F2}s tailFrame={8}",
                diagnostics.TailSeedConsidered,
                diagnostics.TailSeedAccepted,
                diagnostics.TailDeclineReason ?? "(none)",
                diagnostics.TailUT,
                diagnostics.TailSma,
                diagnostics.TailEcc,
                diagnostics.LatestSegmentEndUT,
                diagnostics.RotationDriftSeconds,
                diagnostics.TailFrameSource ?? "(none)");
        }

        private static bool TryResolveTerminalOrbitGhostSeed(
            IPlaybackTrajectory traj,
            out double inclination,
            out double eccentricity,
            out double semiMajorAxis,
            out double lan,
            out double argumentOfPeriapsis,
            out double meanAnomalyAtEpoch,
            out double epoch,
            out string bodyName)
        {
            inclination = 0.0;
            eccentricity = 0.0;
            semiMajorAxis = 0.0;
            lan = 0.0;
            argumentOfPeriapsis = 0.0;
            meanAnomalyAtEpoch = 0.0;
            epoch = 0.0;
            bodyName = null;

            if (traj == null
                || string.IsNullOrEmpty(traj.TerminalOrbitBody)
                || traj.TerminalOrbitSemiMajorAxis <= 0.0)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(traj.EndpointBodyName)
                && traj.EndpointPhase != RecordingEndpointPhase.Unknown
                && !string.Equals(traj.EndpointBodyName, traj.TerminalOrbitBody, StringComparison.Ordinal))
            {
                return false;
            }

            if (RecordingEndpointResolver.TryGetPreferredEndpointBodyName(traj, out string endpointBodyName)
                && !string.IsNullOrEmpty(endpointBodyName)
                && !string.Equals(endpointBodyName, traj.TerminalOrbitBody, StringComparison.Ordinal))
            {
                return false;
            }

            if (traj.TerminalStateValue.HasValue)
            {
                TerminalState terminalState = traj.TerminalStateValue.Value;
                if (terminalState != TerminalState.Orbiting
                    && terminalState != TerminalState.SubOrbital
                    && terminalState != TerminalState.Docked)
                {
                    return false;
                }
            }

            inclination = traj.TerminalOrbitInclination;
            eccentricity = traj.TerminalOrbitEccentricity;
            semiMajorAxis = traj.TerminalOrbitSemiMajorAxis;
            lan = traj.TerminalOrbitLAN;
            argumentOfPeriapsis = traj.TerminalOrbitArgumentOfPeriapsis;
            meanAnomalyAtEpoch = traj.TerminalOrbitMeanAnomalyAtEpoch;
            epoch = traj.TerminalOrbitEpoch;
            bodyName = traj.TerminalOrbitBody;
            return true;
        }

        private static Vessel BuildAndLoadGhostProtoVesselCore(
            IPlaybackTrajectory traj,
            Orbit orbit,
            CelestialBody body,
            string logContext,
            string orbitSource = null,
            string orbitSourceDetail = null)
        {
            ProtoVessel pv = null;
            try
            {

                ConfigNode vesselNode = BuildGhostProtoVesselNode(
                    traj,
                    orbit,
                    out VesselType vtype,
                    out string vesselName);

                pv = new ProtoVessel(vesselNode, HighLogic.CurrentGame);

                // PRE-REGISTER PID before Load — pv.Load fires onVesselCreate and guards
                // must see this PID as a ghost vessel during the event cascade.
                ghostMapVesselPids.Add(pv.persistentId);

                HighLogic.CurrentGame.flightState.protoVessels.Add(pv);

                // Load — creates Vessel GO, OrbitDriver, MapObject, OrbitRenderer,
                // registers in FlightGlobals, fires GameEvents.onVesselCreate
                pv.Load(HighLogic.CurrentGame.flightState);

                if (pv.vesselRef == null)
                {
                    ParsekLog.Error(Tag,
                        string.Format(ic,
                            "BuildAndLoadGhostProtoVessel: vesselRef is null after Load for {0}",
                            logContext));
                    RemoveGhostProtoVessel(pv, nullSafeFlightState: false);
                    return null;
                }

                // Log creation + OrbitDriver state for diagnostics (#172)
                Vessel v = pv.vesselRef;
                // KSP promotes a single-part root with PhysicsSignificance=1 back to
                // FULL, and the thermal explode site checks temperature > maxTemp
                // independent of physical significance. Without this pass the marker's
                // sensorBarometer (maxTemp=1200) overheats and Part.explode() destroys
                // the vessel mid-Re-Fly whenever the active vessel passes through dense
                // atmosphere at orbital speed (repro at logs/2026-05-19_1847_refly-
                // booster-explosion, log line 18:44:50.128).
                HardenGhostVesselPartPhysics(v, logContext);
                NormalizeGhostOrbitDriverTargetIdentity(v, logContext);
                string driverState = "no-orbitDriver";
                if (v.orbitDriver != null)
                {
                    Orbit drv = v.orbitDriver.orbit;

                    driverState = string.Format(ic,
                        "updateMode={0} sma={1:F0} ecc={2:F6} inc={3:F4} " +
                        "argPe={4:F4} mna={5:F6} epoch={6:F1} vesselPos=({7:F1},{8:F1},{9:F1}) {10} registered={11}",
                        v.orbitDriver.updateMode,
                        drv.semiMajorAxis, drv.eccentricity, drv.inclination,
                        drv.argumentOfPeriapsis, drv.meanAnomalyAtEpoch, drv.epoch,
                        v.GetWorldPos3D().x, v.GetWorldPos3D().y, v.GetWorldPos3D().z,
                        BuildGhostOrbitDriverIdentity(v),
                        IsVesselRegistered(v));
                }

                // Ensure OrbitRenderer is enabled — in Tracking Station, pv.Load()
                // may create the renderer in a disabled state.
                bool rendererForceEnabled = false;
                if (v.orbitRenderer != null)
                {
                    v.orbitRenderer.drawMode = OrbitRendererBase.DrawMode.REDRAW_AND_RECALCULATE;
                    v.orbitRenderer.drawIcons = OrbitRendererBase.DrawIcons.ALL;
                    if (!v.orbitRenderer.enabled)
                    {
                        rendererForceEnabled = true;
                        v.orbitRenderer.enabled = true;
                        ParsekLog.Verbose(Tag, string.Format(ic,
                            "Force-enabled OrbitRenderer for ghost '{0}'", vesselName));
                    }
                }

                // Force correct VesselType — KSP may override for single-part vessels
                if (v.vesselType != vtype)
                {
                    ParsekLog.Verbose(Tag, string.Format(ic,
                        "Ghost vessel type overridden by KSP: {0} → {1}, restoring to {2}",
                        vtype, v.vesselType, vtype));
                    v.vesselType = vtype;
                }

                string mapVisibilityState = BuildGhostProtoVesselVisibilityState(
                    v.mapObject != null,
                    v.orbitRenderer != null,
                    v.orbitRenderer != null && v.orbitRenderer.enabled,
                    v.orbitRenderer != null ? v.orbitRenderer.drawIcons.ToString() : "(none)",
                    ghostsWithSuppressedIcon.Contains(v.persistentId),
                    rendererForceEnabled);

                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Created ghost vessel '{0}' ghostPid={1} type={2} body={3} sma={4:F0} for {5} " +
                        "orbitSource={6}{7} | {8} {9} scene={10}",
                        vesselName, v.persistentId,
                        vtype, body.name, orbit.semiMajorAxis, logContext,
                        string.IsNullOrEmpty(orbitSource) ? "(unspecified)" : orbitSource,
                        string.IsNullOrEmpty(orbitSourceDetail) ? string.Empty : " " + orbitSourceDetail,
                        driverState,
                        mapVisibilityState,
                        HighLogic.LoadedScene));

                // MapRenderTrace Tier-A: structural GhostCreated event keyed by the
                // live ghost persistentId (the map world's native key). Gated at the
                // call site so no UT / world-position read and no string formatting
                // happens in normal (disabled) play; EmitStructural also early-returns
                // internally, matching the GhostRenderTrace emitter contract.
                if (MapRenderTrace.IsEnabled)
                {
                    double nowUT = CurrentUTNow();
                    MapRenderTrace.EmitStructural(
                        "GhostCreated",
                        MapRenderTrace.RenderSurface.ProtoIcon,
                        v.persistentId.ToString(ic),
                        nowUT,
                        nowUT,
                        MapRenderTrace.InitialWindowSeconds,
                        MapRenderTrace.BuildLifecycleDetails(
                            vesselName,
                            body.name,
                            HighLogic.LoadedScene.ToString(),
                            v.GetWorldPos3D(),
                            // Correlation key: carry the recordingId in the reason so a GhostCreated line is
                            // greppable back to its recording (the pid<->recordingId map is otherwise only in
                            // the separate vesselPidToRecordingId writes). Ties the ghost-create TRUTH to the
                            // descent DECISION ([ReaimDescent] is recordingId/member-keyed) for one grep.
                            "rec=" + (traj != null ? traj.RecordingId : "<none>") + " " + logContext));
                }

                return v;
            }
            catch (Exception ex)
            {
                if (pv != null)
                {
                    RemoveGhostProtoVessel(pv, nullSafeFlightState: true);
                }
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "BuildAndLoadGhostProtoVessel failed for {0}: {1}",
                        logContext, ex.Message));
                return null;
            }
        }

        private static ConfigNode BuildGhostProtoVesselNode(
            IPlaybackTrajectory traj,
            Orbit orbit,
            out VesselType vtype,
            out string vesselName)
        {
            // Single antenna-free part (avoids CommNet conflict with GhostCommNetRelay).
            // Aero/thermal tolerances are hardened post-load in HardenGhostVesselPartPhysics:
            // the partNode is loaded into a real Part with prefab maxTemp=1200, which
            // would otherwise overheat and explode the marker vessel during low-altitude
            // playback (see BuildAndLoadGhostProtoVesselCore).
            ConfigNode partNode = ProtoVessel.CreatePartNode("sensorBarometer", 0);

            // Discovery: fully visible, infinite lifetime
            ConfigNode discovery = ProtoVessel.CreateDiscoveryNode(
                DiscoveryLevels.Owned, UntrackedObjectClass.C,
                double.PositiveInfinity, double.PositiveInfinity);

            vtype = ResolveVesselType(traj.VesselSnapshot);
            vesselName = "Ghost: " + (traj.VesselName ?? "Unknown");

            ConfigNode vesselNode = ProtoVessel.CreateVesselNode(
                vesselName, vtype, orbit, 0,
                new ConfigNode[] { partNode }, discovery);

            // Critical settings: prevent ground positioning and KSC cleanup
            vesselNode.SetValue("vesselSpawning", "False", true);
            vesselNode.SetValue("prst", "True", true);
            vesselNode.SetValue("cln", "False", true);

            // Defensive: ensure sub-nodes that SpaceTracking.buildVesselsList and other
            // KSP internals assume exist. CreateVesselNode adds ACTIONGROUPS but omits
            // these three. Missing nodes can cause NREs in tracking station code paths.
            if (vesselNode.GetNode("FLIGHTPLAN") == null)
                vesselNode.AddNode("FLIGHTPLAN");
            if (vesselNode.GetNode("CTRLSTATE") == null)
                vesselNode.AddNode("CTRLSTATE");
            if (vesselNode.GetNode("VESSELMODULES") == null)
                vesselNode.AddNode("VESSELMODULES");

            return vesselNode;
        }

        /// <summary>
        /// Override aero/thermal/structural tolerances on every part of a freshly-loaded
        /// ghost-owned ProtoVessel (map-presence marker, replay flag, future single-part
        /// ghost vessels) so the vessel behaves as a render-only presence rather than a
        /// physical body. Without this, KSP's FlightIntegrator runs aerothermal sim on
        /// the vessel (single-part root parts are promoted to PhysicalSignificance.FULL
        /// regardless of prefab settings) and the prefab maxTemp / crashTolerance values
        /// trigger Part.explode() when something hot or fast happens nearby.
        /// </summary>
        internal static int HardenGhostVesselPartPhysics(Vessel v, string logContext)
        {
            if (v == null || v.parts == null) return 0;
            int count = 0;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;
                p.maxTemp = double.PositiveInfinity;
                p.skinMaxTemp = double.PositiveInfinity;
                p.crashTolerance = float.PositiveInfinity;
                p.gTolerance = double.PositiveInfinity;
                p.breakingForce = float.PositiveInfinity;
                p.breakingTorque = float.PositiveInfinity;
                count++;
            }
            ParsekLog.Verbose(Tag, string.Format(ic,
                "Ghost vessel parts hardened: vessel='{0}' parts={1} for {2}",
                v.vesselName ?? "(null)", count, logContext));
            return count;
        }

        private static void RemoveGhostProtoVessel(ProtoVessel pv, bool nullSafeFlightState)
        {
            ghostMapVesselPids.Remove(pv.persistentId);
            if (nullSafeFlightState)
                HighLogic.CurrentGame?.flightState?.protoVessels?.Remove(pv);
            else
                HighLogic.CurrentGame.flightState.protoVessels.Remove(pv);
        }

        /// <summary>
        /// Read VesselType from vessel snapshot ConfigNode.
        /// Falls back to VesselType.Ship if snapshot is null or type is missing.
        /// </summary>
        internal static VesselType ResolveVesselType(ConfigNode vesselSnapshot)
        {
            if (vesselSnapshot == null) return VesselType.Ship;

            string typeStr = vesselSnapshot.GetValue("type");
            if (string.IsNullOrEmpty(typeStr)) return VesselType.Ship;

            if (Enum.TryParse(typeStr, true, out VesselType vtype))
                return vtype;

            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "ResolveVesselType: unrecognized type '{0}' — defaulting to Ship",
                    typeStr));
            return VesselType.Ship;
        }

        // ===================================================================================
        // Flight-scene map-presence lifecycle (Phase 8d.1 relocation from ParsekPlaybackPolicy).
        // FAITHFUL MOVE — the body, struct, dicts, scalars, and dict-touching helpers below were
        // copied VERBATIM from ParsekPlaybackPolicy.CheckPendingMapVessels and its helpers. The
        // only edits are: (a) engine.CurrentLoopUnits -> the loopUnits parameter, and (b) the
        // internal-static ShouldRunMapOrbitReseed / TryResolveTerminalFallbackMapOrbitUpdate /
        // ShouldRetainMapPresenceForTerminalRealSpawn helpers (left in ParsekPlaybackPolicy per the
        // plan) are now qualified with ParsekPlaybackPolicy. — those helpers' bodies are unchanged.
        // The fields use a flight* prefix to mirror the trackingStation* twin naming. They are
        // internal (not private) because the still-in-policy enqueue tail + teardowns reach them
        // until Phase 8d.2 relocates those too.
        // ===================================================================================

        /// <summary>
        /// Recording indices eligible for ghost map ProtoVessels but deferred until
        /// the ghost enters an orbital segment. Avoids showing orbit lines during
        /// atmospheric ascent when the ghost mesh is still on the pad.
        /// </summary>
        internal struct PendingMapVessel
        {
            internal IPlaybackTrajectory Trajectory;
            internal bool AllowSoiGapStateVectorFallback;
            internal string ExpectedSoiGapBody;

            internal PendingMapVessel(
                IPlaybackTrajectory trajectory,
                bool allowSoiGapStateVectorFallback,
                string expectedSoiGapBody)
            {
                Trajectory = trajectory;
                AllowSoiGapStateVectorFallback = allowSoiGapStateVectorFallback;
                ExpectedSoiGapBody = expectedSoiGapBody;
            }
        }

        internal static readonly Dictionary<int, PendingMapVessel> flightPendingMapVessels =
            new Dictionary<int, PendingMapVessel>();

        internal static readonly Dictionary<int, string> flightSoiGapStateVectorExpectedBodies =
            new Dictionary<int, string>();

        /// <summary>
        /// Tracks the last orbit segment body+SMA per recording index for change detection.
        /// Used to update the ghost ProtoVessel orbit as the ghost traverses segments.
        /// </summary>
        private const float MapOrbitUpdateIntervalSec = 0.5f;
        private static float nextMapOrbitUpdateTime;

        internal static readonly Dictionary<int, (string body, double sma, double ecc)> flightLastMapOrbitByIndex =
            new Dictionary<int, (string body, double sma, double ecc)>();

        internal static readonly HashSet<string> flightTerminalMapRetentionLoggedIds =
            new HashSet<string>();

        /// <summary>
        /// Per-chain dedup: maps chainId → recording index that currently owns the ghost map vessel.
        /// When a new chain segment creates a ghost map vessel, the previous segment's is removed.
        /// Prevents duplicate orbit lines during fast time warp across chain boundaries.
        /// </summary>
        internal static readonly Dictionary<string, int> flightChainMapOwner = new Dictionary<string, int>();

        /// <summary>
        /// State-vector orbit tracking: recording indices with physics-only suborbital
        /// orbit lines (no orbit segments). Maps index → trajectory for re-defer.
        /// </summary>
        internal static readonly Dictionary<int, IPlaybackTrajectory> flightStateVectorOrbitTrajectories =
            new Dictionary<int, IPlaybackTrajectory>();

        /// <summary>
        /// Per-recording cached waypoint indices for InterpolateAtUT calls (avoids
        /// O(n) scan on every 0.5s orbit update).
        /// </summary>
        internal static readonly Dictionary<int, int> flightStateVectorCachedIndices =
            new Dictionary<int, int>();

        /// <summary>
        /// Bulk-clear all flight-scene map-presence collections + reset the reseed timer.
        /// Faithfully reproduces the exact set of clears that ParsekPlaybackPolicy's
        /// HandleAllGhostsDestroying + Dispose performed for these collections (Phase 8d.1).
        /// </summary>
        internal static void ClearFlightMapPresenceState()
        {
            flightPendingMapVessels.Clear();
            flightLastMapOrbitByIndex.Clear();
            flightChainMapOwner.Clear();
            flightStateVectorOrbitTrajectories.Clear();
            flightSoiGapStateVectorExpectedBodies.Clear();
            flightStateVectorCachedIndices.Clear();
            flightTerminalMapRetentionLoggedIds.Clear();
            nextMapOrbitUpdateTime = 0f;
        }

        /// <summary>
        /// Resolve the Mission-loop sample UT for a flight-scene map ghost and the matching
        /// live-frame epoch shift. Thin wrapper over
        /// <see cref="GhostPlaybackLogic.ResolveTrackingStationSampleUT"/> (the same seam the
        /// Tracking Station and KSC map drivers use): returns the loop-mapped <c>effUT</c> for a
        /// looping member (and <paramref name="renderHidden"/>=true when it is outside its loop
        /// window this cycle), or the unchanged <paramref name="currentUT"/> with shift 0 for a
        /// non-member / when no Mission loops. <paramref name="loopEpochShiftSeconds"/> is
        /// <c>currentUT - effUT</c>: the amount the seeded orbit epoch + stored arc bounds must be
        /// pushed forward so the icon, drawn at the live clock, lands on the replayed position.
        /// </summary>
        internal static double ResolveMapPresenceSampleUT(
            int idx, double memberStartUT, double memberEndUT, double currentUT,
            GhostPlaybackLogic.LoopUnitSet loopUnits,
            out bool renderHidden, out double loopEpochShiftSeconds)
        {
            double effUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                idx, memberStartUT, memberEndUT, currentUT, loopUnits, out renderHidden);
            loopEpochShiftSeconds = currentUT - effUT;
            return effUT;
        }

        // =====================================================================
        //  Per-instance overlap map presence (slice i)
        //  docs/dev/plans/maprender-overlap-per-instance.md
        //
        //  Mirrors the proven KSC per-instance overlap model (ParsekKSC.UpdateOverlapKsc /
        //  SpawnKscGhost / kscOverlapGhosts) on the map + Tracking Station presence layer:
        //  ONE map ProtoVessel per LIVE overlap cycle so the N map icons match the N flight
        //  meshes 1:1. The schedule resolves through the PURE
        //  GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule so flight AND Tracking Station
        //  (no flight engine present) resolve identically; the cycle math reuses
        //  GhostPlaybackLogic.GetActiveCycles + ComputeOverlapCyclePlaybackUT verbatim.
        // =====================================================================

        /// <summary>
        /// Per-recording overlap gate (slice i). A UNION of two overlap sources, mirroring the flight
        /// engine's two overlap entry points exactly:
        ///   (b) MISSION-UNIT self-overlap (checked FIRST, preferred when both apply): this index is a
        ///       member of a looped Mission unit whose overlap cadence is shorter than its span
        ///       (<see cref="GhostPlaybackLogic.UnitMemberOverlaps"/>), so the whole mission relaunches
        ///       before the prior instance finishes (GhostPlaybackEngine.cs:2144-2183). A Mission member
        ///       does NOT carry its own <c>rec.LoopPlayback</c> flag, so this branch must run regardless
        ///       of it - the bug that cost a playtest: the maintainer loops via the Missions tab.
        ///   (a) STANDALONE per-recording overlap: the recording itself loops (<c>rec.LoopPlayback</c>)
        ///       and its launch-to-launch period is shorter than its duration (ParsekKSC.cs:454-484).
        /// Returns (a)||(b). <paramref name="loopUnits"/> is the per-frame
        /// <see cref="GhostPlaybackEngine.CurrentLoopUnits"/> threaded from the scene driver; Empty
        /// (the common case) collapses this to source (a) only - byte-identical to pre-Missions.
        /// </summary>
        internal static bool IsOverlapRecording(
            Recording rec, int recIdx, IReadOnlyList<Recording> committed,
            GhostPlaybackLogic.LoopUnitSet loopUnits)
        {
            if (rec == null)
                return false;

            // (b) Mission-unit self-overlap — checked first, regardless of rec.LoopPlayback.
            if (IsUnitOverlapMember(rec, recIdx, loopUnits))
                return true;

            // (a) Standalone per-recording overlap loop.
            if (!rec.LoopPlayback)
                return false;
            if (!GhostPlaybackEngine.ShouldLoopPlayback(rec))
                return false;

            double duration = GhostPlaybackEngine.EffectiveLoopDuration(rec);
            if (duration <= LoopTiming.MinLoopDurationSeconds)
                return false;

            double intervalSeconds = ResolveOverlapLoopIntervalSeconds(rec, recIdx, committed);
            return GhostPlaybackLogic.IsOverlapLoop(intervalSeconds, duration);
        }

        /// <summary>
        /// Source (b) predicate: is <paramref name="recIdx"/> a member of a looped Mission unit that
        /// SELF-OVERLAPS (span &gt; 0 &amp;&amp; OverlapCadenceSeconds &lt; span via
        /// <see cref="GhostPlaybackLogic.UnitMemberOverlaps"/>) AND this member's trimmed render window
        /// is long enough to replay? Mirrors the flight engine's unit-overlap branch entry
        /// (GhostPlaybackEngine.cs:2163-2169 - the <c>memberDuration &gt; 0</c> guard, tightened here
        /// to the same <see cref="LoopTiming.MinLoopDurationSeconds"/> floor the standalone path uses).
        /// </summary>
        private static bool IsUnitOverlapMember(
            Recording rec, int recIdx, GhostPlaybackLogic.LoopUnitSet loopUnits)
        {
            if (rec == null || loopUnits == null)
                return false;
            if (!loopUnits.TryGetUnitForMember(recIdx, out GhostPlaybackLogic.LoopUnit unit))
                return false;
            if (!GhostPlaybackLogic.UnitMemberOverlaps(unit))
                return false;

            double memberStartUT = unit.MemberStartUT(recIdx, rec.StartUT);
            double memberEndUT = unit.MemberEndUT(recIdx, rec.EndUT);
            return memberEndUT - memberStartUT > LoopTiming.MinLoopDurationSeconds;
        }

        /// <summary>
        /// The per-instance overlap path is always available (8e S4 dropped the director-drive gate that
        /// previously made it conditional; the Director pipeline is unconditional, so the N per-cycle
        /// epochs are always bakeable). Kept as a method for call-site stability.
        /// </summary>
        internal static bool IsOverlapPerInstanceGateOn()
        {
            return true;
        }

        /// <summary>
        /// Should THIS recording be driven by the per-instance overlap path this frame? True when the
        /// recording is an overlap loop (<see cref="IsOverlapRecording"/>) - overlap recordings ALWAYS
        /// take the per-instance path now (8e S4). When this is true the legacy passes hand off to
        /// <see cref="EnsureOverlapInstances"/> and skip their own single-instance create/reseed for the
        /// index.
        /// </summary>
        internal static bool ShouldDriveOverlapPerInstance(
            Recording rec, int recIdx, IReadOnlyList<Recording> committed,
            GhostPlaybackLogic.LoopUnitSet loopUnits)
        {
            return IsOverlapRecording(rec, recIdx, committed, loopUnits);
        }

        /// <summary>
        /// Resolve the launch-to-launch interval for an overlap-looped recording, threading the
        /// global-auto-launch-queue schedule through the PURE
        /// <see cref="GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule"/> (so the cadence matches
        /// flight + KSC for an auto-interval recording). Falls back to
        /// <see cref="GhostPlaybackLogic.ResolveLoopInterval"/> for non-queue recordings. Mirrors
        /// ParsekKSC.GetLoopIntervalSeconds.
        /// </summary>
        private static double ResolveOverlapLoopIntervalSeconds(
            Recording rec, int recIdx, IReadOnlyList<Recording> committed)
        {
            if (TryResolveOverlapScheduleAnchor(
                    rec, recIdx, committed, out _, out double intervalSeconds))
                return intervalSeconds;

            double globalInterval = ParsekSettings.Current?.autoLoopIntervalSeconds
                                    ?? LoopTiming.DefaultLoopIntervalSeconds;
            return GhostPlaybackLogic.ResolveLoopInterval(
                rec, globalInterval, LoopTiming.DefaultLoopIntervalSeconds, LoopTiming.MinCycleDuration);
        }

        /// <summary>
        /// Resolve the (scheduleStartUT, intervalSeconds) anchor for an overlap-looped recording via
        /// the PURE auto-launch-queue resolver. Mirrors ParsekKSC.TryGetLoopSchedule's schedule
        /// branch: for a global-auto-launch-queue recording the schedule start + cadence come from
        /// <see cref="GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule"/> (which sorts ALL queue
        /// members so the slot/cadence is deterministic across scenes); otherwise the recording's own
        /// effective loop start + resolved interval. Returns false when the recording is not a loop.
        /// </summary>
        private static bool TryResolveOverlapScheduleAnchor(
            Recording rec, int recIdx, IReadOnlyList<Recording> committed,
            out double scheduleStartUT, out double intervalSeconds)
        {
            scheduleStartUT = 0.0;
            intervalSeconds = 0.0;
            if (rec == null || !GhostPlaybackEngine.ShouldLoopPlayback(rec))
                return false;

            double playbackStartUT = GhostPlaybackEngine.EffectiveLoopStartUT(rec);
            double globalInterval = ParsekSettings.Current?.autoLoopIntervalSeconds
                                    ?? LoopTiming.DefaultLoopIntervalSeconds;
            double baseIntervalSeconds = GhostPlaybackLogic.ResolveLoopInterval(
                rec, globalInterval, LoopTiming.DefaultLoopIntervalSeconds, LoopTiming.MinCycleDuration);

            scheduleStartUT = playbackStartUT;
            intervalSeconds = baseIntervalSeconds;

            if (recIdx >= 0
                && committed != null
                && GhostPlaybackLogic.ShouldUseGlobalAutoLaunchQueue(rec))
            {
                var trajectories = new List<IPlaybackTrajectory>(committed.Count);
                for (int i = 0; i < committed.Count; i++)
                    trajectories.Add(committed[i]);

                if (GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule(
                        trajectories,
                        recIdx,
                        baseIntervalSeconds,
                        out GhostPlaybackLogic.AutoLoopLaunchSchedule autoSchedule))
                {
                    scheduleStartUT = autoSchedule.LaunchStartUT;
                    intervalSeconds = autoSchedule.LaunchCadenceSeconds;
                }
            }

            return true;
        }

        /// <summary>
        /// Resolve the full overlap schedule tuple for a recording: playbackStartUT (where the replay
        /// timeline begins), scheduleStartUT (the launch anchor), duration, the EFFECTIVE launch
        /// cadence (raised so <c>ceil(duration/cadence) &lt;= cap</c>, mirroring
        /// ParsekKSC.UpdateOverlapKsc + GhostPlaybackEngine.UpdateOverlapPlayback), and the
        /// cycleDuration the cycle math uses. A UNION of the two overlap sources (the unit branch is
        /// dispatched FIRST so a Mission member uses the unit's schedule even if it also carries its
        /// own <c>rec.LoopPlayback</c>): both converge on the SAME tuple shape, so the SAME
        /// <see cref="EnsureOverlapInstances"/> -&gt; GetActiveCycles -&gt;
        /// <see cref="DecideOverlapInstanceChanges"/> -&gt; ComputeOverlapCyclePlaybackUT path runs for
        /// both (no second create path). Returns false when the recording is neither.
        /// </summary>
        internal static bool ResolveOverlapSchedule(
            Recording rec, int recIdx, IReadOnlyList<Recording> committed,
            GhostPlaybackLogic.LoopUnitSet loopUnits,
            out double playbackStartUT, out double scheduleStartUT,
            out double duration, out double effectiveCadence, out double cycleDuration)
        {
            playbackStartUT = 0.0;
            scheduleStartUT = 0.0;
            duration = 0.0;
            effectiveCadence = 0.0;
            cycleDuration = 0.0;
            if (rec == null)
                return false;

            // Source (b): Mission-unit self-overlap — dispatched first.
            if (TryResolveUnitOverlapSchedule(
                    rec, recIdx, loopUnits,
                    out playbackStartUT, out scheduleStartUT,
                    out duration, out effectiveCadence, out cycleDuration))
                return true;

            // Source (a): standalone per-recording overlap loop.
            if (!GhostPlaybackEngine.ShouldLoopPlayback(rec))
                return false;

            playbackStartUT = GhostPlaybackEngine.EffectiveLoopStartUT(rec);
            duration = GhostPlaybackEngine.EffectiveLoopDuration(rec);
            if (duration <= LoopTiming.MinLoopDurationSeconds)
                return false;

            if (!TryResolveOverlapScheduleAnchor(
                    rec, recIdx, committed, out scheduleStartUT, out double intervalSeconds))
                return false;

            effectiveCadence = GhostPlaybackLogic.ComputeEffectiveLaunchCadence(
                intervalSeconds, duration, GhostPlayback.MaxOverlapGhostsPerRecording);
            cycleDuration = Math.Max(effectiveCadence, LoopTiming.MinCycleDuration);
            return true;
        }

        /// <summary>
        /// Source (b) schedule: resolve the overlap tuple for a Mission-unit self-overlap member,
        /// mirroring the flight engine's unit-overlap branch EXACTLY (GhostPlaybackEngine.cs:2163-2183
        /// for the member window + schedule anchor, GhostPlaybackEngine.cs:3570-3571 for the cap
        /// re-clamp inside UpdateOverlapPlayback):
        ///   memberStartUT   = unit.MemberStartUT(recIdx, rec.StartUT)   (interval-level start trim)
        ///   memberEndUT     = unit.MemberEndUT(recIdx, rec.EndUT)       (interval-level end trim)
        ///   duration        = memberEndUT - memberStartUT
        ///   playbackStartUT = memberStartUT
        ///   scheduleStartUT = ComputeMemberOverlapScheduleStartUT(PhaseAnchorUT, SpanStartUT, memberStartUT)
        ///   effectiveCadence= ComputeEffectiveLaunchCadence(OverlapCadenceSeconds, duration, cap)
        ///   cycleDuration   = max(effectiveCadence, MinCycleDuration)
        /// SPAN-CLOCK CAVEAT: the overlap path uses this RAW schedule with ComputeOverlapCyclePlaybackUT,
        /// NOT ResolveTrackingStationSampleUT / ResolveMapPresenceSampleUT (the span-clock NON-overlap
        /// path with loiter-cut / arrival-hold / re-aim remapping). The engine's overlap branch
        /// deliberately skips the span clock (GhostPlaybackEngine.cs:2152-2156); re-aim / zero-drift
        /// units are non-overlapping by construction (cadence raised &gt;= span) so they never enter
        /// here. Returns false when the recording is not a self-overlapping unit member.
        /// </summary>
        private static bool TryResolveUnitOverlapSchedule(
            Recording rec, int recIdx, GhostPlaybackLogic.LoopUnitSet loopUnits,
            out double playbackStartUT, out double scheduleStartUT,
            out double duration, out double effectiveCadence, out double cycleDuration)
        {
            playbackStartUT = 0.0;
            scheduleStartUT = 0.0;
            duration = 0.0;
            effectiveCadence = 0.0;
            cycleDuration = 0.0;
            if (rec == null || loopUnits == null)
                return false;
            if (!loopUnits.TryGetUnitForMember(recIdx, out GhostPlaybackLogic.LoopUnit unit))
                return false;
            if (!GhostPlaybackLogic.UnitMemberOverlaps(unit))
                return false;

            double memberStartUT = unit.MemberStartUT(recIdx, rec.StartUT);
            double memberEndUT = unit.MemberEndUT(recIdx, rec.EndUT);
            duration = memberEndUT - memberStartUT;
            if (duration <= LoopTiming.MinLoopDurationSeconds)
                return false;

            playbackStartUT = memberStartUT;
            scheduleStartUT = GhostPlaybackLogic.ComputeMemberOverlapScheduleStartUT(
                unit.PhaseAnchorUT, unit.SpanStartUT, memberStartUT);
            effectiveCadence = GhostPlaybackLogic.ComputeEffectiveLaunchCadence(
                unit.OverlapCadenceSeconds, duration, GhostPlayback.MaxOverlapGhostsPerRecording);
            cycleDuration = Math.Max(effectiveCadence, LoopTiming.MinCycleDuration);
            return true;
        }

        /// <summary>
        /// PURE create/destroy decision for the per-instance overlap lifecycle: given the cycles that
        /// currently have a live instance, the active window <c>[firstCycle, lastCycle]</c>, and the
        /// per-frame spawn budget, decides which cycles to create (live but missing, honoring the cap +
        /// throttle) and which to destroy (expired below firstCycle). Unity-free so the policy is
        /// xUnit-pinnable in isolation. <paramref name="spawnBudget"/> is the remaining
        /// <see cref="GhostPlayback.MaxSpawnsPerFrame"/> headroom this frame; once exhausted,
        /// remaining live-but-missing cycles are deferred to a later frame (the create path re-runs
        /// every lifecycle tick), exactly like the flight engine's spawn throttle.
        /// </summary>
        internal static void DecideOverlapInstanceChanges(
            IEnumerable<long> existingCycles,
            long firstCycle,
            long lastCycle,
            int spawnBudget,
            out List<long> toCreate,
            out List<long> toDestroy)
        {
            toCreate = new List<long>();
            toDestroy = new List<long>();
            var present = new HashSet<long>();
            if (existingCycles != null)
            {
                foreach (long c in existingCycles)
                {
                    present.Add(c);
                    // Expired: a cycle that has dropped out of the live window (below firstCycle, the
                    // cap clamp or natural completion) OR somehow ahead of the newest cycle.
                    if (c < firstCycle || c > lastCycle)
                        toDestroy.Add(c);
                }
            }

            if (lastCycle < firstCycle)
                return;

            // Create newest-first so under a throttle the most-recently-launched (most visible)
            // instances win the budget; older live cycles fill in on subsequent ticks.
            int budget = spawnBudget;
            for (long c = lastCycle; c >= firstCycle; c--)
            {
                if (present.Contains(c))
                    continue;
                if (budget <= 0)
                    break;
                toCreate.Add(c);
                budget--;
            }
        }

        /// <summary>
        /// Slice (i) core: ensure ONE live map ProtoVessel per active overlap cycle for an
        /// overlap-looped recording, mirroring ParsekKSC.UpdateOverlapKsc. Resolves the schedule via
        /// the pure auto-launch-queue resolver, computes <c>[firstCycle, lastCycle]</c> via
        /// <see cref="GhostPlaybackLogic.GetActiveCycles"/>, destroys instances whose cycle dropped
        /// out of the window, and creates missing live cycles (each at its own
        /// <c>effUT = ComputeOverlapCyclePlaybackUT(cycle)</c> with
        /// <c>loopEpochShiftSeconds = currentUT - effUT</c>), throttled by the per-frame spawn budget.
        /// ALL N instances (including the newest cycle) live in
        /// <see cref="overlapInstanceVessels"/> (single-ownership rule). Returns the number of
        /// instances created this call so the caller can debit a shared per-frame spawn counter.
        /// </summary>
        internal static int EnsureOverlapInstances(
            int recIdx,
            Recording rec,
            double currentUT,
            IReadOnlyList<Recording> committed,
            GhostPlaybackLogic.LoopUnitSet loopUnits,
            ref int frameSpawnCount)
        {
            if (rec == null)
                return 0;

            if (!ResolveOverlapSchedule(
                    rec, recIdx, committed, loopUnits,
                    out double playbackStartUT, out double scheduleStartUT,
                    out double duration, out double effectiveCadence, out double cycleDuration))
            {
                // Not a resolvable loop (defensive — caller already gated on IsOverlapRecording):
                // tear down any leftover instances for this index.
                DestroyAllOverlapInstancesForRecording(recIdx, "overlap-schedule-unresolved");
                return 0;
            }

            // Before the schedule's first launch: no instances yet.
            if (currentUT < scheduleStartUT)
            {
                DestroyAllOverlapInstancesForRecording(recIdx, "overlap-before-schedule-start");
                return 0;
            }

            // Step 2: suppress this overlap recording's OWN instance ghosts ONLY while
            // its guid-gated launch-matched live vessel is loaded AND it was the LIVE
            // docking anchor of an in-window relative member this-or-last frame (the
            // Step-1 live-bind event), NOT for the whole loop (the hard-coded
            // loopMemberInWindow:true overlap path would otherwise re-create the double
            // the lifecycle pass suppresses only during the actual docking overlap).
            // Keyed on the guid-gated RealVesselExistsForRecording, NOT the pid-only
            // IsMaterializedForMapPresence, so a same-craft different-launch vessel
            // never suppresses. Best-effort on this map path: the live-bind is stamped
            // at member resolution (ghost (re)creation), not per on-rails frame, so the
            // duplicate can briefly show until the next resolve (see
            // WasLiveBoundThisOrLastFrame).
            bool liveLaunchMatchedAnchorOfActiveMember =
                rec != null
                && GhostPlaybackLogic.RealVesselExistsForRecording(rec)
                && RelativeAnchorResolver.WasLiveBoundThisOrLastFrame(rec.RecordingId);

            long firstCycle, lastCycle;
            GhostPlaybackLogic.GetActiveCycles(
                currentUT,
                scheduleStartUT,
                scheduleStartUT + duration,
                effectiveCadence,
                GhostPlayback.MaxOverlapGhostsPerRecording,
                out firstCycle, out lastCycle);

            // Collect the cycles currently live for THIS recording index.
            var existing = new List<long>();
            foreach (var kvp in overlapInstanceVessels)
            {
                if (kvp.Key.recIdx == recIdx)
                    existing.Add(kvp.Key.cycle);
            }

            int spawnBudget = GhostPlayback.MaxSpawnsPerFrame - frameSpawnCount;
            if (spawnBudget < 0) spawnBudget = 0;

            DecideOverlapInstanceChanges(
                existing, firstCycle, lastCycle, spawnBudget,
                out List<long> toCreate, out List<long> toDestroy);

            int destroyed = 0;
            for (int i = 0; i < toDestroy.Count; i++)
            {
                RemoveOverlapInstance(recIdx, toDestroy[i], "overlap-cycle-expired");
                destroyed++;
            }

            int created = 0;
            for (int i = 0; i < toCreate.Count; i++)
            {
                // Respect the shared per-frame throttle (mirror the flight engine's MaxSpawnsPerFrame
                // gate); DecideOverlapInstanceChanges already bounded toCreate by the budget, but
                // re-check against the live counter in case multiple recordings spawn this frame.
                if (GhostPlaybackLogic.ShouldThrottleSpawn(frameSpawnCount, GhostPlayback.MaxSpawnsPerFrame))
                {
                    ParsekLog.VerboseRateLimited(Tag, "overlap-instance-throttle",
                        string.Format(ic,
                            "Overlap per-instance create throttled rec=#{0} \"{1}\" " +
                            "(used {2}/{3} spawns this frame, deferring remaining cycles)",
                            recIdx, rec.VesselName ?? "(null)",
                            frameSpawnCount, GhostPlayback.MaxSpawnsPerFrame),
                        1.0);
                    break;
                }

                long cycle = toCreate[i];
                double effUT = GhostPlaybackLogic.ComputeOverlapCyclePlaybackUT(
                    currentUT,
                    scheduleStartUT,
                    playbackStartUT,
                    duration,
                    cycleDuration,
                    cycle);
                double loopEpochShiftSeconds = currentUT - effUT;

                Vessel inst = CreateOverlapInstanceVessel(
                    recIdx, rec, cycle, effUT, loopEpochShiftSeconds,
                    liveLaunchMatchedAnchorOfActiveMember);
                if (inst != null)
                {
                    created++;
                    frameSpawnCount++;
                }
            }

            if (created > 0 || destroyed > 0)
            {
                ParsekLog.VerboseRateLimited(Tag, "overlap-lifecycle-" + recIdx,
                    string.Format(ic,
                        "Overlap per-instance lifecycle rec=#{0} \"{1}\" cycles=[{2}..{3}] " +
                        "created={4} destroyed={5} liveNow={6} currentUT={7:F1}",
                        recIdx, rec.VesselName ?? "(null)", firstCycle, lastCycle,
                        created, destroyed, GetOverlapInstanceCount(recIdx), currentUT),
                    2.0);
            }

            return created;
        }

        /// <summary>
        /// Per-frame overlap sweep, the SOLE create/destroy authority for overlap recordings on the
        /// map + Tracking Station presence layers. Drives each overlap recording through
        /// <see cref="EnsureOverlapInstances"/> (gated on
        /// <see cref="ShouldDriveOverlapPerInstance"/>) and reaps any leftover per-instance vessels
        /// for indices that are no longer overlap recordings (gate flipped off, recording stopped
        /// looping, period grew past duration, or the committed list shrank). Threads a per-frame
        /// spawn counter so a burst of newly-relaunching cycles is throttled exactly like the flight
        /// engine. Called from both <see cref="UpdateFlightMapGhostLifecycle"/> and
        /// <see cref="UpdateTrackingStationGhostLifecycle"/>. <paramref name="loopUnits"/> is the
        /// per-frame <see cref="GhostPlaybackEngine.CurrentLoopUnits"/> the scene drivers already hold
        /// (null-coalesced to Empty), so a Mission-tab loop (source b) drives the per-instance path
        /// even when <c>rec.LoopPlayback</c> is false. With the gate OFF nothing here runs (every
        /// recording fails <see cref="ShouldDriveOverlapPerInstance"/>) AND the leftover reaper tears
        /// down any instances created while the gate was on, so the scene falls cleanly back to the
        /// legacy one-per-recording path.
        /// </summary>
        internal static void RunOverlapPerInstanceSweep(
            double currentUT, IReadOnlyList<Recording> committed,
            GhostPlaybackLogic.LoopUnitSet loopUnits)
        {
            if (loopUnits == null)
                loopUnits = GhostPlaybackLogic.LoopUnitSet.Empty;

            // Snapshot the set of indices that currently have any per-instance vessel, so we can
            // reap the ones that should no longer be driven per-instance after the create pass below.
            HashSet<int> indicesWithInstances = null;
            if (overlapInstanceVessels.Count > 0)
            {
                indicesWithInstances = new HashSet<int>();
                foreach (var kvp in overlapInstanceVessels)
                    indicesWithInstances.Add(kvp.Key.recIdx);
            }

            int frameSpawnCount = 0;
            HashSet<int> drivenThisFrame = null;
            if (committed != null)
            {
                bool gateOn = IsOverlapPerInstanceGateOn();
                for (int i = 0; i < committed.Count; i++)
                {
                    var rec = committed[i];
                    bool shouldDrive = ShouldDriveOverlapPerInstance(rec, i, committed, loopUnits);

                    // GATE-DECISION TRACE (the blind spot that cost us a playtest): one rate-limited
                    // line PER committed recording explaining the verdict, BEFORE the continue. A
                    // re-fly Mission member shows isMember=true unitOverlaps=true even when
                    // rec.LoopPlayback=false, so a "one icon instead of N" report is diagnosable from
                    // the log alone.
                    LogOverlapGateDecision(i, rec, committed, loopUnits, gateOn, shouldDrive);

                    if (!shouldDrive)
                    {
                        // BOUNDARY-OVERLAP secondary (docs/dev/plan-launch-boundary-overlap.md 5.1): a launch-hold
                        // re-aim unit member is NON-overlap (ShouldDriveOverlapPerInstance is false), so its PRIMARY
                        // map vessel lives in vesselsByRecordingIndex via the per-index create path. During the
                        // borrow window of a zero-slack loop the early-launching NEXT instance N+1 needs its own map
                        // ProtoVessel (icon + escape conic). Create it INSIDE this sweep so the reaper below spares
                        // it (register the index in drivenThisFrame) and tears it down automatically the frame the
                        // borrow window ends. This reuses the create/seed/reap plumbing verbatim - the secondary's
                        // in-SOI escape conic resolves to a Segment source (verified for aa48920e), so it is
                        // loop-shift driven exactly like an overlap instance. Inert (NoSecondary) for every
                        // non-launch-hold member and every already-aligned loop.
                        if (TryEnsureBoundaryOverlapSecondaryInstance(
                                i, rec, currentUT, committed, loopUnits, ref frameSpawnCount))
                        {
                            if (drivenThisFrame == null) drivenThisFrame = new HashSet<int>();
                            drivenThisFrame.Add(i);
                        }
                        continue;
                    }
                    EnsureOverlapInstances(i, rec, currentUT, committed, loopUnits, ref frameSpawnCount);
                    if (drivenThisFrame == null) drivenThisFrame = new HashSet<int>();
                    drivenThisFrame.Add(i);
                }
            }

            // Reap: any index that has per-instance vessels but was NOT driven this frame is no longer
            // an overlap recording under the active gate (or fell off the committed list). Tear its
            // instances down so the legacy one-per-recording path can resume cleanly (or so the gate-off
            // fallback renders the single legacy icon).
            if (indicesWithInstances != null)
            {
                foreach (int idx in indicesWithInstances)
                {
                    if (drivenThisFrame != null && drivenThisFrame.Contains(idx))
                        continue;
                    DestroyAllOverlapInstancesForRecording(idx, "overlap-no-longer-per-instance");
                }
            }
        }

        /// <summary>
        /// BOUNDARY-OVERLAP secondary map presence (docs/dev/plan-launch-boundary-overlap.md 5.1): when committed
        /// recording <paramref name="recIdx"/> is a launch-hold re-aim unit member that carries a live
        /// boundary-overlap secondary this frame (the early-launching NEXT instance N+1, during the borrow window of
        /// a zero-slack loop), ensure ONE <see cref="overlapInstanceVessels"/> entry keyed (recIdx, secondaryCycle)
        /// seeded at the secondary's loop-mapped sample UT, and reap any stale secondary cycle for this index. Reuses
        /// <see cref="CreateOverlapInstanceVessel"/> (so the in-SOI escape conic resolves to a Segment source and is
        /// loop-shift driven exactly like an overlap instance). Returns true when this index has a live
        /// boundary-overlap secondary this frame (so the caller registers it in <c>drivenThisFrame</c> and the
        /// reaper spares it); false otherwise (NoSecondary -> nothing created, any stale secondary already reaped).
        /// Inert for every non-member, every non-launch-hold member, and every already-aligned loop. Single-ownership
        /// is preserved: the PRIMARY stays in <see cref="vesselsByRecordingIndex"/>; the secondary lives ONLY here.
        /// </summary>
        private static bool TryEnsureBoundaryOverlapSecondaryInstance(
            int recIdx, Recording rec, double currentUT,
            IReadOnlyList<Recording> committed,
            GhostPlaybackLogic.LoopUnitSet loopUnits,
            ref int frameSpawnCount)
        {
            if (rec == null || loopUnits == null || loopUnits.Count == 0)
                return false;
            if (!loopUnits.TryGetUnitForMember(recIdx, out GhostPlaybackLogic.LoopUnit unit))
                return false;
            if (!unit.LaunchHoldEngaged)
                return false;

            // Resolve the dual-clock frame for THIS member: primary sample UT + the optional boundary-overlap
            // secondary in this member's window. The PRIMARY is handled by the per-index create path (this method
            // only owns the secondary). NoSecondary -> reap any stale secondary for this index and return false.
            double memberStartUT = unit.MemberStartUT(recIdx, rec.StartUT);
            double memberEndUT = unit.MemberEndUT(recIdx, rec.EndUT);
            GhostPlaybackLogic.BoundaryOverlapSecondaryDecision decision =
                GhostPlaybackLogic.DecideBoundaryOverlapSecondaryRender(
                    currentUT, unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
                    memberStartUT, memberEndUT, out double secondaryUT, out long secondaryCycle,
                    unit.RelaunchSchedule, unit.LoiterCuts,
                    unit.ArrivalHoldSeconds, unit.ArrivalHoldAtUT, unit.ArrivalAlignPeriodSeconds,
                    unit.LaunchBodyRotationPeriodSeconds, unit.LaunchHoldEngaged, unit.RecordedSoiExitUT);

            if (decision != GhostPlaybackLogic.BoundaryOverlapSecondaryDecision.Render)
            {
                // No live secondary this frame: reap any stale boundary-secondary instance for this index. (The
                // outer reaper would also catch it via drivenThisFrame, but tear it down here so a NoSecondary frame
                // does not leave a lingering icon for one extra frame.)
                ReapBoundaryOverlapSecondaryInstances(recIdx, secondaryCycle: long.MinValue,
                    reason: "boundary-overlap-secondary-window-ended");
                return false;
            }

            // Reap any stale secondary cycle for this index (the borrow window's instance advanced to the next
            // N+1), keeping only the current secondaryCycle.
            ReapBoundaryOverlapSecondaryInstances(recIdx, secondaryCycle,
                reason: "boundary-overlap-secondary-cycle-advanced");

            // Already live for this cycle: nothing to create, but report the live secondary so the reaper spares it.
            if (overlapInstanceVessels.ContainsKey((recIdx, secondaryCycle)))
                return true;

            // Respect the shared per-frame spawn throttle exactly like EnsureOverlapInstances.
            if (GhostPlaybackLogic.ShouldThrottleSpawn(frameSpawnCount, GhostPlayback.MaxSpawnsPerFrame))
            {
                ParsekLog.VerboseRateLimited(Tag,
                    string.Format(ic, "boundary-overlap-secondary-throttle-{0}", recIdx),
                    string.Format(ic,
                        "Boundary-overlap secondary create throttled rec=#{0} \"{1}\" cycle={2} " +
                        "(used {3}/{4} spawns this frame)",
                        recIdx, rec.VesselName ?? "(null)", secondaryCycle,
                        frameSpawnCount, GhostPlayback.MaxSpawnsPerFrame),
                    1.0);
                // Still report the secondary live so the reaper does not destroy a (deferred) create next frame.
                return true;
            }

            double loopEpochShiftSeconds = currentUT - secondaryUT;
            Vessel inst = CreateOverlapInstanceVessel(
                recIdx, rec, secondaryCycle, secondaryUT, loopEpochShiftSeconds,
                out TrackingStationGhostSource diagSource, out OrbitSegment diagSegment,
                liveLaunchMatchedAnchorOfActiveMember: false);

            // SEAM-RENDER OBSERVABILITY 2 (docs/dev/design-reaim-launch-hold-seam.md): the secondary's MAP
            // presence (icon + escape conic) first-create truth, INCLUDING the create-returned-null /
            // no-accepted-source case (the leading-hypothesis smoking gun). created=false + source=none means
            // the post-ascent in-SOI escape window has NO accepted Segment yet, so the icon/conic only
            // materializes once the secondaryLoopUT reaches the recorded escape Segment - that lag (the few
            // minutes the prompt reports) is lagFromSpanStartSec = secondaryLoopUT - spanStart at the moment of
            // create. segmentUT names the covering Segment span when one resolved. Rate-limited per
            // (recIdx, secondaryCycle) so each borrow window's first-create truth is captured exactly once;
            // logging-only, no control-flow effect (created is read from the create result, not a new gate).
            string segUtStr = (diagSource == TrackingStationGhostSource.Segment
                               || diagSource == TrackingStationGhostSource.EndpointTail)
                ? string.Format(ic, "{0:F1}-{1:F1}", diagSegment.startUT, diagSegment.endUT)
                : "n/a";
            double lagFromSpanStart = secondaryUT - unit.SpanStartUT;
            ParsekLog.VerboseRateLimited(Tag,
                string.Format(ic, "boundary-overlap-secondary-map-presence-{0}-{1}", recIdx, secondaryCycle),
                string.Format(ic,
                    "boundary-overlap secondary map-presence: created={0} currentUT={1:R} secondaryLoopUT={2:R} " +
                    "source={3} segmentUT={4} lagFromSpanStartSec={5:R} rec=#{6} \"{7}\" cycle={8}",
                    inst != null, currentUT, secondaryUT, diagSource, segUtStr, lagFromSpanStart,
                    recIdx, rec.VesselName ?? "(null)", secondaryCycle),
                2.0);

            if (inst != null)
            {
                // M1: tag this pid so the vesselsByRecordingIndex-reader fall-through
                // (GetGhostVesselPidForRecording / HasGhostVesselForRecording) does not let the secondary
                // shadow the launch recording's identity. Dropped on reap in RemoveOverlapInstance.
                boundaryOverlapSecondaryPids.Add(inst.persistentId);
                frameSpawnCount++;
                ParsekLog.VerboseRateLimited(Tag,
                    string.Format(ic, "boundary-overlap-secondary-create-{0}", recIdx),
                    string.Format(ic,
                        "Created boundary-overlap secondary map vessel rec=#{0} \"{1}\" cycle={2} " +
                        "secondaryUT={3:F1} loopShift={4:F1}",
                        recIdx, rec.VesselName ?? "(null)", secondaryCycle, secondaryUT, loopEpochShiftSeconds),
                    2.0);
            }
            // Report live even if the create deferred (no Segment yet): the reaper must not tear down a pending one.
            return true;
        }

        /// <summary>
        /// Reaps boundary-overlap secondary instances for <paramref name="recIdx"/>, destroying every per-instance
        /// vessel for this index whose cycle is NOT <paramref name="secondaryCycle"/> (pass <c>long.MinValue</c> to
        /// reap all). Only touches entries whose cycle differs from the live secondary cycle, so it never destroys a
        /// genuine self-overlap instance (a launch-hold unit member never has self-overlap instances anyway). The
        /// primary in <see cref="vesselsByRecordingIndex"/> is untouched.
        /// </summary>
        private static void ReapBoundaryOverlapSecondaryInstances(int recIdx, long secondaryCycle, string reason)
        {
            List<long> stale = null;
            foreach (var kvp in overlapInstanceVessels)
            {
                if (kvp.Key.recIdx == recIdx && kvp.Key.cycle != secondaryCycle)
                {
                    if (stale == null) stale = new List<long>();
                    stale.Add(kvp.Key.cycle);
                }
            }
            if (stale == null)
                return;
            for (int i = 0; i < stale.Count; i++)
                RemoveOverlapInstance(recIdx, stale[i], reason);
        }

        /// <summary>
        /// Per-recording gate-decision trace (rate-limited, per-index key). Surfaces every input to the
        /// overlap verdict so a "one icon instead of N" report is diagnosable from the log WITHOUT a
        /// rebuild: the director-drive gate, the standalone source (a) inputs (rec.LoopPlayback +
        /// IsOverlapLoop result), the Mission source (b) inputs (isMember / overlapCadence / span /
        /// unitOverlaps), the resolved schedule tuple (scheduleStart / duration / effectiveCadence), and
        /// the final ShouldDriveOverlapPerInstance verdict + the live cycle window when driven. Reuses
        /// the same pure resolvers the verdict uses, so the log can never disagree with the decision.
        /// Unity-free (reads only the pure resolvers + the test-overridable <c>CurrentUTNow</c>), so the
        /// xUnit log-assertion test can drive it directly.
        /// </summary>
        internal static void LogOverlapGateDecision(
            int recIdx, Recording rec, IReadOnlyList<Recording> committed,
            GhostPlaybackLogic.LoopUnitSet loopUnits, bool gateOn, bool shouldDrive)
        {
            // Source (a) inputs.
            bool recLoopPlayback = rec != null && rec.LoopPlayback;
            double autoDuration = rec != null && GhostPlaybackEngine.ShouldLoopPlayback(rec)
                ? GhostPlaybackEngine.EffectiveLoopDuration(rec) : double.NaN;
            double autoInterval = double.NaN;
            bool autoOverlap = false;
            if (recLoopPlayback && !double.IsNaN(autoDuration) && autoDuration > LoopTiming.MinLoopDurationSeconds)
            {
                autoInterval = ResolveOverlapLoopIntervalSeconds(rec, recIdx, committed);
                autoOverlap = GhostPlaybackLogic.IsOverlapLoop(autoInterval, autoDuration);
            }

            // Source (b) inputs.
            bool isMember = loopUnits != null
                && loopUnits.TryGetUnitForMember(recIdx, out GhostPlaybackLogic.LoopUnit unit);
            double overlapCadence = double.NaN;
            double span = double.NaN;
            bool unitOverlaps = false;
            if (isMember)
            {
                loopUnits.TryGetUnitForMember(recIdx, out unit);
                overlapCadence = unit.OverlapCadenceSeconds;
                span = unit.SpanEndUT - unit.SpanStartUT;
                unitOverlaps = GhostPlaybackLogic.UnitMemberOverlaps(unit);
            }

            // Resolved schedule tuple (whichever source won), + the live cycle window when driven.
            string scheduleStr = "(none)";
            string cycleWindowStr = "(not-driven)";
            if (ResolveOverlapSchedule(
                    rec, recIdx, committed, loopUnits,
                    out _, out double scheduleStartUT,
                    out double duration, out double effectiveCadence, out _))
            {
                scheduleStr = string.Format(ic,
                    "scheduleStart={0:F1} duration={1:F1} effectiveCadence={2:F1}",
                    scheduleStartUT, duration, effectiveCadence);
                if (shouldDrive)
                {
                    OverlapCyclesForTesting(rec, recIdx, committed, loopUnits, CurrentUTNow(),
                        out long firstCycle, out long lastCycle);
                    cycleWindowStr = string.Format(ic,
                        "cycles=[{0}..{1}] liveNow={2}", firstCycle, lastCycle,
                        GetOverlapInstanceCount(recIdx));
                }
            }

            string message = string.Format(ic,
                "Overlap gate decision rec=#{0} \"{1}\": directorDrive={2} | "
                + "(a) loopPlayback={3} autoIsOverlapLoop={4} | "
                + "(b) isMember={5} overlapCadence={6:F1} span={7:F1} unitOverlaps={8} | "
                + "{9} | verdict shouldDrive={10} {11}",
                recIdx, rec?.VesselName ?? "(null)", gateOn,
                recLoopPlayback, autoOverlap,
                isMember, overlapCadence, span, unitOverlaps,
                scheduleStr, shouldDrive, cycleWindowStr);

            // This verdict is STABLE across the vast majority of frames (almost always
            // "not-driven"), so the old per-index time-based rate limit still re-emitted
            // every interval forever: ~48k lines (~15% of all verbose output) in the
            // 2026-06-07 career playtest. Switch to change-detection so the line fires
            // only when the decision actually flips. Identity is the stable RecordingId
            // (committed recordings always have one), so a different recording reusing a
            // positional index gets its own identity; the positional "idx-N" fallback is
            // best-effort and only reachable for an id-less recording (not expected here).
            // The state key carries only the boolean decision facets so the continuously
            // varying cadence / span / cycle-window floats don't defeat coalescing while
            // a recording is driven.
            string identity = !string.IsNullOrEmpty(rec?.RecordingId)
                ? rec.RecordingId
                : "idx-" + recIdx.ToString(ic);
            string stateKey = string.Format(ic, "{0}|{1}|{2}|{3}|{4}|{5}",
                gateOn, shouldDrive, recLoopPlayback, autoOverlap, isMember, unitOverlaps);
            ParsekLog.VerboseOnChange(Tag, identity, stateKey, message);
        }

        /// <summary>
        /// Create ONE per-instance overlap map ProtoVessel for (recIdx, cycle) at its loop-mapped
        /// <paramref name="effUT"/>. Mirrors ParsekKSC.SpawnKscGhost's "fresh object per instance, no
        /// per-index early-return" model rather than the per-index
        /// <see cref="CreateGhostVesselFromSource"/> funnel (which early-returns the existing
        /// per-index vessel and writes the single <see cref="vesselsByRecordingIndex"/> slot via
        /// <see cref="TrackRecordingGhostVessel"/>). Registers PER INSTANCE: the new pid into
        /// <see cref="ghostMapVesselPids"/>, the pid-&gt;index / pid-&gt;id reverse maps, the per-pid
        /// loop epoch shift, and the per-pid orbit bounds the icon-drive / arc-clip patches read. The
        /// orbit source is resolved at <paramref name="effUT"/> exactly like the per-index create.
        /// </summary>
        private static Vessel CreateOverlapInstanceVessel(
            int recIdx, Recording rec, long cycle, double effUT, double loopEpochShiftSeconds,
            bool liveLaunchMatchedAnchorOfActiveMember = false)
        {
            // Diagnostic-only overload-style wrapper: the boundary-overlap secondary create
            // (TryEnsureBoundaryOverlapSecondaryInstance) reads back the resolved source / covering
            // segment to emit observability 2 (the map-presence pre-Segment gap). The regular overlap
            // sweep does not need them, so it routes through this thin wrapper that discards them.
            return CreateOverlapInstanceVessel(
                recIdx, rec, cycle, effUT, loopEpochShiftSeconds,
                out _, out _, liveLaunchMatchedAnchorOfActiveMember);
        }

        private static Vessel CreateOverlapInstanceVessel(
            int recIdx, Recording rec, long cycle, double effUT, double loopEpochShiftSeconds,
            out TrackingStationGhostSource diagSource, out OrbitSegment diagSegment,
            bool liveLaunchMatchedAnchorOfActiveMember = false)
        {
            diagSource = TrackingStationGhostSource.None;
            diagSegment = default(OrbitSegment);

            if (overlapInstanceVessels.ContainsKey((recIdx, cycle)))
                return overlapInstanceVessels[(recIdx, cycle)];

            // Resolve the covering orbit source at this instance's effUT (segment / state-vector /
            // terminal-orbit), the same resolution the per-index create uses.
            int cachedStateVectorIndex = -1;
            TrackingStationGhostSource source = ResolveMapPresenceGhostSource(
                rec,
                false,
                IsMaterializedForMapPresence(rec),
                effUT,
                true,
                "overlap-instance-create",
                ref cachedStateVectorIndex,
                out OrbitSegment segment,
                out TrajectoryPoint stateVectorPoint,
                out string skipReason,
                recordingIndex: recIdx,
                // A live overlap instance always has a non-zero epoch shift, so it is "in window".
                loopMemberInWindow: true,
                liveLaunchMatchedAnchorOfActiveMember: liveLaunchMatchedAnchorOfActiveMember);

            // Read-back-only: expose the resolved source + covering segment to the boundary-overlap
            // secondary diagnostic. No control-flow effect (the create decisions below read `source` /
            // `segment` directly, exactly as before).
            diagSource = source;
            diagSegment = segment;

            if (!IsMapCreateAcceptedSource(source))
            {
                // Throttle key is per-RECORDING, NOT per-cycle: at high time warp the overlap cycle
                // index advances every frame, so a per-cycle key yields a fresh key each frame and
                // defeats VerboseRateLimited (a per-frame flood). Every cycle of a non-orbital recording
                // skips for the same reason, so one line per recording per interval suffices; the cycle
                // stays in the message for context.
                ParsekLog.VerboseRateLimited(Tag,
                    string.Format(ic, "overlap-instance-no-source-{0}", recIdx),
                    string.Format(ic,
                        "Overlap per-instance create skipped rec=#{0} \"{1}\" cycle={2} effUT={3:F1} " +
                        "source={4} reason={5} (no map-visible orbit this cycle yet)",
                        recIdx, rec.VesselName ?? "(null)", cycle, effUT, source,
                        skipReason ?? "(none)"),
                    2.0);
                return null;
            }

            // Build the ProtoVessel directly (fresh KSP-unique pid via CreateVesselNode vesselID:0).
            // Segment / EndpointTail seed the orbit from the covering segment; TerminalOrbit /
            // state-vector seed from the recording's terminal/endpoint orbit.
            string logContext = string.Format(ic,
                "overlap instance rec=#{0} cycle={1} (source={2})", recIdx, cycle, source);
            Vessel vessel;
            switch (source)
            {
                case TrackingStationGhostSource.Segment:
                case TrackingStationGhostSource.EndpointTail:
                    vessel = BuildAndLoadGhostProtoVessel(
                        rec, segment, logContext,
                        source == TrackingStationGhostSource.EndpointTail
                            ? "overlap-instance-endpoint-tail"
                            : "overlap-instance-segment");
                    break;
                case TrackingStationGhostSource.TerminalOrbit:
                    vessel = BuildAndLoadGhostProtoVessel(rec, logContext);
                    break;
                case TrackingStationGhostSource.StateVector:
                case TrackingStationGhostSource.StateVectorSoiGap:
                    vessel = BuildAndLoadGhostProtoVesselFromStateVector(
                        rec, stateVectorPoint, effUT, loopEpochShiftSeconds, logContext);
                    break;
                default:
                    return null;
            }

            if (vessel == null)
                return null;

            // Register this instance PER PID (single-ownership store + the per-pid maps the icon /
            // arc / marker paths read). Do NOT write vesselsByRecordingIndex (that is the legacy
            // one-per-recording slot; overlap recordings live solely in overlapInstanceVessels).
            uint pid = vessel.persistentId;
            overlapInstanceVessels[(recIdx, cycle)] = vessel;
            ghostMapVesselPids.Add(pid);
            vesselPidToRecordingIndex[pid] = recIdx;
            if (!string.IsNullOrEmpty(rec.RecordingId))
                vesselPidToRecordingId[pid] = rec.RecordingId;
            else
                vesselPidToRecordingId.Remove(pid);

            if (source == TrackingStationGhostSource.Segment
                || source == TrackingStationGhostSource.EndpointTail)
            {
                // Segment / orbit path: seed the RAW recorded epoch + record the loop shift + per-pid
                // bounds so GhostOrbitIconDrivePatch drives the OrbitDriver at effUT = liveUT - shift
                // every frame (slice i: the per-pid ghostOrbitEpochShift IS the phase, NOT an
                // instanceKey). Mirrors the per-index segment contract (ApplyOrbitToVessel).
                ApplyOrbitToVessel(vessel, segment, logContext, loopEpochShiftSeconds);
            }
            else
            {
                // Terminal-orbit + state-vector instances mirror the per-index NON-segment contract:
                // the orbit is seeded at the SHIFTED epoch (live UT) and stock OrbitDriver propagates
                // it at the live Planetarium clock - there is no raw-epoch + effUT-drive contract
                // (no OrbitSegments / bounds). So this branch MUST NOT register ghostOrbitEpochShift /
                // ghostOrbitLoopShiftedPids: doing so makes GhostOrbitIconDrivePatch bail on the
                // no-bounds branch and stock then advances the icon by `shift` along the orbit (wrong
                // phase). Clear any stale segment-drive entries (defensive; the pid is fresh here, but
                // this mirrors the per-index state-vector clear at GhostMapPresence.cs:7969-7973).
                ghostOrbitBounds.Remove(pid);
                ghostBodyFrameOrbitBounds.Remove(pid);
                ghostOrbitLoopShiftedPids.Remove(pid);
                ghostOrbitEpochShift.Remove(pid);
            }

            lifecycleCreatedThisTick++;

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Created overlap per-instance map vessel rec=#{0} \"{1}\" cycle={2} pid={3} " +
                    "source={4} effUT={5:F1} loopShift={6:F1}",
                    recIdx, rec.VesselName ?? "(null)", cycle, pid, source, effUT, loopEpochShiftSeconds));

            return vessel;
        }

        /// <summary>
        /// Remove ONE per-instance overlap map ProtoVessel for (recIdx, cycle): Die() the vessel,
        /// drop all per-pid map entries, and clear the store slot. Mirrors
        /// <see cref="RemoveGhostVesselForRecording"/> but keyed on the (recIdx, cycle) instance and
        /// the per-instance store (never touches <see cref="vesselsByRecordingIndex"/>).
        /// </summary>
        internal static void RemoveOverlapInstance(int recIdx, long cycle, string reason)
        {
            if (!overlapInstanceVessels.TryGetValue((recIdx, cycle), out Vessel vessel))
                return;

            uint ghostPid = vessel != null ? vessel.persistentId : 0u;

            if (vessel != null)
            {
                try { vessel.Die(); }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag,
                        string.Format(ic,
                            "RemoveOverlapInstance: Die() threw for rec=#{0} cycle={1}: {2}",
                            recIdx, cycle, ex.Message));
                }
            }

            if (ghostPid != 0)
            {
                boundaryOverlapSecondaryPids.Remove(ghostPid); // M1: no-op for genuine self-overlap instances
                ghostMapVesselPids.Remove(ghostPid);
                ghostsWithSuppressedIcon.Remove(ghostPid);
                ghostNoBoundsSuppressLastFrame.Remove(ghostPid);
                ghostOrbitLineGraceUntilFrame.Remove(ghostPid);
                ghostOrbitBounds.Remove(ghostPid);
                ghostBodyFrameOrbitBounds.Remove(ghostPid);
                ghostLastAppliedOrbitBody.Remove(ghostPid);
                ghostLastAppliedOrbitElements.Remove(ghostPid);
                ghostOrbitLoopShiftedPids.Remove(ghostPid);
                ghostOrbitEpochShift.Remove(ghostPid);
                ghostIconDrivePropagation.Remove(ghostPid);
                vesselPidToRecordingIndex.Remove(ghostPid);
                vesselPidToRecordingId.Remove(ghostPid);
            }
            overlapInstanceVessels.Remove((recIdx, cycle));
            lifecycleDestroyedThisTick++;

            ParsekLog.VerboseRateLimited(Tag, "overlap-remove-" + recIdx,
                string.Format(ic,
                    "Removed overlap per-instance map vessel rec=#{0} cycle={1} pid={2} reason={3}",
                    recIdx, cycle, ghostPid, reason ?? "(none)"),
                2.0);
        }

        /// <summary>
        /// Destroy every per-instance overlap map vessel for a recording index (e.g. when the
        /// recording is no longer an overlap loop, became inactive, or the gate flipped off). Mirrors
        /// ParsekKSC.DestroyAllKscOverlapGhosts.
        /// </summary>
        internal static void DestroyAllOverlapInstancesForRecording(int recIdx, string reason)
        {
            List<long> cycles = null;
            foreach (var kvp in overlapInstanceVessels)
            {
                if (kvp.Key.recIdx != recIdx)
                    continue;
                if (cycles == null) cycles = new List<long>();
                cycles.Add(kvp.Key.cycle);
            }
            if (cycles == null)
                return;
            for (int i = 0; i < cycles.Count; i++)
                RemoveOverlapInstance(recIdx, cycles[i], reason);
        }

        /// <summary>
        /// Number of live per-instance overlap map vessels for a recording index. Exposed for the
        /// in-game test (asserts map count == flight overlap count + 1, capped at the per-recording
        /// cap).
        /// </summary>
        internal static int GetOverlapInstanceCount(int recIdx)
        {
            int count = 0;
            foreach (var kvp in overlapInstanceVessels)
            {
                if (kvp.Key.recIdx == recIdx)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// The pid of the NEWEST live overlap instance (highest cycle) for a recording index, or 0
        /// when none. Used by the <see cref="vesselsByRecordingIndex"/>-reader fall-through
        /// (<see cref="GetGhostVesselPidForRecording"/> / <see cref="HasGhostVesselForRecording"/>) so
        /// watch-camera focus, TS-Fly, UI marker suppression, and the polyline owner resolve get the
        /// newest instance's pid (matching the legacy "one icon == newest cycle" behavior) instead of 0.
        /// </summary>
        internal static uint GetNewestOverlapInstancePidForRecording(
            int recIdx, bool excludeBoundarySecondary = false)
        {
            uint newestPid = 0;
            long newestCycle = long.MinValue;
            foreach (var kvp in overlapInstanceVessels)
            {
                if (kvp.Key.recIdx != recIdx)
                    continue;
                if (kvp.Value == null)
                    continue;
                // M1: the vesselsByRecordingIndex-reader fall-through excludes the boundary-overlap secondary
                // so it never shadows the launch recording's identity (the polyline's second-head resolution
                // passes false and still gets it). No-op for genuine self-overlap instances (not in the set).
                if (excludeBoundarySecondary && boundaryOverlapSecondaryPids.Contains(kvp.Value.persistentId))
                    continue;
                if (kvp.Key.cycle > newestCycle)
                {
                    newestCycle = kvp.Key.cycle;
                    newestPid = kvp.Value.persistentId;
                }
            }
            return newestPid;
        }

        /// <summary>
        /// Test-only (Unity-free): resolve the active cycle window [firstCycle, lastCycle] this
        /// recording's per-instance path WOULD drive at <paramref name="currentUT"/>, without minting
        /// any ProtoVessel. Mirrors the exact schedule resolution + GetActiveCycles call inside
        /// <see cref="EnsureOverlapInstances"/> so xUnit can pin the cycle-set equivalence with the
        /// flight engine. Returns (0, -1) (empty window) when the recording is not a resolvable loop
        /// or currentUT precedes the schedule start.
        /// </summary>
        internal static void OverlapCyclesForTesting(
            Recording rec, int recIdx, IReadOnlyList<Recording> committed,
            GhostPlaybackLogic.LoopUnitSet loopUnits, double currentUT,
            out long firstCycle, out long lastCycle)
        {
            firstCycle = 0;
            lastCycle = -1;
            if (!ResolveOverlapSchedule(
                    rec, recIdx, committed, loopUnits,
                    out _, out double scheduleStartUT,
                    out double duration, out double effectiveCadence, out _))
                return;
            if (currentUT < scheduleStartUT)
                return;
            GhostPlaybackLogic.GetActiveCycles(
                currentUT, scheduleStartUT, scheduleStartUT + duration,
                effectiveCadence, GhostPlayback.MaxOverlapGhostsPerRecording,
                out firstCycle, out lastCycle);
        }

        /// <summary>Test-only: snapshot the live (recIdx, cycle) keys for an index.</summary>
        internal static List<long> GetOverlapInstanceCyclesForTesting(int recIdx)
        {
            var cycles = new List<long>();
            foreach (var kvp in overlapInstanceVessels)
            {
                if (kvp.Key.recIdx == recIdx)
                    cycles.Add(kvp.Key.cycle);
            }
            cycles.Sort();
            return cycles;
        }

        /// <summary>
        /// Slice (iii): resolve the per-instance playback head UTs that the flight-map marker path
        /// should ride for an OVERLAP recording this frame - one entry per live overlap cycle, each
        /// at its own <see cref="GhostPlaybackLogic.ComputeOverlapCyclePlaybackUT"/> head (NOT the
        /// span-clock collapse of <see cref="GhostPlaybackLogic.ResolveTrackingStationSampleUT"/>).
        /// The cycle SET is byte-identical to the slice-(i) <see cref="OverlapCyclesForTesting"/> /
        /// <see cref="EnsureOverlapInstances"/> set (same gate + same schedule + same GetActiveCycles
        /// call), so the N markers on the shared polyline match the N flight meshes.
        ///
        /// Returns false (so the caller's legacy single-marker tail runs byte-identically) when the
        /// recording is not driven by the per-instance path (gate off / non-overlap), the schedule is
        /// unresolvable, or <paramref name="currentUT"/> precedes the first launch. On true,
        /// <paramref name="outBuffer"/> holds (cycle, headUT) for each live cycle in
        /// [firstCycle, lastCycle].
        /// </summary>
        internal static bool TryGetLiveOverlapHeadUTs(
            Recording rec, int recIdx, IReadOnlyList<Recording> committed,
            GhostPlaybackLogic.LoopUnitSet loopUnits, double currentUT,
            List<(long cycle, double headUT)> outBuffer)
        {
            if (outBuffer == null)
                return false;
            outBuffer.Clear();

            if (!ShouldDriveOverlapPerInstance(rec, recIdx, committed, loopUnits))
                return false;

            if (!ResolveOverlapSchedule(
                    rec, recIdx, committed, loopUnits,
                    out double playbackStartUT, out double scheduleStartUT,
                    out double duration, out double effectiveCadence, out double cycleDuration))
                return false;

            if (currentUT < scheduleStartUT)
                return false;

            GhostPlaybackLogic.GetActiveCycles(
                currentUT, scheduleStartUT, scheduleStartUT + duration,
                effectiveCadence, GhostPlayback.MaxOverlapGhostsPerRecording,
                out long firstCycle, out long lastCycle);

            for (long cycle = firstCycle; cycle <= lastCycle; cycle++)
            {
                double headUT = GhostPlaybackLogic.ComputeOverlapCyclePlaybackUT(
                    currentUT, scheduleStartUT, playbackStartUT,
                    duration, cycleDuration, cycle);
                outBuffer.Add((cycle, headUT));
            }

            return true;
        }

        /// <summary>
        /// Launch-&gt;escape seam render: resolve whether a launch-hold re-aim member has a LIVE
        /// boundary-overlap SECONDARY head this frame whose in-SOI ASCENT icon must be drawn as an
        /// ADDITIVE non-proto polyline marker (the early-launching instance N+1's ascent during the
        /// borrow window of a zero-slack re-aim launch loop). The secondary's own proto icon + escape
        /// conic come from the map-presence sweep ONCE its escape <c>OrbitSegment</c> materializes;
        /// this only covers the PRE-SEGMENT ascent gap, where no proto can exist (an atmospheric ascent
        /// has no orbit, so <c>ResolveMapPresenceGhostSource</c> returns None and the create defers).
        /// The caller rides this UT on the SHARED polyline via <c>DrawOneOverlapInstanceMarker</c> /
        /// <c>DrawOneTsOverlapInstanceMarker</c>, which self-gates per cycle (a live non-suppressed
        /// proto icon for the cycle skips the marker, so there is no double once the Segment exists).
        ///
        /// Returns false - with <paramref name="secondaryUT"/>/<paramref name="secondaryCycle"/>
        /// defaulted - for every non-map surface, debris recording, null recording, and any member
        /// without a live secondary head this frame (every non-launch-hold member and every ALIGNED
        /// loop, where <c>ResolveTrackingStationSampleFrame</c> reports <c>hasSecondary=false</c>), so
        /// the caller's additive marker draw stays inert there. Map-surface only because the secondary
        /// head needs the live UT + the shared map polyline to ride (flight view passes currentUT=0 and
        /// has no map polyline).
        /// </summary>
        internal static bool TryResolveBoundaryOverlapSecondaryMarker(
            bool isMapSurface, Recording rec, int recIdx, double liveUT,
            GhostPlaybackLogic.LoopUnitSet loopUnits,
            out double secondaryUT, out long secondaryCycle)
        {
            secondaryUT = liveUT;
            secondaryCycle = 0;
            if (!isMapSurface || rec == null || rec.IsDebris)
                return false;
            GhostPlaybackLogic.ResolveTrackingStationSampleFrame(
                recIdx, rec.StartUT, rec.EndUT, liveUT, loopUnits,
                out _, out bool hasSecondary, out secondaryUT, out secondaryCycle);
            return hasSecondary;
        }

        /// <summary>
        /// Slice (iii) orbital-interaction join: resolve the live per-instance ProtoVessel pid for a
        /// specific overlap (recIdx, cycle), or 0 when no instance exists for that cycle (the
        /// pure-suborbital case - <see cref="overlapInstanceVessels"/> never holds a state-vector cycle
        /// - or a cycle not yet materialized this frame). Lets the marker path decide per-cycle whether
        /// the polyline marker is redundant with a live proto icon (skip) or is the sole indicator
        /// (draw).
        /// </summary>
        internal static uint TryGetOverlapInstancePidForCycle(int recIdx, long cycle)
        {
            if (overlapInstanceVessels.TryGetValue((recIdx, cycle), out Vessel inst) && inst != null)
                return inst.persistentId;
            return 0u;
        }

        /// <summary>
        /// Build a per-instance overlap map ProtoVessel from a recorded state-vector point (physics-only
        /// suborbital ascent cycle), without the per-index <see cref="TrackRecordingGhostVessel"/> write
        /// or the Re-Fly suppression machinery in <see cref="CreateGhostVesselFromStateVectors"/> (which
        /// is per-index state and irrelevant to overlap loops). Resolves the world position via the same
        /// <see cref="ResolveStateVectorWorldPosition"/> the per-index path uses, then seeds an orbit
        /// from the world pos + recorded velocity.
        ///
        /// Mirrors the per-index state-vector contract (<c>UpdateGhostOrbitFromStateVectors</c>,
        /// GhostMapPresence.cs:7956-8000): the orbit is seeded at the SHIFTED epoch
        /// (<paramref name="effUT"/> + <paramref name="loopEpochShiftSeconds"/> == the instance's LIVE
        /// UT) so stock <see cref="OrbitDriver"/> propagation at the live Planetarium clock lands the
        /// icon on the world position recorded at <paramref name="effUT"/>. Physics-only recordings have
        /// no <c>OrbitSegments</c>, so there is no segment-drive raw-epoch + effUT contract here: the
        /// caller must NOT register <c>ghostOrbitEpochShift</c> / <c>ghostOrbitLoopShiftedPids</c> for a
        /// state-vector instance (that would make <see cref="GhostOrbitIconDrivePatch"/> bail on the
        /// no-bounds branch and re-subtract a shift from this already-shifted orbit). Returns null when
        /// the body or world frame cannot resolve (the cycle is then retried next tick).
        /// </summary>
        private static Vessel BuildAndLoadGhostProtoVesselFromStateVector(
            IPlaybackTrajectory traj, TrajectoryPoint point, double effUT, double loopEpochShiftSeconds,
            string logContext)
        {
            CelestialBody body = FindBodyByName(point.bodyName);
            if (body == null)
                return null;

            StateVectorWorldFrame resolution =
                ResolveStateVectorWorldPosition(traj, point, body, allowOrbitalCheckpointStateVector: true);
            if (!resolution.Resolved)
                return null;

            Vector3d worldPos = resolution.WorldPos;
            Vector3d vel = new Vector3d(point.velocity.x, point.velocity.y, point.velocity.z);

            // Seed at the SHIFTED epoch (live UT), mirroring the per-index state-vector path so stock
            // propagation at the live clock lands the icon on the effUT-recorded world position.
            Orbit orbit = new Orbit();
            OrbitReseed.FromWorldPosAndRecordedVelocity(
                orbit, body, worldPos, vel, effUT + loopEpochShiftSeconds);

            return BuildAndLoadGhostProtoVesselCore(
                traj,
                orbit,
                body,
                logContext,
                "overlap-instance-state-vector",
                string.Format(ic,
                    "stateBody={0} effUT={1:F1} liveUT={2:F1} stateAlt={3:F0} stateSpeed={4:F1} frame={5}",
                    point.bodyName ?? "(null)", effUT, effUT + loopEpochShiftSeconds,
                    point.altitude, point.velocity.magnitude, resolution.Branch));
        }

        /// <summary>
        /// Per-frame check for ghost map ProtoVessels. Handles three responsibilities:
        /// 1. Creates deferred ProtoVessels when ghosts enter their first orbital segment
        /// 2. Creates state-vector orbit ProtoVessels for physics-only suborbital recordings
        /// 3. Updates existing ProtoVessel orbits (segment-based and state-vector-based)
        /// </summary>
        internal static void UpdateFlightMapGhostLifecycle(
            double currentUT, GhostPlaybackLogic.LoopUnitSet loopUnits)
        {
            // Mission-loop span clock: the same per-frame LoopUnitSet the flight engine drives the
            // ghost MESHES with (engine.CurrentLoopUnits). The Tracking Station and KSC map drivers
            // already remap the live UT through this clock; this is the flight-scene equivalent so
            // a looped Mission's map orbit lines + icons sample the recording at the loop-mapped
            // effUT instead of the raw live UT (which is far outside a looped member's recorded UT
            // range). Empty (the common case) makes every ResolveMapPresenceSampleUT below return
            // the unchanged live UT with shift 0, so non-looping behavior is byte-identical.

            RunFlightMapDeferredCreatePass(currentUT, loopUnits);            // Pass 1 (always runs)

            // Per-instance overlap sweep (slice i). The SOLE create/destroy authority for overlap
            // recordings on the flight map; the three legacy passes below skip overlap indices.
            // Runs in the always-runs section so cycles relaunch/expire promptly. Resolves the live
            // committed list here (the rate-limited section below resolves its own copy after the
            // gate; the overlap path must run every frame).
            RunOverlapPerInstanceSweep(currentUT, RecordingStore.CommittedRecordings, loopUnits);

            // 2. Map-orbit reseed for existing ProtoVessels. Rate-limited to the real-time timer, BUT
            // warp-aware: under time warp the playback head sprints through short segments (e.g. the many
            // short Duna-capture conics) faster than the 0.5 s timer reseeds, so the applied orbit goes
            // stale and GhostOrbitLinePatch's stale-segment guard blanks the line every frame (the ~1-min
            // warped-approach blink). Also reseed the moment a ghost's head leaves its applied segment so
            // the orbit stays current and the stale-segment guard (which still suppresses the genuine
            // pre-burn arc on a propulsive->orbital handoff) stops firing on reseed lag alone. The head-left
            // scan is computed only while warping + before the timer (so 1x is byte-identical + cost-free).
            //
            // Pass 2 gate + preamble stay HERE so the two early-returns keep skipping Pass 3.
            bool mapReseedTimerElapsed = UnityEngine.Time.time >= nextMapOrbitUpdateTime;
            bool mapReseedHeadLeftSegment = !mapReseedTimerElapsed
                && TimeWarp.CurrentRate > 1.0f
                && GhostMapPresence.AnyGhostHeadLeftAppliedSegment(currentUT);
            if (!ParsekPlaybackPolicy.ShouldRunMapOrbitReseed(mapReseedTimerElapsed, TimeWarp.CurrentRate, mapReseedHeadLeftSegment))
                return;
            nextMapOrbitUpdateTime = UnityEngine.Time.time + MapOrbitUpdateIntervalSec;

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return;
            PruneTerminalMapRetentionLogKeys(committed);

            RunFlightMapOrbitReseedPass(currentUT, loopUnits, committed);    // Pass 2 body
            RunFlightMapStateVectorUpdatePass(currentUT, loopUnits, committed); // Pass 3 body
        }

        // Pass 1 — deferred create. Always runs (no gate). Creates deferred ProtoVessels for ghosts
        // that just entered an orbital segment OR for physics-only recordings that crossed the
        // state-vector threshold. Body relocated verbatim from the former inline pass.
        private static void RunFlightMapDeferredCreatePass(
            double currentUT, GhostPlaybackLogic.LoopUnitSet loopUnits)
        {
            // 1. Create deferred ProtoVessels for ghosts that just entered an orbital segment
            //    OR for physics-only recordings that crossed the state-vector threshold.
            if (flightPendingMapVessels.Count > 0)
            {
                List<(int idx, TrackingStationGhostSource source, OrbitSegment segment, TrajectoryPoint point, string expectedBody, double effUT)> toCreate = null;

                var committedForOverlapSkip = RecordingStore.CommittedRecordings;
                foreach (var kvp in flightPendingMapVessels)
                {
                    int idx = kvp.Key;
                    PendingMapVessel pending = kvp.Value;
                    IPlaybackTrajectory traj = pending.Trajectory;

                    // Slice (i): overlap recordings are owned solely by the per-instance sweep
                    // (RunOverlapPerInstanceSweep, run earlier this frame). Skip the legacy
                    // single-instance deferred create for them so there is no double-management.
                    if (committedForOverlapSkip != null && idx >= 0 && idx < committedForOverlapSkip.Count
                        && ShouldDriveOverlapPerInstance(committedForOverlapSkip[idx], idx, committedForOverlapSkip, loopUnits))
                        continue;

                    // Loop-mapped sample UT for this member (effUT == currentUT off the loop path).
                    // renderHidden => this member is outside its loop window this cycle: skip the
                    // create, exactly like the Tracking Station create pass skips it.
                    double effUT = ResolveMapPresenceSampleUT(
                        idx, traj.StartUT, traj.EndUT, currentUT, loopUnits,
                        out bool renderHidden, out double pendingLoopShift);
                    if (renderHidden)
                        continue;

                    // A non-zero loop epoch shift means this is a Mission-loop member replaying
                    // inside its window this cycle (effUT != live currentUT). Two consequences:
                    //  - it unlocks the no-segment terminal-orbit synthesis (plan section 1.4); and
                    //  - it lets the map ghost draw even when the recording already materialized a
                    //    persisted real terminal vessel (loopMemberInWindow). Without the latter, a
                    //    looped leg whose mission left a real craft parked at its terminal (e.g. the
                    //    Mun-return leg) gets no trajectory following the ghost, because the static
                    //    real vessel claims the materialization. Both flags default false off the loop
                    //    path, so neither the terminal-orbit synthesis nor the materialization bypass
                    //    affects non-loop members. (The EndpointTail entry in the acceptance check below
                    //    is a SEPARATE, intentional consistency fix -- it materializes a non-loop
                    //    member that resolves to EndpointTail, which the create path previously left
                    //    pending -- and is NOT gated by these flags.)
                    bool isLoopMemberInWindow = pendingLoopShift != 0.0;
                    bool acceptTerminalOrbitForLoopSynthesis =
                        isLoopMemberInWindow
                        && GhostMapPresence.IsTerminalOrbitSynthesisSafeForLoopMember(traj);
                    if (acceptTerminalOrbitForLoopSynthesis)
                    {
                        ParsekLog.VerboseRateLimited("Policy",
                            string.Format(CultureInfo.InvariantCulture,
                                "pending-map-accept-terminal-{0}", idx),
                            string.Format(CultureInfo.InvariantCulture,
                                "Pending map-create accepting terminal-orbit synthesis for loop member " +
                                "rec=#{0} vessel=\"{1}\" pendingLoopShift={2:F1}",
                                idx,
                                traj.VesselName ?? "(null)",
                                pendingLoopShift),
                            5.0);
                    }

                    int cachedStateVectorIndex = flightStateVectorCachedIndices.TryGetValue(idx, out int cached)
                        ? cached
                        : -1;
                    // Step 2 (flight map presence): suppress this rec's OWN loop-ghost
                    // double ONLY while its guid-gated launch-matched live vessel is
                    // loaded AND it was the LIVE docking anchor of an in-window relative
                    // member this-or-last frame (the Step-1 live-bind event), NOT for the
                    // whole loop (mirrors the TS lifecycle pass). Keyed on the guid-gated
                    // RealVesselExistsForRecording, NOT the pid-only
                    // IsMaterializedForMapPresence that feeds alreadyMaterialized here, so
                    // a same-craft different-launch vessel never suppresses. Best-effort
                    // on this map path: the live-bind is stamped at member resolution
                    // (ghost (re)creation), not per on-rails frame, so the duplicate can
                    // briefly show until the next resolve (see WasLiveBoundThisOrLastFrame).
                    Recording pendingAnchorRec = (committedForOverlapSkip != null
                        && idx >= 0 && idx < committedForOverlapSkip.Count)
                        ? committedForOverlapSkip[idx]
                        : null;
                    bool liveLaunchMatchedAnchorOfActiveMember =
                        pendingAnchorRec != null
                        && GhostPlaybackLogic.RealVesselExistsForRecording(pendingAnchorRec)
                        && RelativeAnchorResolver.WasLiveBoundThisOrLastFrame(pendingAnchorRec.RecordingId);
                    TrackingStationGhostSource source = GhostMapPresence.ResolveMapPresenceGhostSource(
                        traj,
                        false,
                        IsMaterializedForMapPresence(traj),
                        effUT,
                        true,
                        "map-presence-pending-create",
                        ref cachedStateVectorIndex,
                        out OrbitSegment segment,
                        out TrajectoryPoint point,
                        out _,
                        recordingIndex: idx,
                        allowSoiGapStateVectorFallback: pending.AllowSoiGapStateVectorFallback,
                        expectedSoiGapBody: pending.ExpectedSoiGapBody,
                        acceptTerminalOrbitForLoopSynthesis: acceptTerminalOrbitForLoopSynthesis,
                        loopMemberInWindow: isLoopMemberInWindow,
                        liveLaunchMatchedAnchorOfActiveMember: liveLaunchMatchedAnchorOfActiveMember);
                    flightStateVectorCachedIndices[idx] = cachedStateVectorIndex;

                    // Re-aim: swap the recorded covering segment for the re-aimed one at create time so
                    // the orbit line is aimed at the target's CURRENT position from the first frame the
                    // ghost re-enters its window. A re-aim owner in a TRIM GAP (between a recorded
                    // body-relative leg and the trimmed interplanetary transfer) has no covering re-aimed
                    // segment: skip the create entirely (keep the member pending / hidden) rather than
                    // fall back to a recorded sub-segment, which re-created the ghost at a random orbit
                    // position every frame while the refresh removed it (the SOI-boundary icon flicker).
                    if (source == TrackingStationGhostSource.Segment
                        && !GhostMapPresence.TryResolveReaimedCoveringSegment(
                            idx, traj.RecordingId, traj.OrbitSegments, currentUT, effUT, loopUnits,
                            segment, out segment))
                        continue;

                    if (GhostMapPresence.IsMapCreateAcceptedSource(source))
                    {
                        if (toCreate == null)
                            toCreate = new List<(int, TrackingStationGhostSource, OrbitSegment, TrajectoryPoint, string, double)>();
                        toCreate.Add((idx, source, segment, point, pending.ExpectedSoiGapBody, effUT));
                    }
                }

                if (toCreate != null)
                {
                    for (int i = 0; i < toCreate.Count; i++)
                    {
                        int idx = toCreate[i].idx;
                        if (flightPendingMapVessels.TryGetValue(idx, out var pending))
                        {
                            IPlaybackTrajectory traj = pending.Trajectory;
                            // Per-chain dedup before deferred creation
                            RemovePreviousChainMapVessel(idx);
                            // Create at the loop-mapped effUT so the source/segment/point match the
                            // looped phase (effUT == currentUT off the loop path). Pass the live-frame
                            // epoch shift through so the orbit + body-frame cache are shifted at
                            // create-time too. Without this the body-frame cache holds raw recorded
                            // UTs on the first frame, and the orbit-line patch sees currentUT past
                            // those bounds and blanks the line until the next rate-limited update
                            // pass refreshes it (up to ~0.5s in flight, ~2s in TS) -- visible as a
                            // brief orbit-line blackout at first appearance and at every window
                            // re-entry.
                            double pendingLoopEpochShift = currentUT - toCreate[i].effUT;
                            Vessel ghost = GhostMapPresence.CreateGhostVesselFromSource(
                                idx,
                                traj,
                                toCreate[i].source,
                                toCreate[i].segment,
                                toCreate[i].point,
                                toCreate[i].effUT,
                                out bool retryLater,
                                loopEpochShiftSeconds: pendingLoopEpochShift);
                            // PR #574 review P2 (retry-later semantics): keep
                            // the pending entry alive when the active-Re-Fly
                            // suppression gate fires — otherwise a parent
                            // recording mid-flight in a Relative-anchored
                            // section would be permanently dropped from the
                            // pending-map queue and never re-attempt after
                            // the Re-Fly session ends.
                            if (retryLater && ghost == null)
                            {
                                ParsekLog.Verbose("Policy", string.Format(CultureInfo.InvariantCulture,
                                    "CheckPendingMapVessels: kept pending entry for #{0} \"{1}\" — " +
                                    "map ghost creation requested retryLater, will retry next tick",
                                    idx, traj.VesselName ?? "(null)"));
                                continue;
                            }
                            flightPendingMapVessels.Remove(idx);

                            if (ghost != null)
                            {
                                if (GhostMapPresence.IsStateVectorGhostSource(toCreate[i].source))
                                {
                                    flightStateVectorOrbitTrajectories[idx] = traj;
                                    if (toCreate[i].source == TrackingStationGhostSource.StateVectorSoiGap)
                                        flightSoiGapStateVectorExpectedBodies[idx] = toCreate[i].expectedBody;
                                    else
                                        flightSoiGapStateVectorExpectedBodies.Remove(idx);
                                    ParsekLog.Info("Policy", string.Format(CultureInfo.InvariantCulture,
                                        "Created state-vector ghost map vessel for #{0} \"{1}\" — alt={2:F0} speed={3:F1} source={4}",
                                        idx, traj.VesselName,
                                        toCreate[i].point.altitude,
                                        toCreate[i].point.velocity.magnitude,
                                        toCreate[i].source == TrackingStationGhostSource.StateVectorSoiGap
                                            ? "soi-gap-state-vector"
                                            : "state-vector"));
                                }
                                else if (TryGetMapOrbitKey(toCreate[i].source, toCreate[i].segment, out var orbitKey))
                                {
                                    flightSoiGapStateVectorExpectedBodies.Remove(idx);
                                    flightLastMapOrbitByIndex[idx] = orbitKey;

                                    ParsekLog.Info("Policy",
                                        $"Created deferred ghost map vessel for #{idx} \"{traj.VesselName}\" " +
                                        $"— source={toCreate[i].source} body={orbitKey.body} sma={orbitKey.sma:F0}");
                                }
                            }
                        }
                    }
                }
            }
        }

        // Pass 2 — segment-based map-orbit reseed for existing ProtoVessels. Runs only after the
        // orchestrator's gate + preamble passed (committed already resolved, non-null, and the
        // terminal-retention log keys already pruned). Body relocated verbatim from the former
        // "2a. Segment-based orbit updates" pass.
        private static void RunFlightMapOrbitReseedPass(
            double currentUT, GhostPlaybackLogic.LoopUnitSet loopUnits, IReadOnlyList<Recording> committed)
        {
            // 2a. Segment-based orbit updates (existing)
            List<KeyValuePair<int, (string body, double sma, double ecc)>> orbitUpdates = null;
            List<int> toRemoveFromMap = null;
            List<(int idx, string expectedBody)> toRequeue = null;

            foreach (var kvp in flightLastMapOrbitByIndex)
            {
                int idx = kvp.Key;
                if (idx < 0 || idx >= committed.Count) continue;

                var rec = committed[idx];
                if (!rec.HasOrbitSegments) continue;

                // Slice (i): overlap recordings are reseeded by the per-instance sweep, not here.
                if (ShouldDriveOverlapPerInstance(rec, idx, committed, loopUnits))
                    continue;

                // Loop-mapped sample UT + live-frame epoch shift (effUT == currentUT, shift 0 off
                // the loop path). renderHidden => the member left its loop window this cycle: tear
                // the orbit ghost down, mirroring the Tracking Station refresh pass.
                double effUT = ResolveMapPresenceSampleUT(
                    idx, rec.StartUT, rec.EndUT, currentUT, loopUnits,
                    out bool renderHidden, out double loopEpochShiftSeconds);
                if (renderHidden)
                {
                    // Enrich the teardown reason with the loop role: a descent member tearing down here is the
                    // descent-revert/finish path (the icon falls back to the loiter conic), so the destroy line
                    // names it for correlation with the [ReaimDescent] DESCENT REVERTED/SKIPPED line. Empty (=>
                    // byte-identical reason) for every non-descent member.
                    GhostMapPresence.RemoveGhostVesselForRecording(idx,
                        "mission-loop-out-of-window"
                        + GhostPlaybackLogic.DescribeLoopMemberRoleForTeardown(idx, loopUnits));
                    if (toRemoveFromMap == null) toRemoveFromMap = new List<int>();
                    toRemoveFromMap.Add(idx);
                    // Re-queue to pendingMapVessels so the create pass re-materializes the orbit
                    // ghost when the span clock sweeps back into this member's window next cycle.
                    // Unlike the Tracking Station (which scans all committed indices every frame),
                    // the flight driver only creates from the pending queue, and the ProtoVessel
                    // was just removed, so 2a alone could never bring it back.
                    if (toRequeue == null) toRequeue = new List<(int, string)>();
                    toRequeue.Add((idx, null));
                    continue;
                }

                // Re-aim: for a re-aim loop owner, the flight map orbit line must follow the per-window
                // RE-AIMED transfer (aimed at the target's CURRENT position), not the recorded geometry
                // (which points at the target's RECORDED position - the "wrong place in the target's
                // orbit" the playtest showed). Resolved ONCE here from the LIVE currentUT (same window
                // the flight engine + tracking station use) and threaded through every effUT-based read
                // below; reference-identical to rec.OrbitSegments for every non-re-aim member.
                List<OrbitSegment> effectiveSegments = GhostMapPresence.ResolveEffectiveMapOrbitSegments(
                    idx, rec.RecordingId, rec.OrbitSegments, currentUT, loopUnits);

                // LOITER-GAP map-presence clamp (re-aim looped LANDING destination loiter): the descent-trigger
                // TRANSFER member's recorded loop clock (effUT) sweeps the parking conic up to its end
                // (ParkingConicEndUT = the destination loiter run end = the deorbit point). PAST that point an
                // UNCLAMPED FindOrbitSegmentOrSameBodyCarry walks INTO the CONTIGUOUS deorbit-transition
                // OrbitSegment and draws that deorbit arc as the loiter orbit (~1/3 of an ellipse), then past the
                // deorbit-arc end no segment covers effUT and the proto is destroyed ("left-orbit-segments"), so
                // the parking conic stops rendering until the descent fires. HOLD the segment-lookup sample UT at
                // ParkingConicEndUT inside the gap so the lookup keeps returning the real recorded PARKING-conic
                // segment and the proto stays alive on it. The clamp applies ONLY to this segment lookup; every
                // other read below still uses the live effUT. Layer B then stamps a per-pid line hold so
                // GhostOrbitLinePatch keeps the FULL parking ellipse drawn until the live descent trigger. False
                // (byte-identical) for every member except the destination transfer member (the owner, every
                // ride-along in a different/unshifted frame, and every descent-set member), for non-re-aim units,
                // and once the descent trigger fires (the transfer member then retires via the live-clock
                // continuation gate).
                double segmentLookupUT = effUT;
                uint flightGhostPid = vesselsByRecordingIndex.TryGetValue(idx, out Vessel flightGhostVessel)
                        && flightGhostVessel != null
                    ? flightGhostVessel.persistentId
                    : 0u;
                if (GhostPlaybackLogic.IsDescentTransferMemberInLoiterGap(loopUnits, idx, effUT))
                {
                    segmentLookupUT = GhostPlaybackLogic.ResolveLoiterGapConicEndUT(loopUnits, idx);
                    ParsekLog.VerboseRateLimited("Policy",
                        "flight-loiter-gap-clamp-" + idx,
                        string.Format(CultureInfo.InvariantCulture,
                            "Flight loiter-gap clamp: member={0} effUT={1:F1} held segment-lookup UT at parkingConicEnd={2:F1} "
                            + "(re-aim descent transfer member in captureShift loiter gap; parking conic kept rendering)",
                            idx, effUT, segmentLookupUT),
                        5.0);
                    // Layer B: hold the parking-conic LINE visible through the loiter (past the live parking-conic
                    // bound) until the live descent trigger; GhostOrbitLinePatch consults the stamp.
                    StampParkingConicLineHoldForLoiterGap(
                        idx, flightGhostPid, currentUT, loopUnits,
                        rec.StartUT, rec.EndUT, "FLIGHT", "flight-parking-conic-line-hold-");
                }
                else if (flightGhostPid != 0)
                {
                    // Not in the gap (still on the conic, or the descent fired / loop wrapped): tear down any
                    // line hold for this ghost so the stamp never lingers into the next phase.
                    ClearParkingConicLineHold(flightGhostPid);
                }

                // Same-body carry: while the playback head is inside a body frame, briefly
                // dropping the ghost between two non-orbit-equivalent segments (e.g., capture
                // burn between two Mun orbits) would tear down and recreate the ProtoVessel,
                // producing the visible flicker the user reported near Mun SOI. We only want
                // to drop the ghost when the body actually changes (SOI / frame change) or
                // when the recording is truly past its last segment. Body-frame carry keeps
                // the previous segment's orbit active until UT enters the next segment.
                OrbitSegment? seg = TrajectoryMath.FindOrbitSegmentOrSameBodyCarry(effectiveSegments, segmentLookupUT);

                // Gap-points glide: FindOrbitSegmentOrSameBodyCarry keeps seg.HasValue true across a
                // same-body inter-segment gap by carrying the PREVIOUS segment forward. That is correct
                // for an equivalent-orbit gap (capture burn between two same Mun orbits) but wrong for a
                // real orbit RAISE (parking -> higher loiter, sma 671928 -> 731230 across a ~205s arc):
                // the stale parking orbit freezes the icon, then it teleports ~1318 km onto the loiter
                // orbit when UT enters the next segment. The recorder captured that raise as body-fixed
                // Absolute trajectory POINTS, so route the icon onto those points and glide it across the
                // gap. Gated to the NON-orbit-equivalent gap so the equivalent-orbit carry stays untouched.
                bool driveGapFromPoints =
                    seg.HasValue
                    && GhostMapPresence.ShouldDriveGapFromPoints(effectiveSegments, rec, effUT);

                // Flight-map companion to the TS covering-segment log: same MapTraj on-change diagnostic so the
                // SOI-exit blink / orbit-line GAP / body flip-flop is visible on the flight path too. When a
                // segment covers effUT the icon follows that OrbitSegment (source=Segment); when none does and
                // a terminal state-vector fallback is cached for this member, the icon is driven by that
                // fallback (resolved in the !seg.HasValue branch below), so report source=StateVector. The
                // gap-points glide above also drives the icon from the state-vector point path, so report
                // source=StateVector for it too. The membership check is stable frame-to-frame (it does not
                // flip-flop) and mirrors the TS path's trackingStationStateVectorOrbitTrajectories check.
                bool flightIsStateVector =
                    driveGapFromPoints
                    || (!seg.HasValue && flightStateVectorCachedIndices.ContainsKey(idx));
                GhostMapPresence.LogMapCoveringSegmentChange("FLIGHT", idx, effUT, seg, seg.HasValue && !driveGapFromPoints,
                    flightIsStateVector,
                    effectiveSegments != null ? effectiveSegments.Count : 0);

                // Gap-points glide branch: drive the existing (Segment-created) ghost from the recorded
                // body-fixed point at effUT via the same state-vector positioner the SOI-gap fallback uses.
                // UpdateGhostOrbitFromStateVectors clears the stale segment-drive dicts (bounds / loop-shift /
                // epoch-shift) so GhostOrbitIconDrivePatch defers to stock at live UT instead of clamping the
                // icon past-window on the parking orbit. continue skips the segment-apply tail below.
                if (driveGapFromPoints)
                {
                    int gapCachedIndex = flightStateVectorCachedIndices.TryGetValue(idx, out int gapCached)
                        ? gapCached
                        : -1;
                    TrajectoryPoint? gapPoint = TrajectoryMath.BracketPointAtUT(rec.Points, effUT, ref gapCachedIndex);
                    flightStateVectorCachedIndices[idx] = gapCachedIndex;
                    if (gapPoint.HasValue)
                    {
                        // UpdateGhostOrbitFromStateVectors returns true ONLY in the active-re-fly
                        // suppression case; false on the normal success path. The flight scene removes the
                        // ghost via its own re-fly suppression machinery (MarkStateVectorGhostDeferredForActiveReFly
                        // runs inside the call), so we just record the result for the log here.
                        bool reFlySuppressed = GhostMapPresence.UpdateGhostOrbitFromStateVectors(
                            idx, rec, gapPoint.Value, effUT,
                            stateVectorUpdateReason: "orbit-raise-gap-points",
                            loopEpochShiftSeconds: loopEpochShiftSeconds);
                        ParsekLog.VerboseRateLimited("Policy",
                            "flight-gap-points-glide-" + idx,
                            string.Format(CultureInfo.InvariantCulture,
                                "Flight gap-points glide: member={0} effUT={1:F1} body={2} alt={3:F0} reFlySuppressed={4} " +
                                "(orbit-raise gap, icon glides recorded ascent instead of segment carry)",
                                idx, effUT, gapPoint.Value.bodyName ?? "(null)", gapPoint.Value.altitude, reFlySuppressed),
                            2.0);
                        // The ghost is now state-vector-driven; record a sentinel orbit key (sma/ecc 0)
                        // via the deferred orbitUpdates list (we cannot mutate lastMapOrbitByIndex during
                        // its own enumeration) so the next in-segment frame sees a differing key and
                        // re-applies a fresh covering orbit instead of skip-unchanged.
                        if (orbitUpdates == null) orbitUpdates = new List<KeyValuePair<int, (string, double, double)>>();
                        orbitUpdates.Add(new KeyValuePair<int, (string, double, double)>(
                            idx, (gapPoint.Value.bodyName, 0.0, 0.0)));
                        continue;
                    }
                    // No bracketing point (effUT precedes first / past last recorded point): fall through
                    // to the unchanged carry/segment-apply path. Never force a single stale point.
                    ParsekLog.VerboseRateLimited("Policy",
                        "flight-gap-points-nobracket-" + idx,
                        string.Format(CultureInfo.InvariantCulture,
                            "Flight gap-points glide skipped (no bracketing point): member={0} effUT={1:F1} " +
                            "(falling through to same-body segment carry)",
                            idx, effUT),
                        5.0);
                }

                // No map-visible orbit at current UT — either we've truly left orbital
                // playback, or the next segment is in a different SOI/body.
                if (!seg.HasValue)
                {
                    int cachedStateVectorIndex = flightStateVectorCachedIndices.TryGetValue(idx, out int cached)
                        ? cached
                        : -1;
                    if (ParsekPlaybackPolicy.TryResolveTerminalFallbackMapOrbitUpdate(
                        rec,
                        idx,
                        effUT,
                        loopEpochShiftSeconds,
                        kvp.Value,
                        IsMaterializedForMapPresence(rec),
                        ref cachedStateVectorIndex,
                        out OrbitSegment fallbackSegment,
                        out var fallbackKey,
                        out bool fallbackChanged))
                    {
                        flightStateVectorCachedIndices[idx] = cachedStateVectorIndex;
                        if (fallbackChanged)
                        {
                            GhostMapPresence.UpdateGhostOrbitForRecording(
                                idx, fallbackSegment,
                                loopEpochShiftSeconds: loopEpochShiftSeconds);
                            if (orbitUpdates == null) orbitUpdates = new List<KeyValuePair<int, (string, double, double)>>();
                            orbitUpdates.Add(new KeyValuePair<int, (string, double, double)>(idx, fallbackKey));
                        }
                        continue;
                    }
                    flightStateVectorCachedIndices[idx] = cachedStateVectorIndex;

                    bool hasFutureSegment = false;
                    string futureSegmentBody = null;
                    var segs = effectiveSegments;
                    for (int s = 0; s < segs.Count; s++)
                    {
                        if (segs[s].startUT > effUT)
                        {
                            hasFutureSegment = true;
                            futureSegmentBody = segs[s].bodyName;
                            break;
                        }
                    }

                    if (ParsekPlaybackPolicy.ShouldRetainMapPresenceForTerminalRealSpawn(rec, hasFutureSegment))
                    {
                        string retentionKey = rec.RecordingId ?? idx.ToString(CultureInfo.InvariantCulture);
                        string reason = rec.TerminalSpawnCannotSpawnSafely
                            ? rec.TerminalSpawnSafetyReasonCode ?? "cannot-spawn-safely"
                            : rec.TerminalSpawnSafetyDeferred
                                ? rec.TerminalSpawnSafetyReasonCode ?? "deferred"
                                : "pending-terminal-real-spawn";
                        string message = string.Format(CultureInfo.InvariantCulture,
                            "Map presence retained because terminal real spawn is pending/deferred: " +
                            "rec={0} idx={1} vessel=\"{2}\" currentUT={3:F2} reason={4}",
                            rec.RecordingId ?? "(null)",
                            idx,
                            rec.VesselName ?? "(null)",
                            currentUT,
                            reason);
                        if (flightTerminalMapRetentionLoggedIds.Add(retentionKey))
                            ParsekLog.Info("Policy", message);
                        else
                            ParsekLog.VerboseRateLimited(
                                "Policy",
                                "terminal-map-retained-" + retentionKey,
                                message);
                        continue;
                    }

                    GhostMapPresence.RemoveGhostVesselForRecording(idx,
                        hasFutureSegment ? "gap-between-orbit-segments" : "left-orbit-segments");

                    if (hasFutureSegment)
                    {
                        // Re-add to pending so the next segment creates a new ProtoVessel
                        if (toRequeue == null) toRequeue = new List<(int, string)>();
                        toRequeue.Add((idx, futureSegmentBody));
                    }

                    if (toRemoveFromMap == null) toRemoveFromMap = new List<int>();
                    toRemoveFromMap.Add(idx);
                    continue;
                }

                // Skip re-applying an unchanged orbit — EXCEPT for loop members, whose stored key
                // (body/sma/ecc) does not capture the per-cycle epoch + bounds shift, so a cycle
                // wrap onto the same-shape segment would otherwise leave a stale shift and mis-clip
                // the arc. Loop members re-apply every tick (cheap; the shift is constant within a
                // cycle so this is idempotent until the wrap).
                if (loopEpochShiftSeconds == 0.0
                    && seg.Value.bodyName == kvp.Value.body
                    && seg.Value.semiMajorAxis == kvp.Value.sma
                    && seg.Value.eccentricity == kvp.Value.ecc)
                    continue;

                GhostMapPresence.UpdateGhostOrbitForRecording(
                    idx, seg.Value,
                    loopEpochShiftSeconds: loopEpochShiftSeconds,
                    effectiveOrbitSegments: effectiveSegments);
                if (orbitUpdates == null) orbitUpdates = new List<KeyValuePair<int, (string, double, double)>>();
                orbitUpdates.Add(new KeyValuePair<int, (string, double, double)>(
                    idx, (seg.Value.bodyName, seg.Value.semiMajorAxis, seg.Value.eccentricity)));
            }

            if (orbitUpdates != null)
            {
                for (int i = 0; i < orbitUpdates.Count; i++)
                    flightLastMapOrbitByIndex[orbitUpdates[i].Key] = orbitUpdates[i].Value;
            }
            if (toRemoveFromMap != null)
            {
                for (int i = 0; i < toRemoveFromMap.Count; i++)
                    flightLastMapOrbitByIndex.Remove(toRemoveFromMap[i]);
            }
            if (toRequeue != null)
            {
                for (int i = 0; i < toRequeue.Count; i++)
                {
                    int idx = toRequeue[i].idx;
                    if (!flightPendingMapVessels.ContainsKey(idx) && idx < committed.Count)
                    {
                        var traj = committed[idx] as IPlaybackTrajectory;
                        if (traj != null)
                        {
                            flightPendingMapVessels[idx] = new PendingMapVessel(
                                traj,
                                allowSoiGapStateVectorFallback: true,
                                expectedSoiGapBody: toRequeue[i].expectedBody);
                            ParsekLog.Verbose("MapPresence",
                                $"Re-queued recording #{idx} to pendingMapVessels (gap between orbit segments expectedBody={toRequeue[i].expectedBody ?? "(none)"})");
                        }
                    }
                }
            }
        }

        // Pass 3 — state-vector orbit updates (physics-only suborbital) for existing ProtoVessels.
        // Runs only after the orchestrator's gate + preamble passed (committed already resolved and
        // non-null). Body relocated verbatim from the former "2b. State-vector orbit updates" pass,
        // including the EmitLifecycleSummary tail.
        private static void RunFlightMapStateVectorUpdatePass(
            double currentUT, GhostPlaybackLogic.LoopUnitSet loopUnits, IReadOnlyList<Recording> committed)
        {
            // 2b. State-vector orbit updates (new — physics-only suborbital)
            if (flightStateVectorOrbitTrajectories.Count > 0)
            {
                List<int> toReDefer = null;
                List<int> toExitStateVector = null;
                List<KeyValuePair<int, (string body, double sma, double ecc)>> stateVectorSegmentUpdates = null;

                foreach (var kvp in flightStateVectorOrbitTrajectories)
                {
                    int idx = kvp.Key;
                    IPlaybackTrajectory traj = kvp.Value;

                    // Slice (i): overlap recordings are driven by the per-instance sweep, not here.
                    if (committed != null && idx >= 0 && idx < committed.Count
                        && ShouldDriveOverlapPerInstance(committed[idx], idx, committed, loopUnits))
                        continue;

                    if (!flightStateVectorCachedIndices.ContainsKey(idx))
                        flightStateVectorCachedIndices[idx] = -1;
                    int cached = flightStateVectorCachedIndices[idx];

                    // Loop-mapped sample UT + live-frame epoch shift (effUT == currentUT, shift 0
                    // off the loop path). renderHidden => the member is outside its loop window
                    // this cycle: tear the ghost down and re-defer so the create pass re-materializes
                    // it when the span clock sweeps back into its window.
                    double effUT = ResolveMapPresenceSampleUT(
                        idx, traj.StartUT, traj.EndUT, currentUT, loopUnits,
                        out bool renderHidden, out double loopEpochShiftSeconds);
                    if (renderHidden)
                    {
                        // Enrich the teardown reason with the loop role (descent member => the descent
                        // revert/finish path) so the destroy line correlates with the [ReaimDescent] line.
                        // Empty (byte-identical reason) for every non-descent member.
                        GhostMapPresence.RemoveGhostVesselForRecording(idx,
                            "mission-loop-out-of-window"
                            + GhostPlaybackLogic.DescribeLoopMemberRoleForTeardown(idx, loopUnits));
                        if (toReDefer == null) toReDefer = new List<int>();
                        toReDefer.Add(idx);
                        continue;
                    }

                    if (flightSoiGapStateVectorExpectedBodies.TryGetValue(idx, out string expectedSoiGapBody))
                    {
                        // Loop-aware caller (state-vector orbit update pass): plan section 1.4.
                        bool acceptTerminalOrbitForLoopSynthesis =
                            loopEpochShiftSeconds != 0.0
                            && GhostMapPresence.IsTerminalOrbitSynthesisSafeForLoopMember(traj);
                        TrackingStationGhostSource source = GhostMapPresence.ResolveMapPresenceGhostSource(
                            traj,
                            false,
                            IsMaterializedForMapPresence(traj),
                            effUT,
                            true,
                            "map-presence-soi-gap-state-vector-update",
                            ref cached,
                            out OrbitSegment segment,
                            out TrajectoryPoint soiGapPoint,
                            out _,
                            recordingIndex: idx,
                            allowSoiGapStateVectorFallback: true,
                            expectedSoiGapBody: expectedSoiGapBody,
                            acceptTerminalOrbitForLoopSynthesis: acceptTerminalOrbitForLoopSynthesis,
                            // Keep updating a loop member's ghost alongside any persisted real
                            // terminal vessel (mirrors the create-path bypass).
                            loopMemberInWindow: loopEpochShiftSeconds != 0.0);
                        flightStateVectorCachedIndices[idx] = cached;

                        if (source == TrackingStationGhostSource.StateVectorSoiGap)
                        {
                            if (GhostMapPresence.UpdateGhostOrbitFromStateVectors(
                                idx,
                                traj,
                                soiGapPoint,
                                effUT,
                                allowOrbitalCheckpointStateVector: true,
                                stateVectorUpdateReason: "soi-gap-state-vector-fallback",
                                loopEpochShiftSeconds: loopEpochShiftSeconds))
                            {
                                GhostMapPresence.RemoveGhostVesselForRecording(
                                    idx,
                                    GhostMapPresence.TrackingStationGhostSkipActiveReFlyRelativeUpdate);
                                if (toReDefer == null) toReDefer = new List<int>();
                                toReDefer.Add(idx);
                            }
                            continue;
                        }

                        if (GhostMapPresence.IsSegmentBearingGhostSource(source))
                        {
                            // EndpointTail populates a valid `segment` out-param exactly
                            // like TerminalOrbit (the loop-synthesis no-segment fallback),
                            // so consume it here too; otherwise a terminal-region loop
                            // member resolving to EndpointTail would fall through to the
                            // flat BracketPointAtUT path below. Matches the pending-create
                            // (line ~1387) and orbit-update (line ~1922) callers.
                            GhostMapPresence.UpdateGhostOrbitForRecording(
                                idx, segment,
                                loopEpochShiftSeconds: loopEpochShiftSeconds);
                            if (TryGetMapOrbitKey(source, segment, out var segmentKey))
                            {
                                if (stateVectorSegmentUpdates == null)
                                    stateVectorSegmentUpdates = new List<KeyValuePair<int, (string, double, double)>>();
                                stateVectorSegmentUpdates.Add(new KeyValuePair<int, (string, double, double)>(idx, segmentKey));
                            }

                            if (toExitStateVector == null) toExitStateVector = new List<int>();
                            toExitStateVector.Add(idx);
                            continue;
                        }
                    }

                    TrajectoryPoint? pt = TrajectoryMath.BracketPointAtUT(traj.Points, effUT, ref cached);
                    flightStateVectorCachedIndices[idx] = cached;

                    if (!pt.HasValue) continue;

                    // Relative-frame points reuse `TrajectoryPoint.altitude` as the
                    // anchor-local dz offset (metres along the anchor's local z axis),
                    // not as geographic altitude. Feeding the dz into the
                    // `ShouldRemoveStateVectorOrbit` altitude threshold trips the
                    // remove path on every typical rendezvous frame (dz ~ 0) and
                    // re-defers the ghost — but pending-create currently skips
                    // Relative frames, so the ghost disappears through the section
                    // rather than staying attached to the anchor (#547 P1 review).
                    // Skip the threshold check entirely for Relative-frame points;
                    // `UpdateGhostOrbitFromStateVectors` already dispatches on
                    // `referenceFrame` and resolves the world position via
                    // anchor + offset for that branch.
                    bool inRelativeFrame = GhostMapPresence.IsInRelativeFrame(traj, effUT);
                    if (!inRelativeFrame)
                    {
                        double atmosRemove = GhostMapPresence.GetAtmosphereDepth(pt.Value.bodyName);
                        if (GhostMapPresence.ShouldRemoveStateVectorOrbit(pt.Value.altitude, pt.Value.velocity.magnitude, atmosRemove))
                        {
                            GhostMapPresence.RemoveGhostVesselForRecording(idx, "below-state-vector-threshold");
                            if (toReDefer == null) toReDefer = new List<int>();
                            toReDefer.Add(idx);
                            ParsekLog.Info("Policy", string.Format(CultureInfo.InvariantCulture,
                                "Removed state-vector ghost map vessel for #{0} — alt={1:F0} speed={2:F1} below threshold",
                                idx, pt.Value.altitude, pt.Value.velocity.magnitude));
                            continue;
                        }
                    }

                    if (GhostMapPresence.UpdateGhostOrbitFromStateVectors(idx, traj, pt.Value, effUT,
                        loopEpochShiftSeconds: loopEpochShiftSeconds))
                    {
                        GhostMapPresence.RemoveGhostVesselForRecording(
                            idx,
                            GhostMapPresence.TrackingStationGhostSkipActiveReFlyRelativeUpdate);
                        if (toReDefer == null) toReDefer = new List<int>();
                        toReDefer.Add(idx);
                    }
                }

                if (toReDefer != null)
                {
                    for (int i = 0; i < toReDefer.Count; i++)
                    {
                        int idx = toReDefer[i];
                        if (flightStateVectorOrbitTrajectories.TryGetValue(idx, out var traj))
                            flightPendingMapVessels[idx] = new PendingMapVessel(
                                traj,
                                allowSoiGapStateVectorFallback: false,
                                expectedSoiGapBody: null);
                        flightStateVectorOrbitTrajectories.Remove(idx);
                        flightSoiGapStateVectorExpectedBodies.Remove(idx);
                        // stateVectorCachedIndices[idx] intentionally kept — avoids
                        // O(n) re-scan if the ghost re-ascends above threshold.
                    }
                }

                if (stateVectorSegmentUpdates != null)
                {
                    for (int i = 0; i < stateVectorSegmentUpdates.Count; i++)
                        flightLastMapOrbitByIndex[stateVectorSegmentUpdates[i].Key] = stateVectorSegmentUpdates[i].Value;
                }

                if (toExitStateVector != null)
                {
                    for (int i = 0; i < toExitStateVector.Count; i++)
                    {
                        int idx = toExitStateVector[i];
                        flightStateVectorOrbitTrajectories.Remove(idx);
                        flightSoiGapStateVectorExpectedBodies.Remove(idx);
                    }
                }
            }

            GhostMapPresence.EmitLifecycleSummary("flight-map-presence", currentUT);
        }

        private static void PruneTerminalMapRetentionLogKeys(IReadOnlyList<Recording> committed)
        {
            if (flightTerminalMapRetentionLoggedIds.Count == 0)
                return;

            if (committed == null || committed.Count == 0)
            {
                flightTerminalMapRetentionLoggedIds.Clear();
                return;
            }

            List<string> staleKeys = null;
            foreach (string key in flightTerminalMapRetentionLoggedIds)
            {
                bool found = false;
                for (int i = 0; i < committed.Count; i++)
                {
                    var rec = committed[i];
                    if (rec == null)
                        continue;

                    if (string.Equals(rec.RecordingId, key, StringComparison.Ordinal)
                        || (string.IsNullOrEmpty(rec.RecordingId)
                            && key == i.ToString(CultureInfo.InvariantCulture)))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    if (staleKeys == null) staleKeys = new List<string>();
                    staleKeys.Add(key);
                }
            }

            if (staleKeys == null)
                return;

            for (int i = 0; i < staleKeys.Count; i++)
                flightTerminalMapRetentionLoggedIds.Remove(staleKeys[i]);
        }

        internal static bool TryGetMapOrbitKey(
            TrackingStationGhostSource source,
            OrbitSegment segment,
            out (string body, double sma, double ecc) orbitKey)
        {
            if (source == TrackingStationGhostSource.Segment
                || source == TrackingStationGhostSource.TerminalOrbit)
            {
                orbitKey = (segment.bodyName, segment.semiMajorAxis, segment.eccentricity);
                return true;
            }

            orbitKey = default((string, double, double));
            return false;
        }

        internal static bool IsMaterializedForMapPresence(IPlaybackTrajectory traj)
        {
            var rec = traj as Recording;
            if (rec == null)
                return false;

            bool realVesselExists = false;
            if (rec.VesselPersistentId != 0)
            {
                try
                {
                    realVesselExists = FlightRecorder.FindVesselByPid(rec.VesselPersistentId) != null;
                }
                catch (Exception)
                {
                    realVesselExists = false;
                }
            }

            return GhostMapPresence.IsTrackingStationRecordingMaterialized(rec, realVesselExists);
        }

        /// <summary>
        /// If the recording at the given index belongs to a chain and another segment
        /// from the same chain currently owns a ghost map vessel, remove the old one.
        /// Updates chainMapOwner to track the new owner.
        /// </summary>
        internal static void RemovePreviousChainMapVessel(int newIndex)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || newIndex < 0 || newIndex >= committed.Count) return;

            string chainId = committed[newIndex].ChainId;
            if (string.IsNullOrEmpty(chainId)) return;

            if (flightChainMapOwner.TryGetValue(chainId, out int oldIndex) && oldIndex != newIndex)
            {
                GhostMapPresence.RemoveGhostVesselForRecording(oldIndex, "chain-segment-replaced");
                flightLastMapOrbitByIndex.Remove(oldIndex);
                ParsekLog.Verbose("Policy",
                    $"Chain dedup: removed ghost map for #{oldIndex} (replaced by #{newIndex} in chain {chainId})");
            }

            flightChainMapOwner[chainId] = newIndex;
        }

        // ===================================================================================
        // Flight-scene ghost-create / ghost-destroy MAP-PRESENCE portions (Phase 8d.2 relocation
        // from ParsekPlaybackPolicy.HandleGhostCreated / HandleGhostDestroyed). FAITHFUL MOVE —
        // the blocks below were copied VERBATIM from those two handlers. The only edits are:
        // (a) engine.CurrentLoopUnits -> the loopUnits parameter (HandleFlightGhostCreatedMapPresence),
        // (b) the ShouldDeferLoopShiftedMapPresence predicate (left in ParsekPlaybackPolicy — it has
        // test callers there) is qualified with ParsekPlaybackPolicy., and (c) now-redundant
        // GhostMapPresence. self-qualifiers on members that are in-class here were stripped. The
        // policy keeps the engine-event subscription wiring + the non-presence concerns
        // (TryAutoFollowChainSeamSpawn camera follow, heldGhosts soft-cap state).
        // ===================================================================================

        /// <summary>
        /// Map-presence ENQUEUE portion of <c>ParsekPlaybackPolicy.HandleGhostCreated</c> (Phase 8d.2
        /// relocation): debris-skip gate, terminal-eligibility / orbit-data gates, per-chain dedup,
        /// initial source resolution, the loop-aware defer decision, and the immediate-create vs
        /// pending-queue branch. The policy's handler runs <c>TryAutoFollowChainSeamSpawn(evt)</c>
        /// (camera concern) first, then calls this. <paramref name="loopUnits"/> is the engine's
        /// per-frame <c>CurrentLoopUnits</c> the policy reads at the call site.
        /// </summary>
        internal static void HandleFlightGhostCreatedMapPresence(
            GhostLifecycleEvent evt, GhostPlaybackLogic.LoopUnitSet loopUnits)
        {
            // KEEP debris-only: this is the policy decision to NOT give debris
            // recordings their own tracking-station map presence / orbit lines.
            // Controlled-decoupled children (extension of the parent-anchor
            // contract) carry IsDebris=false and correctly pass this gate so
            // they continue to receive map presence as today.
            if (evt.Trajectory == null || evt.Trajectory.IsDebris)
            {
                if (evt.Trajectory?.IsDebris == true)
                    ParsekLog.VerboseRateLimited("Policy", $"skip-map-debris-{evt.Index}",
                        $"Skipped ghost map for #{evt.Index} \"{evt.Trajectory?.VesselName}\" - debris",
                        3.0);
                return;
            }

            var terminal = evt.Trajectory.TerminalStateValue;
            if (!IsTerminalStateEligibleForMapPresence(terminal))
            {
                ParsekLog.VerboseRateLimited("Policy", $"skip-map-terminal-{evt.Index}",
                    $"Skipped ghost map for #{evt.Index} \"{evt.Trajectory.VesselName}\" — terminal={terminal.Value}");
                return;
            }

            // Accept recordings with terminal orbit data, orbit segments, or trajectory
            // points (physics-only suborbital recordings have points but no orbit data).
            bool hasOrbitData = HasOrbitData(evt.Trajectory);
            bool hasOrbitSegments = evt.Trajectory.HasOrbitSegments;
            bool hasPoints = evt.Trajectory.Points != null && evt.Trajectory.Points.Count > 0;
            if (!hasOrbitData && !hasOrbitSegments && !hasPoints)
                return;

            // Slice (i): overlap recordings (looped, period < duration) under the director-drive gate
            // are owned solely by the per-frame per-instance sweep (RunOverlapPerInstanceSweep) which
            // creates ONE map vessel per live cycle. Do NOT enqueue/create a single per-index ghost
            // for them here. Off the gate (or for non-overlap recordings) this is byte-identical to
            // before: the per-instance path is unreachable and the legacy enqueue runs.
            {
                var committedForOverlap = RecordingStore.CommittedRecordings;
                if (committedForOverlap != null && evt.Index >= 0 && evt.Index < committedForOverlap.Count
                    && ShouldDriveOverlapPerInstance(committedForOverlap[evt.Index], evt.Index, committedForOverlap, loopUnits))
                {
                    ParsekLog.VerboseRateLimited("Policy", $"defer-overlap-sweep-{evt.Index}",
                        string.Format(CultureInfo.InvariantCulture,
                            "Ghost map for #{0} \"{1}\" deferred to per-instance overlap sweep " +
                            "(looped overlap recording / Mission-unit overlap member, director-drive on)",
                            evt.Index, evt.Trajectory.VesselName ?? "(null)"),
                        3.0);
                    return;
                }
            }

            // Per-chain dedup: if another segment from the same chain already has a ghost
            // map vessel, remove it before creating the new one. Prevents duplicate orbit
            // lines during fast time warp across chain boundaries.
            RemovePreviousChainMapVessel(evt.Index);

            // Check whether the ghost starts in a map-visible source. Otherwise,
            // defer — the per-frame shared resolver will create it when it enters
            // an orbital segment or state-vector fallback range.
            double startUT = evt.Trajectory.StartUT;
            int cachedStateVectorIndex = flightStateVectorCachedIndices.TryGetValue(evt.Index, out int cached)
                ? cached
                : -1;
            // Initial-create-on-first-loop-entry: pass acceptTerminalOrbitForLoopSynthesis:
            // false. This path runs at startUT with no loopUnits / effUT / shift in scope, so
            // the relaxed terminal-orbit source would seed at the raw recorded UT (wrong
            // position). The proto-vessel is created later on a loop-aware tick once the
            // pending queue and per-frame ResolveMapPresenceSampleUT compute effUT and the
            // accompanying loop epoch shift. Same reasoning as the TS-startup wrapper. Plan section 1.4.
            TrackingStationGhostSource source = ResolveMapPresenceGhostSource(
                evt.Trajectory,
                false,
                IsMaterializedForMapPresence(evt.Trajectory),
                startUT,
                true,
                "map-presence-initial-create",
                ref cachedStateVectorIndex,
                out OrbitSegment segment,
                out TrajectoryPoint stateVectorPoint,
                out _,
                recordingIndex: evt.Index,
                acceptTerminalOrbitForLoopSynthesis: false);
            flightStateVectorCachedIndices[evt.Index] = cachedStateVectorIndex;

            // Loop-shifted members must be created on a loop-aware per-frame tick
            // (CheckPendingMapVessels): that path resolves the source at the loop-mapped
            // effUT and threads the live-frame epoch shift into the orbit + arc-clip bounds.
            // Creating here at the raw recorded startUT with the default shift=0 seeds
            // recorded-UT bounds and leaves ghostOrbitLoopShiftedPids clear, so for one tick
            // the map-orbit window resolver returns a recorded-UT fallback window (the
            // icon-clamp / orbit-line glitch at first appearance and at every window re-entry).
            // Defer so the per-frame path owns loop-member creation, mirroring the TS create
            // sites and the flight pending-create path. Off the loop path effUT == currentUT
            // (shift 0, renderHidden false), so non-loop members keep the immediate create
            // byte-for-byte.
            double initialNowUT = Planetarium.GetUniversalTime();
            ResolveMapPresenceSampleUT(
                evt.Index, evt.Trajectory.StartUT, evt.Trajectory.EndUT, initialNowUT,
                loopUnits, out bool initialRenderHidden, out double initialLoopShift);
            bool deferForLoopAware =
                ParsekPlaybackPolicy.ShouldDeferLoopShiftedMapPresence(initialLoopShift, initialRenderHidden);

            if (source != TrackingStationGhostSource.None && !deferForLoopAware)
            {
                Vessel ghost = CreateGhostVesselFromSource(
                    evt.Index,
                    evt.Trajectory,
                    source,
                    segment,
                    stateVectorPoint,
                    startUT);
                if (ghost != null)
                {
                    if (IsStateVectorGhostSource(source))
                    {
                        flightStateVectorOrbitTrajectories[evt.Index] = evt.Trajectory;
                        if (source != TrackingStationGhostSource.StateVectorSoiGap)
                            flightSoiGapStateVectorExpectedBodies.Remove(evt.Index);
                    }
                    else if (TryGetMapOrbitKey(source, segment, out var orbitKey))
                    {
                        flightSoiGapStateVectorExpectedBodies.Remove(evt.Index);
                        flightLastMapOrbitByIndex[evt.Index] = orbitKey;
                    }
                }
            }
            else
            {
                // Initial/pre-orbital deferrals and loop-shifted members are not SOI-gap
                // recoveries; only the gap-between-orbit-segments requeue path opts into that
                // fallback. The per-frame CheckPendingMapVessels pass resolves the source at
                // the loop-mapped effUT and creates with the live-frame epoch shift.
                flightPendingMapVessels[evt.Index] = new PendingMapVessel(
                    evt.Trajectory,
                    allowSoiGapStateVectorFallback: false,
                    expectedSoiGapBody: null);
                ParsekLog.VerboseRateLimited("Policy", $"defer-map-{evt.Index}",
                    string.Format(CultureInfo.InvariantCulture,
                        "Deferred ghost map vessel for #{0} \"{1}\": {2} (source={3} loopShift={4:F2} renderHidden={5})",
                        evt.Index,
                        evt.Trajectory.VesselName,
                        deferForLoopAware
                            ? "loop-shifted member, deferring to loop-aware per-frame create"
                            : "recording starts pre-orbital",
                        source,
                        initialLoopShift,
                        initialRenderHidden),
                    3.0);
            }
        }

        /// <summary>
        /// Map-presence TEARDOWN portion of <c>ParsekPlaybackPolicy.HandleGhostDestroyed</c> (Phase
        /// 8d.2 relocation): clears the five per-index presence dicts, resolves the committed
        /// vesselPid (and drops the terminal-retention log key), then removes both recording-index
        /// and chain-based ghost map ProtoVessels. The policy's handler keeps the Verbose log and the
        /// <c>heldGhosts.Remove</c> (soft-cap state) and calls this with <c>evt.Index</c>.
        /// </summary>
        internal static void HandleFlightGhostDestroyedMapPresence(int index)
        {
            flightPendingMapVessels.Remove(index);
            flightLastMapOrbitByIndex.Remove(index);
            flightStateVectorOrbitTrajectories.Remove(index);
            flightSoiGapStateVectorExpectedBodies.Remove(index);
            flightStateVectorCachedIndices.Remove(index);

            // Remove both recording-index and chain-based ghost map ProtoVessels
            var committed = RecordingStore.CommittedRecordings;
            uint vesselPid = 0;
            if (committed != null && index >= 0 && index < committed.Count)
            {
                vesselPid = committed[index].VesselPersistentId;
                if (!string.IsNullOrEmpty(committed[index].RecordingId))
                    flightTerminalMapRetentionLoggedIds.Remove(committed[index].RecordingId);
            }

            RemoveAllGhostPresenceForIndex(index, vesselPid, "ghost-destroyed");
        }
    }
}
