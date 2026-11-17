using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // Recorded relative anchor pose resolution.
    internal sealed partial class ParsekFlightGhostPositioner
    {
        internal bool TryResolveRecordedRelativeAnchorPose(
            RelativeSectionPlaybackTarget target,
            double targetUT,
            out ParsekFlight.RelativeAnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            return TryResolveRecordedRelativeAnchorPose(
                target.RecordingId,
                target.AnchorRecordingId,
                target.SectionIndex,
                targetUT,
                out pose,
                out failure);
        }

        internal bool TryResolveRecordedRelativeAnchorPose(
            string recordingId,
            string anchorRecordingId,
            int sectionIndex,
            double targetUT,
            out ParsekFlight.RelativeAnchorPose pose)
        {
            return TryResolveRecordedRelativeAnchorPose(
                recordingId,
                anchorRecordingId,
                sectionIndex,
                targetUT,
                out pose,
                out _);
        }

        internal bool TryResolveRecordedRelativeAnchorPose(
            string recordingId,
            string anchorRecordingId,
            int sectionIndex,
            double targetUT,
            out ParsekFlight.RelativeAnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            pose = default(ParsekFlight.RelativeAnchorPose);
            failure = default;
            if (string.IsNullOrWhiteSpace(anchorRecordingId))
            {
                failure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.PreconditionFailed,
                    "anchor-recording-id-missing",
                    recordingId,
                    anchorRecordingId,
                    targetUT,
                    sectionIndex);
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "recorded-anchor-pose",
                    "anchor-recording-id-missing",
                    0u,
                    anchorRecordingId,
                    success: false,
                    fromRecordedTrajectory: true,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: Vector3d.zero,
                    outputPosition: Vector3d.zero);
                ParsekLog.WarnRateLimited(
                    "Anchor",
                    "recorded-relative-missing-anchor-recording-id|" +
                    (recordingId ?? "(none)") + "|" +
                    sectionIndex.ToString(CultureInfo.InvariantCulture),
                    $"recorded-relative-unresolved: reason=anchor-recording-id-missing " +
                    $"recordingId={recordingId ?? "(none)"} " +
                    $"sectionIndex={sectionIndex} ut={targetUT.ToString("R", CultureInfo.InvariantCulture)}",
                    5.0);
                return false;
            }

            anchorRecordingId = anchorRecordingId.Trim();
            if (!TryFindRelativeAnchorFocusTree(recordingId, out RecordingTree focusTree))
            {
                failure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.Other,
                    "focus-tree-missing",
                    recordingId,
                    anchorRecordingId,
                    targetUT,
                    sectionIndex);
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "recorded-anchor-pose",
                    "focus-tree-missing",
                    0u,
                    anchorRecordingId,
                    success: false,
                    fromRecordedTrajectory: true,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: Vector3d.zero,
                    outputPosition: Vector3d.zero);
                ParsekLog.WarnRateLimited(
                    "Anchor",
                    "recorded-relative-focus-tree-missing|" + (recordingId ?? "(none)"),
                    $"recorded-relative-unresolved: reason=focus-tree-missing " +
                    $"recordingId={recordingId ?? "(none)"} " +
                    $"anchorRecordingId={anchorRecordingId} " +
                    $"ut={targetUT.ToString("R", CultureInfo.InvariantCulture)}",
                    5.0);
                return false;
            }

            var context = ParsekFlight.BuildFlightRelativeAnchorResolverContext(
                focusTree,
                recordingId,
                ParsekScenario.Instance?.ActiveReFlySessionMarker,
                catalog);
            HashSet<string> visited = recordedRelativeAnchorVisited;
            visited.Clear();
            bool resolved;
            AnchorPose anchorPose;
            try
            {
                resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                    context,
                    anchorRecordingId,
                    targetUT,
                    visited,
                    out anchorPose,
                    out failure);
            }
            finally
            {
                visited.Clear();
            }

            if (!resolved)
            {
                if (!failure.HasFailure)
                {
                    failure = RelativeAnchorResolveFailure.Create(
                        RelativeAnchorResolveOutcome.Other,
                        "resolver-unresolved",
                        recordingId,
                        anchorRecordingId,
                        targetUT,
                        sectionIndex);
                }
                string reason = RelativeAnchorResolveFailure.ReasonOrFallback(
                    failure,
                    "resolver-unresolved");
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "recorded-anchor-pose",
                    reason,
                    0u,
                    anchorRecordingId,
                    success: false,
                    fromRecordedTrajectory: true,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: Vector3d.zero,
                    outputPosition: Vector3d.zero);
                return false;
            }

            pose.worldPos = anchorPose.WorldPos;
            pose.worldRotation = anchorPose.WorldRotation;
            pose.fromRecordedTrajectory = true;
            GhostRenderTrace.EmitRelativeResolver(
                recordingId,
                -1,
                Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                targetUT,
                "recorded-anchor-pose",
                "resolved",
                0u,
                anchorRecordingId,
                success: true,
                fromRecordedTrajectory: true,
                anchorPosition: pose.worldPos,
                anchorRotation: pose.worldRotation,
                localOffset: Vector3d.zero,
                outputPosition: pose.worldPos);
            return true;
        }

        internal bool TryFindRelativeAnchorFocusTree(
            string recordingId,
            out RecordingTree focusTree)
        {
            return catalog.TryFindTreeHolding(
                recordingId,
                CatalogTreeOrder.CommittedFirstContainsKey,
                out focusTree);
        }
    }
}
