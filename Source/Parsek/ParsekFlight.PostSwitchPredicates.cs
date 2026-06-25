using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    public partial class ParsekFlight
    {
        internal static bool HasMeaningfulLandedMotionChange(
            double distanceDeltaMeters,
            double speedMetersPerSecond)
        {
            return distanceDeltaMeters >= PostSwitchLandedMotionDistanceThreshold
                || speedMetersPerSecond >= PostSwitchLandedMotionSpeedThreshold;
        }

        internal static float ComputePostSwitchAttitudeDeltaDegrees(
            Quaternion baselineWorldRotation,
            Quaternion currentWorldRotation)
        {
            return Quaternion.Angle(
                TrajectoryMath.NormalizeQuaternionForComparison(baselineWorldRotation),
                TrajectoryMath.NormalizeQuaternionForComparison(currentWorldRotation));
        }

        internal static bool HasMeaningfulAttitudeChange(
            uint armedVesselPid,
            uint activeVesselPid,
            bool hasBaselineRotation,
            Quaternion baselineWorldRotation,
            Quaternion currentWorldRotation,
            float thresholdDegrees = PostSwitchAttitudeChangeThresholdDegrees)
        {
            if (!hasBaselineRotation || armedVesselPid == 0 || activeVesselPid == 0
                || armedVesselPid != activeVesselPid)
            {
                return false;
            }

            return ComputePostSwitchAttitudeDeltaDegrees(
                baselineWorldRotation,
                currentWorldRotation) >= thresholdDegrees;
        }

        internal static bool HasMeaningfulOrbitChange(
            PostSwitchOrbitSnapshot baseline,
            PostSwitchOrbitSnapshot current)
        {
            if (!baseline.IsValid || !current.IsValid)
                return false;

            if (!string.Equals(baseline.BodyName, current.BodyName, StringComparison.Ordinal))
                return true;

            if (Math.Abs(current.SemiMajorAxis - baseline.SemiMajorAxis) >=
                PostSwitchOrbitSemiMajorAxisThresholdMeters)
                return true;
            if (Math.Abs(current.Eccentricity - baseline.Eccentricity) >=
                PostSwitchOrbitEccentricityThreshold)
                return true;
            if (NormalizeAngleDeltaDegrees(
                    current.Inclination,
                    baseline.Inclination) >= PostSwitchOrbitAngleThresholdDegrees)
                return true;
            if (NormalizeAngleDeltaDegrees(
                    current.LongitudeOfAscendingNode,
                    baseline.LongitudeOfAscendingNode) >= PostSwitchOrbitAngleThresholdDegrees)
                return true;
            return NormalizeAngleDeltaDegrees(
                current.ArgumentOfPeriapsis,
                baseline.ArgumentOfPeriapsis) >= PostSwitchOrbitAngleThresholdDegrees;
        }

        internal static bool HasMeaningfulCrewDelta(Dictionary<string, int> delta)
        {
            if (delta == null) return false;
            foreach (var entry in delta)
            {
                if (entry.Value != 0)
                    return true;
            }
            return false;
        }

        internal static int CountMeaningfulCrewDelta(Dictionary<string, int> delta)
        {
            if (delta == null) return 0;
            int count = 0;
            foreach (var entry in delta)
            {
                if (entry.Value != 0)
                    count++;
            }
            return count;
        }

        internal static bool HasMeaningfulResourceDelta(
            Dictionary<string, double> delta,
            double epsilon = PostSwitchResourceDeltaEpsilon)
        {
            if (delta == null) return false;
            foreach (var entry in delta)
            {
                if (Math.Abs(entry.Value) > epsilon)
                    return true;
            }
            return false;
        }

        internal static int CountMeaningfulResourceDelta(
            Dictionary<string, double> delta,
            double epsilon = PostSwitchResourceDeltaEpsilon)
        {
            if (delta == null) return 0;
            int count = 0;
            foreach (var entry in delta)
            {
                if (Math.Abs(entry.Value) > epsilon)
                    count++;
            }
            return count;
        }

        internal static bool HasMeaningfulInventoryDelta(Dictionary<string, InventoryItem> delta)
        {
            if (delta == null) return false;
            foreach (var entry in delta)
            {
                if (entry.Value.count != 0 || entry.Value.slotsTaken != 0)
                    return true;
            }
            return false;
        }

        internal static int CountMeaningfulInventoryDelta(Dictionary<string, InventoryItem> delta)
        {
            if (delta == null) return 0;
            int count = 0;
            foreach (var entry in delta)
            {
                if (entry.Value.count != 0 || entry.Value.slotsTaken != 0)
                    count++;
            }
            return count;
        }

        internal static bool HasMeaningfulPartStateTokenChange(
            ICollection<string> baselineTokens,
            ICollection<string> currentTokens)
        {
            int baselineCount = baselineTokens != null ? baselineTokens.Count : 0;
            int currentCount = currentTokens != null ? currentTokens.Count : 0;
            if (baselineCount != currentCount)
                return true;
            if (baselineCount == 0)
                return false;

            var baselineSet = baselineTokens as HashSet<string> ?? new HashSet<string>(baselineTokens);
            foreach (string token in currentTokens)
            {
                if (!baselineSet.Contains(token))
                    return true;
            }
            return false;
        }

        internal static int CountPartStateTokenDelta(
            ICollection<string> baselineTokens,
            ICollection<string> currentTokens)
        {
            int baselineCount = baselineTokens != null ? baselineTokens.Count : 0;
            int currentCount = currentTokens != null ? currentTokens.Count : 0;
            if (baselineCount == 0)
                return currentCount;
            if (currentCount == 0)
                return baselineCount;

            var baselineSet = baselineTokens as HashSet<string> ?? new HashSet<string>(baselineTokens);
            var currentSet = currentTokens as HashSet<string> ?? new HashSet<string>(currentTokens);
            int delta = 0;
            foreach (string token in currentSet)
            {
                if (!baselineSet.Contains(token))
                    delta++;
            }
            foreach (string token in baselineSet)
            {
                if (!currentSet.Contains(token))
                    delta++;
            }
            return delta;
        }

        internal static string FormatPostSwitchManifestDeltaSummary(
            uint vesselPid,
            int crewDeltaKeys,
            int resourceDeltaKeys,
            int inventoryDeltaKeys,
            int partStateTokenDelta,
            bool crewChanged,
            bool resourceChanged,
            bool partStateChanged,
            double nextEvaluationUt)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Post-switch manifest delta: pid={0} crewChanged={1} resourceChanged={2} partStateChanged={3} crewDeltaKeys={4} resourceDeltaKeys={5} inventoryDeltaKeys={6} partStateTokenDelta={7} nextEvalUT={8:F1}",
                vesselPid,
                crewChanged,
                resourceChanged,
                partStateChanged,
                FormatPostSwitchManifestDeltaCount(crewDeltaKeys),
                FormatPostSwitchManifestDeltaCount(resourceDeltaKeys),
                FormatPostSwitchManifestDeltaCount(inventoryDeltaKeys),
                FormatPostSwitchManifestDeltaCount(partStateTokenDelta),
                nextEvaluationUt);
        }

        internal static string FormatSplitSkipSummary(
            string source,
            string reason,
            string activeRecordingId,
            uint sourcePid,
            uint targetPid,
            bool pendingSplitInProgress)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0}: split path skipped reason={1} activeRec={2} sourcePid={3} targetPid={4} pendingSplitInProgress={5}",
                string.IsNullOrEmpty(source) ? "(unknown)" : source,
                string.IsNullOrEmpty(reason) ? "unspecified" : reason,
                string.IsNullOrEmpty(activeRecordingId) ? "(none)" : activeRecordingId,
                sourcePid,
                targetPid,
                pendingSplitInProgress);
        }

        internal static bool HasMeaningfulPartStateChange(IEnumerable<PartEventType> changedTypes)
        {
            if (changedTypes == null) return false;
            foreach (PartEventType type in changedTypes)
            {
                if (GhostingTriggerClassifier.IsGhostingTrigger(type))
                    return true;
            }
            return false;
        }

        internal static PostSwitchAutoRecordTrigger EvaluatePostSwitchAutoRecordTrigger(
            bool engineTriggered,
            bool rcsTriggered,
            bool attitudeChanged,
            bool crewChanged,
            bool resourceChanged,
            bool partStateChanged,
            bool landedMotionChanged,
            bool orbitChanged)
        {
            if (engineTriggered)
                return PostSwitchAutoRecordTrigger.EngineActivity;
            if (rcsTriggered)
                return PostSwitchAutoRecordTrigger.SustainedRcsActivity;
            if (attitudeChanged)
                return PostSwitchAutoRecordTrigger.AttitudeChange;
            if (crewChanged)
                return PostSwitchAutoRecordTrigger.CrewChange;
            if (resourceChanged)
                return PostSwitchAutoRecordTrigger.ResourceChange;
            if (partStateChanged)
                return PostSwitchAutoRecordTrigger.PartStateChange;
            if (landedMotionChanged)
                return PostSwitchAutoRecordTrigger.LandedMotion;
            if (orbitChanged)
                return PostSwitchAutoRecordTrigger.OrbitChange;
            return PostSwitchAutoRecordTrigger.None;
        }

        internal static PostSwitchAutoRecordStartDecision EvaluatePostSwitchAutoRecordStartDecision(
            uint armedVesselPid,
            uint activeVesselPid,
            bool hasActiveTree,
            bool activeVesselTrackedInBackground,
            bool canRestorePendingTrackedTree,
            bool suppressStart)
        {
            if (suppressStart || armedVesselPid == 0 || activeVesselPid == 0
                || armedVesselPid != activeVesselPid)
                return PostSwitchAutoRecordStartDecision.None;
            if (hasActiveTree && activeVesselTrackedInBackground)
                return PostSwitchAutoRecordStartDecision.PromoteTrackedRecording;
            if (canRestorePendingTrackedTree)
                return PostSwitchAutoRecordStartDecision.RestoreAndPromoteTrackedRecording;
            return PostSwitchAutoRecordStartDecision.StartFreshRecording;
        }
    }
}
