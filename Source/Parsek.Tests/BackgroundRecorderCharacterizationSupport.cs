using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Shared support for the BackgroundRecorder long-method characterization goldens:
    /// headless KSP stubs, a reflective dump of the recorder's per-vessel state and the
    /// golden-file assert.
    ///
    /// Stubs: an "alive" Unity object has a non-zero cached native pointer, so Unity's
    /// <c>==</c> reads it as alive without touching native code; a "dead" one (pointer zero)
    /// reads as null to Unity's <c>==</c> while its managed fields stay readable. Any native
    /// member (<c>transform</c>, <c>GetComponent</c>, ...) still throws on mono, so a case that
    /// reaches one records the exception type and stops there.
    /// </summary>
    internal static class BackgroundRecorderCharacterizationSupport
    {
        private static readonly FieldInfo CachedPtr = typeof(UnityEngine.Object).GetField(
            "m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static T Uninit<T>()
        {
            return (T)FormatterServices.GetUninitializedObject(typeof(T));
        }

        internal static T Alive<T>() where T : UnityEngine.Object
        {
            T o = Uninit<T>();
            Assert.NotNull(CachedPtr);
            CachedPtr.SetValue(o, new IntPtr(1));
            return o;
        }

        internal static CelestialBody Body(string name, bool atmosphere, double atmosphereDepth)
        {
            CelestialBody body = Alive<CelestialBody>();
            body.bodyName = name;
            body.atmosphere = atmosphere;
            body.atmosphereDepth = atmosphereDepth;
            return body;
        }

        /// <summary>A vessel stub whose mainBody resolves through orbitDriver.orbit.referenceBody.</summary>
        internal static Vessel Vessel(uint pid, bool alive, CelestialBody body, double altitude = 0.0,
            bool packed = false, bool loaded = true)
        {
            Vessel v = alive ? Alive<Vessel>() : Uninit<Vessel>();
            v.persistentId = pid;
            v.parts = new List<Part>();
            v.orbitDriver = Uninit<OrbitDriver>();
            v.orbitDriver.orbit = Uninit<Orbit>();
            v.orbitDriver.orbit.referenceBody = body;
            v.altitude = altitude;
            v.packed = packed;
            v.loaded = loaded;
            v.vesselName = "Stub " + pid.ToString(CultureInfo.InvariantCulture);
            return v;
        }

        /// <summary>Installs Planetarium.fetch with a fixed UT; returns the previous value.</summary>
        internal static Planetarium InstallPlanetarium(double ut)
        {
            Planetarium previous = Planetarium.fetch;
            Planetarium p = Alive<Planetarium>();
            p.time = ut;
            Planetarium.fetch = p;
            return previous;
        }

        internal static void SetPlanetariumUT(double ut)
        {
            Planetarium.fetch.time = ut;
        }

        // ------------------------------------------------------------------ dump

        internal static string D(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>
        /// Dumps the loaded and on-rails state the recorder holds for <paramref name="pid"/>
        /// (every field, sorted by name, collections expanded one level) plus its pending
        /// per-pid dictionaries.
        /// </summary>
        internal static void DumpRecorder(StringBuilder sb, BackgroundRecorder bg, uint pid)
        {
            foreach (string dictName in new[]
            {
                "loadedStates", "onRailsStates", "pendingInitialTrajectoryPoints",
                "pendingInitialEnvironmentOverrides", "debrisTTLExpiry", "finalizationCaches",
                "pendingDebrisSeedParentAnchorPoints", "railsSpanPartStates"
            })
            {
                FieldInfo f = typeof(BackgroundRecorder).GetField(dictName,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (f == null)
                {
                    sb.Append("  ").Append(dictName).Append(": (no field)\n");
                    continue;
                }
                var dict = f.GetValue(bg) as IDictionary;
                if (dict == null || !dict.Contains(pid))
                {
                    sb.Append("  ").Append(dictName).Append(": -\n");
                    continue;
                }
                sb.Append("  ").Append(dictName).Append(":\n");
                DumpValue(sb, dict[pid], 2, 0);
            }
            FieldInfo skip = typeof(BackgroundRecorder).GetField("fixedStepSampleSkipCount",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (skip != null)
                sb.Append("  fixedStepSampleSkipCount=").Append(skip.GetValue(bg)).Append('\n');
        }

        internal static void DumpRecording(StringBuilder sb, Recording rec)
        {
            if (rec == null)
            {
                sb.Append("  recording: null\n");
                return;
            }
            sb.Append("  recording ").Append(rec.RecordingId)
                .Append(" explicitEnd=").Append(D(rec.ExplicitEndUT))
                .Append(" points=").Append(rec.Points.Count)
                .Append(" partEvents=").Append(rec.PartEvents.Count)
                .Append(" sections=").Append(rec.TrackSections.Count)
                .Append(" orbitSegments=").Append(rec.OrbitSegments.Count)
                .Append(" filesDirty=").Append(rec.FilesDirty)
                .Append('\n');
            for (int i = 0; i < rec.Points.Count; i++)
            {
                sb.Append("    pt[").Append(i).Append("] ");
                AppendPoint(sb, rec.Points[i]);
                sb.Append('\n');
            }
            for (int i = 0; i < rec.PartEvents.Count; i++)
            {
                PartEvent e = rec.PartEvents[i];
                sb.Append("    ev[").Append(i).Append("] ").Append(e.eventType)
                    .Append(" pid=").Append(e.partPersistentId)
                    .Append(" ut=").Append(D(e.ut)).Append('\n');
            }
        }

        private static void AppendPoint(StringBuilder sb, TrajectoryPoint p)
        {
            sb.Append("ut=").Append(D(p.ut))
                .Append(" lat=").Append(D(p.latitude))
                .Append(" lon=").Append(D(p.longitude))
                .Append(" alt=").Append(D(p.altitude))
                .Append(" body=").Append(p.bodyName ?? "null")
                .Append(" clr=").Append(D(p.recordedGroundClearance));
        }

        private static void DumpValue(StringBuilder sb, object value, int indent, int depth)
        {
            string pad = new string(' ', indent * 2);
            if (value == null)
            {
                sb.Append(pad).Append("null\n");
                return;
            }
            Type t = value.GetType();
            FieldInfo[] fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .ToArray();
            if (IsScalar(t) || fields.Length == 0)
            {
                sb.Append(pad).Append(Scalar(value)).Append('\n');
                return;
            }
            foreach (FieldInfo f in fields)
            {
                // Wall-clock stamps (RecordingFinalizationCache.CachedAtRealtime) differ per run.
                if (f.Name.IndexOf("Realtime", StringComparison.Ordinal) >= 0)
                    continue;
                object fv = f.GetValue(value);
                sb.Append(pad).Append(f.Name).Append('=');
                AppendField(sb, fv, indent, depth);
            }
        }

        private static void AppendField(StringBuilder sb, object fv, int indent, int depth)
        {
            if (fv == null)
            {
                sb.Append("null\n");
                return;
            }
            Type ft = fv.GetType();
            if (IsScalar(ft))
            {
                sb.Append(Scalar(fv)).Append('\n');
                return;
            }
            if (fv is UnityEngine.Object)
            {
                sb.Append("<").Append(ft.Name).Append(">\n");
                return;
            }
            if (fv is IEnumerable seq && !(fv is string))
            {
                var items = new List<object>();
                foreach (object o in seq)
                    items.Add(o);
                sb.Append("count=").Append(items.Count).Append('\n');
                if (depth < 2)
                {
                    List<string> rendered = items.Select(o => RenderItem(o, depth + 1)).ToList();
                    if (!(fv is IList))
                        rendered.Sort(StringComparer.Ordinal);
                    foreach (string r in rendered)
                        sb.Append(new string(' ', (indent + 1) * 2)).Append("- ").Append(r).Append('\n');
                }
                return;
            }
            if (depth >= 3)
            {
                sb.Append("<").Append(ft.Name).Append(">\n");
                return;
            }
            sb.Append('\n');
            DumpValue(sb, fv, indent + 1, depth + 1);
        }

        private static string RenderItem(object o, int depth)
        {
            if (o == null)
                return "null";
            Type t = o.GetType();
            if (IsScalar(t))
                return Scalar(o);
            if (o is TrajectoryPoint p)
            {
                var sb = new StringBuilder();
                AppendPoint(sb, p);
                return sb.ToString();
            }
            var inner = new StringBuilder();
            DumpValue(inner, o, 0, depth);
            return "{" + inner.ToString().TrimEnd('\n').Replace("\n", "; ") + "}";
        }

        private static bool IsScalar(Type t)
        {
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)
                || t == typeof(UnityEngine.Vector3) || t == typeof(UnityEngine.Quaternion)
                || t == typeof(Vector3d) || t == typeof(Guid);
        }

        private static string Scalar(object v)
        {
            switch (v)
            {
                case double d: return D(d);
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                case UnityEngine.Vector3 v3:
                    return "(" + v3.x.ToString("R", CultureInfo.InvariantCulture) + ","
                        + v3.y.ToString("R", CultureInfo.InvariantCulture) + ","
                        + v3.z.ToString("R", CultureInfo.InvariantCulture) + ")";
                case UnityEngine.Quaternion q:
                    return "(" + q.x.ToString("R", CultureInfo.InvariantCulture) + ","
                        + q.y.ToString("R", CultureInfo.InvariantCulture) + ","
                        + q.z.ToString("R", CultureInfo.InvariantCulture) + ","
                        + q.w.ToString("R", CultureInfo.InvariantCulture) + ")";
                case Vector3d v3d:
                    return "(" + D(v3d.x) + "," + D(v3d.y) + "," + D(v3d.z) + ")";
                case IFormattable fm: return fm.ToString(null, CultureInfo.InvariantCulture);
                default: return v.ToString();
            }
        }

        // ---------------------------------------------------------------- golden

        private static string GoldenPath(string name)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            return Path.Combine(root, "Source", "Parsek.Tests", "Fixtures", "Characterization", name);
        }

        internal static void AssertGolden(string name, string actual)
        {
            string path = GoldenPath(name);
            if (Environment.GetEnvironmentVariable("PARSEK_CHARACTERIZATION_REGEN") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, actual, new UTF8Encoding(false));
                return;
            }

            Assert.True(File.Exists(path), "golden capture missing: " + path);
            string expected = File.ReadAllText(path).Replace("\r\n", "\n");
            if (string.Equals(expected, actual, StringComparison.Ordinal))
                return;
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "golden-actual");
            Directory.CreateDirectory(dir);
            string actualPath = Path.Combine(dir, name);
            File.WriteAllText(actualPath, actual, new UTF8Encoding(false));
            Assert.True(false, "golden mismatch for " + path + "; actual transcript written to " + actualPath);
        }
    }
}
