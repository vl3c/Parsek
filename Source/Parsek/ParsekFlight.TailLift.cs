using System;
using System.Globalization;

namespace Parsek
{
    public partial class ParsekFlight
    {
        internal static void InvalidateTailLiftPlanCache()
        {
            s_tailLiftPlanCache.Clear();
            s_tailLiftPlanTerminalBodyCache.Clear();
        }

        internal static void RegisterPreviewRecordingForTailLift(Recording rec)
        {
            s_previewRecordingForTailLift = rec;
            // The preview source has changed identity; drop any stale plan cached
            // under the same recordingId from a prior preview (or stale lookup).
            InvalidateTailLiftPlanCache();
        }

        internal static void ClearPreviewRecordingForTailLift()
        {
            if (s_previewRecordingForTailLift == null) return;
            s_previewRecordingForTailLift = null;
            InvalidateTailLiftPlanCache();
        }

        private static Recording ResolveRecordingForTailLift(string recordingId)
        {
            Recording committed = ResolveRecordingById(recordingId);
            if (committed != null) return committed;

            Recording preview = s_previewRecordingForTailLift;
            if (preview != null
                && !string.IsNullOrEmpty(preview.RecordingId)
                && string.Equals(preview.RecordingId, recordingId, StringComparison.Ordinal))
            {
                return preview;
            }
            return null;
        }

        internal static double ResolveEffectiveAltitudeWithTailLift(
            CelestialBody body, double latitude, double longitude,
            double recordedAltitude, double recordedGroundClearance,
            ReferenceFrame referenceFrame, double pointUT, string recordingId)
        {
            double phase7 = ResolvePhase7EffectiveAltitude(
                body, latitude, longitude, recordedAltitude,
                recordedGroundClearance, referenceFrame);

            // Phase 7 owns finite-clearance points. Tail-lift only fills the
            // atmospheric/legacy NaN-clearance gap on Absolute-frame payloads.
            if (referenceFrame != ReferenceFrame.Absolute)
                return phase7;
            if (!double.IsNaN(recordedGroundClearance))
                return phase7;
            if (string.IsNullOrEmpty(recordingId))
                return phase7;

            TerrainCorrector.TailLiftPlan plan = ResolveTailLiftPlan(recordingId, body);
            return phase7 + TerrainCorrector.EvaluateTailLift(pointUT, in plan);
        }

        private static TerrainCorrector.TailLiftPlan ResolveTailLiftPlan(
            string recordingId, CelestialBody body)
        {
            if (string.IsNullOrEmpty(recordingId))
                return TerrainCorrector.TailLiftPlan.Inactive;

            TerrainCorrector.TailLiftPlan cached;
            string cachedTerminalBodyName;
            if (s_tailLiftPlanCache.TryGetValue(recordingId, out cached))
            {
                if (!s_tailLiftPlanTerminalBodyCache.TryGetValue(recordingId, out cachedTerminalBodyName)
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
                s_tailLiftPlanCache[recordingId] = plan;
                s_tailLiftPlanTerminalBodyCache[recordingId] = terminalBodyName;
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

            s_tailLiftPlanCache[recordingId] = TerrainCorrector.TailLiftPlan.Inactive;
            if (!string.IsNullOrEmpty(terminalBodyName))
                s_tailLiftPlanTerminalBodyCache[recordingId] = terminalBodyName;
        }

        private static bool IsTransientTailLiftInactiveReason(string reason)
        {
            return string.Equals(reason, "recording-missing", StringComparison.Ordinal)
                || string.Equals(reason, "body-missing", StringComparison.Ordinal)
                || string.Equals(reason, "terminal-body-missing", StringComparison.Ordinal)
                || string.Equals(reason, "current-body-not-terminal", StringComparison.Ordinal)
                || string.Equals(reason, "current-terrain-nan", StringComparison.Ordinal);
        }

        private static string ResolveTailLiftTerminalBodyName(Recording rec, TrajectoryPoint lastPt)
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
                CelestialBody resolved = FlightGlobals.Bodies.Find(b =>
                    !object.ReferenceEquals(b, null)
                    && (string.Equals(b.bodyName, terminalBodyName, StringComparison.Ordinal)
                        || string.Equals(b.name, terminalBodyName, StringComparison.Ordinal)));
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

        private static bool TailLiftTerminalPointIsAbsolute(Recording rec, TrajectoryPoint lastPt)
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
            if (rec == null)
                return "recording-missing";
            if (rec.Points == null || rec.Points.Count == 0)
                return "no-points";
            if (object.ReferenceEquals(body, null))
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

        /// <summary>
        /// Phase 7 (design doc §13.1, §18 Phase 7) — pure helper: given a
        /// recorded altitude and a recordedGroundClearance, returns the
        /// altitude to render at. When clearance is NaN (legacy point or
        /// non-surface environment), returns the recorded altitude
        /// unchanged (HR-9 silent fall-through). When clearance is finite,
        /// looks up the current terrain height at lat/lon via
        /// <see cref="Parsek.Rendering.TerrainCacheBuckets"/> and returns
        /// <c>terrain + clearance</c>. If the cache returns NaN (PQS
        /// unspun, body missing) we fall back to the recorded altitude rather
        /// than producing an invalid world position.
        ///
        /// <para>The <paramref name="referenceFrame"/> parameter is a
        /// **safety gate** (review pass P2-1): when the section is
        /// <see cref="ReferenceFrame.Relative"/> (anchor-local metres in
        /// lat/lon/alt — see CLAUDE.md "Rotation / world frame") or
        /// <see cref="ReferenceFrame.OrbitalCheckpoint"/>, the helper
        /// short-circuits to <paramref name="recordedAltitude"/> regardless
        /// of clearance. Today's recorder never writes a finite clearance on
        /// a non-Absolute point, so this path is unreachable in practice —
        /// but a future codec / optimizer / merge that surfaced a finite
        /// clearance on a RELATIVE point would otherwise interpret metre-
        /// scale lat/lon as degrees and produce a position deep inside the
        /// planet. Pin the contract at the renderer.</para>
        /// </summary>
        internal static double ResolvePhase7EffectiveAltitude(
            CelestialBody body, double latitude, double longitude,
            double recordedAltitude, double recordedGroundClearance,
            ReferenceFrame referenceFrame)
        {
            if (double.IsNaN(recordedGroundClearance))
                return recordedAltitude;
            if (referenceFrame != ReferenceFrame.Absolute)
            {
                // P2-1: defensive short-circuit. A RELATIVE-frame point
                // stores anchor-local metres in latitude/longitude/altitude;
                // applying terrain correction would mix metres with degrees
                // catastrophically. OrbitalCheckpoint sections have no
                // per-point latitude/longitude/altitude payload either.
                ParsekLog.VerboseRateLimited("Pipeline-Terrain",
                    "non-absolute-frame-skip",
                    $"ResolvePhase7EffectiveAltitude: skipping terrain correction " +
                    $"for non-Absolute frame={referenceFrame} " +
                    $"clearance={recordedGroundClearance.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"— returning recorded altitude " +
                    $"{recordedAltitude.ToString("F2", CultureInfo.InvariantCulture)}m " +
                    $"(future-codec safety gate; today's recorder never writes " +
                    $"finite clearance on non-Absolute points)",
                    5.0);
                return recordedAltitude;
            }
            if (object.ReferenceEquals(body, null))
                return recordedAltitude;

            double currentTerrain = Parsek.Rendering.TerrainCacheBuckets.GetCachedSurfaceHeight(
                body, latitude, longitude);
            if (double.IsNaN(currentTerrain))
            {
                // Cache returned NaN (PQS not spun, body has no controller).
                // HR-9 fall-through to recorded altitude — better one frame
                // of altitude pop than clipping into terrain (design §13.3).
                ParsekLog.VerboseRateLimited("Pipeline-Terrain",
                    "effective-altitude-pqs-miss",
                    $"ResolvePhase7EffectiveAltitude: terrain NaN at " +
                    $"body={body.bodyName} lat={latitude.ToString("F6", CultureInfo.InvariantCulture)} " +
                    $"lon={longitude.ToString("F6", CultureInfo.InvariantCulture)} " +
                    $"— falling back to recorded altitude " +
                    $"{recordedAltitude.ToString("F2", CultureInfo.InvariantCulture)}m",
                    5.0);
                return recordedAltitude;
            }

            return currentTerrain + recordedGroundClearance;
        }
    }
}
