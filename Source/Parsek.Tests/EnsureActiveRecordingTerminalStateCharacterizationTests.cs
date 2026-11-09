using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization golden for <c>ParsekFlight.EnsureActiveRecordingTerminalState</c>,
    /// written against the method as it stood before the wave-2 W8b extraction and kept
    /// unchanged through it. Each case builds a fresh tree (and, where the branch needs one, a
    /// finalization cache or a scene-exit finalizer result), runs the method once, and records
    /// the return value, every public or internal member of the active recording that the call
    /// changed (doubles as bit patterns, collections element by element, snapshots node by
    /// node) and every log line the call wrote.
    ///
    /// The live-vessel branch (<c>v != null</c>) is not reachable here: the vessel comes from
    /// <c>FlightRecorder.FindVesselByPid</c>, which reads FlightGlobals, and that type cannot
    /// initialize in a headless host. Every other exit is covered.
    ///
    /// The golden lives in <c>Fixtures/Characterization/</c>; a mismatch writes the actual
    /// transcript next to the test binary and names it, and PARSEK_CHARACTERIZATION_REGEN=1
    /// rewrites it.
    /// </summary>
    [Collection("Sequential")]
    public class EnsureActiveRecordingTerminalStateCharacterizationTests : IDisposable
    {
        private const string GoldenName = "ensure-active-recording-terminal-state.golden.txt";
        private const int MaxDepth = 4;
        private static readonly Regex ElapsedMs = new Regex(@"elapsedMs=[0-9.,]+");

        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;

        public EnsureActiveRecordingTerminalStateCharacterizationTests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            // Several log lines format numbers with the thread culture; the golden is
            // recorded under the invariant one so a ro-RO host reads the same text.
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            // A terminal stamp runs the crew observer only once KerbalsModule has been
            // touched in this process; install it so the transcript does not depend on
            // which test classes ran first.
            KerbalsModule.EnsureTerminalStampObserverInstalled();
        }

        public void Dispose()
        {
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            Thread.CurrentThread.CurrentCulture = savedCulture;
        }

        // ---------------------------------------------------------------- transcript

        private static string Format(object value, int depth)
        {
            if (value == null) return "<null>";
            // Bit patterns, not "R" text: the .NET Framework and mono format some floats and
            // the sign of zero differently, and the golden must read the same on both hosts.
            if (value is double d)
                return "d:" + BitConverter.DoubleToInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture);
            if (value is float f)
                return "f:" + BitConverter.ToInt32(BitConverter.GetBytes(f), 0).ToString("X8", CultureInfo.InvariantCulture);
            if (value is string s) return "'" + s + "'";
            if (value is bool b) return b ? "true" : "false";
            Type type = value.GetType();
            if (type.IsEnum || type.IsPrimitive || value is Guid || value is decimal)
                return value is IFormattable fm ? fm.ToString(null, CultureInfo.InvariantCulture) : value.ToString();
            if (value is ConfigNode node) return FormatNode(node, depth);
            if (value is UnityEngine.Object) return "<" + type.Name + ">";
            if (depth <= 0) return "<" + type.Name + ">";
            if (value is IDictionary dict)
            {
                var keys = new List<string>();
                foreach (object key in dict.Keys) keys.Add(Format(key, 0));
                keys.Sort(StringComparer.Ordinal);
                return "dict" + dict.Count + "{" + string.Join(",", keys.ToArray()) + "}";
            }
            if (value is IEnumerable seq)
            {
                var sb = new StringBuilder("[");
                int n = 0;
                foreach (object item in seq)
                {
                    if (n++ > 0) sb.Append(", ");
                    sb.Append(Format(item, depth - 1));
                }
                return sb.Append("]#").Append(n).ToString();
            }
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            Array.Sort(fields, (x, y) => string.CompareOrdinal(x.Name, y.Name));
            var obj = new StringBuilder("{");
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0) obj.Append(' ');
                obj.Append(fields[i].Name).Append('=').Append(Format(fields[i].GetValue(value), depth - 1));
            }
            return obj.Append('}').ToString();
        }

        private static string FormatNode(ConfigNode node, int depth)
        {
            var sb = new StringBuilder("node:").Append(node.name).Append('(');
            for (int i = 0; i < node.values.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(node.values[i].name).Append('=').Append(node.values[i].value);
            }
            for (int i = 0; i < node.nodes.Count; i++)
                sb.Append(' ').Append(depth > 0 ? FormatNode(node.nodes[i], depth - 1) : "<node>");
            return sb.Append(')').ToString();
        }

        private static bool IsVisible(FieldInfo f) => f.IsPublic || f.IsAssembly || f.IsFamilyOrAssembly;

        private static bool IsVisible(MethodInfo m) => m != null && (m.IsPublic || m.IsAssembly || m.IsFamilyOrAssembly);

        /// <summary>Every public or internal instance field and property of the recording.</summary>
        private static SortedDictionary<string, string> Dump(Recording rec)
        {
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (rec == null) return map;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (FieldInfo field in typeof(Recording).GetFields(flags))
            {
                if (!IsVisible(field) || field.Name.Contains("<")) continue;
                map["f." + field.Name] = Format(field.GetValue(rec), MaxDepth);
            }
            foreach (PropertyInfo prop in typeof(Recording).GetProperties(flags))
            {
                if (prop.GetIndexParameters().Length > 0 || !IsVisible(prop.GetGetMethod(true))) continue;
                string text;
                try { text = Format(prop.GetValue(rec, null), MaxDepth); }
                catch (TargetInvocationException ex) { text = "<throws " + ex.InnerException?.GetType().Name + ">"; }
                map["p." + prop.Name] = text;
            }
            return map;
        }

        private sealed class Case
        {
            public string Name;
            public RecordingTree Tree;
            public bool IsSceneExit;
            public double CommitUT = double.NaN;
            public RecordingFinalizationCache Cache;
            public IncompleteBallisticSceneExitFinalizer.TryFinalizeDelegate SceneExitOverride;
        }

        private string Run(IEnumerable<Case> cases)
        {
            var sb = new StringBuilder();
            foreach (Case c in cases)
            {
                IncompleteBallisticSceneExitFinalizer.ResetForTesting();
                // The default scene-exit extrapolation needs FlightGlobals; every case runs
                // with an override (declining unless the case supplies a result).
                IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting = c.SceneExitOverride
                    ?? ((Recording recording, Vessel vessel, double commitUT,
                        out IncompleteBallisticFinalizationResult result) =>
                    {
                        result = default(IncompleteBallisticFinalizationResult);
                        return false;
                    });
                Recording active = null;
                if (c.Tree.ActiveRecordingId != null)
                    c.Tree.Recordings.TryGetValue(c.Tree.ActiveRecordingId, out active);
                SortedDictionary<string, string> before = Dump(active);
                logLines.Clear();
                bool returned = ParsekFlight.EnsureActiveRecordingTerminalState(
                    c.Tree, c.IsSceneExit, c.CommitUT, c.Cache);
                var lines = new List<string>(logLines);
                SortedDictionary<string, string> after = Dump(active);

                sb.Append("== ").Append(c.Name).Append('\n');
                sb.Append("return ").Append(returned ? "true" : "false").Append('\n');
                foreach (KeyValuePair<string, string> kv in after)
                {
                    before.TryGetValue(kv.Key, out string was);
                    if (!string.Equals(was, kv.Value, StringComparison.Ordinal))
                        sb.Append("set ").Append(kv.Key).Append(": ").Append(was ?? "<absent>")
                            .Append(" -> ").Append(kv.Value).Append('\n');
                }
                foreach (string line in lines)
                {
                    // Wall-clock timings are the one part of a line that varies run to run.
                    string stable = ElapsedMs.Replace(line, "elapsedMs=<t>");
                    sb.Append("log ").Append(stable.Replace("\r", "\\r").Replace("\n", "\\n")).Append('\n');
                }
            }
            return sb.ToString();
        }

        private static string GoldenPath()
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            return Path.Combine(root, "Source", "Parsek.Tests", "Fixtures", "Characterization", GoldenName);
        }

        private static void AssertGolden(string actual)
        {
            string path = GoldenPath();
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
            string actualPath = Path.Combine(dir, GoldenName);
            File.WriteAllText(actualPath, actual, new UTF8Encoding(false));
            Assert.True(false, "golden mismatch for " + path + "; actual transcript written to " + actualPath);
        }

        // ---------------------------------------------------------------- builders

        private static ConfigNode MakeSnapshot(string sit)
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("sit", sit);
            return node;
        }

        private static RecordingTree Tree(Recording active, string activeId = null)
        {
            var tree = new RecordingTree { Id = "tree-" + active.RecordingId, TreeName = "Tree " + active.RecordingId };
            active.TreeId = tree.Id;
            tree.Recordings[active.RecordingId] = active;
            tree.ActiveRecordingId = activeId ?? active.RecordingId;
            return tree;
        }

        private static Recording Rec(string id, uint pid = 0)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Vessel " + id,
                VesselPersistentId = pid,
            };
        }

        private static Recording WithPoints(Recording rec, params double[] utAlt)
        {
            for (int i = 0; i + 1 < utAlt.Length; i += 2)
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = utAlt[i],
                    altitude = utAlt[i + 1],
                    latitude = 0.1 * i,
                    longitude = -74.5 + 0.01 * i,
                    bodyName = "Kerbin",
                });
            return rec;
        }

        private static OrbitSegment Segment(double startUT, double endUT, string body = "Kerbin",
            double sma = 700000.0, double ecc = 0.05, bool predicted = false)
        {
            return new OrbitSegment
            {
                bodyName = body,
                startUT = startUT,
                endUT = endUT,
                semiMajorAxis = sma,
                eccentricity = ecc,
                inclination = 1.0,
                longitudeOfAscendingNode = 2.0,
                argumentOfPeriapsis = 3.0,
                meanAnomalyAtEpoch = 0.4,
                epoch = startUT,
                isPredicted = predicted,
            };
        }

        private static RecordingFinalizationCache DestroyedCache(string recordingId, uint pid, double terminalUT)
        {
            var cache = new RecordingFinalizationCache
            {
                RecordingId = recordingId,
                VesselPersistentId = pid,
                Owner = FinalizationCacheOwner.ActiveRecorder,
                Status = FinalizationCacheStatus.Fresh,
                CachedAtUT = terminalUT - 5.0,
                RefreshReason = "test-cache",
                LastObservedUT = terminalUT - 5.0,
                LastObservedBodyName = "Kerbin",
                LastSituation = Vessel.Situations.FLYING,
                LastWasInAtmosphere = true,
                TailStartsAtUT = 100.0,
                TerminalUT = terminalUT,
                TerminalState = TerminalState.Destroyed,
                TerminalBodyName = "Kerbin",
            };
            cache.PredictedSegments.Add(Segment(100.0, terminalUT, sma: 700000.0, ecc: 0.1, predicted: true));
            return cache;
        }

        private static IncompleteBallisticSceneExitFinalizer.TryFinalizeDelegate SceneExitResult(
            IncompleteBallisticFinalizationResult value)
        {
            return (Recording recording, Vessel vessel, double commitUT,
                out IncompleteBallisticFinalizationResult result) =>
            {
                result = value;
                return true;
            };
        }

        // ---------------------------------------------------------------- cases

        private IEnumerable<Case> Cases()
        {
            Recording noActive = Rec("e01");
            RecordingTree noActiveTree = Tree(noActive);
            noActiveTree.ActiveRecordingId = null;
            yield return new Case { Name = "no active recording id", Tree = noActiveTree, IsSceneExit = true };

            yield return new Case
            {
                Name = "active id not in tree",
                Tree = Tree(Rec("e02"), activeId: "e02-missing"),
                IsSceneExit = true,
            };

            Recording already = WithPoints(Rec("e03"), 100.0, 5.0);
            already.TerminalStateValue = TerminalState.Landed;
            yield return new Case { Name = "already terminal", Tree = Tree(already), IsSceneExit = true };

            yield return new Case
            {
                Name = "scene-exit result landed with snapshot",
                Tree = Tree(WithPoints(Rec("e04"), 100.0, 5000.0)),
                IsSceneExit = true,
                CommitUT = 200.0,
                SceneExitOverride = SceneExitResult(new IncompleteBallisticFinalizationResult
                {
                    terminalState = TerminalState.Landed,
                    terminalUT = 260.0,
                    vesselSnapshot = MakeSnapshot("LANDED"),
                    terminalPosition = new SurfacePosition
                    {
                        body = "Kerbin",
                        latitude = 1.25,
                        longitude = -74.5,
                        altitude = 12.0,
                        rotation = UnityEngine.Quaternion.identity,
                        situation = SurfaceSituation.Landed,
                    },
                    terrainHeightAtEnd = 8.5,
                }),
            };

            Recording ghostOnly = WithPoints(Rec("e05"), 100.0, 80000.0);
            ghostOnly.OrbitSegments.Add(Segment(100.0, 180.0, body: "Mun", sma: 300000.0, ecc: 0.02));
            yield return new Case
            {
                Name = "scene-exit result orbiting ghost-only orbit from last segment",
                Tree = Tree(ghostOnly),
                IsSceneExit = true,
                CommitUT = 200.0,
                SceneExitOverride = SceneExitResult(new IncompleteBallisticFinalizationResult
                {
                    terminalState = TerminalState.Orbiting,
                    terminalUT = 280.0,
                    ghostVisualSnapshot = MakeSnapshot("ORBITING"),
                }),
            };

            yield return new Case
            {
                Name = "scene-exit result orbiting no orbit data",
                Tree = Tree(WithPoints(Rec("e06"), 100.0, 80000.0)),
                IsSceneExit = true,
                CommitUT = 200.0,
                SceneExitOverride = SceneExitResult(new IncompleteBallisticFinalizationResult
                {
                    terminalState = TerminalState.Orbiting,
                    terminalUT = 280.0,
                    vesselSnapshot = MakeSnapshot("ORBITING"),
                }),
            };

            yield return new Case
            {
                Name = "scene-exit result suborbital with terminal orbit",
                Tree = Tree(WithPoints(Rec("e07"), 100.0, 60000.0)),
                IsSceneExit = true,
                CommitUT = 200.0,
                SceneExitOverride = SceneExitResult(new IncompleteBallisticFinalizationResult
                {
                    terminalState = TerminalState.SubOrbital,
                    terminalUT = 240.0,
                    vesselSnapshot = MakeSnapshot("SUB_ORBITAL"),
                    terminalOrbit = new RecordingFinalizationTerminalOrbit
                    {
                        bodyName = "Kerbin",
                        semiMajorAxis = 650000.0,
                        eccentricity = 0.2,
                        inclination = 4.0,
                        longitudeOfAscendingNode = 5.0,
                        argumentOfPeriapsis = 6.0,
                        meanAnomalyAtEpoch = 0.7,
                        epoch = 240.0,
                    },
                }),
            };

            Recording commitCache = WithPoints(Rec("e08", 42), 100.0, 5000.0);
            yield return new Case
            {
                Name = "commit cache fallback",
                Tree = Tree(commitCache),
                CommitUT = 120.0,
                Cache = DestroyedCache("e08", 42, 180.0),
            };

            Recording sceneCache = WithPoints(Rec("e09", 43), 100.0, 5000.0);
            yield return new Case
            {
                Name = "scene-exit declined then cache fallback",
                Tree = Tree(sceneCache),
                IsSceneExit = true,
                CommitUT = 120.0,
                Cache = DestroyedCache("e09", 43, 180.0),
            };

            yield return new Case
            {
                Name = "scene-exit cache rejected then inference",
                Tree = Tree(WithPoints(Rec("e10", 44), 90.0, 4000.0, 100.0, 5000.0)),
                IsSceneExit = true,
                CommitUT = 120.0,
                Cache = DestroyedCache("other-recording", 44, 180.0),
            };

            yield return new Case
            {
                Name = "scene-exit infers landed",
                Tree = Tree(WithPoints(Rec("e11"), 100.0, 80.0, 120.0, 10.0)),
                IsSceneExit = true,
            };

            yield return new Case
            {
                Name = "scene-exit infers suborbital",
                Tree = Tree(WithPoints(Rec("e12"), 90.0, 4000.0, 100.0, 5000.0)),
                IsSceneExit = true,
            };

            Recording orbitInfer = WithPoints(Rec("e13"), 100.0, 80000.0, 110.0, 81000.0);
            orbitInfer.OrbitSegments.Add(Segment(110.0, 190.0));
            yield return new Case { Name = "scene-exit inference with orbit segment", Tree = Tree(orbitInfer), IsSceneExit = true };

            yield return new Case
            {
                Name = "scene-exit single point suborbital-only skip",
                Tree = Tree(WithPoints(Rec("e14"), 100.0, 5000.0)),
                IsSceneExit = true,
            };

            Recording restored = WithPoints(Rec("e15", 4444), 100.0, 80.0, 120.0, 10.0);
            restored.RestoredFromCommittedTreeThisFrame = true;
            yield return new Case { Name = "scene-exit restored this frame skips inference", Tree = Tree(restored), IsSceneExit = true };

            Recording restoredZero = WithPoints(Rec("e16"), 100.0, 80.0, 120.0, 10.0);
            restoredZero.RestoredFromCommittedTreeThisFrame = true;
            yield return new Case { Name = "scene-exit restored pid zero", Tree = Tree(restoredZero), IsSceneExit = true };

            yield return new Case
            {
                Name = "commit vessel not found",
                Tree = Tree(WithPoints(Rec("e17", 4545), 100.0, 80.0, 120.0, 10.0)),
            };

            yield return new Case
            {
                Name = "commit empty pid zero",
                Tree = Tree(Rec("e18")),
            };
        }

        [Fact]
        public void EnsureActiveRecordingTerminalState_Golden()
        {
            AssertGolden(Run(Cases()));
        }
    }
}
