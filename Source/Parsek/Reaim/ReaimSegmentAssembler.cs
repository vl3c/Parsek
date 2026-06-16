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
        /// advanced between the RECORDED departure and this window's live departure
        /// <paramref name="departureUTForWindow"/> (= <c>schedule.DepartureUTForWindow(window)</c>, i.e.
        /// D_c). <c>Delta_lon(c) = omega_parent * (D_c - recordedDepartureUT)</c> where
        /// <c>omega_parent = 360 / launchBodyOrbitPeriodSeconds</c> (deg/s, the launch body's HELIOCENTRIC
        /// rate - NOT its rotation period). The recorded park sits at the recorded-epoch longitude; the
        /// re-aimed transfer is freshly synthesized at D_c (in the live frame), so the park must rotate by
        /// this same inertial angle to connect to it. Returns NaN on a degenerate period (&lt;= 0 / NaN /
        /// Inf) so the caller fails closed to faithful. Pure (no Unity). The result may exceed 360 (the
        /// LAN re-wrap in <see cref="RotateLanForParkRephase"/> reduces it).
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
        /// The inclination (degrees) of the recorded heliocentric PARK - the latest non-predicted
        /// <paramref name="commonAncestor"/>-bodied segment ending at/before
        /// <paramref name="recordedDepartureUT"/> (the coast BEFORE the trans-target burn). Returns NaN
        /// when the member has no such segment (e.g. a direct transfer with no park). The caller uses this
        /// for the near-equatorial fail-closed guard (<see cref="ParkRephaseMaxInclinationDeg"/>). Pure.
        /// </summary>
        internal static double FindHeliocentricParkInclination(
            IReadOnlyList<OrbitSegment> memberSegments, string commonAncestor, double recordedDepartureUT)
        {
            if (memberSegments == null || string.IsNullOrEmpty(commonAncestor))
                return double.NaN;
            double inc = double.NaN;
            double latestEnd = double.NegativeInfinity;
            for (int i = 0; i < memberSegments.Count; i++)
            {
                OrbitSegment s = memberSegments[i];
                if (!s.isPredicted && s.bodyName == commonAncestor
                    && s.endUT <= recordedDepartureUT + 1.0 && s.endUT >= latestEnd)
                {
                    latestEnd = s.endUT;
                    inc = s.inclination;
                }
            }
            return inc;
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
        /// </summary>
        internal static List<OrbitSegment> ReplaceHeliocentricLeg(
            IReadOnlyList<OrbitSegment> memberSegments, OrbitSegment transferSegment,
            string commonAncestor, double recordedDepartureUT, double recordedArrivalUT,
            double transferRenderStartUT, double transferRenderEndUT,
            double parkDeltaLonDeg = 0.0)
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
                    // Heliocentric PARK re-phase: rotate ONLY the recorded common-ancestor coast(s)
                    // BEFORE the burn (the Sun-inertial park) into the live frame. The body-relative
                    // Kerbin/Duna legs (which already follow their live body) and predicted tails are
                    // never rotated. parkDeltaLonDeg == 0 (the direct-transfer / Increment-1-disabled
                    // path) leaves every segment byte-identical.
                    if (parkDeltaLonDeg != 0.0 && !s.isPredicted && s.bodyName == commonAncestor
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

        /// <summary>
        /// Flag dispatcher for the re-aim whole-chain synthesis fix (reaim-fix-plan.md, option 3 / P1+P3).
        /// This is the PURE seam the <c>reaimChainSynthesis</c> feature flag gates so the flag-OFF
        /// byte-identical proof is runnable in xUnit (the live <c>BuildWindowSegments</c> is Unity-bound
        /// and cannot run headless).
        ///
        /// <para>When <paramref name="useChain"/> is <c>false</c> this returns EXACTLY
        /// <see cref="ReplaceHeliocentricLeg"/>'s output (the identity path: today's single-leg
        /// heliocentric replacement, byte-identical to the baseline). When <c>true</c> it calls the P3
        /// <see cref="ReplaceTransferChain"/> (escape + transfer + capture spliced contiguously into the
        /// recorded segments). <see cref="ReplaceTransferChain"/> is ALL-OR-NOTHING fail-closed: on any
        /// synthesis failure (missing legs, NaN spans, a UT-tiling fault, no heliocentric leg in the
        /// member) it returns null and THIS method falls back to <see cref="ReplaceHeliocentricLeg"/>'s
        /// result, so the chain path is never worse than today's baseline (reaim-fix-plan.md "Fallback
        /// chain"). When the chain inputs are absent (the legacy/placeholder call shape with no
        /// <paramref name="escapeSeg"/>/<paramref name="captureSeg"/>/spans), <see cref="ReplaceTransferChain"/>
        /// returns null and the ON path stays byte-identical to OFF - the same contract P1 shipped.</para>
        ///
        /// <para>Same purity contract as <see cref="ReplaceHeliocentricLeg"/>: operates on copied
        /// OrbitSegment value structs, never writes back to <paramref name="memberSegments"/> / the
        /// recording / the .prec. <paramref name="parkDeltaLonDeg"/> (#1167 live-frame park re-phase,
        /// default 0 = no rotation) is forwarded unchanged on BOTH branches so gating the flag never
        /// silently drops the re-phase.</para>
        /// </summary>
        internal static List<OrbitSegment> AssembleWindowChain(
            bool useChain,
            IReadOnlyList<OrbitSegment> memberSegments, OrbitSegment transferSegment,
            string commonAncestor, double recordedDepartureUT, double recordedArrivalUT,
            double transferRenderStartUT, double transferRenderEndUT,
            double parkDeltaLonDeg = 0.0,
            OrbitSegment? escapeSeg = null, OrbitSegment? captureSeg = null,
            string launchBody = null, string targetBody = null,
            double escapeRunStartUT = double.NaN, double escapeRunEndUT = double.NaN,
            double firstCaptureStartUT = double.NaN, double firstCaptureEndUT = double.NaN,
            bool escapeRunIsParkingOnly = false, bool captureSynthesizable = false)
        {
            if (useChain)
            {
                // P3 chain synthesis. ALL-OR-NOTHING: on any failure (null result) fall back to today's
                // heliocentric-only baseline - the chain is never worse than the single-honest-kink baseline.
                List<OrbitSegment> chain = ReplaceTransferChain(
                    memberSegments, escapeSeg, transferSegment, captureSeg,
                    commonAncestor, launchBody, targetBody,
                    recordedDepartureUT, recordedArrivalUT,
                    escapeRunStartUT, escapeRunEndUT,
                    firstCaptureStartUT, firstCaptureEndUT,
                    escapeRunIsParkingOnly, captureSynthesizable, parkDeltaLonDeg);
                if (chain != null && chain.Count > 0)
                    return chain;

                // Fall through to the heliocentric-only baseline (parkDeltaLonDeg forwarded).
                return ReplaceHeliocentricLeg(
                    memberSegments, transferSegment, commonAncestor,
                    recordedDepartureUT, recordedArrivalUT,
                    transferRenderStartUT, transferRenderEndUT, parkDeltaLonDeg);
            }

            // Identity path (flag OFF): byte-identical to today's baseline (parkDeltaLonDeg forwarded).
            return ReplaceHeliocentricLeg(
                memberSegments, transferSegment, commonAncestor,
                recordedDepartureUT, recordedArrivalUT,
                transferRenderStartUT, transferRenderEndUT, parkDeltaLonDeg);
        }

        /// <summary>
        /// P3 whole-chain synthesis (reaim-fix-plan.md STEP 3/4/5). Returns a copy of
        /// <paramref name="memberSegments"/> with the recorded ESCAPE run, heliocentric TRANSFER run, and
        /// FIRST CAPTURE leg REPLACED by the three pre-built synthesized legs (already shifted into
        /// recorded-span time by the live caller), spliced in with re-stamped CONTIGUOUS recorded-span UTs
        /// so the legs tile zero-gap into the surrounding verbatim recorded segments (circular parking,
        /// recorded heliocentric park, Ike thread, re-capture, descent, debris). The full-span render is
        /// then correct (each synth leg covers its own recorded span with real body-relative geometry), so
        /// there is NO center-vs-SOI gap to trim and the reverted launch-side trim is NOT reintroduced
        /// (guarantee 7).
        ///
        /// <para>FAIL-CLOSED, ALL-OR-NOTHING (returns null =&gt; the caller uses today's
        /// <see cref="ReplaceHeliocentricLeg"/> baseline):
        /// <list type="bullet">
        /// <item>no transfer leg could be selected (the member has no heliocentric leg in the window),</item>
        /// <item>any required recorded-span boundary UT is NaN / non-finite / mis-ordered,</item>
        /// <item>the synth transfer leg is the seam everything pins to and MUST be present (the caller's
        /// resolver only reaches here after the transfer synthesized).</item>
        /// </list></para>
        ///
        /// <para>CAPTURE-SIDE FAIL-CLOSED (reaim-fix-plan.md "Capture-side fail-closed"): when
        /// <paramref name="captureSynthesizable"/> is false (Ike-thread / secondary-SOI / atmospheric-direct
        /// arrival) OR <paramref name="captureSeg"/> is null, the ESCAPE + TRANSFER legs are synthesized and
        /// the recorded capture / arrival run is kept VERBATIM (byte-identical to the baseline arrival), so
        /// an Ike-threaded arrival never renders worse than today. The escape-side improvement still ships.</para>
        ///
        /// <para>ESCAPE-SIDE (reaim-fix-plan.md STEP 3): when <paramref name="escapeRunIsParkingOnly"/> is
        /// true (a direct SOI exit with no separately-recorded escape hyperbola) OR <paramref name="escapeSeg"/>
        /// is null, the escape run is kept VERBATIM (no escape leg synthesized) and only the transfer (and
        /// the capture, when synthesizable) are spliced. The recorded circular parking orbit is always kept
        /// verbatim; the escape leg is anchored at the recorded launch-body SOI-exit STATE (velocity-only,
        /// built from v1 by the live caller), so the parking-&gt;escape seam stays at the SOI edge.</para>
        ///
        /// <para>Pure (no Unity); the recorded escape/capture/parking structs are read-only inputs, written
        /// only into the returned copy. Sorts + coalesces exactly like <see cref="ReplaceHeliocentricLeg"/>;
        /// <paramref name="parkDeltaLonDeg"/> re-phases the recorded heliocentric PARK identically.</para>
        /// </summary>
        internal static List<OrbitSegment> ReplaceTransferChain(
            IReadOnlyList<OrbitSegment> memberSegments,
            OrbitSegment? escapeSeg, OrbitSegment transferSegment, OrbitSegment? captureSeg,
            string commonAncestor, string launchBody, string targetBody,
            double recordedDepartureUT, double recordedArrivalUT,
            double escapeRunStartUT, double escapeRunEndUT,
            double firstCaptureStartUT, double firstCaptureEndUT,
            bool escapeRunIsParkingOnly, bool captureSynthesizable,
            double parkDeltaLonDeg = 0.0)
        {
            if (memberSegments == null || string.IsNullOrEmpty(commonAncestor))
                return null;
            // The transfer window must be a sane, ordered recorded-span interval (the seam everything pins
            // to). NaN / non-finite / mis-ordered bounds => fail closed to the baseline.
            if (double.IsNaN(recordedDepartureUT) || double.IsInfinity(recordedDepartureUT)
                || double.IsNaN(recordedArrivalUT) || double.IsInfinity(recordedArrivalUT)
                || !(recordedDepartureUT < recordedArrivalUT))
                return null;

            // Decide which sides actually synthesize. The escape side is synthesized only when an escape
            // leg was built AND the launch-body predecessor of the heliocentric leg is a real escape
            // hyperbola (not the circular parking orbit) with a sane recorded-span run. The capture side is
            // synthesized only when CaptureSynthesizable (no Ike-thread / atmospheric-direct) AND a capture
            // leg was built AND its recorded-span first-capture span is sane.
            bool synthEscape = escapeSeg.HasValue && !escapeRunIsParkingOnly
                && !string.IsNullOrEmpty(launchBody)
                && IsFiniteOrderedSpan(escapeRunStartUT, escapeRunEndUT)
                // The escape run must end at the transfer-run start (a co-located handoff, within a generous
                // burn tolerance); otherwise a recorded park sits between escape and transfer and the escape
                // leg's end is not the SOI-exit -> keep escape verbatim (STEP 3 co-location gate).
                && System.Math.Abs(escapeRunEndUT - recordedDepartureUT) <= EscapeHandoffToleranceSeconds;

            bool synthCapture = captureSeg.HasValue && captureSynthesizable
                && !string.IsNullOrEmpty(targetBody)
                && IsFiniteOrderedSpan(firstCaptureStartUT, firstCaptureEndUT)
                // STEP 4: the synth capture's SOI-entry is pinned to recordedArrivalUT (the
                // heliocentric->capture boundary the loop clock compresses). The recorded first-capture
                // leg must begin at that boundary (within tolerance) or the pin would tear the tiling.
                && System.Math.Abs(firstCaptureStartUT - recordedArrivalUT) <= EscapeHandoffToleranceSeconds;

            var result = new List<OrbitSegment>(memberSegments.Count + 1);
            bool transferReplaced = false;
            bool escapeReplaced = false;
            bool captureReplaced = false;

            for (int i = 0; i < memberSegments.Count; i++)
            {
                OrbitSegment s = memberSegments[i];

                // (1) Heliocentric (Sun) leg in the transfer window -> the synth transfer. Further
                //     in-window heliocentric coasts collapse into the one arc.
                bool isHelioInWindow = !s.isPredicted && s.bodyName == commonAncestor
                    && s.startUT < recordedArrivalUT && s.endUT > recordedDepartureUT;
                if (isHelioInWindow)
                {
                    if (!transferReplaced)
                    {
                        OrbitSegment transfer = transferSegment;
                        transfer.startUT = recordedDepartureUT;
                        transfer.endUT = recordedArrivalUT;
                        transfer.bodyName = commonAncestor;
                        transfer.isPredicted = false;
                        result.Add(transfer);
                        transferReplaced = true;
                    }
                    continue;
                }

                // (2) Escape run (launch-body) -> the synth escape leg (only when synthesizing the escape
                //     side). Further escape-run hyperbolae collapse into the one leg.
                bool isEscapeRun = synthEscape && !s.isPredicted && s.bodyName == launchBody
                    && s.startUT < escapeRunEndUT && s.endUT > escapeRunStartUT;
                if (isEscapeRun)
                {
                    if (!escapeReplaced)
                    {
                        OrbitSegment escape = escapeSeg.Value;
                        // The caller phase-pinned the escape SOI-EXIT moment to RecordedDepartureUT (==
                        // escapeRunEndUT within tolerance) via ShiftInTime, so the epoch already places the
                        // SOI exit here; these stamp only the RENDER WINDOW over the recorded escape run.
                        escape.startUT = escapeRunStartUT;
                        escape.endUT = escapeRunEndUT;
                        escape.bodyName = launchBody;
                        escape.isPredicted = false;
                        result.Add(escape);
                        escapeReplaced = true;
                    }
                    continue;
                }

                // (3) First capture leg (target-body) -> the synth capture leg (only when synthesizing the
                //     capture side). Only the FIRST capture leg is replaced; everything after it (Ike,
                //     re-capture, descent) stays verbatim (it falls outside the first-capture span).
                bool isFirstCapture = synthCapture && !s.isPredicted && s.bodyName == targetBody
                    && s.startUT < firstCaptureEndUT && s.endUT > firstCaptureStartUT;
                if (isFirstCapture)
                {
                    if (!captureReplaced)
                    {
                        OrbitSegment capture = captureSeg.Value;
                        // STEP 4: the caller phase-pinned the capture SOI-ENTRY moment to RecordedArrivalUT
                        // (the compressed-clock arrival boundary) via ShiftInTime, so the epoch already
                        // places the SOI entry here; these stamp only the RENDER WINDOW over the recorded
                        // first-capture leg. (Earlier this force-overwrote startUT WITHOUT moving the epoch,
                        // a phase error along the leg - fixed in the resolver's capturePinShift.)
                        capture.startUT = recordedArrivalUT;
                        capture.endUT = firstCaptureEndUT;
                        capture.bodyName = targetBody;
                        capture.isPredicted = false;
                        result.Add(capture);
                        captureReplaced = true;
                    }
                    continue;
                }

                // (4) Everything else passes through verbatim. The recorded heliocentric PARK (a
                //     non-predicted common-ancestor coast ENDING at/before the burn) is re-phased into the
                //     live frame identically to ReplaceHeliocentricLeg; the body-relative legs and predicted
                //     tails are never rotated. parkDeltaLonDeg == 0 leaves every segment byte-identical.
                if (parkDeltaLonDeg != 0.0 && !s.isPredicted && s.bodyName == commonAncestor
                    && s.endUT <= recordedDepartureUT + 1.0)
                    result.Add(RotateLanForParkRephase(s, parkDeltaLonDeg));
                else
                    result.Add(s);
            }

            // The transfer leg is the seam everything pins to; if the member carried no heliocentric leg in
            // the window there is nothing to synthesize -> fail closed to the baseline (the heliocentric-only
            // ReplaceHeliocentricLeg returns null for the same member, so the caller stays faithful).
            if (!transferReplaced)
                return null;

            result.Sort((a, b) => a.startUT.CompareTo(b.startUT));
            // Coalesce the recorder's same-orbit fragments exactly as ReplaceHeliocentricLeg (loop-only,
            // in-memory; the recorded data is never touched). Do NOT modify CoalesceSameOrbitFragments.
            return TrajectoryMath.CoalesceSameOrbitFragments(result);
        }

        /// <summary>
        /// Tolerance (seconds) for the recorded-span handoffs the chain pins to: the escape run must END at
        /// the transfer-run START (the launch SOI-exit handoff) and the recorded first capture must BEGIN at
        /// the transfer-run END (the target SOI-entry the STEP 4 pin lands on). The recorder leaves small
        /// inter-segment sampling gaps at recording-mode / segment transitions (the Duna One topology shows
        /// sub-100s gaps), so a generous-but-bounded burn-scale tolerance keeps the co-location gate from
        /// false-failing on a sampling artifact while still rejecting a recorded PARK (hours/days) sitting
        /// between the escape and the transfer (which would move the seam off the SOI edge).
        /// </summary>
        internal const double EscapeHandoffToleranceSeconds = 3600.0;

        /// <summary>True when <paramref name="startUT"/> / <paramref name="endUT"/> are finite and ordered
        /// (start &lt; end). Pure helper for the chain-run span guards.</summary>
        internal static bool IsFiniteOrderedSpan(double startUT, double endUT)
        {
            if (double.IsNaN(startUT) || double.IsInfinity(startUT)
                || double.IsNaN(endUT) || double.IsInfinity(endUT))
                return false;
            return startUT < endUT;
        }
    }
}
