using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>One 50 m end-of-flight circle around a stock launch-site spawn point.</summary>
    internal struct LaunchSiteCircle
    {
        /// <summary>Stock internal site name ("Desert_Launch_Site"); "KSC" for the KSC pair.</summary>
        public string SiteName;
        public KscExclusionZone Kind;
        public double Latitude;
        public double Longitude;
        /// <summary>"runtime" (read from PSystemSetup) or "fallback" (static table).</summary>
        public string Source;
    }

    /// <summary>Which launch-site circle a position fell in (Kind None = no circle).</summary>
    internal struct LaunchSiteZoneHit
    {
        public KscExclusionZone Kind;
        public string SiteName;
        public bool IsKsc;

        public bool IsHit => Kind != KscExclusionZone.None;

        internal static readonly LaunchSiteZoneHit None = new LaunchSiteZoneHit
        {
            Kind = KscExclusionZone.None,
            SiteName = null,
            IsKsc = false,
        };
    }

    /// <summary>
    /// The non-KSC stock launch sites for the end-of-flight retirement (owner ruling
    /// 2026-09-27: a flight that ends parked on ANY stock launch site is retired like the KSC
    /// pad / runway ending). KSC keeps its own constants in
    /// <see cref="SpawnCollisionDetector"/> so its behavior is byte-identical; this class adds
    /// the Making History sites (Desert pad + airfield, Woomerang pad, Island airfield).
    ///
    /// <para>Positions come from stock at runtime: <c>PSystemSetup.Instance.LaunchSites</c>
    /// (the set-up sites), filtered to <c>PSystemSetup.IsStockLaunchSite</c> on the home
    /// world, one circle per <c>LaunchSite.SpawnPoint</c> (its <c>latitude</c> /
    /// <c>longitude</c> when <c>latlonaltSet</c>, else the spawn transform read through
    /// <c>Body.GetLatLonAlt</c>); a site whose editor facility is the SPH is a runway,
    /// otherwise a pad (decompiled KSP 1.12.5). Any site the runtime read cannot resolve
    /// uses the static fallback table below.</para>
    /// </summary>
    internal static class LaunchSiteExclusionZones
    {
        internal const string Tag = "LaunchSiteZones";
        internal const string KscSiteName = "KSC";

        /// <summary>
        /// Static fallback spawn points (home world only). Desert Launch Site is the measured
        /// PRELAUNCH position of a vessel rolled out there (MC-4 produced save, 2026-09-25);
        /// the other three are the published stock coordinates of the sites and are used only
        /// when the runtime read is unavailable.
        /// </summary>
        internal static readonly LaunchSiteCircle[] FallbackAltSiteCircles =
        {
            new LaunchSiteCircle { SiteName = "Desert_Launch_Site", Kind = KscExclusionZone.Pad,
                Latitude = -6.5604, Longitude = -143.9500, Source = "fallback" },
            new LaunchSiteCircle { SiteName = "Desert_Airfield", Kind = KscExclusionZone.Runway,
                Latitude = -6.5998, Longitude = -144.0406, Source = "fallback" },
            new LaunchSiteCircle { SiteName = "Woomerang_Launch_Site", Kind = KscExclusionZone.Pad,
                Latitude = 45.2897, Longitude = 136.1103, Source = "fallback" },
            new LaunchSiteCircle { SiteName = "Island_Airfield", Kind = KscExclusionZone.Runway,
                Latitude = -1.5178, Longitude = -71.9103, Source = "fallback" },
        };

        /// <summary>
        /// Every stock launch-site / facility name a vessel's <c>landedAt</c> or
        /// <c>FlightDriver.LaunchSiteName</c> can carry.
        /// </summary>
        internal static readonly string[] StockLaunchSiteNames =
        {
            "LaunchPad", "Runway",
            "Desert_Launch_Site", "Desert_Airfield", "Woomerang_Launch_Site", "Island_Airfield",
        };

        /// <summary>Test seam: replaces the runtime read + fallback merge.</summary>
        internal static Func<IList<LaunchSiteCircle>> AltSiteCirclesOverrideForTesting;

        /// <summary>Test seam: replaces the Making History install check.</summary>
        internal static Func<bool> MakingHistoryInstalledOverrideForTesting;

        private static readonly LaunchSiteCircle[] NoFallbackCircles = new LaunchSiteCircle[0];

        /// <summary>
        /// Whether the Making History expansion is installed (stock
        /// <c>Expansions.ExpansionsLoader.IsExpansionInstalled("MakingHistory")</c>, the check
        /// <c>PSystemSetup</c> itself uses before it sets up the expansion sites). Without it
        /// the four non-KSC sites do not exist, so their fallback circles must not retire a
        /// flight that merely ends where they would be. A failed read counts as not installed.
        /// </summary>
        internal static bool IsMakingHistoryInstalled()
        {
            var provider = MakingHistoryInstalledOverrideForTesting;
            if (provider != null)
                return provider();
            try
            {
                return ReadMakingHistoryInstalledCore();
            }
            catch (Exception)
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadMakingHistoryInstalledCore()
        {
            return Expansions.ExpansionsLoader.IsExpansionInstalled("MakingHistory");
        }

        /// <summary>
        /// The static fallback circles that apply: the table when Making History is installed,
        /// none otherwise (the KSC pad / runway are separate and always apply). Pure.
        /// </summary>
        internal static IList<LaunchSiteCircle> ResolveFallbackCircles(bool makingHistoryInstalled)
        {
            return makingHistoryInstalled ? (IList<LaunchSiteCircle>)FallbackAltSiteCircles : NoFallbackCircles;
        }

        private static IList<LaunchSiteCircle> cachedRuntimeCircles;

        internal static bool IsStockLaunchSiteName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            for (int i = 0; i < StockLaunchSiteNames.Length; i++)
            {
                if (string.Equals(StockLaunchSiteNames[i], name, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Pure merge: every runtime circle, plus each fallback entry whose site the runtime
        /// read produced no circle for.
        /// </summary>
        internal static List<LaunchSiteCircle> MergeWithFallback(
            IList<LaunchSiteCircle> runtime, IList<LaunchSiteCircle> fallback)
        {
            var merged = new List<LaunchSiteCircle>();
            var covered = new HashSet<string>(StringComparer.Ordinal);
            if (runtime != null)
            {
                for (int i = 0; i < runtime.Count; i++)
                {
                    merged.Add(runtime[i]);
                    if (!string.IsNullOrEmpty(runtime[i].SiteName))
                        covered.Add(runtime[i].SiteName);
                }
            }
            if (fallback != null)
            {
                for (int i = 0; i < fallback.Count; i++)
                {
                    if (!covered.Contains(fallback[i].SiteName))
                        merged.Add(fallback[i]);
                }
            }
            return merged;
        }

        /// <summary>
        /// Pure classification: the KSC pad / runway circles first (the unchanged
        /// <see cref="SpawnCollisionDetector.ClassifyKscExclusionZone"/>), then the non-KSC
        /// circles in list order.
        /// </summary>
        internal static LaunchSiteZoneHit Classify(
            double latitude,
            double longitude,
            double bodyRadius,
            double exclusionRadiusMeters,
            IList<LaunchSiteCircle> altCircles)
        {
            KscExclusionZone ksc = SpawnCollisionDetector.ClassifyKscExclusionZone(
                latitude, longitude, bodyRadius, exclusionRadiusMeters);
            if (ksc != KscExclusionZone.None)
                return new LaunchSiteZoneHit { Kind = ksc, SiteName = KscSiteName, IsKsc = true };
            if (altCircles == null
                || double.IsNaN(latitude) || double.IsNaN(longitude)
                || double.IsNaN(bodyRadius) || bodyRadius <= 0.0)
                return LaunchSiteZoneHit.None;
            for (int i = 0; i < altCircles.Count; i++)
            {
                LaunchSiteCircle c = altCircles[i];
                if (c.Kind == KscExclusionZone.None)
                    continue;
                double d = SpawnCollisionDetector.SurfaceDistance(
                    latitude, longitude, c.Latitude, c.Longitude, bodyRadius);
                if (d < exclusionRadiusMeters)
                    return new LaunchSiteZoneHit { Kind = c.Kind, SiteName = c.SiteName, IsKsc = false };
            }
            return LaunchSiteZoneHit.None;
        }

        /// <summary>Human-readable site name ("Desert Launch Site"), or "KSC".</summary>
        internal static string DescribeSite(string siteName)
        {
            if (string.IsNullOrEmpty(siteName))
                return "?";
            return siteName.Replace('_', ' ');
        }

        /// <summary>
        /// The non-KSC circles: runtime positions merged over the fallback table (the table
        /// only when Making History is installed). Headless or before PSystemSetup exists this
        /// is the fallback table, or nothing without the expansion. A successful runtime read is
        /// cached for the process (site spawn points are body-fixed).
        /// </summary>
        internal static IList<LaunchSiteCircle> GetAltSiteCircles()
        {
            if (AltSiteCirclesOverrideForTesting != null)
                return AltSiteCirclesOverrideForTesting();
            if (cachedRuntimeCircles != null)
                return cachedRuntimeCircles;

            List<LaunchSiteCircle> runtime = null;
            try
            {
                runtime = ReadRuntimeAltSiteCirclesCore();
            }
            catch (Exception)
            {
                runtime = null;
            }
            bool makingHistory = IsMakingHistoryInstalled();
            List<LaunchSiteCircle> merged = MergeWithFallback(
                runtime, ResolveFallbackCircles(makingHistory));
            if (!makingHistory)
            {
                ParsekLog.VerboseRateLimited(Tag, "no-making-history",
                    "Making History not installed: no fallback launch-site circles (KSC pad / runway only"
                    + (runtime != null && runtime.Count > 0 ? ", plus the runtime sites)" : ")"));
            }
            if (runtime != null && runtime.Count > 0)
            {
                cachedRuntimeCircles = merged;
                int fallbackCount = 0;
                for (int i = 0; i < merged.Count; i++)
                    if (merged[i].Source == "fallback") fallbackCount++;
                ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                    "Launch-site retirement circles resolved: runtime={0} fallback={1}",
                    runtime.Count, fallbackCount));
            }
            return merged;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<LaunchSiteCircle> ReadRuntimeAltSiteCirclesCore()
        {
            PSystemSetup setup = PSystemSetup.Instance;
            if (setup == null || setup.LaunchSites == null)
                return null;
            var result = new List<LaunchSiteCircle>();
            List<LaunchSite> sites = setup.LaunchSites;
            for (int i = 0; i < sites.Count; i++)
            {
                LaunchSite site = sites[i];
                if (site == null || string.IsNullOrEmpty(site.name))
                    continue;
                if (!setup.IsStockLaunchSite(site.name) && !IsStockLaunchSiteName(site.name))
                    continue;
                CelestialBody body = site.Body;
                if (body == null || !body.isHomeWorld || site.spawnPoints == null)
                    continue;
                KscExclusionZone kind = site.editorFacility == EditorFacility.SPH
                    ? KscExclusionZone.Runway
                    : KscExclusionZone.Pad;
                for (int s = 0; s < site.spawnPoints.Length; s++)
                {
                    LaunchSite.SpawnPoint sp = site.spawnPoints[s];
                    if (sp == null)
                        continue;
                    double lat, lon;
                    if (sp.latlonaltSet)
                    {
                        lat = sp.latitude;
                        lon = sp.longitude;
                    }
                    else
                    {
                        // GetSpawnPointTransform dereferences the site transform, which a
                        // site not yet built into the scene lacks; that site falls back.
                        try
                        {
                            UnityEngine.Transform t = sp.GetSpawnPointTransform();
                            if (t == null)
                                continue;
                            body.GetLatLonAlt(t.position, out lat, out lon, out _);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                    }
                    if (double.IsNaN(lat) || double.IsNaN(lon))
                        continue;
                    result.Add(new LaunchSiteCircle
                    {
                        SiteName = site.name,
                        Kind = kind,
                        Latitude = lat,
                        Longitude = lon,
                        Source = "runtime",
                    });
                }
            }
            return result;
        }

        internal static void ResetForTesting()
        {
            AltSiteCirclesOverrideForTesting = null;
            MakingHistoryInstalledOverrideForTesting = null;
            cachedRuntimeCircles = null;
        }
    }
}
