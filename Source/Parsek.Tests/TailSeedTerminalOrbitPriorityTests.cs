using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class TailSeedTerminalOrbitPriorityTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TailSeedTerminalOrbitPriorityTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            OrbitSeedResolver.ResetForTesting();
        }

        // Values copied verbatim from harness/fixtures/saves/bdock-recorded:
        // Parsek/Recordings/4af6cfd725d646ccbac9ef2f7749667e.prec.txt (last BODY_FIXED_POINT of the
        // Relative section anchored to 37d0...) and the tOrb* block of persistent.sfs.
        private static Recording BuildBdockInterceptorLeaf()
        {
            var rec = new Recording { RecordingId = "4af6cfd725d646ccbac9ef2f7749667e" };
            var bf = new TrajectoryPoint
            {
                ut = 8951.4879785489447,
                latitude = 0.021392957561275794,
                longitude = -77.6076554440825,
                altitude = 116935.11297172075,
                bodyName = "Kerbin",
                velocity = new Vector3(-880.500366f, 3.03128576f, -2024.92822f),
                rotation = Quaternion.identity,
            };
            var local = bf;
            local.latitude = -0.00022183622058946639;
            local.longitude = 2.9001181125640869;
            local.altitude = -0.09994342178106308;
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Relative,
                startUT = 8950.6079785489255,
                endUT = 8951.547978548946,
                anchorRecordingId = "37d0dc074351408ba0374230793abb1c",
                frames = new List<TrajectoryPoint> { local },
                bodyFixedFrames = new List<TrajectoryPoint> { bf },
                checkpoints = new List<OrbitSegment>(),
            });
            rec.Points.Add(local);
            rec.TerminalStateValue = TerminalState.Orbiting;
            rec.TerminalOrbitBody = "Kerbin";
            rec.TerminalOrbitInclination = 0.081570830567265676;
            rec.TerminalOrbitEccentricity = 0.010585799947378403;
            rec.TerminalOrbitSemiMajorAxis = 709680.88346381392;
            rec.TerminalOrbitLAN = 146.73011743401088;
            rec.TerminalOrbitArgumentOfPeriapsis = 180.27518621987687;
            rec.TerminalOrbitMeanAnomalyAtEpoch = 3.40783358412598;
            rec.TerminalOrbitEpoch = 8951.547978548946;
            return rec;
        }

        [Fact]
        public void TailSeed_LiveCapturedTerminalOrbitNewerThanTail_DeclinesAndEndpointSeedIsTerminalOrbit()
        {
            CelestialBody body = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            Recording rec = BuildBdockInterceptorLeaf();

            bool tailWon = VesselSpawner.TryDeriveTerminalOrbitSeedFromTrajectoryTail(
                rec, body, spawnUT: 8960.0,
                out _, out _, out _, out _, out _, out _, out _, out _,
                out string declineReason);

            Assert.False(tailWon);
            Assert.Equal("terminal-orbit-newer-than-tail", declineReason);
            Assert.Contains(logLines, l => l.Contains("[Spawner]")
                && l.Contains("reason=terminal-orbit-newer-than-tail")
                && l.Contains("tailUT=8951.49")
                && l.Contains("terminalOrbitEpoch=8951.55"));

            Assert.True(VesselSpawner.TryGetEndpointAlignedRecordedOrbitSeedForSpawn(
                rec, out _, out double ecc, out double sma, out _, out _, out _, out _, out string seedBody));
            Assert.Equal("Kerbin", seedBody);
            Assert.InRange(ecc, 0.0105, 0.0107);
            Assert.InRange(sma, 709680.0, 709682.0);
        }

        [Fact]
        public void TailSeed_SegmentCopiedTerminalOrbitOlderThanTail_StillLetsTailProceed()
        {
            CelestialBody body = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            Recording rec = BuildBdockInterceptorLeaf();
            rec.TerminalOrbitEpoch = 8000.0; // pre-burn segment epoch: stale, the tail must still win
            bool tailWon = VesselSpawner.TryDeriveTerminalOrbitSeedFromTrajectoryTail(
                rec, body, spawnUT: 8960.0,
                out _, out _, out _, out _, out _, out _, out _, out _,
                out string declineReason);
            Assert.NotEqual("terminal-orbit-newer-than-tail", declineReason);
        }
    }
}
