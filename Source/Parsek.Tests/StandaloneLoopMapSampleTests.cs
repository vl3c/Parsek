using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the map / Tracking Station sample UT of a Recordings-tab (standalone per-recording) loop
    /// (todo RECORDINGS-TAB-LOOP-NEVER-REPLAYS-ON-MAP): every map consumer resolves its sample UT through
    /// <see cref="GhostPlaybackLogic.ResolveTrackingStationSampleUT"/>, which used to return the live UT for
    /// any index that is not a Missions-loop unit member, so a looped flight replayed its mesh in flight
    /// and KSC while the map icon, orbit line and marker sat at the live UT past the recording's end.
    /// The scene-published set now carries the committed list as its standalone-loop source and the seam
    /// replays the loop on the clock the flight engine plays its mesh on.
    /// </summary>
    [Collection("Sequential")]
    public class StandaloneLoopMapSampleTests
    {
        // Recording [1000, 2000] looping every 3000 s: cycle k plays live [1000 + 3000k, 2000 + 3000k],
        // then a 2000 s pause until the next launch.
        private static MockTrajectory Looped(double interval = 3000.0)
            => new MockTrajectory { RecordingId = "loop-a", VesselName = "Looper" }
                .WithTimeRange(1000, 2000).WithLoop(interval);

        private static GhostPlaybackLogic.LoopUnitSet SetFor(IReadOnlyList<IPlaybackTrajectory> committed)
            => GhostPlaybackLogic.LoopUnitSet.Empty.WithStandaloneLoopSource(committed);

        private static double Sample(GhostPlaybackLogic.LoopUnitSet units, int i, IPlaybackTrajectory t,
            double liveUT, out bool hidden)
            => GhostPlaybackLogic.ResolveTrackingStationSampleUT(i, t.StartUT, t.EndUT, liveUT, units, out hidden);

        [Fact]
        public void SecondCycle_SamplesTheReplayedUT_NotTheLiveUT()
        {
            var rec = Looped();
            var committed = new List<IPlaybackTrajectory> { rec };

            // The old contract (no source): the live UT, far past the recording's end.
            double old = Sample(GhostPlaybackLogic.LoopUnitSet.Empty, 0, rec, 4500.0, out bool oldHidden);
            Assert.Equal(4500.0, old, 9);
            Assert.False(oldHidden);

            double eff = Sample(SetFor(committed), 0, rec, 4500.0, out bool hidden);
            Assert.False(hidden);
            Assert.Equal(1500.0, eff, 9); // cycle 1, 500 s in
        }

        [Fact]
        public void FirstRun_IsTheLiveUT()
        {
            var rec = Looped();
            double eff = Sample(SetFor(new List<IPlaybackTrajectory> { rec }), 0, rec, 1700.0, out bool hidden);
            Assert.False(hidden);
            Assert.Equal(1700.0, eff, 9);
        }

        [Fact]
        public void PauseBetweenCycles_AndBeforeStart_AreHidden()
        {
            var rec = Looped();
            var units = SetFor(new List<IPlaybackTrajectory> { rec });
            Sample(units, 0, rec, 2500.0, out bool pauseHidden);
            Assert.True(pauseHidden);
            Sample(units, 0, rec, 500.0, out bool beforeHidden);
            Assert.True(beforeHidden);
        }

        [Fact]
        public void MatchesTheFlightEngineLoopClock_AcrossASweep()
        {
            var rec = Looped(2500.0);
            var units = SetFor(new List<IPlaybackTrajectory> { rec });
            var engine = new GhostPlaybackEngine(positioner: null);
            for (double ut = 1000.0; ut < 12000.0; ut += 137.0)
            {
                Assert.True(engine.TryComputeLoopPlaybackUT(
                    rec, ut, 30.0, out double engineUT, out _, out bool enginePause, 0));
                double eff = Sample(units, 0, rec, ut, out bool hidden);
                Assert.Equal(enginePause, hidden);
                if (!enginePause)
                    Assert.Equal(engineUT, eff, 9);
            }
        }

        [Fact]
        public void OverlapLoop_IsLeftToThePerInstanceSweep()
        {
            var rec = Looped(400.0); // period < duration: overlap
            double eff = Sample(SetFor(new List<IPlaybackTrajectory> { rec }), 0, rec, 4500.0, out bool hidden);
            Assert.False(hidden);
            Assert.Equal(4500.0, eff, 9);
        }

        [Fact]
        public void NonLoopingRecording_KeepsTheLiveUT()
        {
            var rec = new MockTrajectory { RecordingId = "plain" }.WithTimeRange(1000, 2000);
            double eff = Sample(SetFor(new List<IPlaybackTrajectory> { rec }), 0, rec, 4500.0, out bool hidden);
            Assert.False(hidden);
            Assert.Equal(4500.0, eff, 9);
        }

        [Fact]
        public void OutOfRangeIndex_KeepsTheLiveUT()
        {
            var rec = Looped();
            double eff = Sample(SetFor(new List<IPlaybackTrajectory> { rec }), 3, rec, 4500.0, out bool hidden);
            Assert.False(hidden);
            Assert.Equal(4500.0, eff, 9);
        }

        [Fact]
        public void UnitMember_KeepsTheMissionClock()
        {
            // A Missions-loop member is driven by its unit even when the recording also carries its own
            // loop flag (the engine's interception order): the standalone source must not change it.
            var rec = Looped();
            var committed = new List<IPlaybackTrajectory> { rec };
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 2000.0, cadenceSeconds: 1500.0, phaseAnchorUT: 1000.0);
            var plain = new GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { 0, unit } },
                new Dictionary<int, int> { { 0, 0 } });
            var withSource = plain.WithStandaloneLoopSource(committed);
            foreach (double ut in new[] { 1200.0, 2600.0, 4500.0, 5100.0 })
            {
                double a = Sample(plain, 0, rec, ut, out bool ha);
                double b = Sample(withSource, 0, rec, ut, out bool hb);
                Assert.Equal(a, b, 9);
                Assert.Equal(ha, hb);
            }
        }

        [Fact]
        public void AutoQueue_UsesTheSharedLaunchSlot()
        {
            var a = new MockTrajectory { RecordingId = "q-a" }.WithTimeRange(1000, 1100).WithLoop(0, LoopTimeUnit.Auto);
            var b = new MockTrajectory { RecordingId = "q-b" }.WithTimeRange(5000, 5100).WithLoop(0, LoopTimeUnit.Auto);
            var committed = new List<IPlaybackTrajectory> { a, b };
            Assert.True(GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule(
                committed, 1, 200.0, out GhostPlaybackLogic.AutoLoopLaunchSchedule slot));

            double live = slot.LaunchStartUT + slot.LaunchCadenceSeconds * 3 + 40.0;
            Assert.True(GhostPlaybackLogic.TryResolveStandaloneLoopSampleUT(
                b, 1, committed, 200.0, live, out double eff, out bool hidden));
            Assert.False(hidden);
            Assert.Equal(5040.0, eff, 9);
        }

        [Fact]
        public void AutoQueue_LeavesOutLoopUnitMembers_LikeTheEngine()
        {
            // A unit member that also carries its own Auto loop is scheduled by its unit, and the
            // flight engine and KSC leave it out of the global Auto queue. The map must too, or every
            // other Auto loop gets a different slot and cadence from the mesh it mirrors.
            var member = new MockTrajectory { RecordingId = "m" }.WithTimeRange(1000, 1020).WithLoop(0, LoopTimeUnit.Auto);
            var solo = new MockTrajectory { RecordingId = "s" }.WithTimeRange(1100, 1125).WithLoop(0, LoopTimeUnit.Auto);
            var committed = new List<IPlaybackTrajectory> { member, solo };
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1020.0, cadenceSeconds: 60.0, phaseAnchorUT: 1000.0);
            var units = new GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { 0, unit } },
                new Dictionary<int, int> { { 0, 0 } }).WithStandaloneLoopSource(committed);

            // The engine's queue for this set holds the solo alone: the same schedule as a list
            // without the member.
            var soloOnly = new List<IPlaybackTrajectory> { solo };
            double gap = GhostPlaybackLogic.ResolveGlobalAutoLoopIntervalSeconds();
            int differsFromUnexcluded = 0;
            for (double ut = 1100.0; ut <= 1500.0; ut += 1.0)
            {
                double eff = Sample(units, 1, solo, ut, out bool hidden);
                Assert.True(GhostPlaybackLogic.TryResolveStandaloneLoopSampleUT(
                    solo, 0, soloOnly, gap, ut, out double expected, out bool expectedHidden));
                Assert.Equal(expectedHidden, hidden);
                if (!hidden)
                    Assert.Equal(expected, eff, 9);

                GhostPlaybackLogic.TryResolveStandaloneLoopSampleUT(
                    solo, 1, committed, gap, ut, out double unexcluded, out bool unexcludedHidden);
                if (unexcludedHidden != hidden || (!hidden && System.Math.Abs(unexcluded - eff) > 1e-9))
                    differsFromUnexcluded++;
            }
            Assert.True(differsFromUnexcluded > 0,
                "the fixture must put the member in the unexcluded queue, or this test proves nothing");
        }

        [Fact]
        public void LiveAt_KeepsTheSource_AndTheViewIsMemoized()
        {
            var committed = new List<IPlaybackTrajectory> { Looped() };
            var set = SetFor(committed);
            Assert.Same(set, GhostPlaybackLogic.LoopUnitSet.Empty.WithStandaloneLoopSource(committed));
            Assert.Same(set, set.WithStandaloneLoopSource(committed));
            Assert.Same(committed, set.LiveAt(4500.0).StandaloneLoopSource);
            Assert.Null(GhostPlaybackLogic.LoopUnitSet.Empty.StandaloneLoopSource);

            var published = MissionLoopUnitBuilder.ResolveLiveUnits(
                "Test", GhostPlaybackLogic.LoopUnitSet.Empty, null, 4500.0, committed);
            Assert.Same(committed, published.StandaloneLoopSource);
            Assert.Null(MissionLoopUnitBuilder.ResolveLiveUnits(
                "Test", GhostPlaybackLogic.LoopUnitSet.Empty, null, 4500.0).StandaloneLoopSource);
        }
    }
}
