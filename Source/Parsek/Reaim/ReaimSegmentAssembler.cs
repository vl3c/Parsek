using System.Collections.Generic;

namespace Parsek.Reaim
{
    // Per-member OrbitSegment substitution for a re-aimed ghost (docs/dev/plans/
    // reaim-interplanetary-transfers.md, Phase 3). PURE: operates on OrbitSegment structs + scalars, so
    // the substitution + UT coherence is unit-testable; the live caller supplies the synthesized
    // heliocentric transfer segment (built from ReaimTransferSynthesizer's Orbit) and the member's OWN
    // recorded segments.
    //
    // MODEL (per-member heliocentric replacement). Only the heliocentric (common-ancestor / Sun) leg is
    // inertial-fixed and "misses" when replayed faithfully after the target body has moved over a synodic
    // period. The launch-body parking orbit, the target-body capture orbit, and body-fixed ascent/descent
    // are reconstructed RELATIVE to their live body (new Orbit(..., body)), so they already follow that
    // body's current position and stay correct. So re-aim replaces ONLY the heliocentric leg(s) within
    // each member that has them, leaving every other segment untouched. This handles a chained mission
    // (launch leg / transfer leg / arrival leg / debris as separate recordings) correctly: the transfer
    // member's heliocentric segments are re-aimed; the launch / arrival / debris members carry no
    // heliocentric leg and pass through faithfully (their body-relative segments follow Kerbin / Duna /
    // their parent).
    //
    // TIMELINE: the engine samples a loop member's trajectory at loopUT in [spanStart, spanEnd]
    // (GhostPlaybackLogic.TryComputeSpanLoopUT), and phaseAnchor maps the live clock to the chosen synodic
    // window. The re-aimed transfer is placed at the member's RECORDED [recordedDepartureUT,
    // recordedArrivalUT] (the SOI-exit -> target-SOI-entry window) with the recorded tof, so it fits the
    // member's recorded timeline exactly. What varies PER WINDOW is only the transfer orbit's INERTIAL
    // ORIENTATION (inc/LAN/argPe, aiming at the target's actual position at that window) and its epoch -
    // which the live caller shifts into recorded-span time before calling here.
    internal static class ReaimSegmentAssembler
    {
        /// <summary>
        /// Near-equatorial inclination guard (degrees) for the heliocentric-park LAN re-phase
        /// (<see cref="ReplaceHeliocentricLeg"/> parkDeltaLonDeg). The LAN += Delta_lon rotation rotates
        /// the orbit's position about the parent reference-plane normal exactly for inc = 0 and degrades
        /// with inclination. A re-aim heliocentric park is admissibility-gated near-circular + co-orbital
        /// with the (near-ecliptic) launch body by ReaimClassifier, so a real park is well under this. A
        /// park above this threshold (or a degenerate omega) fails CLOSED - the caller declines the
        /// window to faithful (recorded) playback rather than rendering a wrong-plane rotation.
        /// </summary>
        internal const double ParkRephaseMaxInclinationDeg = 15.0;

        /// <summary>
        /// Returns a copy of <paramref name="seg"/> with its <see cref="OrbitSegment.longitudeOfAscendingNode"/>
        /// advanced by <paramref name="deltaLonDeg"/> (degrees) and re-wrapped to [0, 360). For a
        /// near-equatorial orbit this rotates the orbit's inertial orientation - and so its position at
        /// every preserved mean anomaly - by <paramref name="deltaLonDeg"/> about the parent
        /// reference-plane normal (R_z(LAN + d) = R_z(d) * R_z(LAN); exact for inc = 0). The orbit shape
        /// (inc/ecc/sma/argPe/mEp/epoch) and body/UTs are untouched, so the orbit still replays at its
        /// recorded-span UTs - only its inertial longitude turns. Pure. Used to re-phase a recorded
        /// heliocentric PARK from its recorded (Sun-inertial) longitude into the LIVE frame so it connects
        /// to the body-relative escape leg + the per-window re-aimed transfer.
        /// </summary>
        internal static OrbitSegment RotateLanForParkRephase(OrbitSegment seg, double deltaLonDeg)
        {
            double lan = (seg.longitudeOfAscendingNode + deltaLonDeg) % 360.0;
            if (lan < 0.0)
                lan += 360.0;
            seg.longitudeOfAscendingNode = lan;
            return seg;
        }

        /// <summary>
        /// The per-window park re-phase angle (degrees): how far the launch body's heliocentric longitude
        /// advanced between the RECORDED departure and a supplied live replay time
        /// <paramref name="departureUTForWindow"/>. <c>Delta_lon = omega_parent * (replayUT -
        /// recordedDepartureUT)</c> where <c>omega_parent = 360 / launchBodyOrbitPeriodSeconds</c> (deg/s,
        /// the launch body's HELIOCENTRIC rate - NOT its rotation period). The recorded park sits at the
        /// recorded-epoch longitude; rotating its LAN by this angle moves it to where the launch body is at
        /// replayUT, so it sits next to the LIVE launch body. The caller passes the engine's CADENCE-clock
        /// relaunch time (<c>schedule.RelaunchUTForWindow(window)</c>) - the live instant the ghost replays
        /// its recorded departure, which the body-relative escape leg also tracks - NOT the synodic transfer
        /// departure (<c>schedule.DepartureUTForWindow(window)</c>); the two coincide when cadence ==
        /// synodic. Returns NaN on a degenerate period (&lt;= 0 / NaN / Inf) so the caller fails closed to
        /// faithful. Pure (no Unity). The result may exceed 360 (the LAN re-wrap in
        /// <see cref="RotateLanForParkRephase"/> reduces it).
        /// </summary>
        internal static double ComputeParkDeltaLonDegrees(
            double departureUTForWindow, double recordedDepartureUT, double launchBodyOrbitPeriodSeconds)
        {
            if (double.IsNaN(launchBodyOrbitPeriodSeconds) || double.IsInfinity(launchBodyOrbitPeriodSeconds)
                || launchBodyOrbitPeriodSeconds <= 0.0
                || double.IsNaN(departureUTForWindow) || double.IsNaN(recordedDepartureUT))
                return double.NaN;
            double omegaParent = 360.0 / launchBodyOrbitPeriodSeconds; // deg/s
            return omegaParent * (departureUTForWindow - recordedDepartureUT);
        }

        /// <summary>
        /// The recorded heliocentric PARK segment - the latest non-predicted
        /// <paramref name="commonAncestor"/>-bodied segment ending at/before
        /// <paramref name="recordedDepartureUT"/> (the coast BEFORE the trans-target burn). Returns null
        /// when the member has no such segment (e.g. a direct transfer with no park). This is the SINGLE
        /// SOURCE of both the near-equatorial inc guard (<see cref="FindHeliocentricParkInclination"/>
        /// delegates here) AND the live caller's r1 departure-anchor reconstruction (the LAN-rotated park
        /// the icon abuts at the burn), so the two can never diverge on which segment is "the park". Pure.
        /// </summary>
        internal static OrbitSegment? FindHeliocentricParkSegment(
            IReadOnlyList<OrbitSegment> memberSegments, string commonAncestor, double recordedDepartureUT)
        {
            if (memberSegments == null || string.IsNullOrEmpty(commonAncestor))
                return null;
            OrbitSegment? park = null;
            double latestEnd = double.NegativeInfinity;
            for (int i = 0; i < memberSegments.Count; i++)
            {
                OrbitSegment s = memberSegments[i];
                if (!s.isPredicted && s.bodyName == commonAncestor
                    && s.endUT <= recordedDepartureUT + 1.0 && s.endUT >= latestEnd)
                {
                    latestEnd = s.endUT;
                    park = s;
                }
            }
            return park;
        }

        /// <summary>
        /// The inclination (degrees) of the recorded heliocentric PARK (the segment
        /// <see cref="FindHeliocentricParkSegment"/> returns). Returns NaN when the member has no such
        /// segment (e.g. a direct transfer with no park). The caller uses this for the near-equatorial
        /// fail-closed guard (<see cref="ParkRephaseMaxInclinationDeg"/>). Delegates to
        /// <see cref="FindHeliocentricParkSegment"/> so the inc guard and the r1 source can never diverge.
        /// Pure.
        /// </summary>
        internal static double FindHeliocentricParkInclination(
            IReadOnlyList<OrbitSegment> memberSegments, string commonAncestor, double recordedDepartureUT)
        {
            OrbitSegment? park = FindHeliocentricParkSegment(memberSegments, commonAncestor, recordedDepartureUT);
            return park.HasValue ? park.Value.inclination : double.NaN;
        }

        /// <summary>
        /// The fail-closed park re-phase decision (pure). Returns true with the window's
        /// <paramref name="parkDeltaLonDeg"/> when the recorded park can be safely re-phased: a finite
        /// <see cref="ComputeParkDeltaLonDegrees"/> angle AND a near-equatorial park
        /// (<paramref name="parkInclinationDeg"/> &lt;= <see cref="ParkRephaseMaxInclinationDeg"/>, not
        /// NaN). Returns false (parkDeltaLonDeg = 0) on a degenerate omega / non-equatorial / unknown-inc
        /// park - the caller then declines the window to faithful (never the verbatim park). Pure.
        /// </summary>
        internal static bool TryComputeParkRephase(
            double parkInclinationDeg, double departureUTForWindow, double recordedDepartureUT,
            double launchBodyOrbitPeriodSeconds, out double parkDeltaLonDeg)
        {
            parkDeltaLonDeg = 0.0;
            double dLon = ComputeParkDeltaLonDegrees(
                departureUTForWindow, recordedDepartureUT, launchBodyOrbitPeriodSeconds);
            if (double.IsNaN(dLon)
                || double.IsNaN(parkInclinationDeg)
                || parkInclinationDeg > ParkRephaseMaxInclinationDeg)
                return false;
            parkDeltaLonDeg = dLon;
            return true;
        }

        /// <summary>
        /// The pure decision returned by <see cref="DecideDepartureAnchor"/>: the anchor mode, the park
        /// LAN re-phase angle, and the UT the live caller must evaluate the LAN-rotated park at to derive
        /// the r1 / vel override (<see cref="DepartureAnchorMode.ParkEndOverride"/> only; NaN otherwise).
        /// </summary>
        internal struct DepartureAnchorDecision
        {
            public DepartureAnchorMode Mode;
            public double ParkDeltaLonDeg;  // LAN advance for the park rephase (0 unless ParkEndOverride)
            public double ParkEvalUT;       // UT to eval the LAN-rotated park at (== RecordedDepartureUT for
                                            // ParkEndOverride; NaN otherwise) - BLOCKER 1
        }

        /// <summary>
        /// The outcome of <see cref="DecideDepartureAnchor"/>: WHICH transfer-departure-anchor mode the
        /// resolver must use this window, plus the LAN re-phase angle and the UT at which the live caller
        /// must evaluate the (LAN-rotated) park to derive r1 / vel for the override.
        /// </summary>
        internal enum DepartureAnchorMode
        {
            /// <summary>Direct transfer (not a parking departure): r1 stays the launch body's center
            /// (byte-identical to main). No park rotation.</summary>
            LaunchCenter,
            /// <summary>Parking departure with a safe park re-phase: r1 is overridden to the LAN-rotated
            /// park-end state evaluated at <see cref="DepartureAnchorDecision.ParkEvalUT"/>
            /// (== RecordedDepartureUT). The park leg is LAN-rotated by
            /// <see cref="DepartureAnchorDecision.ParkDeltaLonDeg"/>.</summary>
            ParkEndOverride,
            /// <summary>Parking departure whose park re-phase DECLINED (non-equatorial / degenerate): fail
            /// CLOSED to faithful (decline the window) - NEVER fall back to the launch center for a parking
            /// departure (that would render the original seam disguised as a fix).</summary>
            DeclineToFaithful
        }

        /// <summary>
        /// The pure departure-anchor decision (the override-vs-no-override DECISION, headlessly testable).
        /// Does NOT touch Unity: the resolver reconstructs the LAN-rotated park Orbit and evaluates r1 / vel
        /// at <see cref="DepartureAnchorDecision.ParkEvalUT"/> at the call site (Orbit / CelestialBody are
        /// Unity-bound). Three outcomes:
        /// <list type="bullet">
        /// <item><c>departedFromHeliocentricPark == false</c> => <see cref="DepartureAnchorMode.LaunchCenter"/>
        /// (direct transfer, parkDeltaLonDeg = 0, no eval).</item>
        /// <item>parking departure AND <see cref="TryComputeParkRephase"/> succeeds =>
        /// <see cref="DepartureAnchorMode.ParkEndOverride"/> with the rephase angle and
        /// <c>ParkEvalUT = recordedDepartureUT</c> (BLOCKER 1: the park-end the icon abuts renders at the
        /// RECORDED burn UT, not the future synodic departure D0).</item>
        /// <item>parking departure AND the park re-phase DECLINES =>
        /// <see cref="DepartureAnchorMode.DeclineToFaithful"/> (fail closed, never the launch center).</item>
        /// </list>
        /// Pure (no Unity).
        /// </summary>
        internal static DepartureAnchorDecision DecideDepartureAnchor(
            bool departedFromHeliocentricPark, double parkInclinationDeg,
            double parkReplayUT, double recordedDepartureUT, double launchBodyOrbitPeriodSeconds)
        {
            if (!departedFromHeliocentricPark)
                return new DepartureAnchorDecision
                {
                    Mode = DepartureAnchorMode.LaunchCenter,
                    ParkDeltaLonDeg = 0.0,
                    ParkEvalUT = double.NaN
                };

            if (!TryComputeParkRephase(
                    parkInclinationDeg, parkReplayUT, recordedDepartureUT, launchBodyOrbitPeriodSeconds,
                    out double parkDeltaLonDeg))
                return new DepartureAnchorDecision
                {
                    Mode = DepartureAnchorMode.DeclineToFaithful,
                    ParkDeltaLonDeg = 0.0,
                    ParkEvalUT = double.NaN
                };

            return new DepartureAnchorDecision
            {
                Mode = DepartureAnchorMode.ParkEndOverride,
                ParkDeltaLonDeg = parkDeltaLonDeg,
                // BLOCKER 1: the rendered park-end the icon abuts is the LAN-rotated park evaluated at the
                // RECORDED burn UT (the park is only LAN-rotated, NOT ShiftInTime'd - it keeps its recorded
                // epoch / startUT / endUT). Evaluating at the future synodic departure D0 would advance the
                // park a non-integer number of revs over (D0 - RecordedDepartureUT = N*synodic) and re-open
                // the seam. So r1 must be evaluated HERE, never at departureUT.
                ParkEvalUT = recordedDepartureUT
            };
        }

        /// <summary>
        /// Shifts an OrbitSegment in time by <paramref name="deltaUT"/>, moving startUT/endUT AND the
        /// Kepler epoch by the same amount so the mean anomaly at each corresponding time is unchanged
        /// (M(t) = mEp + n*(t - epoch); shifting t and epoch together preserves the phase along the
        /// orbit). The orbit shape (inc/ecc/sma/LAN/argPe) and body are untouched. Pure.
        /// </summary>
        internal static OrbitSegment ShiftInTime(OrbitSegment seg, double deltaUT)
        {
            seg.startUT += deltaUT;
            seg.endUT += deltaUT;
            seg.epoch += deltaUT;
            return seg;
        }

        /// <summary>
        /// Re-times the IN-CAPTURE PartEvents (those with <c>ut &gt;= recordedArrivalUT - eps</c>) by
        /// <paramref name="captureShiftSeconds"/> so a recorded capture-phase event (capture-burn / staging /
        /// decouple) stays aligned with the capture OrbitSegment after that segment was
        /// <see cref="ShiftInTime"/>'d by the SAME amount (the parking-departure F2 path arrives on the
        /// shorter Hohmann tof, so the capture leg + its events move earlier together). PartEvents are
        /// consumed monotonically by <c>GhostPlaybackLogic.ApplyPartEvents</c> at their <c>ut</c> against the
        /// playback clock, so without this the FX would fire <c>|captureShift|</c> off from the shifted
        /// geometry. Returns a NEW list with the in-capture events shifted and every PRE-capture event
        /// (transfer / launch / park phase) untouched, KEEPING the original event ordering (the resolver's
        /// shift is always &lt;= 0, so the relative order of the shifted block is preserved and it cannot
        /// cross into the pre-capture block; the engine re-sorts defensively anyway). When
        /// <paramref name="captureShiftSeconds"/> is 0 (or events are null/empty) the input reference is
        /// returned unchanged (byte-identical direct path / no-op). Pure.
        /// </summary>
        internal static List<PartEvent> ShiftCapturePartEvents(
            IReadOnlyList<PartEvent> events, double recordedArrivalUT, double captureShiftSeconds)
        {
            if (events == null)
                return null;
            if (events.Count == 0 || captureShiftSeconds == 0.0)
                return new List<PartEvent>(events); // no-op copy (caller owns a List it can hand to the adapter)
            var result = new List<PartEvent>(events.Count);
            for (int i = 0; i < events.Count; i++)
            {
                PartEvent e = events[i];
                if (e.ut >= recordedArrivalUT - 1.0)
                    e.ut += captureShiftSeconds; // in-capture event moves with its (earlier) capture segment
                result.Add(e);
            }
            return result;
        }


        /// <summary>
        /// True when <paramref name="memberSegments"/> contains at least one non-predicted
        /// <paramref name="commonAncestor"/>-bodied (heliocentric) OrbitSegment overlapping the recorded
        /// transfer window [<paramref name="recordedDepartureUT"/>, <paramref name="recordedArrivalUT"/>].
        /// This is the cheap pre-check that lets the live caller skip the Lambert solve for members that
        /// carry no heliocentric leg (launch / arrival / debris legs of a chained mission). Pure.
        /// </summary>
        internal static bool HasHeliocentricLegInWindow(
            IReadOnlyList<OrbitSegment> memberSegments, string commonAncestor,
            double recordedDepartureUT, double recordedArrivalUT)
        {
            if (memberSegments == null || string.IsNullOrEmpty(commonAncestor))
                return false;
            for (int i = 0; i < memberSegments.Count; i++)
            {
                OrbitSegment s = memberSegments[i];
                if (!s.isPredicted && s.bodyName == commonAncestor
                    && s.startUT < recordedArrivalUT && s.endUT > recordedDepartureUT)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns a copy of <paramref name="memberSegments"/> with its heliocentric
        /// (<paramref name="commonAncestor"/>-bodied) leg(s) in the recorded transfer window
        /// [<paramref name="recordedDepartureUT"/>, <paramref name="recordedArrivalUT"/>] REPLACED by the
        /// single re-aimed <paramref name="transferSegment"/>, and every OTHER segment kept untouched.
        /// This is the per-member substitution (docs/dev/plans/reaim-interplanetary-transfers.md): only
        /// the heliocentric coast is inertial-fixed and must be re-aimed; the launch-body parking orbit,
        /// the target-body capture orbit, and body-fixed legs are reconstructed relative to their LIVE
        /// body, so they already follow it and stay faithful. Any number of recorded heliocentric
        /// segments in the window (e.g. mid-course-correction coasts) collapse into the one re-aimed arc.
        /// The caller has already placed the transfer at [departure, arrival] with its per-window
        /// orientation + recorded-span epoch. Pure. Returns null when the member has NO heliocentric leg
        /// in the window (nothing to substitute -> the member stays faithful).
        ///
        /// <para><paramref name="parkDeltaLonDeg"/> (default 0 = no rotation, byte-identical to the
        /// direct-transfer path): the per-window park re-phase angle. When non-zero, each recorded
        /// heliocentric PARK segment (a non-predicted <paramref name="commonAncestor"/>-bodied coast
        /// ENDING at/before <paramref name="recordedDepartureUT"/> - the coast BEFORE the trans-target
        /// burn) has its LAN advanced by this angle (<see cref="RotateLanForParkRephase"/>) so the
        /// Sun-inertial recorded park is rotated into the LIVE frame and connects to the escape leg + the
        /// re-aimed transfer. Only the departure-side park is rotated; the in-window transfer leg, the
        /// body-relative Kerbin/Duna legs, and any predicted tails are untouched. The caller computes the
        /// angle via <see cref="ComputeParkDeltaLonDegrees"/> and gates / fails-closed on the
        /// near-equatorial guard - this method only applies a supplied non-zero rotation.</para>
        ///
        /// <para><paramref name="captureRetimeShiftSeconds"/> (default 0 = no shift, byte-identical to the
        /// direct-transfer path) + <paramref name="targetBody"/> (default null): the parking-departure
        /// capture-leg re-time. When the re-aimed transfer arrives EARLIER than the recorded arrival (the
        /// Hohmann tof is shorter than the recorded tof), the recorded target-body capture leg(s) must move
        /// back to meet the new arrival. When <paramref name="captureRetimeShiftSeconds"/> != 0 AND
        /// <paramref name="targetBody"/> != null, each non-predicted <paramref name="targetBody"/>-bodied
        /// segment starting at/after <paramref name="recordedArrivalUT"/> is
        /// <see cref="ShiftInTime"/>'d by this (negative = earlier) amount, BEFORE the sort/coalesce, so its
        /// startUT/endUT/epoch move together (phase preserved). The transfer + launch + park legs are
        /// untouched. Defaults (0 / null) leave every capture segment byte-identical (direct path).</para>
        /// </summary>
        internal static List<OrbitSegment> ReplaceHeliocentricLeg(
            IReadOnlyList<OrbitSegment> memberSegments, OrbitSegment transferSegment,
            string commonAncestor, double recordedDepartureUT, double recordedArrivalUT,
            double transferRenderStartUT, double transferRenderEndUT,
            double parkDeltaLonDeg = 0.0,
            double captureRetimeShiftSeconds = 0.0, string targetBody = null)
        {
            if (memberSegments == null || string.IsNullOrEmpty(commonAncestor))
                return null;

            // The synthesized transfer is center-to-center (departs the launch body's center, arrives at the
            // target's center), so its first/last stretches sit inside the launch / target SOI at the body
            // center - "below atmosphere", which the map suppresses and flickers. Render the transfer only
            // over the interplanetary span [transferRenderStartUT, transferRenderEndUT] (the SOI-exit /
            // SOI-entry UTs in recorded-span time); the recorded escape / capture legs cover inside the SOI,
            // and the ghost is hidden in the brief handoff gap. The caller passes the recorded departure /
            // arrival UTs when a clean SOI crossing was not found, degrading to the full (untrimmed) leg.
            double renderStart = double.IsNaN(transferRenderStartUT) ? recordedDepartureUT : transferRenderStartUT;
            double renderEnd = double.IsNaN(transferRenderEndUT) ? recordedArrivalUT : transferRenderEndUT;
            if (!(renderStart >= recordedDepartureUT && renderEnd <= recordedArrivalUT && renderStart < renderEnd))
            {
                renderStart = recordedDepartureUT;
                renderEnd = recordedArrivalUT;
            }

            var result = new List<OrbitSegment>(memberSegments.Count + 1);
            bool replaced = false;
            for (int i = 0; i < memberSegments.Count; i++)
            {
                OrbitSegment s = memberSegments[i];
                bool isHelioInWindow = !s.isPredicted && s.bodyName == commonAncestor
                    && s.startUT < recordedArrivalUT && s.endUT > recordedDepartureUT;
                if (isHelioInWindow)
                {
                    if (!replaced)
                    {
                        OrbitSegment transfer = transferSegment;
                        transfer.startUT = renderStart;
                        transfer.endUT = renderEnd;
                        transfer.bodyName = commonAncestor;
                        transfer.isPredicted = false;
                        result.Add(transfer);
                        replaced = true;
                    }
                    // else: a further heliocentric coast in the window collapses into the one arc.
                }
                else
                {
                    // Capture-leg re-time (parking path): the re-aimed transfer arrives EARLIER than the
                    // recorded arrival, so shift the recorded target-body capture leg(s) back to meet it. Only
                    // a non-predicted targetBody-bodied segment starting at/after the recorded arrival is
                    // moved; ShiftInTime moves startUT/endUT/epoch together (phase preserved). Gated on a
                    // non-zero shift + a non-null targetBody, so the direct path (0 / null) leaves it
                    // byte-identical. Mutually exclusive with the park-rephase below (park is commonAncestor,
                    // capture is targetBody).
                    if (captureRetimeShiftSeconds != 0.0 && !string.IsNullOrEmpty(targetBody)
                        && !s.isPredicted && s.bodyName == targetBody
                        && s.startUT >= recordedArrivalUT - 1.0)
                    {
                        result.Add(ShiftInTime(s, captureRetimeShiftSeconds));
                    }
                    // Heliocentric PARK re-phase: rotate ONLY the recorded common-ancestor coast(s)
                    // BEFORE the burn (the Sun-inertial park) into the live frame. The body-relative
                    // Kerbin/Duna legs (which already follow their live body) and predicted tails are
                    // never rotated. parkDeltaLonDeg == 0 (the direct-transfer / Increment-1-disabled
                    // path) leaves every segment byte-identical.
                    else if (parkDeltaLonDeg != 0.0 && !s.isPredicted && s.bodyName == commonAncestor
                        && s.endUT <= recordedDepartureUT + 1.0)
                    {
                        result.Add(RotateLanForParkRephase(s, parkDeltaLonDeg));
                    }
                    else
                    {
                        result.Add(s);
                    }
                }
            }

            if (!replaced)
                return null; // no heliocentric leg in this member -> faithful (body-relative legs follow their bodies)

            result.Sort((a, b) => a.startUT.CompareTo(b.startUT));
            // Coalesce same-orbit fragments the recorder split at recording-mode transitions, so a looped
            // member never lands its playback head in a too-short fragment (the decouple-seam flash). This
            // is the LEGACY/re-aim consumer's call; the new MapRender pipeline coalesces separately in
            // ChainAssembler (the two renderers do not share an upstream list). Loop-only + in-memory -
            // the recorded data is never touched. See TrajectoryMath.CoalesceSameOrbitFragments.
            return TrajectoryMath.CoalesceSameOrbitFragments(result);
        }
    }
}
