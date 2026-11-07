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

            TerrainCorrector.TailLiftPlan plan = ParsekFlightGhostPositioner.ResolveTailLiftPlan(recordingId, body);
            return phase7 + TerrainCorrector.EvaluateTailLift(pointUT, in plan);
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
