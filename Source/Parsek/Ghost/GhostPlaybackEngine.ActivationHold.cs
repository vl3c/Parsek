using System;

namespace Parsek
{
    internal partial class GhostPlaybackEngine
    {
        internal static double ResolveGhostActivationStartUT(IPlaybackTrajectory traj)
        {
            return PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(traj);
        }

        internal static double ResolveVisiblePlaybackUT(
            IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            if (traj == null || state == null)
                return playbackUT;

            if (!state.deferVisibilityUntilPlaybackSync || state.appearanceCount != 0)
                return playbackUT;

            // Body-fixed-primary carve-out (matches the hide-gate carve-out): parent-anchored
            // debris with body-fixed primary covering the activation UT does
            // NOT get clamped to activationStartUT for one frame either.
            // Without this, the clamp produces a one-frame seed render that
            // then jumps to the natural playback UT on the next frame --
            // (rawPlaybackUT - activationStartUT) * velocity = ~6 m of
            // downrange slide for atmospheric debris at ~150-190 m/s. With
            // the carve-out, the first visible frame renders at the natural
            // playback UT (typically <0.02 s past the seed, so the visible
            // position is sub-metre off the seed) and subsequent frames
            // advance smoothly with no jump. Body-fixed primary playback is
            // deterministic at any UT inside the section so this is safe.
            if (IsV13ParentAnchoredDebrisWithBodyFixedPrimaryAtActivationUT(traj))
                return playbackUT;

            double activationStartUT = ResolveGhostActivationStartUT(traj);
            double activationLead = playbackUT - activationStartUT;

            if (DebrisRelativePlaybackPolicy.TryResolveInitialStructuralSeedBridgeEndUT(
                    traj,
                    activationStartUT,
                    GhostPlayback.InitialDebrisSeedBridgeActivationHiddenMaxSeconds,
                    out double debrisSeedBridgeEndUT))
            {
                double debrisSeedBridgeOvershoot = playbackUT - debrisSeedBridgeEndUT;
                if (debrisSeedBridgeOvershoot > 0.0
                    && debrisSeedBridgeOvershoot <= GhostPlayback.InitialVisibleFrameClampWindowSeconds)
                {
                    return debrisSeedBridgeEndUT;
                }
            }

            if (activationLead <= 0.0 || activationLead > GhostPlayback.InitialVisibleFrameClampWindowSeconds)
                return playbackUT;

            return activationStartUT;
        }

        internal static bool ShouldHoldInitialRelativeActivationHidden(
            IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            if (!CanEvaluateInitialActivationHidden(traj, state))
                return false;

            double activationStartUT = ResolveGhostActivationStartUT(traj);
            double activationLead = playbackUT - activationStartUT;
            if (activationLead < -1e-6
                || activationLead > GhostPlayback.InitialRelativeActivationHiddenSeconds)
            {
                return false;
            }

            if (traj.TrackSections == null || traj.TrackSections.Count == 0)
                return false;

            int sectionIndex = TrajectoryMath.FindTrackSectionForUT(
                traj.TrackSections, activationStartUT + 1e-6);
            if (sectionIndex < 0)
                sectionIndex = TrajectoryMath.FindTrackSectionForUT(
                    traj.TrackSections, activationStartUT);
            if (sectionIndex < 0
                || traj.TrackSections[sectionIndex].referenceFrame != ReferenceFrame.Relative)
            {
                return false;
            }

            // Body-fixed-primary carve-out: parent-anchored debris that has a body-fixed primary
            // surface covering the activation UT does not need the generic
            // relative-start hide. The body-fixed primary path resolves the
            // recorded world pose directly from the section's bodyFixedFrames
            // without consulting any live anchor, so there is no anchor-
            // resolution race to mask. Without this carve-out, the 0.08s hide
            // forces playback to advance to activationLead > 0.08 before the
            // ghost becomes visible -- at debris velocities (~190 m/s in
            // atmosphere), that is ~19 m of velocity-integrated downrange
            // motion past the recorded seed pose, producing the user-visible
            // "ghost spawns too far forward" symptom.
            if (IsV13ParentAnchoredDebrisWithBodyFixedPrimaryAtActivationUT(traj))
                return false;

            return true;
        }

        private static bool IsParentAnchoredDebrisTrajectory(IPlaybackTrajectory traj)
        {
            return traj != null
                && traj.IsDebris
                && !string.IsNullOrWhiteSpace(traj.ParentAnchorRecordingId);
        }

        /// <summary>
        /// Shared predicate for the parent-anchored debris activation-hide
        /// carve-outs: returns true when the trajectory is parent-anchored
        /// debris AND the section covering the activation UT is Relative AND
        /// body-fixed primary coverage exists at the activation UT. Both the
        /// relative-start UT-window gate and the activation-settle time-warp
        /// fallback consult this so they stay symmetric; flipping one without
        /// the other reintroduces the velocity-integrated forward slide that
        /// the carve-out is designed to eliminate.
        /// </summary>
        internal static bool IsV13ParentAnchoredDebrisWithBodyFixedPrimaryAtActivationUT(
            IPlaybackTrajectory traj)
        {
            if (!IsParentAnchoredDebrisTrajectory(traj))
                return false;
            if (traj.TrackSections == null || traj.TrackSections.Count == 0)
                return false;

            double activationStartUT = ResolveGhostActivationStartUT(traj);
            int sectionIndex = TrajectoryMath.FindTrackSectionForUT(
                traj.TrackSections, activationStartUT + 1e-6);
            if (sectionIndex < 0)
                sectionIndex = TrajectoryMath.FindTrackSectionForUT(
                    traj.TrackSections, activationStartUT);
            if (sectionIndex < 0
                || traj.TrackSections[sectionIndex].referenceFrame != ReferenceFrame.Relative)
            {
                return false;
            }

            return ParsekFlight.BodyFixedPrimaryCoversPlaybackUT(
                traj.TrackSections[sectionIndex],
                activationStartUT,
                out _,
                out _);
        }

        private static bool CanEvaluateInitialActivationHidden(
            IPlaybackTrajectory traj, GhostPlaybackState state)
        {
            return traj != null
                && state != null
                && state.deferVisibilityUntilPlaybackSync
                && state.appearanceCount == 0;
        }

        internal static bool ShouldHoldInitialAbsoluteBridgeActivationHidden(
            IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            if (!CanEvaluateInitialActivationHidden(traj, state))
                return false;

            return TryResolveInitialAbsoluteBridgeActivationEndUT(
                    traj,
                    out double activationStartUT,
                    out double bridgeEndUT)
                && playbackUT >= activationStartUT - 1e-6
                && playbackUT <= bridgeEndUT + 1e-6;
        }

        internal static bool ShouldHoldInitialAbsoluteToRelativePrimerActivationHidden(
            IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            if (!CanEvaluateInitialActivationHidden(traj, state))
                return false;

            return TryResolveInitialAbsoluteToRelativePrimerEndUT(
                    traj,
                    out double activationStartUT,
                    out double primerEndUT)
                && playbackUT >= activationStartUT - 1e-6
                && playbackUT <= primerEndUT + 1e-6;
        }

        internal static bool ShouldHoldInitialDebrisSeedBridgeActivationHidden(
            IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            if (!CanEvaluateInitialActivationHidden(traj, state))
                return false;

            double activationStartUT = ResolveGhostActivationStartUT(traj);
            return DebrisRelativePlaybackPolicy.TryResolveInitialStructuralSeedBridgeEndUT(
                    traj,
                    activationStartUT,
                    GhostPlayback.InitialDebrisSeedBridgeActivationHiddenMaxSeconds,
                    out double bridgeEndUT)
                && playbackUT >= activationStartUT - 1e-6
                && playbackUT < bridgeEndUT - 1e-6;
        }

        private static bool IsInitialDebrisSeedBridgeEndFrame(
            IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            if (!CanEvaluateInitialActivationHidden(traj, state))
                return false;

            double activationStartUT = ResolveGhostActivationStartUT(traj);
            return DebrisRelativePlaybackPolicy.TryResolveInitialStructuralSeedBridgeEndUT(
                    traj,
                    activationStartUT,
                    GhostPlayback.InitialDebrisSeedBridgeActivationHiddenMaxSeconds,
                    out double bridgeEndUT)
                && Math.Abs(playbackUT - bridgeEndUT) <= 1e-6;
        }

        private static bool TryResolveInitialAbsoluteBridgeActivationEndUT(
            IPlaybackTrajectory traj,
            out double activationStartUT,
            out double bridgeEndUT)
        {
            activationStartUT = double.NaN;
            bridgeEndUT = double.NaN;
            if (traj?.TrackSections == null || traj.TrackSections.Count == 0)
                return false;

            activationStartUT = ResolveGhostActivationStartUT(traj);
            int sectionIndex = TrajectoryMath.FindTrackSectionForUT(
                traj.TrackSections, activationStartUT + 1e-6);
            if (sectionIndex < 0)
                sectionIndex = TrajectoryMath.FindTrackSectionForUT(
                    traj.TrackSections, activationStartUT);
            if (sectionIndex < 0 || sectionIndex >= traj.TrackSections.Count)
                return false;

            TrackSection section = traj.TrackSections[sectionIndex];
            if (section.referenceFrame != ReferenceFrame.Absolute
                || section.frames == null
                || section.frames.Count != 1)
            {
                return false;
            }

            TrajectoryPoint seed = section.frames[0];
            double bridgeDuration = section.endUT - seed.ut;
            if (bridgeDuration <= 1e-6
                || bridgeDuration > GhostPlayback.InitialAbsoluteBridgeActivationHiddenMaxSeconds)
            {
                return false;
            }

            if (Math.Abs(seed.ut - activationStartUT)
                > GhostPlayback.InitialAbsoluteBridgeActivationHiddenMaxSeconds)
            {
                return false;
            }

            bridgeEndUT = section.endUT;
            return true;
        }

        private static bool TryResolveInitialAbsoluteToRelativePrimerEndUT(
            IPlaybackTrajectory traj,
            out double activationStartUT,
            out double primerEndUT)
        {
            activationStartUT = double.NaN;
            primerEndUT = double.NaN;
            if (traj?.TrackSections == null || traj.TrackSections.Count == 0)
                return false;

            activationStartUT = ResolveGhostActivationStartUT(traj);
            int sectionIndex = TrajectoryMath.FindTrackSectionForUT(
                traj.TrackSections, activationStartUT + 1e-6);
            if (sectionIndex < 0)
                sectionIndex = TrajectoryMath.FindTrackSectionForUT(
                    traj.TrackSections, activationStartUT);
            if (sectionIndex < 0 || sectionIndex >= traj.TrackSections.Count)
                return false;

            double maxEndUT = activationStartUT
                + GhostPlayback.InitialAbsoluteBridgeActivationHiddenMaxSeconds;
            bool sawAbsolutePrimer = false;
            for (int i = sectionIndex; i < traj.TrackSections.Count; i++)
            {
                TrackSection section = traj.TrackSections[i];
                if (section.startUT > maxEndUT + 1e-6)
                    return false;

                if (section.referenceFrame == ReferenceFrame.Relative)
                {
                    if (!sawAbsolutePrimer)
                        return false;

                    double relativeStartUT = Math.Max(section.startUT, activationStartUT);
                    if (relativeStartUT <= activationStartUT + 1e-6
                        || relativeStartUT > maxEndUT + 1e-6)
                    {
                        return false;
                    }

                    primerEndUT = relativeStartUT;
                    return true;
                }

                if (section.referenceFrame != ReferenceFrame.Absolute)
                    return false;

                if (section.endUT > maxEndUT + 1e-6)
                    return false;

                sawAbsolutePrimer = true;
            }

            return false;
        }

        internal static bool ShouldHoldInitialActivationHiddenThisFrame(
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double playbackUT,
            out string reason)
        {
            reason = null;
            if (state == null)
                return false;

            bool withinDebrisSeedBridge = ShouldHoldInitialDebrisSeedBridgeActivationHidden(
                traj, state, playbackUT);
            bool withinRelativeWindow = !withinDebrisSeedBridge
                && ShouldHoldInitialRelativeActivationHidden(
                    traj, state, playbackUT);
            bool withinAbsoluteBridge = !withinDebrisSeedBridge
                && !withinRelativeWindow
                && ShouldHoldInitialAbsoluteBridgeActivationHidden(
                    traj, state, playbackUT);
            bool withinAbsoluteToRelativePrimer = !withinDebrisSeedBridge
                && !withinRelativeWindow
                && !withinAbsoluteBridge
                && ShouldHoldInitialAbsoluteToRelativePrimerActivationHidden(
                    traj, state, playbackUT);
            bool withinUtWindow = withinDebrisSeedBridge
                || withinRelativeWindow
                || withinAbsoluteBridge
                || withinAbsoluteToRelativePrimer;
            // Body-fixed-primary carve-out: parent-anchored debris that has body-fixed primary
            // covering the activation UT does not need ANY initial-hide path --
            // not the UT-window gates above, and not the activation-settle
            // time-warp guard below. Body-fixed primary playback resolves the
            // recorded world pose directly without any live-anchor race, so
            // even under time warp there is nothing to wait for. Without this
            // gate, the activation-settle clause primes the minimum-frames
            // counter and the ghost stays hidden for additional frames during
            // which playback advances and the transform slides forward by one
            // physics-tick of velocity-integrated motion (the "ghost slides
            // 2-3m in front then settles" symptom). The same body-fixed
            // primary coverage condition that gates the relative-start skip
            // in ShouldHoldInitialRelativeActivationHidden also gates this
            // activation-settle skip, so the two carve-outs stay in lockstep.
            bool v13ParentAnchoredDebrisExempt =
                IsV13ParentAnchoredDebrisWithBodyFixedPrimaryAtActivationUT(traj);
            // Chain-seam carve-out: a StandardEnter spawn that replaces a same-chain predecessor
            // whose ghost just delivered its terminal pose this same frame does not need the
            // activation-settle hold. The settle window exists to mask the fresh first-appearance
            // pose pop that races visual construction + anchor resolution against the engine's
            // first positioning call; at a chain seam the predecessor's last pose is by
            // construction continuous with the successor's first pose (same vessel id, same chain,
            // same body, same physics tick), so there is no first-appearance race to suppress.
            // Skipping settle here removes the 14 ms invisible-ghost gap the camera otherwise sees
            // immediately after a chain handoff and keeps the new ghost visually continuous with
            // the just-departed predecessor. UT-window clauses above (debris-seed-bridge,
            // relative-start, absolute-seed-bridge, absolute-primer-to-relative) are unaffected;
            // chain successors that fall inside one of those windows still hide for the window's
            // own physical reason.
            bool chainSeamSpawnExempt = state.spawnedAtChainSeam;
            bool withinActivationSettle = !withinUtWindow
                && !v13ParentAnchoredDebrisExempt
                && !chainSeamSpawnExempt
                && CanEvaluateInitialActivationHidden(traj, state)
                && !IsInitialDebrisSeedBridgeEndFrame(traj, state, playbackUT)
                && !state.initialRelativeActivationHiddenPrimed;
            bool shouldPrimeHiddenFrames = withinUtWindow || withinActivationSettle;
            if (shouldPrimeHiddenFrames && !state.initialRelativeActivationHiddenPrimed)
            {
                state.initialRelativeActivationHiddenPrimed = true;
                state.initialRelativeActivationHiddenFramesRemaining =
                    Math.Max(
                        state.initialRelativeActivationHiddenFramesRemaining,
                        GhostPlayback.InitialActivationHiddenMinimumFrames);
            }

            if (shouldPrimeHiddenFrames)
            {
                reason = withinDebrisSeedBridge
                    ? "debris-seed-bridge"
                    : (withinRelativeWindow
                        ? "relative-start"
                        : (withinAbsoluteBridge
                            ? "absolute-seed-bridge"
                            : (withinAbsoluteToRelativePrimer
                                ? "absolute-primer-to-relative"
                                : "activation-settle")));
                ConsumeInitialRelativeHiddenFrame(state);
                return true;
            }

            if (state.initialRelativeActivationHiddenPrimed
                && state.initialRelativeActivationHiddenFramesRemaining > 0
                && state.appearanceCount == 0
                && state.deferVisibilityUntilPlaybackSync)
            {
                reason = "minimum-frames";
                ConsumeInitialRelativeHiddenFrame(state);
                return true;
            }

            return false;
        }

        internal static bool ShouldHoldInitialRelativeActivationHiddenThisFrame(
            IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            return ShouldHoldInitialActivationHiddenThisFrame(
                traj, state, playbackUT, out string _);
        }

        private static void ConsumeInitialRelativeHiddenFrame(GhostPlaybackState state)
        {
            if (state != null && state.initialRelativeActivationHiddenFramesRemaining > 0)
                state.initialRelativeActivationHiddenFramesRemaining--;
        }
    }
}
