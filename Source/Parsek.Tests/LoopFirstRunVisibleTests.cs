using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Operator ruling 2026-09-24 (todo LOOP-ARMED-REWIND-FIRST-RUN-NOT-RENDERED): with a mission
    /// loop armed, the first run is the real flight and plays as a normal ghost; the loop copies
    /// begin after it. Every scene publishes <see cref="GhostPlaybackLogic.LoopUnitSet.LiveAt"/>
    /// (through <see cref="MissionLoopUnitBuilder.ResolveLiveUnits"/>) as its per-frame unit set,
    /// so a unit before its first loop instance is absent and its members play as ordinary
    /// recordings. These cells pin the predicate, the view and its memo, and the consumers the
    /// view feeds (the engine's first-run spawn seam, the map / Tracking Station sample seam).
    /// </summary>
    [Collection("Sequential")]
    public class LoopFirstRunVisibleTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LoopFirstRunVisibleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            MissionLoopUnitBuilder.SuppressLogging = false;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // Span [1000, 1100] (the recorded first run); the loop anchor floors at the span end or
        // later (MissionLoopUnitBuilder step 7b-i), here 1150.
        private const double SpanStart = 1000.0;
        private const double SpanEnd = 1100.0;
        private const double Anchor = 1150.0;

        private static GhostPlaybackLogic.LoopUnit Unit(
            int owner, int[] members, double spanStart = SpanStart, double spanEnd = SpanEnd,
            double anchor = Anchor)
        {
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: owner, memberIndices: members, spanStartUT: spanStart,
                spanEndUT: spanEnd, cadenceSeconds: spanEnd - spanStart, phaseAnchorUT: anchor);
        }

        private static GhostPlaybackLogic.LoopUnitSet Set(params GhostPlaybackLogic.LoopUnit[] units)
        {
            var byOwner = new Dictionary<int, GhostPlaybackLogic.LoopUnit>();
            var ownerByIndex = new Dictionary<int, int>();
            foreach (var u in units)
            {
                byOwner.Add(u.OwnerIndex, u);
                foreach (int m in u.MemberIndices)
                    ownerByIndex.Add(m, u.OwnerIndex);
            }
            return new GhostPlaybackLogic.LoopUnitSet(byOwner, ownerByIndex);
        }

        #region IsLoopUnitBeforeFirstInstance

        [Fact]
        public void BeforeFirstInstance_BeforeAnchor_True()
        {
            var u = Unit(0, new[] { 0 });
            Assert.True(GhostPlaybackLogic.IsLoopUnitBeforeFirstInstance(900.0, u));
            Assert.True(GhostPlaybackLogic.IsLoopUnitBeforeFirstInstance(1050.0, u));
            Assert.True(GhostPlaybackLogic.IsLoopUnitBeforeFirstInstance(1149.99, u));
        }

        [Fact]
        public void BeforeFirstInstance_AtOrAfterAnchor_False()
        {
            // Mirror direction: from the anchor on the unit owns the member (the copies).
            var u = Unit(0, new[] { 0 });
            Assert.False(GhostPlaybackLogic.IsLoopUnitBeforeFirstInstance(Anchor, u));
            Assert.False(GhostPlaybackLogic.IsLoopUnitBeforeFirstInstance(5000.0, u));
        }

        [Fact]
        public void BeforeFirstInstance_DegenerateSpanOrUnsetAnchor_False()
        {
            Assert.False(GhostPlaybackLogic.IsLoopUnitBeforeFirstInstance(
                900.0, Unit(0, new[] { 0 }, spanStart: 1000.0, spanEnd: 1000.0)));
            Assert.False(GhostPlaybackLogic.IsLoopUnitBeforeFirstInstance(
                900.0, Unit(0, new[] { 0 }, anchor: double.NaN)));
            Assert.False(GhostPlaybackLogic.IsLoopUnitBeforeFirstInstance(
                double.NaN, Unit(0, new[] { 0 })));
        }

        #endregion

        #region LiveAt

        [Fact]
        public void LiveAt_EveryUnitStarted_ReturnsSameInstance()
        {
            // The steady state (a loop armed after its first run, GS-12's shape): the published
            // set is the build itself, no allocation, byte-identical behavior.
            var set = Set(Unit(0, new[] { 0, 1 }), Unit(5, new[] { 5 }, anchor: 1120.0));
            Assert.Same(set, set.LiveAt(Anchor));
            Assert.Same(set, set.LiveAt(9000.0));
        }

        [Fact]
        public void LiveAt_Empty_ReturnsEmpty()
        {
            Assert.Same(GhostPlaybackLogic.LoopUnitSet.Empty,
                GhostPlaybackLogic.LoopUnitSet.Empty.LiveAt(1.0));
        }

        [Fact]
        public void LiveAt_BeforeAnchor_LeavesTheWholeUnitOut()
        {
            var set = Set(Unit(0, new[] { 0, 1, 2 }));
            var live = set.LiveAt(1050.0);

            Assert.NotSame(set, live);
            Assert.Equal(0, live.Count);
            for (int m = 0; m <= 2; m++)
            {
                Assert.False(live.IsMember(m));
                Assert.False(live.TryGetUnitForMember(m, out _));
            }
            // The build itself is untouched (the Missions window and plan capture read it).
            Assert.True(set.IsMember(1));
        }

        [Fact]
        public void LiveAt_OnlyThePendingUnitIsLeftOut()
        {
            var started = Unit(7, new[] { 7, 8 }, anchor: 900.0);
            var pending = Unit(0, new[] { 0, 1 });
            var live = Set(pending, started).LiveAt(1050.0);

            Assert.Equal(1, live.Count);
            Assert.False(live.IsMember(0));
            Assert.False(live.IsMember(1));
            Assert.True(live.TryGetUnitForMember(8, out var u));
            Assert.Equal(7, u.OwnerIndex);
        }

        [Fact]
        public void LiveAt_IsMemoizedUntilTheClockCrossesAnAnchor()
        {
            var set = Set(Unit(0, new[] { 0 }), Unit(3, new[] { 3 }, anchor: 1300.0));

            var a = set.LiveAt(1010.0);
            Assert.Same(a, set.LiveAt(1099.0));   // same pending subset -> same view
            Assert.Equal(0, a.Count);

            var b = set.LiveAt(1200.0);           // unit 0 crossed its anchor
            Assert.NotSame(a, b);
            Assert.True(b.IsMember(0));
            Assert.False(b.IsMember(3));
            Assert.Same(b, set.LiveAt(1250.0));

            Assert.Same(set, set.LiveAt(1300.0)); // both live -> the build

            var back = set.LiveAt(1010.0);        // a rewind back before both anchors
            Assert.Equal(0, back.Count);
            Assert.False(back.IsMember(0));
        }

        [Fact]
        public void CountBeforeFirstInstance_CountsPendingUnits()
        {
            var set = Set(Unit(0, new[] { 0 }), Unit(3, new[] { 3 }, anchor: 1300.0));
            Assert.Equal(2, set.CountBeforeFirstInstance(1000.0));
            Assert.Equal(1, set.CountBeforeFirstInstance(1200.0));
            Assert.Equal(0, set.CountBeforeFirstInstance(1300.0));
        }

        #endregion

        #region ResolveLiveUnits (the scene publish seam)

        [Fact]
        public void ResolveLiveUnits_LogsOncePerViewChange()
        {
            var built = Set(Unit(0, new[] { 0, 1 }));

            var live = MissionLoopUnitBuilder.ResolveLiveUnits("Flight", built, null, 1010.0);
            Assert.Equal(0, live.Count);
            Assert.Contains(logLines, l => l.Contains("[MissionLoopFirstRun]")
                && l.Contains("scene=Flight") && l.Contains("live=0 beforeFirstInstance=1")
                && l.Contains("owner=#0 members=2 span=[1000.00,1100.00] anchor=1150.00")
                && l.Contains("first run plays as an ordinary recording until the anchor"));

            int before = logLines.Count;
            var again = MissionLoopUnitBuilder.ResolveLiveUnits("Flight", built, live, 1050.0);
            Assert.Same(live, again);
            Assert.Equal(before, logLines.Count); // unchanged view: no per-frame line

            var handover = MissionLoopUnitBuilder.ResolveLiveUnits("Flight", built, again, 1150.0);
            Assert.Same(built, handover);
            Assert.Contains(logLines, l => l.Contains("[MissionLoopFirstRun]")
                && l.Contains("live=1 beforeFirstInstance=0"));
        }

        [Fact]
        public void ResolveLiveUnits_LoopArmedAfterFirstRun_Silent()
        {
            // GS-12's shape: the anchor is already behind the clock at the first publish.
            var built = Set(Unit(0, new[] { 0 }));
            var live = MissionLoopUnitBuilder.ResolveLiveUnits("KSC", built, null, 2000.0);
            Assert.Same(built, live);
            Assert.DoesNotContain(logLines, l => l.Contains("[MissionLoopFirstRun]"));
        }

        #endregion

        #region Consumers of the published view

        [Fact]
        public void EngineFirstRunSeam_BeforeAnchor_LeavesTheSpawnToTheOrdinaryCompletion()
        {
            // Pre-anchor the member is ordinary: the ordinary past-end completion owns its spawn,
            // so the loop seam must not queue a second (LoopFirstRun) completion.
            var engine = new GhostPlaybackEngine(positioner: null);
            var traj = new MockTrajectory().WithTimeRange(SpanStart, SpanEnd);
            var built = Set(Unit(0, new[] { 0 }));
            var flags = new TrajectoryPlaybackFlags
            {
                needsSpawn = true, chainEndUT = SpanEnd, recordingId = "lfv-rec",
            };

            double ut = 1101.0; // past the member's end, before the anchor
            engine.SetLoopUnits(built.LiveAt(ut));
            Assert.False(engine.TryQueueLoopFirstRunSpawn(0, traj, flags,
                new FrameContext { currentUT = ut, warpRate = 1f },
                GhostPlaybackEngine.ResolveGhostActivationStartUT(traj), hasPointData: true));
            Assert.Empty(engine.PendingCompletedEventsForTesting);
            Assert.False(engine.IsLoopUnitMember(0));
        }

        [Fact]
        public void EngineFirstRunSeam_PastAnchorStillUnspawned_KeepsTheLoopSeam()
        {
            // Mirror direction (#1778): a clock that jumps straight past the anchor (a TimeJump)
            // never ran the ordinary path, so the loop seam still spawns the first run once.
            var engine = new GhostPlaybackEngine(positioner: null);
            var traj = new MockTrajectory().WithTimeRange(SpanStart, SpanEnd);
            var built = Set(Unit(0, new[] { 0 }));
            var flags = new TrajectoryPlaybackFlags
            {
                needsSpawn = true, chainEndUT = SpanEnd, recordingId = "lfv-rec",
            };

            double ut = 1160.0;
            engine.SetLoopUnits(built.LiveAt(ut));
            Assert.True(engine.IsLoopUnitMember(0));
            Assert.True(engine.TryQueueLoopFirstRunSpawn(0, traj, flags,
                new FrameContext { currentUT = ut, warpRate = 1f },
                GhostPlaybackEngine.ResolveGhostActivationStartUT(traj), hasPointData: true));
            Assert.Single(engine.PendingCompletedEventsForTesting);
            Assert.True(engine.PendingCompletedEventsForTesting[0].LoopFirstRun);
        }

        [Fact]
        public void MapSampleSeam_BeforeAnchor_ReadsAsAnOrdinaryRecording()
        {
            // The one map / Tracking Station / marker / polyline seam: with the published view a
            // pre-anchor member samples at the live UT and is NOT hidden (the first run's icon and
            // orbit line show). The full build would hide it (SpanClockUnresolved).
            var built = Set(Unit(0, new[] { 0 }));
            double ut = 1040.0;

            double effLive = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                0, SpanStart, SpanEnd, ut, built.LiveAt(ut), out bool hiddenLive);
            Assert.False(hiddenLive);
            Assert.Equal(ut, effLive);

            GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                0, SpanStart, SpanEnd, ut, built, out bool hiddenBuilt);
            Assert.True(hiddenBuilt);
        }

        [Fact]
        public void MapSampleSeam_AfterAnchor_IsTheLoopClock()
        {
            // Loop cycles after the first stay on the span clock (ghost-only copies).
            var built = Set(Unit(0, new[] { 0 }));
            double ut = Anchor + 30.0;

            double eff = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                0, SpanStart, SpanEnd, ut, built.LiveAt(ut), out bool hidden);
            Assert.False(hidden);
            Assert.Equal(SpanStart + 30.0, eff, 6);
        }

        [Fact]
        public void UnitRenderDecision_BeforeAnchorIsUnresolved_SoOnlyTheLiveViewRendersTheFirstRun()
        {
            // Documents WHY the view is needed: on the unit's own clock the whole first run is
            // SpanClockUnresolved (the engine used to skip and destroy the member there).
            var d = GhostPlaybackLogic.DecideUnitMemberRender(
                1050.0, Anchor, SpanStart, SpanEnd, SpanEnd - SpanStart, SpanStart, SpanEnd,
                out _, out _, out _);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.SpanClockUnresolved, d);
            Assert.True(GhostPlaybackLogic.IsLoopUnitBeforeFirstInstance(1050.0, Unit(0, new[] { 0 })));
        }

        #endregion
    }
}
