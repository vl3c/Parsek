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
        /// </summary>
        internal static List<OrbitSegment> ReplaceHeliocentricLeg(
            IReadOnlyList<OrbitSegment> memberSegments, OrbitSegment transferSegment,
            string commonAncestor, double recordedDepartureUT, double recordedArrivalUT,
            double transferRenderStartUT, double transferRenderEndUT)
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
                    result.Add(s);
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
        /// Flag dispatcher for the re-aim whole-chain synthesis fix (reaim-fix-plan.md, option 3 / P1).
        /// This is the PURE seam the <c>reaimChainSynthesis</c> feature flag gates so the flag-OFF
        /// byte-identical proof is runnable in xUnit (the live <c>BuildWindowSegments</c> is Unity-bound
        /// and cannot run headless).
        ///
        /// <para>When <paramref name="useChain"/> is <c>false</c> this returns EXACTLY
        /// <see cref="ReplaceHeliocentricLeg"/>'s output (the identity path: today's single-leg
        /// heliocentric replacement, byte-identical to the baseline). When <c>true</c> it CURRENTLY also
        /// returns <see cref="ReplaceHeliocentricLeg"/> - a placeholder for the P3 chain synthesis
        /// (escape + transfer + capture). Until P3 lands, flag ON and flag OFF are identical, so turning
        /// the flag on changes no behavior yet. Same purity contract as
        /// <see cref="ReplaceHeliocentricLeg"/>: operates on copied OrbitSegment value structs, never
        /// writes back to <paramref name="memberSegments"/> / the recording / the .prec.</para>
        /// </summary>
        // TODO P3: when useChain is true, call the new ReplaceTransferChain(...) instead of
        // ReplaceHeliocentricLeg(...). ReplaceTransferChain synthesizes the escape + transfer + capture
        // legs from the single Lambert solve's v1/v2 and tiles them contiguously into the recorded
        // segments (see reaim-fix-plan.md STEP 0-5 + the P2/P3 phases). On any synthesis failure it must
        // fall back to ReplaceHeliocentricLeg (all-or-nothing fail-closed). For now the ON path is a no-op
        // placeholder returning the identical result so P1 ships a default-OFF flag with zero behavior
        // change.
        internal static List<OrbitSegment> AssembleWindowChain(
            bool useChain,
            IReadOnlyList<OrbitSegment> memberSegments, OrbitSegment transferSegment,
            string commonAncestor, double recordedDepartureUT, double recordedArrivalUT,
            double transferRenderStartUT, double transferRenderEndUT)
        {
            if (useChain)
            {
                // P3 placeholder: same result as the identity path until ReplaceTransferChain lands. Keeps
                // flag ON behavior-identical to flag OFF in this phase (no actual chain synthesis yet).
                return ReplaceHeliocentricLeg(
                    memberSegments, transferSegment, commonAncestor,
                    recordedDepartureUT, recordedArrivalUT,
                    transferRenderStartUT, transferRenderEndUT);
            }

            // Identity path (flag OFF): byte-identical to today's baseline.
            return ReplaceHeliocentricLeg(
                memberSegments, transferSegment, commonAncestor,
                recordedDepartureUT, recordedArrivalUT,
                transferRenderStartUT, transferRenderEndUT);
        }
    }
}
