using System;
using System.Collections.Generic;

namespace Parsek.InGameTests
{
    // MissionPhasing in-game tests (M4a follow-up + M4b): the Unity-bound live-vessel resolution
    // seam the VesselOrbital constraint and the phasing knob solve against. The schedule / span
    // clock / partition logic is pure and fully covered by the xUnit suite
    // (MissionLoiterKnobTests / MissionPeriodicityTests); only the FlightGlobals-backed
    // TryGetVesselOrbit needs a live game. A full synthetic station-resupply scenario injection
    // stays a playtest concern (the generators can author vessel-anchored Relative sections, but
    // the in-game injection of a live anchor vessel is the playtest's job).
    internal static class MissionPhasingInGameTests
    {
        [InGameTest(Category = "MissionPhasing", Scene = GameScenes.FLIGHT,
            Description = "TryGetVesselOrbit resolves the active vessel's live orbit by pid+guid")]
        public static void TryGetVesselOrbit_ResolvesActiveVessel()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            InGameAssert.IsNotNull(v, "active vessel exists");
            // A landed / splashed / prelaunch vessel carries a stock PSEUDO-orbit (extreme
            // eccentricity, finite period) that passes the closed-orbit filters, and so does an
            // aircraft FLYING or an ascending SUB_ORBITAL rocket. All three fail the ORBIT
            // contract - the orbit intersects the surface or the atmosphere
            // (PERIODICITY-LANDED-ANCHOR-PHASE-LOCK) - not the open-orbit filters.
            bool onSurface = MissionPeriodicity.IsSurfaceAnchorSituation(
                v.LandedOrSplashed, v.situation);
            bool orbitReenters = !onSurface && !IsLiveOrbitAPhaseReference(v);
            if (onSurface || orbitReenters || v.orbit == null || v.orbit.eccentricity >= 1.0
                || !(v.orbit.period > 0.0) || double.IsNaN(v.orbit.period))
            {
                // The seam only resolves CLOSED orbits that clear the surface / atmosphere by
                // contract; a suborbital / flying / landed active vessel must fail closed rather
                // than report a bogus period.
                bool openResolved = FlightGlobalsBodyInfo.Instance.TryGetVesselOrbit(
                    v.persistentId, v.id.ToString(), out _, out _);
                InGameAssert.IsFalse(openResolved,
                    "non-closed-orbit / surface / atmosphere-intersecting active vessel does not "
                    + "resolve (fail closed)");
                ParsekLog.Info("InGameTest",
                    "[MissionPhasing] active vessel is not an eligible phase anchor " +
                    $"(onSurface={onSurface} orbitReenters={orbitReenters} " +
                    $"situation={v.situation}); fail-closed contract verified");
                return;
            }

            bool ok = FlightGlobalsBodyInfo.Instance.TryGetVesselOrbit(
                v.persistentId, v.id.ToString(), out double period, out string bodyName);
            InGameAssert.IsTrue(ok, "active vessel resolves by pid + own guid");
            InGameAssert.IsTrue(period > 0.0 && !double.IsNaN(period),
                $"resolved period is positive (got {period})");
            InGameAssert.AreEqual(v.orbit.referenceBody.bodyName, bodyName,
                "resolved orbit body matches the live orbit");
            // Within 1% of the live orbital period (the same orbit, read back through the seam).
            InGameAssert.IsTrue(Math.Abs(period - v.orbit.period) <= 0.01 * v.orbit.period,
                $"period {period:F1}s matches live {v.orbit.period:F1}s");
        }

        [InGameTest(Category = "MissionPhasing", Scene = GameScenes.FLIGHT,
            Description = "TryGetVesselOrbit fails closed for a bogus pid and a foreign-launch guid")]
        public static void TryGetVesselOrbit_FailsClosedOnIdentityMismatch()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            InGameAssert.IsNotNull(v, "active vessel exists");

            bool bogusPid = FlightGlobalsBodyInfo.Instance.TryGetVesselOrbit(
                0xDEADBEEFu, null, out _, out _);
            InGameAssert.IsFalse(bogusPid, "a pid not present in the save does not resolve");

            // The active vessel's pid with a DIFFERENT launch guid: the craft-baked-pid trap.
            // GuidsConclusivelyDiffer must gate the match (persistentId is not launch-unique).
            string foreignGuid = Guid.NewGuid().ToString();
            bool foreign = FlightGlobalsBodyInfo.Instance.TryGetVesselOrbit(
                v.persistentId, foreignGuid, out _, out _);
            InGameAssert.IsFalse(foreign,
                "the live pid with a conclusively different launch guid does not resolve");
        }

        [InGameTest(Category = "MissionPhasing", Scene = GameScenes.FLIGHT,
            Description = "A LANDED/PRELAUNCH anchor emits NO VesselOrbital constraint: the stock "
                + "surface pseudo-orbit passes the closed-orbit filters, so without the "
                + "IsPhaseAnchorEligible guard a surface-only relay tree acquires an orbital "
                + "phase lock it can never satisfy (PERIODICITY-LANDED-ANCHOR-PHASE-LOCK)")]
        public static void LandedAnchor_EmitsNoVesselOrbitalConstraint()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            InGameAssert.IsNotNull(v, "active vessel exists");
            if (!MissionPeriodicity.IsSurfaceAnchorSituation(v.LandedOrSplashed, v.situation))
            {
                InGameAssert.Skip(
                    "needs a LANDED / SPLASHED / PRELAUNCH active vessel to stand in for the "
                    + $"surface anchor (situation={v.situation})");
                return;
            }

            // The guard is load-bearing only if the pseudo-orbit would otherwise have passed:
            // record what stock actually reports for this landed craft.
            Orbit orbit = v.orbit;
            InGameAssert.IsNotNull(orbit, "a landed vessel still carries a stock pseudo-orbit");
            ParsekLog.Info("InGameTest",
                "[MissionPhasing] landed pseudo-orbit: " +
                $"situation={v.situation} ecc={orbit.eccentricity:F4} period={orbit.period:F2}s " +
                $"body={(orbit.referenceBody != null ? orbit.referenceBody.bodyName : "<none>")}");

            // ONE foreign anchor naming the landed active vessel by pid, launch body = the body
            // it sits on: the exact shape that emitted the bad lock. SectionPid is stamped (so no
            // committed-recording lookup runs) and selfPid is 0 (so the self-partition cannot
            // reattribute it) - the ONLY thing that can reject here is the live-orbit rule.
            double ut0 = Planetarium.GetUniversalTime();
            var anchors = new Dictionary<string, MissionPeriodicity.VesselAnchorInfo>
            {
                ["landed-anchor"] = new MissionPeriodicity.VesselAnchorInfo
                {
                    SectionPid = v.persistentId,
                    EarliestUT = ut0 + 10.0,
                    AnchorRecordingId = null,
                    OwnerPid = 0,
                    OwnerGuid = null,
                    OwnerRecordingId = null
                }
            };

            MissionPeriodicity.VesselOrbitalClassification cls =
                MissionPeriodicity.ClassifyVesselOrbitalConstraint(
                    anchors,
                    v.mainBody != null ? v.mainBody.bodyName : null,
                    ut0,
                    selfPid: 0,
                    selfGuid: null,
                    bodyInfo: FlightGlobalsBodyInfo.Instance,
                    committed: new List<Recording>(),
                    bodyConstraints: new List<PhaseConstraint>(),
                    out PhaseConstraint constraint,
                    out string rejectReason,
                    out _);

            InGameAssert.AreEqual(
                MissionPeriodicity.VesselOrbitalClassification.Rejected, cls,
                $"a landed anchor is rejected, not emitted (reason={rejectReason ?? "<none>"})");
            InGameAssert.AreNotEqual(
                ConstraintKind.VesselOrbital, constraint.Kind,
                "no VesselOrbital constraint is produced for a landed anchor");

            bool seamResolved = FlightGlobalsBodyInfo.Instance.TryGetVesselOrbit(
                v.persistentId, v.id.ToString(), out _, out _);
            InGameAssert.IsFalse(seamResolved,
                "the live seam itself refuses the landed anchor (the guard, not a downstream rule)");
        }

        [InGameTest(Category = "MissionPhasing", Scene = GameScenes.FLIGHT,
            Description = "An AIRBORNE anchor whose orbit re-enters (aircraft FLYING, ascending "
                + "SUB_ORBITAL rocket, or any periapsis inside the atmosphere) is refused by the "
                + "orbit contract: stock reports a closed orbit with a finite period for it, and "
                + "locking a mission to that number is as meaningless as locking to a landed "
                + "craft's pseudo-orbit")]
        public static void AirborneAnchor_RefusedByTheOrbitContract()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            InGameAssert.IsNotNull(v, "active vessel exists");
            if (MissionPeriodicity.IsSurfaceAnchorSituation(v.LandedOrSplashed, v.situation))
            {
                InGameAssert.Skip(
                    "needs an AIRBORNE active vessel whose orbit re-enters (FLYING / SUB_ORBITAL, "
                    + "or any orbit with periapsis inside the atmosphere); the active vessel is "
                    + $"on the surface (situation={v.situation})");
                return;
            }
            Orbit orbit = v.orbit;
            CelestialBody refBody = orbit != null ? orbit.referenceBody : null;
            if (orbit == null || refBody == null)
            {
                InGameAssert.Skip(
                    "needs an airborne active vessel carrying a stock orbit around a resolvable "
                    + "body to read PeA / atmosphereDepth from");
                return;
            }
            if (IsLiveOrbitAPhaseReference(v))
            {
                InGameAssert.Skip(
                    "needs an active vessel whose orbit INTERSECTS the surface or atmosphere "
                    + $"(peA={orbit.PeA:F1} clears {refBody.bodyName}'s floor, so it is a genuine "
                    + "orbiting anchor)");
                return;
            }

            // The contract is load-bearing only if stock would otherwise have handed the solver a
            // usable number: record what it actually reports for this airborne craft.
            ParsekLog.Info("InGameTest",
                "[MissionPhasing] airborne orbit: " +
                $"situation={v.situation} ecc={orbit.eccentricity:F4} period={orbit.period:F2}s " +
                $"peA={orbit.PeA:F1} body={refBody.bodyName} atmosphere={refBody.atmosphere} " +
                $"atmosphereDepth={refBody.atmosphereDepth:F1}");

            InGameAssert.IsFalse(
                MissionPeriodicity.IsPhaseAnchorEligible(
                    orbit.PeA, refBody.atmosphere, refBody.atmosphereDepth),
                "the pure contract refuses an orbit that intersects the surface / atmosphere");

            bool seamResolved = FlightGlobalsBodyInfo.Instance.TryGetVesselOrbit(
                v.persistentId, v.id.ToString(), out double period, out _);
            InGameAssert.IsFalse(seamResolved,
                "the live seam refuses the airborne anchor (the orbit contract, not a "
                + "downstream rule)");
            InGameAssert.IsTrue(double.IsNaN(period),
                "a refused anchor reports no period at all (fail closed, not a bogus number)");
        }

        // The live-orbit half of the eligibility contract, read off the vessel the way
        // FlightGlobalsBodyInfo.TryGetVesselOrbit reads it. False when there is no orbit or no
        // reference body to place it against (fail closed, mirroring the seam).
        private static bool IsLiveOrbitAPhaseReference(Vessel v)
        {
            Orbit orbit = v != null ? v.orbit : null;
            CelestialBody refBody = orbit != null ? orbit.referenceBody : null;
            if (refBody == null)
                return false;
            return MissionPeriodicity.IsPhaseAnchorEligible(
                orbit.PeA, refBody.atmosphere, refBody.atmosphereDepth);
        }
    }
}
