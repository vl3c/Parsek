using System.Globalization;

namespace Parsek.Reaim
{
    // Live synthesis of a re-aimed heliocentric transfer (docs/dev/plans/
    // reaim-interplanetary-transfers.md, Phase 2). Given a launch body, a cross-parent target, a
    // departure UT, and a time-of-flight, this re-plans the transfer to the target's CURRENT position
    // (the pure UvLambert solve) and builds a stock KSP Orbit for it, then propagates it through stock
    // PatchedConics to find the target-SOI entry. The PURE math (window + Lambert) is unit-tested
    // elsewhere; THIS class is the Unity-bound glue (live orbits + UpdateFromStateVectors +
    // CalculatePatch) and is exercised by the in-game canary (CrossParentReaimCanaryInGameTest).
    //
    // Frame handling (the load-bearing detail): a body's orbit relative position/velocity come back
    // YZ-swizzled (AliceWorld); we un-swizzle with .xzy to do the Lambert solve in a consistent world
    // frame, then re-swizzle (.xzy is its own inverse) when feeding Orbit.UpdateFromStateVectors,
    // which expects swizzled inputs. v1 single-hop only: launch + target must share a reference body
    // (the Sun); deeper chains (Ike via Duna) are deferred.
    internal static class ReaimTransferSynthesizer
    {
        // Reject an absurd Lambert result before it reaches CalculatePatch (plan review M3): a sane
        // heliocentric transfer between two bound planets is elliptic (0 <= e < 1) with a positive,
        // finite semi-major axis. A hyperbolic / NaN / non-positive-sma result means the window's
        // geometry + tof did not yield a usable transfer; the caller steps to the next window.
        internal static bool IsSaneTransferConic(double eccentricity, double semiMajorAxis)
        {
            if (double.IsNaN(eccentricity) || double.IsInfinity(eccentricity)
                || double.IsNaN(semiMajorAxis) || double.IsInfinity(semiMajorAxis))
                return false;
            return eccentricity >= 0.0 && eccentricity < 1.0 && semiMajorAxis > 0.0;
        }

        /// <summary>
        /// Re-plans + builds the heliocentric transfer Orbit for one window and finds its target-SOI
        /// entry. Returns true with <paramref name="transferOrbit"/> (Sun-relative conic),
        /// <paramref name="soiEntryUT"/> (when the transfer enters the target's SOI), and
        /// <paramref name="encounterBody"/> (== targetBody) on success. Returns false (with a reason)
        /// when the bodies do not share a parent, the Lambert solve fails / is degenerate, or
        /// PatchedConics finds no target encounter - the caller then steps to the next window. Live
        /// (reads FlightGlobals body orbits + stock PatchedConics); not unit-testable off-Unity (the
        /// in-game canary is its test). <paramref name="tofSeconds"/> should be the Hohmann time for
        /// THIS window's geometry (plan review M3), not the recorded tof.
        /// </summary>
        internal static bool TrySynthesizeTransfer(
            CelestialBody launchBody, CelestialBody targetBody, double departureUT, double tofSeconds,
            bool prograde,
            out Orbit transferOrbit, out double soiEntryUT, out CelestialBody encounterBody,
            out string failReason)
        {
            transferOrbit = null;
            soiEntryUT = double.NaN;
            encounterBody = null;
            failReason = null;

            if (launchBody == null || targetBody == null)
            {
                failReason = "null launch/target body";
                return false;
            }
            if (launchBody == targetBody)
            {
                failReason = "launch == target";
                return false;
            }
            CelestialBody parent = launchBody.referenceBody;
            if (parent == null || targetBody.referenceBody != parent)
            {
                // v1 single-hop: both bodies must orbit the same parent (the Sun). A deeper chain
                // (Ike via Duna) is deferred; the caller leaves such a mission on the faithful path.
                failReason = "launch/target do not share a parent (deep chain not supported in v1)";
                return false;
            }
            if (double.IsNaN(departureUT) || double.IsNaN(tofSeconds) || tofSeconds <= 0.0)
            {
                failReason = "bad departureUT/tof";
                return false;
            }

            double mu = parent.gravParameter;
            double arrivalUT = departureUT + tofSeconds;

            // Heliocentric endpoints, un-swizzled to a consistent world frame for the Lambert solve.
            Vector3d r1 = launchBody.orbit.getRelativePositionAtUT(departureUT).xzy;
            Vector3d r2 = targetBody.orbit.getRelativePositionAtUT(arrivalUT).xzy;

            if (!UvLambert.Solve(mu, r1, r2, tofSeconds, prograde, out Vector3d v1, out _))
            {
                failReason = "lambert no solution (degenerate geometry / non-convergence)";
                return false;
            }

            // Build the Sun-relative transfer conic. UpdateFromStateVectors expects SWIZZLED inputs, so
            // re-swizzle (.xzy is its own inverse).
            var transfer = new Orbit();
            transfer.UpdateFromStateVectors(r1.xzy, v1.xzy, parent, departureUT);

            if (!IsSaneTransferConic(transfer.eccentricity, transfer.semiMajorAxis))
            {
                failReason = $"degenerate transfer conic ecc={transfer.eccentricity.ToString("R", CultureInfo.InvariantCulture)} " +
                             $"sma={transfer.semiMajorAxis.ToString("R", CultureInfo.InvariantCulture)}";
                return false;
            }

            // Bound the SOI search to the transfer span and propagate through stock patched conics to
            // detect the target encounter. Under default settings the ENCOUNTER promotes when the
            // transfer's radial band intersects the target's orbit (it does) and the closest approach
            // falls inside the target SOI (it does - Lambert aimed r2 at the target's position).
            transfer.StartUT = departureUT;
            transfer.EndUT = arrivalUT;
            var nextPatch = new Orbit();
            PatchedConics.CalculatePatch(
                transfer, nextPatch, departureUT, new PatchedConics.SolverParameters(), targetBody);

            if (transfer.patchEndTransition != Orbit.PatchTransitionType.ENCOUNTER
                || transfer.closestEncounterBody != targetBody)
            {
                failReason = $"no target encounter (transition={transfer.patchEndTransition} " +
                             $"closest={(transfer.closestEncounterBody != null ? transfer.closestEncounterBody.bodyName : "<none>")})";
                return false;
            }

            transferOrbit = transfer;
            soiEntryUT = transfer.UTsoi;
            encounterBody = transfer.closestEncounterBody;
            return true;
        }
    }
}
