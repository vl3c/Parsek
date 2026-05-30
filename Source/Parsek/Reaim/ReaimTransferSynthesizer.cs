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
            // Both bodies' orbits are relative to the shared parent (the Sun), so r1/r2 are in the same
            // frame. NOTE (deliberate, do NOT "simplify"): the Lambert solve is frame-agnostic, so the
            // .xzy here and the .xzy below are a round-trip through the orbit API's native swizzled
            // frame - applied identically to r1 and v1, the conic is invariant. Dropping one .xzy would
            // silently corrupt the orbit. (Verified correct in-game by the C2 canary.)
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
            // detect the target encounter (fast path: when stock cleanly promotes it, we get an accurate
            // SOI-entry UT for free).
            transfer.StartUT = departureUT;
            transfer.EndUT = arrivalUT;
            var nextPatch = new Orbit();
            PatchedConics.CalculatePatch(
                transfer, nextPatch, departureUT, new PatchedConics.SolverParameters(), targetBody);

            if (transfer.patchEndTransition == Orbit.PatchTransitionType.ENCOUNTER
                && transfer.closestEncounterBody == targetBody)
            {
                transferOrbit = transfer;
                soiEntryUT = transfer.UTsoi;
                encounterBody = targetBody;
                LogSynthGeometry(transfer, launchBody, targetBody, departureUT, arrivalUT, soiEntryUT, "patched-conic");
                return true;
            }

            // CalculatePatch resolved to the LAUNCH body instead (or did not promote an encounter): the
            // heliocentric transfer STARTS at the launch body's position (inside its SOI), so the first
            // patch is that launch-SOI transition, which masks the downstream target encounter. The
            // Lambert solution passes through the target's exact position at arrivalUT BY CONSTRUCTION, so
            // validate the encounter DIRECTLY by proximity: sample the transfer-to-target distance over
            // the leg and accept if it comes within the target's SOI. Robust to the launch-SOI masking.
            if (TryFindTargetEncounterByProximity(transfer, targetBody, departureUT, arrivalUT, out double geomSoiUT))
            {
                transferOrbit = transfer;
                soiEntryUT = geomSoiUT;
                encounterBody = targetBody;
                LogSynthGeometry(transfer, launchBody, targetBody, departureUT, arrivalUT, soiEntryUT, "proximity");
                return true;
            }

            failReason = $"no target encounter (transition={transfer.patchEndTransition} " +
                         $"closest={(transfer.closestEncounterBody != null ? transfer.closestEncounterBody.bodyName : "<none>")}; " +
                         $"proximity check also failed)";
            return false;
        }

        // Diagnostic: log the synthesized transfer's geometry against the bodies it must connect, so a
        // mis-aimed transfer (arrives where the target is NOT) is caught at the source. All positions are
        // parent-relative in the orbit API's native frame, so the distances are frame-consistent without
        // any swizzle. xfer-vs-launch@depart and xfer-vs-target@arrival must be ~0 (the Lambert connects
        // r1->r2 by construction); xfer-vs-target@soi must be <= the target SOI. Verbose, one-shot per
        // window resolve (the resolver caches the window).
        private static void LogSynthGeometry(
            Orbit transfer, CelestialBody launchBody, CelestialBody targetBody,
            double departureUT, double arrivalUT, double soiEntryUT, string path)
        {
            var ic = CultureInfo.InvariantCulture;
            double depMiss = (transfer.getRelativePositionAtUT(departureUT)
                - launchBody.orbit.getRelativePositionAtUT(departureUT)).magnitude;
            double arrMiss = (transfer.getRelativePositionAtUT(arrivalUT)
                - targetBody.orbit.getRelativePositionAtUT(arrivalUT)).magnitude;
            double soiMiss = double.IsNaN(soiEntryUT) ? double.NaN
                : (transfer.getRelativePositionAtUT(soiEntryUT)
                    - targetBody.orbit.getRelativePositionAtUT(soiEntryUT)).magnitude;
            ParsekLog.Verbose("ReaimSeam",
                $"synth geometry ({path}): departUT={departureUT.ToString("F0", ic)} " +
                $"arrivalUT={arrivalUT.ToString("F0", ic)} soiEntryUT={soiEntryUT.ToString("F0", ic)} " +
                $"sma={transfer.semiMajorAxis.ToString("R", ic)} ecc={transfer.eccentricity.ToString("F4", ic)} | " +
                $"xfer-vs-{launchBody.bodyName}@depart={depMiss.ToString("F0", ic)}m (~0) | " +
                $"xfer-vs-{targetBody.bodyName}@arrival={arrMiss.ToString("F0", ic)}m (~0) | " +
                $"xfer-vs-{targetBody.bodyName}@soi={(double.IsNaN(soiMiss) ? "NaN" : soiMiss.ToString("F0", ic))}m " +
                $"(SOI={targetBody.sphereOfInfluence.ToString("F0", ic)})");
        }

        // Direct geometric encounter check: samples the heliocentric transfer's distance to the target
        // over [departureUT, arrivalUT] (both positions parent-relative, .xzy-unswizzled to a consistent
        // frame) and returns true with the first within-SOI UT when the closest approach falls inside the
        // target's sphere of influence. The Lambert solve aims the transfer at the target's position at
        // arrivalUT, so a converged solution always passes near arrivalUT; this bypasses
        // PatchedConics.CalculatePatch masking the target behind the launch body's SOI.
        private static bool TryFindTargetEncounterByProximity(
            Orbit transfer, CelestialBody target, double departureUT, double arrivalUT, out double soiEntryUT)
        {
            soiEntryUT = double.NaN;
            if (transfer == null || target == null || target.orbit == null)
                return false;
            double soi = target.sphereOfInfluence;
            if (double.IsNaN(soi) || soi <= 0.0 || arrivalUT <= departureUT)
                return false;

            const int samples = 96;
            double span = arrivalUT - departureUT;
            for (int i = 1; i <= samples; i++)
            {
                double t = departureUT + span * i / samples;
                Vector3d tp = transfer.getRelativePositionAtUT(t).xzy;
                Vector3d dp = target.orbit.getRelativePositionAtUT(t).xzy;
                if ((tp - dp).magnitude <= soi)
                {
                    soiEntryUT = t;
                    return true;
                }
            }
            return false;
        }
    }
}
