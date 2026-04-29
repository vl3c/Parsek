using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Rendering
{
    /// <summary>
    /// Phase 7 (design doc §13, §17.3.2, §18 Phase 7) — lat/lon-bucketed cache
    /// of <c>body.pqsController.GetSurfaceHeight</c> / <c>body.TerrainAltitude</c>
    /// results. Render-time terrain correction for <see cref="SegmentEnvironment.SurfaceMobile"/>
    /// ghosts uses the formula
    ///
    /// <code>rendered_altitude = current_terrain_height(lat, lon) + recorded_ground_clearance</code>
    ///
    /// to keep rovers / surface bases at constant ground clearance across
    /// procedural-mesh shifts between sessions. The cache amortises the PQS
    /// query so dozens of co-located surface ghosts do not each pay the µs
    /// per-frame.
    ///
    /// <para>Bucket size is 0.001° lat × 0.001° lon (≈ 111 m on Kerbin's
    /// surface — small enough that ground-clearance error stays sub-meter for
    /// slow rovers, large enough that traffic across the surface still hits a
    /// shared bucket within a few frames). Per-bucket value lifetime within a
    /// scene is forever — terrain does not move within a scene — so no LRU
    /// eviction is needed. Cache is cleared on scene transition via
    /// <see cref="Clear"/>.</para>
    ///
    /// <para>The cache is process-wide and per-<c>(body, latBucket, lonBucket)</c>
    /// keyed. A 100k-entry safety cap guards against pathological recordings;
    /// when hit, the cache stops growing (no eviction — the diagnostic IS the
    /// diagnostic) and emits a one-shot Warn. Pure POCO except for the PQS
    /// resolver seam which lets unit tests exercise cache logic without
    /// FlightGlobals; production resolver calls
    /// <c>body.TerrainAltitude(lat, lon, true)</c>.</para>
    /// </summary>
    internal static class TerrainCacheBuckets
    {
        private const string Tag = "Pipeline-Terrain";

        /// <summary>
        /// Bucket size in degrees (lat / lon). 0.001° ≈ 111 m on Kerbin's
        /// surface (great-circle distance, body radius 600 km). Chosen so a
        /// rover at typical surface speeds (5-20 m/s) crosses a bucket every
        /// few seconds — bucket reuse dominates within a single playback,
        /// while the metre-scale terrain variation across a single bucket is
        /// far below the visible-jitter threshold for a SurfaceMobile ghost.
        /// </summary>
        internal const double BucketDegrees = 0.001;

        /// <summary>
        /// Default cap on the number of cached buckets. Hit-count = number
        /// of distinct (body, latBucket, lonBucket) triples seen in this
        /// scene. 100k buckets at 0.001° span only a tiny fraction of a
        /// Kerbin-sized body's surface (~18M needed for full coverage), so
        /// this cap will only trip on truly pathological inputs. When
        /// tripped, the cache stops growing — further misses still resolve
        /// via the resolver but do not insert into the dictionary,
        /// sacrificing speed but not correctness. The active cap is
        /// <see cref="cap"/>; tests override via <see cref="CapForTesting"/>.
        /// </summary>
        internal const int MaxCachedBuckets = 100_000;

        // Active cap — tests can lower this via CapForTesting to exercise
        // the cap path without inserting 100k+ entries.
        private static int cap = MaxCachedBuckets;

        // Process-wide cache. Cleared on scene transition. Keyed by body
        // reference (we cannot intern by name without resolution overhead) +
        // packed bucket coordinates.
        private static readonly Dictionary<BucketKey, double> Cache =
            new Dictionary<BucketKey, double>(1024);
        // Hit/miss counters monotonically tracked since the last Clear()
        // (i.e. since the current scene began). Logged via the rate-limited
        // summary so you see "hits / misses since this scene started" — the
        // ratio is what matters for the "cache amortises PQS" claim.
        private static int hitCountSinceClear;
        private static int missCountSinceClear;
        private static bool capWarnEmitted;

        /// <summary>
        /// Test seam — when set, replaces the production
        /// <c>body.TerrainAltitude(lat, lon, true)</c> call. Lets unit tests
        /// exercise the cache without a live PQS controller. Resolver
        /// receives <paramref name="bodyName"/>, lat, lon and returns the
        /// terrain height in metres above mean radius.
        /// </summary>
        internal static Func<string, double, double, double> TerrainResolverForTesting;

        /// <summary>
        /// Test seam — overrides <see cref="MaxCachedBuckets"/> so unit
        /// tests can exercise cap-warn behaviour with a small number of
        /// inserts (P2-4 review pass). Set to a small value, populate the
        /// cache past it, observe the warn line, and reset via
        /// <see cref="ResetForTesting"/>.
        /// </summary>
        internal static int CapForTesting
        {
            get { return cap; }
            set { cap = value; }
        }

        /// <summary>
        /// Reset all transient state. Used by the scene-transition hook in
        /// <c>ParsekFlight.OnSceneChangeRequested</c> and by tests.
        /// </summary>
        internal static void Clear()
        {
            int countBefore = Cache.Count;
            Cache.Clear();
            hitCountSinceClear = 0;
            missCountSinceClear = 0;
            capWarnEmitted = false;
            if (!RecordingStore.SuppressLogging)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(CultureInfo.InvariantCulture,
                        "TerrainCacheBuckets cleared: bucketsBefore={0}",
                        countBefore));
            }
        }

        /// <summary>
        /// Reset everything including test seams (xUnit Dispose).
        /// </summary>
        internal static void ResetForTesting()
        {
            Clear();
            TerrainResolverForTesting = null;
            cap = MaxCachedBuckets;
        }

        /// <summary>
        /// Snapshot the current cache size for diagnostics / tests.
        /// </summary>
        internal static int CachedBucketCount => Cache.Count;

        /// <summary>
        /// Pure helper: snap a lat/lon to its bucket index. Extracted so unit
        /// tests can pin the bucket-key contract without round-tripping
        /// through <see cref="GetCachedSurfaceHeight"/>.
        /// </summary>
        internal static (int latBucket, int lonBucket) ComputeBucketIndex(double lat, double lon)
        {
            // Math.Floor rather than truncation so negative latitudes /
            // longitudes round towards -∞, keeping bucket boundaries
            // continuous across 0° (a vessel crossing the equator does not
            // skip a bucket).
            int latIdx = (int)Math.Floor(lat / BucketDegrees);
            int lonIdx = (int)Math.Floor(lon / BucketDegrees);
            return (latIdx, lonIdx);
        }

        /// <summary>
        /// Returns the cached terrain height (metres above mean radius) at
        /// the given lat/lon, calling the PQS resolver on cache miss. The
        /// hit/miss summary is emitted via
        /// <see cref="ParsekLog.VerboseRateLimited"/> at the bottom of every
        /// call so a burst across many ghosts collapses into one line per
        /// the rate-limit window. No per-call ECall (review pass P2-5) —
        /// the previous <c>UnityEngine.Time.frameCount</c> probe was the
        /// hot path's only Unity API call and defeated the "amortise PQS
        /// query" advertisement when N concurrent surface ghosts each hit
        /// the cache per frame.
        /// </summary>
        internal static double GetCachedSurfaceHeight(CelestialBody body, double lat, double lon)
        {
            if (object.ReferenceEquals(body, null))
                return double.NaN;

            string bodyName = body.bodyName;
            (int latIdx, int lonIdx) = ComputeBucketIndex(lat, lon);
            var key = new BucketKey(bodyName, latIdx, lonIdx);

            double height;
            bool hit = Cache.TryGetValue(key, out height);
            if (hit)
            {
                hitCountSinceClear++;
                EmitFrameSummary(bodyName);
                return height;
            }

            // Miss. Call the resolver — test seam first, production fallback
            // otherwise. body.TerrainAltitude returns sea-level-corrected
            // height for ocean-bearing bodies (the `withOcean=true` arg
            // matches what the existing landed-ghost clamp uses).
            height = ResolveTerrainHeight(body, bodyName, lat, lon);

            if (Cache.Count < cap)
            {
                Cache[key] = height;
            }
            else if (!capWarnEmitted)
            {
                capWarnEmitted = true;
                ParsekLog.Warn(Tag,
                    string.Format(CultureInfo.InvariantCulture,
                        "TerrainCacheBuckets cap reached: bucketsCached={0} cap={1} — " +
                        "subsequent misses resolve via PQS but are not cached " +
                        "(diagnostic: too many distinct (body, lat, lon) buckets " +
                        "in this scene, expected with very long surface drives)",
                        Cache.Count, cap));
            }

            missCountSinceClear++;
            ParsekLog.VerboseRateLimited(Tag, "terrain-cache-miss-detail",
                string.Format(CultureInfo.InvariantCulture,
                    "TerrainCacheBuckets miss: body={0} lat={1:F6} lon={2:F6} " +
                    "bucket=({3},{4}) height={5:F2}m cached={6}",
                    bodyName, lat, lon, latIdx, lonIdx, height, Cache.Count),
                5.0);

            EmitFrameSummary(bodyName);
            return height;
        }

        private static double ResolveTerrainHeight(CelestialBody body, string bodyName, double lat, double lon)
        {
            var seam = TerrainResolverForTesting;
            if (seam != null)
                return seam(bodyName, lat, lon);

            // Production: route through CelestialBody.TerrainAltitude (PQS-only,
            // matches what VesselSpawner.ClampAltitudeForLanded uses). Returns 0
            // for a missing PQS controller; we map that to NaN so the playback
            // path falls through to the legacy altitude — silent degradation
            // (HR-9 visibility holds via the rate-limited Verbose miss line).
            if (body.pqsController == null)
                return double.NaN;

            return body.TerrainAltitude(lat, lon, true);
        }

        private static void EmitFrameSummary(string bodyName)
        {
            // Hit/miss summary: VerboseRateLimited collapses bursts across
            // many ghosts into one line per the 2 s window. No Unity API
            // call on the hot path (P2-5 review pass — the previous
            // EmitFrameSummaryIfDue gated on UnityEngine.Time.frameCount,
            // which is an ECall and the only Unity API call in the cache's
            // hot path; it defeated the "amortise PQS query" advertisement
            // when N concurrent surface ghosts each hit the cache per frame).
            // Counters are scene-local monotonics — they reset only on
            // Clear() / scene transition, so the summary line shows
            // accumulated cache pressure since the scene began.
            ParsekLog.VerboseRateLimited(Tag, "frame-summary",
                string.Format(CultureInfo.InvariantCulture,
                    "frame summary cacheHits={0} cacheMisses={1} cached={2} body={3}",
                    hitCountSinceClear, missCountSinceClear, Cache.Count,
                    bodyName ?? "(null)"),
                2.0);
        }

        private readonly struct BucketKey : IEquatable<BucketKey>
        {
            public readonly string BodyName;
            public readonly int LatBucket;
            public readonly int LonBucket;

            public BucketKey(string bodyName, int latBucket, int lonBucket)
            {
                BodyName = bodyName ?? string.Empty;
                LatBucket = latBucket;
                LonBucket = lonBucket;
            }

            public bool Equals(BucketKey other)
            {
                return LatBucket == other.LatBucket
                    && LonBucket == other.LonBucket
                    && string.Equals(BodyName, other.BodyName, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is BucketKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = BodyName != null ? StringComparer.Ordinal.GetHashCode(BodyName) : 0;
                    h = (h * 397) ^ LatBucket;
                    h = (h * 397) ^ LonBucket;
                    return h;
                }
            }
        }
    }
}
