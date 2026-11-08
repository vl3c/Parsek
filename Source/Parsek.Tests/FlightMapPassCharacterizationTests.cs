using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization of the flight map deferred-create pass
    /// (<c>GhostMapPresence.RunFlightMapDeferredCreatePass</c>) and orbit-reseed pass
    /// (<c>GhostMapPresence.RunFlightMapOrbitReseedPass</c>). Each case seeds the flight
    /// per-frame stores, runs one or more frames of a pass, and records after every frame the
    /// content of every flight store the passes touch plus every log line written, compared
    /// byte for byte against a golden file captured from the passes before they were split.
    ///
    /// Reach, measured: no ghost ProtoVessel can be built headless (a test body reads as Unity
    /// fake-null, so a create attempt logs the missing body and returns null), so the
    /// created-ghost bookkeeping of the create pass and every arm that needs a live ghost vessel
    /// are outside this capture. The re-aim descent arms (loiter-gap clamp, trim-gap create skip)
    /// need a re-aim unit and are outside it too. Both passes are private and pinned by
    /// <c>MapPresenceSeamTests</c>, so they are invoked by reflection.
    ///
    /// The corpus reaches KSP orbit / body methods whose bodies call Unity natives, so both facts
    /// are <see cref="UnityEntryFactAttribute"/>.
    ///
    /// Regenerate (only when a behavior change is intended, never in a refactor):
    /// PARSEK_CHARACTERIZATION_REGEN=1 with the filter on this class.
    /// </summary>
    [Collection("Sequential")]
    public class FlightMapPassCharacterizationTests : IDisposable
    {
        private const string GoldenName = "flight-map-passes.golden.txt";
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
        private static readonly Regex GuidPattern = new Regex("[0-9a-f]{32}", RegexOptions.Compiled);

        private readonly List<string> logLines = new List<string>();
        private readonly CelestialBody kerbin;
        private readonly CelestialBody mun;
        private double clock = 1000.0;

        public FlightMapPassCharacterizationTests()
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
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
            GhostMapPresence.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private enum Pass { Create, Reseed }

        private sealed class Case
        {
            internal string Name;
            internal Action Setup;
            internal Func<GhostPlaybackLogic.LoopUnitSet> Units = () => GhostPlaybackLogic.LoopUnitSet.Empty;
            internal List<(Pass pass, double ut)> Frames = new List<(Pass, double)>();
        }

        // ---------------------------------------------------------------- builders

        private static Recording Orbiting(string id, string body = "Kerbin")
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

        private static OrbitSegment Seg(double start, double end, double sma, string body = "Kerbin",
            double ecc = 0.01)
        {
            return new OrbitSegment
            {
                startUT = start, endUT = end, bodyName = body, semiMajorAxis = sma,
                eccentricity = ecc, inclination = 0.3, epoch = start
            };
        }

        private static Recording SegmentRecording(string id)
        {
            var rec = Orbiting(id);
            rec.OrbitSegments = new List<OrbitSegment> { Seg(400, 600, 700000) };
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

        private static Recording PointsOnlyRecording(string id)
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
                        ut = 100, bodyName = "Kerbin", altitude = 75000,
                        velocity = new Vector3(0, 2200, 0)
                    },
                    new TrajectoryPoint
                    {
                        ut = 900, bodyName = "Kerbin", altitude = 90000,
                        velocity = new Vector3(0, 2100, 0)
                    }
                }
            };
        }

        // Two non-equivalent same-body segments with an Absolute track section across the gap
        // and points inside it: the orbit-raise gap the points glide drives.
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

        private static Recording RetainedRecording(string id)
        {
            var rec = SegmentRecording(id);
            rec.VesselSnapshot = new ConfigNode("VESSEL");
            return rec;
        }

        private static GhostPlaybackLogic.LoopUnitSet SingleMemberUnit(int idx)
        {
            var unit = new GhostPlaybackLogic.LoopUnit(
                idx, new[] { idx }, spanStartUT: 100, spanEndUT: 900,
                cadenceSeconds: 2000, phaseAnchorUT: 100);
            return new GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { idx, unit } },
                new Dictionary<int, int> { { idx, idx } });
        }

        private static void Add(Recording rec) => RecordingStore.AddRecordingWithTreeForTesting(rec);

        private static void Pending(int idx, Recording rec, bool allowSoi = false, string body = null)
        {
            GhostMapPresence.flightPendingMapVessels[idx] =
                new GhostMapPresence.PendingMapVessel(rec, allowSoi, body);
        }

        private static void Last(int idx, string body, double sma, double ecc)
        {
            GhostMapPresence.flightLastMapOrbitByIndex[idx] = (body, sma, ecc);
        }

        private static List<(Pass, double)> Frames(Pass pass, params double[] uts)
        {
            var list = new List<(Pass, double)>();
            foreach (double ut in uts)
                list.Add((pass, ut));
            return list;
        }

        // ---------------------------------------------------------------- corpus

        private static IEnumerable<Case> Cases()
        {
            // ---- deferred create
            yield return new Case
            {
                Name = "create-empty-queue",
                Setup = () => Add(SegmentRecording("c0")),
                Frames = Frames(Pass.Create, 500),
            };
            yield return new Case
            {
                Name = "create-segment-accepted",
                Setup = () => { var r = SegmentRecording("c1"); Add(r); Pending(0, r); },
                Frames = Frames(Pass.Create, 500),
            };
            yield return new Case
            {
                Name = "create-before-any-data-stays-pending",
                Setup = () => { var r = SegmentRecording("c2"); Add(r); Pending(0, r); },
                Frames = Frames(Pass.Create, 50, 60),
            };
            yield return new Case
            {
                Name = "create-terminal-orbit-after-end",
                Setup = () => { var r = SegmentRecording("c3"); Add(r); Pending(0, r); },
                Frames = Frames(Pass.Create, 950),
            };
            yield return new Case
            {
                Name = "create-state-vector-points-only",
                Setup = () => { var r = PointsOnlyRecording("c4"); Add(r); Pending(0, r); },
                Frames = Frames(Pass.Create, 500),
            };
            yield return new Case
            {
                Name = "create-soi-gap-pending-flags",
                Setup = () => { var r = SegmentRecording("c5"); Add(r); Pending(0, r, true, "Mun"); },
                Frames = Frames(Pass.Create, 700),
            };
            yield return new Case
            {
                Name = "create-landed-past-segments",
                Setup = () => { var r = LandedSegmentRecording("c6"); Add(r); Pending(0, r); },
                Frames = Frames(Pass.Create, 700, 950),
            };
            yield return new Case
            {
                Name = "create-several-pending",
                Setup = () =>
                {
                    var a = SegmentRecording("c7a"); var b = PointsOnlyRecording("c7b");
                    var c = LandedSegmentRecording("c7c");
                    Add(a); Add(b); Add(c);
                    Pending(2, c); Pending(0, a); Pending(1, b);
                    GhostMapPresence.flightStateVectorCachedIndices[1] = 0;
                },
                Frames = Frames(Pass.Create, 500, 950),
            };
            yield return new Case
            {
                Name = "create-pending-index-not-committed",
                Setup = () => { var r = SegmentRecording("c8"); Pending(4, r); },
                Frames = Frames(Pass.Create, 500),
            };
            yield return new Case
            {
                Name = "create-loop-out-of-window-hidden",
                Setup = () => { var r = SegmentRecording("c9"); Add(r); Pending(0, r); },
                Units = () => SingleMemberUnit(0),
                Frames = Frames(Pass.Create, 3500),
            };
            yield return new Case
            {
                Name = "create-loop-in-window-synthesis",
                Setup = () => { var r = SegmentRecording("c10"); Add(r); Pending(0, r); },
                Units = () => SingleMemberUnit(0),
                Frames = Frames(Pass.Create, 2500, 2600),
            };
            yield return new Case
            {
                Name = "create-loop-in-window-cross-body-terminal",
                Setup = () =>
                {
                    var r = SegmentRecording("c11"); r.TerminalOrbitBody = "Mun";
                    Add(r); Pending(0, r);
                },
                Units = () => SingleMemberUnit(0),
                Frames = Frames(Pass.Create, 2500),
            };

            // ---- orbit reseed
            yield return new Case
            {
                Name = "reseed-index-out-of-range",
                Setup = () => { Add(SegmentRecording("r0")); Last(5, "Kerbin", 1, 0); },
                Frames = Frames(Pass.Reseed, 500),
            };
            yield return new Case
            {
                Name = "reseed-no-segments",
                Setup = () => { Add(PointsOnlyRecording("r1")); Last(0, "Kerbin", 1, 0); },
                Frames = Frames(Pass.Reseed, 500),
            };
            yield return new Case
            {
                Name = "reseed-unchanged-key",
                Setup = () => { Add(SegmentRecording("r2")); Last(0, "Kerbin", 700000, 0.01); },
                Frames = Frames(Pass.Reseed, 500),
            };
            yield return new Case
            {
                Name = "reseed-changed-key",
                Setup = () => { Add(SegmentRecording("r3")); Last(0, "Kerbin", 1, 0); },
                Frames = Frames(Pass.Reseed, 500, 510),
            };
            yield return new Case
            {
                Name = "reseed-terminal-fallback",
                Setup = () => { Add(SegmentRecording("r4")); Last(0, "Kerbin", 700000, 0.01); },
                Frames = Frames(Pass.Reseed, 700, 710, 950),
            };
            yield return new Case
            {
                Name = "reseed-future-segment-requeue",
                Setup = () => { Add(LandedSegmentRecording("r5")); Last(0, "Kerbin", 700000, 0.01); },
                Frames = Frames(Pass.Reseed, 300),
            };
            yield return new Case
            {
                Name = "reseed-future-segment-already-pending",
                Setup = () =>
                {
                    var r = LandedSegmentRecording("r6"); Add(r);
                    Last(0, "Kerbin", 700000, 0.01); Pending(0, r, false, "Duna");
                },
                Frames = Frames(Pass.Reseed, 300),
            };
            yield return new Case
            {
                Name = "reseed-left-orbit-segments",
                Setup = () => { Add(LandedSegmentRecording("r7")); Last(0, "Kerbin", 700000, 0.01); },
                Frames = Frames(Pass.Reseed, 700),
            };
            yield return new Case
            {
                Name = "reseed-left-orbit-cached-state-vector",
                Setup = () =>
                {
                    Add(LandedSegmentRecording("r8")); Last(0, "Kerbin", 700000, 0.01);
                    GhostMapPresence.flightStateVectorCachedIndices[0] = 1;
                },
                Frames = Frames(Pass.Reseed, 950),
            };
            yield return new Case
            {
                Name = "reseed-terminal-retention",
                Setup = () =>
                {
                    var r = RetainedRecording("r9");
                    r.TerminalStateValue = TerminalState.Orbiting;
                    r.TerminalOrbitBody = null;
                    r.TerminalOrbitSemiMajorAxis = 0;
                    Add(r); Last(0, "Kerbin", 700000, 0.01);
                },
                Frames = Frames(Pass.Reseed, 950, 960, 970),
            };
            yield return new Case
            {
                Name = "reseed-terminal-retention-deferred-reason",
                Setup = () =>
                {
                    var r = RetainedRecording("r10");
                    r.TerminalOrbitBody = null;
                    r.TerminalOrbitSemiMajorAxis = 0;
                    r.TerminalSpawnSafetyDeferred = true;
                    r.TerminalSpawnSafetyReasonCode = "char-deferred";
                    Add(r); Last(0, "Kerbin", 700000, 0.01);
                },
                Frames = Frames(Pass.Reseed, 950),
            };
            yield return new Case
            {
                Name = "reseed-terminal-retention-no-id",
                Setup = () =>
                {
                    var r = RetainedRecording("r11");
                    r.TerminalOrbitBody = null;
                    r.TerminalOrbitSemiMajorAxis = 0;
                    r.TerminalSpawnSafetyDeferred = true;
                    Add(r); r.RecordingId = null; Last(0, "Kerbin", 700000, 0.01);
                },
                Frames = Frames(Pass.Reseed, 950, 955),
            };
            yield return new Case
            {
                Name = "reseed-gap-points-glide",
                Setup = () => { Add(GapRecording("r12", true)); Last(0, "Kerbin", 700000, 0.01); },
                Frames = Frames(Pass.Reseed, 400, 401, 550),
            };
            yield return new Case
            {
                Name = "reseed-gap-points-no-bracket",
                Setup = () => { Add(GapRecording("r13", false)); Last(0, "Kerbin", 700000, 0.01); },
                Frames = Frames(Pass.Reseed, 400, 401),
            };
            yield return new Case
            {
                Name = "reseed-loop-out-of-window-requeue",
                Setup = () => { Add(SegmentRecording("r14")); Last(0, "Kerbin", 700000, 0.01); },
                Units = () => SingleMemberUnit(0),
                Frames = Frames(Pass.Reseed, 3500),
            };
            yield return new Case
            {
                Name = "reseed-loop-in-window-reapplies",
                Setup = () => { Add(SegmentRecording("r15")); Last(0, "Kerbin", 700000, 0.01); },
                Units = () => SingleMemberUnit(0),
                Frames = Frames(Pass.Reseed, 2500, 2510),
            };
            yield return new Case
            {
                Name = "reseed-several-indices",
                Setup = () =>
                {
                    Add(SegmentRecording("r16a")); Add(LandedSegmentRecording("r16b"));
                    Add(PointsOnlyRecording("r16c")); Add(SegmentRecording("r16d"));
                    Last(3, "Kerbin", 1, 0); Last(1, "Kerbin", 700000, 0.01);
                    Last(2, "Kerbin", 1, 0); Last(0, "Kerbin", 700000, 0.01);
                },
                Frames = Frames(Pass.Reseed, 300, 500, 700),
            };
            yield return new Case
            {
                Name = "reseed-then-create-cycle",
                Setup = () => { Add(LandedSegmentRecording("r17")); Last(0, "Kerbin", 700000, 0.01); },
                Frames = new List<(Pass, double)>
                {
                    (Pass.Reseed, 300), (Pass.Create, 300), (Pass.Create, 450), (Pass.Reseed, 450),
                },
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
            GhostMapPresence.ClearFlightMapPresenceState();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetRateLimitsForTesting();
            GhostMapPresence.FindBodyByNameForTesting = name =>
                string.Equals(name, "Mun", StringComparison.Ordinal) ? mun
                : string.Equals(name, "Kerbin", StringComparison.Ordinal) ? kerbin
                : null;
            clock = 1000.0;

            c.Setup();
            GhostPlaybackLogic.LoopUnitSet units = c.Units();
            var sb = new StringBuilder();
            sb.Append("== ").Append(c.Name).Append('\n');
            sb.Append("seed ").Append(DumpStores()).Append('\n');
            foreach (var frame in c.Frames)
            {
                logLines.Clear();
                if (frame.pass == Pass.Create)
                    Invoke("RunFlightMapDeferredCreatePass", frame.ut, units);
                else
                    Invoke("RunFlightMapOrbitReseedPass", frame.ut, units,
                        (IReadOnlyList<Recording>)RecordingStore.CommittedRecordings);
                sb.Append("-- ").Append(frame.pass).Append(" ut=").Append(R(frame.ut)).Append('\n');
                foreach (string line in logLines)
                    sb.Append("log ").Append(Scrub(line)).Append('\n');
                sb.Append("after ").Append(DumpStores()).Append('\n');
                clock += 3.0;
            }
            return sb.ToString();
        }

        private static string Scrub(string line)
        {
            return GuidPattern.Replace(line.Replace("\r", "\\r").Replace("\n", "\\n"), "<guid>");
        }

        private static string DumpStores()
        {
            var sb = new StringBuilder();
            sb.Append("pending=[");
            foreach (var kv in GhostMapPresence.flightPendingMapVessels)
            {
                sb.Append(kv.Key.ToString(Ic)).Append(':')
                    .Append(kv.Value.Trajectory?.RecordingId ?? "(null)").Append('/')
                    .Append(kv.Value.AllowSoiGapStateVectorFallback).Append('/')
                    .Append(kv.Value.ExpectedSoiGapBody ?? "(null)").Append(' ');
            }
            sb.Append("] last=[");
            foreach (var kv in GhostMapPresence.flightLastMapOrbitByIndex)
            {
                sb.Append(kv.Key.ToString(Ic)).Append(':')
                    .Append(kv.Value.body ?? "(null)").Append('/')
                    .Append(R(kv.Value.sma)).Append('/')
                    .Append(R(kv.Value.ecc)).Append(' ');
            }
            sb.Append("] svCached=[");
            foreach (var kv in GhostMapPresence.flightStateVectorCachedIndices)
                sb.Append(kv.Key.ToString(Ic)).Append(':').Append(kv.Value.ToString(Ic)).Append(' ');
            sb.Append("] svTraj=[");
            foreach (var kv in GhostMapPresence.flightStateVectorOrbitTrajectories)
                sb.Append(kv.Key.ToString(Ic)).Append(':')
                    .Append(kv.Value?.RecordingId ?? "(null)").Append(' ');
            sb.Append("] soiBodies=[");
            foreach (var kv in GhostMapPresence.flightSoiGapStateVectorExpectedBodies)
                sb.Append(kv.Key.ToString(Ic)).Append(':').Append(kv.Value ?? "(null)").Append(' ');
            sb.Append("] retentionLogged=[");
            foreach (string id in GhostMapPresence.flightTerminalMapRetentionLoggedIds)
                sb.Append(id).Append(' ');
            sb.Append(']');
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
        public void EveryPassArm_MatchesTheGoldenCapture()
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
            // Each fragment below marks one characterized arm of the two passes: a line only that
            // arm writes (or causes), or a store state only it leaves. The corpus must keep
            // reaching all of them or the golden stops covering that arm.
            string all = Capture();
            string[] arms =
            {
                // create pass
                "Pending map-create accepting terminal-orbit synthesis",
                "map-presence-pending-create",
                "create-segment-intent:",
                "create-state-vector-intent:",
                "create-terminal-orbit-intent:",
                // reseed pass
                "Flight gap-points glide: member=0",
                "Flight gap-points glide skipped (no bracketing point)",
                "Map presence retained because terminal real spawn is pending/deferred",
                "reason=char-deferred",
                "Re-queued recording #0 to pendingMapVessels (gap between orbit segments expectedBody=Kerbin)",
                "Re-queued recording #0 to pendingMapVessels (gap between orbit segments expectedBody=(none))",
                "last=[0:Kerbin/0/0 ]",
                "to terminal-orbit fallback during sparse gap",
            };
            foreach (string fragment in arms)
                Assert.True(all.Contains(fragment), "no case reaches: " + fragment);
        }
    }
}
