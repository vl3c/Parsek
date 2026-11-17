using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek.InGameTests.Spikes
{
    // R0 spike branch only (spike/mp-r0, never merged). Spike S4a, plan
    // docs/dev/plans/multiplayer/r0-spikes.md "S4a Probe: where the KSC is and what reads it".
    // Pure parts (xUnit-tested): value formatting, the reflective field walk, the turn-back
    // predicate, footprint extents and the CSV headers. Nothing here touches a live game.

    /// <summary>One (field, value) pair of the reflective dump.</summary>
    internal struct SpikeS4Field
    {
        internal string Name;
        internal string Value;
    }

    /// <summary>Lat / lon / alt bounds and root-local extents of one footprint.</summary>
    internal struct SpikeS4Extents
    {
        internal int Points;
        internal double LatMin, LatMax, LonMin, LonMax, AltMin, AltMax;
        internal double LocalMinX, LocalMaxX, LocalMinY, LocalMaxY, LocalMinZ, LocalMaxZ;

        internal double LengthX => Points == 0 ? 0.0 : LocalMaxX - LocalMinX;
        internal double LengthY => Points == 0 ? 0.0 : LocalMaxY - LocalMinY;
        internal double LengthZ => Points == 0 ? 0.0 : LocalMaxZ - LocalMinZ;
        internal double CentreLat => Points == 0 ? double.NaN : (LatMin + LatMax) / 2.0;
        internal double CentreLon => Points == 0 ? double.NaN : (LonMin + LonMax) / 2.0;
    }

    internal static class SpikeS4Format
    {
        internal const string SpikeId = "S4a";

        /// <summary>Second switch, read with S4a armed: the Game.Start turn-back prefix.</summary>
        internal const string TurnBackEnvVar = "PARSEK_SPIKE_S4A_TURNBACK";

        /// <summary>A save folder or game title starting with this is turned back at Game.Start.</summary>
        internal const string TurnBackPrefix = "SPIKE-REFUSE";

        internal static readonly string[] KscStateHeader =
            { "session", "dump", "scene", "reason", "kind", "object", "field", "value" };

        internal static readonly string[] FootprintHeader =
        {
            "session", "dump", "scene", "source", "site", "root", "colliders", "colliders_aabb",
            "lat_min", "lat_max", "lon_min", "lon_max", "alt_min", "alt_max",
            "centre_lat", "centre_lon", "len_x_m", "len_y_m", "len_z_m", "tags",
        };

        internal static readonly string[] CornerHeader =
        {
            "session", "dump", "source", "site", "collider_index", "collider", "shape", "tag",
            "is_trigger", "corner", "lat", "lon", "alt",
        };

        internal static readonly string[] CircleHeader =
        {
            "session", "dump", "scene", "list", "index", "site", "kind", "lat", "lon",
            "circle_source", "radius_m",
        };

        internal static readonly string[] EventHeader =
        {
            "session", "seq", "frame", "realtime_s", "event", "detail", "scene",
            "game_id", "game_title", "save_folder",
        };

        /// <summary>True for the literal "1" (the same fail-closed shape as PARSEK_TEST_COMMANDS).</summary>
        internal static bool IsTurnBackArmedValue(string raw)
            => raw != null && raw.Trim() == "1";

        /// <summary>
        /// True when the game being started must be turned back: its save folder or its
        /// title starts with SPIKE-REFUSE (ordinal, case-insensitive). The folder test is the
        /// one that matters for a copied save: a folder copy keeps the source save's Title.
        /// </summary>
        internal static bool ShouldTurnBack(string title, string saveFolder)
            => StartsWithRefuse(saveFolder) || StartsWithRefuse(title);

        private static bool StartsWithRefuse(string s)
            => !string.IsNullOrEmpty(s)
               && s.Trim().StartsWith(TurnBackPrefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Invariant text of one value. Null -> "null"; float / double -> "R"; Vector2/3,
        /// Vector3d, Vector2d, Quaternion, QuaternionD -> components joined by one space;
        /// enum -> its name; array -> "len=N"; other reference types -> "&lt;TypeName&gt;".
        /// </summary>
        internal static string FormatValue(object value)
        {
            if (value == null) return "null";
            switch (value)
            {
                case string s: return s;
                case bool b: return b ? "true" : "false";
                case double d: return D(d);
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                case Vector3 v: return Join(v.x, v.y, v.z);
                case Vector2 v: return Join(v.x, v.y);
                case Quaternion q: return Join(q.x, q.y, q.z, q.w);
                case Vector3d v: return JoinD(v.x, v.y, v.z);
                case Vector2d v: return JoinD(v.x, v.y);
                case QuaternionD q: return JoinD(q.x, q.y, q.z, q.w);
                case Color c: return Join(c.r, c.g, c.b, c.a);
                case Enum e: return e.ToString();
                case Array a: return "len=" + a.Length.ToString(CultureInfo.InvariantCulture);
                case IFormattable fmt: return fmt.ToString(null, CultureInfo.InvariantCulture);
            }
            return "<" + value.GetType().Name + ">";
        }

        internal static string D(double d) => d.ToString("R", CultureInfo.InvariantCulture);

        private static string Join(params float[] xs)
        {
            var parts = new string[xs.Length];
            for (int i = 0; i < xs.Length; i++) parts[i] = xs[i].ToString("R", CultureInfo.InvariantCulture);
            return string.Join(" ", parts);
        }

        private static string JoinD(params double[] xs)
        {
            var parts = new string[xs.Length];
            for (int i = 0; i < xs.Length; i++) parts[i] = D(xs[i]);
            return string.Join(" ", parts);
        }

        /// <summary>
        /// Field types the dump writes by value: primitives, string, enums, the vector and
        /// quaternion structs, Color, and arrays (as their length). Object references are
        /// written as their type name only, so the walk never recurses.
        /// </summary>
        internal static bool IsValueLike(Type t)
        {
            if (t == null) return false;
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)
                   || t == typeof(Vector3) || t == typeof(Vector2) || t == typeof(Quaternion)
                   || t == typeof(Vector3d) || t == typeof(Vector2d) || t == typeof(QuaternionD)
                   || t == typeof(Color);
        }

        /// <summary>
        /// Every instance field declared on the object's type and its bases up to (not
        /// including) <paramref name="stopAt"/>, in declaration order, base fields last.
        /// Public fields always; non-public ones when <paramref name="includeNonPublic"/>.
        /// Compiler-generated backing fields are skipped. A field whose read throws is
        /// written as "&lt;error: Type&gt;".
        /// </summary>
        internal static List<SpikeS4Field> DumpFields(object obj, bool includeNonPublic, Type stopAt = null)
        {
            var result = new List<SpikeS4Field>();
            if (obj == null) return result;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
            if (includeNonPublic) flags |= BindingFlags.NonPublic;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (Type t = obj.GetType(); t != null && t != stopAt && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(flags))
                {
                    if (f.Name.IndexOf('<') >= 0) continue;
                    if (f.IsDefined(typeof(CompilerGeneratedAttribute), false)) continue;
                    string name = seen.Add(f.Name) ? f.Name : t.Name + "." + f.Name;
                    string text;
                    try
                    {
                        object v = f.GetValue(obj);
                        text = IsValueLike(f.FieldType) || v == null || v is Array
                            ? FormatValue(v)
                            : "<" + v.GetType().Name + ">";
                    }
                    catch (Exception ex)
                    {
                        text = "<error: " + ex.GetType().Name + ">";
                    }
                    result.Add(new SpikeS4Field { Name = name, Value = text });
                }
            }
            return result;
        }

        /// <summary>
        /// Bounds of a footprint. <paramref name="latLonAlt"/> and <paramref name="local"/>
        /// are parallel lists of 3-element arrays (lat, lon, alt) and (x, y, z) metres in
        /// the root's own axes. Throws when the lists differ in length.
        /// </summary>
        internal static SpikeS4Extents ComputeExtents(IList<double[]> latLonAlt, IList<double[]> local)
        {
            if (latLonAlt == null || local == null) throw new ArgumentNullException();
            if (latLonAlt.Count != local.Count) throw new ArgumentException("lists differ in length");
            var e = new SpikeS4Extents();
            for (int i = 0; i < latLonAlt.Count; i++)
            {
                double[] g = latLonAlt[i], l = local[i];
                if (g == null || l == null || g.Length < 3 || l.Length < 3) continue;
                if (double.IsNaN(g[0]) || double.IsNaN(g[1])) continue;
                if (e.Points == 0)
                {
                    e.LatMin = e.LatMax = g[0];
                    e.LonMin = e.LonMax = g[1];
                    e.AltMin = e.AltMax = g[2];
                    e.LocalMinX = e.LocalMaxX = l[0];
                    e.LocalMinY = e.LocalMaxY = l[1];
                    e.LocalMinZ = e.LocalMaxZ = l[2];
                }
                else
                {
                    e.LatMin = Math.Min(e.LatMin, g[0]); e.LatMax = Math.Max(e.LatMax, g[0]);
                    e.LonMin = Math.Min(e.LonMin, g[1]); e.LonMax = Math.Max(e.LonMax, g[1]);
                    e.AltMin = Math.Min(e.AltMin, g[2]); e.AltMax = Math.Max(e.AltMax, g[2]);
                    e.LocalMinX = Math.Min(e.LocalMinX, l[0]); e.LocalMaxX = Math.Max(e.LocalMaxX, l[0]);
                    e.LocalMinY = Math.Min(e.LocalMinY, l[1]); e.LocalMaxY = Math.Max(e.LocalMaxY, l[1]);
                    e.LocalMinZ = Math.Min(e.LocalMinZ, l[2]); e.LocalMaxZ = Math.Max(e.LocalMaxZ, l[2]);
                }
                e.Points++;
            }
            return e;
        }

        /// <summary>The eight corners of a box (centre, full size), sign order x fastest.</summary>
        internal static Vector3[] BoxCorners(Vector3 centre, Vector3 size)
        {
            Vector3 h = size * 0.5f;
            var corners = new Vector3[8];
            int i = 0;
            for (int sz = -1; sz <= 1; sz += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sx = -1; sx <= 1; sx += 2)
                        corners[i++] = new Vector3(centre.x + sx * h.x, centre.y + sy * h.y, centre.z + sz * h.z);
            return corners;
        }

        /// <summary>Sorted, distinct, non-empty tags joined by '|'.</summary>
        internal static string JoinTags(IEnumerable<string> tags)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            if (tags != null)
                foreach (string t in tags)
                    if (!string.IsNullOrEmpty(t)) set.Add(t);
            return string.Join("|", set);
        }

        /// <summary>One event_order.csv row (see <see cref="EventHeader"/>).</summary>
        internal static object[] EventRow(string session, int seq, int frame, double realtime,
            string evt, string detail, string scene, string gameId, string gameTitle, string saveFolder)
            => new object[] { session, seq, frame, realtime, evt, detail, scene, gameId, gameTitle, saveFolder };

        /// <summary>The log line body of one event (after the [Parsek][INFO][Spike.S4a] prefix).</summary>
        internal static string FormatEventMessage(int seq, int frame, string evt, string detail,
            string scene, string gameId, string gameTitle, string saveFolder)
            => string.Format(CultureInfo.InvariantCulture,
                "event seq={0} frame={1} ev={2} scene={3} game={4} title={5} folder={6} detail={7}",
                seq, frame, evt, scene ?? "", gameId ?? "none", gameTitle ?? "", saveFolder ?? "", detail ?? "");

        /// <summary>One argument of a hooked call, for the event detail column.</summary>
        internal static string FormatArg(object arg)
        {
            if (arg == null) return "null";
            if (arg is string s) return "\"" + s + "\"";
            if (IsValueLike(arg.GetType())) return FormatValue(arg);
            return "<" + arg.GetType().Name + ">";
        }

        /// <summary>A hooked method name: "DeclaringType.Method".</summary>
        internal static string MethodLabel(MethodBase m)
            => m == null ? "?" : (m.DeclaringType != null ? m.DeclaringType.Name + "." : "") + m.Name;
    }

    /// <summary>
    /// Process-local readable ids ("g1", "g2", ...) for live objects, held weakly so a
    /// dropped Game is not kept alive. Used to tell which Game is current at each event.
    /// </summary>
    internal sealed class SpikeObjectIds
    {
        private sealed class Box { internal string Id; }

        private readonly ConditionalWeakTable<object, Box> ids = new ConditionalWeakTable<object, Box>();
        private readonly string prefix;
        private int next;

        internal SpikeObjectIds(string prefix) { this.prefix = prefix ?? ""; }

        internal string IdOf(object o)
        {
            if (o == null) return "none";
            Box box = ids.GetValue(o, _ => new Box { Id = prefix + (++next).ToString(CultureInfo.InvariantCulture) });
            return box.Id;
        }
    }
}
