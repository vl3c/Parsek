using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live cells for the spawn-blocked chain tip ghost (D18-CHAIN-SPAWN-BLOCKED-GHOST-6B4-NOOP,
    /// design 12.9.2 / 13.5). The policy-side hold and release are unit-tested headless; what
    /// needs KSP is the positioning itself: an Orbiting tip's ghost must sit on its terminal
    /// orbit at the current UT (KSP's own Orbit.getPositionAtUT), a landed tip's ghost on its
    /// body-fixed end pose, and both must register the FloatingOrigin reapply entry. Each cell
    /// drives a throwaway probe object through the production positioning path.
    /// </summary>
    public sealed class ChainTipBlockedGhostInGameTest
    {
        private const double HoldHeightMeters = 50.0;
        private const double OrbitStepSeconds = 60.0;

        // Its own category: no committed harness spec pins it, so adding it moves no
        // pinned BATCH_COMPLETE tally.
        [InGameTest(Category = "ChainTipBlockedGhost", Scene = GameScenes.FLIGHT,
            Description = "A spawn-blocked Orbiting chain tip's ghost follows its terminal orbit to the current UT and registers the FloatingOrigin reapply entry")]
        public void OrbitSource_GhostFollowsTheTerminalOrbit()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            Vessel active = FlightGlobals.ActiveVessel;
            if (flight == null || active == null || active.orbit == null || active.mainBody == null)
            {
                InGameAssert.Skip("needs a FLIGHT scene with a ParsekFlight instance and an active vessel");
                return;
            }

            CelestialBody body = active.mainBody;
            Orbit src = active.orbit;
            var orbit = new Orbit(src.inclination, src.eccentricity, src.semiMajorAxis, src.LAN,
                src.argumentOfPeriapsis, src.meanAnomalyAtEpoch, src.epoch, body);
            double ut = Planetarium.GetUniversalTime();
            Quaternion srfRel = Quaternion.Euler(10f, 20f, 30f);

            var probe = new GameObject("ParsekBlockedChainTipOrbitProbe");
            try
            {
                for (int step = 0; step < 2; step++)
                {
                    double sampleUT = ut + step * OrbitStepSeconds;
                    int entriesBefore = flight.PendingGhostPosEntryCountForInGameTest;
                    flight.PositionBlockedChainTipGhostForInGameTest(
                        probe, body, orbit, srfRel, 0.0, 0.0, 0.0, sampleUT);

                    Vector3d expected = orbit.getPositionAtUT(sampleUT);
                    double tolerance = System.Math.Max(0.05,
                        InGameFixtureMath.SceneFloatGridToleranceMeters(
                            System.Math.Abs(((Vector3)expected).magnitude)));
                    double error = Vector3d.Distance(expected, probe.transform.position);
                    InGameAssert.IsTrue(error <= tolerance,
                        "orbit-source ghost at step " + step + " is "
                        + error.ToString("F3", CultureInfo.InvariantCulture)
                        + " m from the terminal orbit position (tolerance "
                        + tolerance.ToString("F3", CultureInfo.InvariantCulture) + " m)");
                    InGameAssert.IsTrue(
                        Quaternion.Angle(body.bodyTransform.rotation * srfRel, probe.transform.rotation) < 0.01f,
                        "orbit-source ghost rotation is not the body-relative end attitude");
                    // The second step replaces the probe's own entry (same-frame latest wins).
                    InGameAssert.IsTrue(step == 0
                            ? flight.PendingGhostPosEntryCountForInGameTest > entriesBefore
                            : flight.PendingGhostPosEntryCountForInGameTest == entriesBefore,
                        "orbit-source positioning did not register exactly one FloatingOrigin reapply entry (step "
                        + step + ")");
                }
            }
            finally
            {
                Object.Destroy(probe);
            }

            ParsekLog.Info("TestRunner",
                "ChainTipBlockedGhost: orbit-source ghost followed the terminal orbit across "
                + OrbitStepSeconds.ToString("F0", CultureInfo.InvariantCulture) + " s on " + body.name);
        }

        [InGameTest(Category = "ChainTipBlockedGhost", Scene = GameScenes.FLIGHT,
            Description = "A spawn-blocked landed chain tip's ghost holds its body-fixed end pose and registers the FloatingOrigin reapply entry")]
        public void HoldSource_GhostHoldsItsBodyFixedEndPose()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            Vessel active = FlightGlobals.ActiveVessel;
            if (flight == null || active == null || active.mainBody == null)
            {
                InGameAssert.Skip("needs a FLIGHT scene with a ParsekFlight instance and an active vessel");
                return;
            }

            CelestialBody body = active.mainBody;
            double lat = active.latitude;
            double lon = active.longitude;
            double alt = active.altitude + HoldHeightMeters;
            Quaternion srfRel = Quaternion.Euler(0f, 45f, 0f);

            var probe = new GameObject("ParsekBlockedChainTipHoldProbe");
            try
            {
                int entriesBefore = flight.PendingGhostPosEntryCountForInGameTest;
                flight.PositionBlockedChainTipGhostForInGameTest(
                    probe, body, null, srfRel, lat, lon, alt, Planetarium.GetUniversalTime());

                Vector3d expected = body.GetWorldSurfacePosition(lat, lon, alt);
                double tolerance = System.Math.Max(0.05,
                    InGameFixtureMath.SceneFloatGridToleranceMeters(
                        System.Math.Abs(((Vector3)expected).magnitude)));
                double error = Vector3d.Distance(expected, probe.transform.position);
                InGameAssert.IsTrue(error <= tolerance,
                    "hold-source ghost is " + error.ToString("F3", CultureInfo.InvariantCulture)
                    + " m from its body-fixed end pose (tolerance "
                    + tolerance.ToString("F3", CultureInfo.InvariantCulture) + " m)");
                InGameAssert.IsTrue(
                    Quaternion.Angle(body.bodyTransform.rotation * srfRel, probe.transform.rotation) < 0.01f,
                    "hold-source ghost rotation is not the body-relative end attitude");
                InGameAssert.IsTrue(flight.PendingGhostPosEntryCountForInGameTest > entriesBefore,
                    "hold-source positioning registered no FloatingOrigin reapply entry");
            }
            finally
            {
                Object.Destroy(probe);
            }

            ParsekLog.Info("TestRunner",
                "ChainTipBlockedGhost: hold-source ghost held its body-fixed end pose on " + body.name);
        }
    }
}
