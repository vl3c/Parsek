using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A vessel left on an escape trajectory spawns on its recorded path (owner ruling Q10,
    /// 2026-11-18; todo SPAWN-ESCAPE-TRAJECTORY-NEVER-SPAWNS): the escape evidence, the
    /// spawn decision, the patched-conic carry to the spawn UT and the safety periapsis.
    /// </summary>
    [Collection("Sequential")]
    public class EscapeTrajectorySpawnTests : IDisposable
    {
        private const double HomeRadius = 600000.0;
        private const double HomeAtmosphereDepth = 70000.0;
        private const double HomeGravParameter = 3.5316e12;
        private const double HomeSoi = 1500000.0;
        private const double StarRadius = 1000000.0;
        private const double StarGravParameter = 1.0e14;

        private readonly List<string> logLines = new List<string>();

        public EscapeTrajectorySpawnTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            EffectiveState.ResetRecoveredIndexForTesting();
        }

        public void Dispose()
        {
            EffectiveState.ResetRecoveredIndexForTesting();
            EffectiveState.ResetCachesForTesting();
            Ledger.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static ConfigNode Snapshot(string sit)
        {
            var node = new ConfigNode("VESSEL");
            if (sit != null)
                node.AddValue("sit", sit);
            node.AddValue("type", "Probe");
            return node;
        }

        /// <summary>A probe committed on a hyperbolic Kerbin escape (KSP sit ESCAPING).</summary>
        private static Recording HyperbolicEscapeProbe(string id = "rec-escape")
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Escape Probe",
                VesselPersistentId = 777u,
                VesselSnapshot = Snapshot("ESCAPING"),
                TerminalStateValue = TerminalState.SubOrbital,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = -2500000.0,
                TerminalOrbitEccentricity = 1.3,
                TerminalOrbitInclination = 0.0,
                TerminalOrbitLAN = 0.0,
                TerminalOrbitArgumentOfPeriapsis = 0.0,
                TerminalOrbitMeanAnomalyAtEpoch = 0.5,
                TerminalOrbitEpoch = 1000.0,
            };
        }

        /// <summary>A plain ballistic arc inside the sphere of influence (KSP sit SUB_ORBITAL).</summary>
        private static Recording BallisticArc()
        {
            Recording rec = HyperbolicEscapeProbe("rec-arc");
            rec.VesselSnapshot = Snapshot("SUB_ORBITAL");
            rec.TerminalOrbitSemiMajorAxis = 500000.0;
            rec.TerminalOrbitEccentricity = 0.3;
            return rec;
        }

        #region escape evidence

        [Fact]
        public void IsEscapeTerminal_OpenConic_IsAnEscape()
        {
            Recording rec = HyperbolicEscapeProbe();
            rec.VesselSnapshot = Snapshot("SUB_ORBITAL"); // stale snapshot: the conic decides

            Assert.True(Recording.IsEscapeTrajectoryTerminal(rec, out string evidence));
            Assert.Equal("open-conic", evidence);
        }

        [Fact]
        public void IsEscapeTerminal_ClosedConicWithEscapingSnapshot_IsAnEscape()
        {
            // A Mun escape: a closed conic whose apoapsis is beyond the sphere of influence,
            // which only KSP's own ESCAPING situation tells headless code.
            Recording rec = BallisticArc();
            rec.VesselSnapshot = Snapshot("ESCAPING");

            Assert.True(Recording.IsEscapeTrajectoryTerminal(rec, out string evidence));
            Assert.Equal("snapshot-escaping", evidence);
        }

        [Fact]
        public void IsEscapeTerminal_BallisticArc_IsNotAnEscape()
        {
            Assert.False(Recording.IsEscapeTrajectoryTerminal(BallisticArc()));
        }

        [Fact]
        public void IsEscapeTerminal_NeedsSubOrbitalTerminalAndAPropagatableOrbit()
        {
            Recording orbiting = HyperbolicEscapeProbe();
            orbiting.TerminalStateValue = TerminalState.Orbiting;
            Assert.False(Recording.IsEscapeTrajectoryTerminal(orbiting));

            Recording unfinalized = HyperbolicEscapeProbe();
            unfinalized.TerminalStateValue = null;
            Assert.False(Recording.IsEscapeTrajectoryTerminal(unfinalized));

            Recording noBody = HyperbolicEscapeProbe();
            noBody.TerminalOrbitBody = null;
            Assert.False(Recording.IsEscapeTrajectoryTerminal(noBody));

            Recording mixedSign = HyperbolicEscapeProbe();
            mixedSign.TerminalOrbitSemiMajorAxis = 2500000.0; // open conic with a positive a
            Assert.False(Recording.IsEscapeTrajectoryTerminal(mixedSign));

            Recording parabolic = HyperbolicEscapeProbe();
            parabolic.TerminalOrbitEccentricity = 1.0;
            Assert.False(Recording.IsEscapeTrajectoryTerminal(parabolic));

            Recording nan = HyperbolicEscapeProbe();
            nan.TerminalOrbitMeanAnomalyAtEpoch = double.NaN;
            Assert.False(Recording.IsEscapeTrajectoryTerminal(nan));

            Assert.False(Recording.IsEscapeTrajectoryTerminal(null));
        }

        #endregion

        #region spawn decision

        [Fact]
        public void ShouldSpawnAtRecordingEnd_EscapeTrajectory_Spawns()
        {
            // The finding: an escaping probe ended SubOrbital and never spawned.
            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                HyperbolicEscapeProbe(), false);

            Assert.True(needsSpawn, reason);
            Assert.Equal("", reason);
            Assert.Contains(logLines, l => l.Contains("[Spawner]")
                && l.Contains("Escape-trajectory terminal admitted for spawn")
                && l.Contains("evidence=open-conic"));
        }

        [Fact]
        public void ShouldSpawnAtRecordingEnd_EscapeWithStaleSubOrbitalSnapshot_StillSpawns()
        {
            Recording rec = HyperbolicEscapeProbe();
            rec.VesselSnapshot = Snapshot("SUB_ORBITAL");

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false);

            Assert.True(needsSpawn, reason);
        }

        [Fact]
        public void ShouldSpawnAtRecordingEnd_BallisticArc_StillNeverSpawns()
        {
            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(BallisticArc(), false);

            Assert.False(needsSpawn);
            Assert.Equal("terminal state SubOrbital", reason);
        }

        [Fact]
        public void ShouldSpawnAtKscEnd_EscapeTrajectory_DefersToFlightScene()
        {
            Recording rec = HyperbolicEscapeProbe();
            rec.ExplicitEndUT = 1000.0;

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, 2000.0);

            Assert.False(needsSpawn);
            Assert.Contains("deferred to flight scene", reason);
            Assert.Contains("escape trajectory", reason);
        }

        [Fact]
        public void SpawnRoutes_EscapeTrajectory_UseTheRecordedTerminalOrbit()
        {
            Recording rec = HyperbolicEscapeProbe();

            Assert.True(VesselSpawner.HasRecordedTerminalOrbit(rec));
            Assert.True(VesselSpawner.ShouldUseRecordedTerminalOrbitSpawnState(rec));
            Assert.True(VesselSpawner.ShouldRouteThroughSpawnAtPosition(rec));

            Recording arc = BallisticArc();
            Assert.False(VesselSpawner.ShouldUseRecordedTerminalOrbitSpawnState(arc));
            Assert.False(VesselSpawner.ShouldRouteThroughSpawnAtPosition(arc));
        }

        #endregion

        #region spawn orbit and safety

        [Fact]
        public void ComputeSpawnMeanAnomaly_OpenConic_GrowsWithoutWrapping()
        {
            const double a = 2500000.0;
            double n = Math.Sqrt(HomeGravParameter / (a * a * a));
            double expected = 0.5 + n * 50000.0;

            double actual = VesselSpawner.ComputeSpawnMeanAnomaly(0.5, 1000.0, -a, HomeGravParameter, 51000.0);

            Assert.True(expected > 2.0 * Math.PI, "test needs a mean anomaly past one turn");
            Assert.Equal(expected, actual, 9);
        }

        [Theory]
        // Outbound past periapsis and leaving: the lowest remaining point is here.
        [InlineData(-50000.0, 400000.0, true, false, 400000.0)]
        // Still approaching periapsis: it passes through it first.
        [InlineData(-50000.0, 400000.0, true, true, -50000.0)]
        // Stays inside the sphere of influence: it comes back down.
        [InlineData(-50000.0, 400000.0, false, false, -50000.0)]
        public void ResolveEscapeEffectivePeriapsisAltitude_Cases(
            double periapsis, double current, bool leaves, bool approaching, double expected)
        {
            Assert.Equal(expected,
                VesselSpawner.ResolveEscapeEffectivePeriapsisAltitude(periapsis, current, leaves, approaching));
        }

        [Fact]
        public void EscapeSafety_OutboundDirectAscentEscape_SpawnsInboundGrazer_IsRefused()
        {
            // A direct ascent escape: periapsis inside the planet, but outbound and leaving.
            double outbound = VesselSpawner.ResolveEscapeEffectivePeriapsisAltitude(
                -200000.0, 300000.0, leavesSphereOfInfluence: true, approachingPeriapsis: false);
            TerminalOrbitSpawnSafetyDecision spawn = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                true, HomeAtmosphereDepth, 300000.0, outbound, double.PositiveInfinity);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.SpawnNow, spawn.Action);

            // The same conic flown inbound dives into the atmosphere: refused.
            double inbound = VesselSpawner.ResolveEscapeEffectivePeriapsisAltitude(
                -200000.0, 300000.0, leavesSphereOfInfluence: true, approachingPeriapsis: true);
            TerminalOrbitSpawnSafetyDecision refuse = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                true, HomeAtmosphereDepth, 300000.0, inbound, double.PositiveInfinity);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.CannotSpawnSafely, refuse.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonPeriapsisBelowSafeAltitude, refuse.ReasonCode);
        }

        [Theory]
        [InlineData(-1000000.0, 1.2, 100.0, true)]
        [InlineData(1000000.0, 0.5, 1400000.0, true)]
        [InlineData(1000000.0, 0.3, 1400000.0, false)]
        [InlineData(1000000.0, 0.5, 0.0, false)]
        [InlineData(double.NaN, 1.5, 1400000.0, false)]
        public void ConicLeavesSphereOfInfluence_Cases(double sma, double ecc, double soi, bool expected)
        {
            Assert.Equal(expected, VesselSpawner.ConicLeavesSphereOfInfluence(sma, ecc, soi));
        }

        #endregion

        #region patched-conic carry to the spawn UT

        private static Dictionary<string, ExtrapolationBody> HomeAndStar()
        {
            return new Dictionary<string, ExtrapolationBody>
            {
                ["Star"] = new ExtrapolationBody
                {
                    Name = "Star",
                    GravitationalParameter = StarGravParameter,
                    Radius = StarRadius,
                },
                ["Home"] = new ExtrapolationBody
                {
                    Name = "Home",
                    ParentBodyName = "Star",
                    GravitationalParameter = HomeGravParameter,
                    Radius = HomeRadius,
                    AtmosphereDepth = HomeAtmosphereDepth,
                    SphereOfInfluence = HomeSoi,
                    ParentFrameState = (double ut, out Vector3d position, out Vector3d velocity) =>
                    {
                        position = new Vector3d(200000000.0, 0.0, 0.0);
                        velocity = new Vector3d(0.0, 200.0, 0.0);
                    },
                },
            };
        }

        /// <summary>The Home conic of a state at 100 km, as a terminal orbit at UT 0.</summary>
        private static OrbitSegment HomeTerminal(Vector3d velocity)
        {
            var start = new BallisticStateVector
            {
                ut = 0.0,
                bodyName = "Home",
                position = new Vector3d(HomeRadius + 100000.0, 0.0, 0.0),
                velocity = velocity,
            };
            ExtrapolationResult first = BallisticExtrapolator.Extrapolate(
                start,
                HomeAndStar(),
                new ExtrapolationLimits { maxHorizonYears = 1.0 / BallisticExtrapolator.SecondsPerYear, maxSoiTransitions = 4, soiSampleStep = 60.0 },
                warnOnSubSurfaceStart: false);
            Assert.NotEmpty(first.segments);
            OrbitSegment seg = first.segments[0];
            Assert.Equal("Home", seg.bodyName);
            seg.startUT = seg.epoch;
            seg.endUT = seg.epoch;
            return seg;
        }

        [Fact]
        public void ResolveAtUT_InsideTheSphere_KeepsTheRecordedConic()
        {
            OrbitSegment terminal = HomeTerminal(new Vector3d(0.0, 3600.0, 0.0));

            EscapeSpawnConicResolution r = EscapeTrajectoryTerminal.ResolveAtUT(
                terminal, HomeAndStar(), terminal.epoch + 30.0);

            Assert.True(r.Resolved, r.RefusalReason);
            Assert.Equal("Home", r.BodyName);
            Assert.Equal(0, r.SoiTransitions);
            Assert.Equal(terminal.semiMajorAxis, r.Segment.semiMajorAxis);
            Assert.Equal(terminal.meanAnomalyAtEpoch, r.Segment.meanAnomalyAtEpoch);
        }

        [Fact]
        public void ResolveAtUT_AfterTheSphereExit_SpawnsInTheParentSphere()
        {
            OrbitSegment terminal = HomeTerminal(new Vector3d(0.0, 3600.0, 0.0));
            Assert.True(terminal.eccentricity > 1.0, "the terminal conic must be open");

            EscapeSpawnConicResolution r = EscapeTrajectoryTerminal.ResolveAtUT(
                terminal, HomeAndStar(), terminal.epoch + 5000.0);

            Assert.True(r.Resolved, r.RefusalReason);
            Assert.Equal("Star", r.BodyName);
            Assert.Equal(1, r.SoiTransitions);
            Assert.Equal("Star", r.Segment.bodyName);
            Assert.True(r.Segment.startUT <= terminal.epoch + 5000.0);
        }

        [Fact]
        public void ResolveAtUT_PathDivesIntoTheAtmosphereBeforeSpawn_IsRefused()
        {
            // Inbound and fast: an open conic whose periapsis is deep in the atmosphere.
            OrbitSegment terminal = HomeTerminal(new Vector3d(-3000.0, 1500.0, 0.0));
            Assert.True(terminal.eccentricity > 1.0, "the terminal conic must be open");

            EscapeSpawnConicResolution r = EscapeTrajectoryTerminal.ResolveAtUT(
                terminal, HomeAndStar(), terminal.epoch + 5000.0);

            Assert.False(r.Resolved);
            Assert.Equal(EscapeTrajectoryTerminal.RefusalImpactsBeforeSpawn, r.RefusalReason);
            Assert.True(r.RefusalUT < terminal.epoch + 5000.0);
            Assert.Contains("reason=" + EscapeTrajectoryTerminal.RefusalImpactsBeforeSpawn,
                EscapeTrajectoryTerminal.Describe(r, terminal.epoch + 5000.0));
        }

        [Fact]
        public void ResolveAtUT_UnknownBody_IsRefused()
        {
            OrbitSegment terminal = HomeTerminal(new Vector3d(0.0, 3600.0, 0.0));
            terminal.bodyName = "Nowhere";

            EscapeSpawnConicResolution r = EscapeTrajectoryTerminal.ResolveAtUT(
                terminal, HomeAndStar(), terminal.epoch + 100.0);

            Assert.False(r.Resolved);
            Assert.Equal(EscapeTrajectoryTerminal.RefusalMissingBody, r.RefusalReason);
        }

        #endregion
    }
}
