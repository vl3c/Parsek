using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.Rendering
{
    /// <summary>
    /// Phase 8 tuneable thresholds (design doc §14.1, §22.1, plan §1).
    /// Pure-data POCO. Encoded into the <c>.pann</c> ConfigurationHash so any
    /// change here invalidates every cached <c>.pann</c> via
    /// <c>config-hash-drift</c> (HR-10).
    /// </summary>
    /// <remarks>
    /// Per-environment acceleration ceilings (m/s²) reflect what KSP physics
    /// CAN realistically produce in normal play; values above are physics-
    /// glitch ("kraken") events. See plan §1.2 for the per-environment
    /// rationale. Thresholds are intentionally generous — false positives
    /// (rejecting a real sample) are far worse than false negatives because
    /// the spline still has the rest of the section's samples to work from.
    /// </remarks>
    internal struct OutlierThresholds
    {
        public float AccelCeilingAtmospheric;        // m/s², default 500
        public float AccelCeilingExoPropulsive;      // m/s², default 200
        public float AccelCeilingExoBallistic;       // m/s², default 50
        public float AccelCeilingSurfaceMobile;      // m/s², default 30
        public float AccelCeilingSurfaceStationary;  // m/s², default 10
        public float AccelCeilingApproach;           // m/s², default 50

        /// <summary>KSP physics-bubble radius (~2500 m). Single-tick position
        /// deltas exceeding this are kraken teleports.</summary>
        public float MaxSingleTickPositionDeltaMeters;

        /// <summary>Lower altitude bound (m). Default -100 to accommodate
        /// terrain-mesh shifts between sessions.</summary>
        public float AltitudeFloorMeters;

        /// <summary>Added to <c>body.sphereOfInfluence</c> for the upper
        /// altitude bound. Default 1000 m.</summary>
        public float AltitudeCeilingMargin;

        /// <summary>Section-wide cluster threshold; if rejected/total exceeds
        /// this, the section's classifierMask gets the Cluster bit and the
        /// section is logged Warn as low-fidelity. Default 0.20 (20%).</summary>
        public float ClusterRateThreshold;

        internal static OutlierThresholds Default => new OutlierThresholds
        {
            AccelCeilingAtmospheric = 500.0f,
            AccelCeilingExoPropulsive = 200.0f,
            AccelCeilingExoBallistic = 50.0f,
            AccelCeilingSurfaceMobile = 30.0f,
            AccelCeilingSurfaceStationary = 10.0f,
            AccelCeilingApproach = 50.0f,
            MaxSingleTickPositionDeltaMeters = 2500.0f,
            AltitudeFloorMeters = -100.0f,
            AltitudeCeilingMargin = 1000.0f,
            ClusterRateThreshold = 0.20f,
        };

        internal float AccelCeilingForEnvironment(SegmentEnvironment env)
        {
            switch (env)
            {
                case SegmentEnvironment.Atmospheric: return AccelCeilingAtmospheric;
                case SegmentEnvironment.ExoPropulsive: return AccelCeilingExoPropulsive;
                case SegmentEnvironment.ExoBallistic: return AccelCeilingExoBallistic;
                case SegmentEnvironment.SurfaceMobile: return AccelCeilingSurfaceMobile;
                case SegmentEnvironment.SurfaceStationary: return AccelCeilingSurfaceStationary;
                case SegmentEnvironment.Approach: return AccelCeilingApproach;
                default: return AccelCeilingAtmospheric;
            }
        }
    }

    /// <summary>
    /// Phase 8 outlier classifier (design doc §14, §18 Phase 8, §19.2 Outlier
    /// Rejection rows). Pure static, deterministic (HR-3). Reads only —
    /// never writes <c>Recording</c> / <c>TrackSection</c> data (HR-1).
    ///
    /// <para>
    /// Dispatch by <see cref="TrackSection.environment"/> (HR-7). Per-sample
    /// classifiers (Acceleration, BubbleRadius) skip endpoints where a
    /// neighbour delta would be one-sided. Altitude classifier applies at
    /// every sample including endpoints. RELATIVE / OrbitalCheckpoint
    /// sections short-circuit to an empty result (those frames don't carry
    /// body-fixed lat/lon/alt — see <c>.claude/CLAUDE.md</c> "Rotation /
    /// world frame" notes).
    /// </para>
    /// </summary>
    internal static class OutlierClassifier
    {
        [Flags]
        internal enum ClassifierBit : byte
        {
            None = 0,
            Acceleration = 1 << 0,
            BubbleRadius = 1 << 1,
            AltitudeOutOfRange = 1 << 2,
            Cluster = 1 << 3, // section-wide, never per-sample
        }

        /// <summary>Cap on the number of per-sample Verbose log lines emitted
        /// for one section. Sections with more rejections than this emit one
        /// summary "log capped" Verbose line and stop emitting per-sample
        /// detail.</summary>
        private const int PerSampleVerboseCap = 50;

        /// <summary>
        /// Classify one section's samples. Returns
        /// <c>OutlierFlags.Empty(...)</c> for ineligible sections (RELATIVE /
        /// OrbitalCheckpoint / null frames). Always emits a Pipeline-Outlier
        /// per-section summary Info line so HR-9 visibility holds even when
        /// no rejections fire.
        /// </summary>
        internal static OutlierFlags Classify(
            Recording rec,
            int sectionIndex,
            OutlierThresholds thresholds,
            Func<string, CelestialBody> bodyResolver = null)
        {
            if (rec == null || rec.TrackSections == null
                || sectionIndex < 0 || sectionIndex >= rec.TrackSections.Count)
            {
                return new OutlierFlags
                {
                    SectionIndex = sectionIndex,
                    ClassifierMask = 0,
                    PackedBitmap = new byte[0],
                    RejectedCount = 0,
                    SampleCount = 0,
                };
            }

            TrackSection section = rec.TrackSections[sectionIndex];
            string recordingId = rec.RecordingId ?? string.Empty;

            // HR-7 short-circuit: RELATIVE sections store metre-offsets in
            // latitude/longitude/altitude (not body-fixed lat/lon/alt — see
            // .claude/CLAUDE.md "Rotation / world frame"); OrbitalCheckpoint
            // has no `frames`. Both return an empty result without emitting
            // the per-section Info summary — the classifier did not actually
            // run on these sections (all classifiers short-circuited), so a
            // misleading "rejectedCount=0" line would suggest the section
            // was inspected when it wasn't. Production never reaches this
            // path because SmoothingPipeline.ShouldFitSection already gates
            // RELATIVE / OrbitalCheckpoint out before Classify is called.
            if (section.referenceFrame != ReferenceFrame.Absolute
                || section.frames == null
                || section.frames.Count == 0)
            {
                int sampleCt = section.frames?.Count ?? 0;
                return new OutlierFlags
                {
                    SectionIndex = sectionIndex,
                    ClassifierMask = 0,
                    PackedBitmap = new byte[0],
                    RejectedCount = 0,
                    SampleCount = sampleCt,
                };
            }

            int count = section.frames.Count;
            bool[] rejected = new bool[count];
            int accelCount = 0;
            int bubbleCount = 0;
            int altCount = 0;
            int verboseEmitted = 0;
            int verboseSuppressed = 0;

            float accelCeiling = thresholds.AccelCeilingForEnvironment(section.environment);
            float bubbleCap = thresholds.MaxSingleTickPositionDeltaMeters;
            float altFloor = thresholds.AltitudeFloorMeters;
            float altMargin = thresholds.AltitudeCeilingMargin;

            // Precache body for altitude classifier (per-section, not per-sample).
            // Use the first sample's bodyName as the section's body — sections
            // span one body in practice (body changes terminate the section).
            string sectionBodyName = section.frames[0].bodyName;
            CelestialBody body = null;
            if (bodyResolver != null && !string.IsNullOrEmpty(sectionBodyName))
            {
                try
                {
                    body = bodyResolver(sectionBodyName);
                }
                catch (Exception ex)
                {
                    // HR-9 visibility: surface the swallowed exception so a
                    // degenerate FlightGlobals state (e.g. mid-load Bodies
                    // mutation) is diagnosable from KSP.log instead of
                    // silently skipping the altitude classifier for the
                    // whole section. Rate-limited so a save with many bad
                    // bodyNames cannot spam. Mirrors SmoothingPipeline.ResolveBody.
                    ParsekLog.VerboseRateLimited("Pipeline-Outlier",
                        "classifier-body-resolve-exception",
                        string.Format(CultureInfo.InvariantCulture,
                            "body resolver threw for {0}: {1}",
                            sectionBodyName, ex.GetType().Name),
                        5.0);
                    body = null;
                }
            }

            // Per-sample loop. Endpoints (i==0, i==count-1) skip delta-based
            // classifiers — see plan §2.2 for rationale (one-sided delta would
            // double false-positive probability).
            for (int i = 0; i < count; i++)
            {
                TrajectoryPoint p = section.frames[i];
                bool isFirst = i == 0;
                bool isLast = i == count - 1;

                // -- Altitude (always applies, including endpoints) --
                // Plan §1.4: when sphereOfInfluence can't be resolved (test
                // harness with uninitialised CelestialBody, FlightGlobals
                // mid-load mutation), the upper bound is a no-op. The lower
                // bound (AltitudeFloor) still applies because it does not
                // depend on the body's SOI.
                if (!object.ReferenceEquals(body, null))
                {
                    double soi = body.sphereOfInfluence;
                    bool soiAvailable = IsFiniteDouble(soi) && soi > 0;
                    bool altOOR = false;
                    double effectiveCeiling = soiAvailable ? soi + altMargin : double.PositiveInfinity;
                    if (p.altitude < altFloor) altOOR = true;
                    else if (soiAvailable && p.altitude > effectiveCeiling)
                        altOOR = true;
                    if (altOOR)
                    {
                        if (!rejected[i]) { rejected[i] = true; }
                        altCount++;
                        EmitPerSampleVerbose(recordingId, sectionIndex, i,
                            ClassifierBit.AltitudeOutOfRange,
                            p.altitude, effectiveCeiling,
                            ref verboseEmitted, ref verboseSuppressed);
                    }
                }

                if (isFirst || isLast) continue;

                TrajectoryPoint prev = section.frames[i - 1];
                double dt = p.ut - prev.ut;
                if (dt <= 0)
                {
                    // Non-monotonic UT — skip delta-based tests for this sample.
                    // The spline pre-fit will reject the section separately if
                    // this propagates; we do not double-flag.
                    continue;
                }

                // -- Acceleration --
                double accel = ComputeAccelerationMagnitude(prev, p, body);
                if (IsFiniteDouble(accel) && accel > accelCeiling)
                {
                    if (!rejected[i])
                    {
                        rejected[i] = true;
                    }
                    accelCount++;
                    EmitPerSampleVerbose(recordingId, sectionIndex, i,
                        ClassifierBit.Acceleration, accel, accelCeiling,
                        ref verboseEmitted, ref verboseSuppressed);
                }

                // -- Bubble-radius (single-tick position delta) --
                double posDelta = ComputePositionDeltaMagnitude(prev, p, body);
                if (IsFiniteDouble(posDelta) && posDelta > bubbleCap)
                {
                    if (!rejected[i])
                    {
                        rejected[i] = true;
                    }
                    bubbleCount++;
                    EmitPerSampleVerbose(recordingId, sectionIndex, i,
                        ClassifierBit.BubbleRadius, posDelta, bubbleCap,
                        ref verboseEmitted, ref verboseSuppressed);
                }
            }

            // Aggregate.
            byte[] packed = OutlierFlags.BuildPackedBitmap(rejected);
            int rejectedTotal = 0;
            for (int i = 0; i < rejected.Length; i++) if (rejected[i]) rejectedTotal++;

            byte mask = 0;
            if (accelCount > 0) mask |= (byte)ClassifierBit.Acceleration;
            if (bubbleCount > 0) mask |= (byte)ClassifierBit.BubbleRadius;
            if (altCount > 0) mask |= (byte)ClassifierBit.AltitudeOutOfRange;

            // Cluster check (§14.3). Even one rejected sample participates;
            // we only set the Cluster bit when the rate is over threshold.
            bool cluster = false;
            if (rejectedTotal > 0 && count > 0)
            {
                double rate = (double)rejectedTotal / count;
                if (rate > thresholds.ClusterRateThreshold)
                {
                    mask |= (byte)ClassifierBit.Cluster;
                    cluster = true;
                }
            }

            // Verbose-cap follow-up summary line if we suppressed any.
            if (verboseSuppressed > 0)
            {
                ParsekLog.Verbose("Pipeline-Outlier", string.Format(CultureInfo.InvariantCulture,
                    "sample-reject log capped: recordingId={0} sectionIndex={1} additionalRejections={2} (showing first {3})",
                    recordingId, sectionIndex, verboseSuppressed, PerSampleVerboseCap));
            }

            // Per-section Info summary, always emitted (HR-9 visibility).
            EmitPerSectionSummary(recordingId, sectionIndex, section.environment,
                count, rejectedTotal, accelCount, bubbleCount, altCount, cluster, thresholds);

            return new OutlierFlags
            {
                SectionIndex = sectionIndex,
                ClassifierMask = mask,
                PackedBitmap = packed,
                RejectedCount = rejectedTotal,
                SampleCount = count,
            };
        }

        // ----- helpers -----

        private static double ComputeAccelerationMagnitude(
            TrajectoryPoint prev, TrajectoryPoint cur, CelestialBody body)
        {
            double dt = cur.ut - prev.ut;
            if (dt <= 0) return double.NaN;

            // Prefer KSP-captured velocities when available. The TrajectoryPoint
            // velocity field defaults to Vector3.zero; treat exact zero on
            // BOTH samples as "not available" and skip the acceleration test
            // for that sample. The position 2nd-derivative fallback (|Δp|/Δt²)
            // is too aggressive for typical orbital trajectories (a 1-second
            // sample of a 2 km/s LKO ghost gives ~2000 m/s² which trips every
            // environment ceiling), so we let the bubble-radius classifier
            // catch single-tick teleports independently and only flag
            // acceleration when velocity data is recorded. Plan §12 risk
            // ("4× sensitivity") is sidestepped here: rather than
            // accept-with-noise, we skip the test outright when velocity is
            // unavailable. Production recordings populate velocity from
            // KSP's frame; the synthetic test fixtures do not, so this
            // keeps Phase-1 / Phase-4 / Phase-5 / Phase-6 tests green
            // while still classifying real recordings.
            bool prevVelAvail = !VectorIsZero(prev.velocity);
            bool curVelAvail = !VectorIsZero(cur.velocity);
            if (prevVelAvail && curVelAvail)
            {
                Vector3 dv = cur.velocity - prev.velocity;
                double mag = dv.magnitude;
                return mag / dt;
            }
            return double.NaN; // unavailable; caller skips the test.
        }

        private static double ComputePositionDeltaMagnitude(
            TrajectoryPoint prev, TrajectoryPoint cur, CelestialBody body)
        {
            // Haversine great-circle distance in metres + altitude delta.
            // Plan §1.3 specifies a single-tick position-delta cap; the
            // earlier flat-earth approximation (Δlat × R, Δlon × R × cos(meanLat))
            // collapsed at the poles where cos(meanLat) → 0, letting a polar
            // longitude flip register near-zero horizontal distance.
            // Haversine is singularity-free across the full sphere.
            //
            // Body radius is the arc-length scale; falls back to ~Kerbin
            // (~600 km equivalent OOM) when body is missing (test harnesses
            // without a CelestialBody) so a gross kraken teleport still
            // trips the bubble-radius cap.
            double radius = (!object.ReferenceEquals(body, null) && body.Radius > 0)
                ? body.Radius
                : 600_000.0; // Kerbin-OOM fallback for test harnesses.
            double dLatRad = (cur.latitude - prev.latitude) * Math.PI / 180.0;
            double dLonRad = (cur.longitude - prev.longitude) * Math.PI / 180.0;
            double prevLatRad = prev.latitude * Math.PI / 180.0;
            double curLatRad = cur.latitude * Math.PI / 180.0;
            double sinHalfDLat = Math.Sin(dLatRad / 2.0);
            double sinHalfDLon = Math.Sin(dLonRad / 2.0);
            double a = sinHalfDLat * sinHalfDLat
                + Math.Cos(prevLatRad) * Math.Cos(curLatRad) * sinHalfDLon * sinHalfDLon;
            // Clamp `a` to [0, 1] to keep Asin numerically stable against
            // tiny FP overshoot.
            if (a < 0.0) a = 0.0; else if (a > 1.0) a = 1.0;
            double horizontalM = 2.0 * radius * Math.Asin(Math.Sqrt(a));
            double dAltM = cur.altitude - prev.altitude;
            return Math.Sqrt(horizontalM * horizontalM + dAltM * dAltM);
        }

        private static bool VectorIsZero(Vector3 v)
        {
            return v.x == 0f && v.y == 0f && v.z == 0f;
        }

        private static bool IsFiniteDouble(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private static void EmitPerSampleVerbose(
            string recordingId, int sectionIndex, int sampleIndex,
            ClassifierBit bit, double value, double threshold,
            ref int verboseEmitted, ref int verboseSuppressed)
        {
            if (verboseEmitted >= PerSampleVerboseCap)
            {
                verboseSuppressed++;
                return;
            }
            verboseEmitted++;
            ParsekLog.Verbose("Pipeline-Outlier", string.Format(CultureInfo.InvariantCulture,
                "Sample rejected: recordingId={0} sectionIndex={1} sampleIndex={2} classifier={3} value={4:F2} threshold={5:F2}",
                recordingId, sectionIndex, sampleIndex, bit, value, threshold));
        }

        private static void EmitPerSectionSummary(
            string recordingId, int sectionIndex, SegmentEnvironment env,
            int sampleCount, int rejectedCount, int accelCount, int bubbleCount, int altCount,
            bool cluster, OutlierThresholds thresholds)
        {
            ParsekLog.Info("Pipeline-Outlier", string.Format(CultureInfo.InvariantCulture,
                "Per-section rejection summary: recordingId={0} sectionIndex={1} env={2} sampleCount={3} rejectedCount={4} accel={5} bubble={6} altitude={7} cluster={8}",
                recordingId, sectionIndex, env, sampleCount, rejectedCount,
                accelCount, bubbleCount, altCount, cluster ? "true" : "false"));
        }
    }
}
