using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// The gate that keeps the save round-trip suite complete: every Parsek type that declares a
    /// save or load method over a <see cref="ConfigNode"/>, or reads / writes a binary sidecar,
    /// must have a test class marked <see cref="SaveRoundTripCoversAttribute"/> for it, or a
    /// row in <see cref="Exempt"/> naming why it needs none. A new persisted type therefore
    /// cannot land without its field-level round-trip.
    /// </summary>
    public class SaveRoundTripCoverageGateTests
    {
        /// <summary>
        /// Types with a save/load-shaped method that need no round-trip of their own, each with
        /// the reason. Keep this short: a codec helper belongs here only when the type it
        /// serializes has its own covering test.
        /// </summary>
        internal static readonly Dictionary<string, string> Exempt = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Parsek.ParsekScenario"] =
                "orchestrator: every node OnSave writes is produced by a covered type's own codec",
            ["Parsek.VesselSpawner"] =
                "SaveOrbitToNode writes a stock ORBIT node for a spawn, which stock KSP reads, not Parsek",
        };

        private static readonly string[] Prefixes = { "Save", "Load", "Serialize", "Deserialize", "TryLoad" };

        internal static List<Type> PersistenceBearingTypes()
        {
            var result = new List<Type>();
            foreach (Type t in typeof(GameAction).Assembly.GetTypes())
            {
                string ns = t.Namespace ?? "";
                if (ns.StartsWith("Parsek.InGameTests", StringComparison.Ordinal)
                    || ns.StartsWith("Parsek.TestCommands", StringComparison.Ordinal))
                    continue;
                if (t.GetCustomAttributes(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false).Any())
                    continue;

                bool bears = false;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    ParameterInfo[] ps = m.GetParameters();
                    bool configNodeShaped = Prefixes.Any(p => m.Name.StartsWith(p, StringComparison.Ordinal))
                        && ps.Any(p => p.ParameterType == typeof(ConfigNode));
                    bool binaryShaped = ps.Any(p => p.ParameterType == typeof(BinaryWriter)
                        || p.ParameterType == typeof(BinaryReader));
                    if (configNodeShaped || binaryShaped)
                    {
                        bears = true;
                        break;
                    }
                }
                if (bears)
                    result.Add(t);
            }
            return result;
        }

        internal static HashSet<Type> CoveredTypes() =>
            new HashSet<Type>(typeof(SaveRoundTripCoverageGateTests).Assembly.GetTypes()
                .SelectMany(t => t.GetCustomAttributes(typeof(SaveRoundTripCoversAttribute), false))
                .Cast<SaveRoundTripCoversAttribute>()
                .Select(a => a.Type));

        [Fact]
        public void EveryPersistedType_HasAFieldLevelRoundTrip()
        {
            HashSet<Type> covered = CoveredTypes();
            List<string> uncovered = PersistenceBearingTypes()
                .Where(t => !covered.Contains(t) && !Exempt.ContainsKey(t.FullName))
                .Select(t => t.FullName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            Assert.True(uncovered.Count == 0,
                "Type(s) with a save/load method and no field-level round-trip test: "
                + string.Join(", ", uncovered)
                + ". Add a test that fills every field, saves, loads and diffs with FieldRoundTrip, and mark it "
                + "[SaveRoundTripCovers(typeof(...))]; or add an Exempt row with the reason it needs none.");
        }

        [Fact]
        public void ExemptRows_NameRealPersistenceTypes()
        {
            var bearing = new HashSet<string>(PersistenceBearingTypes().Select(t => t.FullName));
            var stale = Exempt.Keys.Where(k => !bearing.Contains(k)).ToList();
            Assert.True(stale.Count == 0, "Stale Exempt row(s) (no such save/load-bearing type): " + string.Join(", ", stale));
        }

        [Fact]
        public void CoveredTypes_AreNotAlsoExempt()
        {
            var both = CoveredTypes().Where(t => Exempt.ContainsKey(t.FullName)).Select(t => t.FullName).ToList();
            Assert.True(both.Count == 0, "Type(s) both covered and exempt: " + string.Join(", ", both));
        }
    }
}
