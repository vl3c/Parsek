using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    internal enum AnchorCandidateSource
    {
        Live,
        Ghost
    }

    internal readonly struct RecordingAnchorCandidate
    {
        public readonly string RecordingId;
        public readonly Vector3d WorldPos;
        public readonly Quaternion WorldRotation;
        public readonly AnchorCandidateSource Source;
        public readonly uint DiagnosticPid;
        public readonly int GhostIndex;
        public readonly bool IsSealed;
        public readonly bool IsSameReplayPoint;
        public readonly bool IsSameVesselLineage;

        public Vector3d WorldPosition => WorldPos;

        public RecordingAnchorCandidate(
            string recordingId,
            Vector3d worldPos,
            Quaternion worldRotation,
            AnchorCandidateSource source,
            uint diagnosticPid = 0u,
            int ghostIndex = -1,
            bool isSealed = false,
            bool isSameReplayPoint = false,
            bool isSameVesselLineage = false)
        {
            RecordingId = recordingId;
            WorldPos = worldPos;
            WorldRotation = worldRotation;
            Source = source;
            DiagnosticPid = diagnosticPid;
            GhostIndex = ghostIndex;
            IsSealed = isSealed;
            IsSameReplayPoint = isSameReplayPoint;
            IsSameVesselLineage = isSameVesselLineage;
        }
    }

    /// <summary>
    /// Detects when the focused vessel is near a pre-existing persistent vessel
    /// that could serve as a RELATIVE reference frame anchor.
    /// A "pre-existing" vessel is one NOT part of the current recording tree --
    /// it exists independently in the game state (stations, bases, other missions).
    /// </summary>
    internal static class AnchorDetector
    {
        // Transition thresholds (meters)
        internal const double RelativeEntryDistance = DistanceThresholds.RelativeFrame.EntryMeters;
        internal const double RelativeExitDistance = DistanceThresholds.RelativeFrame.ExitMeters;
        internal const double DockingApproachDistance = DistanceThresholds.RelativeFrame.DockingApproachMeters;

        /// <summary>
        /// Scans for the nearest pre-existing vessel within RELATIVE range.
        /// Returns the anchor vessel PID and distance, or (0, MaxValue) if none found.
        ///
        /// Pure static method -- vessel list and tree membership injected for testability.
        /// </summary>
        /// <param name="focusedVesselPid">PID of the focused vessel</param>
        /// <param name="focusedPosition">World position of focused vessel</param>
        /// <param name="vesselInfos">List of (pid, worldPosition) for all loaded vessels</param>
        /// <param name="treeVesselPids">Set of PIDs that are part of the current recording tree (excluded from anchor candidates)</param>
        /// <returns>(anchorPid, distance) -- anchorPid=0 if no anchor found</returns>
        internal static (uint anchorPid, double distance) FindNearestAnchor(
            uint focusedVesselPid,
            Vector3d focusedPosition,
            List<(uint pid, Vector3d position)> vesselInfos,
            HashSet<uint> treeVesselPids)
        {
            uint bestPid = 0;
            double bestDistance = double.MaxValue;

            for (int i = 0; i < vesselInfos.Count; i++)
            {
                uint pid = vesselInfos[i].pid;
                if (pid == focusedVesselPid) continue;
                if (treeVesselPids != null && treeVesselPids.Contains(pid)) continue;

                double dist = Vector3d.Distance(focusedPosition, vesselInfos[i].position);
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    bestPid = pid;
                }
            }

            if (bestPid != 0)
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.VerboseRateLimited("Anchor", "find-nearest",
                    $"FindNearestAnchor: anchorPid={bestPid} dist={bestDistance.ToString("F1", ic)}m " +
                    $"focusedPid={focusedVesselPid} candidates={vesselInfos.Count}");
            }

            return (bestPid, bestDistance);
        }

        internal static (RecordingAnchorCandidate candidate, double distance, bool found) FindNearestRecordingAnchor(
            string focusedRecordingId,
            uint focusedVesselPid,
            Vector3d focusedPosition,
            IReadOnlyList<RecordingAnchorCandidate> candidates,
            double maxDistanceExclusive = double.MaxValue)
        {
            RecordingAnchorCandidate best = default;
            double bestDistance = double.MaxValue;
            bool found = false;

            if (candidates == null || candidates.Count == 0)
                return (best, bestDistance, false);

            for (int i = 0; i < candidates.Count; i++)
            {
                RecordingAnchorCandidate candidate = candidates[i];
                if (string.IsNullOrWhiteSpace(candidate.RecordingId))
                    continue;
                if (string.Equals(candidate.RecordingId, focusedRecordingId, StringComparison.Ordinal))
                    continue;
                if (focusedVesselPid != 0u
                    && candidate.DiagnosticPid != 0u
                    && candidate.DiagnosticPid == focusedVesselPid)
                {
                    continue;
                }

                double distance = Vector3d.Distance(focusedPosition, candidate.WorldPos);
                if (distance >= maxDistanceExclusive)
                    continue;

                if (!found || IsBetterRecordingAnchor(candidate, distance, best, bestDistance))
                {
                    best = candidate;
                    bestDistance = distance;
                    found = true;
                }
            }

            if (found)
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.VerboseRateLimited("Anchor", "find-nearest-recording",
                    $"FindNearestRecordingAnchor: recordingId={best.RecordingId} dist={bestDistance.ToString("F1", ic)}m " +
                    $"focusedRecordingId={focusedRecordingId ?? "(null)"} source={best.Source} ghostIndex={best.GhostIndex} " +
                    $"sealed={best.IsSealed} affinity={RecordingAnchorAffinityRank(best)} candidates={candidates.Count}");
            }

            return (best, bestDistance, found);
        }

        internal static bool TryCreateRecordingAnchorCandidate(
            Recording focusRecording,
            Recording candidateRecording,
            Vector3d worldPos,
            Quaternion worldRotation,
            AnchorCandidateSource source,
            uint diagnosticPid,
            int ghostIndex,
            out RecordingAnchorCandidate candidate)
        {
            candidate = default;
            if (!IsRecordingAnchorEligible(focusRecording, candidateRecording))
                return false;
            if (!IsRecordingAnchorDAGOrderEligible(focusRecording, candidateRecording))
                return false;

            candidate = new RecordingAnchorCandidate(
                candidateRecording.RecordingId,
                worldPos,
                TrajectoryMath.SanitizeQuaternion(worldRotation),
                source,
                diagnosticPid,
                ghostIndex,
                IsSealedRecordingAnchor(candidateRecording),
                SharesReplayPoint(focusRecording, candidateRecording),
                SharesVesselLineage(focusRecording, candidateRecording));
            return true;
        }

        internal static bool IsRecordingAnchorEligible(
            Recording focusRecording,
            Recording candidateRecording)
        {
            if (focusRecording == null || candidateRecording == null)
                return false;
            if (string.IsNullOrWhiteSpace(focusRecording.RecordingId)
                || string.IsNullOrWhiteSpace(candidateRecording.RecordingId))
            {
                return false;
            }
            if (string.Equals(
                    focusRecording.RecordingId,
                    candidateRecording.RecordingId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            // Loop-anchored recordings still depend on live anchor PIDs by design.
            // They are not valid roots for the non-loop recorded-anchor DAG.
            if (candidateRecording.LoopAnchorVesselId != 0u)
                return false;

            // PR 3b regression-fix (2026-05-07): debris recordings are not eligible
            // as anchor candidates for OTHER recordings. Pre-PR-3b debris was
            // robust as an anchor (often Absolute by hysteresis, lifetime tied
            // only to the debris vessel). Post-PR-3b debris is always-Relative-
            // to-parent (Decision §5 Option C) and is ended by `CheckDebrisTTL`
            // when its own parent recording becomes closed/superseded
            // (Decision §10) — so any non-debris recording that picked a debris
            // as a live anchor would lose resolvability the moment the debris's
            // parent gets superseded by a Re-Fly. Observed in
            // `logs/2026-05-07_2157_refly-debris-regression`: a controlled
            // child probe recording was anchored to a sibling debris at 8m,
            // then after the upper-stage Re-Fly the debris was TTL-ended, the
            // probe's Relative section past the debris's end UT became
            // unresolvable, and playback fell back to body-fixed primary with a
            // visibly unstable ghost. Excluding debris from candidacy avoids
            // creating these fragile cross-recording anchors at recording time.
            //
            // Self-reference: a debris recording's OWN contract path
            // (`BackgroundRecorder.UpdateBackgroundAnchorDetection` early-return
            // when `treeRec.DebrisParentRecordingId != null`) bypasses this
            // helper entirely and pins the anchor to the parent recording, so
            // a debris focus never reaches this rejection path. Two-debris
            // anchoring (debris-A as candidate for debris-B) is also impossible
            // by construction: debris-B's contract path forces it to anchor
            // to its own parent (a non-debris parent recording), bypassing the
            // candidate scan.
            if (candidateRecording.IsDebris)
                return false;

            return true;
        }

        internal static bool IsRecordingAnchorDAGOrderEligible(
            Recording focusRecording,
            Recording candidateRecording)
        {
            if (focusRecording == null || candidateRecording == null)
                return false;
            if (!SameNonEmpty(focusRecording.TreeId, candidateRecording.TreeId))
                return true;
            if (focusRecording.TreeOrder < 0 || candidateRecording.TreeOrder < 0)
                return false;

            return candidateRecording.TreeOrder < focusRecording.TreeOrder;
        }

        internal static bool IsSealedRecordingAnchor(Recording recording)
        {
            return recording != null && recording.MergeState == MergeState.Immutable;
        }

        internal static bool SharesReplayPoint(Recording a, Recording b)
        {
            if (a == null || b == null)
                return false;

            return SameNonEmpty(a.ParentBranchPointId, b.ParentBranchPointId)
                || SameNonEmpty(a.ParentBranchPointId, b.ChildBranchPointId)
                || SameNonEmpty(a.ChildBranchPointId, b.ParentBranchPointId)
                || SameNonEmpty(a.ChildBranchPointId, b.ChildBranchPointId);
        }

        internal static bool SharesVesselLineage(Recording a, Recording b)
        {
            if (a == null || b == null)
                return false;

            if (a.VesselPersistentId != 0u && a.VesselPersistentId == b.VesselPersistentId)
                return true;
            if (SameNonEmpty(a.ChainId, b.ChainId))
                return true;
            return SameNonEmpty(a.VesselName, b.VesselName);
        }

        private static bool IsBetterRecordingAnchor(
            RecordingAnchorCandidate candidate,
            double distance,
            RecordingAnchorCandidate best,
            double bestDistance)
        {
            if (candidate.IsSealed != best.IsSealed)
                return candidate.IsSealed;

            int candidateAffinity = RecordingAnchorAffinityRank(candidate);
            int bestAffinity = RecordingAnchorAffinityRank(best);
            if (candidateAffinity != bestAffinity)
                return candidateAffinity > bestAffinity;

            int distanceCompare = distance.CompareTo(bestDistance);
            if (distanceCompare != 0)
                return distanceCompare < 0;

            int recordingIdCompare = string.Compare(candidate.RecordingId, best.RecordingId, StringComparison.Ordinal);
            if (recordingIdCompare != 0)
                return recordingIdCompare < 0;

            int sourceCompare = ((int)candidate.Source).CompareTo((int)best.Source);
            if (sourceCompare != 0)
                return sourceCompare < 0;

            return candidate.GhostIndex < best.GhostIndex;
        }

        internal static int RecordingAnchorAffinityRank(RecordingAnchorCandidate candidate)
        {
            int rank = 0;
            if (candidate.IsSameReplayPoint) rank++;
            if (candidate.IsSameVesselLineage) rank++;
            return rank;
        }

        internal static double RelativeFrameRangeLimit(bool currentlyRelative)
        {
            return currentlyRelative
                ? RelativeExitDistance
                : RelativeEntryDistance;
        }

        private static bool SameNonEmpty(string a, string b)
        {
            return !string.IsNullOrEmpty(a)
                && !string.IsNullOrEmpty(b)
                && string.Equals(a, b, StringComparison.Ordinal);
        }

        /// <summary>
        /// Determines whether the focused vessel should be in RELATIVE reference frame
        /// based on anchor detection and hysteresis.
        /// </summary>
        internal static bool ShouldUseRelativeFrame(
            double anchorDistance,
            bool currentlyRelative)
        {
            if (!currentlyRelative)
            {
                // Not currently relative -- enter at physics bubble edge
                bool enter = anchorDistance < RelativeFrameRangeLimit(currentlyRelative);
                if (enter)
                {
                    var ic = CultureInfo.InvariantCulture;
                    ParsekLog.Info("Anchor",
                        $"RELATIVE entry: dist={anchorDistance.ToString("F1", ic)}m < {RelativeEntryDistance.ToString("F0", ic)}m");
                }
                return enter;
            }
            else
            {
                // Currently relative -- exit with hysteresis
                bool stay = anchorDistance < RelativeFrameRangeLimit(currentlyRelative);
                if (!stay)
                {
                    var ic = CultureInfo.InvariantCulture;
                    ParsekLog.Info("Anchor",
                        $"RELATIVE exit: dist={anchorDistance.ToString("F1", ic)}m >= {RelativeExitDistance.ToString("F0", ic)}m");
                }
                return stay;
            }
        }

        /// <summary>
        /// Returns true if the anchor is within docking approach range (&lt;200m).
        /// Used for logging/diagnostics -- the sample rate is already handled
        /// by ProximityRateSelector.
        /// </summary>
        internal static bool IsInDockingApproach(double anchorDistance)
        {
            bool inRange = anchorDistance < DockingApproachDistance;
            if (inRange)
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.VerboseRateLimited("Anchor", "dock-approach",
                    $"Docking approach detected: dist={anchorDistance.ToString("F1", ic)}m < {DockingApproachDistance.ToString("F0", ic)}m");
            }
            return inRange;
        }
    }
}
