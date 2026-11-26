using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // Deploy motion timing for the families whose recorded event marks the END of stock's motion.
    //
    // The recorder's pollers emit a ModuleDeployablePart, gear, RetractableLadder, cargo bay or
    // standalone ModuleAnimateGeneric event only once stock's motion has finished (they skip
    // EXTENDING / RETRACTING, "Deploying...", a bay mid-travel, an animTime between the ends).
    // So for those families the motion played over [eventUT - motion, eventUT], and the ghost
    // plays it there: a look-ahead arms the transition before the event is consumed, and the
    // consumption of the event itself lands on the same window. Every number is a pure function
    // of the recorded UTs, so a spawn, a scrub, a loop cycle and a warp jump show the same pose.
    // The families whose event marks the START (animation groups, aero and control surfaces,
    // the robot arm scanner) keep the transition that starts at the event and runs the clip.
    // A top-level type, not nested in GhostPlaybackLogic: GhostPlaybackState holds a list of
    // them, and a state field naming GhostPlaybackLogic would close a type cycle.
    /// <summary>One end-markable deploy event of a trajectory.</summary>
    internal struct EndMarkedDeployEntry
    {
        public int eventIndex;
        public double eventUT;
        public uint partPersistentId;
        public bool deployed;
        /// <summary>The UT of the previous end-markable event of the same part; NaN when none.</summary>
        public double floorUT;
    }

    internal static partial class GhostPlaybackLogic
    {
        /// <summary>
        /// The longest motion an end-marked family plays, and so the look-ahead bound: the 30 s
        /// clip clamp at the slowest stock speed factor (a landing leg extends at 0.5).
        /// </summary>
        internal const double MaxEndMarkedMotionSeconds = 60.0;

        /// <summary>A speed factor at or below this is unreadable; the clip then plays at 1.</summary>
        internal const float MinEndMarkedSpeedFactor = 0.01f;

        /// <summary>The transition an end-marked event asks for.</summary>
        internal struct EndMarkedTransitionPlan
        {
            public bool alreadyThere;
            public double startUT;
            public double durationSeconds;
            public float startFraction;
            public float targetFraction;
        }

        /// <summary>
        /// Pure: the deploy event types an end-marked family receives, and the state each asks
        /// for. Other types are never moved.
        /// </summary>
        internal static bool TryClassifyEndMarkableDeployEvent(PartEventType type, out bool deployed)
        {
            switch (type)
            {
                case PartEventType.DeployableExtended:
                case PartEventType.GearDeployed:
                case PartEventType.CargoBayOpened:
                    deployed = true;
                    return true;
                case PartEventType.DeployableRetracted:
                case PartEventType.GearRetracted:
                case PartEventType.CargoBayClosed:
                    deployed = false;
                    return true;
                default:
                    deployed = false;
                    return false;
            }
        }

        /// <summary>True when the info's family records the END of stock's motion.</summary>
        internal static bool IsEndMarkedDeployable(DeployableGhostInfo info)
            => info != null
               && !double.IsNaN(info.extendMotionSeconds)
               && !double.IsNaN(info.retractMotionSeconds);

        /// <summary>
        /// Pure: stock's motion time for one direction. The clip (clamped as every deployable
        /// clip is) played at the module's own speed factor, over the share of the clip the
        /// motion travels (a deploy-limited bay stops short of the open end).
        /// </summary>
        internal static double ResolveEndMarkedMotionSeconds(float clipSeconds, float speedFactor, float travel)
        {
            double clip = ClampDeployableClipSeconds(clipSeconds);
            double speed = Math.Abs(speedFactor);
            if (float.IsNaN(speedFactor) || float.IsInfinity(speedFactor) || speed < MinEndMarkedSpeedFactor)
                speed = 1.0;
            double share = (float.IsNaN(travel) || float.IsInfinity(travel)) ? 1.0 : Math.Min(Math.Max(travel, 0f), 1f);
            return Math.Min(clip / speed * share, MaxEndMarkedMotionSeconds);
        }

        /// <summary>Marks an info as end-marked with its two motion times.</summary>
        internal static void StampEndMarkedMotion(
            DeployableGhostInfo info, float extendSpeedFactor, float retractSpeedFactor, float travel)
        {
            if (info == null) return;
            info.extendMotionSeconds = ResolveEndMarkedMotionSeconds(info.clipLengthSeconds, extendSpeedFactor, travel);
            info.retractMotionSeconds = ResolveEndMarkedMotionSeconds(info.clipLengthSeconds, retractSpeedFactor, travel);
        }

        /// <summary>Full motion time of one direction; 0 for an info that is not end-marked.</summary>
        internal static double EndMarkedMotionSecondsFor(DeployableGhostInfo info, bool deployed)
        {
            if (!IsEndMarkedDeployable(info)) return 0.0;
            return deployed ? info.extendMotionSeconds : info.retractMotionSeconds;
        }

        /// <summary>
        /// How long a full stowed &lt;-&gt; deployed motion of this info takes on the ghost: the
        /// end-marked motion time, else the clip. In-game cells size their probes with it.
        /// </summary>
        internal static double DeployMotionSecondsFor(DeployableGhostInfo info, bool deployed)
            => IsEndMarkedDeployable(info)
                ? EndMarkedMotionSecondsFor(info, deployed)
                : ClampDeployableClipSeconds(info != null ? info.clipLengthSeconds : 0f);

        /// <summary>
        /// Pure: every end-markable deploy event of a trajectory in event order, each with the
        /// UT of the same part's previous end-markable event (the floor its motion cannot start
        /// before). Derived from the recorded list alone, so a re-walk from the first event
        /// gets the same floors.
        /// </summary>
        internal static List<EndMarkedDeployEntry> BuildEndMarkedDeploySchedule(List<PartEvent> events)
        {
            var schedule = new List<EndMarkedDeployEntry>();
            if (events == null) return schedule;
            Dictionary<uint, double> lastByPart = null;
            for (int i = 0; i < events.Count; i++)
            {
                PartEvent evt = events[i];
                if (!TryClassifyEndMarkableDeployEvent(evt.eventType, out bool deployed)) continue;
                if (lastByPart == null) lastByPart = new Dictionary<uint, double>();
                double floor = lastByPart.TryGetValue(evt.partPersistentId, out double prev) ? prev : double.NaN;
                schedule.Add(new EndMarkedDeployEntry
                {
                    eventIndex = i,
                    eventUT = evt.ut,
                    partPersistentId = evt.partPersistentId,
                    deployed = deployed,
                    floorUT = floor,
                });
                lastByPart[evt.partPersistentId] = evt.ut;
            }
            return schedule;
        }

        /// <summary>Pure: the first schedule entry whose event index is at or after <paramref name="eventIndex"/>.</summary>
        internal static int LowerBoundEndMarkedByEventIndex(List<EndMarkedDeployEntry> schedule, int eventIndex)
        {
            if (schedule == null) return 0;
            int lo = 0, hi = schedule.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (schedule[mid].eventIndex < eventIndex) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// Pure: the floor of a recorded event, looked up by UT, part and direction; NaN when the
        /// schedule does not hold it (a hand-built event, a null schedule).
        /// </summary>
        internal static double FindEndMarkedFloorUT(List<EndMarkedDeployEntry> schedule, PartEvent evt, bool deployed)
        {
            if (schedule == null || schedule.Count == 0) return double.NaN;
            int lo = 0, hi = schedule.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (schedule[mid].eventUT < evt.ut) lo = mid + 1;
                else hi = mid;
            }
            for (int i = lo; i < schedule.Count && schedule[i].eventUT == evt.ut; i++)
            {
                if (schedule[i].partPersistentId == evt.partPersistentId && schedule[i].deployed == deployed)
                    return schedule[i].floorUT;
            }
            return double.NaN;
        }

        /// <summary>
        /// Pure: the transition an end-marked event asks for. The motion ENDS at the event UT and
        /// lasts the full motion time scaled by the distance still to travel. It is clamped at
        /// the floor (the same part's previous event): a motion that would start before the
        /// previous motion ended starts at that event instead and runs faster, so it still ends
        /// at its own event UT. A motion that starts before the recording's first UT is NOT
        /// clamped: the ghost does not exist before then, and at its first frame it shows the
        /// pose stock had at that moment.
        /// </summary>
        internal static EndMarkedTransitionPlan PlanEndMarkedTransition(
            double eventUT, bool deployed, float currentFraction, double floorUT, double fullMotionSeconds)
        {
            float target = deployed ? 1f : 0f;
            float start = float.IsNaN(currentFraction) ? 1f - target : Math.Min(Math.Max(currentFraction, 0f), 1f);
            var plan = new EndMarkedTransitionPlan
            {
                startFraction = start,
                targetFraction = target,
                startUT = eventUT,
                durationSeconds = 0.0,
            };
            float span = Math.Abs(target - start);
            if (span <= 1e-4f)
            {
                plan.alreadyThere = true;
                return plan;
            }
            if (double.IsNaN(eventUT) || double.IsInfinity(eventUT))
                return plan;

            double full = (double.IsNaN(fullMotionSeconds) || double.IsInfinity(fullMotionSeconds) || fullMotionSeconds < 0.0)
                ? 0.0 : Math.Min(fullMotionSeconds, MaxEndMarkedMotionSeconds);
            double startUT = eventUT - full * span;
            if (!double.IsNaN(floorUT) && !double.IsInfinity(floorUT) && startUT < floorUT)
                startUT = Math.Min(floorUT, eventUT);
            plan.startUT = startUT;
            plan.durationSeconds = eventUT - startUT;
            return plan;
        }

        /// <summary>
        /// Pure: the fraction of a transition with its own duration (the end-marked kind). The
        /// same shape as <see cref="ComputeDeployableTransitionFraction"/>: the start pose at or
        /// before the start, the target at or after the end, linear between.
        /// </summary>
        internal static float ComputeTransitionFractionOverDuration(
            double currentUT, double transitionStartUT,
            float startFraction, float targetFraction, double durationSeconds,
            out bool complete)
        {
            complete = true;
            startFraction = Math.Min(Math.Max(startFraction, 0f), 1f);
            targetFraction = Math.Min(Math.Max(targetFraction, 0f), 1f);
            if (Math.Abs(targetFraction - startFraction) <= 1e-4f)
                return targetFraction;
            if (double.IsNaN(currentUT) || double.IsInfinity(currentUT)
                || double.IsNaN(transitionStartUT) || double.IsInfinity(transitionStartUT)
                || double.IsNaN(durationSeconds) || durationSeconds <= 0.0)
            {
                return targetFraction;
            }
            double elapsed = currentUT - transitionStartUT;
            if (elapsed >= durationSeconds)
                return targetFraction;
            complete = false;
            if (elapsed <= 0.0)
                return startFraction;
            return startFraction + (targetFraction - startFraction) * (float)(elapsed / durationSeconds);
        }

        /// <summary>The fraction an info's in-flight transition shows at <paramref name="ut"/>, either kind.</summary>
        internal static float ActiveTransitionFractionAt(DeployableGhostInfo info, double ut, out bool complete)
        {
            if (!double.IsNaN(info.transitionDurationSeconds))
                return ComputeTransitionFractionOverDuration(
                    ut, info.transitionStartUT, info.transitionStartFraction,
                    info.transitionTargetFraction, info.transitionDurationSeconds, out complete);
            return ComputeDeployableTransitionFraction(
                ut, info.transitionStartUT, info.transitionStartFraction,
                info.transitionTargetFraction, info.clipLengthSeconds, out complete);
        }

        /// <summary>
        /// The fraction an end-marked event's motion starts from: where the info is, or where its
        /// in-flight transition stands at the event UT (an end-marked transition has ended by its
        /// own event, so a later event starts from that transition's target).
        /// </summary>
        internal static float EndMarkedStartFraction(DeployableGhostInfo info, double eventUT)
        {
            if (!info.transitionActive) return info.deployFraction;
            return ActiveTransitionFractionAt(info, eventUT, out _);
        }

        /// <summary>Pure: the look-ahead horizon, the longest end-marked motion over the infos; 0 when none.</summary>
        internal static double ComputeEndMarkedHorizonSeconds(Dictionary<uint, DeployableGhostInfo> infos)
        {
            if (infos == null) return 0.0;
            double horizon = 0.0;
            foreach (var kvp in infos)
            {
                DeployableGhostInfo info = kvp.Value;
                if (!IsEndMarkedDeployable(info)) continue;
                horizon = Math.Max(horizon, Math.Max(info.extendMotionSeconds, info.retractMotionSeconds));
            }
            return Math.Min(horizon, MaxEndMarkedMotionSeconds);
        }

        private static double ResolveEndMarkedHorizonSeconds(GhostPlaybackState state)
        {
            var infos = state.deployableInfos;
            if (infos == null || infos.Count == 0) return 0.0;
            if (!ReferenceEquals(state.endMarkedHorizonSource, infos))
            {
                state.endMarkedHorizonSource = infos;
                state.endMarkedHorizonSeconds = ComputeEndMarkedHorizonSeconds(infos);
            }
            return state.endMarkedHorizonSeconds;
        }

        private static void EnsureEndMarkedDeploySchedule(GhostPlaybackState state, List<PartEvent> events)
        {
            if (ReferenceEquals(state.endMarkedDeployScheduleSource, events)
                && state.endMarkedDeployScheduleCount == (events != null ? events.Count : -1)
                && state.endMarkedDeploySchedule != null)
                return;
            state.endMarkedDeploySchedule = BuildEndMarkedDeploySchedule(events);
            state.endMarkedDeployScheduleSource = events;
            state.endMarkedDeployScheduleCount = events != null ? events.Count : -1;
        }

        /// <summary>
        /// Called by <see cref="ApplyPartEvents"/> before its walk: builds the trajectory's
        /// end-marked schedule once when the ghost carries an end-marked family, so the walk's
        /// event applications read their floors from it.
        /// </summary>
        internal static void PrepareEndMarkedDeploySchedule(GhostPlaybackState state, IPlaybackTrajectory rec)
        {
            if (state == null || rec?.PartEvents == null) return;
            if (ResolveEndMarkedHorizonSeconds(state) <= 0.0) return;
            EnsureEndMarkedDeploySchedule(state, rec.PartEvents);
        }

        /// <summary>
        /// The consumption of an end-marked event: lands on the same window the look-ahead armed,
        /// or arms it now (a walk that passed the event in one step, a spawn after it).
        /// </summary>
        internal static GhostPartEventOutcome ApplyEndMarkedDeployEvent(
            GhostPlaybackState state, DeployableGhostInfo info, PartEvent evt, bool deployed)
        {
            float target = deployed ? 1f : 0f;
            if (info.transitionActive && info.transitionEventUT == evt.ut
                && Math.Abs(info.transitionTargetFraction - target) <= 1e-4f)
            {
                info.currentDeployed = deployed;
                return HasDeployablePose(info) ? GhostPartEventOutcome.Applied : GhostPartEventOutcome.NoResolvedVisual;
            }

            float startFraction = EndMarkedStartFraction(info, evt.ut);
            double floor = FindEndMarkedFloorUT(state.endMarkedDeploySchedule, evt, deployed);
            EndMarkedTransitionPlan plan = PlanEndMarkedTransition(
                evt.ut, deployed, startFraction, floor, EndMarkedMotionSecondsFor(info, deployed));
            if (plan.alreadyThere)
            {
                if (!info.transitionActive)
                    info.currentDeployed = deployed;
                return HasDeployablePose(info) ? GhostPartEventOutcome.Applied : GhostPartEventOutcome.NoResolvedVisual;
            }

            ArmEndMarkedTransition(state, info, plan, evt.ut, deployed);
            return ApplyDeployableFraction(info, plan.startFraction)
                ? GhostPartEventOutcome.Applied
                : GhostPartEventOutcome.NoResolvedVisual;
        }

        private static void ArmEndMarkedTransition(
            GhostPlaybackState state, DeployableGhostInfo info, EndMarkedTransitionPlan plan,
            double eventUT, bool deployed)
        {
            info.transitionActive = true;
            info.transitionStartUT = plan.startUT;
            info.transitionStartFraction = plan.startFraction;
            info.transitionTargetFraction = plan.targetFraction;
            info.transitionDurationSeconds = plan.durationSeconds;
            info.transitionEventUT = eventUT;
            info.currentDeployed = deployed;
            if (state.activeDeployableTransitions == null)
                state.activeDeployableTransitions = new List<DeployableGhostInfo>();
            if (!state.activeDeployableTransitions.Contains(info))
                state.activeDeployableTransitions.Add(info);
        }

        /// <summary>
        /// Called by <see cref="ApplyPartEvents"/> after its walk: arms every end-marked event
        /// still ahead of the cursor whose motion has already begun at <paramref name="currentUT"/>.
        /// Only the next event of a part can qualify (a later one starts no earlier than that
        /// one's UT, which is still ahead). <see cref="UpdateActiveDeployables"/> then poses it.
        /// </summary>
        internal static void ArmUpcomingEndMarkedDeployMotions(
            int recIdx, GhostPlaybackState state, IPlaybackTrajectory rec, double currentUT)
        {
            if (state?.deployableInfos == null || rec?.PartEvents == null) return;
            double horizon = ResolveEndMarkedHorizonSeconds(state);
            if (horizon <= 0.0) return;
            EnsureEndMarkedDeploySchedule(state, rec.PartEvents);
            List<EndMarkedDeployEntry> schedule = state.endMarkedDeploySchedule;
            if (schedule == null || schedule.Count == 0) return;

            int armed = 0;
            for (int i = LowerBoundEndMarkedByEventIndex(schedule, state.partEventIndex); i < schedule.Count; i++)
            {
                EndMarkedDeployEntry entry = schedule[i];
                if (entry.eventUT > currentUT + horizon) break;
                if (!state.deployableInfos.TryGetValue(entry.partPersistentId, out DeployableGhostInfo info)
                    || !IsEndMarkedDeployable(info))
                    continue;
                if (info.transitionActive && info.transitionEventUT == entry.eventUT) continue;
                if (info.breakSubtreeHidden) continue;

                EndMarkedTransitionPlan plan = PlanEndMarkedTransition(
                    entry.eventUT, entry.deployed, EndMarkedStartFraction(info, entry.eventUT),
                    entry.floorUT, EndMarkedMotionSecondsFor(info, entry.deployed));
                if (plan.alreadyThere || plan.startUT > currentUT) continue;

                ArmEndMarkedTransition(state, info, plan, entry.eventUT, entry.deployed);
                armed++;
                ParsekLog.VerboseRateLimited("GhostVisual", $"deploy-motion-end-{recIdx}-{entry.partPersistentId}",
                    string.Format(CultureInfo.InvariantCulture,
                        "Deploy motion armed ahead of its event: rec={0} pid={1} target={2} start={3:F2} end={4:F2} " +
                        "duration={5:F2}s floor={6} at={7:F2}",
                        recIdx, entry.partPersistentId, entry.deployed ? "deployed" : "stowed",
                        plan.startUT, entry.eventUT, plan.durationSeconds,
                        double.IsNaN(entry.floorUT) ? "none" : entry.floorUT.ToString("F2", CultureInfo.InvariantCulture),
                        currentUT), 5.0);
            }
        }
    }
}
