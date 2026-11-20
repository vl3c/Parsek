using System;
using System.Collections;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live placement of the orbit-route spawn clearance (owner ruling 2026-11-21, todo
    /// SPAWN-ORBITAL-WALKBACK-NOT-WHERE-IT-SPAWNS / SPAWN-RESTING-ON-ROCK), the half the xUnit
    /// suite cannot reach: real stock orbits, real part colliders and the Physics.OverlapBox the
    /// spawn check uses. One test puts a spawn on the active vessel's own orbit point and checks it
    /// moves along that orbit to a clear spot; the other parks a spawn inside a PotatoRoid and
    /// checks it is pushed out with the rock's velocity, then spawns a probe there and watches it
    /// co-move with the rock.
    /// </summary>
    public sealed class OrbitSpawnClearanceInGameTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const string AsteroidPartName = "PotatoRoid";
        private const string ProbePartName = "probeCoreSphere";
        private const double RockSpawnOffsetMeters = 60.0;
        private const float SettleSeconds = 3f;

        private const string IsolatedOnlyBatchSkipReason =
            "Isolated-run only - spawns a PotatoRoid and a probe beside the active vessel. Use Run All + " +
            "Isolated or the row play button in a disposable orbital FLIGHT session; the baseline quickload " +
            "removes both afterwards.";

        [InGameTest(Category = "OrbitSpawnClearance", Scene = GameScenes.FLIGHT,
            Description = "An orbit spawn placed on the active vessel's own orbit point overlaps it and is " +
                "moved a little ahead or behind along that same orbit (same elements, shifted mean anomaly) " +
                "to a spot the live OverlapBox reads clear, within the 1000 m bound")]
        public IEnumerator OrbitSpawnOnTheActiveVessel_MovesAlongItsOwnOrbitToAClearSpot()
        {
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            Vessel active = RequireOrbitingActiveVessel();
            CelestialBody body = active.mainBody;
            double ut = Planetarium.GetUniversalTime();
            Orbit src = active.orbit;
            var spawnOrbit = new Orbit(src.inclination, src.eccentricity, src.semiMajorAxis, src.LAN,
                src.argumentOfPeriapsis, src.meanAnomalyAtEpoch, src.epoch, src.referenceBody);
            Bounds bounds = SpawnCollisionDetector.ComputeVesselBounds(null);

            var (blockedBefore, _, blockerName, _) = SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(
                spawnOrbit.getPositionAtUT(ut), bounds, VesselGhoster.SpawnCollisionPadding);
            if (!blockedBefore)
                InGameAssert.Skip("the active vessel's own orbit point did not read blocked by the live OverlapBox " +
                    "(no part collider there); the test needs a loaded, unpacked vessel with colliders");

            OrbitSpawnClearanceResult result = SpawnCollisionDetector.ResolveOrbitSpawnClearance(
                spawnOrbit, body, ut, bounds, VesselGhoster.SpawnCollisionPadding, 0u, null,
                "ingame-test", "active-vessel-orbit");

            InGameAssert.AreEqual(OrbitSpawnClearanceOutcome.PhaseShifted, result.Outcome,
                $"an orbit spawn on '{active.vesselName}' (blocker '{blockerName}') must move along its orbit");
            InGameAssert.IsTrue(Math.Abs(result.ShiftMeters) <= OrbitSpawnClearance.PhaseShiftMaxMeters,
                $"the shift {result.ShiftMeters.ToString("F1", IC)}m must stay inside the bound");
            InGameAssert.IsTrue(Math.Abs(result.Orbit.semiMajorAxis - src.semiMajorAxis) < 1e-6 * Math.Abs(src.semiMajorAxis)
                && Math.Abs(result.Orbit.eccentricity - src.eccentricity) < 1e-9
                && Math.Abs(result.Orbit.inclination - src.inclination) < 1e-9,
                "the shifted orbit keeps the vessel's own elements");
            double alongOrbit = (result.WorldPos - spawnOrbit.getPositionAtUT(ut + result.ShiftSeconds)).magnitude;
            InGameAssert.IsTrue(alongOrbit < 1.0,
                $"the moved spot is the base orbit's point {result.ShiftSeconds.ToString("F3", IC)}s away " +
                $"(off by {alongOrbit.ToString("F3", IC)}m)");
            var (blockedAfter, _, stillBlocker, _) = SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(
                result.WorldPos, bounds, VesselGhoster.SpawnCollisionPadding);
            InGameAssert.IsFalse(blockedAfter, $"the moved spot must read clear (blocker '{stillBlocker}')");
            ParsekLog.Info("TestRunner", string.Format(IC,
                "OrbitSpawnClearance phase test: shift={0:F1}m shiftSeconds={1:F3} attempts={2}",
                result.ShiftMeters, result.ShiftSeconds, result.Attempts));
        }

        [InGameTest(Category = "OrbitSpawnClearance", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "An orbit spawn parked inside a live PotatoRoid is pushed straight out from the rock's " +
                "centre until its bounds clear the rock's colliders, takes the rock's orbital velocity, and a " +
                "probe spawned on that co-moving orbit stays beside the rock")]
        public IEnumerator OrbitSpawnInsideARock_IsPushedOutAndCoMovesWithTheRock()
        {
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            Vessel active = RequireOrbitingActiveVessel();
            if (PartLoader.getPartInfoByName(AsteroidPartName) == null)
                InGameAssert.Skip($"'{AsteroidPartName}' is not in PartLoader");
            if (PartLoader.getPartInfoByName(ProbePartName) == null)
                InGameAssert.Skip($"'{ProbePartName}' is not in PartLoader");

            string runId = "osc-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Vessel rock = null;
            Vessel probe = null;
            try
            {
                uint rockPid = GrappleCaptureInGameTest.SpawnSinglePartVessel(
                    AsteroidPartName, runId + "-rock", VesselType.SpaceObject, active, RockSpawnOffsetMeters);
                if (rockPid == 0)
                    InGameAssert.Skip("PotatoRoid spawn failed (SpawnAtPosition returned 0)");
                IEnumerator rockWait = GrappleCaptureInGameTest.WaitForSpawnedVesselLive(rockPid, "rock", v => rock = v);
                while (rockWait.MoveNext())
                    yield return rockWait.Current;
                if (rock == null)
                    InGameAssert.Skip("the PotatoRoid never became loaded and unpacked");

                CelestialBody body = rock.mainBody;
                double ut = Planetarium.GetUniversalTime();
                Orbit src = rock.orbit;
                var insideRock = new Orbit(src.inclination, src.eccentricity, src.semiMajorAxis, src.LAN,
                    src.argumentOfPeriapsis, src.meanAnomalyAtEpoch, src.epoch, src.referenceBody);
                Bounds bounds = SpawnCollisionDetector.ComputeVesselBounds(null);
                Vector3d rockCentre = rock.rootPart.transform.position;

                InGameAssert.IsTrue(SpawnCollisionDetector.TryFindRockBlocker(
                        insideRock.getPositionAtUT(ut), bounds, VesselGhoster.SpawnCollisionPadding, ut, body,
                        out SpawnRockBlocker found),
                    "a spawn at the rock's own orbit point must find the rock as its blocker");
                InGameAssert.AreEqual(rock, found.Vessel, "the blocker must be the spawned PotatoRoid");

                OrbitSpawnClearanceResult result = SpawnCollisionDetector.ResolveOrbitSpawnClearance(
                    insideRock, body, ut, bounds, VesselGhoster.SpawnCollisionPadding, 0u, null,
                    "ingame-test", "inside-rock");

                InGameAssert.AreEqual(OrbitSpawnClearanceOutcome.RockPushedOut, result.Outcome,
                    "a spawn inside a rock must be pushed out, not moved along its orbit");
                InGameAssert.IsFalse(SpawnCollisionDetector.TryFindRockBlocker(
                        result.WorldPos, bounds, 0f, ut, body, out _),
                    "the pushed spot must clear the rock's colliders");
                Vector3d rockVelocity = rock.orbit.getOrbitalVelocityAtUT(ut);
                InGameAssert.IsTrue((result.VelocityZup - rockVelocity).magnitude < 1e-3,
                    "the pushed spawn takes the rock's orbital velocity");
                double pushedDistance = (result.WorldPos - rockCentre).magnitude;
                InGameAssert.IsTrue(pushedDistance <= found.Radius
                        + OrbitSpawnClearance.ResolveBoundsRadius(bounds) + OrbitSpawnClearance.RockPushOutMarginMeters + 1.0,
                    $"the push-out stops near the rock (distance {pushedDistance.ToString("F1", IC)}m, rock radius " +
                    $"{found.Radius.ToString("F1", IC)}m)");

                // Spawn a probe on the co-moving orbit and watch it stay beside the rock.
                uint flightId = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
                ConfigNode probeNode = ProtoVessel.CreateVesselNode(runId + "-probe", VesselType.Probe, result.Orbit, 0,
                    new[] { ProtoVessel.CreatePartNode(ProbePartName, flightId) });
                uint probePid = VesselSpawner.SpawnAtPosition(probeNode, body,
                    body.GetLatitude(result.WorldPos), body.GetLongitude(result.WorldPos), body.GetAltitude(result.WorldPos),
                    result.VelocityZup, ut, terminalState: TerminalState.Orbiting, orbitOverride: result.Orbit);
                InGameAssert.IsTrue(probePid != 0, "the probe must spawn on the co-moving orbit");
                IEnumerator probeWait = GrappleCaptureInGameTest.WaitForSpawnedVesselLive(probePid, "probe", v => probe = v);
                while (probeWait.MoveNext())
                    yield return probeWait.Current;
                if (probe == null)
                    InGameAssert.Skip("the probe never became loaded and unpacked");

                double startSeparation = (probe.GetWorldPos3D() - rock.GetWorldPos3D()).magnitude;
                float deadline = Time.realtimeSinceStartup + SettleSeconds;
                while (Time.realtimeSinceStartup < deadline)
                    yield return null;
                double endSeparation = (probe.GetWorldPos3D() - rock.GetWorldPos3D()).magnitude;
                ParsekLog.Info("TestRunner", string.Format(IC,
                    "OrbitSpawnClearance rock test: pushOut={0:F1}m distance={1:F1}m separation {2:F2}m -> {3:F2}m",
                    result.PushOutMeters, pushedDistance, startSeparation, endSeparation));
                InGameAssert.IsTrue(Math.Abs(endSeparation - startSeparation) < 2.0,
                    $"the probe co-moves with the rock (separation {startSeparation.ToString("F2", IC)}m -> " +
                    $"{endSeparation.ToString("F2", IC)}m over {SettleSeconds.ToString("F0", IC)}s)");
            }
            finally
            {
                if (probe != null && probe.state != Vessel.State.DEAD) probe.Die();
                if (rock != null && rock.state != Vessel.State.DEAD) rock.Die();
            }
        }

        private static Vessel RequireOrbitingActiveVessel()
        {
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null)
                InGameAssert.Skip("no active vessel");
            if (active.Landed || active.Splashed || active.situation == Vessel.Situations.PRELAUNCH
                || active.situation == Vessel.Situations.FLYING)
                InGameAssert.Skip($"'{active.vesselName}' is {active.situation}; the orbit-route clearance needs a vessel in space");
            if (!(active.loaded && !active.packed))
                InGameAssert.Skip($"'{active.vesselName}' is not loaded and unpacked");
            if (active.orbit == null || active.orbit.referenceBody == null)
                InGameAssert.Skip($"'{active.vesselName}' has no orbit");
            return active;
        }
    }
}
