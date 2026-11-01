using System.Collections.Generic;

namespace Parsek
{
    internal static partial class GhostPlaybackLogic
    {
        /// <summary>The global auto-loop launch gap from settings (default when settings are absent).</summary>
        internal static double ResolveGlobalAutoLoopIntervalSeconds()
        {
            return ParsekSettings.Current?.autoLoopIntervalSeconds
                   ?? LoopTiming.DefaultLoopIntervalSeconds;
        }

        /// <summary>
        /// The loop schedule of a STANDALONE per-recording loop (the Recordings-tab Loop toggle):
        /// where the replay timeline starts, the launch anchor, the loop duration and the
        /// launch-to-launch period. Mirrors the flight engine's <c>TryResolveLoopSchedule</c> and
        /// <c>ParsekKSC.TryGetLoopSchedule</c> exactly, with the auto-launch queue resolved over
        /// <paramref name="trajectories"/> (the committed list, index-aligned with
        /// <paramref name="recIdx"/>) through the pure <see cref="TryResolveAutoLoopLaunchSchedule"/>,
        /// so the slot and cadence are the ones the engine plays. <paramref name="unitMemberIndices"/>
        /// (the live loop-unit set's <c>OwnerByIndex</c>) is left out of that queue, as the engine's
        /// and the Space Center's queue builds leave it out. Returns false when the trajectory does
        /// not loop. Pure.
        /// </summary>
        internal static bool TryResolveStandaloneLoopSchedule(
            IPlaybackTrajectory traj,
            int recIdx,
            IReadOnlyList<IPlaybackTrajectory> trajectories,
            double globalIntervalSeconds,
            out double playbackStartUT,
            out double scheduleStartUT,
            out double duration,
            out double intervalSeconds,
            IReadOnlyDictionary<int, int> unitMemberIndices = null)
        {
            playbackStartUT = 0.0;
            scheduleStartUT = 0.0;
            duration = 0.0;
            intervalSeconds = 0.0;
            if (traj == null || !GhostPlaybackEngine.ShouldLoopPlayback(traj))
                return false;

            playbackStartUT = GhostPlaybackEngine.EffectiveLoopStartUT(traj);
            duration = GhostPlaybackEngine.EffectiveLoopDuration(traj);
            if (duration <= LoopTiming.MinLoopDurationSeconds)
                return false;

            double baseIntervalSeconds = ResolveLoopInterval(
                traj, globalIntervalSeconds, LoopTiming.DefaultLoopIntervalSeconds, LoopTiming.MinCycleDuration);
            scheduleStartUT = playbackStartUT;
            intervalSeconds = baseIntervalSeconds;

            if (recIdx >= 0
                && trajectories != null
                && ShouldUseGlobalAutoLaunchQueue(traj)
                && TryResolveAutoLoopLaunchSchedule(
                    trajectories, recIdx, baseIntervalSeconds, out AutoLoopLaunchSchedule autoSchedule,
                    unitMemberIndices))
            {
                scheduleStartUT = autoSchedule.LaunchStartUT;
                intervalSeconds = autoSchedule.LaunchCadenceSeconds;
            }
            return true;
        }

        /// <summary>
        /// The map / Tracking Station sample UT of a NON-OVERLAPPING standalone per-recording loop at
        /// <paramref name="liveUT"/>: the recorded UT its replayed mesh sits at in the flight engine
        /// (<c>playbackStartUT + phase</c> of <see cref="TryComputeLoopPlaybackPhase"/>), with
        /// <paramref name="renderHidden"/>=true where the engine shows no mesh: before the schedule
        /// start (the engine's "loop UT computation failed" teardown) and in the pause between cycles
        /// when the period exceeds the duration (the engine's pause-window hide).
        /// <para>Returns false (caller keeps the live UT) when the trajectory does not loop, or when it
        /// is an OVERLAP loop (period shorter than the duration): the per-instance overlap sweep owns
        /// those, one map vessel per live cycle. The watch-mode loop phase reset is a flight-engine-only
        /// offset and is not applied here. Pure.</para>
        /// </summary>
        internal static bool TryResolveStandaloneLoopSampleUT(
            IPlaybackTrajectory traj,
            int recIdx,
            IReadOnlyList<IPlaybackTrajectory> trajectories,
            double globalIntervalSeconds,
            double liveUT,
            out double sampleUT,
            out bool renderHidden,
            IReadOnlyDictionary<int, int> unitMemberIndices = null)
        {
            sampleUT = liveUT;
            renderHidden = false;
            if (!TryResolveStandaloneLoopSchedule(
                    traj, recIdx, trajectories, globalIntervalSeconds,
                    out double playbackStartUT, out double scheduleStartUT,
                    out double duration, out double intervalSeconds,
                    unitMemberIndices))
                return false;
            if (IsOverlapLoop(intervalSeconds, duration))
                return false;

            if (!TryComputeLoopPlaybackPhase(
                    liveUT, scheduleStartUT, duration, intervalSeconds,
                    out double playbackPhase, out _, out bool inPauseWindow)
                || inPauseWindow)
            {
                renderHidden = true;
                return true;
            }

            sampleUT = playbackStartUT + playbackPhase;
            return true;
        }
    }
}
