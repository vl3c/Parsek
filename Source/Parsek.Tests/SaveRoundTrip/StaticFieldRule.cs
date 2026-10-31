using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// The save contract of one static field of a static store: persisted (asserted equal
    /// after a save and a reload), not persisted (asserted NOT restored, with the reason), or a
    /// constant (an immutable helper such as a culture, never state).
    /// </summary>
    internal sealed class StaticFieldRule
    {
        internal FieldRule Rule;
        internal bool IsConstant;
        internal string Reason;

        internal static StaticFieldRule Persisted() => new StaticFieldRule { Rule = FieldRule.Persisted };

        internal static StaticFieldRule NotPersisted(string reason) =>
            new StaticFieldRule { Rule = FieldRule.NotPersisted, Reason = reason };

        internal static StaticFieldRule Constant() =>
            new StaticFieldRule { Rule = FieldRule.Derived, IsConstant = true, Reason = "immutable helper" };

        /// <summary>Static fields that hold state (literal consts and delegates excluded), by logical name.</summary>
        internal static List<FieldInfo> StateFields(Type t) =>
            t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => !f.IsLiteral && !typeof(Delegate).IsAssignableFrom(f.FieldType))
                .ToList();

        internal static void AssertClassified(Type store, Dictionary<string, StaticFieldRule> table)
        {
            var names = StateFields(store).Select(FieldRoundTrip.LogicalName).ToList();
            var unclassified = names.Where(n => !table.ContainsKey(n)).ToList();
            var stale = table.Keys.Where(k => !names.Contains(k)).ToList();
            Assert.True(unclassified.Count == 0,
                store.Name + " static field(s) with no save classification: " + string.Join(", ", unclassified)
                + ". Persist the field in the store's save/load and mark it Persisted, or classify it "
                + "NotPersisted with the reason a load need not restore it.");
            Assert.True(stale.Count == 0, store.Name + " classification names missing field(s): " + string.Join(", ", stale));
        }

        /// <summary>
        /// Captures every non-constant static field. A collection is copied shallowly, so a
        /// later clear of the store's own list does not empty the snapshot.
        /// </summary>
        internal static Dictionary<string, object> Snapshot(Type store, Dictionary<string, StaticFieldRule> table)
        {
            var snap = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (FieldInfo f in StateFields(store))
            {
                string name = FieldRoundTrip.LogicalName(f);
                if (table.TryGetValue(name, out StaticFieldRule r) && r.IsConstant)
                    continue;
                snap[name] = ShallowCopy(f.GetValue(null));
            }
            return snap;
        }

        private static object ShallowCopy(object v)
        {
            if (v == null || v is string || v.GetType().IsValueType)
                return v;
            Type t = v.GetType();
            if (t.IsGenericType)
            {
                Type def = t.GetGenericTypeDefinition();
                // Each has a copy constructor over its own contents.
                if (def == typeof(List<>) || def == typeof(HashSet<>) || def == typeof(Dictionary<,>))
                    return Activator.CreateInstance(t, v);
            }
            return v;
        }

        internal static List<string> Diff(
            FieldRoundTrip rt, Type store, Dictionary<string, StaticFieldRule> table,
            Dictionary<string, object> written, Dictionary<string, object> read)
        {
            var problems = new List<string>();
            foreach (var kv in written)
            {
                string path = store.Name + "." + kv.Key;
                StaticFieldRule rule = table[kv.Key];
                object after = read[kv.Key];
                if (rule.Rule == FieldRule.Persisted)
                {
                    problems.AddRange(rt.Diff(kv.Value, after, path));
                }
                else if (rule.Rule == FieldRule.NotPersisted)
                {
                    bool nonEmpty = kv.Value != null
                        && !(kv.Value is System.Collections.ICollection c && c.Count == 0);
                    // A value-type counter can coincide; only a non-empty collection is checked.
                    if (nonEmpty && !kv.Value.GetType().IsValueType && rt.Diff(kv.Value, after, path).Count == 0)
                        problems.Add(path + ": listed NotPersisted (" + rule.Reason
                            + ") but it survived the round-trip; drop it from the table");
                }
            }
            return problems;
        }
    }
}
