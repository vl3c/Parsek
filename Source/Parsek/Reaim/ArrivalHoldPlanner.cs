using System.Collections.Generic;

namespace Parsek.Reaim
{
    // Re-aim Phase 4 (cross-parent destination-SOI arrival alignment), implementation Phase 3b: the PURE
    // planner that decides the loop-clock arrival HOLD for a re-aim landing. The hold defers the in-SOI
    // replay so the destination's rotation phase at the DEORBIT (orbit->descent handoff) recurs to its
    // recorded value; it is the inverse of a loiter cut, applied inside the shared loop clock so both render
    // paths inherit it. Pure; no Unity. See docs/dev/design-mission-periodicity.md and
    // docs/dev/plans/reaim-destination-arrival-alignment.md.
    internal static class ArrivalHoldPlanner
    {
        /// <summary>The arrival hold decision. Pure value.</summary>
        internal struct ArrivalHoldResult
        {
            public double HoldSeconds;   // 0 when no hold
            public double HoldAtUT;      // NaN when no hold
            public bool Applied;

            internal static ArrivalHoldResult None =>
                new ArrivalHoldResult { HoldSeconds = 0.0, HoldAtUT = double.NaN, Applied = false };
        }

        /// <summary>
        /// The loop-clock arrival hold for a re-aim landing, or None. GATED (fail closed to faithful, leaving
        /// the span clock byte-identical): alignment OFF (Drop mode), an unsupported destination (2+ moons,
        /// Jool-class), an orbit-only arrival (no DestRotation), or a degenerate rotation period all return
        /// None. Otherwise the hold W is the minimal forward delay that lands the SOI entry at the recorded
        /// destination rotation phase; because the in-SOI sequence is rigid, aligning the entry aligns the
        /// deorbit. The live (unshifted) SOI entry replays at
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
            if (mode == TransitedBodyRotationMode.Drop || bodyInfo == null || string.IsNullOrEmpty(targetBody))
                return ArrivalHoldResult.None;
            if (double.IsNaN(recordedArrivalUT) || double.IsNaN(phaseAnchorUT) || double.IsNaN(spanStartUT))
                return ArrivalHoldResult.None;

            DestinationConstraintExtractor.DestinationConstraintSet destSet =
                DestinationConstraintExtractor.ExtractDestinationConstraints(allConstraints, targetBody, bodyInfo);
            // No hold for an unsupported destination (2+ constrained moons) or an orbit-only arrival (no
            // landing rotation): nothing to align, so the clock stays faithful.
            if (!destSet.Supported || !destSet.HasLandingRotation)
                return ArrivalHoldResult.None;

            double tRot = bodyInfo.RotationPeriod(targetBody);
            if (double.IsNaN(tRot) || double.IsInfinity(tRot) || tRot <= 0.0)
                return ArrivalHoldResult.None;

            double liveEntryUT = phaseAnchorUT
                + (GhostPlaybackLogic.CompressSpanUT(recordedArrivalUT, loiterCuts) - spanStartUT);
            double w = GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds(recordedArrivalUT, liveEntryUT, tRot);
            if (!(w > 0.0) || double.IsInfinity(w))
                return ArrivalHoldResult.None;  // already aligned (w == 0) or degenerate: no hold

            return new ArrivalHoldResult { HoldSeconds = w, HoldAtUT = recordedArrivalUT, Applied = true };
        }
    }
}
