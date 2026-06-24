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

        // --- Bug A (heliocentric transfer plane tilt) post-solve correction ---
        // A re-aimed Kerbin->Duna looping transfer renders the heliocentric leg TILTED out of plane:
        // a Hohmann sweeps ~180 deg so r1/r2 are near-antiparallel, r1 x r2 (== the conic's angular
        // momentum direction by construction, since UvLambert returns v1 in span(r1,r2)) has near-zero
        // magnitude whose DIRECTION is dominated by the target's out-of-ecliptic z-offset DIVIDED by the
        // tiny chord-perpendicular distance, so the rendered inclination is an amplified projection of the
        // target's z-offset (2-5 deg for Duna's real 0.06 deg). The launchPlaneNormal handedness axis fixed
        // the near-180 DECLINE (branch flip) but does NOT constrain the solved PLANE (it only picks the
        // branch sign), so the tilt persists. The fix (docs/dev/plans/reaim-transfer-plane-tilt-plan.md) is a
        // POST-solve, target-derived, achievability-GATED plane re-pin onto the target body's own
        // well-conditioned orbital plane, holding r1 fixed and rotating only v1's transverse component.
        // Fail-closed throughout: when holding r1 fixed cannot reach the target plane (Moho at adverse
        // phase) it DECLINES to faithful rather than over-flatten an inclined target.

        // Single tuning point: how far the solved transfer-plane inclination may exceed the target body's
        // own orbital inclination before it is treated as the spurious near-antiparallel tilt. Must exceed
        // any inc(nTarget)-vs-orbit.inclination numerical / RAAN jitter but stay below the smallest spurious
        // tilt caught (the reported Duna window is 2.36 deg; Duna's real inc 0.06 deg => ~0.56 deg bound).
        internal const double InclinationToleranceDegrees = 0.5;

        // Surfaces whether the LAST TrySynthesizeTransfer ACTUALLY fired the tilt correction (re-pinned the
        // plane and passed all re-validation), versus declined or no-op'd. The in-game canary reads
        // FiredCorrectionCount to prove the known Duna 2.36/5.06 deg windows are corrected by FIRING, not by
        // silently DECLINING to faithful (a window could satisfy "inc <= bound" by declining, masking a
        // regression). Process-wide static counter; the in-game test snapshots it before/after a resolve.
        internal static long FiredCorrectionCount;
        internal static long DeclinedCorrectionCount;

        /// <summary>
        /// The target-derived inclination bound (degrees): the inclination a CORRECT re-aimed transfer
        /// SHOULD carry, namely the larger of the launch/target body's own orbital inclination plus
        /// <see cref="InclinationToleranceDegrees"/>. NaN-safe (a NaN body inclination contributes 0). Pure.
        /// </summary>
        internal static double InclinationBoundDegrees(double launchInc, double targetInc)
        {
            double l = double.IsNaN(launchInc) ? 0.0 : launchInc;
            double t = double.IsNaN(targetInc) ? 0.0 : targetInc;
            return Math.Max(Math.Max(l, t), 0.0) + InclinationToleranceDegrees;
        }

        /// <summary>
        /// True when a solved transfer-plane inclination is the SPURIOUS near-antiparallel tilt rather than
        /// the target's real inclination: <c>!NaN(inc) AND inc &lt;= 90 AND inc &gt; bound</c>. The
        /// <c>inc &lt;= 90</c> clause keeps this orthogonal to <see cref="IsRetrogradeTransfer"/> (a
        /// retrograde conic is declined upstream), so the correction only ever runs on a sane, prograde
        /// conic. Pure.
        /// </summary>
        internal static bool IsExcessiveTiltTransfer(double inc, double bound)
        {
            return !double.IsNaN(inc) && inc <= 90.0 && inc > bound;
        }

        /// <summary>
        /// The target body's orbital angular-momentum direction <c>normalize(r2 x v2Target)</c> - a large,
        /// well-conditioned vector carrying the target's REAL inclination + RAAN (plane-invariant for a
        /// Kepler orbit, so an eccentric target's true anomaly at arrival does not perturb it). r2 and
        /// v2Target must be in the SAME (.xzy-unswizzled) Lambert frame as r1. Returns
        /// <see cref="Vector3d.zero"/> as a degenerate sentinel when either input is NaN or the cross
        /// product is ~zero (collinear / zero-length). Pure.
        /// </summary>
        internal static Vector3d ComputeIntendedPlaneNormal(Vector3d r2, Vector3d v2Target)
        {
            if (IsNanVec(r2) || IsNanVec(v2Target))
                return Vector3d.zero;
            Vector3d h = Vector3d.Cross(r2, v2Target);
            double m = h.magnitude;
            if (m <= 0.0 || double.IsNaN(m) || double.IsInfinity(m))
                return Vector3d.zero;
            return h / m;
        }

        /// <summary>
        /// The inclination (degrees) of the BEST plane achievable while holding the fixed departure point
        /// <paramref name="r1"/>: the normal closest to <paramref name="nIntended"/> that is orthogonal to
        /// r-hat is <c>n_ach = normalize(nIntended - (r-hat . nIntended) r-hat)</c>; its inclination is
        /// <c>acos(|n_ach.z| / |n_ach|) * Rad2Deg</c>. Equals nIntended's inclination only when r-hat is
        /// perpendicular to nIntended; at adverse phase (r-hat near the node-perpendicular) it collapses
        /// toward 0. Returns NaN on degenerate input (zero/NaN r1, or n_ach collapses to zero). Pure.
        /// </summary>
        internal static double AchievablePlaneInclinationDegrees(Vector3d r1, Vector3d nIntended)
        {
            if (IsNanVec(r1) || IsNanVec(nIntended))
                return double.NaN;
            double r1m = r1.magnitude;
            if (r1m <= 0.0 || double.IsNaN(r1m) || double.IsInfinity(r1m))
                return double.NaN;
            Vector3d rHat = r1 / r1m;
            Vector3d nAch = nIntended - Vector3d.Dot(rHat, nIntended) * rHat;
            double nm = nAch.magnitude;
            if (nm <= 0.0 || double.IsNaN(nm) || double.IsInfinity(nm))
                return double.NaN;
            double cosInc = Math.Abs(nAch.z) / nm;
            if (cosInc > 1.0) cosInc = 1.0;
            if (cosInc < -1.0) cosInc = -1.0;
            return Math.Acos(cosInc) * (180.0 / Math.PI);
        }

        /// <summary>
        /// The achievability GATE (the over-determination resolution): the correction is SAFE to apply only
        /// when the best plane through the fixed <paramref name="r1"/> lands ON the target plane, i.e.
        /// <c>n_ach non-degenerate AND |incAch - targetInc| &lt;= tol</c>. For Duna (nTarget ~ ecliptic)
        /// incAch ~ targetInc at all r1 phases => always safe. For Moho at adverse phase incAch collapses
        /// toward 0 => |incAch - 7| &gt; tol => UNsafe => the caller declines to faithful (never over-flattens
        /// an inclined target). Pure.
        /// </summary>
        internal static bool ConstrainTransferPlaneIsSafe(
            Vector3d r1, Vector3d nIntended, double targetInc, double tol)
        {
            double incAch = AchievablePlaneInclinationDegrees(r1, nIntended);
            if (double.IsNaN(incAch))
                return false;
            double t = double.IsNaN(targetInc) ? 0.0 : targetInc;
            return Math.Abs(incAch - t) <= tol;
        }

        /// <summary>
        /// The correction body: re-orients the solved velocity <paramref name="v1"/> onto the intended
        /// plane while keeping r1 fixed, by holding the RADIAL part (<c>v_rad = (v1 . r-hat) r-hat</c>) and
        /// rotating only the TRANSVERSE part onto <c>t-hat = normalize(nIntended x r-hat)</c>:
        /// <c>v1' = v_rad + |v_perp| * sign(v_perp . t-hat) * t-hat</c>. This preserves |v1| exactly (so
        /// sma/energy are unchanged), preserves the prograde handedness sign (via the sign term), and keeps
        /// r1 untouched (no departure-seam shift). Because r1 is fixed the resulting plane normal is n_ach,
        /// not exactly nIntended - the achievability gate guarantees n_ach is within tol of nIntended.
        /// Returns false (and <paramref name="v1Out"/> = v1 unchanged) on degenerate input (zero/NaN r1,
        /// zero/NaN nIntended, degenerate t-hat, or zero transverse component). Pure.
        /// </summary>
        internal static bool ConstrainTransferPlane(
            Vector3d r1, Vector3d v1, Vector3d nIntended, out Vector3d v1Out)
        {
            v1Out = v1;
            if (IsNanVec(r1) || IsNanVec(v1) || IsNanVec(nIntended))
                return false;
            double r1m = r1.magnitude;
            if (r1m <= 0.0 || double.IsNaN(r1m) || double.IsInfinity(r1m))
                return false;
            Vector3d rHat = r1 / r1m;

            // t-hat: the in-plane transverse direction of the INTENDED plane at r1.
            Vector3d tDir = Vector3d.Cross(nIntended, rHat);
            double tm = tDir.magnitude;
            if (tm <= 0.0 || double.IsNaN(tm) || double.IsInfinity(tm))
                return false;
            Vector3d tHat = tDir / tm;

            // Decompose v1 into radial + transverse; preserve the radial part and the transverse MAGNITUDE
            // and SIGN (prograde sense), re-aiming only its direction onto the intended plane.
            double vRadMag = Vector3d.Dot(v1, rHat);
            Vector3d vRad = vRadMag * rHat;
            Vector3d vPerp = v1 - vRad;
            double vPerpMag = vPerp.magnitude;
            if (vPerpMag <= 0.0 || double.IsNaN(vPerpMag) || double.IsInfinity(vPerpMag))
                return false;
            double sign = Vector3d.Dot(vPerp, tHat) >= 0.0 ? 1.0 : -1.0;

            Vector3d corrected = vRad + (vPerpMag * sign) * tHat;
            if (IsNanVec(corrected))
                return false;
            v1Out = corrected;
            return true;
        }

        // True if any component of the vector is NaN (degenerate-input guard for the pure helpers).
        private static bool IsNanVec(Vector3d v)
        {
            return double.IsNaN(v.x) || double.IsNaN(v.y) || double.IsNaN(v.z);
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
            out string failReason,
            Vector3d departureOverridePosUnswizzled = default(Vector3d),
            Vector3d departureOverrideVelUnswizzled = default(Vector3d),
            bool hasDepartureOverride = false)
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
            //
            // PARKING-DEPARTURE OVERRIDE (gated to the heliocentric-parking-departure path): for a two-burn
            // departure the icon is coasting on the heliocentric PARK at the trans-target burn (the launch
            // SOI escape happened far earlier), so the transfer must emanate from the vessel's re-phased
            // PARK-END state, NOT the launch body's center. The caller passes r1 / vel ALREADY .xzy-unswizzled
            // (same frame as the launchBody.getRelativePositionAtUT(...).xzy default below), evaluated on the
            // LAN-rotated park at the RECORDED burn UT (see ReaimPlaybackResolver + DecideDepartureAnchor).
            // Default args (hasDepartureOverride=false) reproduce the launch-center conic byte-for-byte.
            Vector3d r1 = hasDepartureOverride
                ? departureOverridePosUnswizzled
                : launchBody.orbit.getRelativePositionAtUT(departureUT).xzy;
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
            // The handedness axis comes from r1 x v at the SAME point r1 is anchored: for the parking-
            // departure override that is the park-end velocity (caller-supplied, .xzy-unswizzled); for the
            // direct path it is the launch body's velocity at departureUT. Keeping the velocity source
            // consistent with r1 keeps launchPlaneNormal well-defined for the near-180 stabilization.
            Vector3d launchPlaneNormal = hasDepartureOverride
                ? Vector3d.Cross(r1, departureOverrideVelUnswizzled)
                : Vector3d.Cross(r1, launchBody.orbit.getOrbitalVelocityAtUT(departureUT).xzy);

            if (!TransferSolver.Solve(mu, r1, r2, tofSeconds, prograde, launchPlaneNormal, out Vector3d v1, out _))
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

            // --- Bug A: post-solve heliocentric-plane TILT correction (achievability-gated). ---
            // The first Lambert solve above ran VERBATIM on raw r2 + launchPlaneNormal (the 0dd6bd3a6 decline
            // fix is structurally untouched). The solved transfer plane is plane(r1,r2) by construction, which
            // near a ~180 deg transfer carries an amplified projection of the target's out-of-plane z-offset as
            // a 2-5 deg tilt (vs Duna's real 0.06 deg). If the inclination exceeds the TARGET-DERIVED bound,
            // re-pin the plane onto the target body's own well-conditioned orbital plane - but ONLY when holding
            // r1 fixed can actually reach that plane (the achievability gate); otherwise decline to faithful
            // rather than over-flatten an inclined target. Inserted AFTER the sane+direction guards so it only
            // ever runs on an already-prograde, already-sane conic. See the plan doc for the full mechanism.
            double launchInc = launchBody.orbit.inclination;
            double targetInc = targetBody.orbit.inclination;
            double tiltBound = InclinationBoundDegrees(launchInc, targetInc);
            double incBefore = transfer.inclination;
            if (IsExcessiveTiltTransfer(incBefore, tiltBound))
            {
                // v2Target in the SAME .xzy-unswizzled Lambert frame as r1/r2.
                Vector3d v2Target = targetBody.orbit.getOrbitalVelocityAtUT(arrivalUT).xzy;
                Vector3d nTarget = ComputeIntendedPlaneNormal(r2, v2Target);
                if (nTarget == Vector3d.zero)
                {
                    LogTiltCorrection(incBefore, tiltBound, targetInc, double.NaN, double.NaN,
                        "declined", "degenerate-target");
                    DeclinedCorrectionCount++;
                    failReason = $"tilt correction: degenerate target plane (inc={incBefore.ToString("F2", CultureInfo.InvariantCulture)} > bound={tiltBound.ToString("F2", CultureInfo.InvariantCulture)})";
                    return false;
                }

                double incAch = AchievablePlaneInclinationDegrees(r1, nTarget);
                if (!ConstrainTransferPlaneIsSafe(r1, nTarget, targetInc, InclinationToleranceDegrees))
                {
                    // The Moho-adverse-phase exit (for Duna it never trips): holding r1 fixed cannot reach the
                    // target plane, so re-pinning would over-flatten the inclined target. Decline to faithful.
                    LogTiltCorrection(incBefore, tiltBound, targetInc, incAch, double.NaN,
                        "declined", "unreachable-plane");
                    DeclinedCorrectionCount++;
                    failReason = $"tilt correction: unreachable target plane (incAch={incAch.ToString("F4", CultureInfo.InvariantCulture)} targetInc={targetInc.ToString("F4", CultureInfo.InvariantCulture)} tol={InclinationToleranceDegrees.ToString("F2", CultureInfo.InvariantCulture)})";
                    return false;
                }

                if (!ConstrainTransferPlane(r1, v1, nTarget, out Vector3d v1Corrected))
                {
                    LogTiltCorrection(incBefore, tiltBound, targetInc, incAch, double.NaN,
                        "declined", "degenerate-rotation");
                    DeclinedCorrectionCount++;
                    failReason = "tilt correction: degenerate rotation (r1/nTarget/v_perp degenerate)";
                    return false;
                }

                // Rebuild the conic from (r1, v1') and re-validate, IN ORDER: sane -> direction -> tilt.
                transfer.UpdateFromStateVectors(r1.xzy, v1Corrected.xzy, parent, departureUT);

                if (!IsSaneTransferConic(transfer.eccentricity, transfer.semiMajorAxis))
                {
                    LogTiltCorrection(incBefore, tiltBound, targetInc, incAch, transfer.inclination,
                        "declined", "sane-fail");
                    DeclinedCorrectionCount++;
                    failReason = $"tilt correction: corrected conic not sane ecc={transfer.eccentricity.ToString("R", CultureInfo.InvariantCulture)} sma={transfer.semiMajorAxis.ToString("R", CultureInfo.InvariantCulture)}";
                    return false;
                }

                // The transverse rotation's sign(v_perp . t-hat) preserves the prograde sense, but re-running
                // the direction guard on the corrected conic closes the door on any handedness flip.
                bool correctedRetrograde = IsRetrogradeTransfer(transfer.inclination);
                if (correctedRetrograde != !prograde)
                {
                    LogTiltCorrection(incBefore, tiltBound, targetInc, incAch, transfer.inclination,
                        "declined", "handedness-flip");
                    DeclinedCorrectionCount++;
                    failReason = $"tilt correction: handedness flip inc={transfer.inclination.ToString("F2", CultureInfo.InvariantCulture)} deg " +
                                 $"(correctedRetrograde={correctedRetrograde}, recordedRetrograde={!prograde})";
                    return false;
                }

                if (IsExcessiveTiltTransfer(transfer.inclination, tiltBound))
                {
                    LogTiltCorrection(incBefore, tiltBound, targetInc, incAch, transfer.inclination,
                        "declined", "residual-tilt");
                    DeclinedCorrectionCount++;
                    failReason = $"tilt correction: residual tilt inc={transfer.inclination.ToString("F2", CultureInfo.InvariantCulture)} > bound={tiltBound.ToString("F2", CultureInfo.InvariantCulture)}";
                    return false;
                }

                // Correction FIRED: the plane is re-pinned within tol of the target plane and all re-checks
                // passed. v1 now reflects the corrected velocity (used downstream by nothing further, but kept
                // consistent in case a future step reads it).
                v1 = v1Corrected;
                LogTiltCorrection(incBefore, tiltBound, targetInc, incAch, transfer.inclination,
                    "fired", "ok");
                FiredCorrectionCount++;
            }
            else
            {
                // The already-in-plane case (e.g. the reported 0.13 deg Duna window) - no correction needed.
                LogTiltCorrection(incBefore, tiltBound, targetInc, double.NaN, incBefore, "noop", "in-plane");
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
                LogSynthGeometry(transfer, launchBody, targetBody, departureUT, arrivalUT, soiEntryUT,
                    "patched-conic", hasDepartureOverride, r1);
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
                LogSynthGeometry(transfer, launchBody, targetBody, departureUT, arrivalUT, soiEntryUT,
                    "proximity", hasDepartureOverride, r1);
                return true;
            }

            failReason = $"no target encounter (transition={transfer.patchEndTransition} " +
                         $"closest={(transfer.closestEncounterBody != null ? transfer.closestEncounterBody.bodyName : "<none>")}; " +
                         $"proximity check also failed)";
            return false;
        }

        // Diagnostic: one-shot Verbose line on the tilt-correction decision (plan 3.3). state is
        // fired|noop|declined; reason names the fail-closed branch on a decline. Grep `tilt-correction`.
        private static void LogTiltCorrection(
            double incBefore, double bound, double targetInc, double incAch, double incAfter,
            string state, string reason)
        {
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Verbose("ReaimSeam",
                $"tilt-correction inc-before={incBefore.ToString("F4", ic)} bound={bound.ToString("F4", ic)} " +
                $"targetInc={targetInc.ToString("F4", ic)} " +
                $"incAch={(double.IsNaN(incAch) ? "NaN" : incAch.ToString("F4", ic))} " +
                $"inc-after={(double.IsNaN(incAfter) ? "NaN" : incAfter.ToString("F4", ic))} " +
                $"state={state} reason={reason}");
        }

        // Diagnostic: log the synthesized transfer's geometry against the bodies/points it connects.
        // xfer-vs-{depart} and xfer-vs-target@arrival are ~0 BY CONSTRUCTION on BOTH paths (the Lambert
        // connects r1->r2 exactly): for the DIRECT path the departure anchor r1 is the launch body's center
        // (xfer-vs-launch@depart), for the PARKING-OVERRIDE path r1 is the caller-supplied park-end
        // (xfer-vs-parkEnd@depart). These two lines are a FRAME-CONSISTENCY sanity canary - they catch a
        // dropped/extra .xzy swizzle (which would make the round-trip miss), NOT a mis-aimed r1: r1 is
        // whatever the caller passed, so the line cannot detect that the WRONG r1 was chosen. The ACTUAL
        // departure-seam geometric proof (that r1 == the rendered park-end, evaluated at RecordedDepartureUT
        // rather than departureUT) lives in the in-game r1==park-end gate (ReaimEndToEndInGameTest) + the
        // pure DecideDepartureAnchor eval-UT unit test, not here. xfer-vs-target@soi must be <= the target
        // SOI. All positions are parent-relative in the orbit API's native frame. Verbose, one-shot per
        // window resolve (the resolver caches the window).
        private static void LogSynthGeometry(
            Orbit transfer, CelestialBody launchBody, CelestialBody targetBody,
            double departureUT, double arrivalUT, double soiEntryUT, string path,
            bool hasDepartureOverride, Vector3d departureOverridePosUnswizzled)
        {
            var ic = CultureInfo.InvariantCulture;
            // departureOverridePosUnswizzled is .xzy-unswizzled (the Lambert frame); the transfer position
            // is in the orbit API's native (swizzled) frame, so re-swizzle the override (.xzy is its own
            // inverse) before differencing - matching how r1.xzy was fed to UpdateFromStateVectors.
            double depMiss = hasDepartureOverride
                ? (transfer.getRelativePositionAtUT(departureUT) - departureOverridePosUnswizzled.xzy).magnitude
                : (transfer.getRelativePositionAtUT(departureUT)
                    - launchBody.orbit.getRelativePositionAtUT(departureUT)).magnitude;
            string depAnchorLabel = hasDepartureOverride ? "parkEnd" : launchBody.bodyName;
            double arrMiss = (transfer.getRelativePositionAtUT(arrivalUT)
                - targetBody.orbit.getRelativePositionAtUT(arrivalUT)).magnitude;
            double soiMiss = double.IsNaN(soiEntryUT) ? double.NaN
                : (transfer.getRelativePositionAtUT(soiEntryUT)
                    - targetBody.orbit.getRelativePositionAtUT(soiEntryUT)).magnitude;
            double inc = transfer.inclination;
            double bound = InclinationBoundDegrees(launchBody.orbit.inclination, targetBody.orbit.inclination);
            ParsekLog.Verbose("ReaimSeam",
                $"synth geometry ({path}): departUT={departureUT.ToString("F0", ic)} " +
                $"arrivalUT={arrivalUT.ToString("F0", ic)} soiEntryUT={soiEntryUT.ToString("F0", ic)} " +
                $"sma={transfer.semiMajorAxis.ToString("R", ic)} ecc={transfer.eccentricity.ToString("F4", ic)} " +
                $"inc={inc.ToString("F4", ic)} bound={bound.ToString("F4", ic)} | " +
                $"xfer-vs-{depAnchorLabel}@depart={depMiss.ToString("F0", ic)}m (~0) | " +
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
