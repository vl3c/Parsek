using System;

namespace Parsek
{
    internal partial class GhostPlaybackEngine
    {
        /// <summary>
        /// Returns the effective loop start UT, falling back to the first playable
        /// ghost-activation UT when LoopStartUT is NaN or out of range.
        /// </summary>
        internal static double EffectiveLoopStartUT(IPlaybackTrajectory traj)
        {
            double activationStartUT = ResolveGhostActivationStartUT(traj);
            double loopStart = traj.LoopStartUT;
            if (!double.IsNaN(loopStart) && loopStart >= activationStartUT && loopStart < traj.EndUT)
            {
                // Cross-validate: effective start must be less than effective end
                double loopEnd = traj.LoopEndUT;
                double effectiveEnd = (!double.IsNaN(loopEnd) && loopEnd <= traj.EndUT && loopEnd > activationStartUT)
                    ? loopEnd : traj.EndUT;
                if (loopStart >= effectiveEnd)
                    return activationStartUT;
                return loopStart;
            }
            return activationStartUT;
        }

        /// <summary>
        /// Returns the effective loop end UT, falling back to traj.EndUT when
        /// LoopEndUT is NaN or out of range.
        /// </summary>
        internal static double EffectiveLoopEndUT(IPlaybackTrajectory traj)
        {
            double activationStartUT = ResolveGhostActivationStartUT(traj);
            double loopEnd = traj.LoopEndUT;
            if (!double.IsNaN(loopEnd) && loopEnd <= traj.EndUT && loopEnd > activationStartUT)
            {
                // Cross-validate: effective end must be greater than effective start
                double loopStart = traj.LoopStartUT;
                double effectiveStart = (!double.IsNaN(loopStart) && loopStart >= activationStartUT && loopStart < traj.EndUT)
                    ? loopStart : activationStartUT;
                if (loopEnd <= effectiveStart)
                    return traj.EndUT;
                return loopEnd;
            }
            return traj.EndUT;
        }

        /// <summary>
        /// Returns the effective loop duration (EffectiveLoopEndUT - EffectiveLoopStartUT).
        /// All loop-dispatch decisions that care about "one cycle length" — IsOverlapLoop,
        /// overlap phase clamping, watch-mode single-vs-overlap choice — should use this
        /// instead of `traj.EndUT - traj.StartUT` or the half-hybrid `traj.EndUT - effStart`,
        /// so recordings with a custom loop subrange get consistent duration everywhere.
        /// #409: was duplicated inline at the watch-mode sites with inconsistent formulas.
        /// </summary>
        internal static double EffectiveLoopDuration(IPlaybackTrajectory traj)
        {
            return EffectiveLoopEndUT(traj) - EffectiveLoopStartUT(traj);
        }

        /// <summary>
        /// Converts a pre-#381 legacy "gap after cycle" value into the current launch-to-launch
        /// period. If the reconstructed period underflows or the trajectory bounds are not
        /// available, clamps defensively to MinCycleDuration.
        /// </summary>
        internal static bool TryConvertLegacyGapToLoopPeriodSeconds(
            IPlaybackTrajectory traj,
            double legacyGapSeconds,
            out double migratedPeriod,
            out double effectiveLoopDuration)
        {
            effectiveLoopDuration = EffectiveLoopDuration(traj);
            migratedPeriod = legacyGapSeconds;
            if (double.IsNaN(effectiveLoopDuration) || double.IsInfinity(effectiveLoopDuration)
                || effectiveLoopDuration <= 0.0)
                return false;
            // #411 follow-up: reject NaN/Inf gap defensively so the caller doesn't store a
            // poisoned period on the recording. All real load paths parse via double.TryParse
            // and never hand NaN in, but hand-edited saves can.
            if (double.IsNaN(legacyGapSeconds) || double.IsInfinity(legacyGapSeconds))
                return false;

            migratedPeriod = effectiveLoopDuration + legacyGapSeconds;
            if (double.IsNaN(migratedPeriod) || double.IsInfinity(migratedPeriod)
                || migratedPeriod < LoopTiming.MinCycleDuration)
                migratedPeriod = LoopTiming.MinCycleDuration;
            return true;
        }

        /// <summary>
        /// Returns the UT where loop playback should hold/teardown. For custom loop ranges this
        /// is the effective loop end, not the recording's raw final timestamp.
        /// </summary>
        internal static double ResolveLoopPlaybackEndpointUT(IPlaybackTrajectory traj)
        {
            return EffectiveLoopEndUT(traj);
        }

        /// <summary>Whether the trajectory should loop (has enough points and duration).</summary>
        internal static bool ShouldLoopPlayback(IPlaybackTrajectory traj)
        {
            if (traj == null || !traj.LoopPlayback || traj.Points == null || traj.Points.Count < 2)
                return false;
            double start = EffectiveLoopStartUT(traj);
            double end = EffectiveLoopEndUT(traj);
            return end - start > LoopTiming.MinLoopDurationSeconds;
        }

        private static int CompareAutoLoopQueueCandidates(AutoLoopQueueCandidate a, AutoLoopQueueCandidate b)
        {
            int cmp = a.PlaybackStartUT.CompareTo(b.PlaybackStartUT);
            if (cmp != 0)
                return cmp;

            cmp = a.PlaybackEndUT.CompareTo(b.PlaybackEndUT);
            if (cmp != 0)
                return cmp;

            cmp = string.CompareOrdinal(a.RecordingId, b.RecordingId);
            if (cmp != 0)
                return cmp;

            return a.RecordingIndex.CompareTo(b.RecordingIndex);
        }
    }
}
