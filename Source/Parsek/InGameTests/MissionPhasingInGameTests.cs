using System;

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
            if (v.orbit == null || v.orbit.eccentricity >= 1.0 || !(v.orbit.period > 0.0)
                || double.IsNaN(v.orbit.period))
            {
                // The seam only resolves CLOSED orbits by contract; a suborbital / landed active
                // vessel must fail closed rather than report a bogus period.
                bool openResolved = FlightGlobalsBodyInfo.Instance.TryGetVesselOrbit(
                    v.persistentId, v.id.ToString(), out _, out _);
                InGameAssert.IsFalse(openResolved,
                    "non-closed-orbit active vessel does not resolve (fail closed)");
                ParsekLog.Info("InGameTest",
                    "[MissionPhasing] active vessel has no closed orbit; fail-closed contract verified");
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
    }
}
