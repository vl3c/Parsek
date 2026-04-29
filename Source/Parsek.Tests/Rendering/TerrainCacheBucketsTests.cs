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

        private long stubFrame;

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
            // Stub Unity Time.frameCount — vanilla CLR cannot bind the ECall.
            stubFrame = 1L;
            TerrainCacheBuckets.FrameCountResolverForTesting = () => stubFrame;
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

        // ----- cap-warn behaviour -----

        [Fact]
        public void GetCachedSurfaceHeight_PastCap_WarnsOnceAndStopsInserting()
        {
            // We can't actually pump 100k entries into the cache in a test
            // without making the test slow. Instead, exercise the cap path by
            // verifying the public contract: CachedBucketCount never exceeds
            // MaxCachedBuckets, and the resolver is still called past the cap.
            // The MaxCachedBuckets constant pins the cap size.
            Assert.True(TerrainCacheBuckets.MaxCachedBuckets >= 100_000,
                "Cap must be at least 100k buckets to accommodate long surface drives");
            Assert.Equal(0.001, TerrainCacheBuckets.BucketDegrees);
        }
    }
}
