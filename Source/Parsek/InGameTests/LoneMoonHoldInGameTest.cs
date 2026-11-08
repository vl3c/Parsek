using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Reaim;

namespace Parsek.InGameTests
{
    /// <summary>
    /// The lone-moon arrival hold against the LIVE body graph: for EVERY moon of EVERY planet the
    /// launch planet can transfer to (a direct child of the launch planet's parent other than the
    /// launch planet itself), a looped trip that arrives at the planet and goes on to that one moon
    /// (no landing on the planet) must take the moon hold, and the per-loop hold must put the moon
    /// back where the recording had it at every replayed encounter, within the moon's live SOI
    /// crossing time. A landing on a tidally locked moon must take the same hold.
    ///
    /// <para><b>Why in-game.</b> The xUnit fixtures (<c>LoneMoonArrivalHoldTests</c>) pin stock
    /// Duna/Ike constants; only a live run reads every shipped moon's period, SOI and orbital
    /// velocity through <c>FlightGlobalsBodyInfo.Instance</c> and proves the hold engages for each.
    /// It is the sibling of <c>JoolConfigHoldInGameTest</c> and uses the same body-independent
    /// recorded time base; the geometry re-solves against the live ephemerides.</para>
    ///
    /// <para><b>Isolation.</b> Pure planner calls with a live-read <c>IBodyInfo</c>; no store,
    /// ledger or route is touched. The log seams are snapshotted and restored in <c>finally</c>.
    /// Batch-safe at the Space Center.</para>
    /// </summary>
    public sealed class LoneMoonHoldInGameTest
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const string Tag = "TestRunner";
        private const string LaunchBody = "Kerbin";

        private const double RecordedArrivalUT = 1.0e6;
        private const double PhaseAnchorUT = 350.0;
        private const double SpanStartUT = 0.0;

        [InGameTest(Category = "MoonArrivalHold", Scene = GameScenes.SPACECENTER,
            Description = "Lone-moon arrival hold (LIVE): for every moon of every planet reachable from "
                + "Kerbin, a looped trip that arrives at the planet and goes on to that one moon engages "
                + "the moon hold on the moon's live orbital period, and the per-loop hold puts the moon "
                + "back within its live SOI crossing time at every replayed encounter; a landing on a "
                + "tidally locked moon takes the same hold")]
        public void EveryPlanetMoon_LoneMoonTrip_EngagesMoonHold_AgainstLiveBodies()
        {
            var bi = FlightGlobalsBodyInfo.Instance;
            CelestialBody kerbin = FindBody(LaunchBody);
            if (kerbin == null || kerbin.referenceBody == null)
                InGameAssert.Skip("Kerbin or its parent is not in FlightGlobals.Bodies - cannot form the trips");
            string sun = kerbin.referenceBody.bodyName;
            double pKerbin = bi.OrbitPeriod(LaunchBody);
            if (double.IsNaN(pKerbin) || pKerbin <= 0.0)
                InGameAssert.Skip("Kerbin has degenerate live orbit data");

            int planets = 0, moons = 0, lockedLandings = 0;
            var summary = new List<string>();
            bool prevLogSuppress = ParsekLog.SuppressLogging;
            bool prevMpSuppress = MissionPeriodicity.SuppressLogging;
            try
            {
                ParsekLog.SuppressLogging = false;
                MissionPeriodicity.SuppressLogging = true; // one summary line below, not one per moon

                for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
                {
                    CelestialBody planet = FlightGlobals.Bodies[i];
                    if (planet == null || planet.bodyName == LaunchBody
                        || bi.ReferenceBodyName(planet.bodyName) != sun)
                        continue;
                    double pPlanet = bi.OrbitPeriod(planet.bodyName);
                    if (double.IsNaN(pPlanet) || pPlanet <= 0.0 || Math.Abs(pPlanet - pKerbin) < 1.0)
                        continue;
                    double synodic = 1.0 / Math.Abs(1.0 / pKerbin - 1.0 / pPlanet);
                    bool planetCounted = false;

                    for (int j = 0; j < FlightGlobals.Bodies.Count; j++)
                    {
                        CelestialBody moon = FlightGlobals.Bodies[j];
                        if (moon == null || bi.ReferenceBodyName(moon.bodyName) != planet.bodyName)
                            continue;
                        double pMoon = bi.OrbitPeriod(moon.bodyName);
                        double tol = MoonSoiToleranceSeconds(bi, moon.bodyName);
                        if (double.IsNaN(pMoon) || pMoon <= 0.0 || double.IsInfinity(tol))
                            continue;
                        if (!planetCounted) { planets++; planetCounted = true; }
                        moons++;

                        // The loop slack: the idle part of one synodic cadence (a generous stand-in
                        // for a short recorded span), always well above one moon orbit in stock.
                        double slack = 0.5 * synodic;
                        var trip = new List<PhaseConstraint>
                        {
                            Orbital(planet.bodyName, pPlanet),
                            Orbital(moon.bodyName, pMoon),
                        };
                        AssertMoonHold(bi, planet.bodyName, moon.bodyName, trip, synodic, slack, pMoon, tol,
                            "orbit");

                        double rot = bi.RotationPeriod(moon.bodyName);
                        if (!double.IsNaN(rot) && rot > 0.0 && Math.Abs(rot - pMoon) <= 1e-6 * pMoon)
                        {
                            trip.Add(new PhaseConstraint
                            {
                                Kind = ConstraintKind.Rotation, BodyName = moon.bodyName,
                                PeriodSeconds = rot, PhaseOffsetSeconds = 0.0, RelativeToParent = false,
                            });
                            AssertMoonHold(bi, planet.bodyName, moon.bodyName, trip, synodic, slack, pMoon,
                                tol, "locked-landing");
                            lockedLandings++;
                        }
                        summary.Add(planet.bodyName + "->" + moon.bodyName);
                    }
                }

                if (moons == 0)
                    InGameAssert.Skip("no planet reachable from Kerbin has a moon in this pack");

                ParsekLog.Info(Tag, string.Format(IC,
                    "LoneMoonHold PASS (LIVE): planets={0} moons={1} lockedLandings={2} trips=[{3}]",
                    planets, moons, lockedLandings, string.Join(",", summary.ToArray())));
            }
            finally
            {
                ParsekLog.SuppressLogging = prevLogSuppress;
                MissionPeriodicity.SuppressLogging = prevMpSuppress;
            }
        }

        private static void AssertMoonHold(
            FlightGlobalsBodyInfo bi, string planet, string moon, List<PhaseConstraint> trip,
            double synodic, double slack, double pMoon, double tol, string shape)
        {
            string label = planet + "->" + moon + " (" + shape + ")";
            ArrivalHoldPlanner.ArrivalHoldResult r = ArrivalHoldPlanner.ComputeArrivalHold(
                trip, planet, RecordedArrivalUT, TransitedBodyRotationMode.Loose,
                PhaseAnchorUT, SpanStartUT, null, bi,
                windowSpacingSeconds: synodic,
                launchBodyName: LaunchBody,
                loopSlackSeconds: slack);

            InGameAssert.IsTrue(r.Applied,
                label + ": the lone-moon trip must ENGAGE the moon hold (amber='"
                + (r.AmberReason ?? "none") + "')");
            InGameAssert.IsTrue(r.IsConfigHold, label + ": the engaged hold must be the moon hold");
            InGameAssert.AreEqual(1, r.ConfigMoonCount, label + ": exactly one constrained moon");
            InGameAssert.IsNull(r.AmberReason, label + ": an engaged hold carries no amber");
            InGameAssert.ApproxEqual(pMoon, r.AlignPeriodSeconds, 1e-6 * pMoon,
                label + ": the hold period must be the moon's live orbital period");

            for (long n = 0; n <= 12; n++)
            {
                double wN = GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(
                    r.HoldSeconds, n, synodic, r.AlignPeriodSeconds);
                InGameAssert.IsTrue(wN >= 0.0 && wN <= r.AlignPeriodSeconds + 1e-6,
                    label + $": loop {n.ToString(IC)} hold {wN.ToString("R", IC)}s outside [0, P_moon]");
                double shift = (PhaseAnchorUT - SpanStartUT) + n * synodic + wN;
                double err = MissionPeriodicity.CircularPhaseError(shift, pMoon);
                InGameAssert.IsTrue(err <= tol,
                    label + $": loop {n.ToString(IC)} moon phase error {err.ToString("F1", IC)}s exceeds "
                    + $"its live SOI tolerance {tol.ToString("F1", IC)}s");
            }
        }

        private static PhaseConstraint Orbital(string body, double period)
        {
            return new PhaseConstraint
            {
                Kind = ConstraintKind.Orbital, BodyName = body, PeriodSeconds = period,
                PhaseOffsetSeconds = 0.0, RelativeToParent = true,
            };
        }

        private static double MoonSoiToleranceSeconds(FlightGlobalsBodyInfo bi, string body)
        {
            double soi = bi.SoiRadius(body);
            double vel = bi.OrbitalVelocity(body);
            if (double.IsNaN(soi) || double.IsNaN(vel) || vel <= 0.0)
                return double.PositiveInfinity;
            return soi / vel;
        }

        private static CelestialBody FindBody(string name)
        {
            if (FlightGlobals.Bodies == null)
                return null;
            for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
            {
                CelestialBody b = FlightGlobals.Bodies[i];
                if (b != null && b.bodyName == name)
                    return b;
            }
            return null;
        }
    }
}
