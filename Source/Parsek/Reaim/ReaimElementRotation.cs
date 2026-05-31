using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Reaim
{
    // Live (Unity-bound) element rotation for the re-aim arrival-seam restitch (docs/dev/plans/
    // reaim-arrival-seam-restitch.md section 5.1). Rotates a target-body OrbitSegment's orientation by a
    // rigid Zup rotation R about the body center using KSP's OWN elements <-> state-vector conversion, so
    // the Zup angle convention (degrees vs radians, inc [0,180], LAN/argPe wrap, degenerate-node clamp) is
    // applied by stock KSP rather than hand-derived: this is correct-by-construction, which matters because
    // the Zup convention cannot be playtested during this run. Canary-tested (it needs a live KSP Orbit);
    // the pure geometry it relies on lives in ReaimRotation and is xUnit-tested.
    //
    // FRAME (plan 4.1): getRelativePositionAtUT / getOrbitalVelocityAtUT return Zup body-relative vectors,
    // and UpdateFromStateVectors expects Zup body-relative inputs (see OrbitReseed.cs). R maps Zup -> Zup.
    // NO .xzy is applied anywhere in this pipeline.
    internal static class ReaimElementRotation
    {
        // Reconstructs the live KSP Orbit for an OrbitSegment about body (mirrors TrajectoryMath's
        // playback reconstruction: inc/LAN/argPe degrees, meanAnomalyAtEpoch radians, epoch UT).
        private static Orbit BuildOrbit(OrbitSegment seg, CelestialBody body)
        {
            return new Orbit(
                seg.inclination, seg.eccentricity, seg.semiMajorAxis,
                seg.longitudeOfAscendingNode, seg.argumentOfPeriapsis,
                seg.meanAnomalyAtEpoch, seg.epoch, body);
        }

        /// <summary>
        /// Returns a copy of <paramref name="seg"/> with its orbital ORIENTATION rigidly rotated by the
        /// Zup rotation <paramref name="r"/> about <paramref name="body"/>'s center, by:
        ///   1. build the segment's Orbit, sample its Zup state at epoch (pos + vel, NO .xzy),
        ///   2. rotate BOTH pos and vel by R (ReaimRotation.RotateVector),
        ///   3. UpdateFromStateVectors(rotPos, rotVel, body, epoch) and read back (inc, LAN, argPe, mEp,
        ///      epoch) into the copy.
        /// Eccentricity, semiMajorAxis, startUT, endUT, bodyName, isPredicted, orbitalFrameRotation, and
        /// angularVelocity are copied VERBATIM from the original: the rigid rotation preserves shape +
        /// along-orbit phase, and the velocity-frame-relative fields follow the rotated velocity
        /// automatically. At R == identity this reproduces the segment TO ROUND-OFF (a rigid rotation
        /// preserves ecc/sma/mEp; the always-on UpdateFromStateVectors round-trip re-derives the read-back
        /// elements within floating-point round-off, not byte-identically; the pure RotateVector identity
        /// path IS byte-identical, but the resolver never invokes this with identity because it returns
        /// faithful before computing R). Returns the original unchanged when <paramref name="body"/> is null
        /// or the rotated state is non-finite (fail closed; never emit a corrupt segment).
        /// </summary>
        internal static OrbitSegment RotateSegmentOrientation(OrbitSegment seg, CelestialBody body, double[,] r)
        {
            if (body == null)
                return seg;

            Orbit src = BuildOrbit(seg, body);
            Vector3d posZup = src.getRelativePositionAtUT(seg.epoch);
            Vector3d velZup = src.getOrbitalVelocityAtUT(seg.epoch);
            if (!IsFinite(posZup) || !IsFinite(velZup))
                return seg;

            double[] rotPos = ReaimRotation.RotateVector(r, new[] { posZup.x, posZup.y, posZup.z });
            double[] rotVel = ReaimRotation.RotateVector(r, new[] { velZup.x, velZup.y, velZup.z });
            var rotPosV = new Vector3d(rotPos[0], rotPos[1], rotPos[2]);
            var rotVelV = new Vector3d(rotVel[0], rotVel[1], rotVel[2]);

            var rotated = new Orbit();
            rotated.UpdateFromStateVectors(rotPosV, rotVelV, body, seg.epoch);
            if (double.IsNaN(rotated.inclination) || double.IsNaN(rotated.LAN)
                || double.IsNaN(rotated.argumentOfPeriapsis) || double.IsNaN(rotated.meanAnomalyAtEpoch))
                return seg;

            OrbitSegment outSeg = seg; // value-copy: carries ecc/sma/UTs/body/flags/ofrRot/angVel verbatim
            outSeg.inclination = rotated.inclination;
            outSeg.longitudeOfAscendingNode = rotated.LAN;
            outSeg.argumentOfPeriapsis = rotated.argumentOfPeriapsis;
            outSeg.meanAnomalyAtEpoch = rotated.meanAnomalyAtEpoch;
            outSeg.epoch = rotated.epoch;
            return outSeg;
        }

        /// <summary>
        /// Rotates every non-predicted, <paramref name="targetBody"/>-bodied OrbitSegment in
        /// <paramref name="segments"/> whose startUT &gt;= <paramref name="recordedArrivalUT"/> - eps by
        /// the Zup rotation <paramref name="r"/> about the target body's center (the recorded arrival
        /// sub-chain: approach hyperbola + capture + low-orbit descent). Every other segment (the Sun
        /// transfer, the launch body, a moon-relative excursion, predicted tails, pre-arrival segments) is
        /// kept verbatim. Mutates the list IN PLACE (the caller owns the per-window assembled list).
        /// Returns the count rotated; outputs the count skipped for being non-target-bodied so the moon
        /// flyby residual (plan section 7) is visible in the log. Pure dispatch over a live helper.
        /// </summary>
        internal static int RotateBodyRelativeSegments(
            List<OrbitSegment> segments, CelestialBody targetBody, double recordedArrivalUT, double[,] r,
            out int skippedNonTarget)
        {
            skippedNonTarget = 0;
            if (segments == null || targetBody == null)
                return 0;
            const double eps = 1.0; // 1s slack on the arrival boundary (classifier UT vs segment startUT)
            int rotated = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                OrbitSegment s = segments[i];
                bool isPostArrival = s.startUT >= recordedArrivalUT - eps;
                bool isTargetBodied = !s.isPredicted && s.bodyName == targetBody.bodyName;
                if (isPostArrival && isTargetBodied)
                {
                    segments[i] = RotateSegmentOrientation(s, targetBody, r);
                    rotated++;
                }
                else if (isPostArrival && !s.isPredicted)
                {
                    // A non-predicted, non-target-bodied post-arrival segment (e.g. an Ike moon excursion)
                    // is NOT rotated in v1: count it so the residual at the moon boundary is visible in the
                    // log (plan section 7). Pre-arrival and predicted segments pass through silently.
                    skippedNonTarget++;
                }
            }
            return rotated;
        }

        /// <summary>
        /// Derives the recorded arrival's inbound-asymptote direction (s_rec) and plane normal (h_rec) in
        /// Zup from the recorded ArrivalLeg OrbitSegment, by reconstructing its Orbit about
        /// <paramref name="targetBody"/> and reading (e, h) via KSP's own GetEccVector / GetOrbitNormal
        /// (Zup), then the analytic asymptote. Returns false when the arrival leg is not hyperbolic (ecc
        /// &lt;= 1: a captured ellipse with no incoming asymptote) or the geometry is degenerate, so the
        /// caller keeps the faithful path for that window. Live (needs the KSP Orbit); the asymptote math
        /// is the pure ReaimRotation.InboundAsymptoteDir.
        /// </summary>
        internal static bool TryRecordedArrivalFrame(
            OrbitSegment arrivalLeg, CelestialBody targetBody,
            out double[] sRec, out double[] hRec, out double recordedEcc)
        {
            sRec = null;
            hRec = null;
            recordedEcc = double.NaN;
            if (targetBody == null)
                return false;
            Orbit recorded = BuildOrbit(arrivalLeg, targetBody);
            Vector3d eVecV = recorded.GetEccVector(); // Zup eccentricity vector
            Vector3d hVecV = recorded.GetOrbitNormal(); // Zup angular-momentum direction (plane normal)
            recordedEcc = recorded.eccentricity;
            var eVec = new[] { eVecV.x, eVecV.y, eVecV.z };
            var hVec = new[] { hVecV.x, hVecV.y, hVecV.z };
            sRec = ReaimRotation.InboundAsymptoteDir(eVec, hVec, recorded.eccentricity);
            hRec = ReaimRotation.Normalize(hVec);
            return sRec != null && hRec != null;
        }

        /// <summary>
        /// Derives the RE-AIMED approach's inbound-asymptote direction (s_re) and plane normal (h_re) in
        /// Zup from the synthesized heliocentric <paramref name="transferOrbit"/> at the target-SOI-entry
        /// instant <paramref name="soiEntryUT"/> (plan 4.3 re-aimed side). Computes the target-relative
        /// state in Zup (both Sun-relative positions/velocities subtracted, NO .xzy):
        ///   r_rel = transfer.getRelativePositionAtUT(soiEntryUT) - target.orbit.getRelativePositionAtUT(soiEntryUT)
        ///   v_rel = transfer.getOrbitalVelocityAtUT(soiEntryUT) - target.orbit.getOrbitalVelocityAtUT(soiEntryUT)
        ///   h_re = r_rel cross v_rel
        ///   e_re = (v_rel cross h_re)/mu_target - r_rel_hat
        /// then the analytic asymptote. Returns false when the geometry is degenerate or the relative conic
        /// is not hyperbolic (ecc &lt;= 1; an arrival that is already captured has no incoming asymptote),
        /// so the caller keeps the faithful path. Live (needs the live transfer + target orbit). The target
        /// orbit and the transfer orbit are both Sun-relative, so their difference is target-relative in the
        /// same Zup frame.
        /// </summary>
        internal static bool TryReaimedArrivalFrame(
            Orbit transferOrbit, CelestialBody targetBody, double soiEntryUT,
            out double[] sRe, out double[] hRe, out double reaimedEcc)
        {
            sRe = null;
            hRe = null;
            reaimedEcc = double.NaN;
            if (transferOrbit == null || targetBody == null || targetBody.orbit == null
                || double.IsNaN(soiEntryUT))
                return false;

            Vector3d rXfer = transferOrbit.getRelativePositionAtUT(soiEntryUT);
            Vector3d rTarget = targetBody.orbit.getRelativePositionAtUT(soiEntryUT);
            Vector3d vXfer = transferOrbit.getOrbitalVelocityAtUT(soiEntryUT);
            Vector3d vTarget = targetBody.orbit.getOrbitalVelocityAtUT(soiEntryUT);
            if (!IsFinite(rXfer) || !IsFinite(rTarget) || !IsFinite(vXfer) || !IsFinite(vTarget))
                return false;

            double[] rRel = { rXfer.x - rTarget.x, rXfer.y - rTarget.y, rXfer.z - rTarget.z };
            double[] vRel = { vXfer.x - vTarget.x, vXfer.y - vTarget.y, vXfer.z - vTarget.z };
            double[] rRelHat = ReaimRotation.Normalize(rRel);
            if (rRelHat == null)
                return false;

            double[] hReVec = ReaimRotation.Cross(rRel, vRel);
            double[] hReHat = ReaimRotation.Normalize(hReVec);
            if (hReHat == null)
                return false;

            double mu = targetBody.gravParameter;
            if (mu <= 0.0 || double.IsNaN(mu))
                return false;
            double[] vCrossH = ReaimRotation.Cross(vRel, hReVec);
            double[] eReVec =
            {
                vCrossH[0] / mu - rRelHat[0],
                vCrossH[1] / mu - rRelHat[1],
                vCrossH[2] / mu - rRelHat[2]
            };
            reaimedEcc = ReaimRotation.Magnitude(eReVec);

            sRe = ReaimRotation.InboundAsymptoteDir(eReVec, hReVec, reaimedEcc);
            hRe = hReHat;
            return sRe != null && hRe != null;
        }

        private static bool IsFinite(Vector3d v)
        {
            return !(double.IsNaN(v.x) || double.IsNaN(v.y) || double.IsNaN(v.z)
                || double.IsInfinity(v.x) || double.IsInfinity(v.y) || double.IsInfinity(v.z));
        }

        // Diagnostic helper for the resolver one-shot log (kept here so the InvariantCulture formatting of
        // a 3x3 matrix angle is reused). Not load-bearing.
        internal static string DescribeRotation(double[,] r)
        {
            if (r == null)
                return "R=<null>";
            var ic = CultureInfo.InvariantCulture;
            double angle = ReaimRotation.RotationAngleRadians(r);
            return $"R-angle={(angle * 180.0 / System.Math.PI).ToString("F2", ic)}deg";
        }
    }
}
