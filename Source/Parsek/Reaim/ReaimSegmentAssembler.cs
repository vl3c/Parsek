using System.Collections.Generic;

namespace Parsek.Reaim
{
    // Assembles the per-window OrbitSegment list for a re-aimed ghost (docs/dev/plans/
    // reaim-interplanetary-transfers.md, Phase 3). PURE: operates on OrbitSegment structs + scalars,
    // so the assembly ordering + UT coherence is unit-testable; the live caller supplies the
    // synthesized heliocentric transfer segment (built from ReaimTransferSynthesizer's Orbit).
    //
    // TIMELINE MODEL (recorded-span, recorded-tof): the engine samples a loop member's trajectory at
    // loopUT in [spanStart, spanEnd] (GhostPlaybackLogic.TryComputeSpanLoopUT: loopUT = spanStart +
    // phaseWithinSpan), then phaseAnchor maps the live clock to the chosen synodic window. So the
    // assembled segments are RECORDED-SPAN-relative, NOT absolute: the parking orbit keeps its recorded
    // UTs, the transfer occupies [recordedExitUT, recordedExitUT + tof], and the arrival is re-anchored
    // after it. The caller passes the RECORDED transfer tof (recordedExitUT = the recorded SOI-exit, and
    // tof = the recorded transfer duration), so the arrival re-anchors to the RECORDED arrival UT and the
    // assembled span fits the fixed recorded loop span [spanStart, spanEnd] EXACTLY - no clipping (this
    // is why re-aim uses the recorded tof, not an idealized Hohmann tof: a Hohmann tof != recorded tof
    // would shift the arrival out of the fixed span). What varies PER WINDOW is only the transfer orbit's
    // INERTIAL ORIENTATION (inc/LAN/argPe, aiming at the target's actual position at that window) and its
    // epoch - which the live caller shifts into recorded-span time before calling here.
    //
    // v1 layout: [S0 recorded parking (recorded UTs)] [S2 transfer at [exitUT, exitUT+tof]] [S3 recorded
    // arrival (re-anchored to start at exitUT+tof = the recorded arrival UT)]. The S1 ejection (a
    // launch-body hyperbola anchored to the recorded SOI-exit state) is a deferred refinement; v1 accepts
    // the tiny SOI-edge seam between the parking orbit and the heliocentric transfer start (both ~at the
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
        /// Builds the RECORDED-SPAN-relative OrbitSegment list for a re-aim window: the recorded
        /// parking orbit (unchanged recorded UTs), the heliocentric <paramref name="transferSegment"/>
        /// placed at [recordedExitUT, recordedExitUT + <paramref name="tofSeconds"/>] (the caller has
        /// already set its per-window inertial orientation + shifted its epoch into recorded-span
        /// time), and the recorded arrival leg re-anchored to start at recordedExitUT + tof. The
        /// recorded SOI-exit time is <c>plan.RecordedDepartureUT</c>. Pure. Returns null when the plan
        /// is unsupported or the tof is degenerate.
        /// </summary>
        internal static List<OrbitSegment> Assemble(
            ReaimMissionPlan plan, OrbitSegment transferSegment, double tofSeconds)
        {
            if (!plan.Supported)
                return null;
            if (double.IsNaN(tofSeconds) || double.IsInfinity(tofSeconds) || tofSeconds <= 0.0)
                return null;

            double exitUT = plan.RecordedDepartureUT;        // recorded-span SOI-exit time
            double arrivalUT = exitUT + tofSeconds;          // re-timed to the (constant) Hohmann tof

            // S0: recorded parking orbit, unchanged (it hands off to the transfer at exitUT).
            OrbitSegment parking = plan.ParkingOrbit;

            // S2: the per-window heliocentric transfer, placed in recorded-span time.
            OrbitSegment transfer = transferSegment;
            transfer.startUT = exitUT;
            transfer.endUT = arrivalUT;
            transfer.bodyName = plan.CommonAncestor;
            transfer.isPredicted = false;

            // S3: recorded arrival leg, re-anchored to begin when the re-timed transfer ends.
            OrbitSegment arrival = ReanchorStart(plan.ArrivalLeg, arrivalUT);

            return new List<OrbitSegment> { parking, transfer, arrival };
        }
    }
}
