using System.Collections.Generic;

namespace Parsek.Reaim
{
    // Re-aim Phase 4 (cross-parent destination-SOI arrival alignment), implementation Phase 3b: the PURE
    // planner that decides the loop-clock arrival HOLD for a re-aim arrival. The hold defers the in-SOI
    // replay so the destination-side phase at the handoff recurs to its recorded value; it is the inverse
    // of a loiter cut, applied inside the shared loop clock so both render paths inherit it. Two alignment
    // targets (M4c, design doc section 6 / D8): a LANDING aligns the destination's ROTATION (T_rot at the
    // deorbit); a destination STATION rendezvous aligns the station's ORBIT (T_station at the SOI entry,
    // rigid through to the rendezvous). A destination carrying BOTH (or a station plus a constrained
    // moon) has no single hold satisfying both periods - fail closed to faithful with the reason
    // surfaced as the UI arrival amber. Pure; no Unity. See
    // docs/dev/design-mission-phasing-alignment.md section 6 and
    // docs/dev/plans/mission-station-arrival-hold.md.
    internal static class ArrivalHoldPlanner
    {
        /// <summary>The arrival hold decision. Pure value.</summary>
        internal struct ArrivalHoldResult
        {
            public double HoldSeconds;          // 0 when no hold
            public double HoldAtUT;             // NaN when no hold
            public double AlignPeriodSeconds;   // T_rot (landing) or T_station (station); NaN when no hold
            public bool Applied;
            public bool IsStationHold;          // true when AlignPeriodSeconds is the station period
            public string AmberReason;          // D8 fail-closed surface (UI arrival amber); null when none

            internal static ArrivalHoldResult None =>
                new ArrivalHoldResult
                {
                    HoldSeconds = 0.0,
                    HoldAtUT = double.NaN,
                    AlignPeriodSeconds = double.NaN,
                    Applied = false,
                    IsStationHold = false,
                    AmberReason = null,
                };

            internal static ArrivalHoldResult NoneWithAmber(string reason)
            {
                ArrivalHoldResult r = None;
                r.AmberReason = reason;
                return r;
            }
        }

        /// <summary>
        /// The loop-clock arrival hold for a re-aim arrival, or None. GATED (fail closed to faithful,
        /// leaving the span clock byte-identical): an unsupported destination (2+ moons Jool-class, or
        /// the D8 dual-constraint shapes - those also carry <see cref="ArrivalHoldResult.AmberReason"/>
        /// when a station is involved), an orbit-only arrival with no station, a landing with alignment
        /// OFF (Drop mode - the mode gates ONLY the rotation hold: it is the transited-body ROTATION
        /// alignment A/B, and the station hold is a fully automatic alignment with no toggle per design
        /// D4), or a degenerate align period all return None. Otherwise the hold W is the minimal
        /// forward delay that lands the SOI entry at the recorded destination-side phase (rotation
        /// phase for a landing; station orbital phase for a destination station - the in-SOI sequence
        /// is rigid, so aligning the entry aligns the deorbit / the rendezvous). The live (unshifted)
        /// SOI entry replays at
        /// <c>phaseAnchorUT + (CompressSpanUT(recordedArrivalUT, loiterCuts) - spanStartUT)</c> (the loop
        /// clock's recorded-span -> live map, accounting for launch-side loiter compression). The hold is
        /// inserted at <c>recordedArrivalUT</c> (the heliocentric-&gt;capture boundary). Pure.
        /// </summary>
        internal static ArrivalHoldResult ComputeArrivalHold(
            IReadOnlyList<PhaseConstraint> allConstraints,
            string targetBody,
            double recordedArrivalUT,
            TransitedBodyRotationMode mode,
            double phaseAnchorUT,
            double spanStartUT,
            IReadOnlyList<GhostPlaybackLogic.LoopCut> loiterCuts,
            IBodyInfo bodyInfo)
        {
            if (bodyInfo == null || string.IsNullOrEmpty(targetBody))
                return ArrivalHoldResult.None;
            if (double.IsNaN(recordedArrivalUT) || double.IsNaN(phaseAnchorUT) || double.IsNaN(spanStartUT))
                return ArrivalHoldResult.None;

            DestinationConstraintExtractor.DestinationConstraintSet destSet =
                DestinationConstraintExtractor.ExtractDestinationConstraints(allConstraints, targetBody, bodyInfo);
            // Unsupported destination: no hold. A station-bearing failure (the D8 dual shapes, a
            // moon-orbiting station, a station-bearing Jool-class destination) carries the reason as
            // the arrival amber so the UI can explain WHY the alignment stays faithful; the
            // pre-existing no-station Jool-class path stays silent (byte-identical).
            if (!destSet.Supported)
            {
                return destSet.HasStation
                    ? ArrivalHoldResult.NoneWithAmber(destSet.Reason)
                    : ArrivalHoldResult.None;
            }

            // Pick the alignment target period. Station first: a Supported set never carries both
            // (the D8 rejection above), so the order only matters defensively. The Drop-mode gate
            // applies ONLY to the rotation hold (see the summary); a station hold computes under
            // every mode.
            double tAlign;
            bool isStationHold;
            if (destSet.HasStation)
            {
                tAlign = destSet.StationPeriodSeconds;
                isStationHold = true;
            }
            else if (destSet.HasLandingRotation && mode != TransitedBodyRotationMode.Drop)
            {
                tAlign = bodyInfo.RotationPeriod(targetBody);
                isStationHold = false;
            }
            else
            {
                // Orbit-only arrival with no station, or a landing with alignment off (Drop):
                // nothing to align, the clock stays faithful.
                return ArrivalHoldResult.None;
            }

            // A destination-side loiter cut (a recorded parking orbit excised AFTER SOI entry) compresses the
            // in-SOI duration by whole orbital periods, breaking the rigidity that lets an entry-referenced
            // hold align the deorbit / the rendezvous (the alignment would land off by cut mod T_align). That
            // is the deferred pre-landing-trim (L8) case: fail closed to faithful (no hold) here rather than
            // apply a partially-aligned hold; the M-MIS-2 P4 re-timer replaces this refusal later. Launch-side
            // cuts (before SOI entry) are fine - CompressSpanUT already folds them into liveEntryUT below.
            if (loiterCuts != null)
            {
                for (int i = 0; i < loiterCuts.Count; i++)
                    if (loiterCuts[i].EndUT > recordedArrivalUT)
                        return ArrivalHoldResult.None;
            }

            if (double.IsNaN(tAlign) || double.IsInfinity(tAlign) || tAlign <= 0.0)
                return ArrivalHoldResult.None;

            double liveEntryUT = phaseAnchorUT
                + (GhostPlaybackLogic.CompressSpanUT(recordedArrivalUT, loiterCuts) - spanStartUT);
            double w = GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds(recordedArrivalUT, liveEntryUT, tAlign);
            if (!(w > 0.0) || double.IsInfinity(w))
                return ArrivalHoldResult.None;  // already aligned (w == 0) or degenerate: no hold

            return new ArrivalHoldResult
            {
                HoldSeconds = w,
                HoldAtUT = recordedArrivalUT,
                AlignPeriodSeconds = tAlign,
                Applied = true,
                IsStationHold = isStationHold,
                AmberReason = null,
            };
        }
    }
}
