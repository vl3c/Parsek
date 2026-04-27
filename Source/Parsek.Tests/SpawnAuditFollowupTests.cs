using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class SpawnAuditFollowupTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly VesselSpawner.ResolveBodyNameByIndexDelegate originalBodyNameResolver;
        private readonly VesselSpawner.ResolveBodyByNameDelegate originalBodyResolver;
        private readonly VesselSpawner.ResolveBodyIndexDelegate originalBodyIndexResolver;

        public SpawnAuditFollowupTests()
        {
            originalBodyNameResolver = VesselSpawner.BodyNameResolverForTesting;
            originalBodyResolver = VesselSpawner.BodyResolverForTesting;
            originalBodyIndexResolver = VesselSpawner.BodyIndexResolverForTesting;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            VesselSpawner.BodyNameResolverForTesting = originalBodyNameResolver;
            VesselSpawner.BodyResolverForTesting = originalBodyResolver;
            VesselSpawner.BodyIndexResolverForTesting = originalBodyIndexResolver;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void CleanupFailedSpawnedProtoVessel_DestroyFailureStillRemovesAndLogsWarning()
        {
            var pv = (ProtoVessel)FormatterServices.GetUninitializedObject(typeof(ProtoVessel));
            var protoVessels = new List<ProtoVessel> { pv };

            VesselSpawner.CleanupFailedSpawnedProtoVessel(
                pv,
                "Spawner",
                "unit-test cleanup",
                protoVessels,
                _ => throw new InvalidOperationException("boom"));

            Assert.Empty(protoVessels);
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]") &&
                l.Contains("unit-test cleanup") &&
                l.Contains("failed to destroy partially spawned vessel") &&
                l.Contains("boom"));
        }

        [Fact]
        public void BuildValidatedRespawnSnapshot_PreparedSnapshotHappyPath_RepairsCopyOnly()
        {
            TestBodyRegistry.Install(("Kerbin", 600000.0, 3.5316e12), ("Mun", 200000.0, 6.5138398e10));
            VesselSpawner.BodyNameResolverForTesting = TestBodyRegistry.ResolveBodyNameByIndex;
            VesselSpawner.BodyResolverForTesting = TestBodyRegistry.ResolveBodyByName;
            VesselSpawner.BodyIndexResolverForTesting = TestBodyRegistry.ResolveBodyIndex;

            var preparedSnapshot = new ConfigNode("VESSEL");
            preparedSnapshot.AddValue("sit", "LANDED");
            preparedSnapshot.AddValue("lat", "1.0");
            preparedSnapshot.AddValue("lon", "2.0");
            preparedSnapshot.AddValue("alt", "3.0");
            preparedSnapshot.AddNode("PART").AddValue("name", "probeCoreOcto");
            var orbitNode = new ConfigNode("ORBIT");
            orbitNode.AddValue("SMA", "700000");
            orbitNode.AddValue("ECC", "0.01");
            orbitNode.AddValue("INC", "0.0");
            orbitNode.AddValue("LPE", "0.0");
            orbitNode.AddValue("LAN", "0.0");
            orbitNode.AddValue("MNA", "0.0");
            orbitNode.AddValue("EPH", "100.0");
            orbitNode.AddValue("REF", "0");
            preparedSnapshot.AddNode(orbitNode);

            var rec = new Recording
            {
                VesselName = "Prepared Surface Repair",
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Landed,
                EndpointPhase = RecordingEndpointPhase.TerminalPosition,
                EndpointBodyName = "Mun",
                TerminalPosition = new SurfacePosition
                {
                    body = "Mun",
                    latitude = 4.0,
                    longitude = 5.0,
                    altitude = 6.0
                }
            };

            ConfigNode validated = VesselSpawner.BuildValidatedRespawnSnapshot(
                preparedSnapshot,
                rec,
                currentUT: 123.0,
                logContext: "prepared-snapshot");

            Assert.NotNull(validated);
            Assert.NotSame(preparedSnapshot, validated);
            Assert.Equal("1.0", preparedSnapshot.GetValue("lat"));
            Assert.Equal("2.0", preparedSnapshot.GetValue("lon"));
            Assert.Equal("3.0", preparedSnapshot.GetValue("alt"));
            Assert.Equal("0", preparedSnapshot.GetNode("ORBIT").GetValue("REF"));
            Assert.Equal("4", validated.GetValue("lat"));
            Assert.Equal("5", validated.GetValue("lon"));
            Assert.Equal("6", validated.GetValue("alt"));
            Assert.Equal("1", validated.GetNode("ORBIT").GetValue("REF"));
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]") &&
                l.Contains("using endpoint surface coordinates") &&
                l.Contains("prepared-snapshot"));
        }

        [Fact]
        public void ApplyResolvedSpawnStateToSnapshot_EvaRecording_OverridesCoordsRotationAndStripsLadder()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("lat", "0");
            snapshot.AddValue("lon", "0");
            snapshot.AddValue("alt", "0");
            snapshot.AddValue("rot", "0,0,0,1");
            var part = snapshot.AddNode("PART");
            var module = part.AddNode("MODULE");
            module.AddValue("name", "KerbalEVA");
            module.AddValue("state", "Ladder (Acquire)");
            module.AddValue("OnALadder", "True");

            var rec = new Recording
            {
                VesselName = "Val",
                EvaCrewName = "Val",
                TerminalStateValue = TerminalState.Landed
            };
            var rotationPoint = new TrajectoryPoint
            {
                bodyName = "Kerbin",
                rotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f)
            };

            VesselSpawner.ApplyResolvedSpawnStateToSnapshot(
                snapshot,
                rec,
                rotationPoint,
                10.5,
                20.5,
                30.5,
                7,
                "unit-test resolved snapshot");

            Assert.Equal("10.5", snapshot.GetValue("lat"));
            Assert.Equal("20.5", snapshot.GetValue("lon"));
            Assert.Equal("30.5", snapshot.GetValue("alt"));
            Assert.False(module.HasValue("state"));
            Assert.Equal("False", module.GetValue("OnALadder"));
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]") &&
                l.Contains("rot updated from surface-relative frame") &&
                l.Contains("unit-test resolved snapshot"));
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]") &&
                l.Contains("ladder state stripped") &&
                l.Contains("Val"));
        }

        [Fact]
        public void ApplyResolvedSpawnStateToSnapshot_StripEvaDisabled_LeavesLadderState()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            var module = part.AddNode("MODULE");
            module.AddValue("name", "KerbalEVA");
            module.AddValue("state", "Ladder (Acquire)");
            module.AddValue("OnALadder", "True");

            var rec = new Recording
            {
                VesselName = "Val",
                EvaCrewName = "Val",
                TerminalStateValue = TerminalState.Landed
            };

            VesselSpawner.ApplyResolvedSpawnStateToSnapshot(
                snapshot,
                rec,
                rotationPoint: null,
                spawnLat: 1.0,
                spawnLon: 2.0,
                spawnAlt: 3.0,
                index: 8,
                logContext: "unit-test no-strip",
                allowPreferredRotation: false,
                stripEvaLadder: false);

            Assert.Equal("Ladder (Acquire)", module.GetValue("state"));
            Assert.Equal("True", module.GetValue("OnALadder"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Spawner]") &&
                l.Contains("ladder state stripped") &&
                l.Contains("Val"));
        }
    }
}
