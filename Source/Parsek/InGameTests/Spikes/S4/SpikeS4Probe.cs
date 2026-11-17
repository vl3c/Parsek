using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace Parsek.InGameTests.Spikes
{
    // R0 spike branch only. Spike S4a: the KSC state dump. Read-only: it reads stock
    // objects and Parsek's launch-site circle cache, and writes CSV rows under
    // <KSP root>/parsek-spike/S4a/ plus one summary log line per section. It runs
    // automatically ~3 s after each SPACECENTER / FLIGHT arrival while S4a is armed
    // (SpikeS4EventOrder), and on demand through the in-game test DumpKscState.
    //
    // CSV files: ksc_state.csv (long format, one row per object field), footprints.csv
    // (one row per pad / runway / site footprint), footprint_corners.csv (eight corners per
    // collider, as lat / lon / alt), circles.csv (Parsek's launch-site circle list and its
    // private cache).
    internal static class SpikeS4Probe
    {
        internal static string Tag => SpikeArming.SpikeTag(SpikeS4Format.SpikeId);

        private static int dumpCounter;

        internal sealed class DumpResult
        {
            internal string DumpId;
            internal int StateRows, FootprintRows, CornerRows, CircleRows, Errors;
        }

        /// <summary>Runs every section; a section that throws is logged and counted, the rest still run.</summary>
        internal static DumpResult Dump(string session, string reason)
        {
            dumpCounter++;
            string scene = HighLogic.LoadedScene.ToString();
            var r = new DumpResult
            {
                DumpId = "d" + dumpCounter.ToString(CultureInfo.InvariantCulture) + "-" + scene,
            };
            ParsekLog.Info(Tag, "dump start id=" + r.DumpId + " reason=" + reason);

            using (var state = SpikeCsv.Open(SpikeS4Format.SpikeId, "ksc_state", SpikeS4Format.KscStateHeader))
            using (var foot = SpikeCsv.Open(SpikeS4Format.SpikeId, "footprints", SpikeS4Format.FootprintHeader))
            using (var corners = SpikeCsv.Open(SpikeS4Format.SpikeId, "footprint_corners", SpikeS4Format.CornerHeader))
            using (var circles = SpikeCsv.Open(SpikeS4Format.SpikeId, "circles", SpikeS4Format.CircleHeader))
            {
                var ctx = new Ctx { Session = session, Dump = r.DumpId, Scene = scene, Reason = reason, State = state };
                Section(r, "game", () => DumpGame(ctx));
                Section(r, "spacecenter", () => DumpSpaceCenter(ctx));
                Section(r, "pqs", () => DumpPqsObjects(ctx));
                Section(r, "camera", () => DumpCamera(ctx));
                Section(r, "facilities", () => DumpFacilities(ctx));
                Section(r, "launchsites", () => DumpLaunchSites(ctx));
                Section(r, "footprints", () => DumpFootprints(ctx, foot, corners, r));
                Section(r, "circles", () => DumpCircles(ctx, circles, r));
                r.StateRows = state.RowsWritten;
                r.FootprintRows = foot.RowsWritten;
                r.CornerRows = corners.RowsWritten;
                r.CircleRows = circles.RowsWritten;
            }

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "dump done id={0} stateRows={1} footprints={2} corners={3} circles={4} errors={5} dir={6}",
                r.DumpId, r.StateRows, r.FootprintRows, r.CornerRows, r.CircleRows, r.Errors,
                SpikeCsv.ResolveSpikeRoot()));
            return r;
        }

        private sealed class Ctx
        {
            internal string Session, Dump, Scene, Reason;
            internal SpikeCsvWriter State;

            internal void Row(string kind, string obj, string field, string value)
                => State.WriteRow(Session, Dump, Scene, Reason, kind, obj, field, value);

            internal void Fields(string kind, string obj, object target, bool nonPublic, Type stopAt = null)
            {
                foreach (SpikeS4Field f in SpikeS4Format.DumpFields(target, nonPublic, stopAt))
                    Row(kind, obj, f.Name, f.Value);
            }
        }

        private static void Section(DumpResult r, string name, Action body)
        {
            try { body(); }
            catch (Exception ex)
            {
                r.Errors++;
                ParsekLog.Warn(Tag, "dump section " + name + " failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void LatLon(Ctx c, string kind, string obj, string prefix, CelestialBody body, Vector3d world)
        {
            if (body == null) { c.Row(kind, obj, prefix + "latlonalt", "no-body"); return; }
            body.GetLatLonAlt(world, out double lat, out double lon, out double alt);
            c.Row(kind, obj, prefix + "lat", SpikeS4Format.D(lat));
            c.Row(kind, obj, prefix + "lon", SpikeS4Format.D(lon));
            c.Row(kind, obj, prefix + "alt", SpikeS4Format.D(alt));
        }

        private static CelestialBody Home() => FlightGlobals.GetHomeBody();

        private static void DumpGame(Ctx c)
        {
            Game g = HighLogic.CurrentGame;
            c.Row("game", "current", "present", g != null ? "true" : "false");
            c.Row("game", "current", "save_folder", HighLogic.SaveFolder ?? "");
            if (g == null) return;
            c.Row("game", "current", "title", g.Title ?? "");
            c.Row("game", "current", "mode", g.Mode.ToString());
            c.Row("game", "current", "startScene", g.startScene.ToString());
            var diff = g.Parameters != null ? g.Parameters.Difficulty : null;
            c.Row("game", "current", "AllowOtherLaunchSites",
                diff != null ? SpikeS4Format.FormatValue(diff.AllowOtherLaunchSites) : "null");
        }

        private static void DumpSpaceCenter(Ctx c)
        {
            SpaceCenter sc = SpaceCenter.Instance;
            c.Row("spacecenter", "Instance", "present", sc != null ? "true" : "false");
            if (sc == null) return;
            c.Row("spacecenter", sc.name, "Latitude", SpikeS4Format.D(sc.Latitude));
            c.Row("spacecenter", sc.name, "Longitude", SpikeS4Format.D(sc.Longitude));
            c.Row("spacecenter", sc.name, "SrfNVector", SpikeS4Format.FormatValue(sc.SrfNVector));
            c.Row("spacecenter", sc.name, "cb", sc.cb != null ? sc.cb.bodyName : "null");
            c.Row("spacecenter", sc.name, "path", PathOf(sc.transform));
            c.Fields("spacecenter", sc.name, sc, true, typeof(MonoBehaviour));
            LatLon(c, "spacecenter", sc.name, "transform_", sc.cb ?? Home(), sc.transform.position);
            if (sc.spaceCenterAreaTrigger != null)
            {
                Bounds b = sc.spaceCenterAreaTrigger.bounds;
                c.Row("spacecenter", sc.name, "areaTrigger_type", sc.spaceCenterAreaTrigger.GetType().Name);
                c.Row("spacecenter", sc.name, "areaTrigger_bounds_size", SpikeS4Format.FormatValue(b.size));
                LatLon(c, "spacecenter", sc.name, "areaTrigger_centre_", sc.cb ?? Home(), b.center);
            }
        }

        private static void DumpPqsObjects(Ctx c)
        {
            CelestialBody home = Home();
            PQS pqs = home != null ? home.pqsController : null;
            c.Row("pqs", home != null ? home.bodyName : "null", "pqsController", pqs != null ? pqs.name : "null");
            int allCities = Resources.FindObjectsOfTypeAll<PQSCity>().Length;
            int allCities2 = Resources.FindObjectsOfTypeAll<PQSCity2>().Length;
            c.Row("pqs", "all", "PQSCity_count_all_bodies_and_prefabs", allCities.ToString(CultureInfo.InvariantCulture));
            c.Row("pqs", "all", "PQSCity2_count_all_bodies_and_prefabs", allCities2.ToString(CultureInfo.InvariantCulture));
            if (pqs == null) return;

            PQSCity[] cities = pqs.GetComponentsInChildren<PQSCity>(true);
            foreach (PQSCity city in cities)
            {
                string obj = city.name;
                c.Row("pqscity", obj, "path", PathOf(city.transform));
                c.Row("pqscity", obj, "activeInHierarchy", SpikeS4Format.FormatValue(city.gameObject.activeInHierarchy));
                c.Fields("pqscity", obj, city, true, typeof(MonoBehaviour));
                LatLon(c, "pqscity", obj, "transform_", home, city.transform.position);
                if (city.lod != null)
                    for (int i = 0; i < city.lod.Length; i++)
                        if (city.lod[i] != null)
                            c.Row("pqscity", obj, "lod[" + i.ToString(CultureInfo.InvariantCulture) + "].visibleRange",
                                SpikeS4Format.FormatValue(city.lod[i].visibleRange));
            }

            PQSCity2[] cities2 = pqs.GetComponentsInChildren<PQSCity2>(true);
            foreach (PQSCity2 city in cities2)
            {
                string obj = city.name;
                c.Row("pqscity2", obj, "path", PathOf(city.transform));
                c.Row("pqscity2", obj, "activeInHierarchy", SpikeS4Format.FormatValue(city.gameObject.activeInHierarchy));
                c.Fields("pqscity2", obj, city, true, typeof(MonoBehaviour));
                LatLon(c, "pqscity2", obj, "transform_", home, city.transform.position);
            }

            foreach (PQSMod_MapDecalTangent d in pqs.GetComponentsInChildren<PQSMod_MapDecalTangent>(true))
                DumpDecal(c, "decal_tangent", d.name, d, d.position, d.radius, pqs, home);
            foreach (PQSMod_MapDecal d in pqs.GetComponentsInChildren<PQSMod_MapDecal>(true))
                DumpDecal(c, "decal", d.name, d, d.position, d.radius, pqs, home);

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "pqs body={0} cities={1} cities2={2} allCities={3} allCities2={4}",
                home.bodyName, cities.Length, cities2.Length, allCities, allCities2));
        }

        private static void DumpDecal(Ctx c, string kind, string obj, PQSMod mod, Vector3 position, double radius,
            PQS pqs, CelestialBody home)
        {
            c.Row(kind, obj, "path", PathOf(mod.transform));
            c.Fields(kind, obj, mod, true, typeof(MonoBehaviour));
            // `position` is a direction in the sphere's own frame; its surface point at the
            // body radius is the decal centre the report compares with the KSC origin.
            if (position.sqrMagnitude > 0f)
            {
                Vector3 worldDir = pqs.transform.TransformDirection(position.normalized);
                Vector3d surface = home.position + (Vector3d)worldDir * home.Radius;
                LatLon(c, kind, obj, "centre_", home, surface);
            }
            c.Row(kind, obj, "radius_m", SpikeS4Format.D(radius));
        }

        private static void DumpCamera(Ctx c)
        {
            SpaceCenterCamera2 cam = UnityEngine.Object.FindObjectOfType<SpaceCenterCamera2>();
            c.Row("camera", "SpaceCenterCamera2", "present", cam != null ? "true" : "false");
            if (cam == null) return;
            c.Row("camera", cam.name, "path", PathOf(cam.transform));
            c.Fields("camera", cam.name, cam, true, typeof(MonoBehaviour));
            Transform pivot = cam.GetPivot();
            if (pivot != null)
                LatLon(c, "camera", cam.name, "pivot_", Home(), pivot.position);
        }

        private static void DumpFacilities(Ctx c)
        {
            PSystemSetup setup = PSystemSetup.Instance;
            c.Row("psystem", "PSystemSetup", "present", setup != null ? "true" : "false");
            if (setup == null) return;
            PSystemSetup.SpaceCenterFacility[] facilities = setup.SpaceCenterFacilities;
            c.Row("psystem", "PSystemSetup", "SpaceCenterFacilities_count",
                facilities != null ? facilities.Length.ToString(CultureInfo.InvariantCulture) : "null");
            if (facilities == null) return;
            foreach (PSystemSetup.SpaceCenterFacility f in facilities)
            {
                if (f == null) continue;
                string obj = f.name;
                c.Fields("facility", obj, f, false);
                c.Row("facility", obj, "IsLaunchFacility", SpikeS4Format.FormatValue(Safe(() => f.IsLaunchFacility())));
                c.Row("facility", obj, "hostBody", f.hostBody != null ? f.hostBody.bodyName : "null");
                if (f.facilityTransform != null)
                {
                    c.Row("facility", obj, "transform_path", PathOf(f.facilityTransform));
                    LatLon(c, "facility", obj, "transform_", f.hostBody ?? Home(), f.facilityTransform.position);
                }
                if (f.spawnPoints == null) continue;
                for (int i = 0; i < f.spawnPoints.Length; i++)
                {
                    PSystemSetup.SpaceCenterFacility.SpawnPoint sp = f.spawnPoints[i];
                    if (sp == null) continue;
                    string spObj = obj + "/" + (sp.name ?? i.ToString(CultureInfo.InvariantCulture));
                    c.Fields("facility_spawn", spObj, sp, false);
                    Transform t = SafeTransform(() => sp.GetSpawnPointTransform());
                    if (t != null) LatLon(c, "facility_spawn", spObj, "transform_", f.hostBody ?? Home(), t.position);
                    else c.Row("facility_spawn", spObj, "transform", "null");
                }
            }
        }

        private static void DumpLaunchSites(Ctx c)
        {
            PSystemSetup setup = PSystemSetup.Instance;
            if (setup == null) return;
            List<LaunchSite> sites = setup.LaunchSites;
            c.Row("psystem", "PSystemSetup", "LaunchSites_count",
                sites != null ? sites.Count.ToString(CultureInfo.InvariantCulture) : "null");
            c.Row("psystem", "PSystemSetup", "StockLaunchSites", NamesOf(setup.StockLaunchSites));
            c.Row("psystem", "PSystemSetup", "NonStockLaunchSites", NamesOf(setup.NonStockLaunchSites));
            if (sites == null) return;
            foreach (LaunchSite s in sites)
            {
                if (s == null) continue;
                string obj = s.name;
                c.Fields("launchsite", obj, s, false);
                c.Row("launchsite", obj, "IsStockLaunchSite", SpikeS4Format.FormatValue(setup.IsStockLaunchSite(s.name)));
                c.Row("launchsite", obj, "isPQSCity", SpikeS4Format.FormatValue(s.isPQSCity));
                c.Row("launchsite", obj, "isPQSCity2", SpikeS4Format.FormatValue(s.isPQSCity2));
                c.Row("launchsite", obj, "IsSetup", SpikeS4Format.FormatValue(s.IsSetup));
                CelestialBody body = s.Body;
                c.Row("launchsite", obj, "Body", body != null ? body.bodyName : "null");
                if (s.launchSiteTransform != null)
                {
                    c.Row("launchsite", obj, "transform_path", PathOf(s.launchSiteTransform));
                    LatLon(c, "launchsite", obj, "transform_", body ?? Home(), s.launchSiteTransform.position);
                }
                if (s.spawnPoints == null) continue;
                for (int i = 0; i < s.spawnPoints.Length; i++)
                {
                    LaunchSite.SpawnPoint sp = s.spawnPoints[i];
                    if (sp == null) continue;
                    string spObj = obj + "/" + (sp.name ?? i.ToString(CultureInfo.InvariantCulture));
                    c.Fields("launchsite_spawn", spObj, sp, false);
                    Transform t = SafeTransform(() => sp.GetSpawnPointTransform());
                    if (t != null) LatLon(c, "launchsite_spawn", spObj, "transform_", body ?? Home(), t.position);
                    else c.Row("launchsite_spawn", spObj, "transform", "null");
                }
            }
        }

        private static void DumpFootprints(Ctx c, SpikeCsvWriter foot, SpikeCsvWriter corners, DumpResult r)
        {
            PSystemSetup setup = PSystemSetup.Instance;
            CelestialBody home = Home();
            if (setup == null || home == null) return;

            // The whole KSC (every collider under the KSC PQSCity), so S1b can test the full
            // footprint against the shoreline.
            PQS pqs = home.pqsController;
            if (pqs != null)
                foreach (PQSCity city in pqs.GetComponentsInChildren<PQSCity>(true))
                    if (city.name == "KSC")
                        Footprint(c, foot, corners, "pqscity", city.name, city.transform, home);

            if (setup.SpaceCenterFacilities != null)
                foreach (PSystemSetup.SpaceCenterFacility f in setup.SpaceCenterFacilities)
                    if (f != null && f.facilityTransform != null && Safe(() => f.IsLaunchFacility()))
                        Footprint(c, foot, corners, "facility", f.name, f.facilityTransform, f.hostBody ?? home);

            if (setup.LaunchSites != null)
                foreach (LaunchSite s in setup.LaunchSites)
                {
                    if (s == null) continue;
                    Transform root = s.launchSiteTransform;
                    if (root == null && s.pqsCity2 != null) root = s.pqsCity2.transform;
                    if (root == null)
                    {
                        foot.WriteRow(c.Session, c.Dump, c.Scene, "launchsite", s.name, "no-transform", 0, 0);
                        continue;
                    }
                    Footprint(c, foot, corners, "launchsite", s.name, root, s.Body ?? home);
                }
        }

        private static void Footprint(Ctx c, SpikeCsvWriter foot, SpikeCsvWriter cornerCsv, string source,
            string site, Transform root, CelestialBody body)
        {
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            var geo = new List<double[]>();
            var local = new List<double[]>();
            var tags = new List<string>();
            int aabb = 0;
            Quaternion inv = Quaternion.Inverse(root.rotation);
            Vector3 origin = root.position;
            for (int ci = 0; ci < colliders.Length; ci++)
            {
                Collider col = colliders[ci];
                if (col == null) continue;
                string tag = SafeTag(col.gameObject);
                tags.Add(tag);
                string shape;
                Vector3[] world = WorldCorners(col, out shape);
                if (shape == "aabb") aabb++;
                for (int k = 0; k < world.Length; k++)
                {
                    body.GetLatLonAlt(world[k], out double lat, out double lon, out double alt);
                    Vector3 l = inv * (world[k] - origin);
                    geo.Add(new[] { lat, lon, alt });
                    local.Add(new double[] { l.x, l.y, l.z });
                    cornerCsv.WriteRow(c.Session, c.Dump, source, site, ci, PathOf(col.transform, root), shape,
                        tag, col.isTrigger, k, lat, lon, alt);
                }
            }
            SpikeS4Extents e = SpikeS4Format.ComputeExtents(geo, local);
            foot.WriteRow(c.Session, c.Dump, c.Scene, source, site, PathOf(root), colliders.Length, aabb,
                e.LatMin, e.LatMax, e.LonMin, e.LonMax, e.AltMin, e.AltMax, e.CentreLat, e.CentreLon,
                e.LengthX, e.LengthY, e.LengthZ, SpikeS4Format.JoinTags(tags));
            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "footprint source={0} site={1} colliders={2} aabb={3} lat=[{4:F5},{5:F5}] lon=[{6:F5},{7:F5}] len={8:F1}x{9:F1}x{10:F1}m",
                source, site, colliders.Length, aabb, e.LatMin, e.LatMax, e.LonMin, e.LonMax,
                e.LengthX, e.LengthY, e.LengthZ));
        }

        // Oriented box corners for box and mesh colliders (their local bounds through the
        // collider's own transform); the world AABB for every other shape (flagged "aabb").
        private static Vector3[] WorldCorners(Collider col, out string shape)
        {
            Vector3 centre, size;
            if (col is BoxCollider box)
            {
                shape = "box"; centre = box.center; size = box.size;
            }
            else if (col is MeshCollider mesh && mesh.sharedMesh != null)
            {
                shape = "mesh"; centre = mesh.sharedMesh.bounds.center; size = mesh.sharedMesh.bounds.size;
            }
            else
            {
                shape = "aabb";
                Bounds b = col.bounds;
                return SpikeS4Format.BoxCorners(b.center, b.size);
            }
            Vector3[] corners = SpikeS4Format.BoxCorners(centre, size);
            for (int i = 0; i < corners.Length; i++)
                corners[i] = col.transform.TransformPoint(corners[i]);
            return corners;
        }

        private static void DumpCircles(Ctx c, SpikeCsvWriter csv, DumpResult r)
        {
            IList<LaunchSiteCircle> returned = LaunchSiteExclusionZones.GetAltSiteCircles();
            WriteCircles(c, csv, "returned", returned);
            FieldInfo f = typeof(LaunchSiteExclusionZones).GetField("cachedRuntimeCircles",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null)
            {
                csv.WriteRow(c.Session, c.Dump, c.Scene, "cache", -1, "field-missing");
                ParsekLog.Warn(Tag, "circles: field cachedRuntimeCircles not found by reflection");
                return;
            }
            var cache = f.GetValue(null) as IList<LaunchSiteCircle>;
            if (cache == null) csv.WriteRow(c.Session, c.Dump, c.Scene, "cache", -1, "null");
            else WriteCircles(c, csv, "cache", cache);
            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "circles returned={0} cache={1} cacheSites={2}",
                returned != null ? returned.Count : -1, cache != null ? cache.Count : -1,
                cache != null ? SiteNames(cache) : "null"));
        }

        private static void WriteCircles(Ctx c, SpikeCsvWriter csv, string list, IList<LaunchSiteCircle> circles)
        {
            if (circles == null) { csv.WriteRow(c.Session, c.Dump, c.Scene, list, -1, "null"); return; }
            for (int i = 0; i < circles.Count; i++)
            {
                LaunchSiteCircle k = circles[i];
                csv.WriteRow(c.Session, c.Dump, c.Scene, list, i, k.SiteName, k.Kind.ToString(), k.Latitude,
                    k.Longitude, k.Source, SpawnCollisionDetector.DefaultKscExclusionRadiusMeters);
            }
        }

        private static string SiteNames(IList<LaunchSiteCircle> circles)
        {
            var names = new List<string>();
            foreach (LaunchSiteCircle k in circles) names.Add(k.SiteName + ":" + k.Kind);
            return string.Join("|", names.ToArray());
        }

        private static string NamesOf(LaunchSite[] sites)
        {
            if (sites == null) return "null";
            var names = new List<string>();
            foreach (LaunchSite s in sites) names.Add(s != null ? s.name : "null");
            return string.Join("|", names.ToArray());
        }

        internal static string PathOf(Transform t, Transform stopAt = null)
        {
            if (t == null) return "null";
            var parts = new List<string>();
            for (Transform x = t; x != null && x != stopAt; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private static string SafeTag(GameObject go)
        {
            try { return go.tag; } catch (Exception) { return "?"; }
        }

        private static bool Safe(Func<bool> f)
        {
            try { return f(); } catch (Exception) { return false; }
        }

        private static Transform SafeTransform(Func<Transform> f)
        {
            try { return f(); } catch (Exception) { return null; }
        }
    }

    /// <summary>The in-game entry point the plan names (category SpikeS4, single run).</summary>
    public class SpikeS4ProbeTests
    {
        [InGameTest(Category = "SpikeS4", Scene = GameScenes.SPACECENTER, AllowBatchExecution = false,
            Description = "Spike S4a: dump the KSC PQSCity, decals, camera, facilities, launch sites, "
                + "footprints and the launch-site circle cache to parsek-spike/S4a/*.csv (needs PARSEK_SPIKE=S4a)")]
        public void DumpKscState()
        {
            if (!SpikeHost.IsArmed(SpikeS4Format.SpikeId))
            {
                InGameAssert.Skip("PARSEK_SPIKE does not name S4a");
                return;
            }
            SpikeS4Probe.DumpResult r = SpikeS4Probe.Dump(SpikeS4EventOrder.Session, "manual");
            InGameAssert.IsTrue(r.StateRows > 0, "no ksc_state row written");
            InGameAssert.IsTrue(r.Errors == 0, "dump sections failed: " + r.Errors + " (see [Spike.S4a] WARN lines)");
        }
    }
}
