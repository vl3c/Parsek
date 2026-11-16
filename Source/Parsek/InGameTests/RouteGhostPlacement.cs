using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Pure comparison core of the <c>RouteGhostPlacement</c> in-game cell
    /// (<see cref="RouteGhostPlacementInGameTests"/>): is a supply route's ghost drawn where
    /// the route's source recording says the ship was at the loop UT the route's own delivery
    /// clock reads? The cell re-derives the expected place from the recording on its own
    /// (section lookup, bracketing samples, latitude / longitude / altitude interpolation, then
    /// stock <c>CelestialBody.GetWorldSurfacePosition</c>), so it does not share the positioner's
    /// code path, and compares it with the ghost root's rendered world position.
    ///
    /// <para>The horizontal distance is the gate: a ghost drawn on the wrong loop cycle, on
    /// the wrong body rotation, at a Relative offset read as latitude / longitude, or left
    /// behind by a floating-origin shift is metres to kilometres off, while a correct ghost
    /// differs only by float rounding and by the chord-versus-arc difference of a short
    /// segment. The vertical check is a wide window, because the renderer legitimately puts a
    /// surface ghost at the CURRENT terrain height plus the recorded ground clearance and the
    /// LateUpdate terrain clamp may lift it by up to
    /// <see cref="MaxTerrainClampClearanceMeters"/>.</para>
    ///
    /// <para>Only body-fixed surfaces are compared: an Absolute section's frames, a Relative
    /// section's recorded body-fixed shadow (<c>TrackSection.bodyFixedFrames</c>), or the flat
    /// point list of a recording with no sections. An orbital checkpoint section, a Relative
    /// section without a covering shadow, and a UT outside the samples are counted as
    /// unsupported, never failed.</para>
    /// </summary>
    internal static class RouteGhostPlacement
    {
        /// <summary>Horizontal floor in metres: sits far above float rounding and the
        /// chord-versus-arc difference of a surface-speed segment, far below any wrong
        /// placement (one loop cycle off is the ship's travel over the cadence).</summary>
        internal const double HorizontalFloorMeters = 3.0;

        /// <summary>Slack added on both sides of the vertical window: the renderer reads terrain
        /// through a bucketed cache, the cell reads the live PQS.</summary>
        internal const double VerticalSlackMeters = 10.0;

        /// <summary>The largest lift the LateUpdate terrain clamp applies
        /// (<c>DistanceThresholds.GhostFlight.ComputeTerrainClearance</c> tops out at 5 m).</summary>
        internal const double MaxTerrainClampClearanceMeters = 5.0;

        /// <summary>The route delivery clock and the engine's render clock are the same pure
        /// function over separately built units, so they agree to round-off or not at all.</summary>
        internal const double ClockEpsilonSeconds = 1e-6;

        /// <summary>Upper bound on the frame time used for the motion allowance.</summary>
        internal const double MaxFrameSeconds = 0.1;

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        internal struct BodyFixedSample
        {
            public string Body;
            public double Latitude;
            public double Longitude;
            public double Altitude;
            /// <summary>Interpolated recorded ground clearance, NaN unless both brackets carry one.</summary>
            public double Clearance;
            public double BeforeUT;
            public double AfterUT;
            public double SpeedMps;
        }

        internal struct Verdict
        {
            public bool Ok;
            public string Reason;
            public double HorizontalMeters;
            public double HorizontalToleranceMeters;
            public double VerticalDeltaMeters;
            public double VerticalMinMeters;
            public double VerticalMaxMeters;
            public double ClockDeltaSeconds;
        }

        /// <summary>
        /// Picks the body-fixed sample list that covers <paramref name="ut"/>. Returns false with
        /// the reason in <paramref name="source"/> when the recording has no body-fixed surface
        /// there. A recording with sections is never read through its flat point list (a flat
        /// list can carry Relative offsets that are not latitude / longitude).
        /// </summary>
        internal static bool TrySelectSurface(IList<TrackSection> sections,
            IList<TrajectoryPoint> flatPoints, double ut,
            out IList<TrajectoryPoint> frames, out string source)
        {
            frames = null;
            if (sections == null || sections.Count == 0)
            {
                if (flatPoints == null || flatPoints.Count < 2)
                {
                    source = "no-points";
                    return false;
                }
                frames = flatPoints;
                source = "flat-points";
                return true;
            }

            int index = FindSectionIndex(sections, ut);
            if (index < 0)
            {
                source = "no-section-at-ut";
                return false;
            }

            TrackSection section = sections[index];
            switch (section.referenceFrame)
            {
                case ReferenceFrame.Absolute:
                    if (section.frames == null || section.frames.Count < 2)
                    {
                        source = "absolute-too-few-frames";
                        return false;
                    }
                    frames = section.frames;
                    source = "absolute-frames";
                    return true;
                case ReferenceFrame.Relative:
                    if (section.bodyFixedFrames == null || section.bodyFixedFrames.Count < 2
                        || ut < section.bodyFixedFrames[0].ut
                        || ut > section.bodyFixedFrames[section.bodyFixedFrames.Count - 1].ut)
                    {
                        source = "relative-no-body-fixed";
                        return false;
                    }
                    frames = section.bodyFixedFrames;
                    source = "relative-body-fixed";
                    return true;
                case ReferenceFrame.OrbitalCheckpoint:
                    source = "orbital-checkpoint";
                    return false;
                default:
                    source = "unknown-frame";
                    return false;
            }
        }

        /// <summary>The section whose [startUT, endUT) holds <paramref name="ut"/>; the last
        /// section's end is inclusive. -1 when none does.</summary>
        internal static int FindSectionIndex(IList<TrackSection> sections, double ut)
        {
            if (sections == null) return -1;
            for (int i = 0; i < sections.Count; i++)
            {
                bool last = i == sections.Count - 1;
                if (ut >= sections[i].startUT && (last ? ut <= sections[i].endUT : ut < sections[i].endUT))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Linear interpolation of latitude, longitude (along the shorter arc), altitude and the
        /// recorded ground clearance between the two samples that bracket <paramref name="ut"/>.
        /// </summary>
        internal static bool TryInterpolate(IList<TrajectoryPoint> frames, double ut,
            out BodyFixedSample sample, out string reason)
        {
            sample = new BodyFixedSample();
            if (frames == null || frames.Count < 2)
            {
                reason = "too-few-frames";
                return false;
            }
            if (double.IsNaN(ut) || ut < frames[0].ut)
            {
                reason = "before-first-frame";
                return false;
            }
            if (ut > frames[frames.Count - 1].ut)
            {
                reason = "after-last-frame";
                return false;
            }

            int lo = 0, hi = frames.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (frames[mid].ut <= ut) lo = mid; else hi = mid;
            }
            TrajectoryPoint a = frames[lo];
            TrajectoryPoint b = frames[hi];
            if (!string.Equals(a.bodyName, b.bodyName, StringComparison.Ordinal))
            {
                reason = "body-change";
                return false;
            }

            double span = b.ut - a.ut;
            double t = span > 0.0 ? (ut - a.ut) / span : 0.0;
            if (t < 0.0) t = 0.0;
            if (t > 1.0) t = 1.0;

            sample.Body = a.bodyName;
            sample.Latitude = a.latitude + (b.latitude - a.latitude) * t;
            sample.Longitude = LerpLongitudeDegrees(a.longitude, b.longitude, t);
            sample.Altitude = a.altitude + (b.altitude - a.altitude) * t;
            sample.Clearance = IsFinite(a.recordedGroundClearance) && IsFinite(b.recordedGroundClearance)
                ? a.recordedGroundClearance + (b.recordedGroundClearance - a.recordedGroundClearance) * t
                : double.NaN;
            sample.BeforeUT = a.ut;
            sample.AfterUT = b.ut;
            double speedA = Magnitude(a.velocity.x, a.velocity.y, a.velocity.z);
            double speedB = Magnitude(b.velocity.x, b.velocity.y, b.velocity.z);
            sample.SpeedMps = speedA + (speedB - speedA) * t;
            reason = "ok";
            return true;
        }

        /// <summary>Longitude interpolation along the shorter arc, result in (-180, 180].</summary>
        internal static double LerpLongitudeDegrees(double from, double to, double t)
        {
            double delta = WrapDegrees(to - from);
            return WrapDegrees(from + delta * t);
        }

        /// <summary>Wraps an angle in degrees into (-180, 180].</summary>
        internal static double WrapDegrees(double deg)
        {
            if (double.IsNaN(deg) || double.IsInfinity(deg)) return deg;
            double w = deg % 360.0;
            if (w > 180.0) w -= 360.0;
            if (w <= -180.0) w += 360.0;
            return w;
        }

        /// <summary>Great-circle distance in metres on a sphere of <paramref name="radiusMeters"/>
        /// (haversine; well conditioned for the metre-scale distances the cell reads).</summary>
        internal static double GreatCircleMeters(double lat1Deg, double lon1Deg,
            double lat2Deg, double lon2Deg, double radiusMeters)
        {
            const double D2R = Math.PI / 180.0;
            double p1 = lat1Deg * D2R, p2 = lat2Deg * D2R;
            double dp = (lat2Deg - lat1Deg) * D2R;
            double dl = WrapDegrees(lon2Deg - lon1Deg) * D2R;
            double s = Math.Sin(dp / 2.0);
            double c = Math.Sin(dl / 2.0);
            double h = s * s + Math.Cos(p1) * Math.Cos(p2) * c * c;
            if (h > 1.0) h = 1.0;
            if (h < 0.0) h = 0.0;
            return 2.0 * radiusMeters * Math.Asin(Math.Sqrt(h));
        }

        /// <summary>
        /// Horizontal tolerance: the floor, plus the scene float grid at the ghost's distance from
        /// the origin, plus one frame of the ship's motion (a frame-boundary re-apply).
        /// </summary>
        internal static double HorizontalToleranceMeters(double floatGridMeters, double speedMps,
            double frameSeconds)
        {
            double grid = IsFinite(floatGridMeters) && floatGridMeters > 0.0 ? floatGridMeters : 0.0;
            double speed = IsFinite(speedMps) && speedMps > 0.0 ? speedMps : 0.0;
            double dt = IsFinite(frameSeconds) && frameSeconds > 0.0
                ? Math.Min(frameSeconds, MaxFrameSeconds) : 0.0;
            return HorizontalFloorMeters + grid + speed * dt;
        }

        /// <summary>
        /// The altitude window a correctly placed ghost may sit in: between the recorded altitude
        /// and the current terrain plus the recorded clearance (whichever the renderer used), and
        /// up to the terrain clamp's lift, each widened by <see cref="VerticalSlackMeters"/>.
        /// NaN terrain (no PQS) or NaN clearance drop out of the window.
        /// </summary>
        internal static void VerticalWindow(double recordedAltitude, double terrainAltitude,
            double clearance, out double min, out double max)
        {
            min = recordedAltitude;
            max = recordedAltitude;
            if (IsFinite(terrainAltitude))
            {
                if (IsFinite(clearance))
                {
                    double phase7 = terrainAltitude + clearance;
                    if (phase7 < min) min = phase7;
                    if (phase7 > max) max = phase7;
                }
                double clamp = terrainAltitude + MaxTerrainClampClearanceMeters;
                if (clamp > max) max = clamp;
            }
            min -= VerticalSlackMeters;
            max += VerticalSlackMeters;
        }

        internal static Verdict Compare(double horizontalMeters, double horizontalToleranceMeters,
            double ghostAltitude, double verticalMin, double verticalMax, double expectedAltitude,
            double clockDeltaSeconds)
        {
            var v = new Verdict
            {
                HorizontalMeters = horizontalMeters,
                HorizontalToleranceMeters = horizontalToleranceMeters,
                VerticalDeltaMeters = ghostAltitude - expectedAltitude,
                VerticalMinMeters = verticalMin,
                VerticalMaxMeters = verticalMax,
                ClockDeltaSeconds = clockDeltaSeconds,
            };
            if (!(Math.Abs(clockDeltaSeconds) <= ClockEpsilonSeconds))
            {
                v.Reason = "clock-mismatch";
                return v;
            }
            if (!IsFinite(horizontalMeters) || !IsFinite(ghostAltitude))
            {
                v.Reason = "non-finite";
                return v;
            }
            if (!(horizontalMeters <= horizontalToleranceMeters))
            {
                v.Reason = "horizontal";
                return v;
            }
            if (ghostAltitude < verticalMin || ghostAltitude > verticalMax)
            {
                v.Reason = "vertical";
                return v;
            }
            v.Ok = true;
            v.Reason = "ok";
            return v;
        }

        internal static string FormatSampleLine(string routeName, int memberIndex, string recordingId,
            string vesselName, string body, string source, double ut, double loopUT, long cycle,
            Verdict v)
        {
            return string.Format(IC,
                "RouteGhostPlacement: route='{0}' member={1} rec={2} vessel='{3}' body={4} source={5} "
                + "ut={6:F2} loopUT={7:F2} cycle={8} horizM={9:F2} tolM={10:F2} dAltM={11:F2} "
                + "altWindow=[{12:F1},{13:F1}] clockDeltaS={14:E1} verdict={15}",
                routeName, memberIndex, recordingId, vesselName, body, source, ut, loopUT, cycle,
                v.HorizontalMeters, v.HorizontalToleranceMeters, v.VerticalDeltaMeters,
                v.VerticalMinMeters, v.VerticalMaxMeters, v.ClockDeltaSeconds, v.Reason);
        }

        /// <summary>The one summary line the lanes pin.</summary>
        internal static string FormatSummary(string scene, int routes, int frames, int compared,
            int ok, int bad, int outsideWindow, int hidden, int unsupported, int notLive,
            double worstHorizontalMeters, string bodies)
        {
            return string.Format(IC,
                "RouteGhostPlacement summary: scene={0} routes={1} frames={2} compared={3} ok={4} "
                + "bad={5} outsideWindow={6} hidden={7} unsupported={8} notLive={9} "
                + "worstHorizM={10:F2} bodies=[{11}]",
                scene, routes, frames, compared, ok, bad, outsideWindow, hidden, unsupported,
                notLive, worstHorizontalMeters, bodies ?? string.Empty);
        }

        private static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);

        private static double Magnitude(double x, double y, double z) => Math.Sqrt(x * x + y * y + z * z);
    }
}
