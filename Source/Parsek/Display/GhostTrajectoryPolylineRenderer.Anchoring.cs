using System;
using System.Collections.Generic;
using UnityEngine;
using Vectrosity;

namespace Parsek.Display
{
    // Anchoring: a leg onto its bracketing conic seam (residual checks, inertial basis, one-sided diagnostic, undrawn fallback) and the map marker onto a drawn polyline.
    internal static partial class GhostTrajectoryPolylineRenderer
    {
        /// <summary>
        /// Diagnostic: body-relative WORLD longitude (degrees, atan2(z,x) in Y-up world axes) of a
        /// recorded leg point as it is ACTUALLY DRAWN - i.e. <c>GetWorldSurfacePosition(lat,lon,alt)</c>
        /// on the LIVE body minus the body centre. This is the leg's body-FIXED position; comparing it to
        /// the orbit's inertial longitude (the MapRenderProbe icon-off-orbit lonOrbit*) exposes how far
        /// the polyline is rotated from the orbits under the loop shift (the escape-burn "isolated
        /// segment" sits ~one body-rotation-over-the-shift off the inertial loiter/hyperbolic). Matches
        /// <c>MapRenderProbe.LongitudeDeg</c> so the two numbers are directly comparable.
        /// </summary>
        private static double LegPointBodyRelLonDeg(CelestialBody body, double lat, double lon, double alt)
        {
            Vector3d rel = body.GetWorldSurfacePosition(lat, lon, alt) - body.position;
            return System.Math.Atan2(rel.z, rel.x) * (180.0 / System.Math.PI);
        }

        /// <summary>
        /// The world position of an OrbitSegment's RECORDED conic at <paramref name="ut"/>
        /// (raw recorded epoch, no loop shift), built from the stored Kepler elements exactly as
        /// <c>GhostMapPresence.ApplyOrbitToVessel</c> does. Used to locate the conic seam (the loiter's
        /// position at the burn start / the escape's position at the burn end). Returns false on any orbit
        /// construction fault.
        /// </summary>
        private static bool TryConicWorldAtUT(
            OrbitSegment seg, CelestialBody body, double ut, out Vector3d world)
        {
            world = Vector3d.zero;
            if (body == null) return false;
            try
            {
                Orbit orbit = new Orbit(
                    seg.inclination,
                    seg.eccentricity,
                    seg.semiMajorAxis,
                    seg.longitudeOfAscendingNode,
                    seg.argumentOfPeriapsis,
                    seg.meanAnomalyAtEpoch,
                    seg.epoch,
                    body);
                world = orbit.getPositionAtUT(ut);
                return IsFiniteVec(world);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Finite-component guard for a Vector3d (KSP's Vector3d has no instance IsFinite).</summary>
        private static bool IsFiniteVec(Vector3d v)
            => !double.IsNaN(v.x) && !double.IsInfinity(v.x)
               && !double.IsNaN(v.y) && !double.IsInfinity(v.y)
               && !double.IsNaN(v.z) && !double.IsInfinity(v.z);

        /// <summary>
        /// PURE guard predicate (Duna/Ike arrival regression): should the conic-anchor REJECT a leg (keep it
        /// body-fixed) because the bracketing conic seam does NOT meet the leg endpoints? The conic-anchor is
        /// only valid for a vacuum maneuver whose endpoints lie ON the bracketing orbits (residual ~0, e.g.
        /// the Kerbin escape burn = 0 km). A Duna/Ike ARRIVAL HYPERBOLA's <c>getPositionAtUT</c> at the leg
        /// boundary lands an inbound arm tens of Mm from the leg, so the radial residual is huge (430-46543
        /// km) and rotating to it would swing the leg to the wrong arm. Reject when the radial residual
        /// exceeds the absolute floor OR the relative fraction of the leg radius. (A radius-only / "ecc&lt;1"
        /// test is insufficient: a mis-bracketed ELLIPTICAL leg measured 430-3041 km off.) xUnit-testable.
        /// </summary>
        internal static bool IsSeamResidualTooLarge(
            float predResidStartKm, float predResidEndKm, float legRadiusKm)
        {
            if (predResidStartKm > AnchorMaxResidualKm || predResidEndKm > AnchorMaxResidualKm)
                return true;
            float relResid = legRadiusKm > 1f
                ? Mathf.Max(predResidStartKm, predResidEndKm) / legRadiusKm : 0f;
            return relResid > AnchorMaxRelResidual;
        }

        /// <summary>
        /// PURE run-leg frame classifier (playtest-5 rule, 2026-06-09): may this leg participate in the
        /// PERSISTENT render run (drawn while the icon is NOT on it)? True only when the leg is bracketed
        /// by a same-body conic on BOTH sides - the precondition for <see cref="TryAnchorLegToConicSeam"/>
        /// to rotate it onto the INERTIAL conic seam. A one-sided leg (launch ascent = after-only,
        /// descent-to-surface = before-only, no conics at all) stays BODY-FIXED when drawn, so as a
        /// persistent run leg it would visibly rotate with the planet against the inertial arcs (the
        /// observed gap-then-overlap sweep at the chain handoff); such legs draw ONLY while the icon is on
        /// them (the head-gated pass, where body-fixed is correct - the live ghost is glued to the
        /// pad/terrain). The bracket lookup uses the leg's OWN recording's segments - the same list the
        /// draw-time anchor consults - so decide and draw can never disagree on candidacy.
        /// xUnit-testable (no Unity).
        /// </summary>
        internal static bool IsRunLegAnchorCandidate(
            List<OrbitSegment> segs, string bodyName, double legStartUT, double legEndUT)
        {
            FindBracketingOrbitSegments(
                segs, bodyName, legStartUT, legEndUT, out int beforeIdx, out int afterIdx);
            return beforeIdx >= 0 && afterIdx >= 0;
        }

        /// <summary>
        /// PURE (todo LOOP-SUN-LEG-DRAWN-OFF-PATH): is this recorded non-orbital leg replaced by a
        /// RE-AIMED conic? A re-aim loop unit swaps the recorded heliocentric stretch (its coasts AND
        /// the course-correction burns between them) for one synthesized transfer segment aimed at the
        /// target's current position. The burn legs inside that stretch are recorded geometry of the
        /// replaced transfer: drawn, they land where the RECORDED transfer went (the Duna supply ship's
        /// two Sun legs, 12.4 Gm off its icon), and anchored they meet only the recorded conics. True
        /// when <paramref name="effective"/> is a re-aimed list (not the recorded list by reference),
        /// the leg is on the re-aimed body <paramref name="reaimedBodyName"/>, and a same-body effective
        /// segment spans the whole leg. A faithful member (effective == recorded by reference) and a
        /// leg on any other body answer false, so their drawing is unchanged. xUnit-testable.
        /// </summary>
        internal static bool IsLegSupersededByReaimedConic(
            List<OrbitSegment> effective, List<OrbitSegment> recorded, string reaimedBodyName,
            string legBodyName, double legStartUT, double legEndUT)
        {
            if (effective == null || ReferenceEquals(effective, recorded))
                return false;
            if (string.IsNullOrEmpty(reaimedBodyName)
                || !string.Equals(legBodyName, reaimedBodyName, StringComparison.Ordinal))
                return false;
            if (double.IsNaN(legStartUT) || double.IsNaN(legEndUT) || legEndUT < legStartUT)
                return false;
            for (int i = 0; i < effective.Count; i++)
            {
                OrbitSegment s = effective[i];
                if (s.endUT <= s.startUT) continue;
                if (!string.Equals(s.bodyName, reaimedBodyName, StringComparison.Ordinal)) continue;
                if (s.startUT <= legStartUT && s.endUT >= legEndUT)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The re-aimed effective segments of committed member <paramref name="recordingIndex"/> this
        /// frame and the re-aimed body (the plan's common ancestor). False, with nulls, for a member
        /// outside a re-aim unit or a window the resolver keeps faithful (the effective list is the
        /// recorded one by reference), so callers skip the superseded-leg test entirely there.
        /// </summary>
        internal static bool TryResolveReaimedLegFilter(
            int recordingIndex, Recording rec, double currentUT,
            GhostPlaybackLogic.LoopUnitSet loopUnits,
            out List<OrbitSegment> effective, out string reaimedBodyName)
        {
            effective = null;
            reaimedBodyName = null;
            if (rec == null || loopUnits == null)
                return false;
            if (!loopUnits.TryGetUnitForMember(recordingIndex, out GhostPlaybackLogic.LoopUnit unit)
                || !unit.IsReaim || !unit.ReaimPlan.HasValue)
                return false;
            List<OrbitSegment> resolved = GhostMapPresence.ResolveEffectiveMapOrbitSegments(
                recordingIndex, rec.RecordingId, rec.OrbitSegments, currentUT, loopUnits);
            if (resolved == null || ReferenceEquals(resolved, rec.OrbitSegments))
                return false;
            effective = resolved;
            reaimedBodyName = unit.ReaimPlan.Value.CommonAncestor;
            return !string.IsNullOrEmpty(reaimedBodyName);
        }

        /// <summary>Rate-limited log for a leg <see cref="IsLegSupersededByReaimedConic"/> hid.</summary>
        internal static void LogLegSupersededByReaimedConic(
            string pass, string recordingId, int legIndex, double legStartUT, double legEndUT,
            string bodyName)
        {
            ParsekLog.VerboseRateLimited(Tag, "leg-reaim-superseded." + pass + "." + recordingId,
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Leg hidden (inside re-aimed conic): rec={0} leg={1} [{2:F1},{3:F1}] body={4} pass={5} " +
                    "- recorded geometry of the transfer the re-aim replaced",
                    recordingId, legIndex, legStartUT, legEndUT, bodyName ?? "(null)", pass),
                5.0);
        }

        internal static void FindBracketingOrbitSegments(
            List<OrbitSegment> segs, string bodyName, double legStartUT, double legEndUT,
            out int beforeIdx, out int afterIdx)
        {
            beforeIdx = -1;
            afterIdx = -1;
            if (segs == null) return;
            const double tol = 1.0;
            double bestBeforeEnd = double.NegativeInfinity;
            double bestAfterStart = double.PositiveInfinity;
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                if (s.endUT <= s.startUT) continue;
                if (!string.Equals(s.bodyName, bodyName, StringComparison.Ordinal)) continue;
                if (s.endUT <= legStartUT + tol && s.endUT > bestBeforeEnd)
                {
                    bestBeforeEnd = s.endUT;
                    beforeIdx = i;
                }
                if (s.startUT >= legEndUT - tol && s.startUT < bestAfterStart)
                {
                    bestAfterStart = s.startUT;
                    afterIdx = i;
                }
            }
        }

        /// <summary>
        /// PURE diagnostic helper: the body-fixed longitude (deg) to pass to
        /// <see cref="CelestialBody.GetWorldSurfacePosition"/> so the resulting world point sits where the
        /// surface point at <paramref name="recordedLon"/> actually WAS at <paramref name="legStartUT"/>,
        /// not where the LIVE body rotation places it now. GetWorldSurfacePosition applies the body's
        /// CURRENT spin; counter-rotating the longitude by the spin accumulated between legStartUT and
        /// liveUT cancels that drift, so a fixed-UT conic seam (<c>getPositionAtUT(legStartUT)</c>) and this
        /// body-fixed point are compared on ONE rotation basis. The shift is self-consistent with
        /// GetWorldSurfacePosition's own eastward (= prograde) longitude convention (world azimuth =
        /// longitude + rotationAngle(t), with rotationAngle advancing prograde over time), so the constant
        /// offset and absolute sign cancel in the difference and no explicit spin-axis / rotationAngle
        /// lookup is needed. A non-rotating body (<paramref name="rotationPeriod"/> == 0 / non-finite)
        /// returns <paramref name="recordedLon"/> unchanged - no spin drift to undo. xUnit-testable (no
        /// Unity).
        /// </summary>
        internal static double BodyFixedLongitudeAtUT(
            double recordedLon, double legStartUT, double liveUT, double rotationPeriod)
        {
            if (double.IsNaN(rotationPeriod) || double.IsInfinity(rotationPeriod)
                || System.Math.Abs(rotationPeriod) <= 1e-9)
                return recordedLon;
            return recordedLon + (legStartUT - liveUT) * 360.0 / rotationPeriod;
        }

        /// <summary>
        /// PURE inverse of <see cref="BodyFixedLongitudeAtUT"/> (playtest-12 gap-fill): the RECORDED
        /// body-fixed longitude of a world point evaluated at <paramref name="sampleUT"/> but converted
        /// through the body's LIVE rotation (<c>CelestialBody.GetLongitude</c> uses the current spin).
        /// Counter-rotating by the spin accrued between sampleUT and liveUT recovers the longitude the
        /// recorder WOULD have stored at sampleUT, so a conic-sampled gap-fill point lands on the same
        /// rotation basis as the recorded leg points around it. Roundtrip contract:
        /// <c>BodyFixedLongitudeAtUT(RecordedLongitudeAtUT(x, t, live), t, live) == x</c>.
        /// xUnit-testable.
        /// </summary>
        internal static double RecordedLongitudeAtUT(
            double lonAtLiveRotation, double sampleUT, double liveUT, double rotationPeriod)
        {
            if (double.IsNaN(rotationPeriod) || double.IsInfinity(rotationPeriod)
                || System.Math.Abs(rotationPeriod) <= 1e-9)
                return lonAtLiveRotation;
            return lonAtLiveRotation + (liveUT - sampleUT) * 360.0 / rotationPeriod;
        }

        /// <summary>
        /// PURE: the rotation-basis UT for anchored-leg point <paramref name="index"/> — the
        /// point's own recorded UT when the leg carries a matching <c>recordedUTs</c> array, else
        /// the leg start UT (legacy legs without per-point UTs; the anchor's Slerp falls back to
        /// index-fraction there too). Evaluating each anchored point's longitude on its own
        /// recorded basis (via <see cref="BodyFixedLongitudeAtUT"/>) freezes the leg in the
        /// body-centred inertial frame, so <see cref="TryAnchorLegToConicSeam"/>'s calibration
        /// rotation is time-constant and the drawn bridge shape stops morphing with the planet's
        /// spin at warp (2026-06-12 high-warp residual 3). xUnit-testable (no Unity).
        /// </summary>
        internal static double ResolveAnchoredPointBasisUT(
            double[] recordedUTs, int index, int pointCount, double legStartUT)
            => recordedUTs != null && recordedUTs.Length == pointCount
               && index >= 0 && index < pointCount
                ? recordedUTs[index]
                : legStartUT;

        /// <summary>
        /// Diagnostic (Bug 2 / Root B): a polyline leg bracketed by an orbit on only ONE side stays
        /// body-fixed (launch ascent = after-only; descent-from-orbit = before-only). For the descent case
        /// this logs the TRUE geometric world gap + body-relative longitudes between the leg's body-fixed
        /// start and the preceding orbit's seam, BOTH evaluated at leg.startUT (the conic via its fixed-UT
        /// getPositionAtUT, the body-fixed point via <see cref="BodyFixedLongitudeAtUT"/> so live planet
        /// rotation does not inflate the value - it reads ~0 km when the orbit-to-descent seam is geometrically
        /// continuous, as at the s15 "Duna One" re-aim landing). Rate-limited per rec; render-neutral.
        /// </summary>
        private static void EmitOneSidedBracketDiagnostic(
            Recording rec, LegPolyline leg, CelestialBody body, int beforeIdx, int afterIdx)
        {
            if (rec == null || body == null || leg.PointCount < 1) return;
            // Lazy Func: the conic build (new Orbit + getPositionAtUT via TryConicWorldAtUT) +
            // GetWorldSurfacePosition + atan2 + format run ONLY when the 2s rate-limit actually emits, not
            // every frame this one-sided leg draws.
            ParsekLog.VerboseRateLimited(Tag, "polyline.onesided." + rec.RecordingId,
                () =>
                {
                    var ic = System.Globalization.CultureInfo.InvariantCulture;
                    string seamInfo = "before=none(ascent)";
                    if (beforeIdx >= 0
                        && TryConicWorldAtUT(rec.OrbitSegments[beforeIdx], body, leg.startUT, out Vector3d cBefore))
                    {
                        // Evaluate the body-fixed deorbit point on the SAME UT/rotation basis as the conic
                        // (leg.startUT), NOT the live body rotation. A raw GetWorldSurfacePosition uses the
                        // body's CURRENT spin, so it drifts away from the fixed-UT conic seam as the planet
                        // turns while this leg stays the active drawn leg - inflating the logged gap (s15
                        // "Duna One": 0 km at the aligned instant -> ~8 km a few seconds later, purely from
                        // Duna's spin) and overstating the true ~0 km geometric seam. BodyFixedLongitudeAtUT
                        // counter-rotates the longitude so the gap reads the real orbit-to-descent seam.
                        double lonAtStartUT = BodyFixedLongitudeAtUT(
                            leg.lons[0], leg.startUT, Planetarium.GetUniversalTime(), body.rotationPeriod);
                        Vector3d bf = body.GetWorldSurfacePosition(leg.lats[0], lonAtStartUT, leg.alts[0]);
                        double seamGapKm = Vector3d.Distance(bf, cBefore) / 1000.0;
                        Vector3d relBf = bf - body.position;
                        Vector3d relC = cBefore - body.position;
                        double lonBf = System.Math.Atan2(relBf.z, relBf.x) * (180.0 / System.Math.PI);
                        double lonSeam = System.Math.Atan2(relC.z, relC.x) * (180.0 / System.Math.PI);
                        var s = rec.OrbitSegments[beforeIdx];
                        seamInfo = string.Format(ic,
                            "before=seg{0}(ecc={1:F3} sma={2:F0}) seamGap={3:F0}km@startUT lonBodyFixed={4:F1} lonOrbitSeam={5:F1}",
                            beforeIdx, s.eccentricity, s.semiMajorAxis, seamGapKm, lonBf, lonSeam);
                    }
                    return string.Format(ic,
                        "Anchor leg SKIPPED (one-sided): rec={0} leg=[{1:F1},{2:F1}] body={3} anchored=false " +
                        "reason=one-sided-bracket {4} after={5}",
                        rec.RecordingId, leg.startUT, leg.endUT, leg.bodyName ?? "(null)",
                        seamInfo, afterIdx >= 0 ? ("seg" + afterIdx) : "none");
                },
                2.0);
        }

        /// <summary>
        /// CONIC ANCHOR (2026-06-03): rotate a vacuum-maneuver leg's already-captured scaled-space points
        /// so the leg lands on the faithful bracketing conic seam, fixing the loop-shift body rotation
        /// that draws the escape burn ~96 deg off the loiter/hyperbola lines. The body-fixed capture has
        /// the correct SHAPE but the wrong rotation (a pure spin-axis rotation = the body's rotation over
        /// the loop shift); this calibrates that rotation DIRECTLY from the recorded OrbitSegment conics
        /// via <c>getPositionAtUT</c> (the proven-faithful source - NOT the longitude-lift, which lands
        /// 600-1200 km off the conic) at BOTH seam endpoints and Slerps it across the leg.
        ///
        /// <para>Applies ONLY to legs bracketed by a conic on BOTH sides - a vacuum maneuver BETWEEN two
        /// orbits (the escape burn, an orbit raise, a circularization). A launch ascent (no preceding
        /// conic) or a descent-to-surface (no following conic) is left body-fixed so it stays glued to the
        /// rotating pad / landing site. Self-calibrating: where the body-fixed capture already coincides
        /// with the conic (the early parking/raise region, seam gap ~0 km) the recovered rotation is
        /// ~identity, so this is a no-op there - it does NOT regress the regions the reverted longitude-
        /// lift broke.</para>
        ///
        /// <para>Rewrites <c>leg.scratchScaledSpace</c> in place (the array is shared with the cached leg,
        /// so the result persists into the draw). Returns true when applied. The minimal
        /// <c>FromToRotation</c> between the two same-latitude rays IS the spin-axis rotation, so this
        /// needs no explicit rotation-axis lookup.</para>
        ///
        /// <para>INERTIAL BASIS (2026-06-12 high-warp residual 3): on the anchored path the leg's points
        /// are RE-EVALUATED on their own recorded rotation basis (<see cref="BodyFixedLongitudeAtUT"/>
        /// per point) before the calibration, so the anchor rotation is time-constant and the drawn
        /// bridge shape no longer morphs with the planet's spin at warp. Rejected / one-sided legs keep
        /// the caller's live body-fixed capture untouched.</para>
        /// </summary>
        private static bool TryAnchorLegToConicSeam(
            Recording rec, LegPolyline leg, CelestialBody body, Transform scaledXform)
        {
            int m = leg.PointCount;
            if (rec == null || body == null || m < 2 || leg.scratchScaledSpace == null) return false;

            FindBracketingOrbitSegments(
                rec.OrbitSegments, leg.bodyName, leg.startUT, leg.endUT,
                out int beforeIdx, out int afterIdx);
            if (beforeIdx < 0 || afterIdx < 0)
            {
                // One-sided bracket -> leg stays body-fixed (correct: a launch ascent off the rotating pad
                // = after-only; a descent-to-surface = before-only). Bug 2 / Root B diagnostic: for the
                // descent case (an orbit BEFORE, surface after) log the TRUE geometric seam gap +
                // body-relative longitudes between the leg's body-fixed start and the preceding orbit's
                // seam, BOTH on the leg.startUT rotation basis (so the value reads the real orbit-to-descent
                // continuity, ~0 km when geometrically continuous, NOT live-rotation drift). Render-neutral.
                EmitOneSidedBracketDiagnostic(rec, leg, body, beforeIdx, afterIdx);
                return false;
            }

            if (!TryConicWorldAtUT(rec.OrbitSegments[beforeIdx], body, leg.startUT, out Vector3d cBeforeWorld)
                || !TryConicWorldAtUT(rec.OrbitSegments[afterIdx], body, leg.endUT, out Vector3d cAfterWorld))
                return false;

            Vector3 center = scaledXform != null
                ? scaledXform.position
                : (Vector3)ScaledSpace.LocalToScaledSpace(body.position);
            Vector3 cBefore = (Vector3)ScaledSpace.LocalToScaledSpace(cBeforeWorld);
            Vector3 cAfter = (Vector3)ScaledSpace.LocalToScaledSpace(cAfterWorld);

            Vector3 relStart = leg.scratchScaledSpace[0] - center;
            Vector3 relEnd = leg.scratchScaledSpace[m - 1] - center;
            Vector3 cRelStart = cBefore - center;
            Vector3 cRelEnd = cAfter - center;
            if (relStart.sqrMagnitude < 1e-10f || relEnd.sqrMagnitude < 1e-10f
                || cRelStart.sqrMagnitude < 1e-10f || cRelEnd.sqrMagnitude < 1e-10f)
                return false;

            // GUARD (Duna/Ike arrival regression): the body-fixed-vs-conic offset is a pure spin-axis
            // rotation ONLY when the conic seam actually MEETS the leg endpoints (same radius). At Kerbin
            // the bracketing conics are near-circular, so the seam coincides with the leg (residual 0 km).
            // At a Duna/Ike ARRIVAL HYPERBOLA the bracketing conic's getPositionAtUT lands tens of Mm from
            // the leg (an inbound arm far from periapsis), so FromToRotation would swing the whole leg -
            // and the marker that rides it - to the WRONG arm. Reject when the seam radius does not match
            // the leg endpoint radius. The radial residual is the proven discriminator: 0 km at Kerbin vs
            // 430-46543 km at Duna/Ike; an "elliptical-only / ecc<1" test is NOT sufficient (a mis-bracketed
            // ELLIPTICAL leg measured 430-3041 km off). Computed BEFORE mutating so a rejected leg stays
            // body-fixed and TryAnchorMarkerToPolyline samples the body-fixed points.
            float sf = (float)ScaledSpace.ScaleFactor;
            float predResidStartKm = Mathf.Abs(relStart.magnitude - cRelStart.magnitude) * sf / 1000f;
            float predResidEndKm = Mathf.Abs(relEnd.magnitude - cRelEnd.magnitude) * sf / 1000f;
            float legRadiusKm = relStart.magnitude * sf / 1000f;
            float relResid = legRadiusKm > 1f
                ? Mathf.Max(predResidStartKm, predResidEndKm) / legRadiusKm : 0f;
            var segB = rec.OrbitSegments[beforeIdx];
            var segA = rec.OrbitSegments[afterIdx];
            if (IsSeamResidualTooLarge(predResidStartKm, predResidEndKm, legRadiusKm))
            {
                ParsekLog.VerboseRateLimited(Tag, "polyline.anchor." + rec.RecordingId,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Anchor leg SKIPPED: rec={0} leg=[{1:F1},{2:F1}] body={3} anchored=false reason=seam-mismatch " +
                        "predResidStart={4:F0}km predResidEnd={5:F0}km legRadius={6:F0}km relResid={7:F2} " +
                        "before=seg{8}(ecc={9:F3} sma={10:F0}) after=seg{11}(ecc={12:F3} sma={13:F0})",
                        rec.RecordingId, leg.startUT, leg.endUT, leg.bodyName ?? "(null)",
                        predResidStartKm, predResidEndKm, legRadiusKm, relResid,
                        beforeIdx, segB.eccentricity, segB.semiMajorAxis,
                        afterIdx, segA.eccentricity, segA.semiMajorAxis),
                    2.0);
                return false; // leave the leg body-fixed; the marker rides the body-fixed head
            }

            // INERTIAL RE-CAPTURE (2026-06-12 high-warp residual 3, anchored-leg curvature morph):
            // the caller's per-frame capture evaluates the recorded body-fixed lat/lon/alt at the
            // LIVE body rotation, so the anchor's input endpoints rotate with the planet while the
            // conic seam targets (getPositionAtUT at fixed recorded UTs) are inertially fixed.
            // Re-deriving FromToRotation per frame keeps the ENDPOINTS glued to the seam, but the
            // minimal-arc quaternions change as the planet turns (their axis is perpendicular to
            // the input pair, NOT the spin axis), so the Slerp'd interior visibly morphs at warp
            // (tracer: rotAngle drifting at exactly the body rotation rate). Re-evaluating every
            // point on its own RECORDED rotation basis (BodyFixedLongitudeAtUT counter-rotates the
            // longitude) freezes the leg in the body-centred inertial frame: the anchor inputs -
            // and with them rotStart/rotEnd and the drawn shape - become time-constant by
            // construction, and the drawn points are the TRUE recorded inertial path (each sample
            // where it actually was at its own UT). Runs only on the anchored path (after the
            // guard, which reads only rotation-invariant endpoint magnitudes), so rejected /
            // one-sided legs keep the caller's live body-fixed capture byte-identical. Same
            // strobe-safe stable-pieces construction as the caller's capture (scaled body centre
            // + body-relative offset; see the "warp-strobe fix" comment in TryDrawLeg). COST
            // (accepted): this duplicates the caller's m GetWorldSurfacePosition calls for
            // anchored legs (m <= the per-leg point budget) — the anchor decision is not known
            // before the caller's fill, and restructuring the strobe-sensitive capture loop to
            // capture once risks the parity-lens/strobe regressions this file has burned on.
            double liveUT = Planetarium.GetUniversalTime();
            double rotationPeriod = body.rotationPeriod;
            Vector3d bodyPos = body.position;
            double invScale = ScaledSpace.InverseScaleFactor;
            for (int i = 0; i < m; i++)
            {
                double basisUT = ResolveAnchoredPointBasisUT(leg.recordedUTs, i, m, leg.startUT);
                double lonAtBasis = BodyFixedLongitudeAtUT(
                    leg.lons[i], basisUT, liveUT, rotationPeriod);
                Vector3d world = body.GetWorldSurfacePosition(
                    leg.lats[i], lonAtBasis, leg.alts[i]);
                leg.scratchScaledSpace[i] = center + (Vector3)((world - bodyPos) * invScale);
            }
            Vector3 relStartInertial = leg.scratchScaledSpace[0] - center;
            Vector3 relEndInertial = leg.scratchScaledSpace[m - 1] - center;

            Quaternion rotStart = Quaternion.FromToRotation(relStartInertial, cRelStart);
            Quaternion rotEnd = Quaternion.FromToRotation(relEndInertial, cRelEnd);

            double t0 = leg.startUT, span = leg.endUT - leg.startUT;
            bool haveUTs = leg.recordedUTs != null && leg.recordedUTs.Length == m && span > 0.0;
            for (int i = 0; i < m; i++)
            {
                float frac = haveUTs
                    ? (float)((leg.recordedUTs[i] - t0) / span)
                    : (m > 1 ? (float)i / (m - 1) : 0f);
                if (frac < 0f) frac = 0f; else if (frac > 1f) frac = 1f;
                Quaternion rot = Quaternion.Slerp(rotStart, rotEnd, frac);
                leg.scratchScaledSpace[i] = center + rot * (leg.scratchScaledSpace[i] - center);
            }

            // Residual proves the pin (should be ~0 after the guard). Enriched with the bracketing-conic
            // sma/ecc + applied rotation angles so a future mismatch is self-evident without cross-grep.
            float residStart = Vector3.Distance(leg.scratchScaledSpace[0], cBefore) * sf / 1000f;
            float residEnd = Vector3.Distance(leg.scratchScaledSpace[m - 1], cAfter) * sf / 1000f;
            ParsekLog.VerboseRateLimited(Tag, "polyline.anchor." + rec.RecordingId,
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Anchor leg: rec={0} leg=[{1:F1},{2:F1}] body={3} anchored=true basis=inertial " +
                    "before=seg{4}(ecc={5:F3} sma={6:F0}) " +
                    "after=seg{7}(ecc={8:F3} sma={9:F0}) residualStart={10:F0}km residualEnd={11:F0}km " +
                    "rotAngleStart={12:F1} rotAngleEnd={13:F1}",
                    rec.RecordingId, leg.startUT, leg.endUT, leg.bodyName ?? "(null)",
                    beforeIdx, segB.eccentricity, segB.semiMajorAxis,
                    afterIdx, segA.eccentricity, segA.semiMajorAxis, residStart, residEnd,
                    Quaternion.Angle(Quaternion.identity, rotStart),
                    Quaternion.Angle(Quaternion.identity, rotEnd)),
                2.0);
            return true;
        }

        /// <summary>
        /// What a marker riding the polyline should do when the leg whose head-UT span it covers was NOT
        /// drawn this frame (its <see cref="LegPolyline.scratchScaledSpace"/> is stale).
        /// </summary>
        internal enum UndrawnLegFallback : byte
        {
            /// <summary>
            /// Hold the last on-line position (the conic-anchored leg's drawn line is rotated off the
            /// body-fixed head, so the caller's body-fixed fallback would land off the line).
            /// </summary>
            HoldLastGoodOnLine = 0,

            /// <summary>
            /// Let the caller keep its fresh body-fixed head (a non-anchored body-fixed leg draws the raw
            /// body-fixed points, so the head is already on the line AND current - no stale hold needed).
            /// </summary>
            UseFreshBodyFixedHead = 1,
        }

        /// <summary>
        /// PURE decision (descent-icon visibility): for a leg covering the marker's head UT that was NOT
        /// drawn this frame, should the marker hold its last on-line position or fall through to the
        /// caller's fresh body-fixed head? A CONIC-ANCHORED leg's drawn line is rotated ~96 deg off the
        /// body-fixed head under the loop shift, so the body-fixed fallback would jump off the line -> HOLD.
        /// A NON-anchored body-fixed leg (descent / atmospheric / surface) draws the raw body-fixed points,
        /// so the caller's fresh body-fixed head is the SAME on-line point but current -> USE it (a
        /// &lt;=5 s-stale hold would only lag the moving descent at warp / on the IMGUI Layout-pass dropout).
        /// This decouples the descent marker's presence from whether the polyline leg happened to be redrawn
        /// THIS frame, without moving the icon off the line. xUnit-testable without Unity.
        /// </summary>
        internal static UndrawnLegFallback ResolveUndrawnLegFallback(bool legWasConicAnchored)
            => legWasConicAnchored
                ? UndrawnLegFallback.HoldLastGoodOnLine
                : UndrawnLegFallback.UseFreshBodyFixedHead;

        /// <summary>
        /// Rides the polyline with a labeled marker (icon + label): returns the world position ON the
        /// drawn polyline at <paramref name="headUT"/> (recorded frame), so the marker sits exactly on the
        /// corrected burn line instead of the body-fixed head (~96 deg off under the loop shift). It
        /// samples the leg's per-frame DRAWN points (<see cref="LegPolyline.scratchScaledSpace"/> - already
        /// conic-anchored by <see cref="TryAnchorLegToConicSeam"/>, or plain body-fixed for a non-anchored
        /// leg, so the marker always matches whatever the line actually shows - no separate rotation to
        /// drift) and interpolates by recorded-time fraction, then converts scaled-&gt;world. Returns false
        /// (caller keeps the body-fixed head) when the head is not inside a leg drawn THIS frame, so a stale
        /// scratch is never read. Call only after the Driver's LateUpdate (e.g. from OnGUI marker draw).
        /// </summary>
        internal static bool TryAnchorMarkerToPolyline(
            string recordingId, double headUT, out Vector3 worldPos)
        {
            // Thin wrapper preserving the original 3-arg contract; the reason/leg out-params are
            // only consumed by the marker tracer. Behavior is byte-identical to the diagnostics
            // overload below (same control flow, same returns).
            return TryAnchorMarkerToPolyline(
                recordingId, headUT, out worldPos, out _, out _);
        }

        /// <summary>
        /// Diagnostics overload of <see cref="TryAnchorMarkerToPolyline(string,double,out Vector3)"/>
        /// that ALSO reports WHY the ride did or did not happen (<paramref name="rideReason"/>) and,
        /// on a successful ride, the leg index (<paramref name="legIndex"/>). The ride LOGIC is
        /// unchanged - every branch sets the reason then takes the exact same return path as before -
        /// so the marker still rides or falls back identically; only the explanation is surfaced.
        /// </summary>
        internal static bool TryAnchorMarkerToPolyline(
            string recordingId, double headUT, out Vector3 worldPos,
            out MapRenderTrace.MarkerRideReason rideReason, out int legIndex)
        {
            worldPos = Vector3.zero;
            rideReason = MapRenderTrace.MarkerRideReason.FallbackNoCache;
            legIndex = -1;
            if (string.IsNullOrEmpty(recordingId)) return false;
            if (!polylineCache.TryGetValue(recordingId, out var set) || set.legs == null) return false;

            // A cache entry exists; the default now becomes "head fell outside every leg" unless a
            // leg matches below.
            rideReason = MapRenderTrace.MarkerRideReason.FallbackHeadOutsideLegs;
            int frame = Time.frameCount;
            for (int li = 0; li < set.legs.Length; li++)
            {
                var leg = set.legs[li];
                if (headUT < leg.startUT || headUT > leg.endUT) continue;
                int m = leg.PointCount;
                if (m < 2 || leg.scratchScaledSpace == null
                    || leg.recordedUTs == null || leg.recordedUTs.Length != m)
                {
                    rideReason = MapRenderTrace.MarkerRideReason.FallbackMissingRecordedUTs;
                    return false; // missing recorded-UT / scratch arrays -> cannot bracket (hard fallback)
                }
                if (leg.lastDrawnFrame != frame)
                {
                    // The leg matched the head but was not drawn this frame, so its scratch is stale.
                    // ResolveUndrawnLegFallback decides what to do by the leg's anchor state:
                    //  - CONIC-ANCHORED leg: the drawn line is rotated ~96 deg off the body-fixed head, so
                    //    snapping the caller to the body-fixed head would jump it off the line. Hold the last
                    //    on-line position if it is still fresh + near in UT (the dominant active-pan case);
                    //    a sustained stall expires the hold (frame-age / UT bound) and falls through to false.
                    //  - NON-anchored body-fixed leg (descent / atmospheric / surface): the drawn line IS the
                    //    raw body-fixed points, so the caller's fresh body-fixed head is already on the line
                    //    AND current. Return false WITHOUT a hold so the caller keeps that fresh head - the
                    //    descent-icon decouple: a stale hold would only lag the moving descent at warp / on
                    //    the IMGUI Layout-pass dropout. (TryHoldLastGood is skipped entirely, so the marker is
                    //    never pinned to a <=5 s-old position for a body-fixed descent leg.)
                    if (ResolveUndrawnLegFallback(leg.wasAnchored) == UndrawnLegFallback.HoldLastGoodOnLine
                        && TryHoldLastGood(recordingId, headUT, frame, out worldPos, out legIndex))
                    {
                        rideReason = MapRenderTrace.MarkerRideReason.HeldLastGood;
                        return true;
                    }
                    rideReason = leg.wasAnchored
                        ? MapRenderTrace.MarkerRideReason.FallbackLegNotDrawnThisFrame
                        : MapRenderTrace.MarkerRideReason.FallbackNonAnchoredUseHead;
                    return false;
                }

                // Bracket headUT between two recorded sample UTs and lerp the drawn (anchored) points.
                int idx = m - 2;
                for (int i = 0; i < m - 1; i++)
                {
                    if (headUT <= leg.recordedUTs[i + 1]) { idx = i; break; }
                }
                double u0 = leg.recordedUTs[idx], u1 = leg.recordedUTs[idx + 1];
                float frac = u1 > u0 ? (float)((headUT - u0) / (u1 - u0)) : 0f;
                if (frac < 0f) frac = 0f; else if (frac > 1f) frac = 1f;
                Vector3 scaled = Vector3.Lerp(
                    leg.scratchScaledSpace[idx], leg.scratchScaledSpace[idx + 1], frac);
                worldPos = (Vector3)ScaledSpace.ScaledToLocalSpace(scaled);
                rideReason = MapRenderTrace.MarkerRideReason.RodeLeg;
                legIndex = li;
                // Stamp the last-good cache so a transient ride dropout (active pan / inter-leg gap) can
                // hold this on-line position instead of snapping to the body-fixed head.
                lastGoodOnLine[recordingId] = new LastGoodOnLine
                {
                    worldPos = worldPos,
                    headUT = headUT,
                    frame = frame,
                    legIndex = li
                };
                return true;
            }

            // Head matched no leg's [start,end]. If it sits in an inter-leg GAP inside the recording's
            // overall span (a connector/deorbit seam between two drawn legs - the case that otherwise
            // starves a leg of its icon), hold the last on-line position. A head BEFORE the first leg or
            // PAST the last leg is a genuine orbital-phase exit and must fall through to the deep fallback.
            if (set.legs.Length > 0)
            {
                double firstStartUT = set.legs[0].startUT;
                double lastEndUT = set.legs[set.legs.Length - 1].endUT;
                if (IsHeadInInterLegGap(firstStartUT, lastEndUT, headUT)
                    && TryHoldLastGood(recordingId, headUT, frame, out worldPos, out legIndex))
                {
                    rideReason = MapRenderTrace.MarkerRideReason.HeldLastGood;
                    return true;
                }
            }
            return false;
        }
    }
}
