using System.Collections.Generic;

namespace Parsek.Reaim
{
    // Assembles the per-window OrbitSegment list for a re-aimed ghost (docs/dev/plans/
    // reaim-interplanetary-transfers.md, Phase 3). PURE: operates on OrbitSegment structs + scalars,
    // so the assembly ordering + UT coherence is unit-testable; the live caller supplies the
    // synthesized heliocentric transfer segment (built from ReaimTransferSynthesizer's Orbit).
    //
    // TIMELINE MODEL (the load-bearing decision): the assembled segments are in ABSOLUTE UT for the
    // chosen window. The synthesizer solves the transfer in absolute UT (the live window), and its
    // inertial orientation aims at the target's position AT THAT WINDOW - it cannot be time-shifted
    // by the faithful loop's phaseAnchor (a recorded->live remap) without pointing at empty space. So
    // a re-aim ghost plays this absolute-UT trajectory directly at the live clock for the window's
    // span; the schedule spawns one per window. (This is why re-aim needs its own playback path, not
    // the faithful span-clock shift - flagged for Phase 3c builder/engine wiring.)
    //
    // v1 layout: [S0 recorded parking (re-anchored to end at departure)] [S2 synthesized transfer]
    // [S3 recorded arrival (re-anchored to start at SOI entry)]. The S1 ejection (a launch-body
    // hyperbola anchored to the recorded SOI-exit state) is a Phase-3b refinement; v1 accepts the
    // tiny SOI-edge seam between the parking orbit and the heliocentric transfer start (both ~at the
    // launch body, sub-pixel at map scale).
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
        /// Re-anchors a segment so its startUT becomes <paramref name="newStartUT"/> (preserving its
        /// duration + phase). Pure.
        /// </summary>
        internal static OrbitSegment ReanchorStart(OrbitSegment seg, double newStartUT)
        {
            return ShiftInTime(seg, newStartUT - seg.startUT);
        }

        /// <summary>
        /// Re-anchors a segment so its endUT becomes <paramref name="newEndUT"/>. Pure.
        /// </summary>
        internal static OrbitSegment ReanchorEnd(OrbitSegment seg, double newEndUT)
        {
            return ShiftInTime(seg, newEndUT - seg.endUT);
        }

        /// <summary>
        /// Builds the absolute-UT OrbitSegment list for one window: the recorded parking orbit
        /// re-anchored to end at <paramref name="departureUT"/>, the synthesized heliocentric
        /// <paramref name="transferSegment"/> (assumed already spanning [departureUT, soiEntryUT]),
        /// and the recorded arrival leg re-anchored to start at <paramref name="soiEntryUT"/>. Pure.
        /// Returns null when the plan is unsupported or the transfer span is degenerate.
        /// </summary>
        internal static List<OrbitSegment> Assemble(
            ReaimMissionPlan plan, double departureUT, OrbitSegment transferSegment, double soiEntryUT)
        {
            if (!plan.Supported)
                return null;
            if (double.IsNaN(departureUT) || double.IsNaN(soiEntryUT) || soiEntryUT <= departureUT)
                return null;

            // S0: parking orbit, re-anchored so it hands off to the transfer at departureUT.
            OrbitSegment parking = ReanchorEnd(plan.ParkingOrbit, departureUT);

            // S2: the synthesized heliocentric transfer. Normalize its span to [departureUT, soiEntryUT]
            // in case the caller passed raw elements (idempotent if already aligned).
            OrbitSegment transfer = transferSegment;
            transfer.startUT = departureUT;
            transfer.endUT = soiEntryUT;
            transfer.bodyName = plan.CommonAncestor;
            transfer.isPredicted = false;

            // S3: recorded arrival leg, re-anchored to begin at the SOI entry.
            OrbitSegment arrival = ReanchorStart(plan.ArrivalLeg, soiEntryUT);

            return new List<OrbitSegment> { parking, transfer, arrival };
        }
    }
}
