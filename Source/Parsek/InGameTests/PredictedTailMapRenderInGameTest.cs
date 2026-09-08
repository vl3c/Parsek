using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Display;
using UnityEngine;

namespace Parsek.InGameTests
{
    // PREDICTED-TAIL map render (2026-09-08). The one cell that watches a predicted scene-exit
    // continuation tail reach a DRAW decision against a LIVE body, which no headless cell can:
    // the tail's ownership split needs the real body radius AND gravitational parameter, and the
    // leg the descent becomes is sampled through a real stock Orbit.
    //
    // It self-sets-up: the fixture is synthesized against whatever body the scene is at, sized
    // from that body's own radius and atmosphere depth so the coast clips at atmosphere entry and
    // the ballistic descent terminates exactly at the surface radius - the shape
    // IncompleteBallisticSceneExitFinalizer appends. No flight, no injection into the committed
    // store, so nothing about the running campaign is touched.
    //
    // What it asserts, and why it is the DRAW rather than the selector's input: the three
    // MapRendering_V1_* cells in IncompleteBallisticRuntimeTests assert
    // TryGetOrbitWindowForMapDisplay's returned window, i.e. the selector's INPUT. Those would
    // have PASSED on the 2026-09-08 session, because the selector really does accept a predicted
    // segment - the loss happened downstream, in the polyline's arc pass and the leg build. This
    // cell reads the two surfaces that actually decide what is on screen (the built legs and the
    // selected forward arcs) and requires their union to cover the tail, then requires no drawn
    // tail point to sit inside the planet.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics). FLIGHT only; career-independent.
    public class PredictedTailMapRenderInGameTest
    {
        /// <summary>Apoapsis clearance above the surface, in atmosphere depths.</summary>
        private const double ApoapsisInAtmosphereDepths = 7.0;

        /// <summary>Subsurface periapsis depth as a fraction of the body radius (the trigger).</summary>
        private const double PeriapsisSubsurfaceFraction = 0.015;

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Predicted continuation tail: the clipped coast is selected as a forward "
                + "arc and the ballistic descent is built as a traced leg, so legs plus arcs cover "
                + "the whole tail and no drawn tail point lies inside the body")]
        public void PredictedTail_IsDrawnAsArcPlusLeg_AndStaysAboveTheSurface()
        {
            CelestialBody body = FlightGlobals.currentMainBody ?? FlightGlobals.GetHomeBody();
            if (body == null)
            {
                InGameAssert.Skip("no main body resolved from FlightGlobals - needs a loaded FLIGHT scene");
                return;
            }
            if (!(body.Radius > 0.0) || !(body.gravParameter > 0.0))
            {
                InGameAssert.Skip("body '" + body.bodyName + "' has no usable radius / gravParameter");
                return;
            }

            double radius = body.Radius;
            double mu = body.gravParameter;
            double atmosphereDepth = body.atmosphere && body.atmosphereDepth > 0.0
                ? body.atmosphereDepth
                : radius * 0.1;

            double apoapsisRadius = radius + ApoapsisInAtmosphereDepths * atmosphereDepth;
            double periapsisRadius = radius * (1.0 - PeriapsisSubsurfaceFraction);
            double sma = 0.5 * (apoapsisRadius + periapsisRadius);
            double ecc = (apoapsisRadius - periapsisRadius) / (apoapsisRadius + periapsisRadius);
            double meanMotion = Math.Sqrt(mu / (sma * sma * sma));

            // Descending-branch mean anomalies at the three radii that define the tail.
            if (!TryDescendingMeanAnomaly(sma, ecc, radius, out double mImpact)
                || !TryDescendingMeanAnomaly(sma, ecc, radius + atmosphereDepth, out double mEntry))
            {
                InGameAssert.Skip("could not size the fixture against body '" + body.bodyName
                    + "' (radius " + radius.ToString("F0", CultureInfo.InvariantCulture)
                    + ", atmosphere " + atmosphereDepth.ToString("F0", CultureInfo.InvariantCulture) + ")");
                return;
            }

            double impactUT = Planetarium.GetUniversalTime() + 5000.0;
            double entryUT = impactUT - (mImpact - mEntry) / meanMotion;
            double coastStartUT = impactUT - (mImpact - Math.PI) / meanMotion;
            double lastRecordedUT = coastStartUT + 0.4; // the finalizer's anchor-reseed overlap

            if (!(entryUT > coastStartUT) || !(impactUT > entryUT))
            {
                InGameAssert.Skip("degenerate fixture timing for body '" + body.bodyName + "'");
                return;
            }

            Recording rec = BuildTailRecording(
                body.bodyName, sma, ecc, mImpact, impactUT,
                coastStartUT, entryUT, lastRecordedUT, radius, atmosphereDepth);

            GhostTrajectoryPolylineRenderer.BodySurfaceProvider surface =
                (string name, out GhostTrajectoryPolylineRenderer.BodySurfaceInfo info) =>
                {
                    info = default(GhostTrajectoryPolylineRenderer.BodySurfaceInfo);
                    if (!string.Equals(name, body.bodyName, StringComparison.Ordinal)) return false;
                    info = new GhostTrajectoryPolylineRenderer.BodySurfaceInfo
                    {
                        radius = body.Radius,
                        gravParameter = body.gravParameter
                    };
                    return true;
                };

            // LIVE sampler: the same stock Orbit evaluation the Driver's own ConicGapSampler uses,
            // so the leg points are the ones the map would draw.
            GhostTrajectoryPolylineRenderer.ConicGapSampler sampler =
                (OrbitSegment seg, double ut, out double lat, out double lon, out double alt) =>
                {
                    lat = 0.0; lon = 0.0; alt = 0.0;
                    try
                    {
                        var orbit = new Orbit(
                            seg.inclination, seg.eccentricity, seg.semiMajorAxis,
                            seg.longitudeOfAscendingNode, seg.argumentOfPeriapsis,
                            seg.meanAnomalyAtEpoch, seg.epoch, body);
                        Vector3d world = orbit.getPositionAtUT(ut);
                        if (double.IsNaN(world.x) || double.IsInfinity(world.x)) return false;
                        lat = body.GetLatitude(world);
                        lon = body.GetLongitude(world);
                        alt = body.GetAltitude(world);
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                    return true;
                };

            List<GhostTrajectoryPolylineRenderer.LegPolyline> legs =
                GhostTrajectoryPolylineRenderer.BuildLegsForRecording(rec, surface, sampler);
            List<int> arcIndices = GhostTrajectoryPolylineRenderer.SelectForwardArcSegmentIndices(
                rec.OrbitSegments,
                forwardWindowStartUT: lastRecordedUT,
                forwardStopUT: impactUT + 100.0,
                headUT: lastRecordedUT - 100.0,
                surface: surface);

            // (1) COVERAGE: the union of leg spans and selected arc spans covers the tail.
            var spans = new List<KeyValuePair<double, double>>();
            for (int i = 0; i < legs.Count; i++)
                spans.Add(new KeyValuePair<double, double>(legs[i].startUT, legs[i].endUT));
            for (int i = 0; i < arcIndices.Count; i++)
            {
                OrbitSegment seg = rec.OrbitSegments[arcIndices[i]];
                spans.Add(new KeyValuePair<double, double>(seg.startUT, seg.endUT));
            }
            double hole = LargestHole(spans, coastStartUT, impactUT);

            // (2) NOT INSIDE THE PLANET: every drawn tail point's world position must clear the
            // body centre by at least the body radius, within the scene's float-grid tolerance.
            // The signal here is the whole tail's altitude range, so the tolerance resolves it by
            // orders of magnitude - asserted rather than assumed.
            double worstBelowSurface = 0.0;
            double worstMagnitude = 0.0;
            int tailPointsChecked = 0;
            for (int i = 0; i < legs.Count; i++)
            {
                var leg = legs[i];
                if (leg.endUT <= lastRecordedUT) continue; // recorded leg, not the tail
                for (int k = 0; k < leg.PointCount; k++)
                {
                    Vector3d world = body.GetWorldSurfacePosition(leg.lats[k], leg.lons[k], leg.alts[k]);
                    double r = (world - body.position).magnitude;
                    double magnitude = Math.Max(Math.Abs(world.x),
                        Math.Max(Math.Abs(world.y), Math.Abs(world.z)));
                    if (magnitude > worstMagnitude) worstMagnitude = magnitude;
                    double below = radius - r;
                    if (below > worstBelowSurface) worstBelowSurface = below;
                    tailPointsChecked++;
                }
            }
            double tolerance = InGameFixtureMath.SceneFloatGridToleranceMeters(worstMagnitude);

            ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                "PredictedTailMapRender: body={0} radius={1:F0} atm={2:F0} sma={3:F0} ecc={4:F6} "
                + "tail=[{5:F1},{6:F1}] entry={7:F1} legs={8} arcs={9} hole={10:F1}s "
                + "tailPts={11} worstBelowSurface={12:F3}m tol={13:F3}m",
                body.bodyName, radius, atmosphereDepth, sma, ecc,
                coastStartUT, impactUT, entryUT, legs.Count, arcIndices.Count, hole,
                tailPointsChecked, worstBelowSurface, tolerance));

            InGameAssert.IsTrue(
                hole <= GhostTrajectoryPolylineRenderer.GapFillMinSeconds,
                string.Format(CultureInfo.InvariantCulture,
                    "the predicted tail [{0:F1},{1:F1}] must be covered by legs plus selected arcs; "
                    + "largest hole was {2:F1}s (legs={3} arcs={4})",
                    coastStartUT, impactUT, hole, legs.Count, arcIndices.Count));

            InGameAssert.IsTrue(
                tailPointsChecked >= 2,
                "the predicted descent must contribute at least two drawn points, got "
                    + tailPointsChecked);

            InGameAssert.IsTrue(
                InGameFixtureMath.ToleranceResolvesSignal(tolerance, atmosphereDepth),
                string.Format(CultureInfo.InvariantCulture,
                    "the float-grid tolerance {0:F3}m must resolve the tail's altitude signal {1:F0}m",
                    tolerance, atmosphereDepth));

            InGameAssert.IsTrue(
                worstBelowSurface <= tolerance,
                string.Format(CultureInfo.InvariantCulture,
                    "no drawn tail point may lie inside {0}: worst was {1:F3}m below the surface "
                    + "(tolerance {2:F3}m)",
                    body.bodyName, worstBelowSurface, tolerance));
        }

        /// <summary>
        /// Mean anomaly on the DESCENDING branch (past apoapsis, before periapsis) at one radius.
        /// False when the radius is outside the conic's own [periapsis, apoapsis] range.
        /// </summary>
        private static bool TryDescendingMeanAnomaly(
            double sma, double ecc, double radius, out double meanAnomaly)
        {
            meanAnomaly = 0.0;
            if (!(sma > 0.0) || !(ecc > 0.0) || ecc >= 1.0) return false;
            double cosE = (1.0 - radius / sma) / ecc;
            if (cosE < -1.0 || cosE > 1.0) return false;
            double eccentricAnomaly = 2.0 * Math.PI - Math.Acos(cosE);
            meanAnomaly = eccentricAnomaly - ecc * Math.Sin(eccentricAnomaly);
            return !double.IsNaN(meanAnomaly) && !double.IsInfinity(meanAnomaly);
        }

        /// <summary>
        /// The shape the scene-exit finalizer leaves: a recorded ascent / climb ending just after
        /// the first tail conic's start (the anchor reseed), then two POINTLESS predicted conics -
        /// the coast clipped at atmosphere entry, and the ballistic descent to the surface.
        /// </summary>
        private static Recording BuildTailRecording(
            string bodyName, double sma, double ecc, double meanAnomalyAtImpact, double impactUT,
            double coastStartUT, double entryUT, double lastRecordedUT,
            double radius, double atmosphereDepth)
        {
            var rec = new Recording { RecordingId = "ingame-predicted-tail", VesselName = "PredictedTail" };

            var climb = new List<TrajectoryPoint>();
            const int steps = 24;
            double climbStartUT = lastRecordedUT - 900.0;
            for (int i = 0; i <= steps; i++)
            {
                double f = (double)i / steps;
                climb.Add(new TrajectoryPoint
                {
                    ut = climbStartUT + (lastRecordedUT - climbStartUT) * f,
                    latitude = -0.1 + 0.4 * f,
                    longitude = -74.5 + 2.0 * f,
                    altitude = atmosphereDepth * 0.1 + f * (sma * (1.0 + ecc) - radius),
                    bodyName = bodyName,
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero
                });
            }
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = climbStartUT,
                endUT = lastRecordedUT,
                frames = climb,
                checkpoints = new List<OrbitSegment>(),
                bodyFixedFrames = null,
                sampleRateHz = 10f
            });

            rec.OrbitSegments.Add(MakeSegment(
                bodyName, sma, ecc, meanAnomalyAtImpact, impactUT, coastStartUT, entryUT));
            rec.OrbitSegments.Add(MakeSegment(
                bodyName, sma, ecc, meanAnomalyAtImpact, impactUT, entryUT, impactUT));
            return rec;
        }

        private static OrbitSegment MakeSegment(
            string bodyName, double sma, double ecc, double meanAnomalyAtEpoch, double epoch,
            double startUT, double endUT)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = bodyName,
                semiMajorAxis = sma,
                eccentricity = ecc,
                inclination = 0.5,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 84.5,
                meanAnomalyAtEpoch = meanAnomalyAtEpoch,
                epoch = epoch,
                isPredicted = true
            };
        }

        /// <summary>Largest uncovered run inside [from, to] across a set of spans.</summary>
        private static double LargestHole(
            List<KeyValuePair<double, double>> spans, double from, double to)
        {
            spans.Sort((a, b) => a.Key.CompareTo(b.Key));
            double cursor = from;
            double worst = 0.0;
            for (int i = 0; i < spans.Count; i++)
            {
                if (spans[i].Value <= cursor) continue;
                if (spans[i].Key > cursor)
                {
                    double gap = Math.Min(spans[i].Key, to) - cursor;
                    if (gap > worst) worst = gap;
                }
                cursor = spans[i].Value;
                if (cursor >= to) break;
            }
            if (cursor < to && to - cursor > worst) worst = to - cursor;
            return worst;
        }
    }
}
