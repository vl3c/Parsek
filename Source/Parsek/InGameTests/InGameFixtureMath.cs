using System;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Pure fixture math shared by the FLIGHT-scene in-game tests. Deliberately free of
    /// UnityEngine and KSP types so <c>Source/Parsek.Tests</c> can cover it headlessly.
    ///
    /// <para>These helpers exist because the FLIGHT batch runs against whatever vessel the
    /// save happens to be flying. A fixture that hardcodes "the parent is a pad rocket,
    /// 100 m of trajectory is plenty, 1 mm of slop is plenty" silently stops measuring
    /// anything the moment the batch runs from an orbiting station. Both numbers below
    /// are therefore derived from the context the batch actually finds.</para>
    /// </summary>
    internal static class InGameFixtureMath
    {
        // ─────────────────────────────────────────────────────────────
        //  Scene-space float tolerance
        // ─────────────────────────────────────────────────────────────

        /// <summary>Step of the IEEE-754 single-precision mantissa: 2^-23.</summary>
        internal const double FloatMantissaStep = 1.1920928955078125e-7;

        /// <summary>
        /// Grid steps of headroom allowed when asserting on an altitude read back out of a
        /// Unity float <c>Vector3</c>. A ghost anchor crosses the float boundary more than
        /// once (<c>Vector3d</c> world position to the positioner's <c>Vector3</c> out
        /// parameter, then into <c>Transform.position</c>, then back out through the
        /// transform's matrix), so one step is not enough headroom.
        /// </summary>
        internal const double SceneFloatGridSteps = 8.0;

        /// <summary>
        /// Floor for <see cref="SceneFloatGridToleranceMeters"/>. A position sitting on the
        /// floating origin has an arbitrarily fine grid, but the round trip still costs a
        /// few sub-millimetre operations.
        /// </summary>
        internal const double MinSceneFloatToleranceMeters = 0.02;

        /// <summary>
        /// Altitude tolerance in metres for an assertion made against a world position that
        /// was stored in a Unity float <c>Vector3</c>.
        ///
        /// <para>Unity's floating origin rides the active vessel, so a scene position's
        /// magnitude is its distance from that vessel. At magnitude M a single float grid
        /// step is <c>M * 2^-23</c>: sub-millimetre for a ghost sitting next to a landed
        /// rocket, but ~2.5 cm for the terrain point under a station orbiting at 214 km.
        /// A fixed millimetre epsilon is therefore not a tolerance at all — it is a bet on
        /// where the batch runs. Scale it with the position instead.</para>
        ///
        /// <para>Callers pass the largest absolute axis of the position (the axis that sets
        /// the grid step for the whole vector). Non-finite input degrades to the floor.</para>
        /// </summary>
        internal static double SceneFloatGridToleranceMeters(double maxAbsAxisMeters)
        {
            if (double.IsNaN(maxAbsAxisMeters) || double.IsInfinity(maxAbsAxisMeters))
                return MinSceneFloatToleranceMeters;

            double gridStep = Math.Abs(maxAbsAxisMeters) * FloatMantissaStep;
            double tolerance = gridStep * SceneFloatGridSteps;
            return tolerance > MinSceneFloatToleranceMeters ? tolerance : MinSceneFloatToleranceMeters;
        }

        /// <summary>
        /// Largest share of the measured signal a tolerance may consume before the assertion
        /// stops discriminating. A quarter leaves a 4x margin.
        /// </summary>
        internal const double MaxToleranceFractionOfSignal = 0.25;

        /// <summary>
        /// True when <paramref name="toleranceMeters"/> is small enough, relative to the
        /// <paramref name="signalMeters"/> displacement being asserted, for the assertion to
        /// mean something. A caller whose scene position is far enough from the floating origin
        /// that the float grid swallows its signal must skip loudly, not pass vacuously.
        /// </summary>
        internal static bool ToleranceResolvesSignal(double toleranceMeters, double signalMeters)
        {
            if (double.IsNaN(toleranceMeters) || double.IsNaN(signalMeters)) return false;
            if (!(signalMeters > 0.0)) return false;
            return toleranceMeters <= signalMeters * MaxToleranceFractionOfSignal;
        }

        // ─────────────────────────────────────────────────────────────
        //  EVA-spawn walkback fixture sizing
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Spacing between synthetic trajectory points, in metres. Larger than
        /// <c>SpawnCollisionDetector.DefaultWalkbackStepMeters</c> (1.5 m) so each segment
        /// subdivides into several sub-steps and the metric-subdivision path in #264 is
        /// actually exercised rather than degenerating to point granularity.
        /// </summary>
        internal const double WalkbackFixtureStepMeters = 5.0;

        /// <summary>Shortest fixture trajectory, so a small parent still gets a multi-segment walkback.</summary>
        internal const double WalkbackFixtureMinLengthMeters = 120.0;

        /// <summary>
        /// Longest fixture trajectory. Every 1.5 m walkback sub-step costs a
        /// <c>Physics.OverlapBox</c>, so an unbounded fixture would hammer the physics
        /// scene. A parent too large to clear inside this budget skips instead.
        /// </summary>
        internal const double WalkbackFixtureMaxLengthMeters = 1000.0;

        /// <summary>
        /// Extra trajectory the fixture lays down BEYOND the clearance budget, giving the
        /// walkback room to be measurably wrong. The distance-from-endpoint upper bound sits
        /// partway up this runway (<see cref="MaxWalkbackTravelRunwayFraction"/>), so a
        /// correct walkback (which stops at ~budget) passes while an overshoot that runs to
        /// the trajectory start (~budget + runway) fails. Must be comfortably larger than the
        /// post-spawn settle / surface-snap / rotating-frame drift the measurement carries.
        /// </summary>
        internal const double WalkbackFixtureRunwayMeters = 80.0;

        /// <summary>
        /// Where in the runway the "did not overshoot" threshold sits, as a fraction from the
        /// clearance budget (0) to the trajectory start (1). One half leaves a full
        /// <c>0.5 * runway</c> (40 m) of headroom on each side: above the ~budget a correct
        /// walkback lands at, and below the ~budget + runway an overshoot-to-start reaches.
        /// </summary>
        internal const double MaxWalkbackTravelRunwayFraction = 0.5;

        /// <summary>
        /// Upper bound on the arc the walkback must cover before its spawn box stops
        /// touching the parent: the parent's own reach, plus the spawn box's half-extent,
        /// plus the overlap padding the collision detector adds around it. The walkback stops
        /// at the FIRST clear position, so a correct walkback's travel from the recorded
        /// endpoint is at most this.
        /// </summary>
        internal static double ResolveWalkbackClearanceBudgetMeters(
            double parentReachMeters, double spawnBoundsExtentMeters, double overlapPaddingMeters)
        {
            return NonNegative(parentReachMeters)
                 + NonNegative(spawnBoundsExtentMeters)
                 + NonNegative(overlapPaddingMeters);
        }

        /// <summary>
        /// Upper bound on how far along the trajectory (measured from the recorded endpoint) a
        /// correctly walked-back spawn may sit. The walkback's contract is "stop at the FIRST
        /// clear position", so travel beyond this means it overshot (or never ran and the
        /// spawn simply landed at the raw endpoint, ~0 travel — caught by the lower bound).
        ///
        /// <para>This is deliberately measured from the ENDPOINT, not from the parent's centre
        /// of mass: endpoint-relative travel is a surface-to-surface arc bounded by the
        /// trajectory length, whereas the straight-line distance to the parent's CoM also
        /// carries the CoM's height above the surface, which for a tall craft can exceed the
        /// trajectory length and make any CoM-relative upper bound either vacuous or
        /// false-failing.</para>
        /// </summary>
        internal static double ResolveMaxWalkbackTravelMeters(
            double parentReachMeters, double spawnBoundsExtentMeters, double overlapPaddingMeters)
        {
            double budget = ResolveWalkbackClearanceBudgetMeters(
                parentReachMeters, spawnBoundsExtentMeters, overlapPaddingMeters);
            return budget + WalkbackFixtureRunwayMeters * MaxWalkbackTravelRunwayFraction;
        }

        /// <summary>
        /// Trajectory length for the fixture: the clearance budget plus a full runway, so a
        /// correct walkback finds its clear position with room to spare and an overshoot runs
        /// out to a detectably larger travel. Clamped into
        /// [<see cref="WalkbackFixtureMinLengthMeters"/>, <see cref="WalkbackFixtureMaxLengthMeters"/>].
        /// </summary>
        internal static double ResolveWalkbackFixtureLengthMeters(
            double parentReachMeters, double spawnBoundsExtentMeters, double overlapPaddingMeters)
        {
            double needed = ResolveWalkbackClearanceBudgetMeters(
                parentReachMeters, spawnBoundsExtentMeters, overlapPaddingMeters)
                + WalkbackFixtureRunwayMeters;

            if (needed < WalkbackFixtureMinLengthMeters) needed = WalkbackFixtureMinLengthMeters;
            if (needed > WalkbackFixtureMaxLengthMeters) needed = WalkbackFixtureMaxLengthMeters;
            return needed;
        }

        /// <summary>
        /// True when the fixture trajectory carries the FULL runway past the clearance budget,
        /// so the max-travel threshold sits strictly inside the trajectory and an
        /// overshoot-to-start is detectably beyond it. False means the parent is larger than
        /// <see cref="WalkbackFixtureMaxLengthMeters"/> can accommodate with a full runway, and
        /// the test must skip rather than report a walkback exhaustion it manufactured itself,
        /// or assert an upper bound the truncated trajectory cannot support.
        /// </summary>
        internal static bool WalkbackFixtureCoversParent(
            double trajectoryLengthMeters, double clearanceBudgetMeters)
        {
            return trajectoryLengthMeters >= NonNegative(clearanceBudgetMeters) + WalkbackFixtureRunwayMeters;
        }

        /// <summary>
        /// Number of trajectory points at <paramref name="stepMeters"/> spacing covering
        /// <paramref name="lengthMeters"/>. Always at least two so the walkback has a segment
        /// to subdivide.
        /// </summary>
        internal static int ResolveWalkbackFixturePointCount(double lengthMeters, double stepMeters)
        {
            if (!(stepMeters > 0.0) || double.IsNaN(lengthMeters))
                return 2;

            int steps = (int)Math.Ceiling(NonNegative(lengthMeters) / stepMeters);
            if (steps < 1) steps = 1;
            return steps + 1;
        }

        private static double NonNegative(double value)
        {
            return double.IsNaN(value) || value < 0.0 ? 0.0 : value;
        }
    }
}
