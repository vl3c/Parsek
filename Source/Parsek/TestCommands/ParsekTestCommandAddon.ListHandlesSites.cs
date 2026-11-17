using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Multiplayer T-07 partial: the live read behind <c>ListHandles kind=sites</c>. It
    /// only copies stock values into a <see cref="SitesReading"/>;
    /// <see cref="TestCommandSitesReadback"/> owns the wire keys, the order and the
    /// formats.
    ///
    /// <para><b>Read-only.</b> Every value is a field or getter read, plus the terrain
    /// height query, which evaluates the terrain without building anything. Nothing
    /// stock caches is refreshed here (no <c>SetSpawnPointsLatLonAlt</c>, no
    /// <c>SpaceCenter.Start</c>): the point of the read is to see the caches as they
    /// stand. A spawn point whose cache is not set yet is read through its transform
    /// and marked <c>src=transform</c>.</para>
    ///
    /// <para><b>Any scene, any save.</b> Every object is null-guarded; a value that
    /// cannot be read reports <c>none</c>, never a defer, because the read is an
    /// observation of the instant it was taken.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SitesReading GatherSitesReading()
        {
            var reading = new SitesReading
            {
                LaunchSites = new List<LaunchSitePointRow>(),
                Samples = new List<TerrainSampleRow>(),
            };

            SpaceCenter spaceCenter = SpaceCenter.Instance;
            CelestialBody body = spaceCenter != null ? spaceCenter.cb : null;
            if (body == null)
                body = FlightGlobals.GetHomeBody();
            reading.BodyName = body != null ? body.bodyName : null;

            if (spaceCenter != null)
            {
                // The cached point stock's recovery distance is measured from.
                reading.RecoveryFound = true;
                reading.RecoveryLatitude = spaceCenter.Latitude;
                reading.RecoveryLongitude = spaceCenter.Longitude;

                UnityEngine.Transform transform = spaceCenter.SpaceCenterTransform;
                if (transform != null && body != null)
                {
                    double lat, lon, alt;
                    body.GetLatLonAlt(transform.position, out lat, out lon, out alt);
                    reading.SpaceCenterFound = true;
                    reading.SpaceCenterLatitude = lat;
                    reading.SpaceCenterLongitude = lon;
                    reading.SpaceCenterAltitude = alt;
                }
            }

            int facilityPoints = ReadFacilityLaunchSitePoints(reading.LaunchSites);
            int alternatePoints = ReadAlternateLaunchSitePoints(reading.LaunchSites);

            int measured = 0;
            SiteSamplePoint[] points = TestCommandSitesReadback.SamplePoints;
            for (int i = 0; i < points.Length; i++)
            {
                var row = new TerrainSampleRow
                {
                    Name = points[i].Name,
                    Latitude = points[i].Latitude,
                    Longitude = points[i].Longitude,
                };
                if (body != null && body.pqsController != null)
                {
                    row.Height = body.TerrainAltitude(points[i].Latitude, points[i].Longitude, true);
                    row.Measured = true;
                    measured++;
                }
                reading.Samples.Add(row);
            }

            ParsekLog.Verbose(Tag, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "listhandles sites read: spaceCenter={0} recovery={1} facilityPoints={2} alternatePoints={3} samplesMeasured={4}/{5}",
                reading.SpaceCenterFound, reading.RecoveryFound, facilityPoints, alternatePoints,
                measured, points.Length));
            return reading;
        }

        /// <summary>The space center's own launch facilities (the pad and the runway),
        /// one row per spawn point. Returns the number of rows added.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ReadFacilityLaunchSitePoints(List<LaunchSitePointRow> rows)
        {
            PSystemSetup setup = PSystemSetup.Instance;
            if (setup == null || setup.SpaceCenterFacilityLaunchSites == null)
                return 0;
            int added = 0;
            List<PSystemSetup.SpaceCenterFacility> facilities = setup.SpaceCenterFacilityLaunchSites;
            for (int i = 0; i < facilities.Count; i++)
            {
                PSystemSetup.SpaceCenterFacility facility = facilities[i];
                if (facility == null || facility.spawnPoints == null)
                    continue;
                for (int s = 0; s < facility.spawnPoints.Length; s++)
                {
                    PSystemSetup.SpaceCenterFacility.SpawnPoint sp = facility.spawnPoints[s];
                    if (sp == null)
                        continue;
                    var row = new LaunchSitePointRow
                    {
                        SiteName = facility.name,
                        PointName = sp.name,
                        Facility = true,
                        Editor = facility.editorFacility.ToString(),
                    };
                    if (sp.latlonaltSet)
                        SetFromCache(ref row, sp.latitude, sp.longitude, sp.altitude);
                    else
                        SetFromTransform(ref row, facility.hostBody, TryFacilitySpawnTransform(sp));
                    rows.Add(row);
                    added++;
                }
            }
            return added;
        }

        /// <summary>The stock alternate launch sites that are set up in this game (the
        /// Making History sites when the expansion is installed), one row per spawn
        /// point. Returns the number of rows added.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ReadAlternateLaunchSitePoints(List<LaunchSitePointRow> rows)
        {
            PSystemSetup setup = PSystemSetup.Instance;
            if (setup == null || setup.LaunchSites == null)
                return 0;
            int added = 0;
            List<LaunchSite> sites = setup.LaunchSites;
            for (int i = 0; i < sites.Count; i++)
            {
                LaunchSite site = sites[i];
                if (site == null || string.IsNullOrEmpty(site.name) || site.spawnPoints == null)
                    continue;
                if (!setup.IsStockLaunchSite(site.name))
                    continue;
                for (int s = 0; s < site.spawnPoints.Length; s++)
                {
                    LaunchSite.SpawnPoint sp = site.spawnPoints[s];
                    if (sp == null)
                        continue;
                    var row = new LaunchSitePointRow
                    {
                        SiteName = site.name,
                        PointName = sp.name,
                        Facility = false,
                        Editor = site.editorFacility.ToString(),
                    };
                    if (sp.latlonaltSet)
                        SetFromCache(ref row, sp.latitude, sp.longitude, sp.altitude);
                    else
                        SetFromTransform(ref row, site.Body, TryAlternateSpawnTransform(sp));
                    rows.Add(row);
                    added++;
                }
            }
            return added;
        }

        private static void SetFromCache(ref LaunchSitePointRow row, double lat, double lon, double alt)
        {
            row.Source = SitePointSource.Cache;
            row.Latitude = lat;
            row.Longitude = lon;
            row.Altitude = alt;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SetFromTransform(
            ref LaunchSitePointRow row, CelestialBody body, UnityEngine.Transform transform)
        {
            if (body == null || transform == null)
                return;
            double lat, lon, alt;
            body.GetLatLonAlt(transform.position, out lat, out lon, out alt);
            row.Source = SitePointSource.Transform;
            row.Latitude = lat;
            row.Longitude = lon;
            row.Altitude = alt;
        }

        // GetSpawnPointTransform dereferences the site's transform, which a site not yet
        // built into the scene lacks; that point reads src=none.
        private static UnityEngine.Transform TryFacilitySpawnTransform(
            PSystemSetup.SpaceCenterFacility.SpawnPoint sp)
        {
            try { return sp.GetSpawnPointTransform(); }
            catch (Exception) { return null; }
        }

        private static UnityEngine.Transform TryAlternateSpawnTransform(LaunchSite.SpawnPoint sp)
        {
            try { return sp.GetSpawnPointTransform(); }
            catch (Exception) { return null; }
        }
    }
}
