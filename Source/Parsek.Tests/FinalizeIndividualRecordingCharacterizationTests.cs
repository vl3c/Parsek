using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization golden for <c>ParsekFlight.FinalizeIndividualRecording</c>, written
    /// against the method as it stood before the wave-2 W8b extraction and kept unchanged
    /// through it. Each case builds a fresh recording (and, where the branch needs one, a tree,
    /// a finalization cache, a scene-exit finalizer override or a live vessel stub), runs the
    /// method once, and records the return value, every live-vessel seam call in order, every
    /// public or internal member of the recording that the call changed (doubles as bit
    /// patterns, collections element by element, snapshots node by node) and every log line
    /// the call wrote. The cases with a live vessel stub reach
    /// <c>SegmentPhaseClassifier.TryClassify</c>, whose body reads a Unity native, so they run
    /// under <see cref="UnityEntryFactAttribute"/>.
    /// The goldens live in <c>Fixtures/Characterization/</c>; a mismatch writes the actual
    /// transcript next to the test binary and names it, and PARSEK_CHARACTERIZATION_REGEN=1
    /// rewrites them.
    /// </summary>
    [Collection("Sequential")]
    public class FinalizeIndividualRecordingCharacterizationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> seamCalls = new List<string>();
        private readonly CultureInfo savedCulture;

        public FinalizeIndividualRecordingCharacterizationTests()
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

        private const int MaxDepth = 4;

        private static readonly System.Text.RegularExpressions.Regex ElapsedMs =
            new System.Text.RegularExpressions.Regex(@"elapsedMs=[0-9.,]+");

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
        internal static SortedDictionary<string, string> Dump(Recording rec)
        {
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
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
            public Recording Rec;
            public double CommitUT = 200.0;
            public bool IsSceneExit;
            public RecordingFinalizationCache Cache;
            public RecordingTree Tree;
            public bool DestroyedDuringRecording;
            public Vessel Live;
            public Recording LaterSpawnOwner;
            public TerminalState LiveTerminal = TerminalState.Landed;
            public string CaptureOrbitBody;
            public bool BackupSnapshot;
            public IncompleteBallisticSceneExitFinalizer.TryFinalizeDelegate SceneExitOverride;
        }

        private ParsekFlight.FinalizationLiveVesselAccess Access(Case c) =>
            new ParsekFlight.FinalizationLiveVesselAccess(
                isFound: vessel =>
                {
                    seamCalls.Add("IsFound(" + (ReferenceEquals(vessel, null) ? "null" : "stub") + ")");
                    return !ReferenceEquals(vessel, null);
                },
                determineTerminalState: vessel =>
                {
                    seamCalls.Add("DetermineTerminalState -> " + c.LiveTerminal);
                    return c.LiveTerminal;
                },
                captureTerminalOrbit: (rec, vessel) =>
                {
                    seamCalls.Add("CaptureTerminalOrbit(" + rec.RecordingId + ")");
                    if (c.CaptureOrbitBody != null)
                    {
                        rec.TerminalOrbitBody = c.CaptureOrbitBody;
                        rec.TerminalOrbitSemiMajorAxis = 250000.0;
                        rec.TerminalOrbitEccentricity = 0.01;
                        rec.TerminalOrbitInclination = 3.0;
                        rec.TerminalOrbitEpoch = 150.0;
                    }
                },
                captureTerminalPosition: (rec, vessel) =>
                    seamCalls.Add("CaptureTerminalPosition(" + rec.RecordingId + ")"),
                tryBackupSnapshot: vessel =>
                {
                    seamCalls.Add("TryBackupSnapshot -> " + (c.BackupSnapshot ? "node" : "null"));
                    return c.BackupSnapshot ? MakeSnapshot("LANDED") : null;
                },
                tryRefreshStableTerminalSnapshot: (rec, vessel, isSceneExit, logPrefix) =>
                {
                    seamCalls.Add("TryRefreshStableTerminalSnapshot(" + rec.RecordingId + ", sceneExit="
                        + (isSceneExit ? "true" : "false") + ", " + logPrefix + ")");
                    return false;
                },
                findLaterSpawnOwner: (rec, vessel) =>
                {
                    seamCalls.Add("FindLaterSpawnOwner -> " + (c.LaterSpawnOwner?.RecordingId ?? "null"));
                    return c.LaterSpawnOwner;
                });

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
                SortedDictionary<string, string> before = Dump(c.Rec);
                seamCalls.Clear();
                logLines.Clear();
                bool returned = ParsekFlight.FinalizeIndividualRecording(
                    c.Rec,
                    c.CommitUT,
                    c.IsSceneExit,
                    c.Cache,
                    c.Tree,
                    c.DestroyedDuringRecording,
                    findVesselByPid: pid =>
                    {
                        seamCalls.Add("FindVesselByPid(" + pid.ToString(CultureInfo.InvariantCulture) + ")");
                        return !ReferenceEquals(c.Live, null) && c.Live.persistentId == pid ? c.Live : null;
                    },
                    liveVesselAccess: Access(c));
                var lines = new List<string>(logLines);
                SortedDictionary<string, string> after = Dump(c.Rec);

                sb.Append("== ").Append(c.Name).Append('\n');
                sb.Append("return ").Append(returned ? "true" : "false").Append('\n');
                foreach (string call in seamCalls) sb.Append("seam ").Append(call).Append('\n');
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

        private static string GoldenPath(string name)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            return Path.Combine(root, "Source", "Parsek.Tests", "Fixtures", "Characterization", name);
        }

        private static void AssertGolden(string name, string actual)
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

        // ---------------------------------------------------------------- builders

        private static ConfigNode MakeSnapshot(string sit)
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("sit", sit);
            return node;
        }

        private static Recording Rec(string id, uint pid = 0)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Vessel " + id,
                VesselPersistentId = pid,
                ChildBranchPointId = null,
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
                Owner = FinalizationCacheOwner.BackgroundLoaded,
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

        private static Vessel Stub(uint pid, Vessel.Situations situation)
        {
            var vessel = (Vessel)FormatterServices.GetUninitializedObject(typeof(Vessel));
            vessel.persistentId = pid;
            vessel.situation = situation;
            vessel.id = new Guid("0000000a-0000-0000-0000-00000000000b");
            return vessel;
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

        private static RecordingTree ChainTree(out Recording head, string prefix)
        {
            var tree = new RecordingTree { Id = prefix + "-tree", TreeName = "Chain" };
            head = WithPoints(Rec(prefix + "-head", 7001), 100.0, 300.0, 150.0, 900.0);
            head.TreeId = tree.Id;
            head.ChainId = prefix + "-chain";
            head.ChainIndex = 0;
            head.ChainBranch = 0;
            Recording tail = WithPoints(Rec(prefix + "-tail", 7001), 150.0, 900.0, 220.0, 20.0);
            tail.TreeId = tree.Id;
            tail.ChainId = prefix + "-chain";
            tail.ChainIndex = 1;
            tail.ChainBranch = 0;
            tree.Recordings[head.RecordingId] = head;
            tree.Recordings[tail.RecordingId] = tail;
            return tree;
        }

        private static RecordingTree BranchTree(Recording parent, uint childPid, string prefix)
        {
            var tree = new RecordingTree { Id = prefix + "-tree", TreeName = "Branch" };
            parent.TreeId = tree.Id;
            parent.ChildBranchPointId = prefix + "-bp";
            Recording child = WithPoints(Rec(prefix + "-child", childPid), 130.0, 400.0);
            child.TreeId = tree.Id;
            child.ParentBranchPointId = prefix + "-bp";
            tree.Recordings[parent.RecordingId] = parent;
            tree.Recordings[child.RecordingId] = child;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = prefix + "-bp",
                Type = BranchPointType.JointBreak,
                UT = 130.0,
                ParentRecordingIds = new List<string> { parent.RecordingId },
                ChildRecordingIds = new List<string> { child.RecordingId },
            });
            return tree;
        }

        // ---------------------------------------------------------------- cases

        private IEnumerable<Case> HeadlessCases()
        {
            yield return new Case { Name = "empty-leaf commit", Rec = Rec("c01") };
            yield return new Case { Name = "empty-leaf scene-exit", Rec = Rec("c02"), IsSceneExit = true };
            Recording sidecar = Rec("c03");
            sidecar.SidecarLoadFailed = true;
            sidecar.SidecarLoadFailureReason = "test-hydration";
            yield return new Case { Name = "empty-leaf sidecar-failed", Rec = sidecar, IsSceneExit = true };
            yield return new Case
            {
                Name = "scene-exit low-point infers landed",
                Rec = WithPoints(Rec("c04"), 100.0, 80.0, 120.0, 10.0),
                IsSceneExit = true,
            };
            yield return new Case
            {
                Name = "scene-exit two high points infers suborbital",
                Rec = WithPoints(Rec("c05"), 90.0, 4000.0, 100.0, 5000.0),
                IsSceneExit = true,
            };
            yield return new Case
            {
                Name = "scene-exit single high point suborbital-fallback skip",
                Rec = WithPoints(Rec("c06"), 100.0, 5000.0),
                IsSceneExit = true,
            };
            Recording orbital = WithPoints(Rec("c07", 4242), 100.0, 80000.0, 110.0, 81000.0);
            orbital.OrbitSegments.Add(Segment(110.0, 190.0));
            yield return new Case { Name = "scene-exit pid missing with orbit", Rec = orbital, IsSceneExit = true };
            Recording destroyed = WithPoints(Rec("c08", 4343), 100.0, 80000.0, 110.0, 81000.0);
            destroyed.OrbitSegments.Add(Segment(110.0, 190.0, body: "Mun", sma: 300000.0, ecc: 0.02));
            yield return new Case { Name = "commit pid missing marks destroyed", Rec = destroyed };
            Recording landed = WithPoints(Rec("c09"), 100.0, 5.0);
            landed.TerminalStateValue = TerminalState.Landed;
            yield return new Case { Name = "existing landed preserved", Rec = landed, IsSceneExit = true };
            Recording orbitingNoBody = WithPoints(Rec("c10"), 100.0, 80000.0);
            orbitingNoBody.OrbitSegments.Add(Segment(100.0, 180.0, body: "Mun", sma: 300000.0, ecc: 0.02));
            orbitingNoBody.TerminalStateValue = TerminalState.Orbiting;
            yield return new Case { Name = "existing orbiting backfills body", Rec = orbitingNoBody };
            Recording orbitingBare = WithPoints(Rec("c11"), 100.0, 80000.0);
            orbitingBare.TerminalStateValue = TerminalState.Orbiting;
            yield return new Case { Name = "existing orbiting refresh declined", Rec = orbitingBare };
            yield return new Case
            {
                Name = "destroyed-during-recording scene-exit",
                Rec = WithPoints(Rec("c12"), 100.0, 3000.0, 120.0, 2500.0),
                IsSceneExit = true,
                DestroyedDuringRecording = true,
            };
            Recording restored = WithPoints(Rec("c13", 4444), 100.0, 80.0, 120.0, 10.0);
            restored.RestoredFromCommittedTreeThisFrame = true;
            yield return new Case { Name = "restored-this-frame scene-exit skips inference", Rec = restored, IsSceneExit = true };
            Recording restoredTerminal = WithPoints(Rec("c14", 4545), 100.0, 80.0, 120.0, 10.0);
            restoredTerminal.RestoredFromCommittedTreeThisFrame = true;
            restoredTerminal.TerminalStateValue = TerminalState.Splashed;
            yield return new Case { Name = "restored-this-frame terminal preserved", Rec = restoredTerminal, IsSceneExit = true };

            Recording cached = WithPoints(Rec("c15", 42), 100.0, 5000.0);
            cached.ExplicitEndUT = double.NaN;
            yield return new Case
            {
                Name = "cache fallback vessel missing",
                Rec = cached,
                CommitUT = 120.0,
                IsSceneExit = true,
                Cache = DestroyedCache("c15", 42, 180.0),
            };
            Recording stale = WithPoints(Rec("c16", 43), 100.0, 5000.0);
            RecordingFinalizationCache staleCache = DestroyedCache("c16", 43, 180.0);
            staleCache.Status = FinalizationCacheStatus.Stale;
            staleCache.CachedAtUT = 105.0;
            yield return new Case
            {
                Name = "stale cache fallback",
                Rec = stale,
                CommitUT = 120.0,
                IsSceneExit = true,
                Cache = staleCache,
            };
            yield return new Case
            {
                Name = "cache rejected falls back to inference",
                Rec = WithPoints(Rec("c17", 44), 90.0, 4000.0, 100.0, 5000.0),
                CommitUT = 120.0,
                IsSceneExit = true,
                Cache = DestroyedCache("other-recording", 44, 180.0),
            };
            Recording repair = WithPoints(Rec("c18", 45), 90.0, 4000.0, 100.0, 5000.0);
            repair.TerminalStateValue = TerminalState.SubOrbital;
            yield return new Case
            {
                Name = "destroyed cache repairs suborbital",
                Rec = repair,
                CommitUT = 120.0,
                Cache = DestroyedCache("c18", 45, 180.0),
            };
            Recording repairRejected = WithPoints(Rec("c19", 46), 90.0, 4000.0, 100.0, 5000.0);
            repairRejected.TerminalStateValue = TerminalState.Destroyed;
            yield return new Case
            {
                Name = "destroyed cache repair rejected already destroyed",
                Rec = repairRejected,
                CommitUT = 120.0,
                Cache = DestroyedCache("c19", 46, 180.0),
            };

            yield return new Case
            {
                Name = "scene-exit override landed supplies snapshot",
                Rec = WithPoints(Rec("c20"), 100.0, 5000.0),
                IsSceneExit = true,
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
            yield return new Case
            {
                Name = "scene-exit override ghost-only orbiting",
                Rec = WithPoints(Rec("c21"), 100.0, 5000.0),
                IsSceneExit = true,
                SceneExitOverride = SceneExitResult(new IncompleteBallisticFinalizationResult
                {
                    terminalState = TerminalState.Orbiting,
                    terminalUT = 280.0,
                    ghostVisualSnapshot = MakeSnapshot("ORBITING"),
                }),
            };

            Recording head;
            RecordingTree chain = ChainTree(out head, "c23");
            yield return new Case { Name = "non-final chain segment", Rec = head, Tree = chain };
            Recording activeHead;
            RecordingTree activeChain = ChainTree(out activeHead, "c24");
            activeChain.ActiveRecordingId = activeHead.RecordingId;
            yield return new Case { Name = "active chain segment stays leaf", Rec = activeHead, Tree = activeChain };

            Recording samePidParent = WithPoints(Rec("c25", 8001), 100.0, 300.0, 130.0, 400.0);
            yield return new Case
            {
                Name = "branch with same-pid child is not leaf",
                Rec = samePidParent,
                Tree = BranchTree(samePidParent, 8001, "c25"),
            };
            Recording sideOffParent = WithPoints(Rec("c26", 8002), 100.0, 300.0, 130.0, 4.0);
            yield return new Case
            {
                Name = "branch with different-pid child is effective leaf",
                Rec = sideOffParent,
                Tree = BranchTree(sideOffParent, 9002, "c26"),
                IsSceneExit = true,
            };
            Recording noTree = WithPoints(Rec("c27"), 100.0, 300.0);
            noTree.ChildBranchPointId = "c27-bp";
            yield return new Case { Name = "branch without tree is not leaf", Rec = noTree };

            Recording explicitUt = WithPoints(Rec("c28"), 100.0, 20.0, 140.0, 5.0);
            explicitUt.ExplicitStartUT = 95.0;
            explicitUt.ExplicitEndUT = 150.0;
            explicitUt.MaxDistanceFromLaunch = 1234.0;
            yield return new Case { Name = "explicit uts and maxdist kept", Rec = explicitUt };
            Recording segOnly = Rec("c29");
            segOnly.OrbitSegments.Add(Segment(110.0, 170.0));
            yield return new Case { Name = "orbit-only start ut", Rec = segOnly, CommitUT = 175.0 };

            Recording sections = WithPoints(Rec("c30"), 100.0, 20.0, 140.0, 5.0);
            sections.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceMobile,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 140.0,
                frames = new List<TrajectoryPoint>(sections.Points),
                checkpoints = new List<OrbitSegment>(),
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
            });
            yield return new Case { Name = "absolute sections scene-exit", Rec = sections, IsSceneExit = true };
            Recording sectionOnly = Rec("c31");
            sectionOnly.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 140.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, altitude = 90000.0, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 140.0, altitude = 95000.0, bodyName = "Kerbin" },
                },
                checkpoints = new List<OrbitSegment>(),
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
            });
            yield return new Case { Name = "section-only payload commit", Rec = sectionOnly };
        }

        private IEnumerable<Case> LiveVesselCases()
        {
            yield return new Case
            {
                Name = "live landed commit backs up snapshot",
                Rec = WithPoints(Rec("v01", 5001), 100.0, 80.0, 120.0, 5.0),
                Live = Stub(5001, Vessel.Situations.LANDED),
                LiveTerminal = TerminalState.Landed,
                BackupSnapshot = true,
            };
            yield return new Case
            {
                Name = "live landed commit backup null",
                Rec = WithPoints(Rec("v02", 5002), 100.0, 80.0, 120.0, 5.0),
                Live = Stub(5002, Vessel.Situations.LANDED),
                LiveTerminal = TerminalState.Landed,
            };
            Recording ghostKept = WithPoints(Rec("v03", 5003), 100.0, 80.0, 120.0, 5.0);
            ghostKept.GhostVisualSnapshot = MakeSnapshot("FLYING");
            yield return new Case
            {
                Name = "live landed commit keeps ghost snapshot",
                Rec = ghostKept,
                Live = Stub(5003, Vessel.Situations.LANDED),
                LiveTerminal = TerminalState.Landed,
                BackupSnapshot = true,
            };
            yield return new Case
            {
                Name = "live orbiting scene-exit refreshes body",
                Rec = WithPoints(Rec("v04", 5004), 100.0, 80000.0, 120.0, 85000.0),
                Live = Stub(5004, Vessel.Situations.ORBITING),
                LiveTerminal = TerminalState.Orbiting,
                CaptureOrbitBody = "Mun",
                IsSceneExit = true,
            };
            yield return new Case
            {
                Name = "live orbiting commit no body captured",
                Rec = WithPoints(Rec("v05", 5005), 100.0, 80000.0, 120.0, 85000.0),
                Live = Stub(5005, Vessel.Situations.ORBITING),
                LiveTerminal = TerminalState.Orbiting,
            };
            Recording owner = Rec("v06-owner", 5006);
            owner.ExplicitEndUT = 900.0;
            yield return new Case
            {
                Name = "live vessel owned by later spawn",
                Rec = WithPoints(Rec("v06", 5006), 100.0, 80.0, 120.0, 5.0),
                Live = Stub(5006, Vessel.Situations.LANDED),
                LaterSpawnOwner = owner,
            };
            Recording activeRec = WithPoints(Rec("v07", 5007), 100.0, 80.0, 120.0, 5.0);
            var activeTree = new RecordingTree { Id = "v07-tree", TreeName = "Active", ActiveRecordingId = "v07" };
            activeRec.TreeId = activeTree.Id;
            activeTree.Recordings[activeRec.RecordingId] = activeRec;
            yield return new Case
            {
                Name = "tree-active recording skips spawn-owner check",
                Rec = activeRec,
                Tree = activeTree,
                Live = Stub(5007, Vessel.Situations.LANDED),
                LaterSpawnOwner = owner,
            };
            Recording existingLanded = WithPoints(Rec("v08", 5008), 100.0, 80.0, 120.0, 5.0);
            existingLanded.TerminalStateValue = TerminalState.Landed;
            yield return new Case
            {
                Name = "live existing landed refreshes stable snapshot",
                Rec = existingLanded,
                Live = Stub(5008, Vessel.Situations.LANDED),
                IsSceneExit = true,
            };
            Recording existingDestroyed = WithPoints(Rec("v09", 5009), 100.0, 80.0, 120.0, 5.0);
            existingDestroyed.TerminalStateValue = TerminalState.Destroyed;
            yield return new Case
            {
                Name = "live existing destroyed no refresh",
                Rec = existingDestroyed,
                Live = Stub(5009, Vessel.Situations.LANDED),
            };
            yield return new Case
            {
                Name = "live scene-exit override supplies snapshot",
                Rec = WithPoints(Rec("v10", 5010), 100.0, 5000.0),
                Live = Stub(5010, Vessel.Situations.FLYING),
                IsSceneExit = true,
                SceneExitOverride = SceneExitResult(new IncompleteBallisticFinalizationResult
                {
                    terminalState = TerminalState.Landed,
                    terminalUT = 260.0,
                    vesselSnapshot = MakeSnapshot("LANDED"),
                }),
            };
            Recording liveCache = WithPoints(Rec("v11", 5011), 90.0, 4000.0, 100.0, 5000.0);
            liveCache.TerminalStateValue = TerminalState.SubOrbital;
            yield return new Case
            {
                Name = "live destroyed cache repair not stale",
                Rec = liveCache,
                CommitUT = 120.0,
                Live = Stub(5011, Vessel.Situations.SUB_ORBITAL),
                Cache = DestroyedCache("v11", 5011, 180.0),
            };
        }

        [Fact]
        public void FinalizeIndividualRecording_Headless_Golden()
        {
            AssertGolden("finalize-individual-recording.headless.golden.txt", Run(HeadlessCases()));
        }

        [UnityEntryFact]
        public void FinalizeIndividualRecording_LiveVessel_Golden()
        {
            AssertGolden("finalize-individual-recording.live.golden.txt", Run(LiveVesselCases()));
        }
    }
}
