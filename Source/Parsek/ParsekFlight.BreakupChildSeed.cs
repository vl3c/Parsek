using System;
using UnityEngine;

namespace Parsek
{
    public partial class ParsekFlight
    {
        /// <summary>
        /// Pure decision: should the controlled-child loop in
        /// <see cref="ProcessBreakupEvent"/> skip creating a recording for a
        /// breakup child whose live <see cref="Vessel"/> is already gone?
        /// Such a child cannot be sampled or controlled after the coalescer
        /// window; even with a pre-captured snapshot it becomes a destroyed
        /// 1-point "Unknown" 0s row. The parent recording's BREAKUP branch
        /// point already records the split.
        /// </summary>
        internal static bool ShouldSkipDeadOnArrivalControlledChild(
            bool childVesselIsAlive,
            bool hasPreCapturedSnapshot)
        {
            // Kept in the signature for call-site/test forensics: the 2026-04-26
            // repro proved that snapshot presence does not make a destroyed live
            // vessel a useful controlled continuation.
            _ = hasPreCapturedSnapshot;
            return !childVesselIsAlive;
        }

        internal static string BuildDeadOnArrivalControlledChildSkipLog(
            uint pid,
            bool hasPreCapturedSnapshot)
        {
            return $"ProcessBreakupEvent: skipping dead-on-arrival controlled child " +
                $"pid={pid} (vessel destroyed before window expired, preCapturedSnapshot={hasPreCapturedSnapshot}) — " +
                "would produce an 'Unknown' 0s row with no playback value";
        }

        // Why 50m: controlled child recordings should start on the live root-part
        // position at the split, while debris still uses the pre-captured seed.
        // KSP.log:16288 in logs/2026-05-02_1132_pr708-refly-long-init-behind
        // showed a controlled child seed 1118.66m behind the live root because the
        // along-track propagated residual looked acceptable. Keep both checks:
        // direct distance catches stale init points, propagated residual catches
        // seeds that are close in time but off the live root path.
        internal const double ControlledChildLiveSeedResidualToleranceMeters = 50.0;
        internal const double MaxBreakupSeedPropagationDeltaSeconds = 1.0;
        // Production anchor lookup has a wider 1.0s search window, but authoring
        // child seed flags must stay at physics-clock equality so later coalescer
        // samples are not marked as structural-event snapshots.
        internal const double StructuralEventChildSeedUTToleranceSeconds = 1e-6;

        internal static bool ShouldFlagStructuralEventChildSeed(
            TrajectoryPoint point,
            double eventUT,
            double toleranceSeconds = StructuralEventChildSeedUTToleranceSeconds)
        {
            if (!IsFinite(point.ut) || !IsFinite(eventUT))
                return false;

            double tolerance = Math.Max(0.0, toleranceSeconds);
            return Math.Abs(point.ut - eventUT) <= tolerance;
        }

        internal static TrajectoryPoint ApplyStructuralEventFlagToChildSeed(
            TrajectoryPoint point,
            double eventUT,
            double toleranceSeconds = StructuralEventChildSeedUTToleranceSeconds)
        {
            return ShouldFlagStructuralEventChildSeed(point, eventUT, toleranceSeconds)
                ? FlightRecorder.ApplyStructuralEventFlag(point)
                : point;
        }

        internal static TrajectoryPoint? ApplyStructuralEventFlagToChildSeed(
            TrajectoryPoint? point,
            double eventUT,
            double toleranceSeconds = StructuralEventChildSeedUTToleranceSeconds)
        {
            if (!point.HasValue)
                return null;

            return ApplyStructuralEventFlagToChildSeed(point.Value, eventUT, toleranceSeconds);
        }

        internal static bool ShouldPreferLiveBreakupChildSeed(
            bool childHasController,
            bool liveVesselAvailable,
            bool capturedSeedAvailable,
            double seedLiveRootDistanceMeters,
            double propagatedSeedLiveRootResidualMeters,
            double toleranceMeters = ControlledChildLiveSeedResidualToleranceMeters)
        {
            // Debris seeds remain excluded in this PR: the controlled child is the
            // user-visible Re-Fly stage, while debris needs a separate threshold policy.
            if (!childHasController || !liveVesselAvailable || !capturedSeedAvailable)
                return false;

            double threshold = Math.Max(0.0, toleranceMeters);
            bool directSeedMissesLiveRoot =
                IsFinite(seedLiveRootDistanceMeters) && seedLiveRootDistanceMeters > threshold;
            bool propagatedSeedMissesLiveRoot =
                IsFinite(propagatedSeedLiveRootResidualMeters) && propagatedSeedLiveRootResidualMeters > threshold;

            return directSeedMissesLiveRoot || propagatedSeedMissesLiveRoot;
        }

        internal static double ComputeBreakupSeedPropagatedResidualMeters(
            Vector3d seedWorld,
            Vector3 seedVelocity,
            double seedUT,
            Vector3d liveRootWorld,
            double liveUT,
            double maxPropagationDeltaSeconds = MaxBreakupSeedPropagationDeltaSeconds)
        {
            double dt = liveUT - seedUT;
            if (!IsFinite(dt) || dt < 0 || dt > maxPropagationDeltaSeconds)
                return double.NaN;

            Vector3d velocity = new Vector3d(seedVelocity.x, seedVelocity.y, seedVelocity.z);
            Vector3d propagatedSeedWorld = seedWorld + velocity * dt;
            return (propagatedSeedWorld - liveRootWorld).magnitude;
        }
    }
}
