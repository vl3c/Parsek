using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    // Relative-section, body-fixed and orbit-tail positioning, and parent-anchored debris coverage retirement.
    internal partial class GhostPlaybackEngine
    {
        internal static bool TryGetRelativeSectionAtUT(
            IPlaybackTrajectory traj,
            double playbackUT,
            out RelativeSectionPlaybackTarget target)
        {
            target = default;
            if (traj?.TrackSections == null || traj.TrackSections.Count == 0)
                return false;

            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, playbackUT);
            if (sectionIdx < 0)
                return false;

            TrackSection section = traj.TrackSections[sectionIdx];
            if (section.referenceFrame != ReferenceFrame.Relative)
                return false;

            target = new RelativeSectionPlaybackTarget(
                traj.RecordingId,
                sectionIdx,
                section);

            if (!target.HasAnchorRecordingId)
            {
                string key =
                    "relative-section-missing-anchor-recording-id|" +
                    (traj.RecordingId ?? "(none)") + "|" +
                    sectionIdx.ToString(CultureInfo.InvariantCulture);
                ParsekLog.WarnRateLimited(
                    "Engine",
                    key,
                    $"RELATIVE section missing anchorRecordingId: " +
                    $"recordingId={traj.RecordingId ?? "(none)"} " +
                    $"sectionIndex={sectionIdx} " +
                    $"sectionUT=[{section.startUT.ToString("F2", CultureInfo.InvariantCulture)}," +
                    $"{section.endUT.ToString("F2", CultureInfo.InvariantCulture)}]",
                    10.0);
            }
            return true;
        }

        internal static bool ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
            IPlaybackTrajectory traj,
            double playbackUT)
        {
            return ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                traj, playbackUT, out _);
        }

        internal static bool ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
            IPlaybackTrajectory traj,
            double playbackUT,
            out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic)
        {
            if (!ShouldApplyOrdinaryParentAnchoredDebrisCoveragePolicy(traj, playbackUT))
            {
                diagnostic = default;
                return false;
            }

            return DebrisRelativePlaybackPolicy.ShouldRetireOutsideAuthoredRelativeCoverage(
                traj, playbackUT, out diagnostic);
        }

        internal static bool ShouldApplyOrdinaryParentAnchoredDebrisCoveragePolicy(
            IPlaybackTrajectory traj,
            double playbackUT)
        {
            return DebrisRelativePlaybackPolicy.ShouldRetireOnRecordedParentAnchorMiss(traj)
                && traj.LoopAnchorVesselId == 0u
                && !ShouldUseLoopAnchoredDebrisChain(traj, playbackUT);
        }

        internal static bool ShouldSkipRecordedRelativeResolverForAuthoredFrameGap(
            IPlaybackTrajectory traj,
            double playbackUT,
            out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic)
        {
            if (!ShouldApplyOrdinaryParentAnchoredDebrisCoveragePolicy(traj, playbackUT))
            {
                diagnostic = default;
                return false;
            }

            return DebrisRelativePlaybackPolicy.ShouldSkipRecordedRelativeResolverForAuthoredFrameGap(
                traj,
                playbackUT,
                out diagnostic);
        }

        private bool TryPositionRelativeSectionAtPlaybackUT(
            int index,
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double playbackUT,
            bool suppressFx,
            out bool usedBodyFixedPrimary)
        {
            usedBodyFixedPrimary = false;
            // Parent-anchored gate is now `ParentAnchorRecordingId != null` alone.
            // Both genuine debris (IsDebris=true) and controlled-decoupled children
            // (IsDebris=false) take this path while inside a Relative section against
            // their parent. Post-window Absolute sections bypass this method entirely
            // through TryGetRelativeSectionAtUT returning false for non-Relative
            // sections (see plan section 7).
            bool parentAnchored = IsParentAnchoredBodyFixedPrimaryCandidate(traj);
            bool loopAnchoredDebrisChain = parentAnchored
                && ShouldUseLoopAnchoredDebrisChain(traj, playbackUT);
            DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic = default;
            if (!loopAnchoredDebrisChain
                && ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                    traj,
                    playbackUT,
                    out diagnostic))
            {
                MarkParentAnchoredDebrisCoverageRetired(
                    index,
                    traj,
                    state,
                    playbackUT,
                    "GhostPlaybackEngine.TryPositionRelativeSectionAtPlaybackUT",
                    diagnostic);
                return true;
            }

            if (!TryGetRelativeSectionAtUT(traj, playbackUT, out RelativeSectionPlaybackTarget target))
                return false;

            if (parentAnchored
                && !loopAnchoredDebrisChain
                && TryPositionBodyFixedPrimary(
                    index,
                    traj,
                    state,
                    playbackUT,
                    "relative-section-direct"))
            {
                usedBodyFixedPrimary = true;
                return true;
            }
            if (parentAnchored && !loopAnchoredDebrisChain)
            {
                diagnostic.Reason = "body-fixed-primary-position-failed";
                MarkParentAnchoredDebrisCoverageRetired(
                    index,
                    traj,
                    state,
                    playbackUT,
                    "GhostPlaybackEngine.TryPositionRelativeSectionAtPlaybackUT",
                    diagnostic);
                return true;
            }

            positioner.InterpolateAndPositionRelative(
                index, traj, state, playbackUT, suppressFx, target);
            // Body-fixed fallback when the relative anchor could not resolve (see the matching block
            // in PositionLoopAtPlaybackUT). Fires for parent-anchored loop-chain debris AND for a
            // top-level vessel whose active section is Relative against a sibling that ended early
            // (e.g. an upper stage recorded relative to a probe that crashed). TryPositionBodyFixedPrimary
            // is self-gating (Relative section + covering body-fixed frames), so it is inert otherwise.
            if (state != null
                && state.anchorRetiredThisFrame
                && TryPositionBodyFixedPrimary(
                    index,
                    traj,
                    state,
                    playbackUT,
                    "relative-section-anchor-retired-fallback"))
            {
                usedBodyFixedPrimary = true;
            }
            return true;
        }

        private bool TryPositionBodyFixedPrimary(
            int index,
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double playbackUT,
            string phase)
        {
            if (positioner == null || traj == null || state?.ghost == null)
                return false;

            // The active Relative section determines which bodyFixedFrames list
            // we lerp through. Other reference frames have no body-fixed contract
            // (Absolute sections already render world positions; Orbital
            // checkpoints are computed from orbit state).
            int sectionIndex = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, playbackUT);
            if (sectionIndex < 0)
                return false;
            TrackSection section = traj.TrackSections[sectionIndex];
            if (!ParsekFlight.BodyFixedPrimaryCoversPlaybackUT(
                    section, playbackUT, out _, out _))
            {
                return false;
            }

            var target = new RelativeSectionPlaybackTarget(
                traj.RecordingId,
                sectionIndex,
                section);
            // Note: positioner.TryPositionFromBodyFixedPrimary calls
            // state.SetInterpolated(...) internally on success (matching the
            // existing positioner-with-state contract); engine does not need
            // to invoke it separately. anchorRetiredThisFrame is intentionally
            // NOT set so the post-position pipeline runs Activate /
            // TrackGhostAppearance for this frame -- the mesh stays visible.
            if (!positioner.TryPositionFromBodyFixedPrimary(
                    index, traj, state, playbackUT, target,
                    out double bracketBeforeUT,
                    out double bracketAfterUT))
            {
                return false;
            }

            state.anchorRetiredThisFrame = false;

            ParsekLog.VerboseRateLimited(
                "Anchor",
                "body-fixed-primary-route-" + index.ToString(CultureInfo.InvariantCulture),
                "body-fixed-primary-route: ghost #"
                + index.ToString(CultureInfo.InvariantCulture)
                + " \"" + (traj?.VesselName ?? state?.vesselName ?? "(unknown)") + "\" "
                + " phase=" + (phase ?? "(unknown)")
                + " playbackUT=" + playbackUT.ToString("R", CultureInfo.InvariantCulture)
                + " bracketBeforeUT=" + bracketBeforeUT.ToString("R", CultureInfo.InvariantCulture)
                + " bracketAfterUT=" + bracketAfterUT.ToString("R", CultureInfo.InvariantCulture)
                + " sectionIndex=" + sectionIndex.ToString(CultureInfo.InvariantCulture)
                + " bodyFixedFrames=" + section.bodyFixedFrames.Count.ToString(CultureInfo.InvariantCulture),
                1.0);

            return true;
        }

        /// <summary>
        /// Gate for the body-fixed-primary playback route: a recording is a parent-anchored
        /// body-fixed-primary candidate when it carries a <c>ParentAnchorRecordingId</c>, REGARDLESS
        /// of <c>IsDebris</c>. Both genuine debris (IsDebris=true) and controlled-decoupled children
        /// (IsDebris=false: probes, landers, a continuing upper stage of a staged rocket) render
        /// through their own <c>bodyFixedFrames</c> first. Shared by the non-loop
        /// (<see cref="TryPositionRelativeSectionAtPlaybackUT"/>) and loop/overlap
        /// (<see cref="PositionLoopAtPlaybackUT"/>) paths so the two never diverge: the loop path
        /// used to additionally require IsDebris, which silently dropped controlled children to the
        /// plain relative resolver and retired them to a stale / garbage position whenever their
        /// transient sibling anchor (e.g. a probe that decoupled and crashed) ended before the
        /// child's own window did. Pure; unit-tested.
        /// </summary>
        internal static bool IsParentAnchoredBodyFixedPrimaryCandidate(IPlaybackTrajectory traj)
            => traj != null && !string.IsNullOrWhiteSpace(traj.ParentAnchorRecordingId);

        // KEEP debris-only: the `IsDebris` conjunct here is semantic. Loop-anchored
        // chains are debris-of-a-looped-vessel; controlled-decoupled children do
        // not participate in loop-anchored chains today (no design support for
        // re-attaching a controlled vessel as a loop anchor's payload).
        internal static bool ShouldUseLoopAnchoredDebrisChain(
            IPlaybackTrajectory traj,
            double playbackUT)
        {
            if (traj == null
                || !traj.IsDebris
                || string.IsNullOrWhiteSpace(traj.ParentAnchorRecordingId))
            {
                return false;
            }

            if (!TryFindPlaybackRecordingTree(traj, out RecordingTree tree, out _)
                || tree?.Recordings == null)
            {
                return false;
            }

            var visited = new HashSet<string>(StringComparer.Ordinal);
            string anchorRecordingId = traj.ParentAnchorRecordingId;
            while (!string.IsNullOrWhiteSpace(anchorRecordingId))
            {
                if (!visited.Add(anchorRecordingId))
                    return false;
                if (!tree.Recordings.TryGetValue(anchorRecordingId, out Recording anchor)
                    || anchor == null)
                {
                    return false;
                }

                if (anchor.TrackSections == null || anchor.TrackSections.Count == 0)
                    return false;

                int sectionIndex = TrajectoryMath.FindTrackSectionForUT(anchor.TrackSections, playbackUT);
                if (sectionIndex < 0)
                    return false;

                TrackSection section = anchor.TrackSections[sectionIndex];
                if (section.referenceFrame != ReferenceFrame.Relative)
                    return false;
                if (!DebrisRelativePlaybackPolicy.RelativeFramesCoverUT(
                        anchor,
                        section,
                        playbackUT,
                        DebrisRelativeCoverageMode.RecorderPersistable))
                {
                    return false;
                }
                if (anchor.LoopAnchorVesselId != 0u)
                {
                    // Accept either: (a) section pid matches the recording's
                    // declared loop anchor (typical case), or (b) section pid
                    // is zero and the resolver will fall back to
                    // recording.LoopAnchorVesselId. Reject mid-loop pid
                    // mismatches — composing through a non-loop live PID
                    // across loop iterations is non-deterministic; body-fixed
                    // primary is the safe fallback.
                    bool sectionPidValid = section.anchorVesselId == anchor.LoopAnchorVesselId
                        || section.anchorVesselId == 0u;
                    if (!sectionPidValid)
                    {
                        string capturedAnchorId = anchorRecordingId;
                        int capturedSectionIndex = sectionIndex;
                        TrackSection capturedSection = section;
                        Recording capturedAnchor = anchor;
                        ParsekLog.VerboseRateLimited("LoopAnchor",
                            "loop-anchor-pid-mismatch:" + traj.RecordingId,
                            () => "ShouldUseLoopAnchoredDebrisChain rejecting: mid-loop section pid mismatch"
                                + " recordingId=" + traj.RecordingId
                                + " anchorRecordingId=" + capturedAnchorId
                                + " declaredLoopPid=" + capturedAnchor.LoopAnchorVesselId.ToString(CultureInfo.InvariantCulture)
                                + " sectionPid=" + capturedSection.anchorVesselId.ToString(CultureInfo.InvariantCulture)
                                + " sectionIndex=" + capturedSectionIndex.ToString(CultureInfo.InvariantCulture)
                                + " ut=" + playbackUT.ToString("R", CultureInfo.InvariantCulture)
                                + "; body-fixed primary fallback");
                    }
                    return sectionPidValid;
                }
                if (string.IsNullOrWhiteSpace(section.anchorRecordingId))
                    return false;

                anchorRecordingId = section.anchorRecordingId;
            }

            return false;
        }

        private static bool TryFindPlaybackRecordingTree(
            IPlaybackTrajectory traj,
            out RecordingTree tree,
            out Recording recording)
        {
            tree = null;
            recording = null;
            if (traj == null || string.IsNullOrWhiteSpace(traj.RecordingId))
                return false;

            if (PendingTreeSlot.HasPendingTree
                && TryFindRecordingInTree(PendingTreeSlot.PendingTree, traj.RecordingId, out recording))
            {
                tree = PendingTreeSlot.PendingTree;
                return true;
            }

            List<RecordingTree> committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    if (TryFindRecordingInTree(committedTrees[i], traj.RecordingId, out recording))
                    {
                        tree = committedTrees[i];
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryFindRecordingInTree(
            RecordingTree tree,
            string recordingId,
            out Recording recording)
        {
            recording = null;
            return tree?.Recordings != null
                && !string.IsNullOrWhiteSpace(recordingId)
                && tree.Recordings.TryGetValue(recordingId, out recording)
                && recording != null;
        }

        /// <summary>
        /// Sentinel check for InterpolateAndPosition's complete-failure paths
        /// (empty points list, body-lookup miss). Both write
        /// <see cref="InterpolationResult.Zero"/> which has bodyName=null;
        /// success paths always copy bodyName from a recorded TrajectoryPoint.
        /// </summary>
        internal static bool IsInterpolationResultValid(InterpolationResult result)
        {
            return !string.IsNullOrEmpty(result.bodyName);
        }

        private bool TryRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
            int index,
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double playbackUT,
            string callsite)
        {
            if (ShouldUseLoopAnchoredDebrisChain(traj, playbackUT))
                return false;

            if (!ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                    traj,
                    playbackUT,
                    out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic))
            {
                return false;
            }

            MarkParentAnchoredDebrisCoverageRetired(
                index, traj, state, playbackUT, callsite, diagnostic);
            return true;
        }

        private bool TryHandleParentAnchoredDebrisCoverageRetired(
            int index,
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double playbackUT,
            double currentUT,
            float warpRate,
            string callsite,
            bool emitExitWatch,
            out bool ghostActive)
        {
            ghostActive = HasLoadedGhostVisuals(state);
            if (ShouldUseLoopAnchoredDebrisChain(traj, playbackUT))
                return false;

            if (!ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                    traj,
                    playbackUT,
                    out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic))
            {
                return false;
            }

            MarkParentAnchoredDebrisCoverageRetired(
                index, traj, state, playbackUT, callsite, diagnostic);
            if (state != null)
            {
                if (HasLoadedGhostVisuals(state))
                {
                    ApplyFrameVisuals(index, traj, state, playbackUT, warpRate,
                        skipPartEvents: true, suppressVisualFx: true,
                        allowTransientEffects: false);
                }
                ResetGhostAppearanceTracking(state);
            }

            GhostRenderTrace.EmitGuardSkip(
                traj, index, currentUT,
                "parent-anchored-debris-outside-relative-coverage playbackUT="
                + playbackUT.ToString("R", CultureInfo.InvariantCulture)
                + " coverageReason=" + (diagnostic.Reason ?? "(unknown)")
                + " callsite=" + (callsite ?? "(unknown)"));

            if (emitExitWatch)
            {
                OnLoopCameraAction?.Invoke(new CameraActionEvent
                {
                    Index = index,
                    Action = CameraActionType.ExitWatch,
                    Trajectory = traj,
                    Flags = default(TrajectoryPlaybackFlags)
                });
                ParsekLog.VerboseRateLimited(
                    "Engine",
                    "coverage-retired-exit-watch-" + index.ToString(CultureInfo.InvariantCulture),
                    $"coverage-retired: exiting direct debris watch for ghost #{index} " +
                    $"\"{traj?.VesselName ?? state?.vesselName ?? "(unknown)"}\" " +
                    $"playbackUT={playbackUT.ToString("R", CultureInfo.InvariantCulture)}",
                    1.0);
            }

            ghostActive = false;
            return true;
        }

        private void MarkParentAnchoredDebrisCoverageRetired(
            int index,
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double playbackUT,
            string callsite,
            DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic)
        {
            GameObject ghost = state != null ? state.ghost : null;
            if (!ReferenceEquals(ghost, null))
                GhostPlaybackLogic.HideGhostForRetire(ghost);
            if (state != null)
            {
                state.anchorRetiredThisFrame = true;
            }

            string recordingId = traj?.RecordingId ?? "(none)";
            string key = BuildParentAnchoredDebrisCoverageRetiredKey(index, recordingId);
            // Warn (not Verbose): missing authored debris coverage is a
            // recording coverage gap, not the transient anchor-miss handled in
            // ParsekFlight.
            ParsekLog.WarnRateLimited(
                "Anchor",
                key,
                "recorded-relative-retired: " +
                "reason=parent-anchored-debris-outside-relative-coverage " +
                $"coverageReason={diagnostic.Reason ?? "(unknown)"} " +
                $"recording=#{index.ToString(CultureInfo.InvariantCulture)} " +
                $"vessel=\"{traj?.VesselName ?? state?.vesselName ?? "(unknown)"}\" " +
                $"recordingId={recordingId} " +
                $"playbackUT={playbackUT.ToString("R", CultureInfo.InvariantCulture)} " +
                $"sectionIndex={diagnostic.SectionIndex.ToString(CultureInfo.InvariantCulture)} " +
                $"sectionUT={FormatCoverageRange(diagnostic.SectionStartUT, diagnostic.SectionEndUT)} " +
                $"relativeFrames={FormatCoverageRange(diagnostic.FirstRelativeFrameUT, diagnostic.LastRelativeFrameUT)} " +
                $"bodyFixedFrames={FormatCoverageRange(diagnostic.FirstBodyFixedFrameUT, diagnostic.LastBodyFixedFrameUT)} " +
                $"anchorRec={diagnostic.AnchorRecordingId ?? "(none)"} " +
                $"callsite={callsite ?? "(unknown)"}",
                5.0);
        }

        private static string FormatCoverageRange(double startUT, double endUT)
        {
            if (double.IsNaN(startUT) || double.IsNaN(endUT))
                return "(none)";

            return "["
                + startUT.ToString("R", CultureInfo.InvariantCulture)
                + ","
                + endUT.ToString("R", CultureInfo.InvariantCulture)
                + "]";
        }

        private static string BuildParentAnchoredDebrisCoverageRetiredKey(
            int index, string recordingId)
        {
            return string.Concat(
                "parent-anchored-debris-outside-relative-coverage|",
                recordingId ?? "(none)",
                "|",
                index.ToString(CultureInfo.InvariantCulture));
        }

        private static bool ShouldExitWatchForCoverageRetiredState(
            int index,
            GhostPlaybackState state,
            FrameContext ctx)
        {
            if (ctx.protectedIndex != index)
                return false;
            if (ctx.protectedLoopCycleIndex == -1)
                return true;
            if (state == null)
                return false;
            return state.loopCycleIndex == ctx.protectedLoopCycleIndex;
        }

        private static bool ShouldExitWatchForCoverageRetiredCycle(
            int index,
            long loopCycleIndex,
            FrameContext ctx)
        {
            if (ctx.protectedIndex != index)
                return false;
            return ctx.protectedLoopCycleIndex == -1
                || loopCycleIndex == ctx.protectedLoopCycleIndex;
        }

        internal static bool ShouldPrimeSinglePointGhostFromOrbit(
            IPlaybackTrajectory traj, double playbackUT)
        {
            return traj?.Points != null
                && traj.Points.Count == 1
                && ShouldUseOrbitTailPlayback(traj, playbackUT);
        }

        internal static bool ShouldUseOrbitTailPlayback(
            IPlaybackTrajectory traj, double playbackUT)
        {
            return TryFindOrbitTailPlaybackSegment(traj, playbackUT, out _, out _);
        }

        internal static bool AuthoredFrameGapHasShadowCoverage(
            IPlaybackTrajectory traj, double playbackUT)
        {
            return ShouldSkipRecordedRelativeResolverForAuthoredFrameGap(
                    traj,
                    playbackUT,
                    out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic)
                && diagnostic.BodyFixedFramesCoverUT;
        }

        internal static double ResolvePredictedOrbitTailContinuityBlendSeconds(
            double lastPointUT, double segmentStartUT)
        {
            double gap = Math.Max(0.0, segmentStartUT - lastPointUT);
            double duration = Math.Max(
                PredictedOrbitTailContinuityMinBlendSeconds,
                gap + PredictedOrbitTailContinuityExtraBlendSeconds);
            return Math.Min(duration, PredictedOrbitTailContinuityMaxBlendSeconds);
        }

        internal static double ResolvePredictedOrbitTailContinuityWeight(
            double lastPointUT, double playbackUT, double blendSeconds)
        {
            if (blendSeconds <= 1e-6)
                return 0.0;
            if (playbackUT <= lastPointUT)
                return 1.0;

            double t = (playbackUT - lastPointUT) / blendSeconds;
            if (t >= 1.0)
                return 0.0;
            if (t <= 0.0)
                return 1.0;

            double smooth = t * t * (3.0 - 2.0 * t);
            return 1.0 - smooth;
        }

        internal static bool TryFindOrbitTailPlaybackSegment(
            IPlaybackTrajectory traj,
            double playbackUT,
            out OrbitSegment segment,
            out int segmentIndex)
        {
            segment = default(OrbitSegment);
            segmentIndex = -1;
            if (traj?.Points == null
                || traj.Points.Count == 0
                || traj.OrbitSegments == null
                || traj.OrbitSegments.Count == 0)
            {
                return false;
            }

            double lastPointUT = traj.Points[traj.Points.Count - 1].ut;
            if (playbackUT <= lastPointUT + 1e-6)
                return false;

            for (int i = 0; i < traj.OrbitSegments.Count; i++)
            {
                OrbitSegment candidate = traj.OrbitSegments[i];
                if (!TrajectoryMath.HasUsableOrbitSegmentElements(candidate))
                    continue;

                bool inRange = i == traj.OrbitSegments.Count - 1
                    ? playbackUT >= candidate.startUT && playbackUT <= candidate.endUT
                    : playbackUT >= candidate.startUT && playbackUT < candidate.endUT;
                if (!inRange)
                    continue;

                segment = candidate;
                segmentIndex = i;
                return true;
            }

            for (int i = 0; i < traj.OrbitSegments.Count; i++)
            {
                OrbitSegment candidate = traj.OrbitSegments[i];
                if (!candidate.isPredicted)
                    continue;
                if (!TrajectoryMath.HasUsableOrbitSegmentElements(candidate))
                    continue;
                if (candidate.startUT <= lastPointUT + 1e-6)
                    continue;

                double gap = candidate.startUT - lastPointUT;
                if (gap > PredictedOrbitTailBridgeMaxGapSeconds + 1e-6)
                    continue;
                if (playbackUT < candidate.startUT - 1e-6
                    && playbackUT <= candidate.endUT
                    && playbackUT > lastPointUT + 1e-6)
                {
                    segment = candidate;
                    segmentIndex = i;
                    return true;
                }
            }

            return false;
        }
    }
}
