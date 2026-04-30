using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Rendering;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 7 (design doc §13.2, §18 Phase 7) tests for
    /// <see cref="TerrainCacheBuckets"/>. The cache must hit for repeat queries
    /// inside the same lat/lon bucket, miss across bucket boundaries, clear on
    /// scene transition, and warn-then-skip-insert past the safety cap.
    /// Touches shared static state (cache + ParsekLog sink) so runs in the
    /// Sequential collection.
    /// </summary>
    [Collection("Sequential")]
    public class TerrainCacheBucketsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CelestialBody fakeKerbin;

        public TerrainCacheBucketsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            // Note: TerrainCacheBuckets.Clear() gates its log line on
            // !RecordingStore.SuppressLogging. Leave SuppressLogging false here
            // so Clear_EmitsVerboseDiagnosticLine can observe the line.
            RecordingStore.SuppressLogging = false;
            TerrainCacheBuckets.ResetForTesting();
            fakeKerbin = TestBodyRegistry.CreateBody("Kerbin", radius: 600000.0, gravParameter: 3.5316e12);
        }

        public void Dispose()
        {
            TerrainCacheBuckets.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        // ----- bucket-key contract -----

        [Fact]
        public void ComputeBucketIndex_PositiveLatLon_TruncatesToBucketSize()
        {
            // BucketDegrees = 0.001; expected bucket index = floor(lat/0.001).
            (int latIdx, int lonIdx) = TerrainCacheBuckets.ComputeBucketIndex(0.0015, 0.0025);
            Assert.Equal(1, latIdx);
            Assert.Equal(2, lonIdx);
        }

        [Fact]
        public void ComputeBucketIndex_NegativeLat_FloorsTowardsNegativeInfinity()
        {
            // -0.0005 / 0.001 = -0.5 → floor(-0.5) = -1 (continuous boundary).
            (int latIdx, _) = TerrainCacheBuckets.ComputeBucketIndex(-0.0005, 0.0);
            Assert.Equal(-1, latIdx);
        }

        [Fact]
        public void ComputeBucketIndex_AdjacentLats_SameBucket()
        {
            // 0.0010 and 0.0019 both fall in bucket index 1.
            (int latA, _) = TerrainCacheBuckets.ComputeBucketIndex(0.0010, 0.0);
            (int latB, _) = TerrainCacheBuckets.ComputeBucketIndex(0.0019, 0.0);
            Assert.Equal(latA, latB);
        }

        // ----- cache hit / miss correctness -----

        [Fact]
        public void GetCachedSurfaceHeight_FirstCall_IsMiss_ResolvesAndCaches()
        {
            int resolverCalls = 0;
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) =>
            {
                resolverCalls++;
                return 100.0;
            };

            double height = TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.5, 1.0);

            Assert.Equal(100.0, height);
            Assert.Equal(1, resolverCalls);
            Assert.Equal(1, TerrainCacheBuckets.CachedBucketCount);
        }

        [Fact]
        public void GetCachedSurfaceHeight_RepeatCallSameBucket_IsHit_DoesNotCallResolver()
        {
            int resolverCalls = 0;
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) =>
            {
                resolverCalls++;
                return 200.0;
            };

            // Two calls within the same 0.001° × 0.001° bucket.
            double a = TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.5000, 1.0000);
            double b = TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.5005, 1.0007);

            Assert.Equal(200.0, a);
            Assert.Equal(200.0, b);
            Assert.Equal(1, resolverCalls);
            Assert.Equal(1, TerrainCacheBuckets.CachedBucketCount);
        }

        [Fact]
        public void GetCachedSurfaceHeight_DifferentBuckets_BothMiss()
        {
            int resolverCalls = 0;
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) =>
            {
                resolverCalls++;
                return lat * 1000.0;
            };

            double a = TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.0005, 0.0);
            double b = TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.5005, 0.0);

            Assert.Equal(0.5, a, 3);
            Assert.Equal(500.5, b, 3);
            Assert.Equal(2, resolverCalls);
            Assert.Equal(2, TerrainCacheBuckets.CachedBucketCount);
        }

        [Fact]
        public void GetCachedSurfaceHeight_NullBody_ReturnsNaN()
        {
            double height = TerrainCacheBuckets.GetCachedSurfaceHeight(null, 0.0, 0.0);
            Assert.True(double.IsNaN(height));
            Assert.Equal(0, TerrainCacheBuckets.CachedBucketCount);
        }

        [Fact]
        public void GetCachedSurfaceHeight_BodyNameDistinguishesBuckets()
        {
            // Same lat/lon but different bodies must produce two cache entries.
            var fakeMun = TestBodyRegistry.CreateBody("Mun", radius: 200000.0, gravParameter: 6.5e10);
            int resolverCalls = 0;
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) =>
            {
                resolverCalls++;
                return string.Equals(name, "Mun", StringComparison.Ordinal) ? 50.0 : 100.0;
            };

            double kerbin = TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.0, 0.0);
            double mun = TerrainCacheBuckets.GetCachedSurfaceHeight(fakeMun, 0.0, 0.0);

            Assert.Equal(100.0, kerbin);
            Assert.Equal(50.0, mun);
            Assert.Equal(2, resolverCalls);
            Assert.Equal(2, TerrainCacheBuckets.CachedBucketCount);
        }

        // P2-3 review pass: a body-A → body-B → body-A flow at the same
        // lat/lon must NOT clobber body A's cached value. Catches a hash-key
        // collision regression where two body names happen to share a bucket
        // hash and the dictionary's overwrite semantics would silently
        // replace one body's terrain height with another's.
        [Fact]
        public void GetCachedSurfaceHeight_BodyA_BodyB_BodyA_BodyACachedValueUnchanged_NoSecondResolverCallForA()
        {
            var fakeMun = TestBodyRegistry.CreateBody("Mun", radius: 200000.0, gravParameter: 6.5e10);
            var resolverCalls = new Dictionary<string, int> { ["Kerbin"] = 0, ["Mun"] = 0 };
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) =>
            {
                resolverCalls[name]++;
                return string.Equals(name, "Kerbin", StringComparison.Ordinal) ? 100.0 : 50.0;
            };

            // (a) Populate Kerbin at lat/lon X — first call resolves, caches.
            double kerbinFirst = TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.5, 1.0);
            Assert.Equal(100.0, kerbinFirst);
            Assert.Equal(1, resolverCalls["Kerbin"]);

            // (b) Query Mun at SAME lat/lon — different body name, different
            // bucket key, must call resolver and cache separately.
            double mun = TerrainCacheBuckets.GetCachedSurfaceHeight(fakeMun, 0.5, 1.0);
            Assert.Equal(50.0, mun);
            Assert.Equal(1, resolverCalls["Mun"]);

            // (c) Re-query Kerbin at the same lat/lon. Must hit the cache —
            // resolver count for Kerbin must NOT increment, and the value
            // must still be Kerbin's original 100.0 (not Mun's 50.0).
            double kerbinSecond = TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.5, 1.0);
            Assert.Equal(100.0, kerbinSecond);
            Assert.Equal(1, resolverCalls["Kerbin"]);
            Assert.Equal(1, resolverCalls["Mun"]);
            Assert.Equal(2, TerrainCacheBuckets.CachedBucketCount);
        }

        // ----- scene-transition clear -----

        [Fact]
        public void Clear_EmptiesCache_NextCallReResolves()
        {
            int resolverCalls = 0;
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) =>
            {
                resolverCalls++;
                return 42.0;
            };

            TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.5, 1.0);
            Assert.Equal(1, TerrainCacheBuckets.CachedBucketCount);

            TerrainCacheBuckets.Clear();
            Assert.Equal(0, TerrainCacheBuckets.CachedBucketCount);

            TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.5, 1.0);
            // Both calls hit the resolver because the cache was cleared between them.
            Assert.Equal(2, resolverCalls);
        }

        [Fact]
        public void Clear_EmitsVerboseDiagnosticLine()
        {
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) => 0.0;
            TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.0, 0.0);

            int linesBeforeClear = logLines.Count;
            TerrainCacheBuckets.Clear();

            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Terrain]")
                && l.Contains("TerrainCacheBuckets cleared")
                && l.Contains("bucketsBefore=1"));
            Assert.True(logLines.Count > linesBeforeClear, "Clear must emit a diagnostic line");
        }

        // ----- resolver fallback when PQS unavailable -----

        [Fact]
        public void GetCachedSurfaceHeight_NaNFromResolver_PropagatesNaN()
        {
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) => double.NaN;

            double height = TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.0, 0.0);
            Assert.True(double.IsNaN(height));
        }

        [Fact]
        public void GetCachedSurfaceHeight_NaNFromResolver_DoesNotCache_AllowsLaterRetry()
        {
            int resolverCalls = 0;
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) =>
            {
                resolverCalls++;
                return resolverCalls == 1 ? double.NaN : 88.0;
            };

            double first = TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.0, 0.0);
            double second = TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.0, 0.0);
            double third = TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.0, 0.0);

            Assert.True(double.IsNaN(first));
            Assert.Equal(88.0, second);
            Assert.Equal(88.0, third);
            Assert.Equal(2, resolverCalls);
            Assert.Equal(1, TerrainCacheBuckets.CachedBucketCount);
        }

        // ----- cap-warn behaviour (P2-4 review pass) -----

        [Fact]
        public void GetCachedSurfaceHeight_DefaultCapMeetsContract()
        {
            Assert.True(TerrainCacheBuckets.MaxCachedBuckets >= 100_000,
                "Default cap must be at least 100k buckets to accommodate long surface drives");
            Assert.Equal(0.001, TerrainCacheBuckets.BucketDegrees);
            // Default cap matches the constant before any test-seam override.
            Assert.Equal(TerrainCacheBuckets.MaxCachedBuckets, TerrainCacheBuckets.CapForTesting);
        }

        [Fact]
        public void GetCachedSurfaceHeight_PastCap_WarnsOnce_StopsInsertingButStillResolves()
        {
            // P2-4: actually exercise the cap path. Lower the cap to 3 via the
            // CapForTesting seam, insert 5 distinct buckets, and assert:
            //  (a) CachedBucketCount stays at 3 (no entries inserted past cap),
            //  (b) exactly ONE Warn line emits (dedup guard works),
            //  (c) entries 4 and 5 still resolve via the resolver (correctness
            //      preserved past cap — the cache is a perf hint, not a gate).
            TerrainCacheBuckets.CapForTesting = 3;

            int resolverCalls = 0;
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) =>
            {
                resolverCalls++;
                return lat * 1000.0 + lon;
            };

            // Insert 5 distinct buckets — each lat/lon pair lands in a fresh
            // bucket index because they're > 0.001° apart.
            double[] heights = new double[5];
            for (int i = 0; i < 5; i++)
            {
                double lat = 0.5 + i * 0.1; // 0.5, 0.6, 0.7, 0.8, 0.9
                heights[i] = TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, lat, 1.0);
                Assert.Equal(lat * 1000.0 + 1.0, heights[i]);
            }

            // (a) Cache filled to cap, never above.
            Assert.Equal(3, TerrainCacheBuckets.CachedBucketCount);

            // (b) Each insert was a unique miss → 5 resolver calls total
            // (cache wasn't able to satisfy any of them).
            Assert.Equal(5, resolverCalls);

            // (c) Cap-warn line emitted exactly once across the two over-cap
            // misses (entries 4 and 5).
            int capWarnLines = 0;
            foreach (var line in logLines)
            {
                if (line.Contains("[Pipeline-Terrain]") && line.Contains("cap reached"))
                    capWarnLines++;
            }
            Assert.Equal(1, capWarnLines);

            // (d) A subsequent over-cap miss still resolves correctly — the
            // perf path degrades gracefully past the cap.
            double extra = TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 1.5, 1.0);
            Assert.Equal(1.5 * 1000.0 + 1.0, extra);
            Assert.Equal(6, resolverCalls);
            Assert.Equal(3, TerrainCacheBuckets.CachedBucketCount);
        }

        [Fact]
        public void ResetForTesting_RestoresDefaultCap()
        {
            TerrainCacheBuckets.CapForTesting = 5;
            Assert.Equal(5, TerrainCacheBuckets.CapForTesting);

            TerrainCacheBuckets.ResetForTesting();
            Assert.Equal(TerrainCacheBuckets.MaxCachedBuckets, TerrainCacheBuckets.CapForTesting);
        }

        // ----- resolver timing observability (review follow-up) -----

        [Fact]
        public void GetCachedSurfaceHeight_FirstMiss_TracksDuration_UpdatesMaxAndSum()
        {
            // Override Stopwatch so the test doesn't rely on real elapsed
            // time (flaky on CI). 0.5 ms is below the slow-miss threshold.
            TerrainCacheBuckets.MissDurationOverrideForTesting = 0.5;
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) => 100.0;

            TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.5, 1.0);

            Assert.Equal(0.5, TerrainCacheBuckets.MaxMissDurationMsForTesting);
            Assert.Equal(0.5, TerrainCacheBuckets.SumMissDurationMsForTesting);
        }

        [Fact]
        public void GetCachedSurfaceHeight_MultipleMisses_TracksMaxAndAccumulatesSum()
        {
            // Three misses with different simulated durations: max wins,
            // sum accumulates. The avg embedded in frame-summary log is
            // sum / missCount = 4.0 / 3 ≈ 1.33 ms.
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) => 100.0;

            TerrainCacheBuckets.MissDurationOverrideForTesting = 0.5;
            TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.5, 1.0);

            TerrainCacheBuckets.MissDurationOverrideForTesting = 2.5;
            TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.6, 1.0);

            TerrainCacheBuckets.MissDurationOverrideForTesting = 1.0;
            TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.7, 1.0);

            Assert.Equal(2.5, TerrainCacheBuckets.MaxMissDurationMsForTesting);
            Assert.Equal(4.0, TerrainCacheBuckets.SumMissDurationMsForTesting);
        }

        [Fact]
        public void GetCachedSurfaceHeight_SlowMiss_EmitsWarnLine()
        {
            // Simulate a 12 ms miss (above the 5 ms slow-miss threshold).
            // Warn line is emitted via VerboseRateLimited so the test sink
            // captures it.
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) => 100.0;
            TerrainCacheBuckets.MissDurationOverrideForTesting = 12.0;

            TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.5, 1.0);

            int slowMissLines = 0;
            foreach (var line in logLines)
            {
                if (line.Contains("[Pipeline-Terrain]") && line.Contains("slow miss"))
                    slowMissLines++;
            }
            Assert.Equal(1, slowMissLines);

            // Verify the threshold value is mentioned (so the operator can
            // tell whether their threshold is the issue or the resolver is).
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Terrain]")
                && l.Contains("slow miss")
                && l.Contains("durationMs=12.00")
                && l.Contains("threshold=5.00"));
        }

        [Fact]
        public void GetCachedSurfaceHeight_FastMiss_DoesNotEmitSlowMissWarn()
        {
            // 1 ms miss is well below the 5 ms threshold — no slow-miss
            // line. Counter still updates.
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) => 100.0;
            TerrainCacheBuckets.MissDurationOverrideForTesting = 1.0;

            TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.5, 1.0);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Pipeline-Terrain]") && l.Contains("slow miss"));
            Assert.Equal(1.0, TerrainCacheBuckets.MaxMissDurationMsForTesting);
        }

        [Fact]
        public void GetCachedSurfaceHeight_NaNResolution_DoesNotCountTowardsTimingMetrics()
        {
            // NaN means the resolver short-circuited (no PQS, body without
            // controller). It's not real PQS work — counting it towards
            // max/avg would skew the metrics towards zero.
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) => double.NaN;
            TerrainCacheBuckets.MissDurationOverrideForTesting = 8.0;

            TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.5, 1.0);

            Assert.Equal(0.0, TerrainCacheBuckets.MaxMissDurationMsForTesting);
            Assert.Equal(0.0, TerrainCacheBuckets.SumMissDurationMsForTesting);
            // No slow-miss warn either — the resolver didn't actually do work.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Pipeline-Terrain]") && l.Contains("slow miss"));
        }

        [Fact]
        public void GetCachedSurfaceHeight_CacheHit_DoesNotMeasureTiming()
        {
            // First call is a miss (counted); second call is a hit (NOT
            // counted — hits don't allocate a Stopwatch).
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) => 100.0;
            TerrainCacheBuckets.MissDurationOverrideForTesting = 3.0;

            TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.5, 1.0);
            // Set a different override and re-query the SAME bucket — hit
            // path must skip the timing logic entirely.
            TerrainCacheBuckets.MissDurationOverrideForTesting = 99.0;
            TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.5, 1.0);

            // Max stays at 3.0 — the second call's 99 ms override never
            // updated the counter (because the hit path doesn't touch it).
            Assert.Equal(3.0, TerrainCacheBuckets.MaxMissDurationMsForTesting);
            Assert.Equal(3.0, TerrainCacheBuckets.SumMissDurationMsForTesting);
        }

        [Fact]
        public void Clear_ResetsTimingCounters()
        {
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) => 100.0;
            TerrainCacheBuckets.MissDurationOverrideForTesting = 7.5;
            TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.5, 1.0);
            Assert.Equal(7.5, TerrainCacheBuckets.MaxMissDurationMsForTesting);

            TerrainCacheBuckets.Clear();

            Assert.Equal(0.0, TerrainCacheBuckets.MaxMissDurationMsForTesting);
            Assert.Equal(0.0, TerrainCacheBuckets.SumMissDurationMsForTesting);
        }

        [Fact]
        public void GetCachedSurfaceHeight_FrameSummary_IncludesMaxAndAvgMissTiming()
        {
            // Frame-summary log line should embed max + avg miss durations
            // alongside hit/miss counts. The summary is rate-limited at 2 s
            // per shared key, so a single call always emits the first line.
            // One miss at 2.5 ms ⇒ max=2.5, avg=2.5 (sum / count = 2.5/1).
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) => 100.0;
            TerrainCacheBuckets.MissDurationOverrideForTesting = 2.5;
            TerrainCacheBuckets.GetCachedSurfaceHeight(fakeKerbin, 0.5, 1.0);

            // Look for a frame-summary line that mentions both max and avg
            // (formatted F2). Confirms the new fields land in the summary
            // line that operators read to evaluate cache effectiveness.
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Terrain]")
                && l.Contains("frame summary")
                && l.Contains("maxMissMs=2.50")
                && l.Contains("avgMissMs=2.50"));
        }
    }
}
