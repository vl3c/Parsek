using System;
using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for the general, reusable DEBUG warp control <see cref="MapRenderWarpControl"/>: the pure,
    /// Unity-free decision helpers (<see cref="MapRenderWarpControl.ComputeMaxWarpRate"/> distance-proportional cap,
    /// <see cref="MapRenderWarpControl.SelectRateIndexForCap"/> rate-level pick,
    /// <see cref="MapRenderWarpControl.SelectActiveWindow"/> active-window selection), the registry
    /// (RegisterWatchWindow upsert / Unregister / Clear), and the end-to-end per-frame
    /// <see cref="MapRenderWarpControl.Tick"/> drive through a FAKE seam (no Unity). The two scene addons and the live
    /// <c>TimeWarp</c> seam are exercised in-game. Migrated from the retired DescentWarpControlTests; the cap /
    /// select / 1,000,000x-straddle regressions are preserved with the generic renames.
    /// </summary>
    [Collection("Sequential")]
    public class MapRenderWarpControlTests : IDisposable
    {
        // Real cross-frame UTs from the 2026-06-20 logs: Trigger is LIVE (~3.96e9); the clip duration is the recorded
        // descent-end minus deorbit (RECORDED-frame). The descent-side recorded->live conversion is tested separately
        // in DescentTriggerTests; here every window is already a plain LIVE-frame [trigger, end].
        private const double Trigger = 3962534821.2722712;
        private const double Deorbit = 2570542380.9910212;
        private const double RecEnd = 2570562894.315805;
        private const double Clip = RecEnd - Deorbit;       // 20513.33s recorded clip duration
        private static double LiveEnd => Trigger + Clip;     // 3962555334.597...

        // The standard stock KSP high-warp rate table (ascending).
        private static readonly float[] Levels = { 1f, 5f, 10f, 50f, 100f, 1000f, 10000f, 100000f, 1000000f };

        private readonly List<string> logLines = new List<string>();

        public MapRenderWarpControlTests()
        {
            ResetStatics();
            // Sibling Sequential tests leave SuppressLogging=true in teardown; re-enable so the action log is captured.
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ResetStatics();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void ResetStatics()
        {
            MapRenderWarpControl.Clear();
            MapRenderWarpControl.Unwire();
            MapRenderWarpControl.DebugWarpEnabled = false;
            MapRenderWarpControl.ForceEnabledForTesting = false;
            // The tracer half of the IsActive gate: pin it off so the gated-off case is deterministic regardless of
            // what a sibling Sequential test left set, and restore off afterward so we never contaminate others.
            MapRenderTrace.ForceEnabledForTesting = false;
        }

        // Wire a fake Unity seam that records every SetRate index into the returned list.
        private static List<int> WireFakeSeam(float currentRate, int currentIndex)
        {
            var calls = new List<int>();
            MapRenderWarpControl.WarpRateProvider = () => currentRate;
            MapRenderWarpControl.RateIndexProvider = () => currentIndex;
            MapRenderWarpControl.RateLevelsProvider = () => Levels;
            MapRenderWarpControl.SetRateAction = idx => calls.Add(idx);
            return calls;
        }

        private static MapRenderWarpControl.WatchWindow W(double trigger, double end, string label) =>
            new MapRenderWarpControl.WatchWindow { triggerUT = trigger, windowEndUT = end, label = label };

        // === ComputeMaxWarpRate (the distance-proportional cap) ================================================

        [Fact]
        public void MaxWarpRate_PastTheWindow_NoCap()
        {
            Assert.True(double.IsPositiveInfinity(
                MapRenderWarpControl.ComputeMaxWarpRate(LiveEnd + 1.0, Trigger, LiveEnd)));
        }

        [Fact]
        public void MaxWarpRate_NaN_NoCap()
        {
            Assert.True(double.IsPositiveInfinity(
                MapRenderWarpControl.ComputeMaxWarpRate(Trigger, double.NaN, Trigger + Clip)));
        }

        [Fact]
        public void MaxWarpRate_InsideWindow_WatchableCap()
        {
            // Just inside the window: cap = span / WatchSeconds (so the moment plays over ~that many seconds).
            double cap = MapRenderWarpControl.ComputeMaxWarpRate(Trigger + 1.0, Trigger, LiveEnd);
            Assert.Equal(Clip / MapRenderWarpControl.WatchSeconds, cap, 6);
            Assert.True(cap * 0.5 < Clip, "in-window cap must leave many frames inside the span");
        }

        [Fact]
        public void MaxWarpRate_Approaching_TightensWithDistance()
        {
            // 2,000,000 s out: cap = distance / WorstFrameSeconds = 2e6 / 2 = 1,000,000.
            double cap = MapRenderWarpControl.ComputeMaxWarpRate(Trigger - 2_000_000.0, Trigger, LiveEnd);
            Assert.Equal(2_000_000.0 / MapRenderWarpControl.WorstFrameSeconds, cap, 3);
        }

        [Fact]
        public void MaxWarpRate_Approaching_CannotOvershoot_TheCoreInvariant()
        {
            // The property that defeats the lag spike: at the cap, a worst-case frame (WorstFrameSeconds long) advances
            // cap * WorstFrameSeconds <= the remaining distance, so it lands AT or before the window — never past.
            foreach (double distance in new[] { 50.0, 500.0, 20_000.0, 500_000.0, 5_000_000.0 })
            {
                double cap = MapRenderWarpControl.ComputeMaxWarpRate(Trigger - distance, Trigger, LiveEnd);
                Assert.True(cap * MapRenderWarpControl.WorstFrameSeconds <= distance + 1e-6,
                    $"a worst-case frame at the cap must not leap past the window (distance={distance})");
            }
        }

        [Fact]
        public void MaxWarpRate_NeverBelowRealtime()
        {
            // 1 s from the trigger: distance/WorstFrameSeconds < 1, floored to 1.0 (never below realtime).
            Assert.Equal(1.0, MapRenderWarpControl.ComputeMaxWarpRate(Trigger - 1.0, Trigger, LiveEnd));
        }

        // === SelectRateIndexForCap (rate-level pick) ==========================================================

        [Theory]
        [InlineData(1_000_000.0, 8)] // exactly the top level
        [InlineData(950_000.0, 7)]   // below 1M -> 100k
        [InlineData(1000.0, 5)]      // exactly 1000x
        [InlineData(999.0, 4)]       // below 1000 -> 100
        [InlineData(20.0, 2)]        // below 50 -> 10
        [InlineData(0.5, 0)]         // below realtime -> 1x floor
        public void SelectRateIndex_PicksHighestLevelAtOrBelowCap(double cap, int expected)
        {
            Assert.Equal(expected, MapRenderWarpControl.SelectRateIndexForCap(cap, Levels));
        }

        [Fact]
        public void SelectRateIndex_InfinityOrEmpty_TopOrZero()
        {
            Assert.Equal(Levels.Length - 1,
                MapRenderWarpControl.SelectRateIndexForCap(double.PositiveInfinity, Levels));
            Assert.Equal(0, MapRenderWarpControl.SelectRateIndexForCap(1000.0, new List<float>()));
            Assert.Equal(0, MapRenderWarpControl.SelectRateIndexForCap(1000.0, null));
        }

        // === The 2026-06-20 regression: 1,000,000x straddles the sub-frame window ============================

        [Fact]
        public void RealApproachAt1Mx_CapsBelow1Mx_SoTheWindowCannotBeStraddled()
        {
            // The exact failure: the player warps at 1,000,000x toward the moment; one frame (≈20,000 s, or a
            // lag-spike ≈400,000 s) jumps the entire 20,513 s window between two ticks -> SKIPPED every cycle.
            // 500,000 s out (well before the window): the cap is distance/WorstFrameSeconds = 250,000 -> index 7
            // (100,000x), already BELOW 1,000,000x, so the player is forced to decelerate long before a frame could
            // straddle the window.
            double cap = MapRenderWarpControl.ComputeMaxWarpRate(Trigger - 500_000.0, Trigger, LiveEnd);
            int targetIdx = MapRenderWarpControl.SelectRateIndexForCap(cap, Levels);
            Assert.True(targetIdx < 8, "approaching at 1,000,000x must be capped DOWN before the window");
            Assert.Equal(7, targetIdx); // 250,000 -> 100,000x
        }

        // === SelectActiveWindow (inside-preferred, nearest-upcoming, past skipped) =============================

        [Fact]
        public void SelectActiveWindow_InsideBeatsUpcoming()
        {
            var ws = new List<MapRenderWarpControl.WatchWindow> { W(100, 200, "inside"), W(300, 400, "upcoming") };
            Assert.True(MapRenderWarpControl.SelectActiveWindow(ws, 150, out var chosen));
            Assert.Equal("inside", chosen.label);
        }

        [Fact]
        public void SelectActiveWindow_NearestUpcomingPicked()
        {
            var ws = new List<MapRenderWarpControl.WatchWindow> { W(500, 600, "far"), W(300, 400, "near") };
            Assert.True(MapRenderWarpControl.SelectActiveWindow(ws, 100, out var chosen));
            Assert.Equal("near", chosen.label);
        }

        [Fact]
        public void SelectActiveWindow_PastSkipped()
        {
            var ws = new List<MapRenderWarpControl.WatchWindow> { W(100, 200, "past"), W(300, 400, "upcoming") };
            Assert.True(MapRenderWarpControl.SelectActiveWindow(ws, 250, out var chosen));
            Assert.Equal("upcoming", chosen.label); // past window (end 200 <= 250) skipped
        }

        [Fact]
        public void SelectActiveWindow_AllPast_ReturnsFalse()
        {
            var ws = new List<MapRenderWarpControl.WatchWindow> { W(100, 200, "past") };
            Assert.False(MapRenderWarpControl.SelectActiveWindow(ws, 999, out _));
        }

        [Fact]
        public void SelectActiveWindow_Empty_ReturnsFalse()
        {
            Assert.False(MapRenderWarpControl.SelectActiveWindow(new List<MapRenderWarpControl.WatchWindow>(), 100, out _));
            Assert.False(MapRenderWarpControl.SelectActiveWindow(null, 100, out _));
        }

        [Fact]
        public void ZeroWidthWindow_PinsOneXApproaching_FreeAtInstant()
        {
            // windowEndUT == triggerUT pins 1x at the instant (an SOI crossing).
            const double trig = 1000.0;
            // Just before the instant: the approaching cap floors to 1.0 (warp pinned to 1x).
            Assert.Equal(1.0, MapRenderWarpControl.ComputeMaxWarpRate(trig - 0.5, trig, trig));
            // At/after the instant: past-the-window -> no cap (you have already observed it at 1x).
            Assert.True(double.IsPositiveInfinity(MapRenderWarpControl.ComputeMaxWarpRate(trig, trig, trig)));
            var ws = new List<MapRenderWarpControl.WatchWindow> { W(trig, trig, "soi") };
            Assert.True(MapRenderWarpControl.SelectActiveWindow(ws, trig - 0.5, out var chosen)); // upcoming
            Assert.Equal("soi", chosen.label);
            Assert.False(MapRenderWarpControl.SelectActiveWindow(ws, trig, out _)); // now past
        }

        // === Registry: RegisterWatchWindow upsert / Unregister / Clear (driven through Tick) ===================

        [Fact]
        public void RegisterWatchWindow_SameLabel_ReplacesNotAppends()
        {
            MapRenderWarpControl.ForceEnabledForTesting = true;
            MapRenderWarpControl.DebugWarpEnabled = true;
            var setCalls = WireFakeSeam(currentRate: 1_000_000f, currentIndex: 8);
            const double cur = 1_000_000_000.0;
            // First a NEAR (tight) window, then re-register the SAME label with a FAR (loose) window. If the upsert
            // replaced, only the far window remains (cap 500,000 -> idx 7). If it had appended, the near window's
            // tighter cap (10,000 -> idx 6) would win the min — so idx 7 proves replace-not-append.
            MapRenderWarpControl.RegisterWatchWindow(cur + 20_000.0, cur + 20_000.0 + Clip, "k");
            MapRenderWarpControl.RegisterWatchWindow(cur + 1_000_000.0, cur + 1_000_000.0 + Clip, "k");
            MapRenderWarpControl.Tick(cur);
            Assert.Single(setCalls);
            Assert.Equal(7, setCalls[0]);
        }

        [Fact]
        public void Unregister_RemovesWindow_NoCapApplied()
        {
            MapRenderWarpControl.ForceEnabledForTesting = true;
            MapRenderWarpControl.DebugWarpEnabled = true;
            var setCalls = WireFakeSeam(1_000_000f, 8);
            const double cur = 1_000_000_000.0;
            MapRenderWarpControl.RegisterWatchWindow(cur + 20_000.0, cur + 20_000.0 + Clip, "k");
            MapRenderWarpControl.Unregister("k");
            MapRenderWarpControl.Tick(cur);
            Assert.Empty(setCalls); // no windows -> no active selection -> no cap
        }

        [Fact]
        public void Clear_RemovesAllWindows()
        {
            MapRenderWarpControl.ForceEnabledForTesting = true;
            MapRenderWarpControl.DebugWarpEnabled = true;
            var setCalls = WireFakeSeam(1_000_000f, 8);
            const double cur = 1_000_000_000.0;
            MapRenderWarpControl.RegisterWatchWindow(cur + 20_000.0, cur + 20_000.0 + Clip, "a");
            MapRenderWarpControl.RegisterWatchWindow(cur + 20_000.0, cur + 20_000.0 + Clip, "b");
            MapRenderWarpControl.Clear();
            MapRenderWarpControl.Tick(cur);
            Assert.Empty(setCalls);
        }

        // === Tick: end-to-end registry + select + cap through the fake seam ===================================

        [Fact]
        public void Tick_Approaching_CapsDownThroughRegistryAndSeam()
        {
            MapRenderWarpControl.ForceEnabledForTesting = true;
            MapRenderWarpControl.DebugWarpEnabled = true;
            var setCalls = WireFakeSeam(currentRate: 1_000_000f, currentIndex: 8);
            MapRenderWarpControl.RegisterWatchWindow(Trigger, LiveEnd, "descent.test");
            // 500,000 s out: cap = 250,000 -> index 7 (100,000x), below the current 1,000,000x.
            MapRenderWarpControl.Tick(Trigger - 500_000.0);
            Assert.Single(setCalls);
            Assert.Equal(7, setCalls[0]);
            Assert.Contains(logLines, l => l.Contains("[MapRenderWarp]") && l.Contains("debug warp cap")
                                           && l.Contains("descent.test"));
        }

        [Fact]
        public void Tick_MultiWindow_BindsToMostRestrictiveCap()
        {
            MapRenderWarpControl.ForceEnabledForTesting = true;
            MapRenderWarpControl.DebugWarpEnabled = true;
            var setCalls = WireFakeSeam(currentRate: 1_000_000f, currentIndex: 8);
            const double cur = 1_000_000_000.0;
            // Far window: 1,000,000 s out -> cap 500,000 -> idx 7. Near window: 20,000 s out -> cap 10,000 -> idx 6.
            // The binding cap is the MIN (near), so the target index is 6, not 7.
            MapRenderWarpControl.RegisterWatchWindow(cur + 1_000_000.0, cur + 1_000_000.0 + Clip, "far");
            MapRenderWarpControl.RegisterWatchWindow(cur + 20_000.0, cur + 20_000.0 + Clip, "near");
            MapRenderWarpControl.Tick(cur);
            Assert.Single(setCalls);
            Assert.Equal(6, setCalls[0]);
        }

        [Fact]
        public void Tick_AlreadySlowEnough_DoesNotRaise()
        {
            MapRenderWarpControl.ForceEnabledForTesting = true;
            MapRenderWarpControl.DebugWarpEnabled = true;
            var setCalls = WireFakeSeam(currentRate: 1f, currentIndex: 0); // already at 1x
            MapRenderWarpControl.RegisterWatchWindow(Trigger, LiveEnd, "descent.test");
            MapRenderWarpControl.Tick(Trigger - 500_000.0); // cap is idx 7 but current idx 0 -> never raise
            Assert.Empty(setCalls);
        }

        [Fact]
        public void Tick_PastAllWindows_NoCap()
        {
            MapRenderWarpControl.ForceEnabledForTesting = true;
            MapRenderWarpControl.DebugWarpEnabled = true;
            var setCalls = WireFakeSeam(1_000_000f, 8);
            MapRenderWarpControl.RegisterWatchWindow(Trigger, LiveEnd, "descent.test");
            MapRenderWarpControl.Tick(LiveEnd + 1.0); // past the window -> free warp
            Assert.Empty(setCalls);
        }

        [Fact]
        public void Tick_GatedOff_NeverChangesWarp()
        {
            var setCalls = WireFakeSeam(1_000_000f, 8);
            MapRenderWarpControl.RegisterWatchWindow(Trigger, LiveEnd, "descent.test");

            // Tracer/force ON but the master flag OFF -> inactive -> no action.
            MapRenderWarpControl.ForceEnabledForTesting = true;
            MapRenderWarpControl.DebugWarpEnabled = false;
            MapRenderWarpControl.Tick(Trigger - 500_000.0);
            Assert.Empty(setCalls);

            // Master flag ON but tracer/force OFF -> still inactive (deceleration needs BOTH).
            MapRenderWarpControl.ForceEnabledForTesting = false;
            MapRenderWarpControl.DebugWarpEnabled = true;
            MapRenderWarpControl.Tick(Trigger - 500_000.0);
            Assert.Empty(setCalls);
        }
    }
}
