using System;
using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Deploy motion timing for the families whose recorded event marks the END of stock's motion
    /// (todo GHOST-DEPLOY-MOTION-TIMING-2026-11-26): the window rule, the speed and travel of the
    /// motion, the schedule and its floors, the look-ahead, and the families that must not move.
    /// Headless: infos carry no transforms, so the pose is read through the pure fraction rather
    /// than off a Transform (UpdateActiveDeployables retires an info with nothing to pose).
    /// </summary>
    [Collection("Sequential")]
    public class DeployMotionEndTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public DeployMotionEndTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private const uint Pid = 100000u;

        private static DeployableGhostInfo EndMarkedInfo(float clip, float extendSpeed = 1f, float retractSpeed = 1f, float travel = 1f)
        {
            var info = new DeployableGhostInfo
            {
                partPersistentId = Pid,
                transforms = new List<DeployableTransformState>(),
                clipLengthSeconds = clip,
            };
            GhostPlaybackLogic.StampEndMarkedMotion(info, extendSpeed, retractSpeed, travel);
            return info;
        }

        private static GhostPlaybackState StateWith(DeployableGhostInfo info)
            => new GhostPlaybackState
            {
                deployableInfos = new Dictionary<uint, DeployableGhostInfo> { [info.partPersistentId] = info },
            };

        private static PartEvent Evt(double ut, PartEventType type, uint pid = Pid)
            => new PartEvent { ut = ut, partPersistentId = pid, eventType = type };

        private static void Near(double expected, double actual, int precision)
            => Assert.Equal(expected, actual, precision);

        private static float FractionAt(DeployableGhostInfo info, double ut)
            => GhostPlaybackLogic.ActiveTransitionFractionAt(info, ut, out _);

        // ------------------------------------------------------------------ event types

        [Theory]
        [InlineData(PartEventType.DeployableExtended, true)]
        [InlineData(PartEventType.GearDeployed, true)]
        [InlineData(PartEventType.CargoBayOpened, true)]
        [InlineData(PartEventType.DeployableRetracted, false)]
        [InlineData(PartEventType.GearRetracted, false)]
        [InlineData(PartEventType.CargoBayClosed, false)]
        public void EndMarkableTypes_MapToTheirDirection(PartEventType type, bool deployed)
        {
            Assert.True(GhostPlaybackLogic.TryClassifyEndMarkableDeployEvent(type, out bool d));
            Assert.Equal(deployed, d);
        }

        [Theory]
        [InlineData(PartEventType.ParachuteDeployed)]
        [InlineData(PartEventType.LightOn)]
        [InlineData(PartEventType.RoboticPositionSample)]
        [InlineData(PartEventType.ShroudJettisoned)]
        [InlineData(PartEventType.FairingJettisoned)]
        [InlineData(PartEventType.DeployableBroken)]
        public void OtherTypes_AreNeverMoved(PartEventType type)
        {
            Assert.False(GhostPlaybackLogic.TryClassifyEndMarkableDeployEvent(type, out _));
        }

        // ------------------------------------------------------------------ motion seconds

        [Fact]
        public void MotionSeconds_IsTheClipAtTheModuleSpeedOverItsTravel()
        {
            // The stock landing leg: a 2.53 s clip at extendDurationFactor 0.5 took 5.05 s (PP-3).
            Near(5.06, GhostPlaybackLogic.ResolveEndMarkedMotionSeconds(2.53f, 0.5f, 1f), 3);
            Near(2.53, GhostPlaybackLogic.ResolveEndMarkedMotionSeconds(2.53f, 1f, 1f), 3);
            // A deploy-limited bay travels only to its stop.
            Near(3.6, GhostPlaybackLogic.ResolveEndMarkedMotionSeconds(8f, 1f, 0.45f), 3);
        }

        [Fact]
        public void MotionSeconds_UnreadableInputsFallBack()
        {
            Near(2.0, GhostPlaybackLogic.ResolveEndMarkedMotionSeconds(2f, float.NaN, 1f), 6);
            Near(2.0, GhostPlaybackLogic.ResolveEndMarkedMotionSeconds(2f, 0f, 1f), 6);
            Near(2.0, GhostPlaybackLogic.ResolveEndMarkedMotionSeconds(2f, -1f, 1f), 6);
            Near(2.0, GhostPlaybackLogic.ResolveEndMarkedMotionSeconds(2f, 1f, float.NaN), 6);
            // An unreadable clip is the 3 s default, as every deployable clip.
            Near(3.0, GhostPlaybackLogic.ResolveEndMarkedMotionSeconds(0f, 1f, 1f), 6);
            // The 30 s clip clamp at half speed is the 60 s cap.
            Near(60.0, GhostPlaybackLogic.ResolveEndMarkedMotionSeconds(100f, 0.5f, 1f), 6);
            Near(60.0, GhostPlaybackLogic.ResolveEndMarkedMotionSeconds(30f, 0.1f, 1f), 6);
        }

        [Fact]
        public void Stamp_SetsBothDirections_AndMarksTheInfo()
        {
            var info = EndMarkedInfo(2.53f, extendSpeed: 0.5f, retractSpeed: 1f);
            Assert.True(GhostPlaybackLogic.IsEndMarkedDeployable(info));
            Near(5.06, info.extendMotionSeconds, 3);
            Near(2.53, info.retractMotionSeconds, 3);
            Near(5.06, GhostPlaybackLogic.DeployMotionSecondsFor(info, true), 3);
            Near(2.53, GhostPlaybackLogic.DeployMotionSecondsFor(info, false), 3);
        }

        [Fact]
        public void UnstampedInfo_IsNotEndMarked_AndItsMotionIsTheClip()
        {
            var info = new DeployableGhostInfo { clipLengthSeconds = 4f };
            Assert.False(GhostPlaybackLogic.IsEndMarkedDeployable(info));
            Near(0.0, GhostPlaybackLogic.EndMarkedMotionSecondsFor(info, true), 6);
            Near(4.0, GhostPlaybackLogic.DeployMotionSecondsFor(info, true), 6);
        }

        [Theory]
        [InlineData(1f, 0.55f, 0.45f)]   // Mk2 / Mk3: closed at 1, stop mirrored to 0.55
        [InlineData(0f, 0.6f, 0.6f)]
        [InlineData(1f, float.NaN, 1f)]  // NaN row = no limit (null)
        public void CargoBayTravel_IsTheDistanceFromClosedToTheStop(float closedPosition, float stop, float expected)
        {
            float? s = float.IsNaN(stop) ? (float?)null : stop;
            Near(expected, GhostVisualBuilder.CargoBayMotionTravel(closedPosition, s), 4);
        }

        // ------------------------------------------------------------------ the window

        [Fact]
        public void Plan_EndsAtTheEvent_AndLastsTheMotion()
        {
            var plan = GhostPlaybackLogic.PlanEndMarkedTransition(100.0, true, 0f, double.NaN, 6.0);
            Assert.False(plan.alreadyThere);
            Near(94.0, plan.startUT, 9);
            Near(6.0, plan.durationSeconds, 9);
            Assert.Equal(0f, plan.startFraction);
            Assert.Equal(1f, plan.targetFraction);
        }

        [Fact]
        public void Plan_Retract_IsTheMirror()
        {
            var plan = GhostPlaybackLogic.PlanEndMarkedTransition(100.0, false, 1f, double.NaN, 4.0);
            Near(96.0, plan.startUT, 9);
            Near(4.0, plan.durationSeconds, 9);
            Assert.Equal(1f, plan.startFraction);
            Assert.Equal(0f, plan.targetFraction);
        }

        [Fact]
        public void Plan_PartWay_ScalesWithTheDistanceLeft()
        {
            var plan = GhostPlaybackLogic.PlanEndMarkedTransition(100.0, true, 0.5f, double.NaN, 6.0);
            Near(97.0, plan.startUT, 6);
            Near(3.0, plan.durationSeconds, 6);
        }

        [Fact]
        public void Plan_ClampsAtThePreviousEvent_AndRunsFasterToStillEndAtItsOwn()
        {
            // Extended at 10, retracted at 12 with a 5 s retract: it cannot start before 10.
            var plan = GhostPlaybackLogic.PlanEndMarkedTransition(12.0, false, 1f, 10.0, 5.0);
            Near(10.0, plan.startUT, 9);
            Near(2.0, plan.durationSeconds, 9);
        }

        [Fact]
        public void Plan_FloorAtOrAfterTheEvent_IsAnInstantArrival()
        {
            var plan = GhostPlaybackLogic.PlanEndMarkedTransition(12.0, true, 0f, 12.0, 5.0);
            Near(12.0, plan.startUT, 9);
            Near(0.0, plan.durationSeconds, 9);
            var later = GhostPlaybackLogic.PlanEndMarkedTransition(12.0, true, 0f, 13.0, 5.0);
            Near(12.0, later.startUT, 9);
            Near(0.0, later.durationSeconds, 9);
        }

        [Fact]
        public void Plan_BeforeTheRecordingStart_IsNotClamped()
        {
            // A recording that began at 0 while the part was already extending: the ghost first
            // shows the pose stock had at 0, three eighths of the way out.
            var plan = GhostPlaybackLogic.PlanEndMarkedTransition(5.0, true, 0f, double.NaN, 8.0);
            Near(-3.0, plan.startUT, 9);
            float atStart = GhostPlaybackLogic.ComputeTransitionFractionOverDuration(
                0.0, plan.startUT, plan.startFraction, plan.targetFraction, plan.durationSeconds, out _);
            Near(0.375f, atStart, 5);
        }

        [Fact]
        public void Plan_AlreadyThere_ArmsNothing()
        {
            Assert.True(GhostPlaybackLogic.PlanEndMarkedTransition(100.0, true, 1f, double.NaN, 6.0).alreadyThere);
            Assert.True(GhostPlaybackLogic.PlanEndMarkedTransition(100.0, false, 0f, double.NaN, 6.0).alreadyThere);
        }

        [Fact]
        public void FractionOverDuration_HoldsBeforeRunsBetweenAndLandsAfter()
        {
            Assert.Equal(0f, GhostPlaybackLogic.ComputeTransitionFractionOverDuration(93.0, 94.0, 0f, 1f, 6.0, out bool c0));
            Assert.False(c0);
            Near(0.5f, GhostPlaybackLogic.ComputeTransitionFractionOverDuration(97.0, 94.0, 0f, 1f, 6.0, out bool c1), 5);
            Assert.False(c1);
            Assert.Equal(1f, GhostPlaybackLogic.ComputeTransitionFractionOverDuration(100.0, 94.0, 0f, 1f, 6.0, out bool c2));
            Assert.True(c2);
            Near(0.25f, GhostPlaybackLogic.ComputeTransitionFractionOverDuration(97.0, 94.0, 1f, 0f, 4.0, out _), 5);
            Assert.Equal(1f, GhostPlaybackLogic.ComputeTransitionFractionOverDuration(50.0, 50.0, 0f, 1f, 0.0, out bool c3));
            Assert.True(c3);
        }

        // ------------------------------------------------------------------ the schedule

        [Fact]
        public void Schedule_CarriesEachPartsPreviousEventAsItsFloor()
        {
            var events = new List<PartEvent>
            {
                Evt(5.0, PartEventType.EngineIgnited),
                Evt(10.0, PartEventType.DeployableExtended),
                Evt(11.0, PartEventType.GearDeployed, 7u),
                Evt(12.0, PartEventType.LightOn),
                Evt(20.0, PartEventType.DeployableRetracted),
                Evt(30.0, PartEventType.GearRetracted, 7u),
            };
            var s = GhostPlaybackLogic.BuildEndMarkedDeploySchedule(events);
            Assert.Equal(4, s.Count);
            Assert.Equal(1, s[0].eventIndex);
            Assert.True(double.IsNaN(s[0].floorUT));
            Assert.True(double.IsNaN(s[1].floorUT));
            Near(10.0, s[2].floorUT, 9);
            Assert.False(s[2].deployed);
            Near(11.0, s[3].floorUT, 9);
            Assert.Equal(7u, s[3].partPersistentId);

            Near(10.0, GhostPlaybackLogic.FindEndMarkedFloorUT(s, events[4], false), 9);
            Assert.True(double.IsNaN(GhostPlaybackLogic.FindEndMarkedFloorUT(s, events[1], true)));
            Assert.True(double.IsNaN(GhostPlaybackLogic.FindEndMarkedFloorUT(s, Evt(99.0, PartEventType.DeployableExtended), true)));
            Assert.True(double.IsNaN(GhostPlaybackLogic.FindEndMarkedFloorUT(null, events[4], false)));

            Assert.Equal(0, GhostPlaybackLogic.LowerBoundEndMarkedByEventIndex(s, 0));
            Assert.Equal(2, GhostPlaybackLogic.LowerBoundEndMarkedByEventIndex(s, 3));
            Assert.Equal(4, GhostPlaybackLogic.LowerBoundEndMarkedByEventIndex(s, 6));
        }

        // ------------------------------------------------------------------ consumption

        [Fact]
        public void EndMarkedEvent_ArmsTheMotionThatEndsAtIt()
        {
            var info = EndMarkedInfo(6f);
            var state = StateWith(info);
            GhostPlaybackLogic.ApplyDeployableStateWithOutcome(
                state, Evt(100.0, PartEventType.DeployableExtended), deployed: true, immediate: false);

            Assert.True(info.transitionActive);
            Near(94.0, info.transitionStartUT, 9);
            Near(6.0, info.transitionDurationSeconds, 9);
            Near(100.0, info.transitionEventUT, 9);
            Near(0.5f, FractionAt(info, 97.0), 5);
            Assert.Equal(1f, FractionAt(info, 100.0));
        }

        [Fact]
        public void EndMarkedEvent_ReappliedIsIdempotent()
        {
            var info = EndMarkedInfo(6f);
            var state = StateWith(info);
            var e = Evt(100.0, PartEventType.DeployableExtended);
            GhostPlaybackLogic.ApplyDeployableStateWithOutcome(state, e, true, false);
            GhostPlaybackLogic.ApplyDeployableStateWithOutcome(state, e, true, false);
            Near(94.0, info.transitionStartUT, 9);
            Near(6.0, info.transitionDurationSeconds, 9);
            Assert.Single(state.activeDeployableTransitions);
        }

        [Fact]
        public void StartMarkedFamily_StillStartsAtTheEventAndRunsTheClip()
        {
            // An animation group (or any info no end-marked builder stamped): unchanged.
            var info = new DeployableGhostInfo
            {
                partPersistentId = Pid,
                transforms = new List<DeployableTransformState>(),
                clipLengthSeconds = 6f,
            };
            var state = StateWith(info);
            GhostPlaybackLogic.ApplyDeployableStateWithOutcome(
                state, Evt(100.0, PartEventType.DeployableExtended), true, false);
            Assert.True(info.transitionActive);
            Near(100.0, info.transitionStartUT, 9);
            Assert.True(double.IsNaN(info.transitionDurationSeconds));
            Near(0.5f, FractionAt(info, 103.0), 5);
        }

        [Fact]
        public void GearRetract_UsesItsOwnFactor_TheMirrorOfExtend()
        {
            var info = EndMarkedInfo(2.5f, extendSpeed: 0.5f, retractSpeed: 1f);
            var state = StateWith(info);
            info.deployFraction = 1f;
            GhostPlaybackLogic.ApplyDeployableStateWithOutcome(
                state, Evt(50.0, PartEventType.GearRetracted), false, false);
            Near(47.5, info.transitionStartUT, 6);
            Near(2.5, info.transitionDurationSeconds, 6);
            Near(0.5f, FractionAt(info, 48.75), 4);
        }

        [Fact]
        public void BackToBackEvents_InOneWalk_TheSecondStartsWhereTheFirstEnded()
        {
            var events = new List<PartEvent>
            {
                Evt(10.0, PartEventType.DeployableExtended),
                Evt(12.0, PartEventType.DeployableRetracted),
            };
            var info = EndMarkedInfo(5f);
            var state = StateWith(info);
            state.endMarkedDeploySchedule = GhostPlaybackLogic.BuildEndMarkedDeploySchedule(events);
            GhostPlaybackLogic.ApplyDeployableStateWithOutcome(state, events[0], true, false);
            GhostPlaybackLogic.ApplyDeployableStateWithOutcome(state, events[1], false, false);
            Near(10.0, info.transitionStartUT, 9);
            Near(2.0, info.transitionDurationSeconds, 9);
            Assert.Equal(1f, info.transitionStartFraction);
            Near(0.5f, FractionAt(info, 11.0), 5);
        }

        [Fact]
        public void SeedAtTheRecordingStart_ShowsTheStateAtOnce()
        {
            // A part already deployed when the recording began: its seed at t0 is a state, and the
            // ghost's first frame shows it deployed, never unfolding after the start.
            var info = EndMarkedInfo(6f);
            var state = StateWith(info);
            GhostPlaybackLogic.ApplyDeployableStateWithOutcome(
                state, Evt(0.0, PartEventType.DeployableExtended), true, false);
            Assert.Equal(1f, FractionAt(info, 0.0));
        }

        [Fact]
        public void ImmediateBaseline_ClearsTheEndMarkedTransition()
        {
            var info = EndMarkedInfo(6f);
            var state = StateWith(info);
            GhostPlaybackLogic.ApplyDeployableStateWithOutcome(state, Evt(100.0, PartEventType.DeployableExtended), true, false);
            GhostPlaybackLogic.ApplyDeployableStateWithOutcome(state, Evt(0.0, PartEventType.DeployableExtended), true, true);
            Assert.False(info.transitionActive);
            Assert.True(double.IsNaN(info.transitionDurationSeconds));
            Assert.True(double.IsNaN(info.transitionEventUT));
        }

        [Fact]
        public void LoopReset_ClearsTheEndMarkedTransition()
        {
            var info = EndMarkedInfo(6f);
            var state = StateWith(info);
            GhostPlaybackLogic.ApplyDeployableStateWithOutcome(state, Evt(100.0, PartEventType.DeployableExtended), true, false);
            GhostPlaybackLogic.ClearActiveDeployableTransitions(state);
            Assert.False(info.transitionActive);
            Assert.True(double.IsNaN(info.transitionDurationSeconds));
            Assert.True(double.IsNaN(info.transitionEventUT));
        }

        // ------------------------------------------------------------------ the look-ahead

        private static Recording RecWith(params PartEvent[] events)
            => new Recording { RecordingId = "deploy-motion-end", PartEvents = new List<PartEvent>(events) };

        [Fact]
        public void LookAhead_ArmsAMotionThatHasBegun_BeforeItsEvent()
        {
            var info = EndMarkedInfo(6f);
            var state = StateWith(info);
            var rec = RecWith(Evt(100.0, PartEventType.DeployableExtended));

            GhostPlaybackLogic.ArmUpcomingEndMarkedDeployMotions(3, state, rec, 93.0);
            Assert.False(info.transitionActive);

            GhostPlaybackLogic.ArmUpcomingEndMarkedDeployMotions(3, state, rec, 97.0);
            Assert.True(info.transitionActive);
            Near(94.0, info.transitionStartUT, 9);
            Near(0.5f, FractionAt(info, 97.0), 5);
            Assert.Contains(logLines, l => l.Contains("[GhostVisual]")
                && l.Contains("Deploy motion armed ahead of its event: rec=3 pid=100000 target=deployed start=94.00 end=100.00"));

            // The walk then consumes the event at its own UT: the window stays the same.
            GhostPlaybackLogic.ApplyDeployableStateWithOutcome(state, rec.PartEvents[0], true, false);
            Near(94.0, info.transitionStartUT, 9);
            Near(6.0, info.transitionDurationSeconds, 9);
        }

        [Fact]
        public void LookAhead_NeverArmsThePartsLaterEvent_BeforeTheEarlierEnds()
        {
            var info = EndMarkedInfo(6f);
            var state = StateWith(info);
            var rec = RecWith(
                Evt(100.0, PartEventType.DeployableExtended),
                Evt(103.0, PartEventType.DeployableRetracted));
            GhostPlaybackLogic.ArmUpcomingEndMarkedDeployMotions(0, state, rec, 99.0);
            Near(100.0, info.transitionEventUT, 9);
            Assert.Equal(1f, info.transitionTargetFraction);
        }

        [Fact]
        public void LookAhead_SkipsAStartMarkedFamily_AndAnUnrelatedPart()
        {
            var startMarked = new DeployableGhostInfo
            {
                partPersistentId = Pid,
                transforms = new List<DeployableTransformState>(),
                clipLengthSeconds = 6f,
            };
            var state = StateWith(startMarked);
            var rec = RecWith(Evt(100.0, PartEventType.DeployableExtended), Evt(100.0, PartEventType.GearDeployed, 5u));
            GhostPlaybackLogic.ArmUpcomingEndMarkedDeployMotions(0, state, rec, 97.0);
            Assert.False(startMarked.transitionActive);
        }

        [Fact]
        public void LookAhead_SkipsEventsTheCursorAlreadyPassed_AndABrokenPanel()
        {
            var info = EndMarkedInfo(6f);
            var state = StateWith(info);
            var rec = RecWith(Evt(100.0, PartEventType.DeployableExtended));
            state.partEventIndex = 1;
            GhostPlaybackLogic.ArmUpcomingEndMarkedDeployMotions(0, state, rec, 97.0);
            Assert.False(info.transitionActive);

            state.partEventIndex = 0;
            info.breakSubtreeHidden = true;
            GhostPlaybackLogic.ArmUpcomingEndMarkedDeployMotions(0, state, rec, 97.0);
            Assert.False(info.transitionActive);
        }

        [Fact]
        public void Horizon_IsTheLongestEndMarkedMotion_ZeroWithoutOne()
        {
            var infos = new Dictionary<uint, DeployableGhostInfo>
            {
                [1u] = EndMarkedInfo(2.5f, extendSpeed: 0.5f),
                [2u] = EndMarkedInfo(8f),
                [3u] = new DeployableGhostInfo { clipLengthSeconds = 20f },
            };
            Near(8.0, GhostPlaybackLogic.ComputeEndMarkedHorizonSeconds(infos), 6);
            infos.Remove(2u);
            Near(5.0, GhostPlaybackLogic.ComputeEndMarkedHorizonSeconds(infos), 6);
            infos.Remove(1u);
            Near(0.0, GhostPlaybackLogic.ComputeEndMarkedHorizonSeconds(infos), 6);
            Near(0.0, GhostPlaybackLogic.ComputeEndMarkedHorizonSeconds(null), 6);
        }
    
        // ------------------------------------------------------------------ wiring (source gates)

        private static string Source(params string[] parts)
        {
            string repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            var all = new List<string> { repoRoot, "Source", "Parsek" };
            all.AddRange(parts);
            string raw = System.IO.File.ReadAllText(System.IO.Path.Combine(all.ToArray()));
            return SourceScanText.StripCommentsAndMaskLiterals(raw.Replace("\r\n", "\n"));
        }

        // ApplyPartEvents needs a live ghost, so its wiring is pinned on the source: the schedule
        // is built before the walk and the look-ahead runs after the cursor moves, before the
        // pose pass.
        [Fact]
        public void ApplyPartEvents_BuildsTheScheduleBeforeTheWalk_AndLooksAheadAfterIt()
        {
            string src = Source("Ghost", "GhostPlaybackLogic.PartEvents.cs");
            int prepare = src.IndexOf("PrepareEndMarkedDeploySchedule(state, rec);", StringComparison.Ordinal);
            int walk = src.IndexOf("while (evtIdx < rec.PartEvents.Count", StringComparison.Ordinal);
            int finish = src.IndexOf("FinishPartEventWalk(recIdx, state, evtIdx", StringComparison.Ordinal);
            int arm = src.IndexOf("ArmUpcomingEndMarkedDeployMotions(recIdx, state, rec, currentUT);", StringComparison.Ordinal);
            int pose = src.IndexOf("UpdateActiveDeployables(state, currentUT);", finish, StringComparison.Ordinal);
            Assert.True(prepare > 0 && walk > prepare, "the schedule must be prepared before the walk");
            Assert.True(finish > walk && arm > finish && pose > arm,
                "the look-ahead must run after the cursor moves and before the deployable pose pass");
        }

        // Exactly the five families whose pollers emit at the END of stock's motion are stamped.
        [Fact]
        public void Builder_StampsExactlyTheFiveEndMarkedFamilies()
        {
            string src = Source("Ghost", "GhostVisualBuilder.PartInfoBuilders.cs");
            var stamped = new List<string>();
            int at = 0;
            while ((at = src.IndexOf("StampEndMarkedDeployMotion(deployableInfo, partName, persistentId, ", at, StringComparison.Ordinal)) >= 0)
            {
                at += "StampEndMarkedDeployMotion(deployableInfo, partName, persistentId, ".Length;
                stamped.Add(src.Substring(at, src.IndexOf(',', at) - at).Trim());
            }
            // Literals are masked by the scan, so the family label reads as a masked string.
            Assert.Equal(5, stamped.Count);
            string gear = src.Substring(src.IndexOf("as ModuleWheels.ModuleWheelDeployment", StringComparison.Ordinal));
            Assert.Contains("wheel.extendDurationFactor, wheel.retractDurationFactor", gear);
            string cargo = src.Substring(src.IndexOf("SampleCargoBayStates(", StringComparison.Ordinal));
            Assert.Contains("CargoBayMotionTravel(cargoBay.closedPosition, deployLimitStop)", cargo);
            foreach (string startMarked in new[] { "AnimationGroup", "AeroSurface", "ControlSurface", "RobotArmScanner" })
            {
                string raw = System.IO.File.ReadAllText(System.IO.Path.Combine(System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..")), "Source", "Parsek", "Ghost", "GhostVisualBuilder.PartInfoBuilders.cs"));
                Assert.DoesNotContain("StampEndMarkedDeployMotion(deployableInfo, partName, persistentId, \"" + startMarked + "\"", raw);
            }
        }

        // The end-marked branch sits after the immediate (baseline) snap, so a baseline and the
        // break-repair snap never take the motion window.
        [Fact]
        public void EndMarkedBranch_FollowsTheImmediateSnap()
        {
            string src = Source("Ghost", "GhostPlaybackLogic.HeatDeployablesLights.cs");
            int body = src.IndexOf("internal static GhostPartEventOutcome ApplyDeployableStateWithOutcome(", StringComparison.Ordinal);
            int snap = src.IndexOf("if (immediate)", body, StringComparison.Ordinal);
            int endMarked = src.IndexOf("if (IsEndMarkedDeployable(info))", body, StringComparison.Ordinal);
            Assert.True(snap > body && endMarked > snap);
        }
    }
}
