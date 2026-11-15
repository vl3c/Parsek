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
    internal static partial class GhostMapPresence
    {
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
            // The counter collect + message format run per tick BEFORE the rate-limiter decides
            // to emit; with verbose off they are pure waste — and the flight tick now runs up to
            // once per frame at extreme warp (the warp-scaled reseed cadence). Guarded like the
            // seam CoMD summary (see the IsVerboseEnabled precedent in the state-vector reseed).
            // The per-tick counter reset stays unconditional: it is the tick contract, not logging.
            if (ParsekLog.IsVerboseEnabled)
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
            }
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
    }
}
