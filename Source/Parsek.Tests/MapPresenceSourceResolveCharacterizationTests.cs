using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization of <see cref="GhostMapPresence.ResolveMapPresenceGhostSource"/>: one case
    /// per decision exit, each recording the returned source, every out / ref value and EVERY log
    /// line the call wrote, compared byte for byte against a golden file captured from the
    /// method before it was split into phase helpers. A refactor that changes a decision, a
    /// reason, a detail string, a log line or the order of the lines fails here.
    ///
    /// Regenerate (only when a behavior change is intended, never in a refactor):
    /// PARSEK_CHARACTERIZATION_REGEN=1 with the filter on this class.
    /// </summary>
    [Collection("Sequential")]
    public class MapPresenceSourceResolveCharacterizationTests : IDisposable
    {
        private const string GoldenName = "map-presence-source-resolve.golden.txt";
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        private readonly List<string> logLines = new List<string>();
        private readonly CelestialBody kerbin;
        private readonly CelestialBody mun;

        public MapPresenceSourceResolveCharacterizationTests()
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
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
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

        private sealed class Case
        {
            internal string Name;
            internal Func<IPlaybackTrajectory> Build;
            internal bool IsSuppressed;
            internal bool AlreadyMaterialized;
            internal double CurrentUT;
            internal bool AllowTerminalOrbitFallback = true;
            internal int RecordingIndex = 3;
            internal bool AllowSoiGapStateVectorFallback;
            internal string ExpectedSoiGapBody;
            internal bool AcceptTerminalOrbitForLoopSynthesis;
            internal bool LoopMemberInWindow;
            internal bool LiveLaunchMatchedAnchorOfActiveMember;
            internal bool TransferMemberDescentContinuation;
            internal bool AcceptedTailSeed;
        }

        private static Recording Orbiting(string id)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Char " + id,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalOrbitEccentricity = 0.01,
                TerminalOrbitInclination = 0.3,
                TerminalOrbitEpoch = 100,
            };
        }

        private static Recording SegmentRecording(string id)
        {
            var rec = Orbiting(id);
            rec.TerminalOrbitBody = "Mun";
            rec.TerminalOrbitSemiMajorAxis = 260300;
            rec.Points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                new TrajectoryPoint { ut = 30000, bodyName = "Mun" }
            };
            rec.OrbitSegments = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    startUT = 400, endUT = 600, bodyName = "Kerbin", semiMajorAxis = 694160,
                    eccentricity = 0.05, inclination = 0.3, epoch = 400
                }
            };
            return rec;
        }

        private static Recording StateVectorRecording(string id)
        {
            return new Recording
            {
                RecordingId = id,
                TerminalStateValue = TerminalState.SubOrbital,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100, bodyName = "Mun", altitude = 3000,
                        velocity = new Vector3(0, 100, 0)
                    },
                    new TrajectoryPoint
                    {
                        ut = 200, bodyName = "Mun", altitude = 3200,
                        velocity = new Vector3(0, 110, 0)
                    }
                }
            };
        }

        private static IEnumerable<Case> Cases()
        {
            yield return new Case { Name = "null-trajectory", Build = () => null, CurrentUT = 500 };
            yield return new Case
            {
                Name = "debris",
                Build = () => { var r = SegmentRecording("debris"); r.IsDebris = true; return r; },
                CurrentUT = 500
            };
            yield return new Case
            {
                Name = "playback-toggle-off",
                Build = () => { var r = SegmentRecording("toggle-off"); r.PlaybackEnabled = false; return r; },
                CurrentUT = 500
            };
            yield return new Case
            {
                Name = "suppressed", Build = () => SegmentRecording("suppressed"),
                CurrentUT = 500, IsSuppressed = true
            };
            yield return new Case
            {
                Name = "live-anchor-double",
                Build = () => { var r = SegmentRecording("anchor-double"); r.VesselPersistentId = 4242u; return r; },
                CurrentUT = 500, AlreadyMaterialized = true, LoopMemberInWindow = true,
                LiveLaunchMatchedAnchorOfActiveMember = true
            };
            yield return new Case
            {
                Name = "already-spawned", Build = () => SegmentRecording("already-spawned"),
                CurrentUT = 500, AlreadyMaterialized = true
            };
            yield return new Case
            {
                Name = "loop-alongside-real-then-segment",
                Build = () => SegmentRecording("alongside"),
                CurrentUT = 500, AlreadyMaterialized = true, LoopMemberInWindow = true
            };
            yield return new Case
            {
                Name = "terminal-landed",
                Build = () => { var r = SegmentRecording("landed"); r.TerminalStateValue = TerminalState.Landed; return r; },
                CurrentUT = 500
            };
            yield return new Case
            {
                Name = "transfer-descent-continuation",
                Build = () => GhostMapEndpointTailTests.BuildEndpointRecording(
                    GhostMapEndpointTailTests.Segment(startUT: 120.0, endUT: 150.0)),
                CurrentUT = 135.7, TransferMemberDescentContinuation = true, AcceptedTailSeed = true
            };
            yield return new Case
            {
                Name = "covering-segment", Build = () => SegmentRecording("segment"), CurrentUT = 500
            };
            yield return new Case
            {
                Name = "covering-segment-with-checkpoint",
                Build = () => GhostMapSoiGapStateVectorTests.MakeSegmentAvailableRecording("seg-checkpoint", "Mun"),
                CurrentUT = 160.0, AllowSoiGapStateVectorFallback = true, ExpectedSoiGapBody = "Mun"
            };
            yield return new Case
            {
                Name = "endpoint-tail-override",
                Build = () => GhostMapEndpointTailTests.BuildEndpointRecording(
                    GhostMapEndpointTailTests.Segment(startUT: 120.0, endUT: 150.0)),
                CurrentUT = 135.7, AcceptedTailSeed = true
            };
            yield return new Case
            {
                Name = "soi-gap-checkpoint-accepted",
                Build = () => GhostMapSoiGapStateVectorTests.MakeSoiGapRecording("soi-accept", "Mun", "Mun"),
                CurrentUT = 160.0, AllowSoiGapStateVectorFallback = true, ExpectedSoiGapBody = "Mun"
            };
            yield return new Case
            {
                Name = "soi-gap-checkpoint-rejected",
                Build = () => GhostMapSoiGapStateVectorTests.MakeSoiGapRecording("soi-reject", "Mun", "Mun"),
                CurrentUT = 160.0, ExpectedSoiGapBody = "Mun"
            };
            yield return new Case
            {
                Name = "soi-gap-checkpoint-rejected-no-terminal-fallback",
                Build = () => GhostMapSoiGapStateVectorTests.MakeSoiGapRecording("soi-reject-nf", "Mun", "Mun"),
                CurrentUT = 160.0, ExpectedSoiGapBody = "Mun", AllowTerminalOrbitFallback = false
            };
            yield return new Case
            {
                Name = "state-vector", Build = () => StateVectorRecording("state-vector"),
                CurrentUT = 150, AllowTerminalOrbitFallback = false
            };
            yield return new Case
            {
                Name = "relative-state-vector-with-segments-elsewhere",
                Build = () =>
                {
                    var r = GhostMapPresenceTests.BuildRelativeFrameRecording("anchor-rec", 0.5, 540.0);
                    r.OrbitSegments = new List<OrbitSegment>
                    {
                        new OrbitSegment { startUT = 0, endUT = 50, bodyName = "Kerbin", semiMajorAxis = 700000 }
                    };
                    return r;
                },
                CurrentUT = 200, AllowTerminalOrbitFallback = false
            };
            yield return new Case
            {
                Name = "relative-segment-gap-deferred",
                Build = () => GhostMapPresenceTests.BuildRelativeFrameRecordingBetweenOrbitSegments(),
                CurrentUT = 200.0
            };
            yield return new Case
            {
                Name = "relative-segment-gap-deferred-no-terminal-fallback",
                Build = () => GhostMapPresenceTests.BuildRelativeFrameRecordingBetweenOrbitSegments(),
                CurrentUT = 200.0, AllowTerminalOrbitFallback = false
            };
            yield return new Case
            {
                Name = "no-terminal-fallback-no-orbit-data",
                Build = () => new Recording
                {
                    RecordingId = "no-fallback", TerminalStateValue = TerminalState.Orbiting,
                    TerminalOrbitBody = "Kerbin", TerminalOrbitSemiMajorAxis = 700000
                },
                CurrentUT = 500, AllowTerminalOrbitFallback = false
            };
            yield return new Case
            {
                Name = "state-vector-miss-no-orbit-data",
                Build = () => StateVectorRecording("sv-miss"), CurrentUT = 50
            };
            yield return new Case
            {
                Name = "terminal-suborbital-refused-for-terminal-orbit",
                Build = () =>
                {
                    var r = Orbiting("suborbital-terminal");
                    r.TerminalStateValue = TerminalState.SubOrbital;
                    r.ExplicitStartUT = 100;
                    r.ExplicitEndUT = 200;
                    return r;
                },
                CurrentUT = 500
            };
            yield return new Case
            {
                Name = "before-activation",
                Build = () =>
                {
                    var r = Orbiting("before-activation");
                    r.ExplicitStartUT = 1000;
                    r.ExplicitEndUT = 2000;
                    return r;
                },
                CurrentUT = 500
            };
            yield return new Case
            {
                Name = "before-terminal-orbit",
                Build = () =>
                {
                    var r = Orbiting("before-terminal");
                    r.ExplicitStartUT = 100;
                    r.ExplicitEndUT = 1000;
                    r.EndpointBodyName = "Kerbin";
                    r.TrackSections = new List<TrackSection>
                    {
                        new TrackSection
                        {
                            startUT = 100, endUT = 400, referenceFrame = ReferenceFrame.Absolute,
                            frames = new List<TrajectoryPoint>
                            {
                                new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                                new TrajectoryPoint { ut = 400, bodyName = "Kerbin" }
                            }
                        }
                    };
                    r.OrbitSegments = new List<OrbitSegment>
                    {
                        new OrbitSegment
                        {
                            startUT = 500, endUT = 1000, bodyName = "Kerbin",
                            semiMajorAxis = 700000, eccentricity = 0.01
                        }
                    };
                    return r;
                },
                CurrentUT = 300
            };
            yield return new Case
            {
                Name = "terminal-endpoint-tail",
                Build = () => GhostMapEndpointTailTests.BuildEndpointRecording(
                    GhostMapEndpointTailTests.Segment(startUT: 120.0, endUT: 150.0)),
                CurrentUT = 700.0, AcceptedTailSeed = true
            };
            yield return new Case
            {
                Name = "terminal-endpoint-tail-loop-synthesis",
                Build = () => GhostMapEndpointTailTests.BuildEndpointRecording(
                    GhostMapEndpointTailTests.Segment(startUT: 120.0, endUT: 150.0)),
                CurrentUT = 700.0, AcceptedTailSeed = true, AcceptTerminalOrbitForLoopSynthesis = true
            };
            yield return new Case
            {
                Name = "unseedable-endpoint-conflict",
                Build = () => new Recording
                {
                    RecordingId = "endpoint-conflict", TerminalOrbitBody = "Kerbin",
                    TerminalOrbitSemiMajorAxis = 700000, TerminalStateValue = TerminalState.Orbiting,
                    ExplicitEndUT = 200,
                    Points = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = 200, bodyName = "Mun", latitude = 1, longitude = 2, altitude = 3000 }
                    }
                },
                CurrentUT = 1000
            };
            yield return new Case
            {
                Name = "terminal-orbit",
                Build = () =>
                {
                    var r = Orbiting("terminal-orbit");
                    r.ExplicitStartUT = 100;
                    r.ExplicitEndUT = 200;
                    return r;
                },
                CurrentUT = 500
            };
            yield return new Case
            {
                Name = "chain-head-borrows-tip-segments",
                Build = () =>
                {
                    PredictedTailMapPresenceTests.RegisterSplitChain(out Recording head, out _);
                    return head;
                },
                CurrentUT = 1500.0
            };
            yield return new Case
            {
                Name = "chain-head-inside-own-span",
                Build = () =>
                {
                    PredictedTailMapPresenceTests.RegisterSplitChain(out Recording head, out _);
                    return head;
                },
                CurrentUT = 150.0
            };
            yield return new Case
            {
                Name = "no-log-operation-name",
                Build = () => SegmentRecording("silent"), CurrentUT = 500, RecordingIndex = -1
            };
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

            IPlaybackTrajectory trajectory = c.Build();
            logLines.Clear();
            int cachedIndex = -1;
            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveMapPresenceGhostSource(
                    trajectory,
                    c.IsSuppressed,
                    c.AlreadyMaterialized,
                    c.CurrentUT,
                    c.AllowTerminalOrbitFallback,
                    c.Name == "no-log-operation-name" ? null : "char-" + c.Name,
                    ref cachedIndex,
                    out OrbitSegment segment,
                    out TrajectoryPoint point,
                    out string skipReason,
                    recordingIndex: c.RecordingIndex,
                    allowSoiGapStateVectorFallback: c.AllowSoiGapStateVectorFallback,
                    expectedSoiGapBody: c.ExpectedSoiGapBody,
                    acceptTerminalOrbitForLoopSynthesis: c.AcceptTerminalOrbitForLoopSynthesis,
                    loopMemberInWindow: c.LoopMemberInWindow,
                    liveLaunchMatchedAnchorOfActiveMember: c.LiveLaunchMatchedAnchorOfActiveMember,
                    transferMemberDescentContinuation: c.TransferMemberDescentContinuation);

            var sb = new StringBuilder();
            sb.Append("== ").Append(c.Name).Append('\n');
            sb.Append("source=").Append(source)
                .Append(" skipReason=").Append(skipReason ?? "(null)")
                .Append(" cachedIndex=").Append(cachedIndex.ToString(Ic)).Append('\n');
            sb.Append("segment=").Append(segment.bodyName ?? "(null)")
                .Append(' ').Append(R(segment.startUT)).Append(' ').Append(R(segment.endUT))
                .Append(" sma=").Append(R(segment.semiMajorAxis))
                .Append(" ecc=").Append(R(segment.eccentricity))
                .Append(" inc=").Append(R(segment.inclination))
                .Append(" lan=").Append(R(segment.longitudeOfAscendingNode))
                .Append(" argPe=").Append(R(segment.argumentOfPeriapsis))
                .Append(" mEp=").Append(R(segment.meanAnomalyAtEpoch))
                .Append(" epoch=").Append(R(segment.epoch))
                .Append(" predicted=").Append(segment.isPredicted).Append('\n');
            sb.Append("point=").Append(point.bodyName ?? "(null)")
                .Append(' ').Append(R(point.ut))
                .Append(" lat=").Append(R(point.latitude))
                .Append(" lon=").Append(R(point.longitude))
                .Append(" alt=").Append(R(point.altitude))
                .Append(" speed=").Append(R(point.velocity.magnitude)).Append('\n');
            foreach (string line in logLines)
                sb.Append("log ").Append(line.Replace("\r", "\\r").Replace("\n", "\\n")).Append('\n');
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

        [Fact]
        public void EveryDecisionExit_MatchesTheGoldenCapture()
        {
            var sb = new StringBuilder();
            foreach (Case c in Cases())
                sb.Append(RunCase(c));
            string actual = sb.ToString();
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

        [Fact]
        public void TheCaptureReachesEveryDecisionExit()
        {
            // Each detail fragment below is written by exactly one exit of the resolver; the
            // corpus must keep reaching all of them or the golden stops covering that exit.
            var sb = new StringBuilder();
            foreach (Case c in Cases())
                sb.Append(RunCase(c));
            string all = sb.ToString();
            string[] exits =
            {
                "null trajectory", "isDebris=True", "playbackEnabled=False", "isSuppressed=True",
                "live-anchor double suppressed", "already spawned", "drawing map ghost alongside",
                "terminal=Landed", "descent-trigger transfer member handed off",
                "source=Segment", "stateVectorSource=OrbitalCheckpoint",
                "endpointTailOverride=stale-visible-segment", "source=StateVectorSoiGap",
                "source=StateVector ", "terminalFallback=False", "hasOrbitSegments=False",
                "terminalOrbitFallback=True", "activationStartUT=", "orbitSegmentGap=True",
                "source=EndpointTail", "seedFailure=", "source=TerminalOrbit",
                "map-presence chain-tip segments:", "before-terminal-orbit",
            };
            foreach (string fragment in exits)
                Assert.True(all.Contains(fragment), "no case reaches: " + fragment);
        }
    }
}
