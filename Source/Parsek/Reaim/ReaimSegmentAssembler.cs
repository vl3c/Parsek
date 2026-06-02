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
            return CoalesceSameOrbitFragments(result);
        }

        /// <summary>
        /// Coalesces consecutive same-orbit OrbitSegments (LOOP-ONLY, IN-MEMORY) into one continuous
        /// segment. The recorder splits a single parking-orbit coast into several fragments with the SAME
        /// Kepler elements (it closes + reopens a segment at every recording-mode transition -
        /// background/foreground vessel switch, scene change - with no coalescing). On the faithful first
        /// play those fragments replay at exact UT over real time and are invisible; but under a LOOP the
        /// compressed span clock can sweep the playback head across a short fragment (e.g. the ~40s parking
        /// tail right before an escape burn) and out of its bounds in under one render frame, opening a
        /// "no orbit bounds" gap where the map icon flashes at the wrong phase (the s15 "Kerbal X" decouple
        /// seam). Merging the fragments into one segment means the head is always inside one continuous
        /// segment across the whole coast, so there is no mid-frame seam to flash on.
        ///
        /// <para>Merges adjacent (by startUT) segments while
        /// <see cref="TrajectoryMath.AreOrbitSegmentsEquivalentForMapDisplay"/> holds AND
        /// <c>isPredicted</c> matches, taking [first.startUT, last.endUT] and KEEPING the FIRST fragment's
        /// elements / epoch / frame (the predicate ignores epoch + mean anomaly, and the first fragment's
        /// epoch correctly anchors the merged arc). A real maneuver (the escape burn changes sma by
        /// millions and ecc 0.0013 -> 1.19) is far outside the predicate tolerance, so boundaries that
        /// matter are preserved; the <c>isPredicted</c> guard keeps a predicted ballistic-tail segment from
        /// folding into a non-predicted parking arc (so the kind classification stays honest). Element
        /// equivalence is the load-bearing test, NOT the gap duration - same-orbit fragments coalesce no
        /// matter how large the sampling gap between them. Pure; assumes <paramref name="segs"/> is sorted
        /// by startUT (the caller sorts first). Operates on the in-memory re-aim playback list only - the
        /// recorded data is never touched.</para>
        /// </summary>
        internal static List<OrbitSegment> CoalesceSameOrbitFragments(List<OrbitSegment> segs)
        {
            if (segs == null || segs.Count < 2)
                return segs;

            var merged = new List<OrbitSegment>(segs.Count);
            OrbitSegment current = segs[0];
            for (int i = 1; i < segs.Count; i++)
            {
                OrbitSegment next = segs[i];
                if (current.isPredicted == next.isPredicted
                    && TrajectoryMath.AreOrbitSegmentsEquivalentForMapDisplay(current, next))
                {
                    // Same orbit, contiguous fragment: extend the running segment over the gap + the new
                    // data. Keep the first fragment's elements/epoch (anchors the merged arc).
                    if (next.endUT > current.endUT)
                        current.endUT = next.endUT;
                }
                else
                {
                    merged.Add(current);
                    current = next;
                }
            }
            merged.Add(current);
            return merged;
        }
    }
}
