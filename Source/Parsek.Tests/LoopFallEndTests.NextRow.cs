using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    // The watch at a row change waits only for a copy it can be handed: the named launch's copy of the next
    // row exists, is the newest one, or starts within the look-ahead. Any other end holds the camera for a
    // moment and then lands on a copy that is drawn.
    public partial class LoopFallEndTests
    {
        private static List<IPlaybackTrajectory> ShortAscentTrajectories() => new List<IPlaybackTrajectory>
        {
            Ended(SpanStart, 51, null, "Ship", Ship), Ended(51, SpanEnd, TerminalState.Landed, "Ship", Ship)
        };

        // Watch launch 0's short ascent [31, 51] (period 30, transfer [51, 140.5]); more than 50x warp hides
        // the meshes from inside the ascent until launch 1 is in the transfer row, so launch 0's copy of it
        // is never built.
        [Fact]
        public void Watch_WarpPastTheNextRowsStart_HoldsAMomentAndLandsOnADrawnCopy()
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows(period: 30, firstEnd: 51, secondStart: 51);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            List<IPlaybackTrajectory> all = ShortAscentTrajectories();
            GhostPlaybackState ascent = Seed(engine, 0, 0);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(210, 0, 0));
            for (double now = 212; now < 252; now += 2)
            {
                SelfOverlapFrame(engine, probe, unit, 0, all, Watching(now, 0, 0, 100f), suppress: true);
                SelfOverlapFrame(engine, probe, unit, CoreIndex, all, Watching(now, 0, 0, 100f), suppress: true);
            }
            Assert.Same(ascent, engine.ghostStates[0]);
            Assert.False(engine.ghostStates.ContainsKey(CoreIndex));

            // 252: the warp ends. Production demotes launch 0 and spawns launch 1 (a spawn needs the game).
            engine.ghostStates.Remove(0);
            engine.overlapGhosts[0] = new List<GhostPlaybackState> { ascent };
            Seed(engine, 1, 0);
            Seed(engine, 1);
            logLines.Clear();
            long watching = 0;
            double holdUntil = double.NaN;
            int handoffs = 0, target = -1;
            long targetCycle = -1;
            double landed = double.NaN;
            for (double now = 252; now < 270 && target < 0; now += 0.02)
            {
                events.Clear();
                foreach (int i in new[] { 0, CoreIndex })
                {
                    SelfOverlapFrame(engine, probe, unit, i, all, Watching(now, 0, watching));
                    long newest = (long)Math.Floor((now - (i == 0 ? 200 : 220)) / 30);
                    if (!engine.ghostStates.ContainsKey(i) && (i == CoreIndex || (now - 200) % 30 < 20))
                        Seed(engine, newest, i);
                }
                handoffs += events.Count(e => e.StartsWith("handoff"));
                if (events.Contains("camera EndHoldStart cycle=0"))
                {
                    watching = GhostPlaybackEngine.WatchHoldCycleIndex;
                    holdUntil = now + GhostPlayback.OverlapExplosionHoldSeconds;
                }
                if (now >= holdUntil
                    && engine.TryResolveSelfOverlapRestart(0, all, now, out target, out targetCycle))
                    landed = now;
            }
            Assert.Equal(0, handoffs);
            Assert.DoesNotContain(logLines, l => l.Contains("unit watch retarget owner="));
            Assert.Contains(logLines, l => l.Contains("[CameraFollow]") && l.Contains(
                "unit watch: no copy of the next row to follow: #0 \"Ship\" cycle=0 reason=SkippedAtStart waitedFrames=0"));
            Assert.Contains("cycle=0", destroyed);
            Assert.Equal(CoreIndex, target);
            Assert.Equal(1, targetCycle);
            Assert.InRange(landed, 252, 252 + GhostPlayback.OverlapExplosionHoldSeconds + 0.05);
            Assert.True(engine.TryGetCopyOfCycle(CoreIndex, 1, out _));
        }

        private static TrajectoryPlaybackFlags[] Flags(int skipped = -1)
        {
            var flags = new TrajectoryPlaybackFlags[4];
            if (skipped >= 0) flags[skipped].skipGhost = true;
            return flags;
        }

        // The next row's ghost is unticked: it draws no copy, so nothing is held for one.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]   // the control
        public void Watch_NextRowUnticked_EndsThere(bool unticked)
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows();
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            engine.frameFlags = Flags(unticked ? CoreIndex : -1);
            List<IPlaybackTrajectory> all = TwoRowTrajectories();
            Seed(engine, 0, 0);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(249, 0, 0));
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(250.1, 0, 0));
            SelfOverlapFrame(engine, probe, unit, CoreIndex, all, Watching(250.1, 0, 0));
            if (unticked)
            {
                Assert.Equal(new[] { "camera EndHoldStart cycle=0", "expired cycle=0 explosion=False" }, events);
                Assert.False(engine.ghostStates.ContainsKey(0));
                Assert.Contains("watch=HoldThenRestart", Assert.Single(EndLines()));
            }
            else
            {
                Assert.All(events, e => Assert.Equal("handoff UnitHandoffRetarget #1 cycle=0", e));
                Assert.NotEmpty(events);
                Assert.True(engine.ghostStates[0].endedCopyHeld);
            }
        }

        // Unticked while the camera is already waiting (the mesh of the row was still being built).
        [Fact]
        public void Watch_NextRowUntickedDuringTheWait_EndsIt()
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows();
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            List<IPlaybackTrajectory> all = TwoRowTrajectories();
            Seed(engine, 0, 0);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(249, 0, 0));
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(250.1, 0, 0));
            Assert.True(engine.ghostStates[0].endedCopyHeld);
            events.Clear();
            engine.frameFlags = Flags(CoreIndex);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(250.2, 0, 0));
            Assert.Equal(new[] { "camera EndHoldStart cycle=0", "expired cycle=0 explosion=False" }, events);
            Assert.Contains(logLines, l => l.Contains("[CameraFollow]") && l.Contains(
                "unit watch: no copy of the next row to follow: #0 \"Ship\" cycle=0 reason=NotDrawn waitedFrames=0"));
            Assert.Single(EndLines());
        }

        // Nothing is counted while the next row has not started: the sample-sized gap is bounded by its own
        // look-ahead, at any frame rate.
        [Fact]
        public void Watch_GapBeforeTheNextRow_IsNotCountedAsAWait()
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows(secondStart: 83);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            List<IPlaybackTrajectory> all = TwoRowTrajectories();
            GhostPlaybackState ascent = Seed(engine, 0, 0);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(249, 0, 0));
            for (double now = 250.001; now < 251.99; now += 0.005)
                SelfOverlapFrame(engine, probe, unit, 0, all, Watching(now, 0, 0));
            Assert.Same(ascent, engine.ghostStates[0]);
            Assert.Equal(0, ascent.endedCopyWaitFrames);
            Assert.Empty(events);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(252.1, 0, 0));
            Assert.Equal(new[] { "handoff UnitHandoffRetarget #1 cycle=0" }, events);
        }

        // Two rows of the vessel follow the ascent; the one that starts later draws no ghost. The handoff
        // names the other: the window picker alone would name the later one and wait for it.
        [Fact]
        public void Watch_HandoffNamesTheRowThatHasACopy()
        {
            var unit = new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0, 1, 2 }, SpanStart, SpanEnd, SpanEnd - SpanStart, Anchor, OwnPeriod,
                new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>
                {
                    [0] = new GhostPlaybackLogic.LoopUnit.MemberWindow(SpanStart, 81),
                    [1] = new GhostPlaybackLogic.LoopUnit.MemberWindow(80, SpanEnd),
                    [2] = new GhostPlaybackLogic.LoopUnit.MemberWindow(81, SpanEnd)
                });
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            engine.frameFlags = Flags(2);
            List<IPlaybackTrajectory> all = TwoRowTrajectories();
            all[1] = Ended(80, SpanEnd, null, "Ship", Ship);
            all.Add(Ended(81, SpanEnd, null, "Ship", Ship));
            Seed(engine, 0, 0);
            Seed(engine, 0, 1);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(249.5, 0, 0));
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(250.1, 0, 0));
            Assert.Equal(new[] { "handoff UnitHandoffRetarget #1 cycle=0" }, events);
            engine.frameFlags = Flags();
            events.Clear();
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(250.2, 0, 0));
            Assert.Equal(new[] { "handoff UnitHandoffRetarget #2 cycle=0" }, events);
        }

        // A landed (stationary) row stays drawn above 50x; the next row moves, so it is hidden there and has
        // no copy to wait for.
        [Theory]
        [InlineData(100f, true)]
        [InlineData(1f, false)]
        public void Watch_NextRowHiddenByWarp_EndsThere(float warp, bool ends)
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows();
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            List<IPlaybackTrajectory> all = TwoRowTrajectories();
            ((MockTrajectory)all[0]).TrackSections.Add(new TrackSection
                { startUT = SpanStart, endUT = 81, environment = SegmentEnvironment.SurfaceStationary });
            Seed(engine, 0, 0);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(249, 0, 0, warp), suppress: warp > 50f);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(250.1, 0, 0, warp), suppress: warp > 50f);
            Assert.Equal(ends, events.Contains("camera EndHoldStart cycle=0"));
            Assert.Equal(!ends, events.Contains("handoff UnitHandoffRetarget #1 cycle=0"));
            Assert.Equal(!ends, engine.ghostStates.ContainsKey(0));
        }

        [Theory]
        [InlineData(0L, 249.9, "NotStarted")]    // the next row starts within the look-ahead
        [InlineData(0L, 246.0, "OutOfReach")]
        [InlineData(0L, 250.1, "Live")]
        [InlineData(0L, 309.4, "Live")]
        [InlineData(0L, 309.6, "OutOfReach")]    // the launch is over
        [InlineData(1L, 350.1, "Live")]          // each launch on its own clock
        [InlineData(1L, 250.1, "OutOfReach")]
        [InlineData(-1L, 250.1, "OutOfReach")]
        public void NextRow_ReadsTheWatchedLaunchsOwnClock(long cycle, double now, string expected)
        {
            Assert.Equal(expected, NextRow(TwoRows(), cycle, now).ToString());
            Assert.Equal(expected == "OutOfReach" ? expected : "NotDrawn",
                NextRow(TwoRows(), cycle, now, drawn: false).ToString());
            Assert.Equal(expected == "OutOfReach" ? expected : "WarpHidden",
                NextRow(TwoRows(), cycle, now, suppress: true).ToString());
        }

        private static GhostPlaybackLogic.NextRowCopy NextRow(
            GhostPlaybackLogic.LoopUnit unit, long cycle, double now, bool drawn = true, bool suppress = false,
            bool slot = false, bool list = false, MockTrajectory row = null)
        {
            return GhostPlaybackLogic.DecideNextRowCopy(
                unit, CoreIndex, row ?? Ended(81, SpanEnd, null, "Ship", Ship), cycle, now, drawn,
                suppress, suppress ? 100f : 1f, slot, list);
        }

        // Period 30, transfer [51, 140.5] from 220: at 252 launch 1's copy is the newest.
        [Fact]
        public void NextRow_OlderLaunch_IsFollowableOnlyWhileItsCopyExists()
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows(period: 30, firstEnd: 51, secondStart: 51);
            Assert.Equal(GhostPlaybackLogic.NextRowCopy.Live, NextRow(unit, 1, 252));
            Assert.Equal(GhostPlaybackLogic.NextRowCopy.SkippedAtStart, NextRow(unit, 0, 252));
            Assert.Equal(GhostPlaybackLogic.NextRowCopy.Live, NextRow(unit, 0, 252, list: true));
            Assert.Equal(GhostPlaybackLogic.NextRowCopy.Live, NextRow(unit, 0, 252, slot: true));
            Assert.Equal(GhostPlaybackLogic.NextRowCopy.Live, NextRow(unit, 0, 249.9));
            // a stationary row at high warp keeps the newest copy alone
            MockTrajectory parked = Ended(51, SpanEnd, null, "Ship", Ship);
            parked.TrackSections.Add(new TrackSection
                { startUT = 51, endUT = SpanEnd, environment = SegmentEnvironment.SurfaceStationary });
            Assert.Equal(GhostPlaybackLogic.NextRowCopy.Live, NextRow(unit, 1, 252, suppress: true, row: parked));
            Assert.Equal(GhostPlaybackLogic.NextRowCopy.SkippedAtStart,
                NextRow(unit, 0, 252, suppress: true, list: true, row: parked));
            foreach (GhostPlaybackLogic.NextRowCopy copy in Enum.GetValues(typeof(GhostPlaybackLogic.NextRowCopy)))
                Assert.Equal(
                    copy == GhostPlaybackLogic.NextRowCopy.Live || copy == GhostPlaybackLogic.NextRowCopy.NotStarted,
                    GhostPlaybackLogic.IsFollowable(copy));
        }

        // A fall row keeps the launches' cadence and drops the copies past the cap, listed or not.
        [Theory]
        [InlineData(19, "Live")]
        [InlineData(20, "PastCopyCap")]
        public void NextRow_PastTheCopyCap_IsNotFollowable(int launchesLater, string expected)
        {
            GhostPlaybackLogic.LoopUnit unit = Unit(6); // the core falls over [119, 332.4] from 288 + 6n
            double now = CoreSchedule + launchesLater * 6 + 1;
            Assert.Equal(expected, NextRow(
                unit, 0, now, list: true, row: Traj(CoreStart, CoreEnd, predictedEnd: true)).ToString());
        }

        [Fact]
        public void NextRow_OfAnotherVessel_OrNone_IsNothingToWaitFor()
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows();
            foreach (List<IPlaybackTrajectory> all in new[]
                     {
                         TwoRowTrajectories(secondGuid: "22222222222222222222222222222222"),
                         new List<IPlaybackTrajectory> { TwoRowTrajectories()[0], null }
                     })
            {
                events.Clear();
                var probe = new ZoneProbe();
                GhostPlaybackEngine engine = WatchEngine(unit, probe);
                Seed(engine, 0, 0);
                SelfOverlapFrame(engine, probe, unit, 0, all, Watching(249, 0, 0));
                SelfOverlapFrame(engine, probe, unit, 0, all, Watching(250.1, 0, 0));
                Assert.Contains("camera EndHoldStart cycle=0", events);
                Assert.DoesNotContain(logLines, l => l.Contains("no copy of the next row to follow"));
            }
        }

        // ---- an end whose anchor is gone, a mesh that comes late ----

        // A Unity object with a native pointer: Unity's null check reads it as alive in this host.
        private static T FakeUnityObject<T>() where T : UnityEngine.Object
        {
            var fake = (T)FormatterServices.GetUninitializedObject(typeof(T));
            FieldInfo ptr = typeof(UnityEngine.Object).GetField(
                "m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(ptr);
            ptr.SetValue(fake, new IntPtr(1));
            return fake;
        }

        private sealed class RetiringPositioner : IGhostPositioner
        {
            public ZoneRenderingResult ApplyZoneRendering(int index, GhostPlaybackState state,
                IPlaybackTrajectory traj, double distance, double playbackUT, int protectedIndex) =>
                throw new OperationCanceledException();
            public void InterpolateAndPosition(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut, bool suppressFx) { }
            public void InterpolateAndPositionRelative(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut, bool suppressFx, RelativeSectionPlaybackTarget target) { }
            public bool TryPositionFromBodyFixedPrimary(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT, RelativeSectionPlaybackTarget target, out double a, out double b) { a = b = double.NaN; return false; }
            public void PositionAtPoint(int index, IPlaybackTrajectory traj, GhostPlaybackState state, TrajectoryPoint point) { }
            public void PositionAtSurface(int index, IPlaybackTrajectory traj, GhostPlaybackState state) { }
            public void PositionFromOrbit(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut) { }
            public void PositionLoop(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut, bool suppressFx) => state.anchorRetiredThisFrame = true;
            public bool TryResolveExplosionAnchorPosition(int index, IPlaybackTrajectory traj, GhostPlaybackState state, out Vector3 world) { world = default(Vector3); return false; }
        }

        // The ended copy cannot be placed (its anchor is gone): no explosion and no expired event from a
        // stale position, as before, but the watch of it holds where the camera is instead of losing its
        // target that frame. Nobody else's camera moves.
        [Theory]
        [InlineData(0, TerminalState.Destroyed, "camera EndHoldStart cycle=0 unresolved=True")]
        [InlineData(0, TerminalState.Landed, "camera EndHoldStart cycle=0 unresolved=True")]
        [InlineData(0, null, "camera EndHoldStart cycle=0 unresolved=True")]   // a next row to wait for
        [InlineData(-1, TerminalState.Landed, null)]
        public void Watch_EndWithItsAnchorGone_HoldsTheCamera(int watched, TerminalState? terminal, string expected)
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows(period: 30, firstEnd: 51, secondStart: 51);
            var engine = new GhostPlaybackEngine(new RetiringPositioner());
            engine.DestroyGhostResourcesOverrideForTesting = s => destroyed.Add("cycle=" + s.loopCycleIndex);
            engine.OnOverlapExpired += e => events.Add("expired cycle=" + e.CycleIndex);
            engine.OnOverlapCameraAction += e => events.Add(
                "camera " + e.Action + " cycle=" + e.NewCycleIndex + " unresolved=" + e.AnchorUnresolved);
            engine.SetLoopUnits(SetOf(unit));
            List<IPlaybackTrajectory> all = ShortAscentTrajectories();
            all[0] = Ended(SpanStart, 51, terminal, "Ship", terminal.HasValue ? "22222222222222222222222222222222" : Ship);
            var copy = new GhostPlaybackState
            {
                vesselName = "Ship", loopCycleIndex = 0, fallClockOffsetUT = 200 - SpanStart,
                ghost = FakeUnityObject<GameObject>()
            };
            engine.overlapGhosts[0] = new List<GhostPlaybackState> { copy };
            engine.overlapGhosts[CoreIndex] = new List<GhostPlaybackState>
                { new GhostPlaybackState { vesselName = "Ship", loopCycleIndex = 0 } };
            SelfOverlapFrame(engine, new ZoneProbe(), unit, 0, all, Watching(252, watched, watched));
            Assert.Equal(expected == null ? new string[0] : new[] { expected }, events);
            Assert.Equal(new[] { "cycle=0" }, destroyed);
            Assert.Equal(0, Copies(engine, 0));
        }

        // A copy held before its mesh was built hides the mesh when it comes (hiding needs the game).
        [Fact]
        public void Watch_HeldCopy_HidesEveryMeshItGets()
        {
            string ends = ReadSource("Ghost", "GhostPlaybackEngine.LoopCopyEnds.cs");
            Assert.Contains(
                "if (ReferenceEquals(state.endedCopyHiddenMesh, state.ghost)) return; state.endedCopyHiddenMesh = state.ghost; StopVisualFxForFrame(state, index);", ends);
            Assert.True(ends.IndexOf("HideHeldCopy(index, state); } if (state.endedCopyHeld) return true;", StringComparison.Ordinal) > 0);
        }

        // ---- the watch controller, run ----

        private static Exception Run(object target, string method, params object[] args)
        {
            MethodInfo info = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(info);
            try
            {
                info.Invoke(target, args);
                return null;
            }
            catch (TargetInvocationException e)
            {
                return e.InnerException;
            }
        }

        // An edit of the loop ended the watched copy and the bridge found none of its row: a mission that
        // overlaps itself holds (the hold restarts on any of its copies); any other loop ends the watch.
        [Theory]
        [InlineData(100.0, 3, GhostPlaybackEngine.WatchHoldCycleIndex)]
        [InlineData(100.0, 0, -1L)]    // the bridge still waits
        [InlineData(300.0, 3, -1L)]    // a loop that does not overlap itself
        public void Watch_BridgeThatFindsNoCopy_BecomesAHold(double period, int framesWaited, long expected)
        {
            CommitTwoRows();
            try
            {
                GhostPlaybackEngine engine = Engine(TwoRows(period: period), new ZoneProbe());
                WatchModeController controller = Controller(engine, 0, -1);
                SetField(controller, "overlapCameraAnchor", FakeUnityObject<GameObject>());
                SetField(controller, "watchNoTargetFrames", framesWaited);
                SetField(controller, "overlapBridgeLastRetryFrame", -1);
                Exception thrown = Run(controller, "TryResolveOverlapBridgeRetarget");
                if (period < SpanEnd - SpanStart) Assert.Null(thrown);
                bool holds = expected == GhostPlaybackEngine.WatchHoldCycleIndex;
                if (period < SpanEnd - SpanStart)
                    Assert.Equal(0, controller.WatchedRecordingIndex);
                Assert.Equal(expected, controller.WatchedLoopCycleIndex);
                Assert.Equal(holds, GetField<double>(controller, "overlapRetargetAfterUT") > 0);
                Assert.Equal(holds, logLines.Any(l => l.Contains("[CameraFollow]")
                    && l.Contains("Overlap: bridge of #0 found no copy of its row - holding for the mission")));
            }
            finally
            {
                RecordingStore.ResetForTesting();
            }
        }

        // The end of another launch's copy never moves the camera; the watched one's end holds it.
        [Theory]
        [InlineData(3L, true)]
        [InlineData(2L, false)]
        [InlineData(4L, false)]
        public void Watch_EndHold_OnlyForTheWatchedLaunch(long endedCycle, bool holds)
        {
            CommitTwoRows();
            try
            {
                WatchModeController controller = Controller(Engine(TwoRows(), new ZoneProbe()), 0, 3);
                SetField(controller, "overlapRetargetAfterUT", -1.0);
                foreach (int index in new[] { CoreIndex, 0 })
                {
                    // Placing the camera anchor, the last step, needs the game.
                    Exception thrown = Run(controller, "HandleOverlapCameraAction", new CameraActionEvent
                    {
                        Index = index, Action = CameraActionType.EndHoldStart,
                        NewCycleIndex = endedCycle, HoldUntilUT = 400
                    });
                    bool held = holds && index == 0;
                    Assert.True(held || thrown == null, "" + thrown);
                    Assert.Equal(held ? GhostPlaybackEngine.WatchHoldCycleIndex : 3, controller.WatchedLoopCycleIndex);
                    Assert.Equal(held ? 400.0 : -1.0, GetField<double>(controller, "overlapRetargetAfterUT"));
                }
                Assert.Equal(holds, logLines.Any(l => l.Contains("[CameraFollow]")
                    && l.Contains("Overlap: camera holding at the end of #0 cycle=3")));
            }
            finally
            {
                RecordingStore.ResetForTesting();
            }
        }
    }
}
