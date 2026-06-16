using System;
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
        // Workstream B (design §6.9): the pure Lambert solve is taken through a replaceable
        // ITransferSolver seam (the swap-a-library boundary) instead of calling UvLambert.Solve
        // directly. Defaults to the verbatim UvLambert delegation, so behaviour is identical and the
        // existing UvLambertTests + canaries still guard the math. Tests can substitute a stub to
        // verify the synthesizer routes through the interface and to fault-inject a no-solution
        // without needing a degenerate-geometry fixture.
        // NOTE: process-wide mutable static. A test that overrides it MUST restore the default in its
        // teardown (try/finally or IDisposable), or it leaks into sibling tests and causes a flaky-test
        // trap. No test mutates it today (TransferSolverInterfaceTests only assert the default type).
        internal static ITransferSolver TransferSolver = UvLambertTransferSolver.Default;

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
        /// Projects <paramref name="v"/> onto the plane through the origin whose normal is
        /// <paramref name="planeNormal"/> (subtracts the component of v along the normal). Returns v
        /// unchanged when the normal is degenerate (zero-length / NaN). Pure.
        /// </summary>
        /// <remarks>
        /// RETAINED but no longer called from <see cref="TrySynthesizeTransfer"/>: it formerly flattened the
        /// target endpoint into the launch body's orbital plane to dodge the near-180-degree Lambert plane
        /// singularity, but flattening r2 toward antiparallel collapsed sin(dnu) onto the
        /// MinSinTransferAngle cliff and removed the target's out-of-plane offset (capping re-aim to
        /// low-inclination targets). It is superseded by feeding the stable launch-plane normal as the
        /// Lambert handedness axis with the UN-projected target endpoint (see TrySynthesizeTransfer). Kept
        /// here as a tested general-purpose helper (its xUnit tests stay green) and as the documented
        /// gated contingency should a future solver want a pre-flattened endpoint.
        /// </remarks>
        internal static Vector3d ProjectOntoPlane(Vector3d v, Vector3d planeNormal)
        {
            double n2 = planeNormal.sqrMagnitude;
            if (n2 <= 0.0 || double.IsNaN(n2))
                return v;
            return v - (Vector3d.Dot(v, planeNormal) / n2) * planeNormal;
        }

        /// <summary>
        /// True when a synthesized transfer is RETROGRADE relative to the parent's reference plane
        /// (inclination &gt; 90 deg). A transfer between two prograde planets must be prograde; a
        /// retrograde solution (inclination ~180 deg) connects the endpoints but travels the wrong way and
        /// must be rejected. Pure; KSP <c>Orbit.inclination</c> is in degrees, 0..180. The strict
        /// <c>&gt; 90</c> classifies an exactly-polar (90 deg) transfer as prograde. Since the launch-plane
        /// normal now drives the Lambert handedness (the r2-projection was removed in the near-180 fix),
        /// a correctly-handed transfer comes back near 0 (prograde) / near 180 (retrograde) - it carries
        /// the target's small real inclination rather than being flattened to exactly 0 - so the 90 deg
        /// boundary is not reached in practice.
        /// </summary>
        internal static bool IsRetrogradeTransfer(double inclinationDegrees)
        {
            return !double.IsNaN(inclinationDegrees) && inclinationDegrees > 90.0;
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
            out Orbit escapeOrbit, out Orbit captureOrbit,
            out double launchSoiExitUT, out double targetSoiEntryUT,
            out string failReason)
        {
            transferOrbit = null;
            soiEntryUT = double.NaN;
            encounterBody = null;
            escapeOrbit = null;
            captureOrbit = null;
            launchSoiExitUT = double.NaN;
            targetSoiEntryUT = double.NaN;
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

            // Resolve the near-180-degree Lambert plane singularity by feeding the STABLE launch-plane
            // normal as the solver's handedness axis with the UN-PROJECTED target endpoint - NOT by
            // flattening r2 into the launch plane. At a Hohmann (~180 deg) transfer angle the plane normal
            // from r1 x r2 is ill-conditioned: its sign (which selects the prograde vs retrograde branch)
            // rides the tiny out-of-plane component and flips on rounding noise, so the solver returns the
            // wrong (retrograde inc=180) branch and the caller declined to faithful even though a perfectly
            // good prograde transfer exists. The earlier fix projected r2 onto the launch plane to fix the
            // branch sign, but that flattened r2 toward antiparallel to r1, collapsing sin(dnu) onto the
            // MinSinTransferAngle cliff (the very degeneracy it tried to dodge) AND removing the target's
            // out-of-plane offset.
            // Instead: compute the launch-plane normal r1 x v_launch (which lies along the reference / swizzled
            // z axis, i.e. a well-defined fixed axis) and pass it to the solver as the handedness axis, with
            // the RAW (un-projected) r2. Un-projecting restores the target's out-of-plane component so r1 x r2
            // regains a well-conditioned magnitude and sin(dnu) lifts off the MinSinTransferAngle cliff, while
            // the small residual sign ambiguity is resolved by dot(r1 x r2, launchPlaneNormal) instead of the
            // noise-dominated z component. Both edits are required together: un-projecting alone restores
            // conditioning but leaves the sign noisy; the normal alone cannot help while r2 is flattened to
            // near-antiparallel.
            // The MinSinTransferAngle guard, the IsSaneTransferConic check, and the IsRetrogradeTransfer
            // direction guard below all stay as the fail-closed backstop: a window that still cannot produce
            // a same-handedness sane conic declines to faithful (never a wrong conic). Because the offset is
            // no longer removed, re-aiming an inclined target is now bounded only by the downstream proximity
            // check (the target must still pass within its SOI), not by the projection.
            Vector3d launchPlaneNormal = Vector3d.Cross(
                r1, launchBody.orbit.getOrbitalVelocityAtUT(departureUT).xzy);

            // Capture BOTH endpoint velocities: v1 (heliocentric departure) builds the escape leg, v2
            // (heliocentric arrival) builds the first-capture leg - the SAME single Lambert solve feeds
            // both SOI legs so the chain is continuous (reaim-fix-plan.md STEP 0). The ITransferSolver /
            // UvLambert already return v2; no signature change there. r1/r2 are un-swizzled (.xzy) world,
            // so v1/v2 are too.
            if (!TransferSolver.Solve(mu, r1, r2, tofSeconds, prograde, launchPlaneNormal,
                    out Vector3d v1, out Vector3d v2))
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

            // Match the recorded mission's DIRECTION (handedness), adapting to what was recorded instead of
            // forcing prograde: the caller passes <paramref name="prograde"/> reflecting the recorded
            // transfer's handedness (prograde => recorded transfer was prograde; if the recorded transfer
            // was retrograde the caller passes false). The launch-plane normal now stabilizes the Lambert
            // branch near a ~180-degree (Hohmann) transfer angle, so the nominal-departure window should
            // converge with the requested handedness instead of flipping on the noisy r1 x r2 sign. This
            // guard REMAINS as the fail-closed backstop: should a window still come back with the wrong
            // handedness (a valid ellipse that connects the endpoints but travels the WRONG way relative to
            // the recording, which IsSaneTransferConic accepts as ecc < 1, sma > 0), reject it and let the
            // localized tof search step (or fall back to faithful) rather than render a wrong conic.
            bool resultRetrograde = IsRetrogradeTransfer(transfer.inclination);
            if (resultRetrograde != !prograde)
            {
                failReason = $"transfer direction mismatch inc={transfer.inclination.ToString("F2", CultureInfo.InvariantCulture)} deg " +
                             $"(resultRetrograde={resultRetrograde}, recordedRetrograde={!prograde})";
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

            string encounterPath = null;
            if (transfer.patchEndTransition == Orbit.PatchTransitionType.ENCOUNTER
                && transfer.closestEncounterBody == targetBody)
            {
                soiEntryUT = transfer.UTsoi;
                encounterPath = "patched-conic";
            }
            else if (TryFindTargetEncounterByProximity(transfer, targetBody, departureUT, arrivalUT, out double geomSoiUT))
            {
                // CalculatePatch resolved to the LAUNCH body instead (or did not promote an encounter): the
                // heliocentric transfer STARTS at the launch body's position (inside its SOI), so the first
                // patch is that launch-SOI transition, which masks the downstream target encounter. The
                // Lambert solution passes through the target's exact position at arrivalUT BY CONSTRUCTION,
                // so validate the encounter DIRECTLY by proximity: sample the transfer-to-target distance
                // over the leg and accept if it comes within the target's SOI. Robust to the launch-SOI
                // masking.
                soiEntryUT = geomSoiUT;
                encounterPath = "proximity";
            }
            else
            {
                failReason = $"no target encounter (transition={transfer.patchEndTransition} " +
                             $"closest={(transfer.closestEncounterBody != null ? transfer.closestEncounterBody.bodyName : "<none>")}; " +
                             $"proximity check also failed)";
                return false;
            }

            transferOrbit = transfer;
            encounterBody = targetBody;
            LogSynthGeometry(transfer, launchBody, targetBody, departureUT, arrivalUT, soiEntryUT, encounterPath);

            // Build the two SOI legs from the SAME Lambert solve (reaim-fix-plan.md STEP 1/2). These are
            // best-effort: a leg that cannot converge a sane conic leaves its out-Orbit null + its UT NaN,
            // and the P3 assembler falls back accordingly (all-or-nothing fail-closed at the chain level).
            // The transfer encounter itself already succeeded, so the heliocentric-only baseline is always
            // available even when neither leg builds.
            captureOrbit = TryBuildCaptureLeg(transfer, targetBody, v2, soiEntryUT, departureUT, arrivalUT,
                out targetSoiEntryUT);
            escapeOrbit = TryBuildEscapeLeg(transfer, launchBody, v1, departureUT, arrivalUT,
                out launchSoiExitUT);
            LogChainLegs(launchBody, targetBody, escapeOrbit, captureOrbit, launchSoiExitUT, targetSoiEntryUT);
            return true;
        }

        // Diagnostic: log the two synthesized SOI legs (escape launch-body-relative, capture
        // target-relative) + their refined SOI-crossing UTs, so a corrupted leg (a swizzle error, a missed
        // bisection) is caught at the source before P3 assembles the chain. A null leg logs "(fail-closed)"
        // so the all-or-nothing fallback reason is visible. Verbose, one-shot per window resolve (the
        // resolver caches the window) - mirrors LogSynthGeometry's level.
        private static void LogChainLegs(
            CelestialBody launchBody, CelestialBody targetBody,
            Orbit escapeOrbit, Orbit captureOrbit, double launchSoiExitUT, double targetSoiEntryUT)
        {
            var ic = CultureInfo.InvariantCulture;
            string esc = escapeOrbit == null
                ? "(fail-closed)"
                : $"sma={escapeOrbit.semiMajorAxis.ToString("R", ic)} ecc={escapeOrbit.eccentricity.ToString("F4", ic)}";
            string cap = captureOrbit == null
                ? "(fail-closed)"
                : $"sma={captureOrbit.semiMajorAxis.ToString("R", ic)} ecc={captureOrbit.eccentricity.ToString("F4", ic)}";
            ParsekLog.Verbose("ReaimSeam",
                $"chain legs: escape@{launchBody.bodyName} {esc} launchSoiExitUT={launchSoiExitUT.ToString("F0", ic)} | " +
                $"capture@{targetBody.bodyName} {cap} targetSoiEntryUT={targetSoiEntryUT.ToString("F0", ic)}");
        }

        // The fractional tolerance (of the body SOI radius) the SOI-crossing bisection refines to. 1e-4 of
        // the SOI radius is far tighter than the in-SOI handoff seams the playtest already tolerates while
        // staying well above floating-point noise on the sampled positions.
        internal const double SoiBisectionToleranceFraction = 1e-4;

        // Builds the first-capture leg (target-relative hyperbola) from the heliocentric arrival velocity v2
        // (reaim-fix-plan.md STEP 1, state-vector PRIMARY path). Bisects the target SOI-sphere crossing
        // around the resolved soiEntryUT, then constructs a target-relative Orbit from the body-relative
        // state at that UT. Mirrors the transfer build's .xzy round-trip EXACTLY (un-swizzle to difference
        // in one world frame, re-swizzle for UpdateFromStateVectors). v2 is in the SAME un-swizzled frame
        // as r1/r2. Returns null (with refinedEntryUT = the raw soiEntryUT) on a degenerate result; the
        // caller / assembler then fails the capture side closed. Live (Orbit + getOrbital*AtUT).
        private static Orbit TryBuildCaptureLeg(
            Orbit transfer, CelestialBody targetBody, Vector3d v2, double soiEntryUT,
            double departureUT, double arrivalUT, out double refinedEntryUT)
        {
            refinedEntryUT = soiEntryUT;
            if (transfer == null || targetBody == null || targetBody.orbit == null
                || double.IsNaN(soiEntryUT))
                return null;

            double soi = targetBody.sphereOfInfluence;
            // Distance (un-swizzled, consistent frame) from the transfer to the target at a UT.
            Func<double, double> dist = ut => ReaimChainGeometry.RelativePosition(
                transfer.getRelativePositionAtUT(ut).xzy,
                targetBody.orbit.getRelativePositionAtUT(ut).xzy).magnitude;

            // Bisect to the SOI shell. Bracket: soiEntryUT (the proximity scan returned it as inside the
            // SOI) to arrivalUT (the transfer is aimed at the target center, so by arrivalUT it is well
            // inside). Walk a tiny step BEFORE soiEntryUT to find an outside sample (the scan's resolution
            // is ~span/96, so soiEntryUT is the FIRST inside sample - just before it is outside).
            double span = arrivalUT - departureUT;
            double step = span > 0.0 ? span / 96.0 : 0.0;
            double outsideUT = soiEntryUT - step;
            double tol = (!double.IsNaN(soi) && soi > 0.0) ? soi * SoiBisectionToleranceFraction : double.NaN;
            if (step > 0.0 && ReaimChainGeometry.TryBisectSoiCrossing(
                    dist, soi, soiEntryUT, outsideUT, tol, out double crossing))
                refinedEntryUT = crossing;

            return BuildBodyRelativeLeg(transfer, targetBody, v2, refinedEntryUT);
        }

        // Builds the escape leg (launch-body-relative hyperbola) from the heliocentric departure velocity v1
        // (reaim-fix-plan.md STEP 2). The transfer is center-to-center, so just after departureUT it sits
        // near the launch-body center; walk FORWARD to the launch SOI shell and bisect there, then construct
        // a launch-body-relative Orbit from the body-relative state at that UT. Same .xzy round-trip + same
        // fail-closed contract as the capture leg. Returns null (launchSoiExitUT NaN) on a degenerate result.
        private static Orbit TryBuildEscapeLeg(
            Orbit transfer, CelestialBody launchBody, Vector3d v1,
            double departureUT, double arrivalUT, out double launchSoiExitUT)
        {
            launchSoiExitUT = double.NaN;
            if (transfer == null || launchBody == null || launchBody.orbit == null)
                return null;

            double soi = launchBody.sphereOfInfluence;
            if (double.IsNaN(soi) || soi <= 0.0)
                return null;
            Func<double, double> dist = ut => ReaimChainGeometry.RelativePosition(
                transfer.getRelativePositionAtUT(ut).xzy,
                launchBody.orbit.getRelativePositionAtUT(ut).xzy).magnitude;

            // Find an OUTSIDE sample by walking forward from departureUT (inside, near the launch center)
            // until the distance exceeds the SOI radius, then bisect [insideUT, outsideUT].
            double span = arrivalUT - departureUT;
            if (span <= 0.0)
                return null;
            const int scan = 96;
            double dStep = span / scan;
            double insideUT = departureUT;
            double foundOutsideUT = double.NaN;
            for (int i = 1; i <= scan; i++)
            {
                double ut = departureUT + dStep * i;
                if (dist(ut) > soi)
                {
                    foundOutsideUT = ut;
                    break;
                }
                insideUT = ut; // still inside the launch SOI -> advance the inside boundary
            }
            if (double.IsNaN(foundOutsideUT))
                return null; // never left the launch SOI within the span -> no escape (fail closed)

            double tol = soi * SoiBisectionToleranceFraction;
            if (!ReaimChainGeometry.TryBisectSoiCrossing(
                    dist, soi, insideUT, foundOutsideUT, tol, out launchSoiExitUT))
                return null;

            return BuildBodyRelativeLeg(transfer, launchBody, v1, launchSoiExitUT);
        }

        // Shared state-vector construction (reaim-fix-plan.md STEP 1/2): given the heliocentric transfer,
        // a body, the heliocentric endpoint velocity vHelio (UN-swizzled .xzy frame) and the SOI-crossing
        // UT, build a body-relative Orbit. Mirrors ReaimTransferSynthesizer.TrySynthesizeTransfer's transfer
        // build EXACTLY: difference the parent-relative positions and velocities in the UN-swizzled (.xzy)
        // world frame, then re-swizzle (.xzy is its own inverse) for UpdateFromStateVectors (which expects
        // swizzled inputs). A dropped/doubled swizzle silently corrupts the orbit - verified in-game by the
        // canary. Returns null when the resulting conic is degenerate (NaN elements / non-finite sma).
        private static Orbit BuildBodyRelativeLeg(
            Orbit transfer, CelestialBody body, Vector3d vHelio, double crossingUT)
        {
            if (double.IsNaN(crossingUT) || double.IsInfinity(crossingUT) || body.orbit == null)
                return null;

            // Un-swizzled, consistent-frame body-relative state at the crossing.
            Vector3d rRel = ReaimChainGeometry.RelativePosition(
                transfer.getRelativePositionAtUT(crossingUT).xzy,
                body.orbit.getRelativePositionAtUT(crossingUT).xzy);
            Vector3d vRel = ReaimChainGeometry.RelativeVelocity(
                vHelio,
                body.orbit.getOrbitalVelocityAtUT(crossingUT).xzy);

            var leg = new Orbit();
            // Re-swizzle (.xzy) for UpdateFromStateVectors, exactly as the transfer build does.
            leg.UpdateFromStateVectors(rRel.xzy, vRel.xzy, body, crossingUT);
            if (double.IsNaN(leg.semiMajorAxis) || double.IsInfinity(leg.semiMajorAxis)
                || double.IsNaN(leg.eccentricity) || double.IsInfinity(leg.eccentricity))
                return null;
            return leg;
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
