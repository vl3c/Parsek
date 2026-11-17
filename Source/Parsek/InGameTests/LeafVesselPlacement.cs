using System;
using System.Globalization;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Pure comparison core of the <c>LeafVesselPlacement</c> in-game cell
    /// (<see cref="LeafVesselPlacementInGameTests"/>): does a committed recording's spawned
    /// vessel sit where the recording ended? An orbiting leaf is compared by its KSP-native
    /// elements (degrees for inclination / LAN / argument of periapsis, the mean anomaly at
    /// epoch in radians, <c>Orbit</c>'s own fields) and by its MEAN LONGITUDE at one common
    /// UT, which is where the vessel is along the orbit; a landed or splashed leaf is compared
    /// by the great-circle distance between its live latitude / longitude and the recorded
    /// terminal surface position. Both sides of an orbit comparison use the same element frame,
    /// so no frame conversion is needed or made.
    ///
    /// <para>The tolerances sit far above what a correct spawn produces (the committed far-body
    /// fixtures read differences of 1e-9 relative in SMA and zero in mean longitude) and far
    /// below what a wrong one produces (another body, another orbit, or a phase off by the
    /// rewind or warp span), so a reading is either about zero or plainly wrong.</para>
    /// </summary>
    internal static class LeafVesselPlacement
    {
        internal const double MaxSmaRelative = 1e-3;
        internal const double MaxEccentricity = 2e-3;
        internal const double MaxInclinationDeg = 0.1;
        internal const double MaxLanDeg = 0.5;
        internal const double MaxArgPeDeg = 1.0;
        internal const double MaxMeanLongitudeDeg = 1.0;
        internal const double MaxSurfaceDistanceMeters = 100.0;

        /// <summary>A Landed leaf more than this far below the terrain, or a Splashed leaf more
        /// than this far below the sea floor, reads buried.</summary>
        internal const double MaxBelowSurfaceMeters = 1.0;

        /// <summary>A Landed leaf more than this far above the terrain reads <c>above-surface</c>;
        /// a Splashed leaf more than this far from its recorded depth reads
        /// <c>off-recorded-depth</c>. Generous on purpose: the live altitude is the vessel's own
        /// reference point, not its lowest part, and a hull moves in the water once loaded.</summary>
        internal const double MaxOffSurfaceMeters = 10.0;

        /// <summary>LAN is only defined for an inclined orbit; below this both sides skip it.</summary>
        internal const double LanDefinedMinInclinationDeg = 1.0;

        /// <summary>The argument of periapsis is only defined for an eccentric orbit.</summary>
        internal const double ArgPeDefinedMinEccentricity = 0.05;

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        internal struct OrbitElements
        {
            public string Body;
            public double SemiMajorAxis;
            public double Eccentricity;
            public double InclinationDeg;
            public double LanDeg;
            public double ArgPeDeg;
            public double MeanAnomalyAtEpochRad;
            public double Epoch;
        }

        internal struct OrbitVerdict
        {
            public bool Ok;
            public string Reason;
            public double SmaRelative;
            public double EccentricityDelta;
            public double InclinationDeltaDeg;
            public double LanDeltaDeg;
            public double ArgPeDeltaDeg;
            public double MeanLongitudeDeltaDeg;
            public double AlongTrackMeters;
        }

        internal struct SurfaceVerdict
        {
            public bool Ok;
            public string Reason;
            public double DistanceMeters;
        }

        internal struct HeightVerdict
        {
            public bool Ok;
            public string Reason;
            /// <summary>Where the leaf should be: the terrain for Landed, the recorded depth
            /// (never above sea level) for Splashed.</summary>
            public double ReferenceMeters;
            /// <summary>Live altitude minus <see cref="ReferenceMeters"/>; negative is below it.</summary>
            public double DeltaMeters;
            /// <summary>The ground under the leaf, <c>TerrainAltitude(lat, lon, true)</c>: the
            /// terrain, or the sea floor under water.</summary>
            public double FloorMeters;
        }

        /// <summary>Folds an angle difference into (-180, 180].</summary>
        internal static double WrapDeg(double deg)
        {
            if (double.IsNaN(deg) || double.IsInfinity(deg)) return double.NaN;
            double d = deg % 360.0;
            if (d > 180.0) d -= 360.0;
            if (d <= -180.0) d += 360.0;
            return d;
        }

        /// <summary>True for a closed orbit the mean-motion phase is defined on.</summary>
        internal static bool IsClosedOrbit(OrbitElements e)
        {
            return e.SemiMajorAxis > 0.0 && e.Eccentricity >= 0.0 && e.Eccentricity < 1.0
                && !double.IsNaN(e.SemiMajorAxis) && !double.IsInfinity(e.SemiMajorAxis);
        }

        /// <summary>Mean longitude (LAN + argument of periapsis + mean anomaly) at
        /// <paramref name="ut"/>, in degrees, unwrapped. NaN for an open orbit or a bad mu.</summary>
        internal static double MeanLongitudeDeg(OrbitElements e, double mu, double ut)
        {
            if (!IsClosedOrbit(e) || !(mu > 0.0)) return double.NaN;
            double n = Math.Sqrt(mu / (e.SemiMajorAxis * e.SemiMajorAxis * e.SemiMajorAxis));
            double meanAnomalyRad = e.MeanAnomalyAtEpochRad + n * (ut - e.Epoch);
            return e.LanDeg + e.ArgPeDeg + meanAnomalyRad * (180.0 / Math.PI);
        }

        internal static OrbitVerdict CompareOrbits(OrbitElements recorded, OrbitElements live, double mu, double ut)
        {
            var v = new OrbitVerdict();
            if (!string.Equals(recorded.Body, live.Body, StringComparison.Ordinal))
            {
                v.Reason = "body-mismatch";
                return v;
            }
            if (!IsClosedOrbit(recorded) || !IsClosedOrbit(live) || !(mu > 0.0))
            {
                v.Reason = "unsupported-open-orbit";
                return v;
            }

            v.SmaRelative = (live.SemiMajorAxis - recorded.SemiMajorAxis) / recorded.SemiMajorAxis;
            v.EccentricityDelta = live.Eccentricity - recorded.Eccentricity;
            v.InclinationDeltaDeg = live.InclinationDeg - recorded.InclinationDeg;
            bool lanDefined = recorded.InclinationDeg >= LanDefinedMinInclinationDeg
                && live.InclinationDeg >= LanDefinedMinInclinationDeg;
            v.LanDeltaDeg = lanDefined ? WrapDeg(live.LanDeg - recorded.LanDeg) : 0.0;
            bool argPeDefined = recorded.Eccentricity >= ArgPeDefinedMinEccentricity
                && live.Eccentricity >= ArgPeDefinedMinEccentricity;
            v.ArgPeDeltaDeg = argPeDefined ? WrapDeg(live.ArgPeDeg - recorded.ArgPeDeg) : 0.0;
            v.MeanLongitudeDeltaDeg = WrapDeg(MeanLongitudeDeg(live, mu, ut) - MeanLongitudeDeg(recorded, mu, ut));
            v.AlongTrackMeters = Math.Abs(v.MeanLongitudeDeltaDeg) * (Math.PI / 180.0) * recorded.SemiMajorAxis;

            if (!(Math.Abs(v.SmaRelative) <= MaxSmaRelative)) v.Reason = "sma";
            else if (!(Math.Abs(v.EccentricityDelta) <= MaxEccentricity)) v.Reason = "eccentricity";
            else if (!(Math.Abs(v.InclinationDeltaDeg) <= MaxInclinationDeg)) v.Reason = "inclination";
            else if (!(Math.Abs(v.LanDeltaDeg) <= MaxLanDeg)) v.Reason = "lan";
            else if (!(Math.Abs(v.ArgPeDeltaDeg) <= MaxArgPeDeg)) v.Reason = "argpe";
            else if (!(Math.Abs(v.MeanLongitudeDeltaDeg) <= MaxMeanLongitudeDeg)) v.Reason = "phase";
            else
            {
                v.Ok = true;
                v.Reason = "ok";
            }
            return v;
        }

        /// <summary>Great-circle distance in metres on a sphere of <paramref name="radius"/>.</summary>
        internal static double SurfaceDistanceMeters(double lat1Deg, double lon1Deg,
            double lat2Deg, double lon2Deg, double radius)
        {
            double toRad = Math.PI / 180.0;
            double p1 = lat1Deg * toRad, p2 = lat2Deg * toRad;
            double dp = p2 - p1, dl = (lon2Deg - lon1Deg) * toRad;
            double a = Math.Sin(dp / 2) * Math.Sin(dp / 2)
                + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2.0 * radius * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
        }

        internal static SurfaceVerdict CompareSurface(string recordedBody, double recordedLat, double recordedLon,
            string liveBody, double liveLat, double liveLon, double bodyRadius)
        {
            var v = new SurfaceVerdict();
            if (!string.Equals(recordedBody, liveBody, StringComparison.Ordinal))
            {
                v.Reason = "body-mismatch";
                return v;
            }
            v.DistanceMeters = SurfaceDistanceMeters(recordedLat, recordedLon, liveLat, liveLon, bodyRadius);
            if (!(v.DistanceMeters <= MaxSurfaceDistanceMeters))
            {
                v.Reason = "distance";
                return v;
            }
            v.Ok = true;
            v.Reason = "ok";
            return v;
        }

        /// <summary>
        /// The vertical half of a landed or splashed leaf's placement. A Landed leaf is read
        /// against the terrain under it (<c>CelestialBody.TerrainAltitude(lat, lon, true)</c>):
        /// bad more than <see cref="MaxBelowSurfaceMeters"/> below it (<c>below-surface</c>) or
        /// more than <see cref="MaxOffSurfaceMeters"/> above it (<c>above-surface</c>). A Splashed
        /// leaf spawns at its recorded depth (owner ruling 2026-11-17), so it is read against
        /// that depth (a positive recorded altitude counts as sea level 0; an unknown one as 0):
        /// bad more than <see cref="MaxBelowSurfaceMeters"/> under the sea floor
        /// (<c>below-sea-floor</c>) or more than <see cref="MaxOffSurfaceMeters"/> from the
        /// recorded depth (<c>off-recorded-depth</c>). The terrain is PQS only, so a vessel
        /// standing on a building, the launch pad or the island runway reads high.
        /// </summary>
        internal static HeightVerdict CompareHeight(TerminalState terminal, double liveAltitude,
            double floorAltitude, double recordedAltitude)
        {
            var v = new HeightVerdict { FloorMeters = floorAltitude };
            if (terminal == TerminalState.Landed)
                v.ReferenceMeters = floorAltitude;
            else if (terminal == TerminalState.Splashed)
                v.ReferenceMeters = double.IsNaN(recordedAltitude) || double.IsInfinity(recordedAltitude)
                    ? 0.0
                    : Math.Min(recordedAltitude, 0.0);
            else
            {
                v.Reason = "unsupported";
                v.ReferenceMeters = double.NaN;
                v.DeltaMeters = double.NaN;
                return v;
            }
            v.DeltaMeters = liveAltitude - v.ReferenceMeters;
            if (double.IsNaN(v.DeltaMeters) || double.IsInfinity(v.DeltaMeters))
                v.Reason = "unsupported";
            else if (terminal == TerminalState.Landed && v.DeltaMeters < -MaxBelowSurfaceMeters)
                v.Reason = "below-surface";
            else if (terminal == TerminalState.Landed && v.DeltaMeters > MaxOffSurfaceMeters)
                v.Reason = "above-surface";
            else if (terminal == TerminalState.Splashed && !double.IsNaN(floorAltitude)
                && liveAltitude < floorAltitude - MaxBelowSurfaceMeters)
                v.Reason = "below-sea-floor";
            else if (terminal == TerminalState.Splashed && Math.Abs(v.DeltaMeters) > MaxOffSurfaceMeters)
                v.Reason = "off-recorded-depth";
            else
            {
                v.Ok = true;
                v.Reason = "ok";
            }
            return v;
        }

        internal static string FormatOrbitLine(string recordingId, string vesselName, uint pid,
            string recordedBody, string liveBody, OrbitVerdict v)
        {
            return string.Format(IC,
                "LeafPlacement: rec={0} vessel='{1}' pid={2} kind=orbit body={3} liveBody={4} "
                + "dSmaRel={5:E2} dEcc={6:E2} dIncDeg={7:F4} dLanDeg={8:F4} dArgPeDeg={9:F3} "
                + "dLongDeg={10:F4} alongTrackM={11:F1} verdict={12}",
                recordingId, vesselName, pid, recordedBody, liveBody,
                v.SmaRelative, v.EccentricityDelta, v.InclinationDeltaDeg, v.LanDeltaDeg,
                v.ArgPeDeltaDeg, v.MeanLongitudeDeltaDeg, v.AlongTrackMeters, v.Reason);
        }

        internal static string FormatSurfaceLine(string recordingId, string vesselName, uint pid,
            string recordedBody, string liveBody, SurfaceVerdict v)
        {
            return string.Format(IC,
                "LeafPlacement: rec={0} vessel='{1}' pid={2} kind=surface body={3} liveBody={4} "
                + "distM={5:F1} verdict={6}",
                recordingId, vesselName, pid, recordedBody, liveBody, v.DistanceMeters, v.Reason);
        }

        /// <summary>The per-leaf vertical line. Its own prefix and its own token
        /// (<c>heightVerdict=</c>), so it never matches a lane's <c>LeafPlacement: ... verdict=</c>
        /// pattern and stays report-only until a lane pins it.</summary>
        internal static string FormatHeightLine(string recordingId, string vesselName, uint pid,
            string body, TerminalState terminal, double liveAltitude, HeightVerdict v)
        {
            return string.Format(IC,
                "LeafPlacement height: rec={0} vessel='{1}' pid={2} terminal={3} body={4} "
                + "altM={5:F1} refM={6:F1} deltaM={7:F1} floorM={8:F1} heightVerdict={9}",
                recordingId, vesselName, pid, terminal, body,
                liveAltitude, v.ReferenceMeters, v.DeltaMeters, v.FloorMeters, v.Reason);
        }

        /// <summary>The vertical summary, a line of its own beside <see cref="FormatSummary"/>.</summary>
        internal static string FormatHeightSummary(string scene, int checkedCount, int ok, int bad,
            int unsupported)
        {
            return string.Format(IC,
                "LeafPlacement height summary: scene={0} checked={1} ok={2} bad={3} unsupported={4}",
                scene, checkedCount, ok, bad, unsupported);
        }

        /// <summary>The one summary line the lanes pin. <paramref name="bodies"/> is the
        /// recorded terminal bodies of the compared pairs, already sorted and comma-joined.</summary>
        internal static string FormatSummary(string scene, int compared, int ok, int bad,
            int absent, int duplicate, int unsupported, string bodies)
        {
            return string.Format(IC,
                "LeafPlacement summary: scene={0} compared={1} ok={2} bad={3} absent={4} "
                + "duplicate={5} unsupported={6} bodies=[{7}]",
                scene, compared, ok, bad, absent, duplicate, unsupported, bodies ?? string.Empty);
        }
    }
}
