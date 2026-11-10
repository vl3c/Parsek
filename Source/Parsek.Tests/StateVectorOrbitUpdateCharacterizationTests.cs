using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization of the per-frame state-vector orbit update of an index-keyed ghost map
    /// ProtoVessel (<c>GhostMapPresence.UpdateGhostOrbitFromStateVectors</c>). Each case seeds the
    /// ghost (an uninitialized <see cref="Vessel"/>, optionally marked alive and given an
    /// uninitialized alive <see cref="OrbitDriver"/>), the committed trees and the Re-Fly marker,
    /// makes one call, and records the return value, every store the method touches and every log
    /// line written, compared byte for byte against a golden file captured from the method before
    /// it was split.
    ///
    /// Reach, measured: the method runs headless through the lookup, rebind, the no-orbit-driver
    /// and body-not-found misses, the world-position resolution and its skip, the active Re-Fly
    /// suppression, the SOI-change line, the gap-glide inertial seed and its declines, the stale
    /// segment-drive clear, the seed velocity and the body-fixed reseed. It stops in
    /// <c>OrbitDriver.updateFromParameters</c>, whose first call (<c>Planetarium.GetUniversalTime</c>)
    /// throws with no Planetarium; those cases record the exception, the innermost Parsek frame and
    /// the top frame, so the capture pins where the headless reach ends. The seam CoMD refresh, the
    /// update line and the last-known stash are covered by direct unit tests of the extracted
    /// units.
    ///
    /// Both facts are <see cref="UnityEntryFactAttribute"/>: the corpus reaches Unity natives.
    ///
    /// Regenerate (only when a behavior change is intended, never in a refactor):
    /// PARSEK_CHARACTERIZATION_REGEN=1 with the filter on this class.
    /// </summary>
    [Collection("Sequential")]
    public class StateVectorOrbitUpdateCharacterizationTests : IDisposable
    {
        private const string GoldenName = "state-vector-orbit-update.golden.txt";
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
        private static readonly Regex GuidPattern = new Regex("[0-9a-f]{32}", RegexOptions.Compiled);
        private static readonly FieldInfo CachedPtr = typeof(UnityEngine.Object).GetField(
            "m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);

        private const string TreeId = "sv-tree";
        private const string BranchPointId = "sv-bp";
        private const uint AnchorPid = 7770u;

        private readonly List<string> logLines = new List<string>();
        private readonly CelestialBody kerbin;
        private readonly CelestialBody deadBody;
        private double clock = 1000.0;
        private readonly GameScenes priorScene;

        public StateVectorOrbitUpdateCharacterizationTests()
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            // The settings type logs one line from its static constructor on first touch; run it
            // before the sink is installed so the capture does not depend on test order.
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
                typeof(ParsekSettings).TypeHandle);
            priorScene = HighLogic.LoadedScene;
            HighLogic.LoadedScene = GameScenes.TRACKSTATION;
            // A Unity null test reads the native pointer: the body the lookup finds must look
            // alive, and an unresolvable name returns a dead body (the method's body == null arm)
            // rather than falling through to FlightGlobals, whose initializer fails headless.
            kerbin = Alive(TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12));
            deadBody = TestBodyRegistry.CreateBody("Nowhere");
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => clock;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            HighLogic.LoadedScene = priorScene;
            GhostMapPresence.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            OrbitSeedResolver.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        internal sealed class Case
        {
            internal string Name;
            internal Action Setup = () => { };
            internal int Index;
            internal Func<IPlaybackTrajectory> Traj = () => null;
            internal TrajectoryPoint Point;
            internal double UT = 500.0;
            internal bool AllowCheckpoint;
            internal string Reason;
            internal double Shift;
        }

        // ---------------------------------------------------------------- stores

        private static object Store(string name)
        {
            FieldInfo f = typeof(GhostMapPresence).GetField(
                name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(f);
            return f.GetValue(null);
        }

        internal static Dictionary<int, Vessel> GhostsByIndex =>
            (Dictionary<int, Vessel>)Store("vesselsByRecordingIndex");

        private static readonly string[] DumpedStores =
        {
            "vesselPidToRecordingId",
            "activeReFlyDeferredStateVectorGhostSessions",
            "ghostLastAppliedOrbitBody",
            "ghostLastAppliedOrbitElements",
            "ghostOrbitBounds",
            "ghostBodyFrameOrbitBounds",
            "ghostOrbitLoopShiftedPids",
            "ghostOrbitEpochShift",
            "ghostIconDrivePropagation",
            "lastKnownByRecordingIndex",
        };

        // ---------------------------------------------------------------- builders

        internal static T Alive<T>(T obj) where T : UnityEngine.Object
        {
            CachedPtr.SetValue(obj, new IntPtr(1));
            return obj;
        }

        /// <summary>An index-keyed ghost: an uninitialized vessel with its pid, alive (so a Unity
        /// null test sees it) unless <paramref name="alive"/> is false, with an uninitialized alive
        /// OrbitDriver holding a fresh Orbit when <paramref name="withDriver"/>.</summary>
        internal static Vessel SvGhost(int idx, uint pid, bool alive = true, bool withDriver = true)
        {
            var vessel = (Vessel)FormatterServices.GetUninitializedObject(typeof(Vessel));
            vessel.persistentId = pid;
            if (alive)
                Alive(vessel);
            if (withDriver)
            {
                var driver = Alive((OrbitDriver)FormatterServices.GetUninitializedObject(typeof(OrbitDriver)));
                driver.orbit = new Orbit();
                vessel.orbitDriver = driver;
            }
            GhostsByIndex[idx] = vessel;
            GhostMapPresence.ghostMapVesselPids.Add(pid);
            return vessel;
        }

        internal static TrajectoryPoint Pt(double ut, string body = "Kerbin", double lat = 1.0,
            double lon = 2.0, double alt = 90000.0, float speed = 2200f)
        {
            return new TrajectoryPoint
            {
                ut = ut, bodyName = body, latitude = lat, longitude = lon, altitude = alt,
                velocity = new Vector3(0f, speed, 0f)
            };
        }

        internal static TrackSection Section(ReferenceFrame frame, double start, double end,
            string anchorRecordingId = null, List<TrajectoryPoint> bodyFixed = null)
        {
            return new TrackSection
            {
                startUT = start, endUT = end,
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = frame,
                source = frame == ReferenceFrame.OrbitalCheckpoint
                    ? TrackSectionSource.Checkpoint : TrackSectionSource.Active,
                anchorRecordingId = anchorRecordingId,
                frames = new List<TrajectoryPoint>(),
                bodyFixedFrames = bodyFixed,
                checkpoints = new List<OrbitSegment>(),
            };
        }

        internal static Recording Rec(string id, params TrackSection[] sections)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Char " + id,
                Points = new List<TrajectoryPoint> { Pt(100), Pt(900, alt: 95000.0) },
                TrackSections = new List<TrackSection>(sections),
            };
        }

        /// <summary>A parent-anchored recording whose Relative section carries body-fixed frames
        /// (two covering 300-700, or one), anchored to the booster recording.</summary>
        internal static Recording BodyFixedRec(string id, bool twoFrames)
        {
            var frames = twoFrames
                ? new List<TrajectoryPoint> { Pt(300, alt: 80000.0), Pt(700, alt: 82000.0) }
                : new List<TrajectoryPoint> { Pt(300, alt: 80000.0) };
            Recording rec = Rec(id, Section(ReferenceFrame.Relative, 100, 900, "sv-booster", frames));
            rec.ParentAnchorRecordingId = "sv-booster";
            return rec;
        }

        /// <summary>One committed tree: the debris recording is the parent (Undock) of the booster,
        /// whose pid the debris section is anchored to.</summary>
        internal static Recording SeedAnchorTree()
        {
            var tree = new RecordingTree { Id = TreeId, TreeName = "SV", RootRecordingId = "sv-debris" };
            Recording debris = BodyFixedRec("sv-debris", twoFrames: true);
            debris.TreeId = TreeId;
            debris.ChildBranchPointId = BranchPointId;
            var booster = new Recording
            {
                RecordingId = "sv-booster",
                VesselName = "Char sv-booster",
                TreeId = TreeId,
                ParentBranchPointId = BranchPointId,
                VesselPersistentId = AnchorPid,
            };
            tree.Recordings[debris.RecordingId] = debris;
            tree.Recordings[booster.RecordingId] = booster;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = BranchPointId,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { debris.RecordingId },
                ChildRecordingIds = new List<string> { booster.RecordingId },
            });
            RecordingStore.AddCommittedTreeForTesting(tree);
            return debris;
        }

        internal static void ArmInPlaceMarker()
        {
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess-sv",
                    TreeId = TreeId,
                    ActiveReFlyRecordingId = "sv-booster",
                    OriginChildRecordingId = "sv-booster",
                    InvokedUT = 150.0,
                    InPlaceContinuation = true,
                }
            });
        }

        // ---------------------------------------------------------------- cases

        private IEnumerable<Case> Cases()
        {
            yield return new Case { Name = "no-ghost-at-index", Index = 4, Point = Pt(500) };

            yield return new Case
            {
                Name = "dead-vessel-no-driver-skips-rebind",
                Setup = () => SvGhost(0, 9000, alive: false, withDriver: false),
                Traj = () => Rec("d0"),
                Point = Pt(500),
            };
            yield return new Case
            {
                Name = "no-orbit-driver",
                Setup = () => SvGhost(0, 9001, withDriver: false),
                Traj = () => Rec("n0"),
                Point = Pt(500),
            };
            yield return new Case
            {
                Name = "no-orbit-driver-null-traj",
                Setup = () => SvGhost(2, 9002, withDriver: false),
                Index = 2,
                Point = Pt(500),
            };
            yield return new Case
            {
                Name = "rebind-same-id-quiet",
                Setup = () =>
                {
                    SvGhost(0, 9003, withDriver: false);
                    GhostMapPresence.RebindGhostRecordingId(9003, "q0");
                },
                Traj = () => Rec("q0"),
                Point = Pt(500),
            };
            yield return new Case
            {
                Name = "body-not-found",
                Setup = () => SvGhost(0, 9004),
                Traj = () => Rec("b0"),
                Point = Pt(500, body: "Nowhere"),
            };
            yield return new Case
            {
                Name = "skip-relative-anchor-not-found",
                Setup = () => SvGhost(0, 9005),
                Traj = () => Rec("r0", Section(ReferenceFrame.Relative, 100, 900, "missing-anchor")),
                Point = Pt(500, lat: 12.0, lon: -3.0, alt: 4.0),
                Reason = "tracking-station-state-vector",
            };
            yield return new Case
            {
                Name = "skip-orbital-checkpoint-refused",
                Setup = () => SvGhost(0, 9006),
                Traj = () => Rec("c0", Section(ReferenceFrame.OrbitalCheckpoint, 100, 900)),
                Point = Pt(500),
            };
            yield return new Case
            {
                Name = "skip-body-fixed-primary-unavailable",
                Setup = () => SvGhost(0, 9007),
                Traj = () => BodyFixedRec("f0", twoFrames: false),
                Point = Pt(500),
            };
            Recording suppressedDebris = null;
            yield return new Case
            {
                Name = "suppressed-active-refly-body-fixed-primary",
                Setup = () =>
                {
                    SvGhost(3, 9008);
                    suppressedDebris = SeedAnchorTree();
                    ArmInPlaceMarker();
                },
                Index = 3,
                Traj = () => suppressedDebris,
                Point = Pt(500),
                Reason = "orbit-raise-gap-points",
                Shift = 2000.0,
            };
            Recording plainDebris = null;
            yield return new Case
            {
                Name = "body-fixed-primary-no-marker-reaches-orbit-driver-update",
                Setup = () =>
                {
                    SvGhost(0, 9009);
                    plainDebris = SeedAnchorTree();
                },
                Traj = () => plainDebris,
                Point = Pt(500),
            };
            yield return new Case
            {
                Name = "absolute-gap-glide-no-rotation-reaches-orbit-driver-update",
                Setup = () => SvGhost(0, 9010),
                Traj = () => Rec("a0", Section(ReferenceFrame.Absolute, 100, 900)),
                Point = Pt(500),
                Reason = "orbit-raise-gap-points",
            };
            yield return new Case
            {
                Name = "orbital-checkpoint-allowed-reaches-orbit-driver-update",
                Setup = () => SvGhost(0, 9011),
                Traj = () => Rec("c1", Section(ReferenceFrame.OrbitalCheckpoint, 100, 900)),
                Point = Pt(500),
                AllowCheckpoint = true,
            };
            yield return new Case
            {
                Name = "no-section-null-traj-reaches-orbit-driver-update",
                Setup = () => SvGhost(0, 9012),
                Point = Pt(500),
            };
            yield return new Case
            {
                Name = "same-body-as-last-applied-no-soi-line",
                Setup = () =>
                {
                    SvGhost(0, 9013);
                    GhostMapPresence.ghostLastAppliedOrbitBody[9013u] = "Kerbin";
                },
                Traj = () => Rec("s0", Section(ReferenceFrame.Absolute, 100, 900)),
                Point = Pt(500),
                Reason = "tracking-station-state-vector",
            };
            yield return new Case
            {
                Name = "stale-segment-drive-cleared-before-reseed",
                Setup = () =>
                {
                    SvGhost(0, 9014);
                    GhostMapPresence.ghostOrbitBounds[9014u] = (400.0, 600.0);
                    GhostMapPresence.ghostBodyFrameOrbitBounds[9014u] = (400.0, 600.0);
                    GhostMapPresence.ghostOrbitLoopShiftedPids.Add(9014u);
                    GhostMapPresence.ghostOrbitEpochShift[9014u] = 2000.0;
                    GhostMapPresence.ghostIconDrivePropagation[9014u] =
                        new GhostMapPresence.IconDrivePropagation { PropagateUT = 2500.0, Frame = 7 };
                    GhostMapPresence.ghostOrbitBounds[1u] = (1.0, 2.0);
                },
                Traj = () => Rec("t0", Section(ReferenceFrame.Absolute, 100, 900)),
                Point = Pt(500),
                Shift = 2000.0,
            };
            yield return new Case
            {
                Name = "icon-drive-record-only-dropped-quietly",
                Setup = () =>
                {
                    SvGhost(0, 9015);
                    GhostMapPresence.ghostIconDrivePropagation[9015u] =
                        new GhostMapPresence.IconDrivePropagation { PropagateUT = 2500.0, Frame = 7 };
                },
                Traj = () => Rec("t1", Section(ReferenceFrame.Absolute, 100, 900)),
                Point = Pt(500),
            };
            yield return new Case
            {
                Name = "gap-glide-point-not-a-sample",
                Setup = () =>
                {
                    SvGhost(0, 9016);
                    OrbitSeedResolver.RotationPeriodForTesting = b => 21549.425;
                },
                Traj = () => Rec("g0", Section(ReferenceFrame.Absolute, 100, 900)),
                Point = Pt(500),
                Reason = "orbit-raise-gap-points",
                Shift = 1000.0,
            };
            yield return new Case
            {
                Name = "gap-glide-pair-found",
                Setup = () =>
                {
                    SvGhost(0, 9017);
                    OrbitSeedResolver.RotationPeriodForTesting = b => 21549.425;
                    OrbitSeedResolver.InitialRotationForTesting = b => 90.0;
                },
                Traj = () => Rec("g1", Section(ReferenceFrame.Absolute, 100, 900)),
                Point = Pt(900, alt: 95000.0),
                UT = 900.0,
                Reason = "orbit-raise-gap-points",
            };
            yield return new Case
            {
                Name = "gap-glide-close-pair",
                Setup = () =>
                {
                    SvGhost(0, 9019);
                    OrbitSeedResolver.RotationPeriodForTesting = b => 21549.425;
                    OrbitSeedResolver.InitialRotationForTesting = b => 90.0;
                },
                Traj = () =>
                {
                    Recording rec = Rec("g2", Section(ReferenceFrame.Absolute, 100, 900));
                    rec.Points = new List<TrajectoryPoint>
                    {
                        Pt(880, alt: 94000.0, speed: 2210f),
                        Pt(900, lon: 3.0, alt: 95000.0),
                    };
                    return rec;
                },
                Point = Pt(900, lon: 3.0, alt: 95000.0),
                UT = 900.0,
                Reason = "orbit-raise-gap-points",
                Shift = 500.0,
            };
            yield return new Case
            {
                Name = "gap-reason-on-relative-branch-not-glided",
                Setup = () => SvGhost(0, 9018),
                Traj = () => BodyFixedRec("f1", twoFrames: true),
                Point = Pt(500),
                Reason = "orbit-raise-gap-points",
            };
        }

        // ---------------------------------------------------------------- runner

        private string RunCase(Case c)
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetRateLimitsForTesting();
            OrbitSeedResolver.ResetForTesting();
            GhostMapPresence.gapGlideInertialSeedCount = 0;
            GhostMapPresence.gapGlideBodyFixedFallbackCount = 0;
            GhostMapPresence.seamComdRefreshCount = 0;
            GhostMapPresence.seamComdSteadyCount = 0;
            GhostMapPresence.seamComdMaxOffOrbitDegPostRefresh = 0.0;
            GhostMapPresence.FindBodyByNameForTesting = name =>
                string.Equals(name, "Kerbin", StringComparison.Ordinal) ? kerbin : deadBody;
            clock = 1000.0;
            c.Setup();
            IPlaybackTrajectory traj = c.Traj();
            var sb = new StringBuilder();
            sb.Append("== ").Append(c.Name).Append('\n');
            sb.Append("seed ").Append(DumpStores()).Append('\n');
            logLines.Clear();
            string outcome;
            try
            {
                bool result = GhostMapPresence.UpdateGhostOrbitFromStateVectors(
                    c.Index, traj, c.Point, c.UT, c.AllowCheckpoint, c.Reason, c.Shift);
                outcome = "returned " + (result ? "true" : "false");
            }
            catch (Exception e)
            {
                outcome = "threw " + Kind(e) + " in " + InnermostParsekFrame(e)
                    + " top " + TopFrame(e);
            }
            foreach (string line in logLines)
                sb.Append("log ").Append(Scrub(line)).Append('\n');
            sb.Append(outcome).Append('\n');
            sb.Append("after ").Append(DumpStores()).Append('\n');
            return sb.ToString();
        }

        // A Unity native throws MissingMethodException under mono and SecurityException on the
        // Windows host; both are the same headless boundary.
        private static string Kind(Exception e)
        {
            if (e is MissingMethodException || e is System.Security.SecurityException)
                return "unity-native";
            return e.GetType().Name;
        }

        private static string InnermostParsekFrame(Exception e)
        {
            StackFrame[] frames = new StackTrace(e, false).GetFrames() ?? new StackFrame[0];
            foreach (StackFrame f in frames)
            {
                MethodBase m = f.GetMethod();
                if (m?.DeclaringType != null && m.DeclaringType.Namespace == "Parsek")
                    return m.DeclaringType.Name + "." + m.Name;
            }
            return "(no Parsek frame)";
        }

        private static string TopFrame(Exception e)
        {
            StackFrame[] frames = new StackTrace(e, false).GetFrames() ?? new StackFrame[0];
            MethodBase m = frames.Length > 0 ? frames[0].GetMethod() : null;
            return m?.DeclaringType == null ? "(none)" : m.DeclaringType.FullName + "." + m.Name;
        }

        private static string Scrub(string line)
        {
            string flat = line.Replace("\r", "\\r").Replace("\n", "\\n");
            return GuidPattern.Replace(flat, "<guid>");
        }

        private static string DumpStores()
        {
            var sb = new StringBuilder();
            sb.Append("ghosts=[");
            foreach (var kv in GhostsByIndex.OrderBy(k => k.Key))
                sb.Append(kv.Key.ToString(Ic)).Append(':')
                    .Append(kv.Value.persistentId.ToString(Ic)).Append(' ');
            sb.Append(']');
            foreach (string name in DumpedStores)
            {
                sb.Append(' ').Append(name).Append("=[");
                object store = Store(name);
                var entries = new List<string>();
                if (store is IDictionary dict)
                {
                    foreach (DictionaryEntry e in dict)
                        entries.Add(Format(e.Key) + ":" + Format(e.Value));
                }
                else if (store is IEnumerable seq)
                {
                    foreach (object o in seq)
                        entries.Add(Format(o));
                }
                entries.Sort(StringComparer.Ordinal);
                sb.Append(string.Join(" ", entries)).Append(']');
            }
            sb.Append(" counters=")
                .Append(GhostMapPresence.lifecycleUpdatedThisTick.ToString(Ic)).Append('/')
                .Append(GhostMapPresence.gapGlideInertialSeedCount.ToString(Ic)).Append('/')
                .Append(GhostMapPresence.gapGlideBodyFixedFallbackCount.ToString(Ic)).Append('/')
                .Append(GhostMapPresence.seamComdRefreshCount.ToString(Ic)).Append('/')
                .Append(GhostMapPresence.seamComdSteadyCount.ToString(Ic)).Append('/')
                .Append(R(GhostMapPresence.seamComdMaxOffOrbitDegPostRefresh));
            return sb.ToString();
        }

        private static string Format(object value)
        {
            switch (value)
            {
                case null: return "(null)";
                case double d: return R(d);
                case ValueTuple<double, double> t2: return R(t2.Item1) + "-" + R(t2.Item2);
                case ValueTuple<double, double, double> t3:
                    return R(t3.Item1) + "/" + R(t3.Item2) + "/" + R(t3.Item3);
                case GhostMapPresence.LastKnownGhostFrame f:
                    return (f.RecordingId ?? "(null)") + "/" + (f.Source ?? "(null)") + "/"
                        + (f.Branch ?? "(null)") + "/" + (f.Body ?? "(null)") + "/" + R(f.LastUT);
                case GhostMapPresence.IconDrivePropagation p:
                    return R(p.PropagateUT) + "@" + p.Frame.ToString(Ic);
                case IFormattable fm: return fm.ToString(null, Ic);
                default: return value.ToString();
            }
        }

        private static string R(double value)
        {
            return value.ToString("R", Ic);
        }

        private static string GoldenPath()
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            return Path.Combine(root, "Source", "Parsek.Tests", "Fixtures", "Characterization", GoldenName);
        }

        private string Capture()
        {
            var sb = new StringBuilder();
            foreach (Case c in Cases())
                sb.Append(RunCase(c));
            return sb.ToString();
        }

        [UnityEntryFact]
        public void EveryUpdateArm_MatchesTheGoldenCapture()
        {
            string actual = Capture();
            string path = GoldenPath();
            if (Environment.GetEnvironmentVariable("PARSEK_CHARACTERIZATION_REGEN") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, actual, new UTF8Encoding(false));
                return;
            }
            Assert.True(File.Exists(path), "golden capture missing: " + path);
            string expected = File.ReadAllText(path).Replace("\r\n", "\n");
            if (expected == actual)
                return;
            string[] e = expected.Split('\n');
            string[] a = actual.Split('\n');
            int n = Math.Min(e.Length, a.Length);
            for (int i = 0; i < n; i++)
            {
                if (!string.Equals(e[i], a[i], StringComparison.Ordinal))
                    Assert.True(false, string.Format(Ic,
                        "line {0} differs\nexpected: {1}\nactual:   {2}", i + 1, e[i], a[i]));
            }
            Assert.True(false, string.Format(Ic,
                "line counts differ: expected {0}, actual {1}", e.Length, a.Length));
        }

        [UnityEntryFact]
        public void TheCaptureReachesEveryCharacterizedArm()
        {
            // Each fragment marks one characterized arm: a line only that arm writes.
            string all = Capture();
            string[] arms =
            {
                "== no-ghost-at-index\nseed ghosts=[]",
                "reason=no-orbit-driver",
                "ghost recId rebind: pid=9001 (none) -> n0",
                "reason=body-not-found",
                "update-state-vector-skip: rec=r0",
                "reason=anchor-not-found",
                "reason=state-vector-from-orbital-checkpoint",
                "branch=BodyFixedPrimary",
                "update-state-vector-suppressed: rec=sv-debris idx=3",
                "returned true",
                "update-state-vector-soi-change: rec=a0",
                "Gap-glide inertial seed declined for ghost pid=9010",
                "reason=historical-rotation-unavailable",
                "reason=gap-pair-point-not-found",
                "State-vector reseed cleared stale segment-drive state for ghost pid=9014",
                "FromWorldPosAndRecordedVelocity: body=Kerbin ut=2500.00",
                "top Planetarium.GetUniversalTime",
            };
            foreach (string arm in arms)
                Assert.True(all.Contains(arm), "the corpus no longer reaches: " + arm);
        }
    }
}
