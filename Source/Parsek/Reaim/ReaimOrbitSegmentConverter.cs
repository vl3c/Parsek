namespace Parsek.Reaim
{
    // Converts a live KSP Orbit into the OrbitSegment value the render path consumes (docs/dev/plans/
    // reaim-interplanetary-transfers.md, Phase 3c). Live (touches the Unity Orbit type); the field
    // mapping matches the recorder's own Orbit->OrbitSegment snapshot (PatchedConicSnapshot:
    // inc/LAN/argPe in DEGREES, meanAnomalyAtEpoch in RADIANS, epoch in UT) so the playback
    // reconstruction (TrajectoryMath: new Orbit(inc, e, sma, lan, argPe, mEp, epoch, body)) round-trips
    // exactly. The in-game canary/test exercises it (it cannot run off-Unity).
    internal static class ReaimOrbitSegmentConverter
    {
        /// <summary>
        /// Snapshots <paramref name="orbit"/>'s Kepler elements into an OrbitSegment bodied as
        /// <paramref name="bodyName"/> (the heliocentric transfer's common-ancestor body). startUT /
        /// endUT are left at 0 - the assembler stamps them. <c>isPredicted</c> is false: this is the
        /// authoritative synthesized transfer, not a ballistic guess.
        /// </summary>
        internal static OrbitSegment ToSegment(Orbit orbit, string bodyName)
        {
            return new OrbitSegment
            {
                inclination = orbit.inclination,
                eccentricity = orbit.eccentricity,
                semiMajorAxis = orbit.semiMajorAxis,
                longitudeOfAscendingNode = orbit.LAN,
                argumentOfPeriapsis = orbit.argumentOfPeriapsis,
                meanAnomalyAtEpoch = orbit.meanAnomalyAtEpoch,
                epoch = orbit.epoch,
                bodyName = bodyName,
                isPredicted = false
            };
        }
    }
}
