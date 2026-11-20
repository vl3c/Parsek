using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The result of carrying an escape-trajectory terminal orbit to a spawn UT through
    /// patched conics (<see cref="EscapeTrajectoryTerminal.ResolveAtUT"/>).
    /// </summary>
    internal struct EscapeSpawnConicResolution
    {
        /// <summary>True when the vessel is still on a conic at the spawn UT.</summary>
        internal bool Resolved;

        /// <summary>The conic the vessel is on at the spawn UT (KSP-native elements).</summary>
        internal OrbitSegment Segment;

        /// <summary>The body whose sphere of influence holds the vessel at the spawn UT.</summary>
        internal string BodyName;

        /// <summary>Sphere-of-influence changes between the terminal orbit and the spawn UT.</summary>
        internal int SoiTransitions;

        /// <summary>Why the path does not reach the spawn UT (null when resolved).</summary>
        internal string RefusalReason;

        /// <summary>The UT the path ended at when refused (NaN when resolved).</summary>
        internal double RefusalUT;
    }

    /// <summary>
    /// A vessel left on an escape trajectory (owner ruling Q10, 2026-11-18: "spawn on its
    /// path"). KSP reports such a vessel ESCAPING (its conic is open, or closed with an
    /// apoapsis beyond the sphere of influence), and the terminal classifier folds that
    /// situation into <see cref="TerminalState.SubOrbital"/>, the "flight not settled yet"
    /// class every Re-Fly, timeline and map consumer already reads. What the ruling changes
    /// is the spawn: an escape SubOrbital spawns as a real vessel on its recorded terminal
    /// orbit carried to the spawn UT, through the same safety checks as an orbit spawn.
    ///
    /// <para>The escape evidence is the recorded terminal orbit itself: an open conic
    /// (eccentricity above 1), or, for a closed conic whose apoapsis needs the body's sphere
    /// of influence to judge, KSP's own ESCAPING situation in the vessel snapshot. Every
    /// other SubOrbital (a ballistic arc inside the sphere of influence) keeps not spawning.
    /// The live spawn re-judges the orbit at the spawn UT with the real body (sphere of
    /// influence, periapsis floor, inbound or outbound), so a stale snapshot situation can
    /// only refuse a spawn, never place a vessel on a path that falls back down.</para>
    /// </summary>
    internal static class EscapeTrajectoryTerminal
    {
        internal const string SnapshotSituationEscaping = "ESCAPING";

        /// <summary>Spawn UTs this close to the terminal orbit's own UT need no propagation.</summary>
        internal const double NoPropagationEpsilonSeconds = 1e-3;

        internal const string RefusalMissingBody = "missing-body";
        internal const string RefusalUnpropagatableElements = "unpropagatable-elements";
        internal const string RefusalImpactsBeforeSpawn = "path-ends-in-atmosphere-or-surface-before-spawn";
        internal const string RefusalSoiTransitionCap = "soi-transition-cap-before-spawn";
        internal const string RefusalExtrapolationFailed = "extrapolation-failed";

        /// <summary>
        /// Relative distance from a sphere-of-influence boundary within which a start state
        /// at the terminal orbit's own epoch counts as sitting on it (a patch starts exactly
        /// on the boundary it crossed).
        /// </summary>
        internal const double BoundaryRelativeTolerance = 1e-6;

        /// <summary>
        /// Pure: does a recording with this terminal state, recorded terminal orbit and
        /// snapshot situation end on an escape trajectory that spawns on its path? SubOrbital
        /// terminal, a propagatable terminal orbit naming a body, and escape evidence: an
        /// open conic (<c>open-conic</c>), or KSP's ESCAPING situation in the snapshot
        /// (<c>snapshot-escaping</c>). <paramref name="evidence"/> is null when it is not.
        /// The recording-level entry point is Recording.IsEscapeTrajectoryTerminal.
        /// </summary>
        internal static bool IsEscapeTerminal(
            TerminalState? terminalState,
            OrbitSegment terminalOrbit,
            string snapshotSituation,
            out string evidence)
        {
            evidence = null;
            if (!terminalState.HasValue
                || terminalState.Value != TerminalState.SubOrbital
                || !IsPropagatableTerminalOrbit(terminalOrbit))
            {
                return false;
            }

            if (terminalOrbit.eccentricity > 1.0)
            {
                evidence = "open-conic";
                return true;
            }

            if (!string.IsNullOrEmpty(snapshotSituation)
                && string.Equals(snapshotSituation.Trim(), SnapshotSituationEscaping, StringComparison.OrdinalIgnoreCase))
            {
                evidence = "snapshot-escaping";
                return true;
            }

            return false;
        }

        /// <summary>
        /// Pure: the terminal orbit names a body and its elements form a conic the Kepler
        /// propagation can follow (finite, closed with a positive semi-major axis or open
        /// with a negative one, not parabolic).
        /// </summary>
        internal static bool IsPropagatableTerminalOrbit(OrbitSegment terminalOrbit)
        {
            return !string.IsNullOrEmpty(terminalOrbit.bodyName)
                && BallisticExtrapolator.TwoBodyOrbit.AreSegmentElementsPropagatable(terminalOrbit, 1.0);
        }

        /// <summary>
        /// Carries <paramref name="terminal"/> from its epoch to <paramref name="spawnUT"/>
        /// through patched conics (sphere-of-influence exits and encounters, the same
        /// two-body propagation the scene-exit finalizer uses). Refused when the path
        /// enters an atmosphere or reaches the surface before the spawn UT, or the chain
        /// cannot be followed. A spawn UT inside the terminal orbit's own sphere of influence
        /// with no encounter returns the terminal orbit unchanged.
        /// <para>The carry starts at <paramref name="captureUT"/> (the recording's end, where
        /// the vessel was known to be inside the terminal body's sphere) when that is later
        /// than the orbit's epoch. A terminal orbit captured on rails carries the epoch of its
        /// patch start, the last sphere-of-influence crossing, where the state sits on the
        /// boundary and the extrapolator's immediate exit / entry tests are numerical noise; a
        /// start still at that epoch does not take an immediate crossing of the boundary it
        /// sits on.</para>
        /// </summary>
        internal static EscapeSpawnConicResolution ResolveAtUT(
            OrbitSegment terminal,
            IReadOnlyDictionary<string, ExtrapolationBody> bodies,
            double spawnUT,
            double captureUT = double.NaN)
        {
            var resolution = new EscapeSpawnConicResolution
            {
                Resolved = false,
                Segment = terminal,
                BodyName = terminal.bodyName,
                SoiTransitions = 0,
                RefusalReason = null,
                RefusalUT = double.NaN
            };

            if (bodies == null
                || string.IsNullOrEmpty(terminal.bodyName)
                || !bodies.TryGetValue(terminal.bodyName, out ExtrapolationBody body)
                || body == null)
            {
                resolution.RefusalReason = RefusalMissingBody;
                return resolution;
            }

            double startUT = terminal.epoch;
            if (!double.IsNaN(captureUT) && !double.IsInfinity(captureUT) && captureUT > startUT)
                startUT = captureUT;
            if (double.IsNaN(spawnUT) || double.IsInfinity(spawnUT)
                || spawnUT - startUT <= NoPropagationEpsilonSeconds)
            {
                resolution.Resolved = true;
                return resolution;
            }

            if (!BallisticExtrapolator.TryPropagate(
                    terminal, body.GravitationalParameter, startUT,
                    out Vector3d startPosition, out Vector3d startVelocity)
                || !IsFinite(startPosition)
                || !IsFinite(startVelocity))
            {
                resolution.RefusalReason = RefusalUnpropagatableElements;
                resolution.RefusalUT = startUT;
                return resolution;
            }

            var startState = new BallisticStateVector
            {
                ut = startUT,
                bodyName = terminal.bodyName,
                position = startPosition,
                velocity = startVelocity,
                orbitalFrameRotation = terminal.orbitalFrameRotation
            };

            string suppressedParentExit = null;
            string suppressedChildEntry = null;
            if (Math.Abs(startUT - terminal.epoch) <= NoPropagationEpsilonSeconds)
            {
                ResolveBoundarySuppressions(
                    body, bodies, startUT, startPosition,
                    out suppressedParentExit, out suppressedChildEntry);
            }

            ExtrapolationLimits limits = ExtrapolationLimits.Default;
            // The horizon is the spawn UT itself (plus a hair so the last conic reaches it).
            limits.maxHorizonYears = (spawnUT - startUT + 1.0) / BallisticExtrapolator.SecondsPerYear;
            ExtrapolationResult extrapolated = BallisticExtrapolator.Extrapolate(
                startState, bodies, limits, warnOnSubSurfaceStart: false,
                initialSuppressedParentExitParentBodyName: suppressedParentExit,
                initialSuppressedChildEntryBodyName: suppressedChildEntry);

            // PqsUnavailable is a soft fallback (the surface scan used sea level), not a
            // broken chain; every other reason means the path could not be followed.
            if (extrapolated.failureReason != ExtrapolationFailureReason.None
                && extrapolated.failureReason != ExtrapolationFailureReason.PqsUnavailable)
            {
                resolution.RefusalReason = RefusalExtrapolationFailed + ":" + extrapolated.failureReason;
                resolution.RefusalUT = extrapolated.terminalUT;
                return resolution;
            }

            if (extrapolated.terminalState == TerminalState.Destroyed
                && extrapolated.terminalUT <= spawnUT)
            {
                resolution.RefusalReason = RefusalImpactsBeforeSpawn;
                resolution.RefusalUT = extrapolated.terminalUT;
                resolution.BodyName = extrapolated.terminalBodyName;
                return resolution;
            }

            if (extrapolated.terminalUT < spawnUT - 1.0)
            {
                resolution.RefusalReason = RefusalSoiTransitionCap;
                resolution.RefusalUT = extrapolated.terminalUT;
                resolution.BodyName = extrapolated.terminalBodyName;
                return resolution;
            }

            List<OrbitSegment> segments = extrapolated.segments;
            int transitions = 0;
            OrbitSegment chosen = terminal;
            if (segments != null)
            {
                for (int i = 0; i < segments.Count; i++)
                {
                    OrbitSegment seg = segments[i];
                    if (seg.startUT > spawnUT)
                        break;
                    if (i > 0 && !string.Equals(seg.bodyName, segments[i - 1].bodyName, StringComparison.Ordinal))
                        transitions++;
                    chosen = seg;
                }
            }

            resolution.Resolved = true;
            resolution.SoiTransitions = transitions;
            if (transitions == 0
                && string.Equals(chosen.bodyName, terminal.bodyName, StringComparison.Ordinal))
            {
                // Still on the terminal conic: keep the recorded elements exactly rather than
                // a frame round trip of the same conic.
                resolution.Segment = terminal;
                resolution.BodyName = terminal.bodyName;
                return resolution;
            }

            resolution.Segment = chosen;
            resolution.BodyName = chosen.bodyName;
            return resolution;
        }

        /// <summary>
        /// Pure: for a start state at a patch start, the boundary it sits on (within
        /// <see cref="BoundaryRelativeTolerance"/>): its own body's sphere (suppress the
        /// immediate exit into the parent) and the nearest child sphere (suppress the
        /// immediate entry into that child).
        /// </summary>
        internal static void ResolveBoundarySuppressions(
            ExtrapolationBody body,
            IReadOnlyDictionary<string, ExtrapolationBody> bodies,
            double ut,
            Vector3d position,
            out string suppressedParentExitParentBodyName,
            out string suppressedChildEntryBodyName)
        {
            suppressedParentExitParentBodyName = null;
            suppressedChildEntryBodyName = null;
            if (body == null)
                return;

            double radius = Magnitude(position);
            if (!string.IsNullOrEmpty(body.ParentBodyName)
                && body.SphereOfInfluence > 0.0
                && Math.Abs(radius - body.SphereOfInfluence)
                    <= body.SphereOfInfluence * BoundaryRelativeTolerance)
            {
                suppressedParentExitParentBodyName = body.ParentBodyName;
            }

            if (bodies == null)
                return;

            double nearest = double.PositiveInfinity;
            foreach (ExtrapolationBody child in bodies.Values)
            {
                if (child == null
                    || child.SphereOfInfluence <= 0.0
                    || child.ParentFrameState == null
                    || !string.Equals(child.ParentBodyName, body.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                child.ParentFrameState(ut, out Vector3d childPosition, out _);
                double gap = Math.Abs(Magnitude(position - childPosition) - child.SphereOfInfluence);
                if (gap <= child.SphereOfInfluence * BoundaryRelativeTolerance && gap < nearest)
                {
                    nearest = gap;
                    suppressedChildEntryBodyName = child.Name;
                }
            }
        }

        private static double Magnitude(Vector3d v)
        {
            return Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        }

        private static bool IsFinite(Vector3d v)
        {
            return !(double.IsNaN(v.x) || double.IsNaN(v.y) || double.IsNaN(v.z)
                || double.IsInfinity(v.x) || double.IsInfinity(v.y) || double.IsInfinity(v.z));
        }

        /// <summary>Formats a resolution for a log line (InvariantCulture).</summary>
        internal static string Describe(EscapeSpawnConicResolution resolution, double spawnUT)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "resolved={0} body={1} soiTransitions={2} spawnUT={3:F2} sma={4:F1} ecc={5:F5} reason={6} refusalUT={7}",
                resolution.Resolved,
                resolution.BodyName ?? "(null)",
                resolution.SoiTransitions,
                spawnUT,
                resolution.Segment.semiMajorAxis,
                resolution.Segment.eccentricity,
                resolution.RefusalReason ?? "(none)",
                double.IsNaN(resolution.RefusalUT)
                    ? "n/a"
                    : resolution.RefusalUT.ToString("F2", CultureInfo.InvariantCulture));
        }
    }
}
