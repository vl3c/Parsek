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
        /// Cap on the number of cached buckets. Hit-count = number of
        /// distinct (body, latBucket, lonBucket) triples seen in this scene.
        /// 100k buckets at 0.001° span the full surface of a Kerbin-sized
        /// body in latitude (~18M needed for full surface), so this cap will
        /// only trip on truly pathological inputs. When tripped, cache stops
        /// growing — further misses still resolve via the resolver but do
        /// not insert into the dictionary, sacrificing speed but not
        /// correctness.
        /// </summary>
        internal const int MaxCachedBuckets = 100_000;

        // Process-wide cache. Cleared on scene transition. Keyed by body
        // reference (we cannot intern by name without resolution overhead) +
        // packed bucket coordinates.
        private static readonly Dictionary<BucketKey, double> Cache =
            new Dictionary<BucketKey, double>(1024);
        private static int hitCountThisFrame;
        private static int missCountThisFrame;
        private static long lastFrameLogged = -1L;
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
        /// Test seam — when set, replaces the <c>UnityEngine.Time.frameCount</c>
        /// query in the per-frame summary log. Required because Unity's
        /// ECall-backed Time properties throw SecurityException when invoked
        /// from a vanilla CLR (xUnit), even inside try/catch (the SE fires at
        /// JIT bind time). Default null ⇒ production path uses Unity API.
        /// </summary>
        internal static Func<long> FrameCountResolverForTesting;

        /// <summary>
        /// Reset all transient state. Used by the scene-transition hook in
        /// <c>ParsekFlight.OnSceneChangeRequested</c> and by tests.
        /// </summary>
        internal static void Clear()
        {
            int countBefore = Cache.Count;
            Cache.Clear();
            hitCountThisFrame = 0;
            missCountThisFrame = 0;
            lastFrameLogged = -1L;
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
        /// Reset everything including test seam (xUnit Dispose).
        /// </summary>
        internal static void ResetForTesting()
        {
            Clear();
            TerrainResolverForTesting = null;
            FrameCountResolverForTesting = null;
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
        /// Returns the cached terrain height (metres above mean radius) at the
        /// given lat/lon, calling the PQS resolver on cache miss. Per-frame
        /// hit/miss counters are emitted as a rate-limited summary the first
        /// call after the frame index advances.
        /// </summary>
        internal static double GetCachedSurfaceHeight(CelestialBody body, double lat, double lon)
        {
            if (object.ReferenceEquals(body, null))
                return double.NaN;

            string bodyName = body.bodyName;
            (int latIdx, int lonIdx) = ComputeBucketIndex(lat, lon);
            var key = new BucketKey(bodyName, latIdx, lonIdx);

            double height;
            if (Cache.TryGetValue(key, out height))
            {
                hitCountThisFrame++;
                EmitFrameSummaryIfDue();
                return height;
            }

            // Miss. Call the resolver — test seam first, production fallback
            // otherwise. body.TerrainAltitude returns sea-level-corrected
            // height for ocean-bearing bodies (the `withOcean=true` arg
            // matches what the existing landed-ghost clamp uses).
            height = ResolveTerrainHeight(body, bodyName, lat, lon);

            if (Cache.Count < MaxCachedBuckets)
            {
                Cache[key] = height;
            }
            else if (!capWarnEmitted)
            {
                capWarnEmitted = true;
                ParsekLog.Verbose(Tag,
                    string.Format(CultureInfo.InvariantCulture,
                        "TerrainCacheBuckets cap reached: bucketsCached={0} cap={1} — " +
                        "subsequent misses resolve via PQS but are not cached " +
                        "(diagnostic: too many distinct (body, lat, lon) buckets " +
                        "in this scene, expected with very long surface drives)",
                        Cache.Count, MaxCachedBuckets));
            }

            missCountThisFrame++;
            ParsekLog.VerboseRateLimited(Tag, "terrain-cache-miss-detail",
                string.Format(CultureInfo.InvariantCulture,
                    "TerrainCacheBuckets miss: body={0} lat={1:F6} lon={2:F6} " +
                    "bucket=({3},{4}) height={5:F2}m cached={6}",
                    bodyName, lat, lon, latIdx, lonIdx, height, Cache.Count),
                5.0);

            EmitFrameSummaryIfDue();
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

        private static void EmitFrameSummaryIfDue()
        {
            // Use UnityEngine.Time.frameCount when available; otherwise the
            // counter never advances and the summary stays per-call rate-limited.
            long currentFrame = TryGetUnityFrame();
            if (currentFrame == lastFrameLogged)
                return;
            lastFrameLogged = currentFrame;

            // Per-frame summary — VerboseRateLimited so a burst of misses
            // across many ghosts in one frame collapses to one line per the
            // rate-limit window.
            ParsekLog.VerboseRateLimited(Tag, "terrain-cache-frame-summary",
                string.Format(CultureInfo.InvariantCulture,
                    "TerrainCacheBuckets frame={0} hits={1} misses={2} cached={3}",
                    currentFrame, hitCountThisFrame, missCountThisFrame, Cache.Count),
                2.0);
            hitCountThisFrame = 0;
            missCountThisFrame = 0;
        }

        private static long TryGetUnityFrame()
        {
            // Test seam: when set, bypass the Unity ECall (xUnit raises a
            // SecurityException at JIT bind time that no try/catch can
            // intercept; the SE fires when the JIT loads any method that
            // textually references UnityEngine.Time.frameCount). Production
            // seam stays null and dispatch falls through to a separate
            // method (NativeUnityFrame) that contains the actual ECall —
            // unit tests never call NativeUnityFrame because they always
            // set the seam.
            var seam = FrameCountResolverForTesting;
            if (seam != null)
                return seam();
            return NativeUnityFrame();
        }

        private static long NativeUnityFrame()
        {
            // Isolated so the JIT only binds the ECall when this method is
            // actually loaded (production / in-game). xUnit never reaches
            // here because the seam short-circuits first.
            return UnityEngine.Time.frameCount;
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
