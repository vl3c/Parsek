using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization of the Tracking Station per-tick refresh of existing ghost ProtoVessels
    /// (<c>GhostMapPresence.RefreshTrackingStationGhosts</c>). Each case seeds committed recordings
    /// and index-keyed ghosts (an uninitialized <see cref="Vessel"/> carrying only its
    /// persistentId), runs one or more refresh ticks, and records after every tick the content of
    /// every store the refresh touches plus every log line written, compared byte for byte against
    /// a golden file captured from the method before it was split.
    ///
    /// Reach, measured: the fake ghost vessel has no OrbitDriver, so every orbit apply logs its
    /// no-orbit-driver line and returns (the state-vector update returns false at that point, so
    /// the active Re-Fly suppression teardown is outside this capture). Removal runs the real
    /// <c>RemoveGhostVesselForRecording</c>; the fake vessel's <c>Die()</c> throws on the missing
    /// FlightGlobals and the caught message is scrubbed. The method is private, so it is invoked
    /// by reflection.
    ///
    /// The corpus reaches KSP orbit / body methods whose bodies call Unity natives, so both facts
    /// are <see cref="UnityEntryFactAttribute"/>.
    ///
    /// Regenerate (only when a behavior change is intended, never in a refactor):
    /// PARSEK_CHARACTERIZATION_REGEN=1 with the filter on this class.
    /// </summary>
    [Collection("Sequential")]
    public class TrackingStationRefreshCharacterizationTests : IDisposable
    {
        private const string GoldenName = "tracking-station-refresh.golden.txt";
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
        private static readonly Regex GuidPattern = new Regex("[0-9a-f]{32}", RegexOptions.Compiled);
        private static readonly Regex DieMessagePattern =
            new Regex(@"(Die\(\) threw for index=-?\d+: ).*$", RegexOptions.Compiled);

        private readonly List<string> logLines = new List<string>();
        private readonly CelestialBody kerbin;
        private readonly CelestialBody mun;
        private double clock = 1000.0;
        private double liveUT;
        private readonly GameScenes priorScene;

        public TrackingStationRefreshCharacterizationTests()
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
            kerbin = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            mun = TestBodyRegistry.CreateBody("Mun", 200000.0, 6.5138398e10);
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
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        internal sealed class Case
        {
            internal string Name;
            internal Action Setup;
            internal bool NullCommitted;
            internal bool AcceptedTailSeed;
            internal Func<HashSet<string>> Suppressed = () => new HashSet<string>();
            internal Func<GhostPlaybackLogic.LoopUnitSet> Units = () => GhostPlaybackLogic.LoopUnitSet.Empty;
            internal double[] Ticks;
        }

        // ---------------------------------------------------------------- private stores

        private static object Store(string name)
        {
            FieldInfo f = typeof(GhostMapPresence).GetField(
                name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(f);
            return f.GetValue(null);
        }

        internal static Dictionary<int, Vessel> GhostsByIndex =>
            (Dictionary<int, Vessel>)Store("vesselsByRecordingIndex");

        internal static Dictionary<int, IPlaybackTrajectory> SvTrajectories =>
            (Dictionary<int, IPlaybackTrajectory>)Store("trackingStationStateVectorOrbitTrajectories");

        internal static Dictionary<int, int> SvCachedIndices =>
            (Dictionary<int, int>)Store("trackingStationStateVectorCachedIndices");

        private static Dictionary<uint, double> LineHolds =>
            (Dictionary<uint, double>)Store("ghostParkingConicLineHoldUntilUT");

        private static Dictionary<int, GhostMapPresence.LastKnownGhostFrame> LastKnown =>
            (Dictionary<int, GhostMapPresence.LastKnownGhostFrame>)Store("lastKnownByRecordingIndex");

        // ---------------------------------------------------------------- builders

        internal static Vessel FakeGhost(uint pid)
        {
            var vessel = (Vessel)FormatterServices.GetUninitializedObject(typeof(Vessel));
            vessel.persistentId = pid;
            return vessel;
        }

        /// <summary>Seeds an index-keyed ghost the way a create leaves it (pid maps, ghost set,
        /// optional orbit bounds, optional state-vector ownership).</summary>
        internal static void Ghost(int idx, uint pid, (double s, double e)? bounds = null,
            Recording stateVectorOwner = null)
        {
            GhostsByIndex[idx] = FakeGhost(pid);
            GhostMapPresence.TrackRecordingGhostIdentityForTesting(pid, idx, null);
            GhostMapPresence.ghostMapVesselPids.Add(pid);
            if (bounds.HasValue)
                GhostMapPresence.ghostOrbitBounds[pid] = bounds.Value;
            if (stateVectorOwner != null)
                SvTrajectories[idx] = stateVectorOwner;
        }

        internal static Recording Orbiting(string id, string body = "Kerbin")
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Char " + id,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = body,
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalOrbitEccentricity = 0.01,
                TerminalOrbitInclination = 0.3,
                TerminalOrbitEpoch = 100,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = body, altitude = 70000 },
                    new TrajectoryPoint { ut = 900, bodyName = body, altitude = 100000 }
                },
            };
        }

        internal static OrbitSegment Seg(double start, double end, double sma, string body = "Kerbin",
            double ecc = 0.01)
        {
            return new OrbitSegment
            {
                startUT = start, endUT = end, bodyName = body, semiMajorAxis = sma,
                eccentricity = ecc, inclination = 0.3, epoch = start
            };
        }

        internal static Recording SegmentRecording(string id)
        {
            var rec = Orbiting(id);
            rec.OrbitSegments = new List<OrbitSegment> { Seg(400, 600, 700000) };
            return rec;
        }

        // Kerbin segment then a Mun segment: no same-body carry across the gap.
        private static Recording CrossBodyGapRecording(string id)
        {
            var rec = Orbiting(id, "Mun");
            rec.OrbitSegments = new List<OrbitSegment>
            {
                Seg(200, 300, 700000),
                Seg(500, 600, 250000, "Mun"),
            };
            return rec;
        }

        private static Recording LandedSegmentRecording(string id)
        {
            var rec = SegmentRecording(id);
            rec.TerminalStateValue = TerminalState.Landed;
            rec.TerminalOrbitBody = null;
            rec.TerminalOrbitSemiMajorAxis = 0;
            return rec;
        }

        internal static Recording PointsOnlyRecording(string id, double altitude = 75000, float speed = 2200)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Char " + id,
                TerminalStateValue = TerminalState.SubOrbital,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100, bodyName = "Kerbin", altitude = altitude,
                        velocity = new Vector3(0, speed, 0)
                    },
                    new TrajectoryPoint
                    {
                        ut = 900, bodyName = "Kerbin", altitude = altitude + 15000,
                        velocity = new Vector3(0, speed - 100, 0)
                    }
                }
            };
        }

        private static Recording RelativePointsRecording(string id, float speed)
        {
            var rec = PointsOnlyRecording(id, 5, speed);
            rec.TrackSections = new List<TrackSection>
            {
                new TrackSection
                {
                    startUT = 100, endUT = 900,
                    environment = SegmentEnvironment.ExoBallistic,
                    referenceFrame = ReferenceFrame.Relative,
                    source = TrackSectionSource.Active,
                    anchorRecordingId = "anchor-" + id,
                    frames = new List<TrajectoryPoint>(),
                }
            };
            return rec;
        }

        // Two non-equivalent same-body segments with an Absolute track section across the gap and
        // points inside it: the orbit-raise gap the points glide drives.
        private static Recording GapRecording(string id, bool pointsBracketGap)
        {
            var rec = Orbiting(id);
            rec.TerminalOrbitSemiMajorAxis = 800000;
            rec.OrbitSegments = new List<OrbitSegment>
            {
                Seg(200, 300, 700000),
                Seg(500, 600, 800000),
            };
            rec.TrackSections = new List<TrackSection>
            {
                new TrackSection
                {
                    startUT = 100, endUT = 900,
                    environment = SegmentEnvironment.ExoBallistic,
                    referenceFrame = ReferenceFrame.Absolute,
                    source = TrackSectionSource.Active,
                    frames = new List<TrajectoryPoint>(),
                }
            };
            rec.Points = pointsBracketGap
                ? new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin", altitude = 70000 },
                    new TrajectoryPoint { ut = 350, bodyName = "Kerbin", altitude = 90000 },
                    new TrajectoryPoint { ut = 450, bodyName = "Kerbin", altitude = 150000 },
                    new TrajectoryPoint { ut = 900, bodyName = "Kerbin", altitude = 200000 },
                }
                : new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 420, bodyName = "Kerbin", altitude = 150000 },
                    new TrajectoryPoint { ut = 900, bodyName = "Kerbin", altitude = 200000 },
                };
            return rec;
        }

        // An OrbitalCheckpoint section [300,700] with frames, plus (optionally) a segment
        // [400,600] covering part of it.
        internal static Recording CheckpointRecording(string id, bool withCoveringSegment)
        {
            var rec = Orbiting(id);
            rec.OrbitSegments = withCoveringSegment
                ? new List<OrbitSegment> { Seg(400, 600, 700000) }
                : new List<OrbitSegment>();
            rec.TrackSections = new List<TrackSection>
            {
                new TrackSection
                {
                    startUT = 300, endUT = 700,
                    environment = SegmentEnvironment.ExoBallistic,
                    referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                    source = TrackSectionSource.Checkpoint,
                    frames = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint
                        {
                            ut = 300, bodyName = "Kerbin", altitude = 90000,
                            velocity = new Vector3(0, 2200, 0)
                        },
                        new TrajectoryPoint
                        {
                            ut = 700, bodyName = "Kerbin", altitude = 95000,
                            velocity = new Vector3(0, 2190, 0)
                        },
                    },
                    checkpoints = new List<OrbitSegment>(),
                }
            };
            return rec;
        }

        private static Recording EndpointRecording()
        {
            return GhostMapEndpointTailTests.BuildEndpointRecording(
                GhostMapEndpointTailTests.Segment(startUT: 120.0, endUT: 150.0));
        }

        internal static GhostPlaybackLogic.LoopUnitSet SingleMemberUnit(int idx)
        {
            var unit = new GhostPlaybackLogic.LoopUnit(
                idx, new[] { idx }, spanStartUT: 100, spanEndUT: 900,
                cadenceSeconds: 2000, phaseAnchorUT: 100);
            return new GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { idx, unit } },
                new Dictionary<int, int> { { idx, idx } });
        }

        internal static void Add(Recording rec) => RecordingStore.AddRecordingWithTreeForTesting(rec);

        private static double[] Ticks(params double[] uts) => uts;

        // ---------------------------------------------------------------- corpus

        private static IEnumerable<Case> Cases()
        {
            yield return new Case
            {
                Name = "no-ghosts-early-return",
                Setup = () => Add(SegmentRecording("e0")),
                Ticks = Ticks(500),
            };
            yield return new Case
            {
                Name = "committed-null-teardown",
                NullCommitted = true,
                Setup = () => { Add(SegmentRecording("e1")); Ghost(0, 9001, (400, 600)); },
                Ticks = Ticks(500),
            };
            yield return new Case
            {
                Name = "index-stale-teardown",
                Setup = () => { Add(SegmentRecording("e2")); Ghost(3, 9002, (400, 600)); },
                Ticks = Ticks(500),
            };
            yield return new Case
            {
                Name = "overlap-handoff-teardown",
                Setup = () =>
                {
                    var r = SegmentRecording("e3");
                    r.LoopPlayback = true;
                    r.LoopIntervalSeconds = 60;
                    Add(r); Ghost(0, 9003, (400, 600));
                },
                Ticks = Ticks(500),
            };
            yield return new Case
            {
                Name = "loop-out-of-window-teardown",
                Setup = () => { Add(SegmentRecording("e4")); Ghost(0, 9004, (400, 600)); },
                Units = () => SingleMemberUnit(0),
                Ticks = Ticks(3500),
            };
            yield return new Case
            {
                Name = "suppressed-child-started",
                Setup = () => { Add(SegmentRecording("e5")); Ghost(0, 9005, (400, 600)); },
                Suppressed = () => new HashSet<string> { "e5" },
                Ticks = Ticks(500),
            };
            yield return new Case
            {
                Name = "playback-disabled",
                Setup = () =>
                {
                    var r = SegmentRecording("e6"); r.PlaybackEnabled = false;
                    Add(r); Ghost(0, 9006, (400, 600));
                },
                Ticks = Ticks(500),
            };
            yield return new Case
            {
                Name = "materialized-real-vessel",
                Setup = () =>
                {
                    var r = SegmentRecording("e7"); r.VesselSpawned = true;
                    Add(r); Ghost(0, 9007, (400, 600));
                },
                Ticks = Ticks(500),
            };
            yield return new Case
            {
                Name = "segment-bounds-unchanged-no-apply",
                Setup = () => { Add(SegmentRecording("s0")); Ghost(0, 9100, (400, 600)); },
                Ticks = Ticks(500, 510),
            };
            yield return new Case
            {
                Name = "segment-bounds-changed-apply",
                Setup = () => { Add(SegmentRecording("s1")); Ghost(0, 9101, (100, 200)); },
                Ticks = Ticks(500, 510),
            };
            yield return new Case
            {
                Name = "no-bounds-no-segment-skip",
                Setup = () => { Add(SegmentRecording("s2")); Ghost(0, 9102); },
                Ticks = Ticks(300),
            };
            yield return new Case
            {
                Name = "no-bounds-covering-segment-apply",
                Setup = () => { Add(SegmentRecording("s3")); Ghost(0, 9103); },
                Ticks = Ticks(500),
            };
            yield return new Case
            {
                Name = "cross-body-gap-expired",
                Setup = () => { Add(CrossBodyGapRecording("s4")); Ghost(0, 9104, (200, 300)); },
                Ticks = Ticks(400),
            };
            yield return new Case
            {
                Name = "landed-past-segments-expired",
                Setup = () => { Add(LandedSegmentRecording("s5")); Ghost(0, 9105, (400, 600)); },
                Ticks = Ticks(950),
            };
            yield return new Case
            {
                Name = "terminal-orbit-endpoint-tail",
                Setup = () => { Add(SegmentRecording("s6")); Ghost(0, 9106, (400, 600)); },
                Ticks = Ticks(950, 960),
            };
            yield return new Case
            {
                Name = "terminal-orbit-no-bounds",
                Setup = () => { Add(SegmentRecording("s7")); Ghost(0, 9107); },
                Ticks = Ticks(950),
            };
            yield return new Case
            {
                Name = "state-vector-update",
                Setup = () =>
                {
                    var r = PointsOnlyRecording("v0"); Add(r); Ghost(0, 9200, null, r);
                },
                Ticks = Ticks(500, 505),
            };
            yield return new Case
            {
                Name = "state-vector-below-threshold",
                Setup = () =>
                {
                    var r = PointsOnlyRecording("v1", 100, 50); Add(r); Ghost(0, 9201, null, r);
                },
                Ticks = Ticks(500),
            };
            yield return new Case
            {
                Name = "state-vector-expired-after-end",
                Setup = () =>
                {
                    var r = PointsOnlyRecording("v2"); Add(r); Ghost(0, 9202, null, r);
                },
                Ticks = Ticks(950),
            };
            yield return new Case
            {
                Name = "state-vector-no-bracket",
                Setup = () =>
                {
                    var r = PointsOnlyRecording("v3"); Add(r); Ghost(0, 9203, null, r);
                    SvCachedIndices[0] = 0;
                },
                Ticks = Ticks(50),
            };
            yield return new Case
            {
                Name = "state-vector-relative-fast",
                Setup = () =>
                {
                    var r = RelativePointsRecording("v4", 2200); Add(r); Ghost(0, 9204, null, r);
                },
                Ticks = Ticks(500),
            };
            yield return new Case
            {
                Name = "state-vector-relative-slow",
                Setup = () =>
                {
                    var r = RelativePointsRecording("v5", 1); Add(r); Ghost(0, 9205, null, r);
                },
                Ticks = Ticks(500),
            };
            yield return new Case
            {
                Name = "gap-points-glide",
                Setup = () => { Add(GapRecording("g0", true)); Ghost(0, 9300, (200, 300)); },
                Ticks = Ticks(400, 401, 550),
            };
            yield return new Case
            {
                Name = "gap-points-no-bracket",
                Setup = () => { Add(GapRecording("g1", false)); Ghost(0, 9301, (200, 300)); },
                Ticks = Ticks(400, 401),
            };
            yield return new Case
            {
                Name = "checkpoint-consumed-no-segment",
                Setup = () => { Add(CheckpointRecording("k0", false)); Ghost(0, 9400, (300, 700)); },
                Ticks = Ticks(350, 360),
            };
            yield return new Case
            {
                Name = "checkpoint-defers-to-segment",
                Setup = () => { Add(CheckpointRecording("k1", true)); Ghost(0, 9401, (400, 600)); },
                Ticks = Ticks(500),
            };
            yield return new Case
            {
                Name = "checkpoint-state-vector-ghost",
                Setup = () =>
                {
                    var r = CheckpointRecording("k2", true); Add(r); Ghost(0, 9402, null, r);
                },
                Ticks = Ticks(500),
            };
            yield return new Case
            {
                Name = "loop-in-window-segment-reapply",
                Setup = () => { Add(SegmentRecording("l0")); Ghost(0, 9500, (400, 600)); },
                Units = () => SingleMemberUnit(0),
                Ticks = Ticks(2500, 2510),
            };
            yield return new Case
            {
                Name = "loop-in-window-terminal-synthesis",
                Setup = () =>
                {
                    var r = SegmentRecording("l1"); r.OrbitSegments = new List<OrbitSegment>();
                    Add(r); Ghost(0, 9501, (400, 600));
                },
                Units = () => SingleMemberUnit(0),
                Ticks = Ticks(2500),
            };
            yield return new Case
            {
                Name = "loop-in-window-cross-body-terminal",
                Setup = () =>
                {
                    var r = SegmentRecording("l2"); r.OrbitSegments = new List<OrbitSegment>();
                    r.TerminalOrbitBody = "Mun";
                    Add(r); Ghost(0, 9502, (400, 600));
                },
                Units = () => SingleMemberUnit(0),
                Ticks = Ticks(2500),
            };
            yield return new Case
            {
                Name = "loop-in-window-materialized-kept",
                Setup = () =>
                {
                    var r = SegmentRecording("l3"); r.VesselSpawned = true;
                    Add(r); Ghost(0, 9503, (400, 600));
                },
                Units = () => SingleMemberUnit(0),
                Ticks = Ticks(2500),
            };
            yield return new Case
            {
                Name = "several-ghosts-mixed",
                Setup = () =>
                {
                    var a = SegmentRecording("m0"); var b = PointsOnlyRecording("m1");
                    var c = LandedSegmentRecording("m2"); var d = SegmentRecording("m3");
                    d.PlaybackEnabled = false;
                    Add(a); Add(b); Add(c); Add(d);
                    Ghost(3, 9603, (400, 600)); Ghost(1, 9601, null, b);
                    Ghost(2, 9602, (400, 600)); Ghost(0, 9600, (100, 200)); Ghost(7, 9607);
                    SvCachedIndices[2] = 5;
                },
                Ticks = Ticks(500, 950),
            };
            yield return new Case
            {
                Name = "endpoint-tail-overrides-covering-segment",
                AcceptedTailSeed = true,
                Setup = () => { Add(EndpointRecording()); Ghost(0, 9800, (120, 150)); },
                Ticks = Ticks(135.7, 140),
            };
            yield return new Case
            {
                Name = "endpoint-tail-rescues-terminal-expiry",
                AcceptedTailSeed = true,
                Setup = () => { Add(EndpointRecording()); Ghost(0, 9801, (120, 150)); },
                Ticks = Ticks(700, 710),
            };
            yield return new Case
            {
                Name = "endpoint-tail-no-bounds-terminal",
                AcceptedTailSeed = true,
                Setup = () => { Add(EndpointRecording()); Ghost(0, 9802); },
                Ticks = Ticks(700),
            };
            yield return new Case
            {
                Name = "endpoint-tail-loop-member-synthesis",
                AcceptedTailSeed = true,
                Setup = () => { Add(EndpointRecording()); Ghost(0, 9803, (120, 150)); },
                Units = () => SingleMemberUnit(0),
                Ticks = Ticks(2550),
            };
            yield return new Case
            {
                Name = "endpoint-tail-loop-member-covering-not-overridden",
                AcceptedTailSeed = true,
                Setup = () => { Add(EndpointRecording()); Ghost(0, 9804, (120, 150)); },
                Units = () => SingleMemberUnit(0),
                Ticks = Ticks(2135.7),
            };
        }

        // ---------------------------------------------------------------- runner

        private static void Invoke(string name, params object[] args)
        {
            MethodInfo m = typeof(GhostMapPresence).GetMethod(
                name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            try { m.Invoke(null, args); }
            catch (TargetInvocationException e) { throw e.InnerException; }
        }

        private string RunCase(Case c)
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetRateLimitsForTesting();
            GhostMapPresence.FindBodyByNameForTesting = name =>
                string.Equals(name, "Mun", StringComparison.Ordinal) ? mun
                : string.Equals(name, "Kerbin", StringComparison.Ordinal) ? kerbin
                : null;
            GhostMapPresence.CurrentUTNow = () => liveUT;
            if (c.AcceptedTailSeed)
            {
                OrbitSeedResolver.TailSeedResolverForTesting =
                    (IPlaybackTrajectory traj, CelestialBody body, double currentUT, TailSeedUse use,
                        out TailDerivedOrbitSeed seed) =>
                    {
                        seed = GhostMapEndpointTailTests.AcceptedTailSeed(
                            tailUT: 453.66,
                            latestStoredSegmentEndUT: 150.0,
                            rotationDriftSeconds: currentUT - 453.66);
                        return true;
                    };
            }
            clock = 1000.0;
            c.Setup();
            GhostPlaybackLogic.LoopUnitSet units = c.Units();
            HashSet<string> suppressed = c.Suppressed();
            var sb = new StringBuilder();
            sb.Append("== ").Append(c.Name).Append('\n');
            sb.Append("seed ").Append(DumpStores()).Append('\n');
            foreach (double ut in c.Ticks)
            {
                logLines.Clear();
                liveUT = ut;
                IReadOnlyList<Recording> committed = c.NullCommitted
                    ? null
                    : (IReadOnlyList<Recording>)RecordingStore.CommittedRecordings;
                Invoke("RefreshTrackingStationGhosts", committed, suppressed, ut, units);
                sb.Append("-- tick ut=").Append(R(ut)).Append('\n');
                foreach (string line in logLines)
                    sb.Append("log ").Append(Scrub(line)).Append('\n');
                sb.Append("after ").Append(DumpStores()).Append('\n');
                clock += 3.0;
            }
            return sb.ToString();
        }

        private static string Scrub(string line)
        {
            string flat = line.Replace("\r", "\\r").Replace("\n", "\\n");
            flat = DieMessagePattern.Replace(flat, "$1<die-message>");
            return GuidPattern.Replace(flat, "<guid>");
        }

        private static string DumpStores()
        {
            var sb = new StringBuilder();
            sb.Append("ghosts=[");
            foreach (var kv in GhostsByIndex)
                sb.Append(kv.Key.ToString(Ic)).Append(':')
                    .Append(kv.Value.persistentId.ToString(Ic)).Append(' ');
            sb.Append("] pids=[");
            foreach (uint pid in GhostMapPresence.ghostMapVesselPids)
                sb.Append(pid.ToString(Ic)).Append(' ');
            sb.Append("] bounds=[");
            foreach (var kv in GhostMapPresence.ghostOrbitBounds)
                sb.Append(kv.Key.ToString(Ic)).Append(':').Append(R(kv.Value.startUT))
                    .Append('-').Append(R(kv.Value.endUT)).Append(' ');
            sb.Append("] bodyFrame=[");
            foreach (var kv in GhostMapPresence.ghostBodyFrameOrbitBounds)
                sb.Append(kv.Key.ToString(Ic)).Append(':').Append(R(kv.Value.startUT))
                    .Append('-').Append(R(kv.Value.endUT)).Append(' ');
            sb.Append("] svTraj=[");
            foreach (var kv in SvTrajectories)
                sb.Append(kv.Key.ToString(Ic)).Append(':')
                    .Append(kv.Value?.RecordingId ?? "(null)").Append(' ');
            sb.Append("] svCached=[");
            foreach (var kv in SvCachedIndices)
                sb.Append(kv.Key.ToString(Ic)).Append(':').Append(kv.Value.ToString(Ic)).Append(' ');
            sb.Append("] holds=[");
            foreach (var kv in LineHolds)
                sb.Append(kv.Key.ToString(Ic)).Append(':').Append(R(kv.Value)).Append(' ');
            sb.Append("] lastKnown=[");
            foreach (var kv in LastKnown)
                sb.Append(kv.Key.ToString(Ic)).Append(':')
                    .Append(kv.Value.Source ?? "(null)").Append('/')
                    .Append(kv.Value.Body ?? "(null)").Append('/')
                    .Append(R(kv.Value.LastUT)).Append(' ');
            sb.Append("] counters=")
                .Append(GhostMapPresence.lifecycleCreatedThisTick.ToString(Ic)).Append('/')
                .Append(GhostMapPresence.lifecycleDestroyedThisTick.ToString(Ic)).Append('/')
                .Append(GhostMapPresence.lifecycleUpdatedThisTick.ToString(Ic));
            return sb.ToString();
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
        public void EveryRefreshArm_MatchesTheGoldenCapture()
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
            // Each fragment marks one characterized arm of the refresh: a line only that arm writes
            // (or causes). The corpus must keep reaching all of them or the golden stops covering it.
            string all = Capture();
            string[] arms =
            {
                "reason=tracking-station-committed-null",
                "reason=tracking-station-index-stale",
                "reason=overlap-handoff-to-per-instance",
                "reason=mission-loop-out-of-window",
                "reason=tracking-station-child-started",
                "reason=playback-disabled",
                "reason=tracking-station-spawned-real-vessel",
                "UT 400.0 reason=tracking-station-expired",
                "reason=tracking-station-state-vector-expired",
                "reason=below-state-vector-threshold",
                "TS orbit source: OrbitalCheckpoint section at effUT=500.0 deferring to covering OrbitSegment",
                "rec=k0 idx=0 vessel=\"Char k0\" source=StateVector",
                "TS gap-points glide: member=0 effUT=400.0",
                "TS gap-points glide skipped (no bracketing point)",
                "rec=v0 idx=0 vessel=\"Char v0\" source=StateVector",
                "ghostPid=9101 segmentBody=Kerbin",
                "ghostPid=9103 segmentBody=Kerbin",
                "loopShift=2000.00",
                "source=EndpointTail branch=(n/a) body=Kerbin worldPos=(0.0,0.0,0.0) ghostPid=9800",
                "source=EndpointTail branch=(n/a) body=Kerbin worldPos=(0.0,0.0,0.0) ghostPid=9801",
                "source=EndpointTail branch=(n/a) body=Kerbin worldPos=(0.0,0.0,0.0) ghostPid=9803",
                "source=Segment branch=(n/a) body=Kerbin worldPos=(0.0,0.0,0.0) ghostPid=9804",
                "IsTerminalOrbitSynthesisSafeForLoopMember: rec=l2 result=False",
            };
            foreach (string arm in arms)
                Assert.True(all.Contains(arm), "the corpus no longer reaches: " + arm);
        }
    }
}
