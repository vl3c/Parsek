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
        // GUI-P1: the per-recording playback tick box in the Recordings tab is OFF for this
        // recording, so it has no presence on any render surface (world ghost, map icon,
        // orbit line, Tracking Station row, non-proto marker, trajectory polyline). Distinct
        // from "suppressed" (chain / supersede / re-fly visibility, which the player does not
        // drive) so a log reader can tell a player choice from a derived state.
        internal const string TrackingStationGhostSkipPlaybackDisabled = "playback-disabled";
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

        /// <summary>
        /// PID tracking set — the canonical ghost vessel identification.
        /// Every guard in the codebase checks this for O(1) exclusion.
        /// </summary>
        internal static readonly HashSet<uint> ghostMapVesselPids = new HashSet<uint>();

        /// <summary>
        /// How many Parsek ghost ProtoVessel <c>Die()</c> calls are on the stack right now.
        /// Nested because <see cref="RemoveAllGhostVessels"/> wraps a whole loop while the
        /// single-ghost removers wrap one call each.
        ///
        /// WHY IT EXISTS: <c>Vessel.Die()</c> fires stock <c>onVesselDestroy</c> SYNCHRONOUSLY,
        /// and <c>SpaceTracking</c> handles that event by rebuilding its vessel list. When that
        /// rebuild throws, <see cref="Parsek.Patches.GhostTrackingBuildVesselsListPatch"/> has to
        /// decide whether the failure is attributable to our ghosts — and its live
        /// <c>FlightGlobals.Vessels</c> scan can itself throw during teardown, leaving the
        /// classifier with zeroed counts and no evidence. This flag is the scan-INDEPENDENT
        /// attribution signal: it is set only while OUR teardown is running.
        /// </summary>
        private static int ghostTeardownDepth;

        /// <summary>Enter a Parsek ghost-teardown scope. Pair with <see cref="EndGhostTeardown"/>
        /// in a <c>finally</c> — never let an exception leave the depth raised.</summary>
        internal static void BeginGhostTeardown()
        {
            ghostTeardownDepth++;
        }

        /// <summary>Leave a Parsek ghost-teardown scope. Floors at zero so an unbalanced
        /// decrement can never latch the flag inverted for the rest of the session.</summary>
        internal static void EndGhostTeardown()
        {
            if (ghostTeardownDepth > 0)
                ghostTeardownDepth--;
        }

        /// <summary>True while a Parsek ghost ProtoVessel <c>Die()</c> is on the stack.</summary>
        internal static bool IsGhostTeardownInProgress
        {
            get { return ghostTeardownDepth > 0; }
        }

        /// <summary>
        /// How many <see cref="RemoveAllGhostVessels"/> Die loops are on the stack. Narrower
        /// than <see cref="ghostTeardownDepth"/>: the single-ghost removers raise that one
        /// while the OTHER ghosts stay alive and may still need an orbit-renderer repair,
        /// whereas inside a remove-all every tracked ghost is about to be destroyed.
        /// SCOPE: this guards only SYNCHRONOUS re-entry while the loop runs. Stock
        /// <c>Vessel.Die()</c> fires <c>onVesselWillDestroy</c> and then <c>Object.Destroy</c>,
        /// which Unity defers to the end of the frame, so <c>onVesselDestroy</c> (the event
        /// SpaceTracking rebuilds on) fires from <c>Vessel.OnDestroy</c> AFTER this counter
        /// has dropped back to zero. It does not cover that path.
        /// </summary>
        private static int removeAllGhostVesselsDepth;

        /// <summary>True while <see cref="RemoveAllGhostVessels"/> is destroying every ghost.</summary>
        internal static bool IsRemoveAllGhostVesselsInProgress
        {
            get { return removeAllGhostVesselsDepth > 0; }
        }

        /// <summary>Live count of registered ghost map ProtoVessels. Read by the
        /// buildVesselsList finalizer as evidence that survives a failed context scan.</summary>
        internal static int RegisteredGhostMapVesselCount
        {
            get { return ghostMapVesselPids.Count; }
        }

        /// <summary>
        /// Ghost ProtoVessels whose native icon is currently suppressed by
        /// GhostOrbitLinePatch (below atmosphere). DrawMapMarkers checks this
        /// to draw our custom icon at the ghost mesh position instead.
        /// </summary>
        internal static readonly HashSet<uint> ghostsWithSuppressedIcon = new HashSet<uint>();

        // Phase 5a: the per-ghost orbit-line grace map (ghostOrbitLineGraceUntilFrame /
        // StampOrbitLineGrace / GetOrbitLineGraceUntilFrame) was DELETED with the legacy
        // line-visibility cascade in GhostOrbitLinePatch (its only consumer). Locked by
        // GhostOrbitLineCascadeDeleteGateTests.

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

        /// <summary>
        /// PAINTED set (V1-REPLAY-LINE-BLINK, RecordingId domain): every recording for which the polyline
        /// Driver's decide walk committed SOME trajectory line this frame - the current-element leg (the
        /// <see cref="IsPolylineOwningGhostPhase"/> case) OR a forward run leg OR a forward arc.
        /// Deliberately BROADER than the ownership set: ownership answers "is the HEAD inside a drawn
        /// CURRENT leg", which is the right question for hiding the proto conic and is unchanged, while
        /// this answers "did the map have a line for this ghost at all". The two differ exactly in the
        /// inter-leg gaps, where the run legs + arcs stay painted but the head sits between legs.
        ///
        /// <para>WHAT THIS SIGNAL DOES NOT PROMISE, stated precisely because a maintainer will otherwise
        /// assume it. (1) It is DECIDE-TIME, not paint-time: the publishes fire where the Driver enqueues
        /// a leg / arc for the <c>onPreCull</c> pass, and that pass can still decline one (a past run leg
        /// whose <c>requireConicAnchor</c> fails, a cache miss, or a frame on which the map camera never
        /// issues <c>onPreCull</c>). The pre-existing ownership publish has the IDENTICAL property - it is
        /// published from <c>anyDrawn</c> in the same decide walk - so this set is no weaker than the
        /// contract it borrows from, but neither is "actual draw" in the strict sense. The true paint set
        /// (<c>currentDrewForwardArcRecordings</c>) is populated in <c>onPreCull</c>, which runs AFTER the
        /// probe's <c>LateUpdate</c>, so it is unreadable same-frame; that ordering, not oversight, is why
        /// it is not used. (2) Each publish is keyed on the MEMBER the leg / arc belongs to, so a chain
        /// run credits every member it actually drew for - but a recording is a coarse key, so the
        /// boundary-overlap instances of one recording share it and cannot be told apart here.</para>
        ///
        /// <para>Populated by <see cref="NotePaintedRecordingLine"/>, cleared by
        /// <see cref="ClearFrameCoverageSets"/> on the same pre-early-return lifecycle as the other frame
        /// sets. Read ONLY by the map-render probe's line-blink guard through
        /// <see cref="IsPolylinePaintingGhostTrajectory"/>; nothing in the live render path consults it,
        /// and the Driver populates it only while tracing is on, so it is observability-only.</para>
        /// </summary>
        private static readonly HashSet<string> paintedRecordingIdsThisFrame =
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

        /// <summary>Test seam: marks a pid as a ghost map vessel (cleared by ResetForTesting).</summary>
        internal static void RegisterGhostMapVesselPidForTesting(uint persistentId)
        {
            ghostMapVesselPids.Add(persistentId);
        }

        /// <summary>
        /// Display-name prefix stamped onto every Parsek-authored ghost map
        /// ProtoVessel. Presentation only - <see cref="IsGhostMapVessel"/> is the
        /// canonical identity check.
        /// <para>
        /// Do NOT build a ConfigNode-level "is this a serialized ghost" guard on
        /// this prefix. Ghosts never reach a save: <c>ParsekScenario.OnSave</c>
        /// calls <see cref="StripFromSave"/> on every save and KSP writes the
        /// SCENARIO nodes before FLIGHTSTATE, so a name-keyed guard could only
        /// ever match a player craft genuinely named "Ghost: ...".
        /// </para>
        /// </summary>
        internal const string GhostVesselNamePrefix = "Ghost: ";

        /// <summary>The map / Tracking Station name of a ghost proto for a recording's display name.</summary>
        internal static string BuildGhostProtoVesselName(string displayName)
            => GhostVesselNamePrefix + (displayName ?? "Unknown");

        /// <summary>
        /// The new name for a ghost proto currently named <paramref name="currentName"/>
        /// whose recording now displays <paramref name="displayName"/>, or null when nothing
        /// changes. Pure.
        /// </summary>
        internal static string ResolveGhostProtoRename(string currentName, string displayName)
        {
            string desired = BuildGhostProtoVesselName(displayName);
            return string.Equals(currentName, desired, StringComparison.Ordinal) ? null : desired;
        }

        // DisplayNames.Version the ghost protos were last renamed against.
        private static int ghostNamesAppliedVersion = int.MinValue;

        /// <summary>
        /// In-place rename of every map / Tracking Station ghost proto (design-vessel-and-mission-names.md
        /// 7.8) when <see cref="DisplayNames.Version"/> moved: no event, no rebuild (a rebuild
        /// trips line-blink / icon-jump and drops targets). Stock's list and labels read the
        /// name live. The recording is resolved by id at apply time (the pid -> recording id
        /// reverse map, else the index key of this frame); nothing index-keyed is cached.
        /// </summary>
        internal static void RefreshGhostProtoNamesIfDisplayNamesMoved(string scene)
        {
            int version = DisplayNames.Version;
            if (version == ghostNamesAppliedVersion) return;
            ghostNamesAppliedVersion = version;
            if (vesselsByRecordingIndex.Count == 0 && overlapInstanceVessels.Count == 0
                && vesselsByChainPid.Count == 0)
                return;

            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            var byId = new Dictionary<string, Recording>(StringComparer.Ordinal);
            for (int i = 0; i < committed.Count; i++)
                if (committed[i] != null && !string.IsNullOrEmpty(committed[i].RecordingId))
                    byId[committed[i].RecordingId] = committed[i];

            int scanned = 0, renamed = 0, unresolved = 0;
            foreach (KeyValuePair<int, Vessel> kv in vesselsByRecordingIndex)
                RenameGhostProtoInPlace(kv.Value, kv.Key, committed, byId, ref scanned, ref renamed, ref unresolved);
            foreach (KeyValuePair<(int recIdx, long cycle), Vessel> kv in overlapInstanceVessels)
                RenameGhostProtoInPlace(kv.Value, kv.Key.recIdx, committed, byId, ref scanned, ref renamed, ref unresolved);
            foreach (KeyValuePair<uint, Vessel> kv in vesselsByChainPid)
                RenameGhostProtoInPlace(kv.Value, -1, committed, byId, ref scanned, ref renamed, ref unresolved);

            ParsekLog.Verbose("VesselNames",
                string.Format(CultureInfo.InvariantCulture,
                    "Ghost proto rename ({0}): scanned={1} renamed={2} unresolved={3} displayVersion={4}",
                    scene ?? "?", scanned, renamed, unresolved, version));
        }

        private static void RenameGhostProtoInPlace(
            Vessel vessel, int indexKey, IReadOnlyList<Recording> committed,
            Dictionary<string, Recording> byId, ref int scanned, ref int renamed, ref int unresolved)
        {
            if (vessel == null) return;
            scanned++;
            Recording rec = null;
            if (vesselPidToRecordingId.TryGetValue(vessel.persistentId, out string recId) && recId != null)
                byId.TryGetValue(recId, out rec);
            if (rec == null && indexKey >= 0 && indexKey < committed.Count)
                rec = committed[indexKey];
            if (rec == null)
            {
                unresolved++;
                return;
            }
            string next = ResolveGhostProtoRename(vessel.vesselName, rec.VesselName);
            if (next == null) return;
            vessel.vesselName = next;
            if (vessel.protoVessel != null) vessel.protoVessel.vesselName = next;
            renamed++;
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
        /// true the orbit-line Postfix's first branch has set the proto <c>drawIcons=NONE</c>. Phase 5b:
        /// this disjunct AND the orbit-line Postfix both read the single intent-sourced selector
        /// <see cref="Parsek.MapRender.ShadowRenderDriver.IsTracedPathOwnedThisFrame"/> (the legacy
        /// side-channel was deleted), so the marker and the proto-icon suppression can never disagree.
        /// This pure core just ORs the three resolved booleans.</para>
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
        /// (<c>ParsekFlightMapMarkers.DrawMapMarkers</c> flight-map, <c>ParsekTrackingStation
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
        ///
        /// <para><b>Phase 4b origin (migration plan section 6.6b), collapsed at Phase 5b:</b> the
        /// <paramref name="directorTracedPathActive"/> disjunct - the ONE disjunct of this decision the
        /// Director's <c>GhostRenderIntent</c> actually decides - is resolved through
        /// <see cref="Parsek.MapRender.ShadowRenderDriver.IsTracedPathOwnedThisFrame"/>, which since the
        /// Phase-5b delete of the legacy side-channel reads the single intent-sourced stamp (the design section 8
        /// "scene.Apply(intent)" sourcing). The marker call sites (flight-map + TS, which route through
        /// this), the polyline Driver's owned-draw routing, and the proto icon/line suppress patches in
        /// <c>GhostOrbitLinePatch</c> all read that ONE selector: no double-marker, no gap.</para>
        ///
        /// <para><b>KEPT, NOT moved (Phase 5):</b> the <paramref name="polylineOwning"/> (actual-draw,
        /// downstream of the intent) and <paramref name="iconSuppressed"/>
        /// (<c>ghostsWithSuppressedIcon</c> / <see cref="IsIconSuppressed"/>) disjuncts have NO intent
        /// equivalent today (the director emits only None/StockConic/TracedPath; there is no
        /// <c>SuppressedMarker</c> treatment value, so the spine does not decide the no-conic floor case),
        /// so they stay on their legacy sources. The icon floor is the ONLY marker signal for
        /// below-atmosphere / off-arc / no-bounds ghosts and is a KEPT permanent fallback - re-sourcing
        /// it would need the director to learn the SuppressedMarker treatment (out of Phase-4 scope).</para>
        /// </summary>
        internal static bool ShouldDrawNonProtoMarkerForGhost(
            uint ghostPid,
            out bool directorTracedPathActive,
            out bool polylineOwning,
            out bool iconSuppressed)
        {
            // The TracedPath disjunct reads the single intent-sourced selector (Phase 5b collapsed the
            // flag-aware selection; the legacy side-channel is gone). Same selector the polyline Driver
            // routes on, so the marker decision never desyncs from the owned draw.
            directorTracedPathActive = Parsek.MapRender.ShadowRenderDriver.IsTracedPathOwnedThisFrame(
                ghostPid, UnityEngine.Time.frameCount);
            polylineOwning = IsPolylineOwningGhostPhase(ghostPid);
            iconSuppressed = IsIconSuppressed(ghostPid);
            return ResolveMarkerDrawDecision(
                directorTracedPathActive,
                polylineOwning,
                iconSuppressed);
        }

        private static double GetCurrentUTSafe()
        {
            return Planetarium.GetUniversalTime();
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
        /// Cached tracking-station-suppressed recording IDs from the last lifecycle tick.
        /// Reused by ParsekTrackingStation.OnGUI to avoid recomputing.
        /// </summary>
        internal static HashSet<string> CachedTrackingStationSuppressedIds { get; private set; }
            = new HashSet<string>();

        // Memo for the LIVE chain-tip walk: the walker scans the committed list and the tree
        // topology, and the resolver runs per recording per frame.
        //
        // COMPOSITE KEY, mirroring the ERS / retired-set caches in EffectiveState. The memoized
        // value is EffectiveState.EffectiveTipRecordingId, which depends on BOTH the committed
        // list (RecordingStore.StateVersion) and the supersede table
        // (ParsekScenario.SupersedeStateVersion). SupersedeCommit.AppendRelations mutates
        // RecordingSupersedes and bumps ONLY the supersede version, so a StateVersion-only key
        // would serve a pre-supersede tip for the rest of the session.
        private static int chainTipSegmentsCacheVersion = -1;
        private static int chainTipSegmentsCacheSupersedeVersion = -1;
        private static readonly Dictionary<string, string> chainTipSegmentsCache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Test seam replacing the Tracking Station hand-off's spawn call
        /// (rec, index, preserveIdentity); the real call needs a live KSP scene.
        /// </summary>
        internal static Action<Recording, int, bool> TrackingStationSpawnOverrideForTesting;

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

        internal const string EnsureOrbitRenderersSkipApplicationQuitting = "application-quitting";
        internal const string EnsureOrbitRenderersSkipRemoveAllInProgress = "remove-all-ghosts-in-progress";

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
        /// Refresh the pid -> recordingId reverse map when the recording
        /// DRIVING a live ghost changes (chain-tip advance, index reuse).
        /// The map was write-once-at-create, which froze the label on the
        /// FIRST recording that bound the pid - the V2 lane measured a looped
        /// ghost's probe lines attributed to a scene-entry stub's recId. The
        /// probe and the polyline-ownership checks all read this map, so a
        /// stale entry mislabels Tier-B/C truth AND coverage bookkeeping.
        /// </summary>
        internal static void RebindGhostRecordingId(uint vesselPid, string recordingId)
        {
            if (vesselPid == 0u || string.IsNullOrEmpty(recordingId))
                return;
            if (vesselPidToRecordingId.TryGetValue(vesselPid, out string existing)
                && string.Equals(existing, recordingId, StringComparison.Ordinal))
                return;
            vesselPidToRecordingId[vesselPid] = recordingId;
            ParsekLog.Verbose(Tag, string.Format(ic,
                "ghost recId rebind: pid={0} {1} -> {2}",
                vesselPid, existing ?? "(none)", recordingId));
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
        /// Reset all state for testing (avoids Debug.Log crash outside Unity).
        /// </summary>
        internal static void ResetForTesting()
        {
            CurrentUTNow = GetCurrentUTSafe;
            ghostNamesAppliedVersion = int.MinValue;
            TrackingStationSpawnOverrideForTesting = null;
            FindBodyByNameForTesting = null;
            OrbitSeedResolver.ResetForTesting();
            ghostTeardownDepth = 0;
            removeAllGhostVesselsDepth = 0;
            ghostMapVesselPids.Clear();
            ghostsWithSuppressedIcon.Clear();
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
            lastOverlapSweepUT = double.NaN;
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
        /// What the in-game test runner's between-run cleanup must destroy before
        /// <see cref="ResetBetweenTestRuns"/> clears the ghost-map bookkeeping. Built by the
        /// pure <see cref="PlanBetweenRunGhostVesselRemoval"/>.
        /// </summary>
        internal struct BetweenRunGhostVesselRemovalPlan
        {
            /// <summary>Ghost vessels held by the chain / recording-index / overlap maps;
            /// destroyed through <see cref="RemoveAllGhostVessels"/>.</summary>
            internal int TrackedVesselCount;

            /// <summary>Live, non-dead vessels whose pid is a registered ghost pid but which no
            /// tracking map holds (in live-list order, distinct). Destroyed one by one while
            /// still registered, so every vessel-event guard keeps reading them as ghosts.</summary>
            internal List<uint> UntrackedLivePids;

            /// <summary>Registered ghost pids with no live vessel: bookkeeping only, cleared by
            /// <see cref="ResetBetweenTestRuns"/>.</summary>
            internal int StaleRegisteredPidCount;

            internal bool RunTrackedRemoval => TrackedVesselCount > 0;

            internal int UntrackedLiveCount => UntrackedLivePids?.Count ?? 0;

            internal bool AnyRemoval => RunTrackedRemoval || UntrackedLiveCount > 0;
        }

        /// <summary>
        /// PURE, Unity-free. Decides which ghost map vessels the between-run cleanup must
        /// destroy so that no ghost ProtoVessel outlives its registration. Without this the
        /// Tracking Station (no ParsekFlight, so nothing else destroys them) kept every ghost
        /// in FlightGlobals.Vessels after the bookkeeping clear: they then read as REAL vessels
        /// to every IsGhostMapVessel guard (stock vessel budget, StripFromSave) and the scene
        /// built a second set beside them (todo INGAME-BATCH-TS-ORPHANS-GHOST-MAP-VESSELS).
        /// In FLIGHT, ParsekFlight.DestroyAllTimelineGhosts has already emptied every map and
        /// the registered set, so the plan is empty there.
        /// </summary>
        /// <param name="trackedVesselCount">Entries across the chain, recording-index and
        /// overlap-instance maps.</param>
        /// <param name="trackedVesselPids">The pids of those entries.</param>
        /// <param name="liveVesselPids">Pids of the live, non-dead vessels in
        /// FlightGlobals.Vessels, in list order.</param>
        /// <param name="registeredGhostPids">The registered ghost pid set.</param>
        internal static BetweenRunGhostVesselRemovalPlan PlanBetweenRunGhostVesselRemoval(
            int trackedVesselCount,
            ICollection<uint> trackedVesselPids,
            IReadOnlyList<uint> liveVesselPids,
            ICollection<uint> registeredGhostPids)
        {
            var plan = new BetweenRunGhostVesselRemovalPlan
            {
                TrackedVesselCount = trackedVesselCount > 0 ? trackedVesselCount : 0,
                UntrackedLivePids = new List<uint>(),
                StaleRegisteredPidCount = 0,
            };

            if (registeredGhostPids == null || registeredGhostPids.Count == 0)
                return plan;

            var live = new HashSet<uint>();
            if (liveVesselPids != null)
            {
                for (int i = 0; i < liveVesselPids.Count; i++)
                {
                    uint pid = liveVesselPids[i];
                    if (pid == 0 || !live.Add(pid))
                        continue;
                    if (!registeredGhostPids.Contains(pid))
                        continue;
                    if (trackedVesselPids != null && trackedVesselPids.Contains(pid))
                        continue;
                    plan.UntrackedLivePids.Add(pid);
                }
            }

            foreach (uint pid in registeredGhostPids)
            {
                if (!live.Contains(pid))
                    plan.StaleRegisteredPidCount++;
            }

            return plan;
        }

        /// <summary>
        /// PURE summary line for <see cref="RemoveRegisteredGhostVesselsBeforeTestReset"/>.
        /// </summary>
        internal static string FormatBetweenRunGhostVesselRemoval(
            string reason,
            string scene,
            BetweenRunGhostVesselRemovalPlan plan,
            int untrackedRemoved,
            int liveVesselsBefore,
            int liveVesselsAfter)
        {
            return string.Format(ic,
                "Between-run ghost vessel removal: reason={0} scene={1} tracked={2} " +
                "untrackedLive={3} untrackedRemoved={4} staleRegisteredPids={5} " +
                "liveVesselsBefore={6} liveVesselsAfter={7}",
                reason ?? "(null)",
                scene ?? "(null)",
                plan.TrackedVesselCount,
                plan.UntrackedLiveCount,
                untrackedRemoved,
                plan.StaleRegisteredPidCount,
                liveVesselsBefore,
                liveVesselsAfter);
        }

        /// <summary>
        /// The in-game test runner's between-run step that runs BEFORE
        /// <see cref="ResetBetweenTestRuns"/>: destroys every ghost map vessel that is still
        /// registered, through the same <c>Vessel.Die()</c> removal the scene teardowns use
        /// (<see cref="RemoveAllGhostVessels"/> for the mapped ones, the single-ghost teardown
        /// scope for any vessel registered by pid alone). The scene's own lifecycle then
        /// rebuilds from a clean slate (the Tracking Station's next lifecycle tick creates the
        /// ghosts again). <c>Vessel.Die()</c> on a non-active vessel removes it from
        /// FlightGlobals.Vessels synchronously and marks it DEAD, so a save the runner writes
        /// in the same frame no longer counts it. Untracked vessels die first, while still
        /// registered, because the remove-all clears the registered set. The live list is
        /// copied before any Die() so no removal mutates a list being iterated.
        /// Returns the number of vessels destroyed.
        /// </summary>
        internal static int RemoveRegisteredGhostVesselsBeforeTestReset(string reason)
        {
            List<Vessel> liveVessels = FlightGlobals.Vessels;
            var liveSnapshot = new List<Vessel>();
            var livePids = new List<uint>();
            if (liveVessels != null)
            {
                for (int i = 0; i < liveVessels.Count; i++)
                {
                    Vessel v = liveVessels[i];
                    if (v == null || v.state == Vessel.State.DEAD)
                        continue;
                    liveSnapshot.Add(v);
                    livePids.Add(v.persistentId);
                }
            }

            int trackedCount = vesselsByChainPid.Count + vesselsByRecordingIndex.Count
                + overlapInstanceVessels.Count;
            var trackedPids = new HashSet<uint>();
            foreach (Vessel v in vesselsByChainPid.Values)
                if (v != null) trackedPids.Add(v.persistentId);
            foreach (Vessel v in vesselsByRecordingIndex.Values)
                if (v != null) trackedPids.Add(v.persistentId);
            foreach (Vessel v in overlapInstanceVessels.Values)
                if (v != null) trackedPids.Add(v.persistentId);

            BetweenRunGhostVesselRemovalPlan plan = PlanBetweenRunGhostVesselRemoval(
                trackedCount, trackedPids, livePids, ghostMapVesselPids);
            string scene = HighLogic.LoadedScene.ToString();

            if (!plan.AnyRemoval)
            {
                ParsekLog.Verbose(Tag,
                    FormatBetweenRunGhostVesselRemoval(
                        reason, scene, plan, 0, liveSnapshot.Count, liveSnapshot.Count)
                    + " (no registered ghost vessel is live)");
                return 0;
            }

            int untrackedRemoved = 0;
            if (plan.UntrackedLiveCount > 0)
            {
                var untracked = new HashSet<uint>(plan.UntrackedLivePids);
                var toKill = new List<Vessel>(plan.UntrackedLiveCount);
                for (int i = 0; i < liveSnapshot.Count; i++)
                {
                    if (untracked.Remove(liveSnapshot[i].persistentId))
                        toKill.Add(liveSnapshot[i]);
                }

                BeginGhostTeardown();
                try
                {
                    for (int i = 0; i < toKill.Count; i++)
                    {
                        try
                        {
                            toKill[i].Die();
                            untrackedRemoved++;
                        }
                        catch (Exception ex)
                        {
                            ParsekLog.Warn(Tag,
                                string.Format(ic,
                                    "Between-run ghost vessel removal: Die() threw for untracked pid={0}: {1}",
                                    toKill[i].persistentId, ex.Message));
                        }
                    }
                }
                finally
                {
                    EndGhostTeardown();
                }

                for (int i = 0; i < toKill.Count; i++)
                    ghostMapVesselPids.Remove(toKill[i].persistentId);
            }

            if (plan.RunTrackedRemoval)
                RemoveAllGhostVessels(reason);

            int liveAfter = 0;
            List<Vessel> after = FlightGlobals.Vessels;
            if (after != null)
            {
                for (int i = 0; i < after.Count; i++)
                {
                    if (after[i] != null && after[i].state != Vessel.State.DEAD)
                        liveAfter++;
                }
            }

            ParsekLog.Info(Tag,
                FormatBetweenRunGhostVesselRemoval(
                    reason, scene, plan, untrackedRemoved, liveSnapshot.Count, liveAfter));
            return plan.TrackedVesselCount + untrackedRemoved;
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
        /// are the caller's responsibility via RemoveAllGhostVessels or engine cleanup; the
        /// runner calls RemoveRegisteredGhostVesselsBeforeTestReset first, so a scene with no
        /// ParsekFlight (the Tracking Station) never keeps a ghost vessel past this clear.
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
            lastOverlapSweepUT = double.NaN;
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
        // The clock the flight map lifecycle last ran at, for the 1x clock-jump reseed
        // (ParsekPlaybackPolicy.IsMapClockDiscontinuity). NaN until the first pass.
        private static double lastFlightMapLifecycleUT = double.NaN;

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
        /// Map-presence TEARDOWN portion of <c>ParsekPlaybackPolicy.HandleGhostDestroyed</c> (Phase
        /// 8d.2 relocation): clears the five per-index presence dicts, resolves the committed
        /// vesselPid (and drops the terminal-retention log key), then removes both recording-index
        /// and chain-based ghost map ProtoVessels. The policy's handler keeps the Verbose log and the
        /// <c>heldGhosts.Remove</c> (soft-cap state) and calls this with <c>evt.Index</c>.
        /// </summary>
        /// <summary>
        /// The committed recording at <paramref name="index"/> is about to leave the list
        /// (list still unshifted). A recording whose ghost already completed can still own a
        /// retained map ProtoVessel and per-index presence entries; the engine's DestroyGhost
        /// never fires OnGhostDestroyed for a slot without ghost state, so tear those down
        /// here or the recording that shifts into the slot inherits them.
        /// </summary>
        internal static void HandleCommittedRecordingRemoving(int index)
        {
            bool hadProto = vesselsByRecordingIndex.ContainsKey(index);
            bool hadFlightEntries = flightPendingMapVessels.ContainsKey(index)
                || flightLastMapOrbitByIndex.ContainsKey(index)
                || flightStateVectorOrbitTrajectories.ContainsKey(index)
                || flightSoiGapStateVectorExpectedBodies.ContainsKey(index)
                || flightStateVectorCachedIndices.ContainsKey(index);
            if (!hadProto && !hadFlightEntries) return;

            ParsekLog.Verbose(Tag,
                $"Committed recording #{index} removing with retained map presence " +
                $"(proto={hadProto} flightEntries={hadFlightEntries}) - tearing down");
            // Chain-pid presence is shared with a surviving merge target, so only the
            // recording-index proto goes; pid 0 skips the chain branch.
            HandleFlightGhostDestroyedMapPresenceCore(index, vesselPid: 0, "committed-removed");
        }

        /// <summary>
        /// Shifts every index-keyed map-presence store after the committed list lost the
        /// entry at <paramref name="removedIndex"/> (entries at that index were torn down by
        /// <see cref="HandleCommittedRecordingRemoving"/> / the ghost destroy; any leftover
        /// is dropped here).
        /// </summary>
        internal static void ReindexPresenceAfterDelete(int removedIndex)
        {
            ShiftIndexKeyedPresence(removedIndex, insert: false);
        }

        /// <summary>Insert mirror of <see cref="ReindexPresenceAfterDelete"/>.</summary>
        internal static void ReindexPresenceAfterInsert(int insertedIndex)
        {
            ShiftIndexKeyedPresence(insertedIndex, insert: true);
        }

        // Single list of every recording-index-keyed presence store, so delete and insert
        // cannot drift apart. vesselPidToRecordingIndex (pid -> index) and flightChainMapOwner
        // (chainId -> index) carry indices as VALUES and are rebuilt from the shifted stores.
        private static void ShiftIndexKeyedPresence(int pivot, bool insert)
        {
            int protosBefore = vesselsByRecordingIndex.Count;
            if (insert)
            {
                IndexShift.DictAfterInsert(vesselsByRecordingIndex, pivot);
                IndexShift.DictAfterInsert(lastKnownByRecordingIndex, pivot);
                IndexShift.DictAfterInsert(activeReFlyDeferredStateVectorGhostSessions, pivot);
                IndexShift.DictAfterInsert(trackingStationStateVectorOrbitTrajectories, pivot);
                IndexShift.DictAfterInsert(trackingStationStateVectorCachedIndices, pivot);
                IndexShift.DictAfterInsert(flightPendingMapVessels, pivot);
                IndexShift.DictAfterInsert(flightSoiGapStateVectorExpectedBodies, pivot);
                IndexShift.DictAfterInsert(flightLastMapOrbitByIndex, pivot);
                IndexShift.DictAfterInsert(flightStateVectorOrbitTrajectories, pivot);
                IndexShift.DictAfterInsert(flightStateVectorCachedIndices, pivot);
            }
            else
            {
                IndexShift.DictAfterDelete(vesselsByRecordingIndex, pivot);
                IndexShift.DictAfterDelete(lastKnownByRecordingIndex, pivot);
                IndexShift.DictAfterDelete(activeReFlyDeferredStateVectorGhostSessions, pivot);
                IndexShift.DictAfterDelete(trackingStationStateVectorOrbitTrajectories, pivot);
                IndexShift.DictAfterDelete(trackingStationStateVectorCachedIndices, pivot);
                IndexShift.DictAfterDelete(flightPendingMapVessels, pivot);
                IndexShift.DictAfterDelete(flightSoiGapStateVectorExpectedBodies, pivot);
                IndexShift.DictAfterDelete(flightLastMapOrbitByIndex, pivot);
                IndexShift.DictAfterDelete(flightStateVectorOrbitTrajectories, pivot);
                IndexShift.DictAfterDelete(flightStateVectorCachedIndices, pivot);
            }

            // Overlap instances key on (recIdx, cycle); rebuild the tuple keys.
            if (overlapInstanceVessels.Count > 0)
            {
                var shifted = new Dictionary<(int recIdx, long cycle), Vessel>(overlapInstanceVessels.Count);
                foreach (var kv in overlapInstanceVessels)
                {
                    int recIdx = kv.Key.recIdx;
                    if (insert)
                        recIdx = IndexShift.AfterInsert(recIdx, pivot);
                    else if (recIdx == pivot)
                        continue;
                    else
                        recIdx = IndexShift.AfterDelete(recIdx, pivot);
                    shifted[(recIdx, kv.Key.cycle)] = kv.Value;
                }
                overlapInstanceVessels.Clear();
                foreach (var kv in shifted) overlapInstanceVessels[kv.Key] = kv.Value;
            }

            vesselPidToRecordingIndex.Clear();
            foreach (var kv in vesselsByRecordingIndex)
            {
                if (kv.Value != null)
                    vesselPidToRecordingIndex[kv.Value.persistentId] = kv.Key;
            }

            if (flightChainMapOwner.Count > 0)
            {
                var owners = new List<string>(flightChainMapOwner.Keys);
                foreach (string chainId in owners)
                {
                    int owner = flightChainMapOwner[chainId];
                    if (insert)
                        flightChainMapOwner[chainId] = IndexShift.AfterInsert(owner, pivot);
                    else if (owner == pivot)
                        flightChainMapOwner.Remove(chainId);
                    else
                        flightChainMapOwner[chainId] = IndexShift.AfterDelete(owner, pivot);
                }
            }

            ParsekLog.Verbose(Tag,
                $"Map presence reindexed after committed {(insert ? "insert" : "removal")} at #{pivot}: " +
                $"protos {protosBefore} -> {vesselsByRecordingIndex.Count}, " +
                $"overlapInstances={overlapInstanceVessels.Count}, chainOwners={flightChainMapOwner.Count}");
        }
    }
}
