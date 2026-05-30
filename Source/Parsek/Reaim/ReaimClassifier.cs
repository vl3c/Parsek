using System;
using System.Collections.Generic;

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

        public double RecordedDepartureUT;     // S2 start = launch-body SOI exit
        public double RecordedArrivalUT;       // S2 end   = target SOI entry
        public double RecordedTransferTofSeconds; // S2 duration (a nudge seed; the window solves its own tof)

        internal static ReaimMissionPlan Unsupported(string launchBody, string reason)
        {
            return new ReaimMissionPlan { Supported = false, LaunchBody = launchBody, Reason = reason };
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

            // TRANSFER RUN (docs/dev/plans/reaim-loiter-compression.md section 3.2): the transfer is the
            // run of common-ancestor (Sun) segments on the SAME orbit ENDING at the target SOI entry
            // (arrival.startUT). RecordedDepartureUT = the start of the EARLIEST such segment (NOT the
            // first Sun segment in the mission, which would wrongly include a heliocentric loiter and feed
            // a too-long tof to the Lambert solve). Walk backwards from the arrival, including contiguous
            // Sun segments whose semi-major axis matches (mid-course-correction coasts are ~the same
            // orbit, so they stay in the run); STOP at a body change (the launch-body parking) or a
            // semi-major-axis STEP (a maneuver -- the transfer burn out of a heliocentric loiter, or any
            // deliberate orbit change). The a-step, not a UT gap, is the discriminator: an MCC burn leaves
            // a small UT gap but ~the same orbit, while the transfer-departure burn is a real a change.
            const double AStepRelThreshold = ReaimLoiterCompressor.DefaultAStepRelThreshold;
            int transferStartIdx = -1;
            double prevIncludedA = double.NaN;
            for (int i = arrivalIdx - 1; i >= 0; i--)
            {
                OrbitSegment s = segs[i];
                if (s.bodyName != commonAncestor)
                    break;                                   // hit the launch-body parking -> run ends
                if (!double.IsNaN(prevIncludedA))
                {
                    double aRel = Math.Abs(s.semiMajorAxis - prevIncludedA)
                        / Math.Max(1.0, Math.Abs(prevIncludedA));
                    if (aRel > AStepRelThreshold)
                        break;                               // a maneuver (transfer burn) -> run ends
                }
                transferStartIdx = i;
                prevIncludedA = s.semiMajorAxis;
            }
            if (transferStartIdx < 0)
                return ReaimMissionPlan.Unsupported(launchBody,
                    "no transfer arc ending at the target SOI entry");

            // Declined v1 case: the segment immediately before the transfer run is also a common-ancestor
            // (Sun) segment -> the transfer departs from a heliocentric PARKING orbit, not the launch
            // body. The Lambert solve assumes r1 = launchBody position, so re-aim would mis-aim; decline
            // to faithful (heliocentric-parking departures are deferred).
            if (transferStartIdx - 1 >= 0 && segs[transferStartIdx - 1].bodyName == commonAncestor)
                return ReaimMissionPlan.Unsupported(launchBody,
                    "transfer departs from a heliocentric parking orbit (deferred); staying faithful");

            OrbitSegment transferStart = segs[transferStartIdx];
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
                RecordedDepartureUT = transferStart.startUT,            // transfer departure (SOI exit)
                RecordedArrivalUT = arrival.startUT,                    // target SOI entry
                RecordedTransferTofSeconds = arrival.startUT - transferStart.startUT
            };
        }
    }
}
