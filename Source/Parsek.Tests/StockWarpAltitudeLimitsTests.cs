using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the stock-warp-limit capture (BetterTimeWarp compatibility) and the
    /// approach-altitude selection that prefers the stock snapshot over a mod-zeroed live limit.
    /// </summary>
    [Collection("Sequential")]
    public class StockWarpAltitudeLimitsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public StockWarpAltitudeLimitsTests()
        {
            StockWarpAltitudeLimits.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            StockWarpAltitudeLimits.ResetForTesting();
        }

        // --- SelectApproachAltitude (pure selection) ---

        [Fact]
        public void Select_PrefersStock_OverLiveAndRadius()
        {
            double alt = FlightRecorder.SelectApproachAltitude(
                stockLimit: 60000f, liveLimit: 0f, bodyRadius: 200000.0, out var source);
            Assert.Equal(60000.0, alt);
            Assert.Equal("stock", source);
        }

        [Fact]
        public void Select_FallsBackToLive_WhenNoStock()
        {
            double alt = FlightRecorder.SelectApproachAltitude(
                stockLimit: 0f, liveLimit: 45000f, bodyRadius: 200000.0, out var source);
            Assert.Equal(45000.0, alt);
            Assert.Equal("live", source);
        }

        [Fact]
        public void Select_FallsBackToRadius_WhenStockAndLiveZeroed()
        {
            // This is the BetterTimeWarp case with no snapshot: both stock and live are 0.
            // Mun radius 200km -> 200000 * 0.15 = 30000m.
            double alt = FlightRecorder.SelectApproachAltitude(
                stockLimit: 0f, liveLimit: 0f, bodyRadius: 200000.0, out var source);
            Assert.Equal(30000.0, alt);
            Assert.Equal("radius", source);
        }

        [Fact]
        public void Select_RadiusFallback_IsClamped()
        {
            // Tiny body clamps up to 5000m; huge body clamps down to 200000m.
            double low = FlightRecorder.SelectApproachAltitude(0f, 0f, 1000.0, out _);
            double high = FlightRecorder.SelectApproachAltitude(0f, 0f, 100000000.0, out _);
            Assert.Equal(5000.0, low);
            Assert.Equal(200000.0, high);
        }

        // --- Capture / TryGetStockLimit ---

        [Fact]
        public void Capture_StoresLimits_AndLogsCount()
        {
            int n = StockWarpAltitudeLimits.Capture(new[]
            {
                new KeyValuePair<string, float[]>("Mun", new[] { 0f, 0f, 0f, 0f, 60000f, 0f, 0f, 0f }),
                new KeyValuePair<string, float[]>("Minmus", new[] { 0f, 0f, 0f, 0f, 30000f, 0f, 0f, 0f }),
            }, "test");

            Assert.Equal(2, n);
            Assert.True(StockWarpAltitudeLimits.HasCaptured);
            Assert.Contains(logLines, l =>
                l.Contains("[StockWarpLimits]") && l.Contains("captured") && l.Contains("2 bodies"));

            Assert.True(StockWarpAltitudeLimits.TryGetStockLimit("Mun", 4, out var mun));
            Assert.Equal(60000f, mun);
        }

        [Fact]
        public void Capture_SkipsNullAndEmptyEntries()
        {
            int n = StockWarpAltitudeLimits.Capture(new[]
            {
                new KeyValuePair<string, float[]>("Mun", new[] { 0f, 0f, 0f, 0f, 60000f, 0f, 0f, 0f }),
                new KeyValuePair<string, float[]>("", new[] { 1f, 2f }),
                new KeyValuePair<string, float[]>("Ike", null),
            }, "test");

            Assert.Equal(1, n);
            Assert.False(StockWarpAltitudeLimits.TryGetStockLimit("Ike", 4, out _));
        }

        [Fact]
        public void Capture_ClonesArray_SoLaterMutationDoesNotLeak()
        {
            var live = new[] { 0f, 0f, 0f, 0f, 60000f, 0f, 0f, 0f };
            StockWarpAltitudeLimits.Capture(new[]
            {
                new KeyValuePair<string, float[]>("Mun", live),
            }, "test");

            // Simulate an in-place edit of the live array AFTER capture.
            live[4] = 0f;

            Assert.True(StockWarpAltitudeLimits.TryGetStockLimit("Mun", 4, out var mun));
            Assert.Equal(60000f, mun);
        }

        [Fact]
        public void Capture_IsWriteOnce_SecondCaptureCannotClobberSnapshot()
        {
            // BetterTimeWarp overrides the live arrays at MainMenu, after our PSystemReady capture.
            // If OnPSystemReady re-fires (Kopernicus PSystem rebuild), the second capture reads the
            // now-zeroed live arrays — it must NOT overwrite the genuine stock snapshot.
            StockWarpAltitudeLimits.Capture(new[]
            {
                new KeyValuePair<string, float[]>("Mun", new[] { 0f, 0f, 0f, 0f, 60000f, 0f, 0f, 0f }),
            }, "first");

            int n = StockWarpAltitudeLimits.Capture(new[]
            {
                new KeyValuePair<string, float[]>("Mun", new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f }),
            }, "second-rezeroed-by-mod");

            Assert.Equal(0, n);
            Assert.True(StockWarpAltitudeLimits.TryGetStockLimit("Mun", 4, out var mun));
            Assert.Equal(60000f, mun); // preserved, not clobbered with the zeroed value
        }

        [Fact]
        public void Capture_SnapshotSurvivesReferenceReassignment()
        {
            // BetterTimeWarp does `body.timeWarpAltitudeLimits = new float[]{...}` — it REASSIGNS the
            // reference rather than mutating in place. The clone-on-capture must defend against that.
            var live = new[] { 0f, 0f, 0f, 0f, 60000f, 0f, 0f, 0f };
            StockWarpAltitudeLimits.Capture(new[]
            {
                new KeyValuePair<string, float[]>("Mun", live),
            }, "test");

            live = new[] { 0f, 0f, 0f, 0f, 0f, 0f, 100000f, 2000000f }; // BTW-style reassignment

            Assert.True(StockWarpAltitudeLimits.TryGetStockLimit("Mun", 4, out var mun));
            Assert.Equal(60000f, mun);
        }

        [Fact]
        public void Capture_AllInvalid_ReturnsZero_AndDoesNotMarkCaptured()
        {
            int n = StockWarpAltitudeLimits.Capture(new[]
            {
                new KeyValuePair<string, float[]>("", new[] { 1f, 2f }),
                new KeyValuePair<string, float[]>("Ike", null),
            }, "test");

            Assert.Equal(0, n);
            Assert.False(StockWarpAltitudeLimits.HasCaptured);
        }

        [Fact]
        public void Select_NegativeStock_FallsThroughToLive()
        {
            // A negative limit means "unavailable" per the contract — must not be returned.
            double alt = FlightRecorder.SelectApproachAltitude(
                stockLimit: -5f, liveLimit: 45000f, bodyRadius: 200000.0, out var source);
            Assert.Equal(45000.0, alt);
            Assert.Equal("live", source);
        }

        [Fact]
        public void TryGetStockLimit_OutOfRangeOrUnknown_ReturnsFalse()
        {
            StockWarpAltitudeLimits.SeedForTesting("Mun", new[] { 0f, 0f, 0f, 0f, 60000f });
            Assert.False(StockWarpAltitudeLimits.TryGetStockLimit("Mun", 9, out _));
            Assert.False(StockWarpAltitudeLimits.TryGetStockLimit("Eve", 4, out _));
            Assert.False(StockWarpAltitudeLimits.TryGetStockLimit(null, 4, out _));
        }

        // --- End-to-end selection through the seeded cache ---

        [Fact]
        public void SeededStock_BeatsZeroedLive_LikeBetterTimeWarp()
        {
            // BetterTimeWarp zeroes the live limit; the captured stock value must win.
            StockWarpAltitudeLimits.SeedForTesting("Mun", new[] { 0f, 0f, 0f, 0f, 60000f, 0f, 0f, 0f });
            Assert.True(StockWarpAltitudeLimits.TryGetStockLimit("Mun", 4, out var stock));

            double alt = FlightRecorder.SelectApproachAltitude(
                stockLimit: stock, liveLimit: 0f, bodyRadius: 200000.0, out var source);
            Assert.Equal(60000.0, alt);
            Assert.Equal("stock", source);
        }
    }
}
