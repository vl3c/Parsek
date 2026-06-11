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
                ConstrainedMoonCount = 0
            };

            PhaseConstraint? destRotation = null;
            var moonConfigs = new List<PhaseConstraint>();
            var seenMoons = new HashSet<string>();

            int n = allConstraints?.Count ?? 0;
            for (int i = 0; i < n; i++)
            {
                PhaseConstraint c = allConstraints[i];
                if (string.IsNullOrEmpty(c.BodyName))
                    continue;

                // M4c (Tier 2, not yet built): a VesselOrbital (station rendezvous) constraint is
                // NOT a destination-body constraint - its BodyName is the body the STATION orbits,
                // which the moon/rotation matching below would misread as a destination orbital
                // config. M4a-supported missions are same-parent (never re-aim), so this is
                // defensive; the destination-station hold lands with M4c.
                if (c.Kind == ConstraintKind.VesselOrbital)
                    continue;

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

            if (moonConfigs.Count > MaxConstrainedMoons)
            {
                // Jool-class many-moon destination: detect and fail closed to faithful rather than
                // attempt a joint solve whose duty-cycle product pushes the aligned window centuries out.
                result.Supported = false;
                result.Reason =
                    moonConfigs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " constrained moons of '" + (targetBody ?? "?") + "' (Jool-class), deferred";
                result.Constraints.Clear();
                LogExtract(targetBody, result);
                return result;
            }

            if (destRotation != null)
                result.Constraints.Add(destRotation.Value);
            result.Constraints.AddRange(moonConfigs);

            // An empty set (orbit-only arrival: no landing rotation and no constrained moon) stays
            // Supported - any window is faithful and the arrival solver's no-constraint path handles it.
            LogExtract(targetBody, result);
            return result;
        }

        private static void LogExtract(string targetBody, DestinationConstraintSet r)
        {
            // Intentional shared gate: the summary reuses MissionPeriodicity.SuppressLogging (the same
            // periodicity subsystem family) rather than a selector-owned flag, so tests silence/observe
            // the whole family with one switch.
            if (MissionPeriodicity.SuppressLogging)
                return;
            ParsekLog.Verbose("ReaimArrival",
                "dest-constraints target=" + (targetBody ?? "?") +
                " landingRotation=" + r.HasLandingRotation +
                " moons=" + r.ConstrainedMoonCount +
                " emitted=" + r.Constraints.Count +
                " supported=" + r.Supported +
                (r.Supported ? "" : " reason='" + r.Reason + "'"));
        }
    }
}
