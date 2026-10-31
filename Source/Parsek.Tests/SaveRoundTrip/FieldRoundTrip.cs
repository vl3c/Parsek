using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// How one instance field of a persisted type is expected to behave across a save and a load.
    /// Every instance field of a type that has a <see cref="FieldSpec"/> is <see cref="Persisted"/>
    /// unless the spec lists it otherwise, so a field added to a persisted type without a codec
    /// arm reds the round-trip test until it is either serialized or classified here with a reason.
    /// </summary>
    internal enum FieldRule
    {
        /// <summary>Must come back equal to what was written (the default for every field).</summary>
        Persisted,

        /// <summary>
        /// Deliberately not written. The round-trip asserts it does NOT come back with the
        /// filled value, so a stale entry (the field is in fact persisted) is caught too.
        /// </summary>
        NotPersisted,

        /// <summary>
        /// Recomputed or normalized on load from other fields; neither direction is asserted.
        /// Use only when the value after a load legitimately depends on something else.
        /// </summary>
        Derived,
    }

    /// <summary>
    /// The per-type round-trip contract: which fields are not persisted (with a reason), and
    /// the fill values for fields whose codec only accepts a constrained value.
    /// </summary>
    internal sealed class FieldSpec
    {
        internal readonly Type Type;
        private readonly Dictionary<string, FieldRule> rules = new Dictionary<string, FieldRule>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<object>> fills = new Dictionary<string, Func<object>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<object, object, string>> comparers =
            new Dictionary<string, Func<object, object, string>>(StringComparer.Ordinal);
        internal Action<object> AfterFill;

        internal FieldSpec(Type type) { Type = type; }

        internal FieldSpec NotPersisted(string reason, params string[] names) => Mark(FieldRule.NotPersisted, reason, names);
        internal FieldSpec Derived(string reason, params string[] names) => Mark(FieldRule.Derived, reason, names);

        /// <summary>Pins the value the filler writes into a field (enum subsets, ranges, formats).</summary>
        internal FieldSpec Fill(string name, Func<object> value)
        {
            RequireField(name);
            fills[name] = value;
            return this;
        }

        /// <summary>
        /// A custom equality for a field whose codec is deliberately lossy (for example a float
        /// stored at reduced precision). Returns null when equal, else a short mismatch note.
        /// </summary>
        internal FieldSpec Compare(string name, Func<object, object, string> comparer)
        {
            RequireField(name);
            comparers[name] = comparer;
            return this;
        }

        internal FieldSpec Then(Action<object> afterFill)
        {
            AfterFill = afterFill;
            return this;
        }

        private FieldSpec Mark(FieldRule rule, string reason, string[] names)
        {
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("A non-persisted field needs a reason", nameof(reason));
            foreach (string name in names)
            {
                RequireField(name);
                rules[name] = rule;
                reasons[name] = reason;
            }
            return this;
        }

        private void RequireField(string name)
        {
            if (FieldRoundTrip.GetFields(Type).Concat(StaticRoundTrip.GetStaticFields(Type))
                    .All(f => FieldRoundTrip.LogicalName(f) != name))
                throw new InvalidOperationException(
                    "FieldSpec for " + Type.Name + " names '" + name + "', which is not a field "
                    + "(remove or rename the entry)");
        }

        internal FieldRule RuleFor(string name) =>
            rules.TryGetValue(name, out FieldRule r) ? r : FieldRule.Persisted;

        internal string ReasonFor(string name) => reasons.TryGetValue(name, out string r) ? r : null;

        internal bool TryGetFill(string name, out Func<object> fill) => fills.TryGetValue(name, out fill);

        internal bool TryGetComparer(string name, out Func<object, object, string> cmp) =>
            comparers.TryGetValue(name, out cmp);

        internal IEnumerable<string> ListedNames => rules.Keys;
    }

    /// <summary>
    /// Reflection engine behind the save/load round-trip gate: fills every instance field of an
    /// object graph with a distinct non-default value and compares two graphs field by field,
    /// reporting every difference with its path. Types reached through fields (lists, dictionaries,
    /// nested classes and structs) use their own registered <see cref="FieldSpec"/> when one exists
    /// and the all-fields-persisted default when none does.
    /// </summary>
    internal sealed class FieldRoundTrip
    {
        private readonly Dictionary<Type, FieldSpec> specs;
        private int counter;
        private const int MaxDepth = 8;

        internal FieldRoundTrip(IEnumerable<FieldSpec> specs, int seed = 0)
        {
            this.specs = specs.ToDictionary(s => s.Type);
            counter = seed;
        }

        internal FieldSpec SpecFor(Type t) => specs.TryGetValue(t, out FieldSpec s) ? s : null;

        // ---------------------------------------------------------------- reflection helpers

        internal static IEnumerable<FieldInfo> GetFields(Type t)
        {
            var all = new List<FieldInfo>();
            for (Type cur = t; cur != null && cur != typeof(object) && cur != typeof(ValueType); cur = cur.BaseType)
            {
                foreach (FieldInfo f in cur.GetFields(BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (typeof(Delegate).IsAssignableFrom(f.FieldType))
                        continue; // event / callback slots are wiring, never data
                    all.Add(f);
                }
            }
            return all;
        }

        /// <summary>Maps an auto-property backing field to its property name.</summary>
        internal static string LogicalName(FieldInfo f)
        {
            string n = f.Name;
            if (n.Length > 0 && n[0] == '<')
            {
                int end = n.IndexOf('>');
                if (end > 1)
                    return n.Substring(1, end - 1);
            }
            return n;
        }

        internal static bool IsLeaf(Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)
                || t == typeof(Guid) || t == typeof(DateTime) || t == typeof(TimeSpan);
        }

        internal static object CreateInstance(Type t)
        {
            if (t.IsValueType)
                return Activator.CreateInstance(t);
            ConstructorInfo ctor = t.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            return ctor != null ? ctor.Invoke(null) : FormatterServices.GetUninitializedObject(t);
        }

        private static object DefaultFieldValue(Type owner, FieldInfo f)
        {
            try
            {
                object fresh = CreateInstance(owner);
                return f.GetValue(fresh);
            }
            catch
            {
                return f.FieldType.IsValueType ? Activator.CreateInstance(f.FieldType) : null;
            }
        }

        // ---------------------------------------------------------------- fill

        /// <summary>Builds a fresh instance of <paramref name="t"/> with every field filled.</summary>
        internal object Build(Type t, string path = null)
        {
            object o = CreateInstance(t);
            o = FillObject(o, path ?? t.Name, 0);
            return o;
        }

        internal T Build<T>() => (T)Build(typeof(T));

        /// <summary>Fills every field of <paramref name="o"/> in place (boxed copy for structs).</summary>
        internal object FillObject(object o, string path, int depth)
        {
            Type t = o.GetType();
            FieldSpec spec = SpecFor(t);
            object fresh = null;
            try { fresh = CreateInstance(t); } catch { }
            foreach (FieldInfo f in GetFields(t))
            {
                string name = LogicalName(f);
                string fpath = path + "." + name;
                object value;
                if (spec != null && spec.TryGetFill(name, out Func<object> fill))
                    value = fill();
                else
                {
                    object current = fresh != null ? f.GetValue(fresh) : null;
                    value = MakeValue(f.FieldType, current, fpath, depth + 1);
                }
                f.SetValue(o, value);
            }
            spec?.AfterFill?.Invoke(o);
            return o;
        }

        /// <summary>A distinct non-default value for one field of type <paramref name="t"/>.</summary>
        internal object MakeFieldValue(Type t, object avoid, string path) => MakeValue(t, avoid, path, 1);

        private object MakeValue(Type t, object avoid, string path, int depth)
        {
            int n = ++counter;
            Type under = Nullable.GetUnderlyingType(t);
            if (under != null)
                return MakeValue(under, null, path, depth);

            if (t == typeof(bool)) return avoid is bool b ? !b : true;
            if (t == typeof(string)) return "v" + n + "_" + Sanitize(path);
            if (t.IsEnum) return PickEnum(t, avoid, n);
            if (t == typeof(int)) return Avoid(1000 + n, avoid);
            if (t == typeof(uint)) return (uint)Avoid(1000 + n, avoid is uint u ? (int)u : (object)null);
            if (t == typeof(long)) return (long)(100000 + n);
            if (t == typeof(ulong)) return (ulong)(100000 + n);
            if (t == typeof(short)) return (short)(100 + n % 20000);
            if (t == typeof(ushort)) return (ushort)(100 + n % 20000);
            if (t == typeof(byte)) return (byte)(1 + n % 200);
            if (t == typeof(sbyte)) return (sbyte)(1 + n % 100);
            if (t == typeof(char)) return (char)('a' + n % 26);
            if (t == typeof(float)) return (float)(n + 0.5);
            if (t == typeof(double)) return n + 0.25;
            if (t == typeof(decimal)) return (decimal)n + 0.5m;
            if (t == typeof(Guid)) return new Guid(n, 0x1234, 0x5678, 1, 2, 3, 4, 5, 6, 7, 8);
            if (t == typeof(DateTime)) return new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(n);
            if (t == typeof(TimeSpan)) return TimeSpan.FromSeconds(n);
            if (t == typeof(ConfigNode))
            {
                var node = new ConfigNode("FILL");
                node.AddValue("fillKey", "fill" + n);
                return node;
            }

            if (depth > MaxDepth)
                return t.IsValueType ? Activator.CreateInstance(t) : null;

            if (t.IsArray)
            {
                Type et = t.GetElementType();
                Array arr = Array.CreateInstance(et, 2);
                for (int i = 0; i < 2; i++)
                    arr.SetValue(MakeValue(et, null, path + "[" + i + "]", depth + 1), i);
                return arr;
            }

            if (t.IsGenericType)
            {
                Type def = t.GetGenericTypeDefinition();
                Type[] args = t.GetGenericArguments();
                if (def == typeof(List<>) || def == typeof(HashSet<>) || def == typeof(IList<>)
                    || def == typeof(IReadOnlyList<>) || def == typeof(ICollection<>) || def == typeof(IEnumerable<>)
                    || def == typeof(IReadOnlyCollection<>) || def == typeof(ISet<>))
                {
                    Type concrete = def == typeof(HashSet<>) || def == typeof(ISet<>)
                        ? typeof(HashSet<>).MakeGenericType(args)
                        : typeof(List<>).MakeGenericType(args);
                    object coll = Activator.CreateInstance(concrete);
                    MethodInfo add = concrete.GetMethod("Add");
                    for (int i = 0; i < 2; i++)
                        add.Invoke(coll, new[] { MakeValue(args[0], null, path + "[" + i + "]", depth + 1) });
                    return coll;
                }
                if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>)
                    || def == typeof(SortedDictionary<,>))
                {
                    Type concrete = (def == typeof(SortedDictionary<,>) ? typeof(SortedDictionary<,>) : typeof(Dictionary<,>))
                        .MakeGenericType(args);
                    var dict = (IDictionary)Activator.CreateInstance(concrete);
                    for (int i = 0; i < 2; i++)
                    {
                        object key = MakeValue(args[0], null, path + ".key" + i, depth + 1);
                        dict[key] = MakeValue(args[1], null, path + "[" + i + "]", depth + 1);
                    }
                    return dict;
                }
            }

            if (t.IsInterface || t.IsAbstract)
                throw new InvalidOperationException(
                    "FieldRoundTrip cannot fill " + path + " of abstract type " + t.Name
                    + "; give its owner's FieldSpec a Fill(...) for it");

            object o = CreateInstance(t);
            return FillObject(o, path, depth);
        }

        private static string Sanitize(string path)
        {
            int dot = path.LastIndexOf('.');
            string leaf = dot >= 0 ? path.Substring(dot + 1) : path;
            var sb = new StringBuilder();
            foreach (char c in leaf)
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        private static int Avoid(int v, object avoid) => avoid is int a && a == v ? v + 1 : v;

        private static object PickEnum(Type t, object avoid, int n)
        {
            Array values = Enum.GetValues(t);
            var candidates = new List<object>();
            foreach (object v in values)
                if (!Equals(v, avoid) && Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0)
                    candidates.Add(v);
            if (candidates.Count == 0)
                foreach (object v in values)
                    if (!Equals(v, avoid)) candidates.Add(v);
            if (candidates.Count == 0)
                return values.GetValue(0);
            return candidates[n % candidates.Count];
        }

        // ---------------------------------------------------------------- compare

        /// <summary>
        /// Compares <paramref name="written"/> with <paramref name="read"/> and returns every
        /// difference as one line per field path (empty when the round-trip is faithful).
        /// </summary>
        internal List<string> Diff(object written, object read, string path)
        {
            var problems = new List<string>();
            DiffValue(written, read, path, problems, 0);
            return problems;
        }

        private void DiffValue(object a, object b, string path, List<string> problems, int depth)
        {
            if (a == null && b == null) return;
            if (a == null || b == null)
            {
                if (IsEmptyCollection(a) || IsEmptyCollection(b)) return; // null vs empty is the same saved state
                problems.Add(path + ": wrote " + Show(a) + ", read " + Show(b));
                return;
            }

            Type t = a.GetType();
            if (IsLeaf(t))
            {
                if (!LeafEquals(a, b))
                    problems.Add(path + ": wrote " + Show(a) + ", read " + Show(b));
                return;
            }
            if (a is ConfigNode na)
            {
                string sa = na.ToString(), sb = (b as ConfigNode)?.ToString();
                if (sa != sb)
                    problems.Add(path + ": ConfigNode differs");
                return;
            }
            if (depth > MaxDepth + 2) return;

            if (a is IDictionary da)
            {
                var db = b as IDictionary;
                if (db == null || da.Count != db.Count)
                {
                    problems.Add(path + ": wrote " + da.Count + " entries, read " + (db?.Count.ToString() ?? "none"));
                    return;
                }
                foreach (object k in da.Keys)
                {
                    if (!db.Contains(k))
                    {
                        problems.Add(path + "[" + k + "]: key missing after load");
                        continue;
                    }
                    DiffValue(da[k], db[k], path + "[" + k + "]", problems, depth + 1);
                }
                return;
            }
            if (a is IEnumerable ea && !(a is string))
            {
                List<object> la = ea.Cast<object>().ToList();
                List<object> lb = (b as IEnumerable)?.Cast<object>().ToList();
                if (lb == null || la.Count != lb.Count)
                {
                    problems.Add(path + ": wrote " + la.Count + " items, read " + (lb?.Count.ToString() ?? "none"));
                    return;
                }
                bool isSet = t.IsGenericType && t.GetGenericTypeDefinition() == typeof(HashSet<>);
                if (isSet)
                {
                    foreach (object x in la)
                        if (!lb.Any(y => LeafOrDeepEqual(x, y, depth)))
                            problems.Add(path + ": set member " + Show(x) + " missing after load");
                    return;
                }
                for (int i = 0; i < la.Count; i++)
                    DiffValue(la[i], lb[i], path + "[" + i + "]", problems, depth + 1);
                return;
            }

            DiffObject(a, b, path, problems, depth);
        }

        private bool LeafOrDeepEqual(object x, object y, int depth)
        {
            var p = new List<string>();
            DiffValue(x, y, "", p, depth + 1);
            return p.Count == 0;
        }

        private void DiffObject(object a, object b, string path, List<string> problems, int depth)
        {
            Type t = a.GetType();
            FieldSpec spec = SpecFor(t);
            foreach (FieldInfo f in GetFields(t))
            {
                string name = LogicalName(f);
                string fpath = path + "." + name;
                FieldRule rule = spec?.RuleFor(name) ?? FieldRule.Persisted;
                object va = f.GetValue(a), vb = f.GetValue(b);
                if (rule == FieldRule.Derived)
                    continue;
                if (rule == FieldRule.NotPersisted)
                {
                    if (va != null && !IsEmptyCollection(va) && LeafOrDeepEqual(va, vb, depth)
                        && !Equals(va, DefaultFieldValue(t, f)))
                        problems.Add(fpath + ": listed NotPersisted (" + spec.ReasonFor(name)
                            + ") but it survived the round-trip; drop it from the spec");
                    continue;
                }
                if (spec != null && spec.TryGetComparer(name, out Func<object, object, string> cmp))
                {
                    string note = cmp(va, vb);
                    if (note != null) problems.Add(fpath + ": " + note);
                    continue;
                }
                DiffValue(va, vb, fpath, problems, depth + 1);
            }
        }

        private static bool IsEmptyCollection(object o) =>
            o is ICollection c && c.Count == 0;

        private static bool LeafEquals(object a, object b)
        {
            if (a is float fa && b is float fb)
                return fa.Equals(fb); // NaN == NaN, exact otherwise
            if (a is double da && b is double db)
                return da.Equals(db);
            return a.Equals(b);
        }

        internal static string Show(object o)
        {
            if (o == null) return "null";
            if (o is string s) return "\"" + s + "\"";
            if (o is float f) return f.ToString("R", CultureInfo.InvariantCulture);
            if (o is double d) return d.ToString("R", CultureInfo.InvariantCulture);
            if (o is ICollection c) return o.GetType().Name + "(count=" + c.Count + ")";
            return Convert.ToString(o, CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- culture

        /// <summary>Runs <paramref name="body"/> under a comma-decimal culture (save codecs must be invariant).</summary>
        internal static void UnderCommaCulture(Action body)
        {
            CultureInfo prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                body();
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        internal static string Report(string what, List<string> problems) =>
            what + " lost or changed " + problems.Count + " field(s) across save + load:\n  "
            + string.Join("\n  ", problems);
    }
}

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// Marks a test class as the field-level save/load round-trip for <see cref="Type"/>.
    /// <c>SaveRoundTripCoverageGateTests</c> requires one for every Parsek type that declares a
    /// save or load method, so a new persisted type cannot land without its round-trip.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    internal sealed class SaveRoundTripCoversAttribute : Attribute
    {
        internal readonly Type Type;
        internal SaveRoundTripCoversAttribute(Type type) { Type = type; }
    }
}

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// The static-store form of the round-trip: a store (MissionStore, VesselNameStore, ...)
    /// keeps its persisted state in static fields, so the gate fills every static field of the
    /// store, saves, resets the store, loads, and diffs each static field against what was
    /// written. A static field added to a store without a save arm reds the test until it is
    /// persisted or classified in the store's <see cref="FieldSpec"/> (NotPersisted fields are
    /// filled and asserted not to survive; Derived fields are left untouched and not asserted).
    /// </summary>
    internal static class StaticRoundTrip
    {
        internal static IEnumerable<FieldInfo> GetStaticFields(Type t)
        {
            foreach (FieldInfo f in t.GetFields(BindingFlags.Static | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (f.IsLiteral) continue;
                if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                if (f.FieldType == typeof(CultureInfo) || f.FieldType == typeof(NumberStyles)) continue;
                if (f.Name.Contains("<>")) continue; // compiler lambda caches
                if (f.IsInitOnly && FieldRoundTrip.IsLeaf(f.FieldType)) continue; // static readonly constants
                yield return f;
            }
        }

        /// <summary>Fills every static field the spec does not mark Derived; collections in place.</summary>
        internal static void Fill(Type store, FieldRoundTrip rt, FieldSpec spec)
        {
            foreach (FieldInfo f in GetStaticFields(store))
            {
                string name = FieldRoundTrip.LogicalName(f);
                FieldRule rule = spec?.RuleFor(name) ?? FieldRule.Persisted;
                if (rule == FieldRule.Derived) continue;
                object value;
                if (spec != null && spec.TryGetFill(name, out Func<object> fill))
                    value = fill();
                else
                    value = rt.MakeFieldValue(f.FieldType, f.GetValue(null), store.Name + "." + name);
                object current = f.GetValue(null);
                if (f.IsInitOnly && current != null)
                    CopyInto(current, value);
                else
                    f.SetValue(null, value);
            }
        }

        private static void CopyInto(object target, object source)
        {
            if (target is IDictionary td && source is IDictionary sd)
            {
                td.Clear();
                foreach (DictionaryEntry e in sd) td[e.Key] = e.Value;
                return;
            }
            if (target is IList tl && source is IEnumerable sl)
            {
                tl.Clear();
                foreach (object o in sl) tl.Add(o);
                return;
            }
            Type tt = target.GetType();
            if (tt.IsGenericType && tt.GetGenericTypeDefinition() == typeof(HashSet<>))
            {
                tt.GetMethod("Clear").Invoke(target, null);
                MethodInfo add = tt.GetMethod("Add");
                foreach (object o in (IEnumerable)source) add.Invoke(target, new[] { o });
                return;
            }
            if (!tt.IsValueType && tt == source.GetType())
            {
                // A readonly static holding one state object: copy the filled object's fields into it.
                foreach (FieldInfo f in FieldRoundTrip.GetFields(tt))
                    f.SetValue(target, f.GetValue(source));
                return;
            }
            throw new InvalidOperationException("Cannot refill readonly static of type " + tt.Name
                + "; give the store's FieldSpec a Fill or classify the field");
        }

        /// <summary>A shallow copy of every static field, so a reset that clears collections in place cannot blank it.</summary>
        internal static Dictionary<string, object> Capture(Type store)
        {
            var snap = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (FieldInfo f in GetStaticFields(store))
                snap[FieldRoundTrip.LogicalName(f)] = ShallowCopy(f.GetValue(null));
            return snap;
        }

        private static object ShallowCopy(object v)
        {
            if (v == null || FieldRoundTrip.IsLeaf(v.GetType())) return v;
            Type t = v.GetType();
            if (v is IDictionary d)
            {
                var copy = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(
                    t.IsGenericType ? t.GetGenericArguments() : new[] { typeof(object), typeof(object) }));
                foreach (DictionaryEntry e in d) copy[e.Key] = e.Value;
                return copy;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(HashSet<>))
                return Activator.CreateInstance(t, v);
            if (v is IList l)
            {
                var copy = new List<object>();
                foreach (object o in l) copy.Add(o);
                return copy;
            }
            return v;
        }

        /// <summary>Diffs the store's statics now against a <see cref="Capture"/> taken before the save.</summary>
        internal static List<string> Diff(Type store, Dictionary<string, object> written, FieldRoundTrip rt, FieldSpec spec)
        {
            var problems = new List<string>();
            Dictionary<string, object> read = Capture(store);
            foreach (var kv in written)
            {
                string path = store.Name + "." + kv.Key;
                FieldRule rule = spec?.RuleFor(kv.Key) ?? FieldRule.Persisted;
                object a = kv.Value, b = read[kv.Key];
                if (a is List<object> la && b is List<object> lb0)
                {
                    a = la; b = lb0;
                }
                if (rule == FieldRule.Derived) continue;
                List<string> diff = rt.Diff(a, b, path);
                if (rule == FieldRule.NotPersisted)
                {
                    if (diff.Count == 0 && a != null && !(a is ICollection c && c.Count == 0))
                        problems.Add(path + ": listed NotPersisted (" + spec.ReasonFor(kv.Key)
                            + ") but it survived the round-trip; drop it from the spec");
                    continue;
                }
                problems.AddRange(diff);
            }
            return problems;
        }
    }
}
