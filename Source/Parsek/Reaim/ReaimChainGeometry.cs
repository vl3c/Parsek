using System;

namespace Parsek.Reaim
{
    // PURE state-vector geometry for the re-aim whole-chain synthesis (reaim-fix-plan.md, STEP 1/2).
    // These are the pieces of the escape / capture leg construction that DON'T need a live Unity scene -
    // the SOI-sphere-crossing bisection (driven by a sampled-distance delegate the live caller supplies),
    // the body-relative velocity conversion (vRel = v_heliocentric - v_body), and the .xzy round-trip
    // identity. The live glue (Orbit.UpdateFromStateVectors, CelestialBody.orbit, PatchedConics) stays in
    // ReaimTransferSynthesizer and is exercised by the in-game canary; the math below is xUnit-tested.
    //
    // Frame contract (the load-bearing detail, mirrored from ReaimTransferSynthesizer): a body's orbit
    // relative position/velocity come back YZ-swizzled (AliceWorld). The transfer is built in the same
    // swizzled frame as the bodies, so the difference rEntry = transfer.relPos - body.relPos and the
    // velocity difference vRel = v - body.orbitalVel are taken in ONE consistent frame and then fed to
    // Orbit.UpdateFromStateVectors verbatim. Re-aim's transfer build does its differencing on the
    // UN-swizzled (.xzy) vectors and re-swizzles (.xzy is its own inverse) for UpdateFromStateVectors; the
    // capture / escape construction MUST mirror that exactly. RelativeVelocity below performs only the
    // subtraction (a frame-agnostic operation: if both operands are in the same frame the result is too),
    // so the live caller controls the swizzling around it identically to the transfer build.
    internal static class ReaimChainGeometry
    {
        /// <summary>
        /// Body-relative velocity at a SOI crossing: <c>vRel = vHeliocentric - vBody</c>. Pure vector
        /// subtraction. BOTH operands must already be in the SAME frame (the caller supplies them either
        /// both swizzled or both un-swizzled - the subtraction is frame-agnostic). This is the
        /// <c>v2 - targetBody.orbit.getOrbitalVelocityAtUT(...)</c> (capture) and
        /// <c>v1 - launchBody.orbit.getOrbitalVelocityAtUT(...)</c> (escape) reduction the state-vector
        /// construction feeds to <c>Orbit.UpdateFromStateVectors</c>.
        /// </summary>
        internal static Vector3d RelativeVelocity(Vector3d vHeliocentric, Vector3d vBody)
        {
            return vHeliocentric - vBody;
        }

        /// <summary>
        /// Body-relative position at a SOI crossing: <c>rRel = rTransfer - rBody</c> (both parent-relative
        /// in the same frame). Pure. This is the <c>transfer.relPos - targetBody.relPos</c> reduction
        /// (parent-relative positions differenced in one frame) the state-vector construction feeds to
        /// <c>Orbit.UpdateFromStateVectors</c>.
        /// </summary>
        internal static Vector3d RelativePosition(Vector3d rTransfer, Vector3d rBody)
        {
            return rTransfer - rBody;
        }

        // Upper bound on a sane escape/capture-leg eccentricity (reaim-fix-plan.md STEP 2/3 fail-closed
        // gate). A real interplanetary ejection / capture hyperbola from a low parking orbit sits at ecc
        // ~1.05-1.5 (Kerbin->Duna v-infinity ~0.9-1.2 km/s over a ~700 km periapsis); anything above ~3 is
        // a state-vector reconstruction artifact (the ecc 8-13 garbage the velocity-source bug produced),
        // never a real leg. The band is deliberately generous (it must accept eccentric Moho/Eeloo
        // departures) but well below the artifact regime so a corrupted leg fails closed.
        internal const double MaxSaneLegEccentricity = 3.0;

        /// <summary>
        /// PURE leg-conic sanity gate (reaim-fix-plan.md STEP 2/3 "MEASURE and fail-closed"). True when a
        /// synthesized escape/capture leg is a real hyperbola anchored at a sane periapsis altitude, so the
        /// caller may splice it; false (FAIL CLOSED to the recorded leg verbatim / the baseline) when the
        /// reduced state produced a degenerate or wrong-altitude conic.
        ///
        /// <para>Rejects, in order: NaN/Inf elements; a non-hyperbolic conic (<paramref name="eccentricity"/>
        /// &lt; 1, i.e. a bound ellipse - a real escape/capture leg is hyperbolic); an absurd eccentricity
        /// (&gt; <see cref="MaxSaneLegEccentricity"/> - the ecc 8-13 garbage from an inconsistent
        /// position/velocity state); and a periapsis radius <c>rp = a*(1-e)</c> outside the sane band
        /// [<paramref name="minPeriapsisRadius"/>, <c>soiRadius * <paramref name="maxPeriapsisSoiFraction"/></c>].
        /// The lower bound is the body's surface (or atmosphere top) radius - a periapsis below it is a leg
        /// that clips the body; the upper bound (default half the SOI) rejects a leg whose periapsis sits out
        /// near the SOI shell (the center-vs-shell sampling artifact: periapsis tens of Mm up).</para>
        ///
        /// <para>For a KSP hyperbola sma &lt; 0 and ecc &gt; 1, so <c>rp = a*(1-e)</c> = (negative)*(negative)
        /// = positive. Pure (scalar arithmetic); the live caller passes the body's Radius/atmosphere/SOI.</para>
        /// </summary>
        internal static bool IsSaneLegConic(
            double eccentricity, double semiMajorAxis,
            double minPeriapsisRadius, double soiRadius,
            double maxLegEccentricity = MaxSaneLegEccentricity,
            double maxPeriapsisSoiFraction = 0.5)
        {
            if (double.IsNaN(eccentricity) || double.IsInfinity(eccentricity)
                || double.IsNaN(semiMajorAxis) || double.IsInfinity(semiMajorAxis))
                return false;
            // Real escape/capture leg is hyperbolic; a bound ellipse (ecc < 1) is not a valid leg here.
            if (eccentricity < 1.0 || eccentricity > maxLegEccentricity)
                return false;
            if (double.IsNaN(minPeriapsisRadius) || minPeriapsisRadius <= 0.0
                || double.IsNaN(soiRadius) || soiRadius <= 0.0)
                return false;
            // Periapsis radius of the hyperbola (sma < 0, ecc > 1 => a*(1-e) > 0).
            double rp = semiMajorAxis * (1.0 - eccentricity);
            if (double.IsNaN(rp) || double.IsInfinity(rp) || rp <= 0.0)
                return false;
            double maxPeriapsis = soiRadius * maxPeriapsisSoiFraction;
            return rp >= minPeriapsisRadius && rp <= maxPeriapsis;
        }

        // Default bisection control for the SOI-sphere crossing refinement. The proximity scan
        // (ReaimTransferSynthesizer.TryFindTargetEncounterByProximity) has ~span/96 UT resolution; bisecting
        // to the SOI shell tightens the seam to well under the in-SOI handoff residual the playtest already
        // tolerates (the Ike/Duna seams log ~0.3-2.6 Mm). 60 iterations halves the bracket ~1e18x, far below
        // any meaningful UT, so it terminates on the position tolerance long before the iteration cap.
        internal const int DefaultBisectionIterations = 60;

        /// <summary>
        /// Refines a SOI-sphere crossing UT by bisection. <paramref name="distanceAtUT"/> returns the
        /// vessel-to-body distance (metres) at a UT; <paramref name="soiRadius"/> is the body's SOI radius.
        /// The bracket [<paramref name="insideUT"/>, <paramref name="outsideUT"/>] must straddle the shell:
        /// <c>distance(insideUT) &lt;= soiRadius</c> (inside the SOI) and
        /// <c>distance(outsideUT) &gt; soiRadius</c> (outside). Returns the UT where the sampled distance is
        /// within <paramref name="toleranceMeters"/> of the SOI radius, or the bracket midpoint at the
        /// iteration cap. Pure (given the delegate); the delegate is the only Unity-bound dependency and the
        /// live caller wires it to <c>(transfer.relPos - body.relPos).magnitude</c>.
        /// </summary>
        /// <remarks>
        /// Returns false (with <paramref name="crossingUT"/> = NaN) when the inputs are degenerate (NaN
        /// bracket, non-positive SOI, the bracket is NOT a valid inside/outside straddle, or the delegate is
        /// null) so the caller fails closed to the raw scanned UT rather than a corrupted refinement.
        /// </remarks>
        internal static bool TryBisectSoiCrossing(
            Func<double, double> distanceAtUT, double soiRadius,
            double insideUT, double outsideUT, double toleranceMeters,
            out double crossingUT,
            int maxIterations = DefaultBisectionIterations)
        {
            crossingUT = double.NaN;
            if (distanceAtUT == null
                || double.IsNaN(soiRadius) || double.IsInfinity(soiRadius) || soiRadius <= 0.0
                || double.IsNaN(insideUT) || double.IsInfinity(insideUT)
                || double.IsNaN(outsideUT) || double.IsInfinity(outsideUT)
                || double.IsNaN(toleranceMeters) || toleranceMeters <= 0.0)
                return false;

            double dIn = distanceAtUT(insideUT);
            double dOut = distanceAtUT(outsideUT);
            if (double.IsNaN(dIn) || double.IsNaN(dOut))
                return false;
            // Require a genuine inside (<= soi) / outside (> soi) straddle so the sign convention is sound.
            if (!(dIn <= soiRadius && dOut > soiRadius))
                return false;

            double lo = insideUT;   // distance(lo) <= soi  (inside)
            double hi = outsideUT;  // distance(hi)  > soi  (outside)
            double mid = 0.5 * (lo + hi);
            for (int i = 0; i < maxIterations; i++)
            {
                mid = 0.5 * (lo + hi);
                double dMid = distanceAtUT(mid);
                if (double.IsNaN(dMid))
                    return false;
                if (Math.Abs(dMid - soiRadius) <= toleranceMeters)
                {
                    crossingUT = mid;
                    return true;
                }
                if (dMid <= soiRadius)
                    lo = mid;   // mid still inside -> push the inside boundary out
                else
                    hi = mid;   // mid outside -> pull the outside boundary in
            }
            crossingUT = mid; // converged on UT (bracket collapsed) even if not on the position tolerance
            return true;
        }
    }
}
