using System.Globalization;

namespace Parsek.Reaim
{
    // Live (Unity-bound) extraction of the recorded + candidate arrival v_inf and the SOI-edge seam for the
    // re-aim arrival-seam SOI-timing objective (docs/dev/plans/reaim-arrival-seam-timing.md). These touch
    // live KSP Orbits (building the recorded arrival conic, sampling the synthesized transfer's
    // target-relative state), so they sit beside the resolver and are canary-tested; the pure geometry they
    // call (asymptote, magnitude, vis-viva sma, objective, decline gate) lives in ReaimArrivalGeometry and
    // is xUnit-tested.
    //
    // FRAME DISCIPLINE (the #1 risk, same as #983): the ENTIRE v_inf computation stays in KSP's swizzled Zup
    // frame. getRelativePositionAtUT / getOrbitalVelocityAtUT / GetEccVector / GetOrbitNormal all return
    // Zup. NO .xzy is applied anywhere in this pipeline. The candidate v_inf and the recorded v_inf are both
    // the analytic asymptotic v_inf from (e, h) + magnitude, in the SAME Zup frame, so the comparison is
    // apples-to-apples (both asymptotic, not instantaneous SOI-edge velocities).
    internal static class ReaimArrivalVInf
    {
        // Reconstructs the live KSP Orbit for an OrbitSegment about body (mirrors TrajectoryMath's playback
        // reconstruction: inc/LAN/argPe degrees, meanAnomalyAtEpoch radians, epoch UT). Salvaged from the
        // #983 ReaimElementRotation.BuildOrbit.
        private static Orbit BuildOrbit(OrbitSegment seg, CelestialBody body)
        {
            return new Orbit(
                seg.inclination, seg.eccentricity, seg.semiMajorAxis,
                seg.longitudeOfAscendingNode, seg.argumentOfPeriapsis,
                seg.meanAnomalyAtEpoch, seg.epoch, body);
        }

        /// <summary>
        /// The recorded arrival v_inf VECTOR (magnitude * inbound-asymptote direction) in Zup, from the
        /// recorded <paramref name="arrivalLeg"/> OrbitSegment reconstructed about <paramref name="targetBody"/>:
        /// direction via GetEccVector / GetOrbitNormal -> the analytic asymptote; magnitude via
        /// sqrt(mu / (-arrivalLeg.semiMajorAxis)). Returns null when the arrival leg is not hyperbolic
        /// (ecc &lt;= 1: a captured ellipse with no incoming asymptote) or the geometry is degenerate, so the
        /// caller keeps the faithful path for that window. Live (needs the KSP Orbit). The recorded arrival
        /// hyperbola is itself the fixed, known thing we are matching against.
        /// </summary>
        internal static double[] RecordedArrivalVInf(OrbitSegment arrivalLeg, CelestialBody targetBody)
        {
            if (targetBody == null)
                return null;
            double mu = targetBody.gravParameter;
            Orbit recorded = BuildOrbit(arrivalLeg, targetBody);
            Vector3d eVecV = recorded.GetEccVector();   // Zup eccentricity vector
            Vector3d hVecV = recorded.GetOrbitNormal(); // Zup angular-momentum direction (plane normal)
            if (!IsFinite(eVecV) || !IsFinite(hVecV))
                return null;
            var eVec = new[] { eVecV.x, eVecV.y, eVecV.z };
            var hVec = new[] { hVecV.x, hVecV.y, hVecV.z };
            // Magnitude from the RECORDED sma (negative for a hyperbola), not the read-back Orbit (which can
            // re-derive a slightly different sma): the recorded sma is the authoritative arrival energy.
            return ReaimArrivalGeometry.InboundVInfVector(eVec, hVec, arrivalLeg.semiMajorAxis, mu);
        }

        /// <summary>
        /// The candidate arrival v_inf VECTOR (magnitude * inbound-asymptote direction) in Zup, from the
        /// synthesized heliocentric <paramref name="transferOrbit"/>'s target-relative state at the
        /// target-SOI-entry instant <paramref name="soiEntryUT"/> (the frame-tested #983 path):
        ///   r_rel = transfer.getRelativePositionAtUT(soiEntryUT) - target.orbit.getRelativePositionAtUT(soiEntryUT)
        ///   v_rel = transfer.getOrbitalVelocityAtUT(soiEntryUT) - target.orbit.getOrbitalVelocityAtUT(soiEntryUT)
        ///   h = r_rel cross v_rel, e = (v_rel cross h)/mu - r_rel_hat, sma via vis-viva, asymptote from (e,h),
        ///   |v_inf| = sqrt(mu / (-sma)).
        /// Both Sun-relative positions/velocities are subtracted (NO .xzy), so r_rel / v_rel are
        /// target-relative in the same Zup frame. Returns null when the geometry is degenerate or the
        /// relative conic is not hyperbolic (ecc &lt;= 1; an arrival that is already captured has no incoming
        /// asymptote), so the caller declines that candidate. Live (needs the live transfer + target orbit).
        /// </summary>
        internal static double[] CandidateArrivalVInf(
            Orbit transferOrbit, CelestialBody targetBody, double soiEntryUT)
        {
            if (transferOrbit == null || targetBody == null || targetBody.orbit == null
                || double.IsNaN(soiEntryUT) || double.IsInfinity(soiEntryUT))
                return null;

            Vector3d rXfer = transferOrbit.getRelativePositionAtUT(soiEntryUT);
            Vector3d rTarget = targetBody.orbit.getRelativePositionAtUT(soiEntryUT);
            Vector3d vXfer = transferOrbit.getOrbitalVelocityAtUT(soiEntryUT);
            Vector3d vTarget = targetBody.orbit.getOrbitalVelocityAtUT(soiEntryUT);
            if (!IsFinite(rXfer) || !IsFinite(rTarget) || !IsFinite(vXfer) || !IsFinite(vTarget))
                return null;

            double[] rRel = { rXfer.x - rTarget.x, rXfer.y - rTarget.y, rXfer.z - rTarget.z };
            double[] vRel = { vXfer.x - vTarget.x, vXfer.y - vTarget.y, vXfer.z - vTarget.z };

            double mu = targetBody.gravParameter;
            double[] eVec = ReaimArrivalGeometry.EccentricityVectorFromState(rRel, vRel, mu);
            double[] hVec = ReaimArrivalGeometry.Cross(rRel, vRel);
            double sma = ReaimArrivalGeometry.SemiMajorAxisFromState(rRel, vRel, mu);
            return ReaimArrivalGeometry.InboundVInfVector(eVec, hVec, sma, mu);
        }

        /// <summary>
        /// The SOI-edge seam magnitude (metres) a transfer produces: the target-relative position
        /// discontinuity between the synthesized transfer's end state (at <paramref name="soiEntryUT"/>) and
        /// the recorded arrival leg's START state (at its own recorded start UT), both relative to the LIVE
        /// target body in Zup (target-relative position subtracted, NO .xzy). This is the cosmetic jump the
        /// ghost shows at the SOI boundary: the recorded arrival hyperbola is spliced on where the transfer
        /// hands off, so a large gap between the two target-relative positions is the visible seam. Returns
        /// NaN when any state is non-finite. Live.
        /// </summary>
        internal static double SoiEdgeSeamMeters(
            Orbit transferOrbit, OrbitSegment arrivalLeg, CelestialBody targetBody, double soiEntryUT)
        {
            if (transferOrbit == null || targetBody == null || targetBody.orbit == null
                || double.IsNaN(soiEntryUT) || double.IsInfinity(soiEntryUT))
                return double.NaN;

            // Transfer end state, target-relative (Zup).
            Vector3d rXfer = transferOrbit.getRelativePositionAtUT(soiEntryUT);
            Vector3d rTargetAtEntry = targetBody.orbit.getRelativePositionAtUT(soiEntryUT);
            if (!IsFinite(rXfer) || !IsFinite(rTargetAtEntry))
                return double.NaN;
            Vector3d xferRel = rXfer - rTargetAtEntry;

            // Recorded arrival hyperbola START state, target-relative (Zup): the recorded leg is already
            // reconstructed about the target body, so its own getRelativePositionAtUT at its start UT IS the
            // target-relative position (the recorded arrival is verbatim relative to the exact target).
            Orbit recorded = BuildOrbit(arrivalLeg, targetBody);
            Vector3d arrivalRel = recorded.getRelativePositionAtUT(arrivalLeg.startUT);
            if (!IsFinite(arrivalRel))
                return double.NaN;

            Vector3d diff = xferRel - arrivalRel;
            return diff.magnitude;
        }

        private static bool IsFinite(Vector3d v)
        {
            return !(double.IsNaN(v.x) || double.IsNaN(v.y) || double.IsNaN(v.z)
                || double.IsInfinity(v.x) || double.IsInfinity(v.y) || double.IsInfinity(v.z));
        }

        /// <summary>
        /// Formats a v_inf vector for the diagnostic log: |v_inf| magnitude only (the direction is implicit
        /// in the residual angle logged alongside). InvariantCulture. Returns "null" for a null vector.
        /// </summary>
        internal static string FormatVInfMag(double[] vInf)
        {
            if (vInf == null)
                return "null";
            double mag = ReaimArrivalGeometry.Magnitude(vInf);
            return mag.ToString("F1", CultureInfo.InvariantCulture);
        }
    }
}
