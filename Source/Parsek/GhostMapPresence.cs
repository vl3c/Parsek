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
    internal static class GhostMapPresence
    {
        internal enum TrackingStationGhostSource
        {
            None = 0,
            Segment = 1,
            TerminalOrbit = 2,
            StateVector = 3,
            StateVectorSoiGap = 4
        }

        internal static bool IsStateVectorGhostSource(TrackingStationGhostSource source)
        {
            return source == TrackingStationGhostSource.StateVector
                || source == TrackingStationGhostSource.StateVectorSoiGap;
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
        private static ReFlySuppressionSearchTreeCache cachedReFlySuppressionSearchTrees;

        private sealed class ReFlySuppressionSearchTreeCache
        {
            internal readonly IReadOnlyList<RecordingTree> CommittedTrees;
            internal readonly RecordingTree PendingTree;
            internal readonly RecordingTree[] CommittedSnapshot;
            internal readonly IReadOnlyList<RecordingTree> ComposedTrees;

            internal ReFlySuppressionSearchTreeCache(
                IReadOnlyList<RecordingTree> committedTrees,
                RecordingTree pendingTree,
                RecordingTree[] committedSnapshot,
                IReadOnlyList<RecordingTree> composedTrees)
            {
                CommittedTrees = committedTrees;
                PendingTree = pendingTree;
                CommittedSnapshot = committedSnapshot;
                ComposedTrees = composedTrees;
            }
        }

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

        internal struct GhostMapVisibilityCounters
        {
            public int uniqueTracked;
            public int recordingTracked;
            public int chainTracked;
            public int mapObjectMissing;
            public int orbitRendererMissing;
            public int orbitRendererDisabled;
            public int drawIconsNotAll;
            public int iconSuppressed;
        }

        /// <summary>
        /// Decision-line field bag for the structured GhostMap log lines.
        /// All numeric fields default to NaN; the builder omits NaN-valued slots.
        /// </summary>
        internal struct GhostMapDecisionFields
        {
            public string Action;          // create / position / update / destroy / source-resolve
            public string RecordingId;
            public int RecordingIndex;
            public string VesselName;
            public string Source;          // Segment / TerminalOrbit / StateVector / None / Chain
            public string Branch;          // Absolute / Relative / OrbitalCheckpoint / no-section / (n/a)
            public string Body;
            public Vector3d? WorldPos;
            public uint GhostPid;          // 0 if unknown
            public uint AnchorPid;         // 0 if not Relative
            public Vector3d? AnchorPos;
            public Vector3d? LocalOffset;  // anchor-local offset (Relative branch)
            public OrbitSegment? Segment;  // populated when source=Segment
            public string TerminalBody;
            public double TerminalSma;     // NaN if unknown
            public double TerminalEcc;     // NaN if unknown
            public double StateVecAlt;     // NaN if unknown
            public double StateVecSpeed;   // NaN if unknown
            public string Reason;          // why this decision / which fallback / skip-reason
            public double UT;              // NaN if unknown
        }

        /// <summary>
        /// Create a <see cref="GhostMapDecisionFields"/> with NaN sentinels in
        /// every numeric slot so the builder can detect "unset". C# 7 structs
        /// default to 0.0 which would make every `terminalSma=0` look like a
        /// real reading; the helper avoids that ambiguity.
        /// </summary>
        internal static GhostMapDecisionFields NewDecisionFields(string action)
        {
            return new GhostMapDecisionFields
            {
                Action = action,
                TerminalSma = double.NaN,
                TerminalEcc = double.NaN,
                StateVecAlt = double.NaN,
                StateVecSpeed = double.NaN,
                UT = double.NaN
            };
        }

        /// <summary>
        /// Read the current world position of a recording-index ghost (after a
        /// successful Vessel.Load). Returns false if no ghost is bound or the
        /// vessel was destroyed mid-frame.
        /// </summary>
        internal static bool TryGetGhostWorldPosForRecording(int recordingIndex, out Vector3d worldPos)
        {
            if (vesselsByRecordingIndex.TryGetValue(recordingIndex, out Vessel vessel)
                && vessel != null)
            {
                worldPos = vessel.GetWorldPos3D();
                return true;
            }
            worldPos = default(Vector3d);
            return false;
        }

        /// <summary>
        /// Emit the per-tick lifecycle summary line and reset the counters.
        /// Called from the two map-presence drivers
        /// (<see cref="UpdateTrackingStationGhostLifecycle"/> and
        /// <c>ParsekPlaybackPolicy.CheckPendingMapVessels</c>) so the post-hoc
        /// reader sees one summary per tick without spam.
        /// </summary>
        internal static void EmitLifecycleSummary(string scope, double currentUT)
        {
            GhostMapVisibilityCounters visibility = CollectMapVisibilityCounters();
            ParsekLog.VerboseRateLimited(
                Tag,
                "gm-lifecycle-summary",
                BuildLifecycleSummaryMessage(
                    scope,
                    visibility,
                    lifecycleCreatedThisTick,
                    lifecycleDestroyedThisTick,
                    lifecycleUpdatedThisTick,
                    currentUT,
                    GetCurrentSceneName()),
                5.0);
            lifecycleCreatedThisTick = 0;
            lifecycleDestroyedThisTick = 0;
            lifecycleUpdatedThisTick = 0;
        }

        internal static string BuildLifecycleSummaryMessage(
            string scope,
            GhostMapVisibilityCounters visibility,
            int created,
            int destroyed,
            int updated,
            double currentUT,
            string scene)
        {
            return string.Format(ic,
                "lifecycle-summary: scope={0} vesselsTracked={1} recordingTracked={2} chainTracked={3} " +
                "created={4} destroyed={5} updated={6} currentUT={7:F1} scene={8} " +
                "mapVisibility[mapObjMissing={9} orbitRendererMissing={10} orbitRendererDisabled={11} " +
                "drawIconsNotAll={12} iconSuppressed={13}]",
                scope ?? "(unspecified)",
                visibility.uniqueTracked,
                visibility.recordingTracked,
                visibility.chainTracked,
                created,
                destroyed,
                updated,
                currentUT,
                scene ?? "n/a",
                visibility.mapObjectMissing,
                visibility.orbitRendererMissing,
                visibility.orbitRendererDisabled,
                visibility.drawIconsNotAll,
                visibility.iconSuppressed);
        }

        internal static string BuildGhostProtoVesselVisibilityState(
            bool hasMapObject,
            bool hasOrbitRenderer,
            bool orbitRendererEnabled,
            string drawIcons,
            bool nativeIconSuppressed,
            bool rendererForceEnabled)
        {
            string visibilityReason;
            if (!hasMapObject)
                visibilityReason = "map-object-missing";
            else if (!hasOrbitRenderer)
                visibilityReason = "orbit-renderer-missing";
            else if (!orbitRendererEnabled)
                visibilityReason = "orbit-renderer-disabled";
            else if (rendererForceEnabled)
                visibilityReason = "renderer-force-enabled";
            else if (!string.Equals(drawIcons, OrbitRendererBase.DrawIcons.ALL.ToString(), StringComparison.Ordinal))
                visibilityReason = "draw-icons-not-all";
            else if (nativeIconSuppressed)
                visibilityReason = "native-icon-suppressed";
            else
                visibilityReason = "visible";

            return string.Format(ic,
                "mapObj={0} orbitRenderer={1} rendererEnabled={2} drawIcons={3} nativeIconSuppressed={4} " +
                "rendererForceEnabled={5} visibilityReason={6}",
                hasMapObject,
                hasOrbitRenderer,
                orbitRendererEnabled,
                string.IsNullOrEmpty(drawIcons) ? "(none)" : drawIcons,
                nativeIconSuppressed,
                rendererForceEnabled,
                visibilityReason);
        }

        private static GhostMapVisibilityCounters CollectMapVisibilityCounters()
        {
            var counters = new GhostMapVisibilityCounters
            {
                recordingTracked = vesselsByRecordingIndex.Count,
                chainTracked = vesselsByChainPid.Count
            };
            var seenPids = new HashSet<uint>();

            foreach (Vessel vessel in vesselsByRecordingIndex.Values)
                CountMapVisibility(vessel, ref counters, seenPids);
            foreach (Vessel vessel in vesselsByChainPid.Values)
                CountMapVisibility(vessel, ref counters, seenPids);

            return counters;
        }

        private static void CountMapVisibility(
            Vessel vessel,
            ref GhostMapVisibilityCounters counters,
            HashSet<uint> seenPids)
        {
            if (vessel == null)
                return;

            uint pid = vessel.persistentId;
            if (pid != 0 && !seenPids.Add(pid))
                return;

            counters.uniqueTracked++;
            if (vessel.mapObject == null)
                counters.mapObjectMissing++;
            if (vessel.orbitRenderer == null)
            {
                counters.orbitRendererMissing++;
            }
            else
            {
                if (!vessel.orbitRenderer.enabled)
                    counters.orbitRendererDisabled++;
                if (vessel.orbitRenderer.drawIcons != OrbitRendererBase.DrawIcons.ALL)
                    counters.drawIconsNotAll++;
            }
            if (pid != 0 && ghostsWithSuppressedIcon.Contains(pid))
                counters.iconSuppressed++;
        }

        private static string GetCurrentSceneName()
        {
            try
            {
                return HighLogic.LoadedScene.ToString();
            }
            catch
            {
                // HighLogic may be unavailable in xUnit
                return "n/a";
            }
        }

        private static string FormatVec3d(Vector3d v)
        {
            return string.Format(ic, "({0:F1},{1:F1},{2:F1})", v.x, v.y, v.z);
        }

        /// <summary>
        /// Build the canonical structured GhostMap decision line. One line per
        /// create / position / update / destroy / source-resolve event. Producers
        /// fill <see cref="GhostMapDecisionFields"/> and pass it here; the builder
        /// formats every populated slot and omits the rest.
        /// </summary>
        internal static string BuildGhostMapDecisionLine(GhostMapDecisionFields f)
        {
            var sb = new StringBuilder();
            sb.Append(string.IsNullOrEmpty(f.Action) ? "decision" : f.Action);
            sb.Append(": rec=");
            sb.Append(string.IsNullOrEmpty(f.RecordingId) ? "(null)" : f.RecordingId);
            sb.Append(" idx=").Append(f.RecordingIndex.ToString(ic));
            sb.Append(" vessel=\"");
            sb.Append(f.VesselName ?? "(null)");
            sb.Append('"');
            sb.Append(" source=").Append(string.IsNullOrEmpty(f.Source) ? "None" : f.Source);
            sb.Append(" branch=").Append(string.IsNullOrEmpty(f.Branch) ? "(n/a)" : f.Branch);
            sb.Append(" body=").Append(string.IsNullOrEmpty(f.Body) ? "(none)" : f.Body);

            if (f.WorldPos.HasValue)
                sb.Append(" worldPos=").Append(FormatVec3d(f.WorldPos.Value));

            if (f.GhostPid != 0)
                sb.Append(" ghostPid=").Append(f.GhostPid.ToString(ic));

            if (f.Segment.HasValue)
            {
                var seg = f.Segment.Value;
                sb.Append(" segmentBody=").Append(seg.bodyName ?? "(null)");
                sb.AppendFormat(ic,
                    " segmentUT={0:F1}-{1:F1} sma={2:F0} ecc={3:F4} inc={4:F4} mna={5:F4} epoch={6:F1}",
                    seg.startUT, seg.endUT,
                    seg.semiMajorAxis, seg.eccentricity, seg.inclination,
                    seg.meanAnomalyAtEpoch, seg.epoch);
            }

            if (!string.IsNullOrEmpty(f.TerminalBody))
                sb.Append(" terminalOrbitBody=").Append(f.TerminalBody);
            if (!double.IsNaN(f.TerminalSma))
                sb.AppendFormat(ic, " terminalSma={0:F0}", f.TerminalSma);
            if (!double.IsNaN(f.TerminalEcc))
                sb.AppendFormat(ic, " terminalEcc={0:F4}", f.TerminalEcc);

            if (!double.IsNaN(f.StateVecAlt))
                sb.AppendFormat(ic, " stateVecAlt={0:F0}", f.StateVecAlt);
            if (!double.IsNaN(f.StateVecSpeed))
                sb.AppendFormat(ic, " stateVecSpeed={0:F1}", f.StateVecSpeed);

            if (f.AnchorPid != 0)
                sb.Append(" anchorPid=").Append(f.AnchorPid.ToString(ic));
            if (f.AnchorPos.HasValue)
                sb.Append(" anchorPos=").Append(FormatVec3d(f.AnchorPos.Value));
            if (f.LocalOffset.HasValue)
                sb.Append(" localOffset=").Append(FormatVec3d(f.LocalOffset.Value));

            if (!double.IsNaN(f.UT))
                sb.AppendFormat(ic, " ut={0:F1}", f.UT);

            sb.Append(" scene=").Append(GetCurrentSceneName());

            if (!string.IsNullOrEmpty(f.Reason))
                sb.Append(" reason=").Append(f.Reason);

            return sb.ToString();
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

        internal const string TrackingStationGhostSkipSuppressed = "suppressed";
        internal const string TrackingStationGhostSkipAlreadySpawned = "already-spawned";
        internal const string TrackingStationGhostSkipEndpointConflict = "endpoint-conflict";
        internal const string TrackingStationGhostSkipUnseedableTerminalOrbit = "terminal-orbit-unseedable";
        internal const string TrackingStationGhostSkipStateVectorThreshold = "state-vector-threshold";
        internal const string TrackingStationGhostSkipRelativeFrame = "relative-frame";
        // #583: Relative-frame state-vector ghost CREATION reaches the resolver
        // when the first map-visible UT lies inside a Relative section. We allow
        // creation through the existing StateVector source kind iff the section's
        // anchor vessel is resolvable in the scene (CreateGhostVesselFromStateVectors
        // already dispatches on referenceFrame and resolves world position via the
        // anchor in the Relative branch — PR #547). When the anchor is not yet
        // resolvable, defer with this dedicated skip reason so the pending-create
        // queue retries on the next tick. Distinct from `relative-frame` (the
        // legacy "always defer" reason kept for sections without an anchor id —
        // those have no resolvable anchor by construction and are unreachable
        // from the new path).
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

        // #583 test seam: production looks the anchor up via
        // FlightRecorder.FindVesselByPid (which short-circuits to null when
        // FlightGlobals.Vessels is unavailable, e.g. xUnit). Tests that need
        // to exercise the "anchor resolvable in scene" branch override this
        // delegate; ResetForTesting clears it back to null.
        internal static Func<uint, bool> AnchorResolvableForTesting = null;

        internal struct GhostProtoOrbitSeedDiagnostics
        {
            public string Source;
            public string EndpointBodyName;
            public string FailureReason;
            public string FallbackReason;
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
                        "created={3} alreadyTracked={4} sources(visibleSegment={5} terminalOrbit={6} stateVector={7}) " +
                        "skipped={8} skipCounts={9}",
                        context,
                        action ?? "(null)",
                        recordingCount,
                        created,
                        alreadyTracked,
                        segmentCount,
                        terminalCount,
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

        /// <summary>
        /// Orbit segment time bounds per ghost vessel PID. Used by GhostOrbitArcPatch
        /// to clip the orbit line to only the visible arc (between segment startUT and endUT).
        /// Only populated for segment-based ghosts — terminal-orbit ghosts render the full ellipse.
        /// </summary>
        internal static readonly Dictionary<uint, (double startUT, double endUT)> ghostOrbitBounds
            = new Dictionary<uint, (double, double)>();

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
        /// <c>"absolute-shadow"</c> both suppress, because both describe a
        /// RELATIVE track section (the latter is the v7 absolute-shadow
        /// sibling of the same section, used when the live anchor is the
        /// active Re-Fly target). Suppressing only <c>"relative"</c> would
        /// leak a parent-chain v7 state-vector ghost into the scene during
        /// active Re-Fly, contradicting the doubled-ProtoVessel guard
        /// (PR #613 review P2).</param>
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

            // Placeholder pattern (provisional != origin): the live vessel
            // in scene is the player's pre-rewind vessel, NOT a fresh
            // restoration. Mirror the carve-out in
            // RewindInvoker.ResolveInPlaceContinuationDebrisToKill — only
            // in-place continuations (origin == active) trigger the
            // doubled-vessel placement.
            if (!string.Equals(
                    marker.ActiveReFlyRecordingId,
                    marker.OriginChildRecordingId,
                    StringComparison.Ordinal))
            {
                suppressReason = "not-suppressed-placeholder-pattern";
                return false;
            }

            // Accept both "relative" and "absolute-shadow" — the latter is
            // the v7 sibling of the same RELATIVE section, returned by
            // ResolveStateVectorWorldPosition when the section's anchor PID
            // matches the active Re-Fly target. The suppression decision
            // depends on the section's underlying RELATIVE shape, not on
            // which positioning source the resolver picked. Without this
            // both-branches check the parent-chain doubled-ProtoVessel
            // guard would silently break for v7 recordings and let a
            // wrong-position ghost ProtoVessel into the scene during
            // active Re-Fly (PR #613 review P2).
            bool branchSuppresses =
                string.Equals(resolutionBranch, "relative", StringComparison.Ordinal)
                || string.Equals(resolutionBranch, "absolute-shadow", StringComparison.Ordinal);
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
                for (int t = 0; t < committedTrees.Count && activeReFlyPid == 0u; t++)
                {
                    RecordingTree tree = committedTrees[t];
                    if (tree == null || tree.Recordings == null) continue;
                    if (tree.Recordings.TryGetValue(marker.ActiveReFlyRecordingId, out Recording rec)
                        && rec != null
                        && rec.VesselPersistentId != 0u)
                    {
                        activeReFlyPid = rec.VesselPersistentId;
                        activePidSource = "search-tree:" + (tree.Id ?? "<no-id>");
                    }
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
            suppressReason = "lookahead-no-track-sections";

            List<TrackSection> sections = traj?.TrackSections;
            if (sections == null || sections.Count == 0)
                return false;

            int candidates = 0;
            int skippedPast = 0;
            int skippedNoAnchor = 0;
            string firstReject = null;
            string lastReject = null;
            bool currentUtUsable = !double.IsNaN(currentUT) && !double.IsInfinity(currentUT);

            for (int i = 0; i < sections.Count; i++)
            {
                TrackSection section = sections[i];
                if (section.referenceFrame != ReferenceFrame.Relative)
                    continue;

                if (section.anchorVesselId == 0u)
                {
                    skippedNoAnchor++;
                    continue;
                }

                if (currentUtUsable
                    && !double.IsNaN(section.endUT)
                    && !double.IsInfinity(section.endUT)
                    && section.endUT < currentUT)
                {
                    skippedPast++;
                    continue;
                }

                candidates++;
                if (ShouldSuppressStateVectorProtoVesselForActiveReFly(
                        marker,
                        "relative",
                        section.anchorVesselId,
                        victimRecordingId,
                        committedRecordings,
                        committedTrees,
                        out string candidateReason))
                {
                    suppressReason = string.Format(ic,
                        "{0} sectionIndex={1} sectionUT={2:F1}-{3:F1} " +
                        "sectionAnchorPid={4} currentUT={5:F1} reason=({6})",
                        TrackingStationGhostSkipActiveReFlyRelativeLookahead,
                        i,
                        section.startUT,
                        section.endUT,
                        section.anchorVesselId,
                        currentUT,
                        candidateReason ?? "(none)");
                    return true;
                }

                if (firstReject == null)
                    firstReject = candidateReason;
                lastReject = candidateReason;
            }

            suppressReason = string.Format(ic,
                "lookahead-no-active-refly-relative-anchor candidates={0} " +
                "skippedPast={1} skippedNoAnchor={2} firstReject=({3}) lastReject=({4})",
                candidates,
                skippedPast,
                skippedNoAnchor,
                firstReject ?? "(none)",
                lastReject ?? "(none)");
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
            RecordingTree tree = null;
            Recording active = null;
            int treesSearched = 0;
            for (int i = 0; i < searchTrees.Count; i++)
            {
                RecordingTree t = searchTrees[i];
                if (t?.Recordings == null) continue;
                treesSearched++;
                if (t.Recordings.TryGetValue(activeRecordingId, out Recording rec)
                    && rec != null)
                {
                    tree = t;
                    active = rec;
                    break;
                }
            }
            if (tree == null || active == null)
            {
                walkTrace = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "active-not-found activeId={0} treesSearched={1}",
                    activeRecordingId, treesSearched);
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

        /// <summary>
        /// #611: composes the search-tree list for the Re-Fly suppression
        /// predicate. Production callers pass <see cref="RecordingStore.CommittedTrees"/>
        /// and (when present) <see cref="RecordingStore.PendingTree"/>; tests
        /// pass an explicit list. Pure-static so unit tests can construct
        /// arbitrary topologies without touching <c>RecordingStore</c>.
        /// <para>
        /// The list MUST include the pending tree because at Re-Fly load
        /// time <see cref="ParsekScenario.TryRestoreActiveTreeNode"/> calls
        /// <see cref="RecordingStore.RemoveCommittedTreeById"/> after the
        /// splice runs, leaving the freshly-loaded tree only as PendingTree.
        /// A predicate that searched only committed trees would silently
        /// fail the active-recording lookup during the load window — which
        /// is exactly when the doubled-vessel ProtoVessel get created (#611).
        /// </para>
        /// <para>
        /// The pending tree is appended (not prepended) so the existing
        /// tie-break behaviour for in-memory committedTrees still wins on
        /// id collisions; the BFS walk's first-match-wins loop then
        /// terminates at the same tree it would have found pre-#611 in the
        /// steady state, and falls through to the pending tree only when
        /// committed-trees lookup misses — matching the diagnosed bug
        /// shape.
        /// </para>
        /// </summary>
        internal static IReadOnlyList<RecordingTree> ComposeSearchTreesForReFlySuppression(
            IReadOnlyList<RecordingTree> committedTrees,
            RecordingTree pendingTree)
        {
            int committedCount = committedTrees?.Count ?? 0;
            bool hasPending = pendingTree != null;
            if (!hasPending)
            {
                ClearCachedReFlySuppressionSearchTrees();
                return committedTrees ?? Array.Empty<RecordingTree>();
            }

            if (TryGetCachedReFlySuppressionSearchTrees(
                    committedTrees,
                    committedCount,
                    pendingTree,
                    out IReadOnlyList<RecordingTree> cached))
            {
                return cached;
            }

            var result = new List<RecordingTree>(committedCount + 1);
            var snapshot = new RecordingTree[committedCount];
            for (int i = 0; i < committedCount; i++)
            {
                RecordingTree t = committedTrees[i];
                snapshot[i] = t;
                if (t == null) continue;
                if (string.Equals(t.Id, pendingTree.Id, StringComparison.Ordinal))
                {
                    // Same tree id in both — drop the committed entry and
                    // keep only the pending copy. At the moment both are
                    // present (load-time transient), pending carries the
                    // post-splice + post-refresh shape that the predicate
                    // needs to walk; committed is the pre-load snapshot.
                    // Skipping committed here avoids double-walk +
                    // visited-set churn; the pending append below makes
                    // the tree visible to the search.
                    continue;
                }
                result.Add(t);
            }
            result.Add(pendingTree);
            cachedReFlySuppressionSearchTrees = new ReFlySuppressionSearchTreeCache(
                committedTrees,
                pendingTree,
                snapshot,
                result);
            return result;
        }

        private static void ClearCachedReFlySuppressionSearchTrees()
        {
            cachedReFlySuppressionSearchTrees = null;
        }

        private static bool TryGetCachedReFlySuppressionSearchTrees(
            IReadOnlyList<RecordingTree> committedTrees,
            int committedCount,
            RecordingTree pendingTree,
            out IReadOnlyList<RecordingTree> cached)
        {
            cached = null;
            ReFlySuppressionSearchTreeCache cache = cachedReFlySuppressionSearchTrees;
            if (cache == null
                || cache.ComposedTrees == null
                || !ReferenceEquals(cache.CommittedTrees, committedTrees)
                || !ReferenceEquals(cache.PendingTree, pendingTree)
                || cache.CommittedSnapshot == null
                || cache.CommittedSnapshot.Length != committedCount)
            {
                return false;
            }

            // RecordingStore keeps the list instance stable while mutating its
            // contents in a few load/merge paths. Validate the source refs so
            // the cache removes hot-path allocations without serving stale tree
            // entries after same-count replacement.
            for (int i = 0; i < committedCount; i++)
            {
                if (!ReferenceEquals(cache.CommittedSnapshot[i], committedTrees[i]))
                    return false;
            }

            cached = cache.ComposedTrees;
            return true;
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
            ghostOrbitBounds.Remove(ghostPid);
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
                PlanetariumCamera.fetch.SetTarget(spawned.mapObject);
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Tracking-station handoff restored map focus from ghostPid={0} to spawnedPid={1} reason={2}",
                        handoffState.GhostPid,
                        spawnedPid,
                        reason));
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
                    setVesselMethod.Invoke(trackingInstance, new[] { vesselSelection });
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

        internal static bool IsTrackingStationRecordingAlreadyMaterialized(Recording rec)
        {
            if (rec == null)
                return false;

            bool realVesselExists = false;
            if (rec.VesselPersistentId != 0)
            {
                try
                {
                    realVesselExists = GhostPlaybackLogic.RealVesselExists(rec.VesselPersistentId);
                }
                catch (Exception)
                {
                    realVesselExists = false;
                }
            }

            return ShouldSkipTrackingStationDuplicateSpawn(rec, realVesselExists);
        }

        private static MethodInfo FindTrackingStationSetVesselMethod(
            Type trackingType,
            Type selectionType)
        {
            if (trackingType == null || selectionType == null)
                return null;

            MethodInfo[] methods = trackingType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method == null || method.Name != "SetVessel")
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1)
                    continue;

                if (parameters[0].ParameterType.IsAssignableFrom(selectionType))
                    return method;
            }

            return null;
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
            if (chainCount == 0 && indexCount == 0)
            {
                ParsekLog.VerboseRateLimited(Tag,
                    "remove-all-empty|" + (reason ?? "(none)"),
                    string.Format(ic,
                        "RemoveAllGhostVessels: no ghost vessels to remove (reason={0})",
                        reason),
                    30.0);
                return;
            }

            // Collect all vessels to destroy (chain + recording index)
            var vessels = new List<Vessel>(chainCount + indexCount);
            vessels.AddRange(vesselsByChainPid.Values);
            vessels.AddRange(vesselsByRecordingIndex.Values);

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
            ghostOrbitBounds.Clear();
            vesselsByChainPid.Clear();
            vesselsByRecordingIndex.Clear();
            vesselPidToRecordingIndex.Clear();
            vesselPidToRecordingId.Clear();
            trackingStationStateVectorOrbitTrajectories.Clear();
            trackingStationStateVectorCachedIndices.Clear();
            activeReFlyDeferredStateVectorGhostSessions.Clear();
            lastKnownByRecordingIndex.Clear();
            lastKnownByChainPid.Clear();

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Removed all {0} ghost vessel(s) reason={1} (chain={2} index={3})",
                    chainCount + indexCount, reason, chainCount, indexCount));
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
            int recordingIndex, IPlaybackTrajectory traj, OrbitSegment segment)
        {
            if (traj == null) return null;

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
                intent.Source = "Segment";
                intent.Branch = "(n/a)";
                intent.Body = segment.bodyName;
                intent.Segment = segment;
                ParsekLog.Verbose(Tag, BuildGhostMapDecisionLine(intent));
            }

            string logContext = string.Format(ic, "recording index={0} (from segment)", recordingIndex);
            Vessel vessel = BuildAndLoadGhostProtoVessel(traj, segment, logContext);
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
                done.Source = "Segment";
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
                    Source = "Segment",
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
        /// Remove a ghost map ProtoVessel for a timeline playback ghost.
        /// Called when the engine destroys a ghost (OnGhostDestroyed).
        /// </summary>
        internal static void RemoveGhostVesselForRecording(int recordingIndex, string reason)
        {
            if (!vesselsByRecordingIndex.TryGetValue(recordingIndex, out Vessel vessel))
                return;

            uint ghostPid = vessel.persistentId;

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
            ghostOrbitBounds.Remove(ghostPid);
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
            if (source == TrackingStationGhostSource.Segment)
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
            string expectedSoiGapBody = null)
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

            if (alreadyMaterialized)
            {
                skipReason = TrackingStationGhostSkipAlreadySpawned;
                return ReturnDecision(TrackingStationGhostSource.None, skipReason, "already materialized");
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

                    return ReturnDecision(
                        TrackingStationGhostSource.Segment,
                        skipReason,
                        CombineSourceDetails(string.Format(ic,
                            "segmentBody={0} segmentUT={1:F1}-{2:F1}",
                            segment.bodyName ?? "(null)",
                            segment.startUT,
                            segment.endUT),
                            checkpointFallbackDetail));
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
            // rendezvous section — CreateGhostVesselFromStateVectors's Relative
            // branch (PR #547) handles the world-position resolution against
            // the anchor vessel.
            bool considerStateVector =
                !traj.HasOrbitSegments
                || IsInRelativeFrame(traj, currentUT);
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
                        traj, considerStateVector, stateVectorSkipReason);
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    CombineSourceDetails(
                        string.Format(ic, "terminalFallback=False hasOrbitSegments={0}", traj.HasOrbitSegments),
                        checkpointFallbackDetail));
            }

            if (!HasOrbitData(traj))
            {
                skipReason = checkpointFallbackRejectReason
                    ?? ResolveStateVectorOrSegmentSkipReason(
                        traj, considerStateVector, stateVectorSkipReason);
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    CombineSourceDetails(
                        string.Format(ic, "hasOrbitSegments={0}", traj.HasOrbitSegments),
                        checkpointFallbackDetail));
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

            double activationStartUT = PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(traj);
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
                skipReason = checkpointFallbackRejectReason ?? "before-terminal-orbit";
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    CombineSourceDetails(
                        string.Format(ic, "endUT={0:F1}", traj.EndUT),
                        checkpointFallbackDetail));
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
                skipReason = seedDiagnostics.FailureReason == TrackingStationGhostSkipEndpointConflict
                    ? TrackingStationGhostSkipEndpointConflict
                    : TrackingStationGhostSkipUnseedableTerminalOrbit;
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    CombineSourceDetails(string.Format(ic,
                        "terminalBody={0} endUT={1:F1} seedFailure={2} endpointBody={3}",
                        traj.TerminalOrbitBody ?? "(null)",
                        traj.EndUT,
                        seedDiagnostics.FailureReason ?? "(none)",
                        seedDiagnostics.EndpointBodyName ?? "(none)"),
                        checkpointFallbackDetail));
            }

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
                    "terminalBody={0} endUT={1:F1} seedBody={2} seedSource={3} endpointBody={4} seedFallback={5}",
                    traj.TerminalOrbitBody ?? "(null)",
                    traj.EndUT,
                    seedBodyName ?? "(null)",
                    seedDiagnostics.Source ?? "(none)",
                    seedDiagnostics.EndpointBodyName ?? "(none)",
                    seedDiagnostics.FallbackReason ?? "(none)"),
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
                    return WithStructured(string.Format(ic,
                        "segmentBody={0} segmentUT={1:F1}-{2:F1}",
                        segment.bodyName ?? "(null)",
                        segment.startUT,
                        segment.endUT));

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
                        return WithStructured(string.Format(ic,
                            "terminalBody={0} endUT={1:F1} seedBody={2} seedSource={3} endpointBody={4} seedFallback={5}",
                            rec.TerminalOrbitBody ?? "(null)",
                            rec.EndUT,
                            seedBodyName ?? "(null)",
                            seedDiagnostics.Source ?? "(none)",
                            seedDiagnostics.EndpointBodyName ?? "(none)",
                            seedDiagnostics.FallbackReason ?? "(none)"));
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
                    return WithStructured("already materialized");
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
                    return WithStructured(string.Format(ic,
                        "terminalBody={0} endUT={1:F1} seedFailure={2} endpointBody={3}",
                        rec.TerminalOrbitBody ?? "(null)",
                        rec.EndUT,
                        seedDiagnostics.FailureReason ?? "(none)",
                        seedDiagnostics.EndpointBodyName ?? "(none)"));
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
        // relative-anchor-unresolved / state-vector-threshold — surface that
        // reason. Otherwise fall back to the legacy split between
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
                ResolveAnchorInScene,
                out point,
                out skipReason);
        }

        // Production anchor-resolvability lookup. Honours the test seam so
        // pure-xUnit cases can exercise the "anchor resolvable" Relative-frame
        // branch without instantiating Unity's FlightGlobals.
        private static bool ResolveAnchorInScene(uint anchorPid)
        {
            if (anchorPid == 0u) return false;
            if (AnchorResolvableForTesting != null)
                return AnchorResolvableForTesting(anchorPid);
            return FlightRecorder.FindVesselByPid(anchorPid) != null;
        }

        /// <summary>
        /// Resolve whether a state-vector trajectory point exists at the current
        /// UT and is suitable for ghost map creation. #583: when the current UT
        /// lies inside a Relative-frame section, the recorded
        /// <c>point.altitude</c> is the anchor-local dz offset (metres), not
        /// geographic altitude — the create/remove altitude thresholds are
        /// meaningless and are skipped. Creation in that branch is gated on the
        /// section's anchor being resolvable in the scene; otherwise we defer
        /// to the next tick so <see cref="ParsekPlaybackPolicy.CheckPendingMapVessels"/>
        /// can retry. Pure: the caller supplies the anchor-resolvability lookup
        /// so xUnit tests can exercise both branches without FlightGlobals.
        /// </summary>
        internal static bool TryResolveStateVectorMapPointPure(
            IPlaybackTrajectory traj,
            double currentUT,
            ref int cachedIndex,
            Func<uint, bool> anchorResolvable,
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

            // Resolve the section covering currentUT once — the Relative branch
            // needs the anchorVesselId, the Absolute branch needs nothing more.
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
                uint anchorPid = currentSection.Value.anchorVesselId;
                // Sections without an anchor id (legacy/synthetic) have no
                // resolvable anchor pose by construction. Surface as the
                // long-standing `relative-frame` reason to preserve pre-#583
                // behaviour for that subset (no map presence inside the
                // section, no log churn from a new reason kind).
                if (anchorPid == 0u)
                {
                    skipReason = TrackingStationGhostSkipRelativeFrame;
                    return false;
                }
                if (anchorResolvable == null || !anchorResolvable(anchorPid))
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

        internal static Vessel CreateGhostVesselFromSource(
            int recordingIndex,
            IPlaybackTrajectory traj,
            TrackingStationGhostSource source,
            OrbitSegment segment,
            TrajectoryPoint stateVectorPoint,
            double currentUT)
        {
            return CreateGhostVesselFromSource(
                recordingIndex,
                traj,
                source,
                segment,
                stateVectorPoint,
                currentUT,
                out _);
        }

        /// <summary>
        /// Overload that propagates <paramref name="retryLater"/> from
        /// <see cref="CreateGhostVesselFromStateVectors(int, IPlaybackTrajectory,
        /// TrajectoryPoint, double, out bool, bool, string)"/>. Non-state-vector
        /// branches always set it false. Callers that maintain a pending-map
        /// queue use this overload to decide whether to drop the pending entry
        /// on null return or keep it for the next tick (PR #574 review P2:
        /// retry-later semantics for the active-Re-Fly suppression gate).
        /// </summary>
        internal static Vessel CreateGhostVesselFromSource(
            int recordingIndex,
            IPlaybackTrajectory traj,
            TrackingStationGhostSource source,
            OrbitSegment segment,
            TrajectoryPoint stateVectorPoint,
            double currentUT,
            out bool retryLater)
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
                    Vessel segmentGhost = CreateGhostVesselFromSegment(recordingIndex, traj, segment);
                    if (segmentGhost != null)
                        UpdateGhostOrbitForRecording(recordingIndex, segment);
                    return segmentGhost;

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
        internal static void UpdateTrackingStationGhostLifecycle()
        {
            double currentUT = CurrentUTNow();
            var committed = RecordingStore.CommittedRecordings;
            bool hasCommittedRecordings = committed != null && committed.Count > 0;

            // Real-vessel materialization intentionally ignores the TS ghost-visibility toggle.
            if (hasCommittedRecordings)
                TryRunTrackingStationSpawnHandoffs(committed, currentUT);

            // #388: respect the user's tracking-station ghost visibility toggle.
            // ParsekTrackingStation.Update already handles the off-flip transition
            // (calls RemoveAllGhostVessels); here we simply skip creation so the
            // list stays empty while the flag is off. An empty-committed cache is
            // still published so ParsekTrackingStation.OnGUI guards still work.
            // Reads through the persistence store so the early-scene-load case
            // (ParsekSettings.Current still null) gets the correct user choice
            // from settings.cfg, not the pre-#388 default.
            if (!ParsekSettingsPersistence.EffectiveShowGhostsInTrackingStation())
            {
                CachedTrackingStationSuppressedIds = new HashSet<string>();
                ParsekLog.VerboseRateLimited(Tag,
                    "ts-lifecycle-disabled",
                    "UpdateTrackingStationGhostLifecycle: showGhostsInTrackingStation=false — skip",
                    2.0);
                return;
            }

            var suppressed = hasCommittedRecordings
                ? FindTrackingStationSuppressedRecordingIds(committed, currentUT)
                : new HashSet<string>();
            AddActiveSessionSuppressedRecordingIds(suppressed, committed);
            CachedTrackingStationSuppressedIds = suppressed;

            if (hasCommittedRecordings)
                GhostPlaybackLogic.InvalidateVesselCache();

            RefreshTrackingStationGhosts(committed, suppressed, currentUT);

            if (!hasCommittedRecordings)
            {
                return;
            }

            // --- Phase 2: create ghosts for recordings that just entered visible orbit range ---
            var sourceBatch = new TrackingStationGhostSourceBatch("tracking-station-lifecycle");
            int lifecycleCreated = 0;
            int alreadyTracked = 0;
            for (int i = 0; i < committed.Count; i++)
            {
                // Skip recordings that already have a ghost
                if (vesselsByRecordingIndex.ContainsKey(i))
                {
                    alreadyTracked++;
                    continue;
                }

                var rec = committed[i];
                bool isSuppressed = suppressed.Contains(rec.RecordingId);
                bool realVesselExists = rec.VesselPersistentId != 0
                    && GhostPlaybackLogic.RealVesselExists(rec.VesselPersistentId);
                int cachedStateVectorIndex = trackingStationStateVectorCachedIndices.TryGetValue(i, out int cached)
                    ? cached
                    : -1;
                TrackingStationGhostSource source = ResolveTrackingStationGhostSourceCore(
                    rec,
                    isSuppressed,
                    realVesselExists,
                    currentUT,
                    ref cachedStateVectorIndex,
                    out OrbitSegment segment,
                    out TrajectoryPoint stateVectorPoint,
                    out _,
                    sourceBatch,
                    i,
                    "tracking-station-lifecycle");
                trackingStationStateVectorCachedIndices[i] = cachedStateVectorIndex;
                if (source == TrackingStationGhostSource.None) continue;

                Vessel v = CreateGhostVesselFromSource(
                    i,
                    rec,
                    source,
                    segment,
                    stateVectorPoint,
                    currentUT);

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
                            i, rec.VesselName ?? "(null)", currentUT, FormatTrackingStationGhostSource(source)));
                }
            }

            sourceBatch.Log(
                "UpdateTrackingStationGhostLifecycle",
                committed.Count,
                lifecycleCreated,
                alreadyTracked);

            EmitLifecycleSummary("tracking-station", currentUT);
        }

        private static void RefreshTrackingStationGhosts(
            IReadOnlyList<Recording> committed,
            HashSet<string> suppressed,
            double currentUT)
        {
            if (vesselsByRecordingIndex.Count == 0)
                return;

            List<(int idx, string reason)> toRemove = null;
            foreach (var kvp in vesselsByRecordingIndex)
            {
                int idx = kvp.Key;
                if (committed == null)
                {
                    if (toRemove == null) toRemove = new List<(int, string)>();
                    toRemove.Add((idx, "tracking-station-expired"));
                    continue;
                }

                if (idx < 0 || idx >= committed.Count)
                {
                    if (toRemove == null) toRemove = new List<(int, string)>();
                    toRemove.Add((idx, "tracking-station-expired"));
                    continue;
                }

                var rec = committed[idx];
                bool isSuppressed = rec != null
                    && !string.IsNullOrEmpty(rec.RecordingId)
                    && suppressed != null
                    && suppressed.Contains(rec.RecordingId);
                bool realVesselExists = rec != null
                    && rec.VesselPersistentId != 0
                    && GhostPlaybackLogic.RealVesselExists(rec.VesselPersistentId);
                bool alreadyMaterialized =
                    IsTrackingStationRecordingMaterialized(rec, realVesselExists);
                uint pid = kvp.Value.persistentId;
                bool hasOrbitBounds = ghostOrbitBounds.TryGetValue(pid, out var bounds);
                bool isStateVector =
                    trackingStationStateVectorOrbitTrajectories.ContainsKey(idx);

                int cachedStateVectorIndex = trackingStationStateVectorCachedIndices.TryGetValue(idx, out int cached)
                    ? cached
                    : -1;
                bool fromCheckpoint = TryResolveCheckpointStateVectorMapPoint(
                    rec,
                    currentUT,
                    ref cachedStateVectorIndex,
                    out TrajectoryPoint checkpointPoint,
                    out _,
                    out _);

                string removeReason = GetTrackingStationGhostRemovalReason(
                    rec,
                    isSuppressed,
                    alreadyMaterialized,
                    hasOrbitBounds,
                    isStateVector || fromCheckpoint,
                    currentUT);
                if (removeReason != null)
                {
                    if (toRemove == null) toRemove = new List<(int, string)>();
                    toRemove.Add((idx, removeReason));
                    continue;
                }

                if (fromCheckpoint)
                {
                    trackingStationStateVectorCachedIndices[idx] = cachedStateVectorIndex;
                    if (UpdateGhostOrbitFromStateVectors(idx, rec, checkpointPoint, currentUT))
                    {
                        if (toRemove == null) toRemove = new List<(int, string)>();
                        toRemove.Add((idx, TrackingStationGhostSkipActiveReFlyRelativeUpdate));
                    }
                    continue;
                }

                if (isStateVector)
                {
                    TrajectoryPoint? pt = TrajectoryMath.BracketPointAtUT(
                        rec.Points,
                        currentUT,
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
                    bool inRelativeFrame = IsInRelativeFrame(rec, currentUT);
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

                    if (UpdateGhostOrbitFromStateVectors(idx, rec, pt.Value, currentUT))
                    {
                        if (toRemove == null) toRemove = new List<(int, string)>();
                        toRemove.Add((idx, TrackingStationGhostSkipActiveReFlyRelativeUpdate));
                    }
                    continue;
                }

                if (!hasOrbitBounds)
                    continue;

                OrbitSegment? seg = TrajectoryMath.FindOrbitSegmentForMapDisplay(rec.OrbitSegments, currentUT);
                if (!seg.HasValue)
                {
                    if (toRemove == null) toRemove = new List<(int, string)>();
                    toRemove.Add((idx, "tracking-station-expired"));
                    continue;
                }
                if (bounds.startUT != seg.Value.startUT || bounds.endUT != seg.Value.endUT)
                    UpdateGhostOrbitForRecording(idx, seg.Value);
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
            double currentUT)
        {
            if (committed == null || committed.Count == 0)
                return;

            var chains = GhostChainWalker.ComputeAllGhostChains(RecordingStore.CommittedTrees, currentUT);
            var relationSupersededIds = CurrentRelationSupersededRecordingIds(committed);
            List<int> eligibleIndices = null;
            for (int i = 0; i < committed.Count; i++)
            {
                var (needsSpawn, _) = ShouldSpawnAtTrackingStationEnd(
                    committed[i],
                    currentUT,
                    chains,
                    relationSupersededIds);
                if (!needsSpawn)
                    continue;

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
            var relationSupersededIds = CurrentRelationSupersededRecordingIds(committed);
            var (needsSpawn, _) = ShouldSpawnAtTrackingStationEnd(
                committed[index],
                currentUT,
                chains,
                relationSupersededIds);
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
                && GhostPlaybackLogic.RealVesselExists(rec.VesselPersistentId);
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
        /// Update orbit for a recording-index ghost when the ghost traverses orbit segments.
        /// </summary>
        internal static void UpdateGhostOrbitForRecording(int recordingIndex, OrbitSegment segment)
        {
            if (!vesselsByRecordingIndex.TryGetValue(recordingIndex, out Vessel vessel))
                return;
            ApplyOrbitToVessel(vessel, segment, string.Format(ic, "recording #{0}", recordingIndex));
            lifecycleUpdatedThisTick++;

            Vector3d worldPos = vessel.GetWorldPos3D();
            string recId = TryGetLastKnownFrame(recordingIndex, out var prev) ? prev.RecordingId : null;
            string vesselName = prev.VesselName;
            string updateKey = string.Format(ic, "rec-orbit-update-{0}",
                recId ?? recordingIndex.ToString(ic));

            var done = NewDecisionFields("update-segment");
            done.RecordingId = recId;
            done.RecordingIndex = recordingIndex;
            done.VesselName = vesselName;
            done.Source = "Segment";
            done.Branch = "(n/a)";
            done.Body = segment.bodyName;
            done.WorldPos = worldPos;
            done.GhostPid = vessel.persistentId;
            done.Segment = segment;
            ParsekLog.VerboseRateLimited(Tag, updateKey, BuildGhostMapDecisionLine(done), 5.0);

            // Refresh last-known so destroy can read the current orbit shape.
            StashLastKnownFrame(recordingIndex, new LastKnownGhostFrame
            {
                RecordingId = recId,
                VesselName = vesselName,
                GhostPid = vessel.persistentId,
                Source = "Segment",
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
            TrajectoryPoint? absoluteShadowPoint = null)
        {
            // v7+ Relative sections store an `absoluteFrames` shadow alongside
            // the anchor-local `frames`. When the caller has determined the
            // live anchor is unsafe (most commonly: the section is anchored to
            // the active Re-Fly target PID, so its live pose is being driven
            // by the player and no longer matches the recording), it can pass
            // the parallel shadow point here. Resolved through the standard
            // body-fixed surface lookup it yields the recorded world position
            // directly — no live anchor multiplication, no rotation drift.
            // Returns Branch="absolute-shadow" so call-site logs and tests can
            // distinguish this fallback from the regular Absolute path.
            if (absoluteShadowPoint.HasValue)
            {
                TrajectoryPoint shadow = absoluteShadowPoint.Value;
                Vector3d pos = absoluteSurfaceLookup(shadow.latitude, shadow.longitude, shadow.altitude);
                return new StateVectorWorldFrame
                {
                    Resolved = true,
                    WorldPos = pos,
                    Branch = "absolute-shadow",
                    FailureReason = null,
                    AnchorPid = section?.anchorVesselId ?? 0u,
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
                        AnchorPid = section.Value.anchorVesselId
                    };
                }

                // The lat/lon/alt fields are reused as anchor-local XYZ offsets in
                // RELATIVE sections (TrajectoryPoint.cs:13-15 docstring). Resolve via
                // the same canonical helper InterpolateAndPositionRelative uses on the
                // flight-scene playback path (ParsekFlight.cs:13821).
                Vector3d worldPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                    anchorWorldPos,
                    anchorWorldRot,
                    point.latitude,
                    point.longitude,
                    point.altitude,
                    recordingFormatVersion);

                return new StateVectorWorldFrame
                {
                    Resolved = true,
                    WorldPos = worldPos,
                    Branch = "relative",
                    FailureReason = null,
                    AnchorPid = section.Value.anchorVesselId
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
        /// Looks up the section, body, and anchor vessel, then delegates to the pure helper.
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
            uint anchorPid = section?.anchorVesselId ?? 0u;
            if (section.HasValue
                && section.Value.referenceFrame == ReferenceFrame.Relative
                && anchorPid != 0u)
            {
                Vessel anchor = FlightRecorder.FindVesselByPid(anchorPid);
                if (anchor != null)
                {
                    anchorFound = true;
                    anchorPos = anchor.GetWorldPos3D();
                    anchorRot = anchor.transform != null
                        ? anchor.transform.rotation
                        : Quaternion.identity;
                }
            }

            // Active-Re-Fly absolute-shadow opt-in: when this Relative section
            // is anchored to the vessel currently being re-flown (the live
            // anchor is being driven by the player, so it no longer matches
            // the recorded anchor pose), prefer the v7 absolute shadow point
            // over the live-anchor-multiplied relative offset. Without this
            // the upper-stage / sibling-chain ghosts get spawned at the
            // player's current world position with a hundreds-of-metres
            // offset and visibly bounce around the map.
            TrajectoryPoint? shadow = TryResolveActiveReFlyAbsoluteShadowPoint(
                traj, section, anchorPid, point.ut);

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
                shadow);
        }

        /// <summary>
        /// Returns the parallel <c>absoluteFrames</c> entry from the section
        /// when (a) we are inside an in-place Re-Fly session, (b) the
        /// section's anchor PID matches the active Re-Fly target's PID, and
        /// (c) the recording carries the v7 shadow payload. Otherwise null —
        /// callers fall through to live-anchor relative resolution.
        /// </summary>
        private static TrajectoryPoint? TryResolveActiveReFlyAbsoluteShadowPoint(
            IPlaybackTrajectory traj,
            TrackSection? section,
            uint anchorPid,
            double pointUT)
        {
            if (!section.HasValue) return null;
            if (section.Value.referenceFrame != ReferenceFrame.Relative) return null;
            if (anchorPid == 0u) return null;
            if (section.Value.absoluteFrames == null
                || section.Value.absoluteFrames.Count == 0)
                return null;
            if (traj == null || string.IsNullOrEmpty(traj.RecordingId)) return null;

            ReFlySessionMarker marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            if (marker == null
                || string.IsNullOrEmpty(marker.ActiveReFlyRecordingId)
                || string.IsNullOrEmpty(marker.OriginChildRecordingId)
                || !string.Equals(
                    marker.ActiveReFlyRecordingId,
                    marker.OriginChildRecordingId,
                    StringComparison.Ordinal))
            {
                return null;
            }

            // Resolve active Re-Fly PID via the same composed-trees walk used
            // elsewhere in this file (#611) so PendingTree placements during
            // Re-Fly load are honoured.
            uint activeReFlyPid = 0u;
            IReadOnlyList<RecordingTree> trees = ComposeSearchTreesForReFlySuppression(
                RecordingStore.CommittedTrees,
                RecordingStore.HasPendingTree ? RecordingStore.PendingTree : null);
            if (trees != null)
            {
                for (int t = 0; t < trees.Count && activeReFlyPid == 0u; t++)
                {
                    var tree = trees[t];
                    if (tree?.Recordings == null) continue;
                    if (tree.Recordings.TryGetValue(marker.ActiveReFlyRecordingId, out Recording rec)
                        && rec != null
                        && rec.VesselPersistentId != 0u)
                    {
                        activeReFlyPid = rec.VesselPersistentId;
                    }
                }
            }
            if (activeReFlyPid == 0u || anchorPid != activeReFlyPid)
                return null;

            // Find the closest absolute-shadow entry to pointUT. The shadow
            // list is sample-aligned with the relative `frames` list, so a
            // simple linear scan picks the matching pair. For robustness
            // against minor UT drift we accept the closest entry within one
            // sample interval (~0.1 s); outside that we fall through and let
            // the regular live-anchor path produce a (possibly wrong) result
            // rather than synthesising a position from a far-away shadow.
            const double matchToleranceSeconds = 0.5;
            var frames = section.Value.absoluteFrames;
            int bestIdx = -1;
            double bestDelta = double.PositiveInfinity;
            for (int i = 0; i < frames.Count; i++)
            {
                double delta = System.Math.Abs(frames[i].ut - pointUT);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestIdx = i;
                }
            }
            if (bestIdx < 0 || bestDelta > matchToleranceSeconds) return null;
            return frames[bestIdx];
        }

        /// <summary>
        /// Create a ghost map ProtoVessel from interpolated trajectory state vectors.
        /// Used for physics-only suborbital recordings that have no orbit segments.
        /// Constructs a Keplerian orbit from position + velocity at the given UT.
        /// Honours the originating TrackSection's <see cref="ReferenceFrame"/>: Absolute
        /// uses surface lat/lon/alt; Relative resolves through the anchor vessel's
        /// world transform (matches the flight-scene contract in ParsekFlight.cs:13821).
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
        /// Overload that exposes <paramref name="retryLater"/> = true when the
        /// PR #574 active-Re-Fly suppression gate fires. Callers that maintain
        /// a "pending map vessel" queue (cf.
        /// <c>ParsekPlaybackPolicy.CheckPendingMapVessels</c>) keep the
        /// pending entry alive on (<c>null</c>, <c>retryLater = true</c>) so
        /// the recording is retried next tick once the Re-Fly session ends —
        /// rather than silently dropping it forever.
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
                skip.Reason = resolution.FailureReason ?? "(null)";
                ParsekLog.Warn(Tag, BuildGhostMapDecisionLine(skip));
                return null;
            }

            // Compute optional anchor metadata for the structured line.
            Vector3d? anchorPosForLog = null;
            Vector3d? localOffsetForLog = null;
            if (resolution.Branch == "relative" && resolution.AnchorPid != 0u)
            {
                Vessel anchorRef = FlightRecorder.FindVesselByPid(resolution.AnchorPid);
                if (anchorRef != null)
                {
                    anchorPosForLog = anchorRef.GetWorldPos3D();
                    localOffsetForLog = new Vector3d(point.latitude, point.longitude, point.altitude);
                }
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
            orbit.UpdateFromStateVectors(worldPos, vel, body, ut);

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
        /// </summary>
        internal static bool UpdateGhostOrbitFromStateVectors(
            int recordingIndex, IPlaybackTrajectory traj,
            TrajectoryPoint point,
            double ut,
            bool allowOrbitalCheckpointStateVector = false,
            string stateVectorUpdateReason = null)
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

            // Compute optional anchor metadata for the structured line.
            Vector3d? anchorPosForLog = null;
            Vector3d? localOffsetForLog = null;
            if (resolution.Branch == "relative" && resolution.AnchorPid != 0u)
            {
                Vessel anchorRef = FlightRecorder.FindVesselByPid(resolution.AnchorPid);
                if (anchorRef != null)
                {
                    anchorPosForLog = anchorRef.GetWorldPos3D();
                    localOffsetForLog = new Vector3d(point.latitude, point.longitude, point.altitude);
                }
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

            // SOI transition handling (same pattern as ApplyOrbitToVessel).
            // OrbitDriver.celestialBody is only for real CelestialBody drivers;
            // vessel targets must keep identity in OrbitDriver.vessel.
            bool soiChanged = vessel.orbitDriver.referenceBody != body;
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

            vessel.orbitDriver.orbit.UpdateFromStateVectors(worldPos, vel, body, ut);
            vessel.orbitDriver.updateFromParameters();
            NormalizeGhostOrbitDriverTargetIdentity(vessel, "update-state-vector");

            if (soiChanged && vessel.orbitRenderer != null)
            {
                vessel.orbitRenderer.drawMode = OrbitRendererBase.DrawMode.REDRAW_AND_RECALCULATE;
                vessel.orbitRenderer.enabled = false;
                vessel.orbitRenderer.enabled = true;
            }

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
            done.Reason = stateVectorUpdateReason;
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
        /// Shared: apply an OrbitSegment's Keplerian elements to a ghost vessel's OrbitDriver.
        /// Handles body resolution, orbit construction, SOI transitions, and logging.
        /// </summary>
        private static void ApplyOrbitToVessel(Vessel vessel, OrbitSegment segment, string logContext)
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

            // SOI transition: compare the Orbit reference body. OrbitDriver.celestialBody
            // must stay null for vessel targets; stock OrbitTargeter treats a non-null
            // celestialBody on a target driver as a body target and drops same-body ghosts.
            bool soiChanged = vessel.orbitDriver.referenceBody != body;
            if (soiChanged)
            {
                ParsekLog.Info(Tag,
                    string.Format(ic, "SOI change for {0} — new body={1}", logContext, body.name));
            }

            // Direct element assignment via SetOrbit — bypasses the lossy
            // state-vector roundtrip in UpdateFromOrbitAtUT (#172).
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

            vessel.orbitDriver.updateFromParameters();
            NormalizeGhostOrbitDriverTargetIdentity(vessel, logContext);

            // Store orbit segment time bounds for arc clipping (GhostOrbitArcPatch)
            ghostOrbitBounds[vessel.persistentId] = (segment.startUT, segment.endUT);

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

            ParsekLog.Verbose(Tag,
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
            var relationSupersededIds =
                CurrentRelationSupersededRecordingIds(RecordingStore.CommittedRecordings);
            return ShouldSpawnAtTrackingStationEnd(
                rec,
                currentUT,
                chains,
                relationSupersededIds);
        }

        internal static (bool needsSpawn, string reason) ShouldSpawnAtTrackingStationEnd(
            Recording rec,
            double currentUT,
            Dictionary<uint, GhostChain> chains,
            HashSet<string> relationSupersededIds)
        {
            if (rec == null)
                return (false, "null");

            if (RecordingStore.RewindUTAdjustmentPending)
                return (false, TrackingStationSpawnSkipRewindPending);

            if (currentUT < rec.EndUT)
                return (false, TrackingStationSpawnSkipBeforeEnd);

            if (!string.IsNullOrEmpty(rec.RecordingId)
                && relationSupersededIds != null
                && relationSupersededIds.Contains(rec.RecordingId))
                return (false, TrackingStationSpawnSkipSupersededByRelation);

            bool isChainLooping = !string.IsNullOrEmpty(rec.ChainId)
                && RecordingStore.IsChainLooping(rec.ChainId);
            var spawnResult = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false, isChainLooping);
            if (!spawnResult.needsSpawn)
                return spawnResult;

            if (RecordingStore.IsChainMidSegment(rec))
                return (false, TrackingStationSpawnSkipIntermediateChainSegment);
            var chainSuppressed = GhostPlaybackLogic.ShouldSuppressSpawnForChain(chains, rec);
            if (chainSuppressed.suppressed)
                return (false, NormalizeTrackingStationSpawnSuppressionReason(chainSuppressed.reason));

            return spawnResult;
        }

        private static HashSet<string> CurrentRelationSupersededRecordingIds(
            IReadOnlyList<Recording> committed)
        {
            var scenario = ParsekScenario.Instance;
            return EffectiveState.ComputeSupersededRecordingIdsByRelation(
                committed,
                object.ReferenceEquals(null, scenario) ? null : scenario.RecordingSupersedes);
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
            return FindTrackingStationSuppressedRecordingIds(recordings, currentUT, supersedes);
        }

        internal static HashSet<string> FindTrackingStationSuppressedRecordingIds(
            IReadOnlyList<Recording> recordings, double currentUT,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
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
            string context)
        {
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
                recordingIndex: recordingIndex);

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
        internal static int CreateGhostVesselsFromCommittedRecordings()
        {
            // #388: respect the user's tracking-station ghost visibility toggle.
            // This path is called from SpaceTracking.Awake prefix where
            // ParsekSettings.Current may still be null — read through the
            // persistence store so the user's recorded preference (from
            // settings.cfg) wins over the pre-#388 default.
            if (!ParsekSettingsPersistence.EffectiveShowGhostsInTrackingStation())
            {
                CachedTrackingStationSuppressedIds = new HashSet<string>();
                int commCount = RecordingStore.CommittedRecordings?.Count ?? 0;
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "CreateGhostVesselsFromCommittedRecordings: " +
                        "showGhostsInTrackingStation=false — skipping {0} committed recording(s)",
                        commCount));
                return 0;
            }

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

            int created = 0, skippedDebris = 0, skippedSuppressed = 0, skippedSpawned = 0;
            int skippedTerminal = 0, skippedBeforeActivation = 0;
            int skippedBeforeTerminalOrbit = 0, skippedNoOrbit = 0;
            int skippedEndpointConflict = 0, skippedUnseedableTerminalOrbit = 0;
            int sourceVisibleSegment = 0, sourceTerminalOrbit = 0, sourceStateVector = 0;
            var sourceBatch = new TrackingStationGhostSourceBatch("tracking-station-startup");

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                bool isSuppressed = suppressed.Contains(rec.RecordingId);
                bool realVesselExists = rec.VesselPersistentId != 0
                    && GhostPlaybackLogic.RealVesselExists(rec.VesselPersistentId);
                int cachedStateVectorIndex = trackingStationStateVectorCachedIndices.TryGetValue(i, out int cached)
                    ? cached
                    : -1;

                TrackingStationGhostSource source = ResolveTrackingStationGhostSourceCore(
                    rec,
                    isSuppressed,
                    realVesselExists,
                    currentUT,
                    ref cachedStateVectorIndex,
                    out OrbitSegment segment,
                    out TrajectoryPoint stateVectorPoint,
                    out string skipReason,
                    sourceBatch,
                    i,
                    "tracking-station-startup");
                trackingStationStateVectorCachedIndices[i] = cachedStateVectorIndex;
                if (source == TrackingStationGhostSource.None)
                {
                    if (skipReason == "debris") skippedDebris++;
                    else if (skipReason == TrackingStationGhostSkipSuppressed) skippedSuppressed++;
                    else if (skipReason == TrackingStationGhostSkipAlreadySpawned) skippedSpawned++;
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
                else if (source == TrackingStationGhostSource.Segment)
                    sourceVisibleSegment++;
                else if (IsStateVectorGhostSource(source))
                    sourceStateVector++;

                Vessel v = CreateGhostVesselFromSource(
                    i,
                    rec,
                    source,
                    segment,
                    stateVectorPoint,
                    currentUT);

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
                    "sources(visibleSegment={2} terminalOrbit={3} stateVector={4}) " +
                    "(skipped: debris={5} suppressed={6} spawned={7} terminal={8} beforeActivation={9} " +
                    "beforeTerminalOrbit={10} noOrbit={11} endpointConflict={12} terminalUnseedable={13})",
                    created, committed.Count,
                    sourceVisibleSegment, sourceTerminalOrbit, sourceStateVector,
                    skippedDebris, skippedSuppressed, skippedSpawned, skippedTerminal,
                    skippedBeforeActivation, skippedBeforeTerminalOrbit, skippedNoOrbit,
                    skippedEndpointConflict, skippedUnseedableTerminalOrbit));

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
            double currentUT)
        {
            if (rec == null)
                return "tracking-station-expired";

            if (isSuppressed)
                return "tracking-station-child-started";

            if (alreadyMaterialized)
                return "tracking-station-materialized-real-vessel";

            if (isStateVector)
                return currentUT > rec.EndUT
                    ? "tracking-station-state-vector-expired"
                    : null;

            if (!hasOrbitBounds)
                return null;

            OrbitSegment? seg = TrajectoryMath.FindOrbitSegmentForMapDisplay(rec.OrbitSegments, currentUT);
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
            return vesselsByRecordingIndex.ContainsKey(recordingIndex);
        }

        /// <summary>
        /// Returns the persistentId of the ghost map vessel for a recording index, or 0 if none.
        /// Used to check ghostsWithSuppressedIcon for the below-atmosphere icon handoff.
        /// </summary>
        internal static uint GetGhostVesselPidForRecording(int recordingIndex)
        {
            if (vesselsByRecordingIndex.TryGetValue(recordingIndex, out Vessel v))
                return v.persistentId;
            return 0;
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

            int recordingIndex = FindRecordingIndexByVesselPid(vesselPid);
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

            if (ghostOrbitBounds.TryGetValue(vesselPid, out var bounds))
            {
                startUT = bounds.startUT;
                endUT = bounds.endUT;
                ParsekLog.VerboseOnChange(Tag,
                    string.Format(ic, "visible-window-stored-bounds|{0}", vesselPid),
                    string.Format(ic, "stored-bounds|{0:F3}-{1:F3}", startUT, endUT),
                    string.Format(ic,
                        "Map-visible orbit window pid={0} source=stored-bounds-fallback ut={1:F2} windowUT={2:F2}-{3:F2}",
                        vesselPid, currentUT, startUT, endUT));
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reset all state for testing (avoids Debug.Log crash outside Unity).
        /// </summary>
        internal static void ResetForTesting()
        {
            CurrentUTNow = GetCurrentUTSafe;
            AnchorResolvableForTesting = null;
            ghostMapVesselPids.Clear();
            ghostsWithSuppressedIcon.Clear();
            ghostOrbitBounds.Clear();
            vesselsByChainPid.Clear();
            vesselsByRecordingIndex.Clear();
            vesselPidToRecordingIndex.Clear();
            vesselPidToRecordingId.Clear();
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
            int reverseCount = vesselPidToRecordingIndex.Count;
            int reverseIdCount = vesselPidToRecordingId.Count;
            int tsStateVectorCount = trackingStationStateVectorOrbitTrajectories.Count;
            int tsStateVectorCacheCount = trackingStationStateVectorCachedIndices.Count;

            int totalTracked = pidCount + suppressedIconCount + orbitBoundsCount
                + chainCount + indexCount + reverseCount + reverseIdCount
                + tsStateVectorCount + tsStateVectorCacheCount;

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
            ghostOrbitBounds.Clear();
            vesselsByChainPid.Clear();
            vesselsByRecordingIndex.Clear();
            vesselPidToRecordingIndex.Clear();
            vesselPidToRecordingId.Clear();
            trackingStationStateVectorOrbitTrajectories.Clear();
            trackingStationStateVectorCachedIndices.Clear();
            activeReFlyDeferredStateVectorGhostSessions.Clear();
            lastKnownByRecordingIndex.Clear();
            lastKnownByChainPid.Clear();

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "ResetBetweenTestRuns: cleared bookkeeping reason={0} " +
                    "pids={1} suppressedIcons={2} orbitBounds={3} chainVessels={4} " +
                    "indexVessels={5} reverseLookup={6} reverseIdLookup={7} " +
                    "tsStateVectors={8} tsStateVectorCache={9}",
                    reason ?? "(null)",
                    pidCount, suppressedIconCount, orbitBoundsCount,
                    chainCount, indexCount, reverseCount, reverseIdCount,
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

        /// <summary>
        /// Find a CelestialBody by name without LINQ allocation.
        /// Returns null if FlightGlobals.Bodies is null or name not found.
        /// </summary>
        private static CelestialBody FindBodyByName(string bodyName)
        {
            var bodies = FlightGlobals.Bodies;
            if (bodies == null) return null;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].name == bodyName) return bodies[i];
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
            IPlaybackTrajectory traj, OrbitSegment segment, string logContext)
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

            Orbit orbit = new Orbit(
                segment.inclination,
                segment.eccentricity,
                segment.semiMajorAxis,
                segment.longitudeOfAscendingNode,
                segment.argumentOfPeriapsis,
                segment.meanAnomalyAtEpoch,
                segment.epoch,
                body);

            return BuildAndLoadGhostProtoVesselCore(
                traj,
                orbit,
                body,
                logContext,
                "visible-segment",
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
                        "seedFailure={1} endpointBody={2} seedFallback={3}",
                        logContext,
                        seedDiagnostics.FailureReason ?? "(none)",
                        seedDiagnostics.EndpointBodyName ?? "(none)",
                        seedDiagnostics.FallbackReason ?? "(none)"));
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
                    "seedBody={0} endpointBody={1} seedFallback={2}",
                    orbitBodyName ?? "(null)",
                    seedDiagnostics.EndpointBodyName ?? "(none)",
                    seedDiagnostics.FallbackReason ?? "(none)"));
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
                    FallbackReason = null
                };
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
                FallbackReason = endpointDiagnostics.FailureReason
            };

            if (fallbackResolved && string.IsNullOrEmpty(diagnostics.EndpointBodyName)
                && RecordingEndpointResolver.TryGetPreferredEndpointBodyName(traj, out string preferredEndpointBody))
            {
                diagnostics.EndpointBodyName = preferredEndpointBody;
            }

            return fallbackResolved;
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
            // Single antenna-free part (avoids CommNet conflict with GhostCommNetRelay)
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
    }
}
