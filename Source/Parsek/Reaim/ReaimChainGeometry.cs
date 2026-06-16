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
