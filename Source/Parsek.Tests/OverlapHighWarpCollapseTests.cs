using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// todo OVERLAP-MAP-GHOSTS-CHURN-AT-HIGH-WARP: the pure gate and decision behind the high-warp
    /// collapse of the per-instance overlap map presence (GhostMapPresence.OverlapHighWarp.cs). The
    /// live ProtoVessel re-point is Unity-bound; these pin when the collapse engages, what it keeps,
    /// re-points, creates and destroys, and that it removes the per-sweep create/destroy churn the
    /// ordinary per-instance decision produces once a sweep spans a launch cycle.
    /// </summary>
    [Collection("Sequential")]
    public class OverlapHighWarpCollapseTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public OverlapHighWarpCollapseTests()
        {
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // -----------------------------------------------------------------
        //  Gate
        // -----------------------------------------------------------------

        [Theory]
        [InlineData(1f)]
        [InlineData(10f)]
        [InlineData(50f)] // the threshold itself is not above it
        public void ShouldCollapse_AtOrBelowGhostHideWarp_False_EvenWhenSweepSpansManyCycles(float warp)
        {
            Assert.False(GhostMapPresence.ShouldCollapseOverlapToSingleInstance(warp, 10000.0, 6.0));
        }

        [Fact]
        public void ShouldCollapse_AboveThreshold_SweepShorterThanCycle_False()
        {
            // 100x at 60 fps: 1.67 game-s per sweep against a 6 s cadence keeps the N icons.
            Assert.False(GhostMapPresence.ShouldCollapseOverlapToSingleInstance(100f, 1.67, 6.0));
        }

        [Theory]
        [InlineData(6.0)]     // exactly one cycle per sweep
        [InlineData(16.7)]    // 1000x at 60 fps
        [InlineData(1667.0)]  // 100000x at 60 fps, past the whole recording
        public void ShouldCollapse_AboveThreshold_SweepSpansACycle_True(double sweepGameSeconds)
        {
            Assert.True(GhostMapPresence.ShouldCollapseOverlapToSingleInstance(1000f, sweepGameSeconds, 6.0));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-5.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void ShouldCollapse_DegenerateSweep_False(double sweepGameSeconds)
        {
            Assert.False(GhostMapPresence.ShouldCollapseOverlapToSingleInstance(1000f, sweepGameSeconds, 6.0));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void ShouldCollapse_DegenerateCycle_False(double cycle)
        {
            Assert.False(GhostMapPresence.ShouldCollapseOverlapToSingleInstance(1000f, 100.0, cycle));
        }

        [Fact]
        public void ResolveSweepGameSeconds_FirstSweepAndBackwardsClock_Zero()
        {
            Assert.Equal(0.0, GhostMapPresence.ResolveOverlapSweepGameSeconds(double.NaN, 500.0));
            Assert.Equal(0.0, GhostMapPresence.ResolveOverlapSweepGameSeconds(500.0, 500.0));
            Assert.Equal(0.0, GhostMapPresence.ResolveOverlapSweepGameSeconds(500.0, 400.0));
            Assert.Equal(0.0, GhostMapPresence.ResolveOverlapSweepGameSeconds(500.0, double.PositiveInfinity));
            Assert.Equal(16.5, GhostMapPresence.ResolveOverlapSweepGameSeconds(500.0, 516.5), 9);
        }

        // -----------------------------------------------------------------
        //  Decision
        // -----------------------------------------------------------------

        [Fact]
        public void DecideCollapsed_NoInstances_CreatesNewestWithinBudget()
        {
            GhostMapPresence.DecideCollapsedOverlapInstanceChanges(
                new long[0], 10, 20, 2,
                out long keep, out long repoint, out bool create, out List<long> destroy);
            Assert.Equal(GhostMapPresence.NoOverlapCycle, keep);
            Assert.Equal(GhostMapPresence.NoOverlapCycle, repoint);
            Assert.True(create);
            Assert.Empty(destroy);
        }

        [Fact]
        public void DecideCollapsed_NoInstances_ZeroBudget_NoCreate()
        {
            GhostMapPresence.DecideCollapsedOverlapInstanceChanges(
                new long[0], 10, 20, 0,
                out _, out _, out bool create, out List<long> destroy);
            Assert.False(create);
            Assert.Empty(destroy);
        }

        [Fact]
        public void DecideCollapsed_LiveInstances_KeepsNewestLive_DestroysEveryOther()
        {
            // 8 expired, 12 / 15 / 19 live: keep 19, destroy 8, 12, 15; nothing created or re-pointed.
            GhostMapPresence.DecideCollapsedOverlapInstanceChanges(
                new long[] { 12, 8, 19, 15 }, 10, 20, 2,
                out long keep, out long repoint, out bool create, out List<long> destroy);
            Assert.Equal(19L, keep);
            Assert.Equal(GhostMapPresence.NoOverlapCycle, repoint);
            Assert.False(create);
            Assert.Equal(new long[] { 8, 12, 15 }, destroy.OrderBy(c => c).ToArray());
        }

        [Fact]
        public void DecideCollapsed_AllExpired_RepointsNewest_DestroysTheRest()
        {
            GhostMapPresence.DecideCollapsedOverlapInstanceChanges(
                new long[] { 3, 7, 5 }, 10, 20, 2,
                out long keep, out long repoint, out bool create, out List<long> destroy);
            Assert.Equal(GhostMapPresence.NoOverlapCycle, keep);
            Assert.Equal(7L, repoint);
            Assert.False(create);
            Assert.Equal(new long[] { 3, 5 }, destroy.OrderBy(c => c).ToArray());
        }

        [Fact]
        public void DecideCollapsed_RepointIgnoresSpawnBudget()
        {
            // A re-point creates no vessel, so it runs even when the frame's spawn budget is spent.
            GhostMapPresence.DecideCollapsedOverlapInstanceChanges(
                new long[] { 4 }, 10, 20, 0,
                out _, out long repoint, out bool create, out List<long> destroy);
            Assert.Equal(4L, repoint);
            Assert.False(create);
            Assert.Empty(destroy);
        }

        [Fact]
        public void DecideCollapsed_InstanceAheadOfWindow_IsRepointed()
        {
            // A clock that went backwards leaves an instance above lastCycle: re-point it, never leak it.
            GhostMapPresence.DecideCollapsedOverlapInstanceChanges(
                new long[] { 30 }, 10, 20, 2,
                out long keep, out long repoint, out _, out List<long> destroy);
            Assert.Equal(GhostMapPresence.NoOverlapCycle, keep);
            Assert.Equal(30L, repoint);
            Assert.Empty(destroy);
        }

        [Fact]
        public void DecideCollapsed_EmptyWindow_DestroysAll()
        {
            GhostMapPresence.DecideCollapsedOverlapInstanceChanges(
                new long[] { 1, 2 }, 5, 4, 2,
                out long keep, out long repoint, out bool create, out List<long> destroy);
            Assert.Equal(GhostMapPresence.NoOverlapCycle, keep);
            Assert.Equal(GhostMapPresence.NoOverlapCycle, repoint);
            Assert.False(create);
            Assert.Equal(new long[] { 1, 2 }, destroy.OrderBy(c => c).ToArray());
        }

        // -----------------------------------------------------------------
        //  Churn: ordinary per-instance decision vs collapse, sweep by sweep
        // -----------------------------------------------------------------

        private const double ScheduleStartUT = 1000.0;
        private const double Duration = 120.0;   // 120 s recording
        private const double Cadence = 6.0;      // 20-copy cap -> cadence floor 6 s
        private const int Cap = 20;

        private static void Window(double ut, out long first, out long last)
        {
            GhostPlaybackLogic.GetActiveCycles(
                ut, ScheduleStartUT, ScheduleStartUT + Duration, Cadence, Cap, out first, out last);
        }

        [Fact]
        public void OrdinaryDecision_SweepSpanningACycle_CreatesAndDestroysEverySweep()
        {
            // The defect: at 1000x / 60 fps (16.7 game-s per sweep) the ordinary per-instance decision
            // mutates the map vessel set on every single sweep once warmed up.
            var live = new HashSet<long>();
            int sweepsWithMutation = 0, sweeps = 0;
            for (int i = 0; i < 200; i++)
            {
                double ut = ScheduleStartUT + 200.0 + i * 16.7;
                Window(ut, out long first, out long last);
                GhostMapPresence.DecideOverlapInstanceChanges(
                    live, first, last, GhostPlayback.MaxSpawnsPerFrame,
                    out List<long> create, out List<long> destroy);
                foreach (long c in destroy) live.Remove(c);
                foreach (long c in create) live.Add(c);
                if (i < 20) continue; // warm-up
                sweeps++;
                if (create.Count > 0 || destroy.Count > 0) sweepsWithMutation++;
            }
            Assert.Equal(sweeps, sweepsWithMutation);
        }

        [Fact]
        public void CollapsedDecision_SweepSpanningACycle_NoVesselMutationAfterTheFirstSweep()
        {
            // The fix: with a successful re-point the collapsed path creates one vessel on the first
            // sweep and then only keeps or re-points it (no create, no destroy) on every later sweep.
            var live = new HashSet<long>();
            int creates = 0, destroys = 0, repoints = 0, keeps = 0;
            for (int i = 0; i < 200; i++)
            {
                double ut = ScheduleStartUT + 200.0 + i * 16.7;
                Window(ut, out long first, out long last);
                Assert.True(GhostMapPresence.ShouldCollapseOverlapToSingleInstance(1000f, 16.7, Cadence));
                GhostMapPresence.DecideCollapsedOverlapInstanceChanges(
                    live, first, last, GhostPlayback.MaxSpawnsPerFrame,
                    out long keep, out long repoint, out bool create, out List<long> destroy);
                foreach (long c in destroy) { live.Remove(c); destroys++; }
                if (keep != GhostMapPresence.NoOverlapCycle) keeps++;
                if (repoint != GhostMapPresence.NoOverlapCycle)
                {
                    live.Remove(repoint);
                    live.Add(last);
                    repoints++;
                }
                if (create) { live.Add(last); creates++; }
                Assert.Single(live);
            }
            Assert.Equal(1, creates);
            Assert.Equal(0, destroys);
            Assert.True(repoints > 0);
            Assert.True(keeps > 0); // 16.7 s sweeps < 120 s duration: the kept vessel rides its cycle
        }

        [Fact]
        public void CollapsedDecision_EnteringFromN_Instances_KeepsOneAndDestroysTheRestOnce()
        {
            var live = new HashSet<long>();
            // Warm up at 1x-like sweeps so the full N-cycle set exists.
            for (int i = 0; i < 400; i++)
            {
                Window(ScheduleStartUT + i * 0.5, out long f, out long l);
                GhostMapPresence.DecideOverlapInstanceChanges(
                    live, f, l, GhostPlayback.MaxSpawnsPerFrame, out var cr, out var de);
                foreach (long c in de) live.Remove(c);
                foreach (long c in cr) live.Add(c);
            }
            int before = live.Count;
            Assert.True(before > 1);

            Window(ScheduleStartUT + 200.0 + 16.7, out long first, out long last);
            GhostMapPresence.DecideCollapsedOverlapInstanceChanges(
                live, first, last, GhostPlayback.MaxSpawnsPerFrame,
                out long keep, out long repoint, out bool create, out List<long> destroy);
            int survivors = (keep != GhostMapPresence.NoOverlapCycle ? 1 : 0)
                            + (repoint != GhostMapPresence.NoOverlapCycle ? 1 : 0);
            Assert.Equal(1, survivors);
            Assert.False(create);
            Assert.Equal(before - 1, destroy.Count);
        }

        // -----------------------------------------------------------------
        //  Sweep wiring + log line
        // -----------------------------------------------------------------

        [Fact]
        public void Sweep_StampsPreviousUT_AndResetClearsIt()
        {
            Assert.True(double.IsNaN(GhostMapPresence.LastOverlapSweepUTForTesting));
            GhostMapPresence.RunOverlapPerInstanceSweep(1234.5, new List<Recording>(), null);
            Assert.Equal(1234.5, GhostMapPresence.LastOverlapSweepUTForTesting);
            GhostMapPresence.ResetForTesting();
            Assert.True(double.IsNaN(GhostMapPresence.LastOverlapSweepUTForTesting));
        }

        [Fact]
        public void Sweep_NoCollapse_EmitsNoCollapseSummary()
        {
            GhostMapPresence.RunOverlapPerInstanceSweep(100.0, new List<Recording>(), null);
            GhostMapPresence.RunOverlapPerInstanceSweep(5000.0, new List<Recording>(), null);
            Assert.DoesNotContain(logLines, l => l.Contains("Overlap high-warp collapse"));
        }

        [Fact]
        public void CollapseSummary_IsInvariantCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string line = GhostMapPresence.FormatOverlapCollapseSummary(2, 1, 1, 3, 1, 1000f, 16.7);
                Assert.Contains("Overlap high-warp collapse: recordings=2 repointed=1 rebuilt=1 destroyed=3 created=1", line);
                Assert.Contains("warp=1000x", line);
                Assert.Contains("sweepGameSeconds=16.7", line);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }
}
