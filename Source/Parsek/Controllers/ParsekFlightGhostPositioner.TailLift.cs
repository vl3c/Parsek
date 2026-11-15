using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // The tail-lift plan and its recording lookup.
    internal sealed partial class ParsekFlightGhostPositioner
    {
        /// <summary>
        /// Resolves a recording by id without driving the active scene's
        /// playback context. Walks <see cref="RecordingStore.CommittedRecordings"/>
        /// directly so the preview-source tail-lift resolver can locate a
        /// recording outside the active playback scope.
        /// </summary>
        private static Recording ResolveRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            try
            {
                IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
                if (committed != null)
                {
                    for (int i = 0; i < committed.Count; i++)
                    {
                        Recording r = committed[i];
                        if (r == null) continue;
                        if (string.Equals(r.RecordingId, recordingId, StringComparison.Ordinal))
                            return r;
                    }
                }
            }
            catch
            {
                // Mid-load mutation; treat as missing.
            }
            return null;
        }

        private static Recording ResolveRecordingForTailLift(string recordingId)
        {
            Recording committed = ResolveRecordingById(recordingId);
            if (committed != null) return committed;

            Recording preview = ParsekFlight.s_previewRecordingForTailLift;
            if (preview != null
                && !string.IsNullOrEmpty(preview.RecordingId)
                && string.Equals(preview.RecordingId, recordingId, StringComparison.Ordinal))
            {
                return preview;
            }
            return null;
        }

        internal static TerrainCorrector.TailLiftPlan ResolveTailLiftPlan(
            string recordingId, CelestialBody body)
        {
            if (string.IsNullOrEmpty(recordingId))
                return TerrainCorrector.TailLiftPlan.Inactive;

            TerrainCorrector.TailLiftPlan cached;
            string cachedTerminalBodyName;
            if (ParsekFlight.s_tailLiftPlanCache.TryGetValue(recordingId, out cached))
            {
                if (!ParsekFlight.s_tailLiftPlanTerminalBodyCache.TryGetValue(recordingId, out cachedTerminalBodyName)
                    || TailLiftBodyMatches(body, cachedTerminalBodyName))
                {
                    return cached;
                }

                return TerrainCorrector.TailLiftPlan.Inactive;
            }

            Recording rec = ResolveRecordingForTailLift(recordingId);
            if (rec == null || rec.Points == null || rec.Points.Count == 0)
            {
                string reason = ResolveTailLiftInactiveReason(rec, body, double.NaN);
                CacheInactiveTailLiftPlanIfStable(recordingId, null, reason);
                LogTailLiftInactive(recordingId, rec, reason,
                    double.NaN, double.NaN);
                return TerrainCorrector.TailLiftPlan.Inactive;
            }

            TrajectoryPoint lastPt = rec.Points[rec.Points.Count - 1];
            string terminalBodyName = ResolveTailLiftTerminalBodyName(rec, lastPt);
            if (object.ReferenceEquals(body, null) || string.IsNullOrEmpty(terminalBodyName))
            {
                string reason = object.ReferenceEquals(body, null) ? "body-missing" : "terminal-body-missing";
                CacheInactiveTailLiftPlanIfStable(recordingId, terminalBodyName, reason);
                LogTailLiftInactive(recordingId, rec, reason, double.NaN, double.NaN);
                return TerrainCorrector.TailLiftPlan.Inactive;
            }

            if (!TailLiftBodyMatches(body, terminalBodyName))
            {
                LogTailLiftInactive(recordingId, rec, "current-body-not-terminal",
                    double.NaN, double.NaN);
                return TerrainCorrector.TailLiftPlan.Inactive;
            }

            if (!TailLiftTerminalPointIsAbsolute(rec, lastPt))
            {
                const string reason = "terminal-non-absolute-frame";
                CacheInactiveTailLiftPlanIfStable(recordingId, terminalBodyName, reason);
                LogTailLiftInactive(recordingId, rec, reason, double.NaN, double.NaN);
                return TerrainCorrector.TailLiftPlan.Inactive;
            }

            CelestialBody terminalBody = ResolveTailLiftTerminalBody(terminalBodyName, body);
            if (object.ReferenceEquals(terminalBody, null))
            {
                const string reason = "terminal-body-missing";
                CacheInactiveTailLiftPlanIfStable(recordingId, terminalBodyName, reason);
                LogTailLiftInactive(recordingId, rec, reason, double.NaN, double.NaN);
                return TerrainCorrector.TailLiftPlan.Inactive;
            }

            double currentTerrain = Parsek.Rendering.TerrainCacheBuckets.GetCachedSurfaceHeight(
                terminalBody, lastPt.latitude, lastPt.longitude);
            TerrainCorrector.TailLiftPlan plan = TerrainCorrector.BuildTailLiftPlan(
                rec.TerminalStateValue,
                rec.TerrainHeightAtEnd,
                currentTerrain,
                lastPt.ut,
                TerrainCorrector.DefaultTailLiftRampSeconds,
                TerrainCorrector.TailLiftMinAbsDeltaMeters);

            if (plan.Active)
            {
                ParsekFlight.s_tailLiftPlanCache[recordingId] = plan;
                ParsekFlight.s_tailLiftPlanTerminalBodyCache[recordingId] = terminalBodyName;
                LogTailLiftActive(recordingId, rec, plan, currentTerrain);
            }
            else
            {
                string reason = ResolveTailLiftInactiveReason(rec, terminalBody, currentTerrain);
                CacheInactiveTailLiftPlanIfStable(recordingId, terminalBodyName, reason);
                LogTailLiftInactive(recordingId, rec, reason,
                    currentTerrain,
                    currentTerrain - rec.TerrainHeightAtEnd);
            }
            return plan;
        }

        private static void CacheInactiveTailLiftPlanIfStable(
            string recordingId, string terminalBodyName, string reason)
        {
            if (string.IsNullOrEmpty(recordingId) || IsTransientTailLiftInactiveReason(reason))
                return;

            ParsekFlight.s_tailLiftPlanCache[recordingId] = TerrainCorrector.TailLiftPlan.Inactive;
            if (!string.IsNullOrEmpty(terminalBodyName))
                ParsekFlight.s_tailLiftPlanTerminalBodyCache[recordingId] = terminalBodyName;
        }

        internal static bool IsTransientTailLiftInactiveReason(string reason)
        {
            return string.Equals(reason, "recording-missing", StringComparison.Ordinal)
                || string.Equals(reason, "body-missing", StringComparison.Ordinal)
                || string.Equals(reason, "terminal-body-missing", StringComparison.Ordinal)
                || string.Equals(reason, "current-body-not-terminal", StringComparison.Ordinal)
                || string.Equals(reason, "current-terrain-nan", StringComparison.Ordinal);
        }

        internal static string ResolveTailLiftTerminalBodyName(Recording rec, TrajectoryPoint lastPt)
        {
            string terminalBodyName;
            if (RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out terminalBodyName)
                && !string.IsNullOrEmpty(terminalBodyName))
            {
                return terminalBodyName;
            }

            return !string.IsNullOrEmpty(lastPt.bodyName) ? lastPt.bodyName : null;
        }

        private static CelestialBody ResolveTailLiftTerminalBody(
            string terminalBodyName, CelestialBody fallbackBody)
        {
            if (!string.IsNullOrEmpty(terminalBodyName)
                && TailLiftBodyMatches(fallbackBody, terminalBodyName))
            {
                return fallbackBody;
            }

            if (!string.IsNullOrEmpty(terminalBodyName) && FlightGlobals.Bodies != null)
            {
                CelestialBody resolved = FindFirstMatching(
                    FlightGlobals.Bodies, terminalBodyName, TailLiftTerminalBodyNameMatches);
                if (!object.ReferenceEquals(resolved, null))
                    return resolved;
            }

            return null;
        }

        private static bool TailLiftBodyMatches(CelestialBody body, string bodyName)
        {
            return !object.ReferenceEquals(body, null)
                && !string.IsNullOrEmpty(bodyName)
                && string.Equals(body.bodyName, bodyName, StringComparison.Ordinal);
        }

        internal static bool TailLiftTerminalPointIsAbsolute(Recording rec, TrajectoryPoint lastPt)
        {
            if (rec == null || rec.TrackSections == null || rec.TrackSections.Count == 0)
                return true;

            int sectionIndex = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, lastPt.ut);
            if (sectionIndex < 0)
                return true;

            return rec.TrackSections[sectionIndex].referenceFrame == ReferenceFrame.Absolute;
        }

        private static string ResolveTailLiftInactiveReason(
            Recording rec, CelestialBody body, double currentTerrain)
        {
            return ResolveTailLiftInactiveReasonCore(
                rec, !object.ReferenceEquals(body, null), currentTerrain);
        }

        /// <summary>
        /// Names why a recording's tail lift is inactive, checked in order: the recording, its
        /// points, the body, the terminal state (only a surface terminal lifts), the recorded and
        /// current terrain heights, then the minimum delta.
        /// </summary>
        internal static string ResolveTailLiftInactiveReasonCore(
            Recording rec, bool hasBody, double currentTerrain)
        {
            if (rec == null)
                return "recording-missing";
            if (rec.Points == null || rec.Points.Count == 0)
                return "no-points";
            if (!hasBody)
                return "body-missing";
            if (!rec.TerminalStateValue.HasValue)
                return "terminal-missing";

            TerminalState terminal = rec.TerminalStateValue.Value;
            if (terminal != TerminalState.Landed
                && terminal != TerminalState.Splashed
                && terminal != TerminalState.Recovered)
                return "non-surface-terminal";
            if (double.IsNaN(rec.TerrainHeightAtEnd))
                return "recorded-terrain-nan";
            if (double.IsNaN(currentTerrain))
                return "current-terrain-nan";

            double delta = currentTerrain - rec.TerrainHeightAtEnd;
            if (System.Math.Abs(delta) < TerrainCorrector.TailLiftMinAbsDeltaMeters)
                return "delta-below-threshold";
            return "inactive";
        }

        private static void LogTailLiftActive(
            string recordingId, Recording rec,
            TerrainCorrector.TailLiftPlan plan, double currentTerrain)
        {
            string vesselName = rec != null && !string.IsNullOrEmpty(rec.VesselName)
                ? rec.VesselName
                : "?";
            double rampSeconds = plan.TerminalUT - plan.RampStartUT;
            ParsekLog.Verbose("Pipeline-Terrain",
                string.Format(CultureInfo.InvariantCulture,
                    "TailLift active: rec={0} vessel='{1}' delta={2}m terminalUT={3:R} " +
                    "rampSec={4:F1} (recTerrain={5:F1} curTerrain={6:F1})",
                    recordingId,
                    vesselName,
                    plan.TerrainDelta.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture),
                    plan.TerminalUT,
                    rampSeconds,
                    rec != null ? rec.TerrainHeightAtEnd : double.NaN,
                    currentTerrain));
        }

        private static void LogTailLiftInactive(
            string recordingId, Recording rec, string reason,
            double currentTerrain, double delta)
        {
            string vesselName = rec != null && !string.IsNullOrEmpty(rec.VesselName)
                ? rec.VesselName
                : "?";
            string key = "tail-lift-inactive-" + recordingId + "-" + reason;
            ParsekLog.VerboseRateLimited("Pipeline-Terrain",
                key,
                string.Format(CultureInfo.InvariantCulture,
                    "TailLift inactive: rec={0} vessel='{1}' reason={2} delta={3}m " +
                    "(recTerrain={4:F1} curTerrain={5:F1})",
                    recordingId,
                    vesselName,
                    reason,
                    delta.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture),
                    rec != null ? rec.TerrainHeightAtEnd : double.NaN,
                    currentTerrain),
                30.0);
        }
    }
}
