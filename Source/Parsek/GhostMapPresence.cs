using System;
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
            StateVector = 3
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

        private const string Tag = "GhostMap";
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

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
            ParsekLog.VerboseRateLimited(
                Tag,
                "gm-lifecycle-summary",
                string.Format(ic,
                    "lifecycle-summary: scope={0} vesselsTracked={1} created={2} destroyed={3} updated={4} currentUT={5:F1} scene={6}",
                    scope ?? "(unspecified)",
                    vesselsByRecordingIndex.Count,
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
        internal const string TrackingStationSpawnSkipRewindPending = "rewind-ut-adjustment-pending";
        internal const string TrackingStationSpawnSkipBeforeEnd = "before-recording-end";
        internal const string TrackingStationSpawnSkipIntermediateChainSegment = "intermediate-chain-segment";
        internal const string TrackingStationSpawnSkipIntermediateGhostChainLink = "intermediate-ghost-chain-link";
        internal const string TrackingStationSpawnSkipTerminatedGhostChain = "terminated-ghost-chain";
        internal const double StateVectorCreateAltitude = 1500;   // meters (airless bodies only)
        internal const double StateVectorCreateSpeed = 60;        // m/s
        internal const double StateVectorRemoveAltitude = 500;    // meters (airless bodies only)
        internal const double StateVectorRemoveSpeed = 30;        // m/s
        private const double LegacyPointCoverageMaxGapSeconds = 30.0;
        internal static Func<double> CurrentUTNow = GetCurrentUTSafe;

        internal struct GhostProtoOrbitSeedDiagnostics
        {
            public string Source;
            public string EndpointBodyName;
            public string FailureReason;
            public string FallbackReason;
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
                else if (source == TrackingStationGhostSource.StateVector)
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
        internal static bool HasOrbitData(Recording rec)
        {
            if (rec == null)
            {
                ParsekLog.Verbose(Tag, "HasOrbitData(Recording): null recording — returning false");
                return false;
            }

            bool hasOrbit = HasTerminalOrbitData(rec);

            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "HasOrbitData(Recording): rec={0} body={1} sma={2} result={3}",
                    rec.RecordingId ?? "(null)",
                    rec.TerminalOrbitBody ?? "(null)",
                    rec.TerminalOrbitSemiMajorAxis,
                    hasOrbit));

            return hasOrbit;
        }

        /// <summary>
        /// Pure: does this trajectory have orbital data suitable for map presence?
        /// Overload accepting IPlaybackTrajectory for engine-side use.
        /// </summary>
        internal static bool HasOrbitData(IPlaybackTrajectory traj)
        {
            if (traj == null)
            {
                ParsekLog.Verbose(Tag, "HasOrbitData(IPlaybackTrajectory): null trajectory — returning false");
                return false;
            }

            bool hasOrbit = HasTerminalOrbitData(traj);

            if (hasOrbit)
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "HasOrbitData(IPlaybackTrajectory): body={0} sma={1} result=True",
                        traj.TerminalOrbitBody,
                        traj.TerminalOrbitSemiMajorAxis));

            return hasOrbit;
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
                string body = vessel.orbitDriver?.celestialBody?.name ?? traj.TerminalOrbitBody;

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
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "RemoveAllGhostVessels: no ghost vessels to remove (reason={0})",
                        reason));
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
                string body = vessel.orbitDriver?.celestialBody?.name ?? traj.TerminalOrbitBody;

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
            else if (source == TrackingStationGhostSource.StateVector)
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
            ParsekLog.VerboseRateLimited(
                Tag,
                string.Format(ic,
                    "gm-source-resolve-{0}-{1}",
                    recId,
                    source),
                BuildGhostMapDecisionLine(srf),
                5.0);
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
            int recordingIndex = -1)
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
                    ParsekLog.VerboseRateLimited(
                        Tag,
                        string.Format(ic,
                            "map-ghost-source-{0}-{1}-{2}-{3}",
                            logOperationName,
                            recId,
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

            // Dense OrbitalCheckpoint frames are derived from the matching OrbitSegment,
            // but they are the higher-fidelity source for map icons during the checkpoint window.
            if (TryResolveCheckpointStateVectorMapPoint(
                traj,
                currentUT,
                ref stateVectorCachedIndex,
                out stateVectorPoint,
                out _,
                out TrackSection checkpointSection))
            {
                resolvedStatePoint = stateVectorPoint;
                return ReturnDecision(
                    TrackingStationGhostSource.StateVector,
                    skipReason,
                    string.Format(ic,
                        "stateVectorSource=OrbitalCheckpoint sectionUT={0:F1}-{1:F1} pointUT={2:F1} stateVectorBody={3} alt={4:F0} speed={5:F1}",
                        checkpointSection.startUT,
                        checkpointSection.endUT,
                        stateVectorPoint.ut,
                        stateVectorPoint.bodyName ?? "(null)",
                        stateVectorPoint.altitude,
                        stateVectorPoint.velocity.magnitude));
            }

            if (traj.HasOrbitSegments)
            {
                OrbitSegment? currentSegment =
                    TrajectoryMath.FindOrbitSegmentForMapDisplay(traj.OrbitSegments, currentUT);
                if (currentSegment.HasValue)
                {
                    segment = currentSegment.Value;
                    resolvedSegment = segment;
                    return ReturnDecision(
                        TrackingStationGhostSource.Segment,
                        skipReason,
                        string.Format(ic,
                            "segmentBody={0} segmentUT={1:F1}-{2:F1}",
                            segment.bodyName ?? "(null)",
                            segment.startUT,
                            segment.endUT));
                }
            }

            string stateVectorSkipReason = null;
            if (!traj.HasOrbitSegments)
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
                skipReason = traj.HasOrbitSegments
                    ? "no-current-segment"
                    : NormalizeStateVectorSkipReasonForNoOrbit(stateVectorSkipReason);
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    string.Format(ic, "terminalFallback=False hasOrbitSegments={0}", traj.HasOrbitSegments));
            }

            if (!HasOrbitData(traj))
            {
                skipReason = traj.HasOrbitSegments
                    ? "no-current-segment"
                    : NormalizeStateVectorSkipReasonForNoOrbit(stateVectorSkipReason);
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    string.Format(ic, "hasOrbitSegments={0}", traj.HasOrbitSegments));
            }

            if (!IsTerminalStateEligibleForTerminalOrbitMapPresence(terminal))
            {
                skipReason = "terminal-" + terminal.Value;
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    string.Format(ic, "terminal={0} terminalOrbitFallback=True", terminal.Value));
            }

            double activationStartUT = PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(traj);
            if (currentUT < activationStartUT)
            {
                skipReason = "before-activation";
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    string.Format(ic, "activationStartUT={0:F1}", activationStartUT));
            }

            bool allowSparseOrbitGapFallback =
                traj.HasOrbitSegments
                && currentUT < traj.EndUT
                && !HasRecordedTrackCoverageAtUT(traj, currentUT);
            if (currentUT < traj.EndUT && !allowSparseOrbitGapFallback)
            {
                skipReason = "before-terminal-orbit";
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    string.Format(ic, "endUT={0:F1}", traj.EndUT));
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
                    string.Format(ic,
                        "terminalBody={0} endUT={1:F1} seedFailure={2} endpointBody={3}",
                        traj.TerminalOrbitBody ?? "(null)",
                        traj.EndUT,
                        seedDiagnostics.FailureReason ?? "(none)",
                        seedDiagnostics.EndpointBodyName ?? "(none)"));
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
                string.Format(ic,
                    "terminalBody={0} endUT={1:F1} seedBody={2} seedSource={3} endpointBody={4} seedFallback={5}",
                    traj.TerminalOrbitBody ?? "(null)",
                    traj.EndUT,
                    seedBodyName ?? "(null)",
                    seedDiagnostics.Source ?? "(none)",
                    seedDiagnostics.EndpointBodyName ?? "(none)",
                    seedDiagnostics.FallbackReason ?? "(none)"));
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
                    return BuildStateVectorSourceStructuredDetail(recId, stateVectorPoint);

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
            string recId,
            TrajectoryPoint point)
        {
            string world = TryResolvePointWorldPosition(point, out Vector3d worldPos)
                ? FormatWorldPosition(worldPos)
                : "(unresolved)";
            return string.Format(ic,
                "sourceKind=StateVector rec={0} body={1} sourceUT={2:F1} pointUT={3:F1} alt={4:F0} speed={5:F1} world={6}",
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

        private static bool TryResolveStateVectorMapPoint(
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

            if (IsInRelativeFrame(traj, currentUT))
            {
                skipReason = TrackingStationGhostSkipRelativeFrame;
                return false;
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
                    dispatch.Body = stateVectorPoint.bodyName;
                    dispatch.StateVecAlt = stateVectorPoint.altitude;
                    dispatch.StateVecSpeed = stateVectorPoint.velocity.magnitude;
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
                    return CreateGhostVesselFromStateVectors(
                        recordingIndex,
                        traj,
                        stateVectorPoint,
                        currentUT);

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
                    if (source == TrackingStationGhostSource.StateVector)
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
                    UpdateGhostOrbitFromStateVectors(idx, rec, checkpointPoint, currentUT);
                    continue;
                }

                if (isStateVector)
                {
                    TrajectoryPoint? pt = TrajectoryMath.BracketPointAtUT(
                        rec.Points,
                        currentUT,
                        ref cachedStateVectorIndex);
                    trackingStationStateVectorCachedIndices[idx] = cachedStateVectorIndex;

                    if (!pt.HasValue || IsInRelativeFrame(rec, currentUT))
                    {
                        if (toRemove == null) toRemove = new List<(int, string)>();
                        toRemove.Add((idx, "tracking-station-state-vector-expired"));
                        continue;
                    }

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

                    UpdateGhostOrbitFromStateVectors(idx, rec, pt.Value, currentUT);
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
            List<int> eligibleIndices = null;
            for (int i = 0; i < committed.Count; i++)
            {
                var (needsSpawn, _) = ShouldSpawnAtTrackingStationEnd(committed[i], currentUT, chains);
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
            var (needsSpawn, _) = ShouldSpawnAtTrackingStationEnd(committed[index], currentUT, chains);
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
            uint anchorVesselId)
        {
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

            // OrbitalCheckpoint: state-vector entry points should never come from a
            // checkpoint section (those carry Keplerian elements, not point samples).
            // If we get here, the upstream caller fed mismatched data — refuse.
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
            CelestialBody body)
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

            int formatVersion = traj?.RecordingFormatVersion ?? 0;
            return ResolveStateVectorWorldPositionPure(
                point,
                section,
                formatVersion,
                (lat, lon, alt) => body.GetWorldSurfacePosition(lat, lon, alt),
                anchorFound,
                anchorPos,
                anchorRot,
                anchorPid);
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
            TrajectoryPoint point, double ut)
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
                ResolveStateVectorWorldPosition(traj, point, body);
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

            Vector3d worldPos = resolution.WorldPos;
            Vector3d vel = new Vector3d(point.velocity.x, point.velocity.y, point.velocity.z);

            Orbit orbit = new Orbit();
            orbit.UpdateFromStateVectors(worldPos, vel, body, ut);

            string logContext = string.Format(ic,
                "recording #{0} (state vectors alt={1:F0} spd={2:F1} frame={3})",
                recordingIndex, point.altitude, point.velocity.magnitude, resolution.Branch);
            Vessel vessel = BuildAndLoadGhostProtoVesselCore(
                traj,
                orbit,
                body,
                logContext,
                "state-vector-fallback",
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
                done.Reason = string.Format(ic, "formatV={0}", traj.RecordingFormatVersion);
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
        internal static void UpdateGhostOrbitFromStateVectors(
            int recordingIndex, IPlaybackTrajectory traj,
            TrajectoryPoint point, double ut)
        {
            if (!vesselsByRecordingIndex.TryGetValue(recordingIndex, out Vessel vessel))
                return;

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
                return;
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
                return;
            }

            StateVectorWorldFrame resolution =
                ResolveStateVectorWorldPosition(traj, point, body);
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
                return;
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

            // SOI transition handling (same pattern as ApplyOrbitToVessel)
            bool soiChanged = vessel.orbitDriver.celestialBody != body;
            if (soiChanged)
            {
                vessel.orbitDriver.celestialBody = body;
                var soi = NewDecisionFields("update-state-vector-soi-change");
                soi.RecordingId = traj?.RecordingId;
                soi.RecordingIndex = recordingIndex;
                soi.VesselName = traj?.VesselName;
                soi.Source = "StateVector";
                soi.Branch = MapResolutionBranch(resolution.Branch);
                soi.Body = body.name;
                soi.GhostPid = vessel.persistentId;
                soi.UT = ut;
                ParsekLog.Info(Tag, BuildGhostMapDecisionLine(soi));
            }

            Vector3d worldPos = resolution.WorldPos;
            Vector3d vel = new Vector3d(point.velocity.x, point.velocity.y, point.velocity.z);

            vessel.orbitDriver.orbit.UpdateFromStateVectors(worldPos, vel, body, ut);
            vessel.orbitDriver.updateFromParameters();

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

            // SOI transition: update celestialBody BEFORE SetOrbit so that
            // orbitDriver and orbit.referenceBody are consistent when
            // updateFromParameters recalculates the orbit line (#189).
            bool soiChanged = vessel.orbitDriver.celestialBody != body;
            if (soiChanged)
            {
                vessel.orbitDriver.celestialBody = body;
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
            if (rec == null)
                return (false, "null");

            if (RecordingStore.RewindUTAdjustmentPending)
                return (false, TrackingStationSpawnSkipRewindPending);

            if (currentUT < rec.EndUT)
                return (false, TrackingStationSpawnSkipBeforeEnd);

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

            return suppressed;
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
                else if (source == TrackingStationGhostSource.StateVector)
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
                    if (source == TrackingStationGhostSource.StateVector)
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
                ParsekLog.VerboseRateLimited(Tag,
                    string.Format(ic,
                        "visible-window-{0}-{1:F3}-{2:F3}-{3:F3}-{4:F3}-{5}",
                        vesselPid,
                        segment.startUT,
                        segment.endUT,
                        startUT,
                        endUT,
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
                        carriedAcrossGap),
                    1.0);

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
                ParsekLog.VerboseRateLimited(Tag,
                    "visible-window-none",
                    string.Format(ic,
                        "Map-visible orbit window unavailable source=none reason=no-active-equivalent-segment " +
                        "pid={0} recIndex={1} ut={2:F2} — " +
                        "no active or equivalent same-orbit segment chain",
                        vesselPid, recordingIndex, currentUT),
                    1.0);
            }

            if (ghostOrbitBounds.TryGetValue(vesselPid, out var bounds))
            {
                startUT = bounds.startUT;
                endUT = bounds.endUT;
                ParsekLog.VerboseRateLimited(Tag,
                    "visible-window-stored-bounds-fallback",
                    string.Format(ic,
                        "Map-visible orbit window pid={0} source=stored-bounds-fallback ut={1:F2} windowUT={2:F2}-{3:F2}",
                        vesselPid, currentUT, startUT, endUT),
                    1.0);
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
            ghostMapVesselPids.Clear();
            ghostsWithSuppressedIcon.Clear();
            ghostOrbitBounds.Clear();
            vesselsByChainPid.Clear();
            vesselsByRecordingIndex.Clear();
            vesselPidToRecordingIndex.Clear();
            vesselPidToRecordingId.Clear();
            trackingStationStateVectorOrbitTrajectories.Clear();
            trackingStationStateVectorCachedIndices.Clear();
            lastKnownByRecordingIndex.Clear();
            lastKnownByChainPid.Clear();
            lifecycleCreatedThisTick = 0;
            lifecycleDestroyedThisTick = 0;
            lifecycleUpdatedThisTick = 0;
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
                string driverState = "no-orbitDriver";
                if (v.orbitDriver != null)
                {
                    Orbit drv = v.orbitDriver.orbit;

                    driverState = string.Format(ic,
                        "updateMode={0} sma={1:F0} ecc={2:F6} inc={3:F4} " +
                        "argPe={4:F4} mna={5:F6} epoch={6:F1} vesselPos=({7:F1},{8:F1},{9:F1})",
                        v.orbitDriver.updateMode,
                        drv.semiMajorAxis, drv.eccentricity, drv.inclination,
                        drv.argumentOfPeriapsis, drv.meanAnomalyAtEpoch, drv.epoch,
                        v.GetWorldPos3D().x, v.GetWorldPos3D().y, v.GetWorldPos3D().z);
                }

                // Ensure OrbitRenderer is enabled — in Tracking Station, pv.Load()
                // may create the renderer in a disabled state.
                if (v.orbitRenderer != null)
                {
                    v.orbitRenderer.drawMode = OrbitRendererBase.DrawMode.REDRAW_AND_RECALCULATE;
                    v.orbitRenderer.drawIcons = OrbitRendererBase.DrawIcons.ALL;
                    if (!v.orbitRenderer.enabled)
                    {
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

                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Created ghost vessel '{0}' ghostPid={1} type={2} body={3} sma={4:F0} for {5} " +
                        "orbitSource={6}{7} | {8} mapObj={9} orbitRenderer={10} scene={11}",
                        vesselName, v.persistentId,
                        vtype, body.name, orbit.semiMajorAxis, logContext,
                        string.IsNullOrEmpty(orbitSource) ? "(unspecified)" : orbitSource,
                        string.IsNullOrEmpty(orbitSourceDetail) ? string.Empty : " " + orbitSourceDetail,
                        driverState,
                        v.mapObject != null, v.orbitRenderer != null,
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
