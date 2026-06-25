using System;
using System.Collections.Generic;

namespace Parsek
{
    internal static partial class GhostMapPresence
    {
        /// <summary>
        /// Per-recording overlap gate (slice i). A UNION of two overlap sources, mirroring the flight
        /// engine's two overlap entry points exactly:
        ///   (b) MISSION-UNIT self-overlap (checked FIRST, preferred when both apply): this index is a
        ///       member of a looped Mission unit whose overlap cadence is shorter than its span
        ///       (<see cref="GhostPlaybackLogic.UnitMemberOverlaps"/>), so the whole mission relaunches
        ///       before the prior instance finishes (GhostPlaybackEngine.cs:2144-2183). A Mission member
        ///       does NOT carry its own <c>rec.LoopPlayback</c> flag, so this branch must run regardless
        ///       of it - the bug that cost a playtest: the maintainer loops via the Missions tab.
        ///   (a) STANDALONE per-recording overlap: the recording itself loops (<c>rec.LoopPlayback</c>)
        ///       and its launch-to-launch period is shorter than its duration (ParsekKSC.cs:454-484).
        /// Returns (a)||(b). <paramref name="loopUnits"/> is the per-frame
        /// <see cref="GhostPlaybackEngine.CurrentLoopUnits"/> threaded from the scene driver; Empty
        /// (the common case) collapses this to source (a) only - byte-identical to pre-Missions.
        /// </summary>
        internal static bool IsOverlapRecording(
            Recording rec, int recIdx, IReadOnlyList<Recording> committed,
            GhostPlaybackLogic.LoopUnitSet loopUnits)
        {
            if (rec == null)
                return false;

            // (b) Mission-unit self-overlap — checked first, regardless of rec.LoopPlayback.
            if (IsUnitOverlapMember(rec, recIdx, loopUnits))
                return true;

            // (a) Standalone per-recording overlap loop.
            if (!rec.LoopPlayback)
                return false;
            if (!GhostPlaybackEngine.ShouldLoopPlayback(rec))
                return false;

            double duration = GhostPlaybackEngine.EffectiveLoopDuration(rec);
            if (duration <= LoopTiming.MinLoopDurationSeconds)
                return false;

            double intervalSeconds = ResolveOverlapLoopIntervalSeconds(rec, recIdx, committed);
            return GhostPlaybackLogic.IsOverlapLoop(intervalSeconds, duration);
        }

        /// <summary>
        /// Source (b) predicate: is <paramref name="recIdx"/> a member of a looped Mission unit that
        /// SELF-OVERLAPS (span &gt; 0 &amp;&amp; OverlapCadenceSeconds &lt; span via
        /// <see cref="GhostPlaybackLogic.UnitMemberOverlaps"/>) AND this member's trimmed render window
        /// is long enough to replay? Mirrors the flight engine's unit-overlap branch entry
        /// (GhostPlaybackEngine.cs:2163-2169 - the <c>memberDuration &gt; 0</c> guard, tightened here
        /// to the same <see cref="LoopTiming.MinLoopDurationSeconds"/> floor the standalone path uses).
        /// </summary>
        private static bool IsUnitOverlapMember(
            Recording rec, int recIdx, GhostPlaybackLogic.LoopUnitSet loopUnits)
        {
            if (rec == null || loopUnits == null)
                return false;
            if (!loopUnits.TryGetUnitForMember(recIdx, out GhostPlaybackLogic.LoopUnit unit))
                return false;
            if (!GhostPlaybackLogic.UnitMemberOverlaps(unit))
                return false;

            double memberStartUT = unit.MemberStartUT(recIdx, rec.StartUT);
            double memberEndUT = unit.MemberEndUT(recIdx, rec.EndUT);
            return memberEndUT - memberStartUT > LoopTiming.MinLoopDurationSeconds;
        }

        /// <summary>
        /// The per-instance overlap path is always available (8e S4 dropped the director-drive gate that
        /// previously made it conditional; the Director pipeline is unconditional, so the N per-cycle
        /// epochs are always bakeable). Kept as a method for call-site stability.
        /// </summary>
        internal static bool IsOverlapPerInstanceGateOn()
        {
            return true;
        }

        /// <summary>
        /// Should THIS recording be driven by the per-instance overlap path this frame? True when the
        /// recording is an overlap loop (<see cref="IsOverlapRecording"/>) - overlap recordings ALWAYS
        /// take the per-instance path now (8e S4). When this is true the legacy passes hand off to
        /// <see cref="EnsureOverlapInstances"/> and skip their own single-instance create/reseed for the
        /// index.
        /// </summary>
        internal static bool ShouldDriveOverlapPerInstance(
            Recording rec, int recIdx, IReadOnlyList<Recording> committed,
            GhostPlaybackLogic.LoopUnitSet loopUnits)
        {
            return IsOverlapRecording(rec, recIdx, committed, loopUnits);
        }

        /// <summary>
        /// Resolve the launch-to-launch interval for an overlap-looped recording, threading the
        /// global-auto-launch-queue schedule through the PURE
        /// <see cref="GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule"/> (so the cadence matches
        /// flight + KSC for an auto-interval recording). Falls back to
        /// <see cref="GhostPlaybackLogic.ResolveLoopInterval"/> for non-queue recordings. Mirrors
        /// ParsekKSC.GetLoopIntervalSeconds.
        /// </summary>
        private static double ResolveOverlapLoopIntervalSeconds(
            Recording rec, int recIdx, IReadOnlyList<Recording> committed)
        {
            if (TryResolveOverlapScheduleAnchor(
                    rec, recIdx, committed, out _, out double intervalSeconds))
                return intervalSeconds;

            double globalInterval = ParsekSettings.Current?.autoLoopIntervalSeconds
                                    ?? LoopTiming.DefaultLoopIntervalSeconds;
            return GhostPlaybackLogic.ResolveLoopInterval(
                rec, globalInterval, LoopTiming.DefaultLoopIntervalSeconds, LoopTiming.MinCycleDuration);
        }

        /// <summary>
        /// Resolve the (scheduleStartUT, intervalSeconds) anchor for an overlap-looped recording via
        /// the PURE auto-launch-queue resolver. Mirrors ParsekKSC.TryGetLoopSchedule's schedule
        /// branch: for a global-auto-launch-queue recording the schedule start + cadence come from
        /// <see cref="GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule"/> (which sorts ALL queue
        /// members so the slot/cadence is deterministic across scenes); otherwise the recording's own
        /// effective loop start + resolved interval. Returns false when the recording is not a loop.
        /// </summary>
        private static bool TryResolveOverlapScheduleAnchor(
            Recording rec, int recIdx, IReadOnlyList<Recording> committed,
            out double scheduleStartUT, out double intervalSeconds)
        {
            scheduleStartUT = 0.0;
            intervalSeconds = 0.0;
            if (rec == null || !GhostPlaybackEngine.ShouldLoopPlayback(rec))
                return false;

            double playbackStartUT = GhostPlaybackEngine.EffectiveLoopStartUT(rec);
            double globalInterval = ParsekSettings.Current?.autoLoopIntervalSeconds
                                    ?? LoopTiming.DefaultLoopIntervalSeconds;
            double baseIntervalSeconds = GhostPlaybackLogic.ResolveLoopInterval(
                rec, globalInterval, LoopTiming.DefaultLoopIntervalSeconds, LoopTiming.MinCycleDuration);

            scheduleStartUT = playbackStartUT;
            intervalSeconds = baseIntervalSeconds;

            if (recIdx >= 0
                && committed != null
                && GhostPlaybackLogic.ShouldUseGlobalAutoLaunchQueue(rec))
            {
                var trajectories = new List<IPlaybackTrajectory>(committed.Count);
                for (int i = 0; i < committed.Count; i++)
                    trajectories.Add(committed[i]);

                if (GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule(
                        trajectories,
                        recIdx,
                        baseIntervalSeconds,
                        out GhostPlaybackLogic.AutoLoopLaunchSchedule autoSchedule))
                {
                    scheduleStartUT = autoSchedule.LaunchStartUT;
                    intervalSeconds = autoSchedule.LaunchCadenceSeconds;
                }
            }

            return true;
        }

        /// <summary>
        /// Resolve the full overlap schedule tuple for a recording: playbackStartUT (where the replay
        /// timeline begins), scheduleStartUT (the launch anchor), duration, the EFFECTIVE launch
        /// cadence (raised so <c>ceil(duration/cadence) &lt;= cap</c>, mirroring
        /// ParsekKSC.UpdateOverlapKsc + GhostPlaybackEngine.UpdateOverlapPlayback), and the
        /// cycleDuration the cycle math uses. A UNION of the two overlap sources (the unit branch is
        /// dispatched FIRST so a Mission member uses the unit's schedule even if it also carries its
        /// own <c>rec.LoopPlayback</c>): both converge on the SAME tuple shape, so the SAME
        /// <see cref="EnsureOverlapInstances"/> -&gt; GetActiveCycles -&gt;
        /// <see cref="DecideOverlapInstanceChanges"/> -&gt; ComputeOverlapCyclePlaybackUT path runs for
        /// both (no second create path). Returns false when the recording is neither.
        /// </summary>
        internal static bool ResolveOverlapSchedule(
            Recording rec, int recIdx, IReadOnlyList<Recording> committed,
            GhostPlaybackLogic.LoopUnitSet loopUnits,
            out double playbackStartUT, out double scheduleStartUT,
            out double duration, out double effectiveCadence, out double cycleDuration)
        {
            playbackStartUT = 0.0;
            scheduleStartUT = 0.0;
            duration = 0.0;
            effectiveCadence = 0.0;
            cycleDuration = 0.0;
            if (rec == null)
                return false;

            // Source (b): Mission-unit self-overlap — dispatched first.
            if (TryResolveUnitOverlapSchedule(
                    rec, recIdx, loopUnits,
                    out playbackStartUT, out scheduleStartUT,
                    out duration, out effectiveCadence, out cycleDuration))
                return true;

            // Source (a): standalone per-recording overlap loop.
            if (!GhostPlaybackEngine.ShouldLoopPlayback(rec))
                return false;

            playbackStartUT = GhostPlaybackEngine.EffectiveLoopStartUT(rec);
            duration = GhostPlaybackEngine.EffectiveLoopDuration(rec);
            if (duration <= LoopTiming.MinLoopDurationSeconds)
                return false;

            if (!TryResolveOverlapScheduleAnchor(
                    rec, recIdx, committed, out scheduleStartUT, out double intervalSeconds))
                return false;

            effectiveCadence = GhostPlaybackLogic.ComputeEffectiveLaunchCadence(
                intervalSeconds, duration, GhostPlayback.MaxOverlapGhostsPerRecording);
            cycleDuration = Math.Max(effectiveCadence, LoopTiming.MinCycleDuration);
            return true;
        }

        /// <summary>
        /// Source (b) schedule: resolve the overlap tuple for a Mission-unit self-overlap member,
        /// mirroring the flight engine's unit-overlap branch EXACTLY (GhostPlaybackEngine.cs:2163-2183
        /// for the member window + schedule anchor, GhostPlaybackEngine.cs:3570-3571 for the cap
        /// re-clamp inside UpdateOverlapPlayback):
        ///   memberStartUT   = unit.MemberStartUT(recIdx, rec.StartUT)   (interval-level start trim)
        ///   memberEndUT     = unit.MemberEndUT(recIdx, rec.EndUT)       (interval-level end trim)
        ///   duration        = memberEndUT - memberStartUT
        ///   playbackStartUT = memberStartUT
        ///   scheduleStartUT = ComputeMemberOverlapScheduleStartUT(PhaseAnchorUT, SpanStartUT, memberStartUT)
        ///   effectiveCadence= ComputeEffectiveLaunchCadence(OverlapCadenceSeconds, duration, cap)
        ///   cycleDuration   = max(effectiveCadence, MinCycleDuration)
        /// SPAN-CLOCK CAVEAT: the overlap path uses this RAW schedule with ComputeOverlapCyclePlaybackUT,
        /// NOT ResolveTrackingStationSampleUT / ResolveMapPresenceSampleUT (the span-clock NON-overlap
        /// path with loiter-cut / arrival-hold / re-aim remapping). The engine's overlap branch
        /// deliberately skips the span clock (GhostPlaybackEngine.cs:2152-2156); re-aim / zero-drift
        /// units are non-overlapping by construction (cadence raised &gt;= span) so they never enter
        /// here. Returns false when the recording is not a self-overlapping unit member.
        /// </summary>
        private static bool TryResolveUnitOverlapSchedule(
            Recording rec, int recIdx, GhostPlaybackLogic.LoopUnitSet loopUnits,
            out double playbackStartUT, out double scheduleStartUT,
            out double duration, out double effectiveCadence, out double cycleDuration)
        {
            playbackStartUT = 0.0;
            scheduleStartUT = 0.0;
            duration = 0.0;
            effectiveCadence = 0.0;
            cycleDuration = 0.0;
            if (rec == null || loopUnits == null)
                return false;
            if (!loopUnits.TryGetUnitForMember(recIdx, out GhostPlaybackLogic.LoopUnit unit))
                return false;
            if (!GhostPlaybackLogic.UnitMemberOverlaps(unit))
                return false;

            double memberStartUT = unit.MemberStartUT(recIdx, rec.StartUT);
            double memberEndUT = unit.MemberEndUT(recIdx, rec.EndUT);
            duration = memberEndUT - memberStartUT;
            if (duration <= LoopTiming.MinLoopDurationSeconds)
                return false;

            playbackStartUT = memberStartUT;
            scheduleStartUT = GhostPlaybackLogic.ComputeMemberOverlapScheduleStartUT(
                unit.PhaseAnchorUT, unit.SpanStartUT, memberStartUT);
            effectiveCadence = GhostPlaybackLogic.ComputeEffectiveLaunchCadence(
                unit.OverlapCadenceSeconds, duration, GhostPlayback.MaxOverlapGhostsPerRecording);
            cycleDuration = Math.Max(effectiveCadence, LoopTiming.MinCycleDuration);
            return true;
        }
    }
}
