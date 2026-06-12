using System.Collections.Generic;

namespace Parsek.Reaim
{
    // Re-aim Phase 4 (cross-parent destination-SOI arrival-UT alignment), implementation Phase 2:
    // SELECT the destination-side constraint set for the arrival-window solver
    // (DestinationArrivalSolver) from the constraint list MissionPeriodicity.ExtractConstraints
    // already emits for the mission.
    //
    // CRITICAL (regression safety): this does NOT modify ExtractConstraints, Solve, or the Support
    // classification. A cross-parent mission MUST keep Support == UnsupportedCrossParent so that
    // MissionLoopUnitBuilder leaves phaseLocked == false and runs the re-aim path
    // (MissionLoopUnitBuilder.cs `if (!phaseLocked)`). Flipping Support to Supported would make
    // MissionPeriodicity.Solve return ShouldPhaseLock == true (it does for ANY Supported config) and
    // the builder would phase-lock the mission via the periodicity path, SKIPPING the re-aim transfer
    // rendering entirely. So this is a pure, additive DOWNSTREAM selector over the already-computed
    // constraint list; nothing upstream changes.
    //
    // The topology: from all emitted constraints, the DestRotation is the Rotation constraint on the
    // TARGET body (the landing surface + arrival-orbit hand-off rule already gates its emission
    // upstream, MissionPeriodicity.cs:391-414). Each MoonConfig is an Orbital constraint on a MOON of
    // the target (a body whose reference body is the target) that the recorded in-SOI arc actually
    // enters the SOI of. The target's OWN Orbital constraint (its heliocentric SOI-entry) is EXCLUDED:
    // arrival alignment does not care WHERE the ghost crosses the destination SOI edge, only the
    // destination configuration at entry / landing. 2+ constrained moons (Jool-class) fail closed to
    // faithful (the deferred deferral boundary, plan section 8b).
    internal static class DestinationConstraintExtractor
    {
        /// <summary>
        /// The destination-side constraints for the arrival solve, selected from the mission's
        /// extracted constraint list. Pure value; nothing persisted.
        /// </summary>
        internal struct DestinationConstraintSet
        {
            /// <summary>DestRotation (the landing body's rotation) + 0/1 MoonConfig, in solver order.</summary>
            public List<PhaseConstraint> Constraints;

            /// <summary>False => fail closed to faithful (the un-aligned render) - a Jool-class
            /// destination with more constrained moons than this phase supports.</summary>
            public bool Supported;

            /// <summary>Why unsupported (set only when <see cref="Supported"/> is false).</summary>
            public string Reason;

            /// <summary>True when a DestRotation (target-body landing rotation) was found.</summary>
            public bool HasLandingRotation;

            /// <summary>Distinct moons of the target whose SOI the recorded arc enters.</summary>
            public int ConstrainedMoonCount;

            /// <summary>M4c (Tier 2): true when a VesselOrbital constraint targets the
            /// destination SYSTEM - orbiting the target itself (the supported station-hold
            /// shape) or a moon of the target (fail-closed with <see cref="Reason"/>). The
            /// arrival hold substitutes the station period for T_rot when this is the only
            /// destination-side constraint; combined with a landing rotation or a constrained
            /// moon there is no single hold satisfying both periods (design D8), so those
            /// shapes set <see cref="Supported"/> false and the reason surfaces as the UI
            /// arrival amber.</summary>
            public bool HasStation;

            /// <summary>The destination station's LIVE orbital period (the VesselOrbital
            /// constraint's PeriodSeconds, read at extraction). NaN when no station orbits the
            /// target itself.</summary>
            public double StationPeriodSeconds;

            /// <summary>The destination station's anchor vessel pid (logging).</summary>
            public uint StationAnchorPid;
        }

        // The most constrained moons this phase handles (0 or 1). 2+ (a Jool-class mini star system)
        // collapses the aligned-window rate toward the centuries-away regime and is deferred.
        internal const int MaxConstrainedMoons = 1;

        /// <summary>
        /// Select the destination constraint set for a cross-parent re-aim arrival from the mission's
        /// extracted constraints. <paramref name="targetBody"/> is the re-aim plan's TargetBody.
        /// <paramref name="bodyInfo"/> resolves moon-of-target via <c>ReferenceBodyName</c>. Pure; does
        /// not mutate <paramref name="allConstraints"/>; never touches Support / the periodicity solve.
        /// </summary>
        internal static DestinationConstraintSet ExtractDestinationConstraints(
            IReadOnlyList<PhaseConstraint> allConstraints,
            string targetBody,
            IBodyInfo bodyInfo)
        {
            var result = new DestinationConstraintSet
            {
                Constraints = new List<PhaseConstraint>(),
                Supported = true,
                Reason = null,
                HasLandingRotation = false,
                ConstrainedMoonCount = 0,
                HasStation = false,
                StationPeriodSeconds = double.NaN,
                StationAnchorPid = 0
            };

            PhaseConstraint? destRotation = null;
            PhaseConstraint? station = null;       // VesselOrbital orbiting the target itself
            string moonStationReason = null;       // VesselOrbital orbiting a moon of the target
            int nonDestStations = 0;               // launch-side / other-system stations (skipped)
            var moonConfigs = new List<PhaseConstraint>();
            var seenMoons = new HashSet<string>();

            int n = allConstraints?.Count ?? 0;
            for (int i = 0; i < n; i++)
            {
                PhaseConstraint c = allConstraints[i];
                if (string.IsNullOrEmpty(c.BodyName))
                    continue;

                // M4c (Tier 2): a VesselOrbital ORBITING THE TARGET is the destination station -
                // the arrival hold substitutes its orbital period for T_rot. At most one exists
                // (the classifier's exactly-one-foreign-anchor rule). A station orbiting a MOON
                // of the target is in-system geometry a single-period hold cannot align (its
                // recorded-relative configuration depends on the moon phase AND the station
                // phase jointly): fail closed with the reason as the arrival amber. Any other
                // VesselOrbital (a launch-side fuel depot, another system) is not a destination
                // constraint - skipped, counted for the log line; the re-aim path has no
                // machinery to align a launch-side station phase.
                if (c.Kind == ConstraintKind.VesselOrbital)
                {
                    if (c.BodyName == targetBody)
                    {
                        if (station == null)
                            station = c;
                        continue;
                    }
                    string stationParent = bodyInfo?.ReferenceBodyName(c.BodyName);
                    if (stationParent == targetBody && !string.IsNullOrEmpty(targetBody))
                    {
                        moonStationReason =
                            "station orbits '" + c.BodyName + "', a moon of destination '" +
                            targetBody + "': in-system station alignment deferred";
                        continue;
                    }
                    nonDestStations++;
                    continue;
                }

                if (c.Kind == ConstraintKind.Rotation)
                {
                    // DestRotation = the landing body's rotation = the TARGET body's rotation. The
                    // launch-pad rotation and any other body's rotation are not arrival constraints.
                    if (c.BodyName == targetBody && destRotation == null)
                        destRotation = c;
                    continue;
                }

                // Orbital: a MoonConfig is an SOI entry into a MOON of the target. The target's OWN
                // Orbital (its heliocentric SOI-entry) is excluded - SOI-edge direction is irrelevant.
                if (c.BodyName == targetBody)
                    continue;
                string parent = bodyInfo?.ReferenceBodyName(c.BodyName);
                if (parent == targetBody && !string.IsNullOrEmpty(targetBody) && seenMoons.Add(c.BodyName))
                    moonConfigs.Add(c);
            }

            result.HasLandingRotation = destRotation != null;
            result.ConstrainedMoonCount = moonConfigs.Count;
            // Station fields populated BEFORE any early return so a station-bearing Jool-class
            // destination still carries them (the arrival amber reads them off the failed set).
            result.HasStation = station != null || moonStationReason != null;
            result.StationPeriodSeconds = station?.PeriodSeconds ?? double.NaN;
            result.StationAnchorPid = station?.AnchorVesselPid ?? 0;

            if (moonConfigs.Count > MaxConstrainedMoons)
            {
                // Jool-class many-moon destination: detect and fail closed to faithful rather than
                // attempt a joint solve whose duty-cycle product pushes the aligned window centuries out.
                result.Supported = false;
                result.Reason =
                    moonConfigs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " constrained moons of '" + (targetBody ?? "?") + "' (Jool-class), deferred";
                result.Constraints.Clear();
                LogExtract(targetBody, result, nonDestStations);
                return result;
            }

            // M4c fail-closed shapes (design D8 + the moon-station extension). ONE hold aligns
            // ONE period: a destination with both a station and any other destination-side
            // period (landing rotation, constrained moon SOI, or the station itself orbiting a
            // moon) has no single hold satisfying all - fail closed to faithful; the reason
            // surfaces as the UI arrival amber. Wiring SolveArrivalWindow for the joint pick is
            // the explicitly deferred post-M4c follow-up. TransitedBodyRotationMode.Drop does
            // NOT rescue the landing+station case: D8's letter wins; making D8 mode-aware is
            // SolveArrivalWindow territory.
            if (moonStationReason != null)
            {
                result.Supported = false;
                result.Reason = moonStationReason;
                result.Constraints.Clear();
                LogExtract(targetBody, result, nonDestStations);
                return result;
            }
            if (result.HasStation && result.HasLandingRotation)
            {
                result.Supported = false;
                result.Reason =
                    "landing rotation + station rendezvous at '" + (targetBody ?? "?") +
                    "': no single arrival hold aligns both periods (deferred)";
                result.Constraints.Clear();
                LogExtract(targetBody, result, nonDestStations);
                return result;
            }
            if (result.HasStation && moonConfigs.Count > 0)
            {
                result.Supported = false;
                result.Reason =
                    "station rendezvous + constrained moon SOI at '" + (targetBody ?? "?") +
                    "': no single arrival hold aligns both periods (deferred)";
                result.Constraints.Clear();
                LogExtract(targetBody, result, nonDestStations);
                return result;
            }

            if (destRotation != null)
                result.Constraints.Add(destRotation.Value);
            result.Constraints.AddRange(moonConfigs);

            // An empty set (orbit-only arrival: no landing rotation and no constrained moon) stays
            // Supported - any window is faithful and the arrival solver's no-constraint path handles
            // it. A station-only set also stays Supported: the hold substitutes T_station.
            LogExtract(targetBody, result, nonDestStations);
            return result;
        }

        private static void LogExtract(string targetBody, DestinationConstraintSet r, int nonDestStations)
        {
            // Intentional shared gate: the summary reuses MissionPeriodicity.SuppressLogging (the same
            // periodicity subsystem family) rather than a selector-owned flag, so tests silence/observe
            // the whole family with one switch.
            if (MissionPeriodicity.SuppressLogging)
                return;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            ParsekLog.Verbose("ReaimArrival",
                "dest-constraints target=" + (targetBody ?? "?") +
                " landingRotation=" + r.HasLandingRotation +
                " moons=" + r.ConstrainedMoonCount +
                " station=" + (r.HasStation
                    ? (r.StationAnchorPid != 0
                        ? r.StationAnchorPid.ToString(ic) + "@" + (targetBody ?? "?") +
                          " T=" + r.StationPeriodSeconds.ToString("F0", ic) + "s"
                        : "moon-of-target")
                    : "none") +
                " nonDestStations=" + nonDestStations.ToString(ic) +
                " emitted=" + r.Constraints.Count +
                " supported=" + r.Supported +
                (r.Supported ? "" : " reason='" + r.Reason + "'"));
        }
    }
}
