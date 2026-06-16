using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Reaim
{
    // Classifies a recorded mission's on-rails SOI-segment chain into the re-aim phase model
    // (docs/dev/plans/reaim-interplanetary-transfers.md, Phase 2). PURE: operates on the recording's
    // OrbitSegments + the IBodyInfo seam (reuses the salvaged AncestorChain / TryFindCommonAncestor),
    // so it is fully unit-testable with synthetic segment chains.
    //
    // Per the design review: re-aim engages ONLY for a cross-parent, SINGLE-HOP mission that actually
    // recorded its heliocentric coast as a common-ancestor-bodied OrbitSegment (on-rails, i.e. the
    // player time-warped through it). A same-parent target, a missing heliocentric leg (never warped /
    // background coast), a deep chain (target not a direct child of the common ancestor, e.g. Ike via
    // Duna), or a multi-hop chain are all NOT supported in v1 -> the classifier returns
    // Supported=false and the mission stays on the existing faithful path (never half-applied).

    internal struct ReaimMissionPlan
    {
        public bool Supported;
        public string Reason;            // why unsupported (diagnostic), or null when Supported

        public string LaunchBody;        // earliest recorded body
        public string TargetBody;        // the cross-parent destination (first body after S2)
        public string CommonAncestor;    // the heliocentric-leg body (LCA of launch + target)

        public OrbitSegment ParkingOrbit;     // S0 tail: last launch-body orbit before the heliocentric leg
        public OrbitSegment HeliocentricLeg;  // S2: the common-ancestor-bodied transfer coast
        public OrbitSegment ArrivalLeg;       // S3: first target-body orbit after S2

        public double RecordedDepartureUT;     // transfer-run start: the launch-body SOI exit for a direct
                                               // transfer, or the trans-target burn for a heliocentric park
        public double RecordedArrivalUT;       // S2 end   = target SOI entry
        public double RecordedTransferTofSeconds; // S2 duration (a nudge seed; the window solves its own tof)

        // True when the transfer departs not from the launch-body SOI exit but from a HELIOCENTRIC
        // PARKING orbit (a two-burn departure: escape into a near-circular solar orbit co-orbital with
        // the launch body, coast >= 1 full heliocentric revolution, then burn for the target). The
        // transfer fields above are unchanged (the transfer leg, not the park, is re-aimed); this flag
        // is purely diagnostic so the ReaimDiag log shows WHY a Sun-predecessor mission engaged.
        public bool DepartedFromHeliocentricPark;

        // ---- Chain-synthesis index spans (reaim-fix-plan.md P2, BLOCKER 1) ----
        // The recorded transfer member is NOT a clean 3-leg [parking, helio, arrival] shape: the real
        // Duna One is a 14+-segment chain with a recorded Kerbin ESCAPE HYPERBOLA, THREE Sun coasts, a
        // Duna CAPTURE HYPERBOLA, an Ike thread, and a descent. Chain synthesis (P3) must replace the
        // escape RUN, the transfer RUN, and the FIRST capture leg by INDEX (never the misnamed
        // ParkingOrbit field, the post-capture parking, the Ike segs, the descent, or chained debris).
        // These indices are into the SORTED non-predicted segment list the classifier walked.

        // The launch-body segment(s) between the circular parking orbit and the heliocentric leg - the
        // ESCAPE RUN. [EscapeRunStartIndex, EscapeRunEndIndex] inclusive. When the launch-body predecessor
        // of the heliocentric leg is the circular parking orbit itself (a direct SOI-exit with no recorded
        // escape hyperbola), the run is a single index (== ParkingIndex) and EscapeRunIsParkingOnly is
        // true (no separate escape leg to synthesize). -1 when not applicable.
        public int EscapeRunStartIndex;
        public int EscapeRunEndIndex;
        public bool EscapeRunIsParkingOnly;   // the launch-body predecessor IS the circular parking orbit (no escape hyperbola)

        // The circular-parking-orbit index (the last launch-body orbit before the escape run), kept
        // VERBATIM. == ParkingIndex below when the escape run is a separate hyperbola; == EscapeRunStartIndex
        // when EscapeRunIsParkingOnly. -1 when not applicable.
        public int ParkingIndex;

        // The common-ancestor (Sun) transfer RUN: [TransferRunStartIndex, TransferRunEndIndex] inclusive
        // (transferStartIdx..lastCoastIdx). All collapse into the one re-aimed arc. -1 when not applicable.
        public int TransferRunStartIndex;
        public int TransferRunEndIndex;

        // ---- Recorded-span UT spans of the chain runs (reaim-fix-plan.md P3/STEP 5) ----
        // The pure assembler (ReaimSegmentAssembler.ReplaceTransferChain) selects the escape / transfer /
        // capture runs out of the member's OWN recorded segment list by recorded-span UT span, NOT by the
        // classifier's segment INDICES: the classifier filters predicted segments and SORTS, so its indices
        // do not align with the caller's raw memberSegments (which can carry predicted tails / a different
        // order). These are the recorded-span boundary UTs the assembler tiles the synth legs against:
        //   - escape run  [EscapeRunStartUT, EscapeRunEndUT]  (launch-body hyperbola run; the synth escape
        //     replaces this span; == ParkingIndex's own span when EscapeRunIsParkingOnly, in which case the
        //     escape side is NOT synthesized).
        //   - transfer run [RecordedDepartureUT, TransferRunEndUT]  (the Sun coasts; the synth transfer
        //     replaces them; RecordedDepartureUT is the run start, == HeliocentricLeg.startUT).
        //   - first capture [FirstCaptureStartUT, FirstCaptureEndUT]  (the first target hyperbola).
        // NaN when not applicable (an Unsupported plan).
        public double EscapeRunStartUT;
        public double EscapeRunEndUT;
        public double TransferRunEndUT;

        // The FIRST target-bodied capture leg (the first Duna hyperbola), kept its own index. The only
        // capture leg chain synthesis replaces (when CaptureSynthesizable); everything after it (Ike,
        // re-capture, descent) stays verbatim. -1 when not applicable.
        public int FirstCaptureIndex;

        // The target SOI-entry UT (== ArrivalLeg.startUT == RecordedArrivalUT) the synth capture leg's
        // SOI-entry is pinned to (in recorded-span time). Convenience copy for the assembler.
        public double FirstCaptureStartUT;
        public double FirstCaptureEndUT;

        // FALSE when the capture side must FAIL CLOSED (reaim-fix-plan.md CRITICAL SCOPE CORRECTION #3/#4):
        // a SECONDARY-body SOI segment (e.g. Ike) threads between the heliocentric leg and the destination
        // capture, OR the arrival is atmospheric-direct (the first target-bodied leg is an elliptic
        // descending arc, no capture hyperbola to synthesize). The Duna One real topology threads Ike, so
        // CaptureSynthesizable = false there: chain synthesis synthesizes escape + transfer only and keeps
        // the recorded capture/arrival run VERBATIM. CaptureSynthesizableReason records why (diagnostic).
        public bool CaptureSynthesizable;
        public string CaptureSynthesizableReason;

        internal static ReaimMissionPlan Unsupported(string launchBody, string reason)
        {
            return new ReaimMissionPlan
            {
                Supported = false, LaunchBody = launchBody, Reason = reason,
                EscapeRunStartIndex = -1, EscapeRunEndIndex = -1, ParkingIndex = -1,
                TransferRunStartIndex = -1, TransferRunEndIndex = -1, FirstCaptureIndex = -1,
                EscapeRunStartUT = double.NaN, EscapeRunEndUT = double.NaN, TransferRunEndUT = double.NaN,
                FirstCaptureStartUT = double.NaN, FirstCaptureEndUT = double.NaN,
            };
        }
    }

    internal static class ReaimClassifier
    {
        /// <summary>
        /// Classifies a recording's non-predicted OrbitSegments into the re-aim phase model. Pure.
        /// Returns Supported=true only for a cross-parent single-hop mission with a recorded
        /// heliocentric (common-ancestor) leg, a parking orbit, and a direct-child arrival; every
        /// other shape returns Supported=false with a reason (caller keeps the faithful path).
        /// </summary>
        internal static ReaimMissionPlan Classify(IReadOnlyList<OrbitSegment> orbitSegments, IBodyInfo bodyInfo)
        {
            if (orbitSegments == null || bodyInfo == null)
                return ReaimMissionPlan.Unsupported(null, "no segments / body info");

            // Non-predicted segments in recorded-UT order (predicted ballistic tails are not real legs).
            var segs = new List<OrbitSegment>();
            for (int i = 0; i < orbitSegments.Count; i++)
            {
                OrbitSegment s = orbitSegments[i];
                if (!s.isPredicted && !string.IsNullOrEmpty(s.bodyName))
                    segs.Add(s);
            }
            if (segs.Count == 0)
                return ReaimMissionPlan.Unsupported(null, "no usable orbit segments");
            segs.Sort((a, b) => a.startUT.CompareTo(b.startUT));

            string launchBody = segs[0].bodyName;

            // The heliocentric leg is the first segment whose body is a STRICT ancestor of the launch
            // body (e.g. the Sun for Kerbin). Its presence is what proves the player time-warped
            // through the coast (on-rails capture); without it re-aim cannot engage (review C1/A/m4).
            var launchAncestors = new HashSet<string>(MissionPeriodicity.AncestorChain(launchBody, bodyInfo));
            launchAncestors.Remove(launchBody); // strict ancestors only
            int helioIdx = -1;
            for (int i = 0; i < segs.Count; i++)
            {
                if (launchAncestors.Contains(segs[i].bodyName))
                {
                    helioIdx = i;
                    break;
                }
            }
            if (helioIdx < 0)
                return ReaimMissionPlan.Unsupported(launchBody,
                    "no heliocentric (common-ancestor) leg recorded - never warped through the coast, or background; staying faithful");

            string commonAncestor = segs[helioIdx].bodyName;

            // Parking orbit = the last launch-body orbit before the heliocentric leg (the S0 tail the
            // synthesized ejection departs from).
            int parkingIdx = -1;
            for (int i = helioIdx - 1; i >= 0; i--)
            {
                if (segs[i].bodyName == launchBody)
                {
                    parkingIdx = i;
                    break;
                }
            }
            // Belt-and-suspenders: segs[0] is the launch body and helioIdx is a STRICT ancestor (never
            // index 0), so a launch-body segment always precedes it and this never fires - kept as a
            // cheap defensive guard.
            if (parkingIdx < 0)
                return ReaimMissionPlan.Unsupported(launchBody,
                    "no launch-body parking orbit before the heliocentric leg");

            // Arrival leg = the first segment after the heliocentric leg whose body is neither the
            // common ancestor nor the launch body -> the target.
            int arrivalIdx = -1;
            for (int i = helioIdx + 1; i < segs.Count; i++)
            {
                string b = segs[i].bodyName;
                if (b != commonAncestor && b != launchBody)
                {
                    arrivalIdx = i;
                    break;
                }
            }
            if (arrivalIdx < 0)
                return ReaimMissionPlan.Unsupported(launchBody,
                    "no target arrival leg after the heliocentric coast");

            string targetBody = segs[arrivalIdx].bodyName;

            // Single-hop guard: the target must be a DIRECT child of the common ancestor (target's
            // parent == the heliocentric-leg body). A deeper target (Ike via Duna: parent Duna, not
            // the Sun) is multi-hop and deferred.
            string targetParent = bodyInfo.ReferenceBodyName(targetBody);
            if (targetParent != commonAncestor)
                return ReaimMissionPlan.Unsupported(launchBody,
                    $"target '{targetBody}' is not a direct child of the common ancestor '{commonAncestor}' (deep/multi-hop, deferred)");

            // Cross-parent confirmation (the same LCA test): launch + target's LCA must be the
            // heliocentric body, and the launch body must NOT be that ancestor (else it is same-parent
            // and stays on the faithful path).
            if (!MissionPeriodicity.TryFindCommonAncestor(launchBody, targetBody, bodyInfo,
                    out string lca, out List<string> launchToAnc, out _)
                || lca != commonAncestor || launchToAnc.Count == 0)
                return ReaimMissionPlan.Unsupported(launchBody,
                    $"'{targetBody}' is not cross-parent of launch body '{launchBody}' via '{commonAncestor}'");

            // Multi-hop guard: no SECOND heliocentric leg after the arrival (a single transfer only).
            for (int i = arrivalIdx + 1; i < segs.Count; i++)
            {
                if (launchAncestors.Contains(segs[i].bodyName))
                    return ReaimMissionPlan.Unsupported(launchBody,
                        "more than one heliocentric leg (multi-hop / gravity assist) - deferred");
            }

            OrbitSegment arrival = segs[arrivalIdx];

            // TRANSFER RUN (docs/dev/plans/reaim-loiter-compression.md section 3.2). The transfer is the
            // run of common-ancestor (Sun) segments on the SAME orbit ENDING at the target SOI entry.
            // RecordedDepartureUT = the start of the EARLIEST such segment (NOT the first Sun segment in
            // the mission -- a heliocentric loiter before the transfer would feed a too-long tof to the
            // Lambert solve). The gathered list is flattened across ALL members (review C1), so:
            //   1. Anchor on the SOI boundary by UT, not list index: the transfer's LAST coast is the Sun
            //      segment whose endUT is contiguous with arrival.startUT (an interleaved debris segment
            //      from another member does NOT end at the arrival, so it cannot masquerade as it).
            //   2. Walk back from that coast including Sun segments whose a matches the FIRST coast's a
            //      (MCC coasts are ~the same orbit; the a-step is the discriminator, anchored to the run
            //      start so gradual drift still ends the run) AND that CHAIN in UT (within a generous
            //      burn tolerance) -- so a same-a debris coast that does not chain to the transfer is
            //      excluded.
            //   3. Decline (faithful) when the run spans more than ~1 revolution (a heliocentric loiter
            //      got absorbed, review M1) or when a chaining Sun segment precedes it (a heliocentric
            //      PARKING departure -- Lambert assumes r1 = launch body, deferred).
            const double AStepRelThreshold = ReaimLoiterCompressor.DefaultAStepRelThreshold;
            const double SameOrbitRelThreshold = ReaimLoiterCompressor.DefaultSameOrbitRelThreshold;
            const double SoiBoundaryEps = 1.0;     // seconds; the SOI-entry boundary is a segment edge
            const double BurnChainTolerance = 3600.0; // generous off-rails (MCC burn) gap between coasts

            int lastCoastIdx = -1;
            for (int i = 0; i < segs.Count; i++)
            {
                if (segs[i].bodyName == commonAncestor
                    && Math.Abs(segs[i].endUT - arrival.startUT) <= SoiBoundaryEps)
                    lastCoastIdx = i; // last contiguous Sun coast handing off to the target SOI entry
            }
            if (lastCoastIdx < 0)
                return ReaimMissionPlan.Unsupported(launchBody,
                    "no transfer coast contiguous with the target SOI entry");

            double firstA = segs[lastCoastIdx].semiMajorAxis;
            int transferStartIdx = lastCoastIdx;
            double runStartUT = segs[lastCoastIdx].startUT;
            for (int i = lastCoastIdx - 1; i >= 0; i--)
            {
                OrbitSegment s = segs[i];
                if (s.bodyName != commonAncestor)
                    break;                                   // hit the launch-body parking -> run ends
                double aRel = Math.Abs(s.semiMajorAxis - firstA) / Math.Max(1.0, Math.Abs(firstA));
                if (aRel > AStepRelThreshold)
                    break;                                   // a maneuver (transfer burn) -> run ends
                // A gap larger than the burn tolerance ends the run UNLESS it is the SAME orbit (sma
                // matches to the tight sameOrbit tolerance): the optimizer splits one ballistic transfer
                // coast into warp-boundary checkpoint sections, leaving gaps between same-sma segments;
                // those stay in the run so a long-warped transfer still measures its full tof. A real
                // mid-course correction shifts sma past the tolerance, so it still ends the run.
                if (runStartUT - s.endUT > BurnChainTolerance && aRel > SameOrbitRelThreshold)
                    break;                                   // a real gap to a different orbit -> ends
                transferStartIdx = i;
                runStartUT = s.startUT;
            }

            OrbitSegment transferStart = segs[transferStartIdx];
            OrbitSegment lastCoast = segs[lastCoastIdx];

            // Decline a run that spans more than ~1 revolution: a clean transfer is a single partial pass
            // (duration < period), so a longer run means a heliocentric loiter was absorbed (review M1).
            // NOTE: a heliocentric PARKING departure (handled by the exception below) sits on a DIFFERENT
            // solar orbit than the transfer, so the a-step ends the transfer run before reaching it - the
            // park is never absorbed here, and transferStart stays the trans-target burn (tof excludes it).
            double tRep = ReaimLoiterCompressor.OrbitalPeriod(firstA, bodyInfo.GravParameter(commonAncestor));
            if (!double.IsNaN(tRep) && tRep > 0.0
                && (lastCoast.endUT - transferStart.startUT) > 1.5 * tRep)
                return ReaimMissionPlan.Unsupported(launchBody,
                    "transfer run spans >1 revolution (heliocentric loiter absorbed); staying faithful");

            // Partial-transfer departure: a DIFFERENT-orbit common-ancestor segment immediately before
            // the transfer run -> the transfer did not depart from the launch-body SOI exit but from an
            // earlier heliocentric state (a solar parking orbit, OR a mid-course correction that the
            // optimizer split at the engine-firing boundary). Either way r1 != launch body, so the Lambert
            // re-aim would mis-aim; decline to faithful. Gap-INDEPENDENT: a slow MCC (a long coast then a
            // burn) leaves a wide gap to the prior coast but is still a partial transfer. Same-orbit prior
            // coasts were already merged into the run above, so a remaining common-ancestor predecessor is
            // necessarily a different orbit (a real maneuver / parking), never a sampling gap.
            //
            // EXCEPTION (s15 Kerbal X #2): a HELIOCENTRIC PARKING departure is re-aimable. When the Sun
            // predecessor is a near-circular solar loiter CO-ORBITAL with the launch body (the player
            // escaped into ~the launch body's own solar orbit, phased there >= 1 full revolution, then
            // burned for the target), r1 = launchBody stays a valid Lambert departure approximation and the
            // park is a closed loiter the existing whole-period cut re-times. IsHeliocentricParkingDeparture
            // engages ONLY that admissible, empty-cut shape; a sub-period MCC, a multi-rev park, or a
            // wide/eccentric solar park all return false and stay declined -> faithful (fail-closed).
            bool sunPredecessor = transferStartIdx - 1 >= 0
                && segs[transferStartIdx - 1].bodyName == commonAncestor;
            bool parkingDeparture = sunPredecessor
                && IsHeliocentricParkingDeparture(segs, transferStartIdx, launchBody, commonAncestor, bodyInfo);
            if (sunPredecessor && !parkingDeparture)
                return ReaimMissionPlan.Unsupported(launchBody,
                    "transfer departs from a heliocentric parking orbit or mid-course correction (deferred); staying faithful");

            // ESCAPE RUN (reaim-fix-plan.md P2/STEP 2/STEP 3, BLOCKER 1). The launch-body segment(s)
            // immediately before the heliocentric leg are the recorded escape (a real Kerbin HYPERBOLA in
            // the Duna One case, seg#8) - the leg chain synthesis replaces by INDEX, not the misnamed
            // ParkingOrbit field. The discriminator between the escape leg and the circular PARKING orbit is
            // the conic shape: the parking orbit is a BOUND (elliptic, ecc < 1, sma > 0) launch-body orbit,
            // the escape leg is HYPERBOLIC (ecc >= 1 / sma < 0). Walk back from helioIdx-1 over CONTIGUOUS
            // launch-body HYPERBOLAE; the first BOUND launch-body orbit hit is the parking orbit (kept
            // verbatim, never synthesized). When the launch-body predecessor of the heliocentric leg is
            // itself BOUND (a direct SOI exit with no separately-recorded escape hyperbola), the escape run
            // is that single parking segment and there is nothing to synthesize on the escape side
            // (EscapeRunIsParkingOnly).
            int escapeRunEndIdx = helioIdx - 1;             // last launch-body seg before helio
            bool lastLaunchBodyIsHyperbolic = IsHyperbolicConic(segs[escapeRunEndIdx]);
            int escapeRunStartIdx;
            int parkingOrbitIdx;
            bool escapeRunIsParkingOnly;
            if (!lastLaunchBodyIsHyperbolic)
            {
                // Direct SOI exit: the launch-body predecessor of helio IS the bound circular parking orbit.
                // The escape run is that single segment; no escape hyperbola to synthesize.
                escapeRunStartIdx = escapeRunEndIdx;
                parkingOrbitIdx = escapeRunEndIdx;
                escapeRunIsParkingOnly = true;
            }
            else
            {
                // Walk back over contiguous launch-body HYPERBOLAE; stop at the first non-launch-body seg or
                // the first BOUND launch-body orbit (the circular parking).
                escapeRunStartIdx = escapeRunEndIdx;
                parkingOrbitIdx = -1;
                for (int i = escapeRunEndIdx - 1; i >= 0; i--)
                {
                    if (segs[i].bodyName != launchBody)
                        break;                              // hit a non-launch-body seg -> run ends
                    if (!IsHyperbolicConic(segs[i]))
                    {
                        parkingOrbitIdx = i;                // the bound circular parking orbit
                        break;
                    }
                    escapeRunStartIdx = i;                  // another launch-body escape hyperbola segment
                }
                // If no bound parking orbit was found before the escape run (recording started already on the
                // escape hyperbola), fall back to the escape-run start as the parking anchor.
                if (parkingOrbitIdx < 0)
                    parkingOrbitIdx = escapeRunStartIdx;
                escapeRunIsParkingOnly = false;
            }

            // CAPTURE-SIDE FAIL-CLOSED detection (reaim-fix-plan.md CRITICAL SCOPE CORRECTION #3/#4).
            bool captureSynthesizable = ClassifyCaptureSynthesizable(
                segs, arrivalIdx, targetBody, commonAncestor, launchBody, bodyInfo,
                out string captureReason);

            return new ReaimMissionPlan
            {
                Supported = true,
                Reason = null,
                LaunchBody = launchBody,
                TargetBody = targetBody,
                CommonAncestor = commonAncestor,
                ParkingOrbit = segs[parkingIdx],
                HeliocentricLeg = transferStart,
                ArrivalLeg = arrival,
                RecordedDepartureUT = transferStart.startUT,            // transfer-run start (SOI exit, or the heliocentric-park burn)
                RecordedArrivalUT = arrival.startUT,                    // target SOI entry
                RecordedTransferTofSeconds = arrival.startUT - transferStart.startUT,
                DepartedFromHeliocentricPark = parkingDeparture,

                // Chain-synthesis index spans (into the sorted non-predicted segs list).
                EscapeRunStartIndex = escapeRunStartIdx,
                EscapeRunEndIndex = escapeRunEndIdx,
                EscapeRunIsParkingOnly = escapeRunIsParkingOnly,
                ParkingIndex = parkingOrbitIdx,
                TransferRunStartIndex = transferStartIdx,
                TransferRunEndIndex = lastCoastIdx,
                EscapeRunStartUT = segs[escapeRunStartIdx].startUT,
                EscapeRunEndUT = segs[escapeRunEndIdx].endUT,
                TransferRunEndUT = lastCoast.endUT,
                FirstCaptureIndex = arrivalIdx,
                FirstCaptureStartUT = arrival.startUT,
                FirstCaptureEndUT = arrival.endUT,
                CaptureSynthesizable = captureSynthesizable,
                CaptureSynthesizableReason = captureReason,
            };
        }

        /// <summary>
        /// True when an OrbitSegment's conic is HYPERBOLIC (an escape / capture leg) rather than BOUND
        /// (a circular / elliptic parking orbit). A KSP hyperbola has sma &lt; 0 (and ecc &gt;= 1); a bound
        /// orbit has sma &gt; 0 and ecc &lt; 1. Used to separate the escape hyperbola from the circular
        /// parking orbit. Pure. NaN sma/ecc reads as NOT hyperbolic (fail safe toward treating it as a
        /// bound parking boundary).
        /// </summary>
        internal static bool IsHyperbolicConic(OrbitSegment seg)
        {
            if (double.IsNaN(seg.semiMajorAxis) || double.IsNaN(seg.eccentricity))
                return false;
            return seg.semiMajorAxis < 0.0 || seg.eccentricity >= 1.0;
        }

        /// <summary>
        /// Decides whether the FIRST target-bodied capture leg (<paramref name="arrivalIdx"/>) can be
        /// synthesized, or whether the capture side must FAIL CLOSED to the recorded capture verbatim
        /// (reaim-fix-plan.md CRITICAL SCOPE CORRECTION #3/#4). Returns false when:
        /// <list type="bullet">
        /// <item>ATMOSPHERIC-DIRECT: the first target-bodied leg is already an elliptic DESCENDING arc
        /// (sma &gt; 0, ecc &lt; 1) rather than a capture hyperbola - there is no capture hyperbola to
        /// synthesize.</item>
        /// <item>SECONDARY-SOI THREAD (e.g. Ike): a segment in the arrival window after the first capture
        /// is bodied as a DIFFERENT body that is neither the target nor the common ancestor nor the launch
        /// body (a secondary moon SOI the arrival threads). The Duna One real topology threads Ike, so this
        /// fires and the capture side fails closed.</item>
        /// </list>
        /// Pure (scans the segment bodies + the first capture's conic sign). <paramref name="reason"/>
        /// records the decision for the diagnostic log.
        /// </summary>
        internal static bool ClassifyCaptureSynthesizable(
            IReadOnlyList<OrbitSegment> segs, int arrivalIdx, string targetBody,
            string commonAncestor, string launchBody, IBodyInfo bodyInfo, out string reason)
        {
            reason = null;
            if (segs == null || arrivalIdx < 0 || arrivalIdx >= segs.Count)
            {
                reason = "no first-capture leg";
                return false;
            }

            // ATMOSPHERIC-DIRECT: the first target leg is an elliptic descending arc, not a capture
            // hyperbola (a hyperbola is sma < 0 / ecc >= 1). No capture hyperbola exists to synthesize.
            OrbitSegment firstCapture = segs[arrivalIdx];
            bool isHyperbola = firstCapture.semiMajorAxis < 0.0 || firstCapture.eccentricity >= 1.0;
            if (!isHyperbola)
            {
                reason = $"atmospheric-direct arrival (first '{targetBody}' leg is elliptic ecc=" +
                         $"{firstCapture.eccentricity.ToString("F3", CultureInfo.InvariantCulture)} " +
                         $"sma={firstCapture.semiMajorAxis.ToString("R", CultureInfo.InvariantCulture)}); capture verbatim";
                return false;
            }

            // SECONDARY-SOI THREAD: a body other than the target / common ancestor / launch body appears
            // after the first capture (e.g. Ike between two Duna captures). Scan forward from the segment
            // after the first capture; stop at the next heliocentric leg (none here - the multi-hop guard
            // already rejected those) or the end of the member.
            for (int i = arrivalIdx + 1; i < segs.Count; i++)
            {
                string b = segs[i].bodyName;
                if (string.IsNullOrEmpty(b))
                    continue;
                if (b != targetBody && b != commonAncestor && b != launchBody)
                {
                    reason = $"secondary-SOI thread ('{b}' between '{targetBody}' captures, e.g. Ike via Duna); capture verbatim";
                    return false;
                }
            }

            reason = "clean single-capture arrival";
            return true;
        }

        // A heliocentric phasing park is admissible for re-aim only when it is near-circular (ecc <=
        // this) AND co-orbital with the launch body (its sma within this relative tolerance of the launch
        // body's OWN heliocentric sma), so r1 = launchBody stays a valid Lambert departure approximation
        // (the burn point sits near the launch body). s15 Kerbal X #2: park ecc 0.0327, sma 3.5% off
        // Kerbin's -> admissible; a wide or eccentric solar park -> declined (fail-closed to faithful).
        internal const double ParkDepartureEccMax = 0.1;
        internal const double ParkDepartureSmaRelTolerance = 0.1;

        /// <summary>
        /// True when the common-ancestor segment immediately before the transfer is an admissible
        /// HELIOCENTRIC PARKING departure (a two-burn departure), as opposed to a sub-period mid-course
        /// correction or a wide/eccentric solar parking orbit. Admissible means: EVERY common-ancestor
        /// (solar) loiter run keeps EXACTLY <c>DefaultKeepRevs</c> whole revolutions (so no heliocentric
        /// loiter cut fires - a multi-rev solar park, even a non-adjacent one, is deferred), AND the
        /// departure orbit at the burn point is near-circular AND co-orbital with the launch body. Pure
        /// (ecc + sma form; no live position) and fail-closed: any degenerate / NaN / unknown-mu input
        /// returns false. Reuses <see cref="ReaimLoiterCompressor.DetectRuns"/> so the engage decision
        /// matches exactly what the compressor will (not) cut.
        /// </summary>
        internal static bool IsHeliocentricParkingDeparture(
            IReadOnlyList<OrbitSegment> segs, int transferStartIdx,
            string launchBody, string commonAncestor, IBodyInfo bodyInfo,
            double parkEccMax = ParkDepartureEccMax,
            double parkSmaRelTolerance = ParkDepartureSmaRelTolerance)
        {
            if (segs == null || bodyInfo == null
                || transferStartIdx <= 0 || transferStartIdx > segs.Count - 1)
                return false;
            OrbitSegment lastPark = segs[transferStartIdx - 1];
            if (lastPark.bodyName != commonAncestor)
                return false;

            // Identify the park run with the SAME detector ComputeCuts uses (so the empty-cut decision is
            // exactly the cut the compressor will make). The run we want is the common-ancestor loiter run
            // whose end coincides with the transfer's predecessor segment.
            List<ReaimLoiterCompressor.LoiterRun> runs =
                ReaimLoiterCompressor.DetectRuns(segs, bodyInfo.GravParameter);
            ReaimLoiterCompressor.LoiterRun park = default;
            bool found = false;
            for (int i = 0; i < runs.Count; i++)
            {
                if (runs[i].BodyName == commonAncestor
                    && Math.Abs(runs[i].EndUT - lastPark.endUT)
                        <= ReaimLoiterCompressor.DefaultContiguityEpsilonSeconds)
                {
                    park = runs[i];
                    found = true;
                    break;
                }
            }
            if (!found)
                return false; // the Sun predecessor is a sub-period (< 1 rev) MCC arc, not a closed park

            // Empty-cut scope (Open Q3 decision, 2026-06-15): NO heliocentric (common-ancestor) park may
            // exceed DefaultKeepRevs. `ComputeCuts` (called downstream on the WHOLE member, not just the
            // predecessor run) excises whole periods from ANY run with wholeRevs > keepRevs, so a multi-rev
            // SOLAR park - even one earlier than the transfer's immediate predecessor - would fire the
            // cutBeforeDeparture composition, which is unvalidated on a heliocentric run; deferred to a
            // follow-up -> decline. (A launch-body loiter cut is the existing validated L2 trim and is NOT
            // gated here.) Scanning EVERY common-ancestor run, not just `park`, makes this engage decision
            // match exactly what the compressor will (not) cut, so engaging adds no park-driven anchor shift.
            for (int i = 0; i < runs.Count; i++)
                if (runs[i].BodyName == commonAncestor
                    && runs[i].WholeRevs > ReaimLoiterCompressor.DefaultKeepRevs)
                    return false;

            // Admissibility gate (keeps r1 = launchBody a valid Lambert departure): the park must be
            // near-circular AND co-orbital with the launch body at the BURN POINT (the departure-orbit
            // state r1 approximates). `DetectRuns` merges segments on sma only (not ecc), so check ecc at
            // BOTH the run anchor and the burn segment (`lastPark` = segs[transferStartIdx-1]), and check
            // the co-orbital sma at the burn segment (the actual departure orbit). NOTE: sma + ecc bound
            // the departure ORBIT, not the vessel's true-anomaly PHASE on it - a co-orbital park 180 deg
            // out of phase would pass both gates yet burn far from the launch body. That is an accepted
            // limitation of the pure (no-live-position) form: it FAILS CLOSED (a mis-aimed r1 reproduces
            // the prior faithful render, never new corruption), the downstream encounter check can still
            // reject a transfer that misses the target, and a real two-burn departure phases to end near
            // the launch body at the burn. A future fixture exposing the phase gap can add a position check.
            OrbitSegment anchor = lastPark;
            for (int i = 0; i < segs.Count; i++)
            {
                if (segs[i].bodyName == commonAncestor
                    && Math.Abs(segs[i].startUT - park.StartUT)
                        <= ReaimLoiterCompressor.DefaultContiguityEpsilonSeconds)
                {
                    anchor = segs[i];
                    break;
                }
            }
            if (Math.Abs(anchor.eccentricity) > parkEccMax
                || Math.Abs(lastPark.eccentricity) > parkEccMax)
                return false;
            double launchHelioSma = HeliocentricSemiMajorAxis(launchBody, commonAncestor, bodyInfo);
            if (double.IsNaN(launchHelioSma) || launchHelioSma <= 0.0)
                return false; // cannot establish the launch body's orbit -> fail closed
            double smaRel = Math.Abs(lastPark.semiMajorAxis - launchHelioSma) / launchHelioSma;
            return smaRel <= parkSmaRelTolerance;
        }

        /// <summary>
        /// Semi-major axis (metres) of <paramref name="launchBody"/>'s orbit about
        /// <paramref name="commonAncestor"/>, derived from its orbit period via Kepler's third law
        /// <c>a = cbrt(mu * (T / 2*pi)^2)</c>. Pure; NaN on a degenerate period / unknown mu so the
        /// admissibility gate fails closed.
        /// </summary>
        internal static double HeliocentricSemiMajorAxis(
            string launchBody, string commonAncestor, IBodyInfo bodyInfo)
        {
            double t = bodyInfo.OrbitPeriod(launchBody);
            double mu = bodyInfo.GravParameter(commonAncestor);
            if (double.IsNaN(t) || double.IsInfinity(t) || t <= 0.0
                || double.IsNaN(mu) || double.IsInfinity(mu) || mu <= 0.0)
                return double.NaN;
            double tOver2Pi = t / (2.0 * Math.PI);
            return Math.Pow(mu * tOver2Pi * tOver2Pi, 1.0 / 3.0);
        }
    }
}
