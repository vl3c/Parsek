using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RecordingEndpointPersistenceTests
    {
        private static Recording BuildExactBoundaryBodyTransitionRecording(
            RecordingEndpointPhase endpointPhase,
            string endpointBodyName)
        {
            return new Recording
            {
                EndpointPhase = endpointPhase,
                EndpointBodyName = endpointBodyName,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 200.0, bodyName = "Kerbin" }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        startUT = 100.0,
                        endUT = 200.0,
                        bodyName = "Mun",
                        semiMajorAxis = 250000.0,
                        eccentricity = 0.01,
                        inclination = 1.0,
                        longitudeOfAscendingNode = 2.0,
                        argumentOfPeriapsis = 3.0,
                        meanAnomalyAtEpoch = 0.4,
                        epoch = 150.0
                    }
                }
            };
        }

        [Fact]
        public void SaveLoadRecordingMetadata_EndpointDecisionRoundTrips()
        {
            var source = new Recording
            {
                RecordingId = "endpoint-meta",
                EndpointPhase = RecordingEndpointPhase.OrbitSegment,
                EndpointBodyName = "Mun"
            };

            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(RecordingEndpointPhase.OrbitSegment, loaded.EndpointPhase);
            Assert.Equal("Mun", loaded.EndpointBodyName);
        }

        [Fact]
        public void RecordingTreeSaveLoad_EndpointDecisionRoundTrips()
        {
            var tree = new RecordingTree
            {
                Id = "tree-endpoint",
                TreeName = "Endpoint Test",
                RootRecordingId = "rec-endpoint"
            };
            tree.Recordings["rec-endpoint"] = new Recording
            {
                RecordingId = "rec-endpoint",
                VesselName = "Endpoint Vessel",
                EndpointPhase = RecordingEndpointPhase.TrajectoryPoint,
                EndpointBodyName = "Kerbin",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);

            RecordingTree restored = RecordingTree.Load(node);
            Recording restoredRec = restored.Recordings["rec-endpoint"];

            Assert.Equal(RecordingEndpointPhase.TrajectoryPoint, restoredRec.EndpointPhase);
            Assert.Equal("Kerbin", restoredRec.EndpointBodyName);
        }

        [Fact]
        public void ExactBoundaryCapture_PersistedOrbitPhaseOverridesPointFallback()
        {
            Recording rec = BuildExactBoundaryBodyTransitionRecording(
                RecordingEndpointPhase.OrbitSegment,
                "Mun");

            Assert.True(RecordingEndpointResolver.ShouldUseOrbitEndpoint(rec));
            Assert.True(RecordingEndpointResolver.TryGetOrbitEndpointUT(rec, out double endpointUT));
            Assert.Equal(200.0, endpointUT, 10);
            Assert.True(RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out string bodyName));
            Assert.Equal("Mun", bodyName);
        }

        [Fact]
        public void ExactBoundaryEscape_PersistedPointPhaseRejectsOrbitFallback()
        {
            Recording rec = BuildExactBoundaryBodyTransitionRecording(
                RecordingEndpointPhase.TrajectoryPoint,
                "Kerbin");

            Assert.False(RecordingEndpointResolver.ShouldUseOrbitEndpoint(rec));
            Assert.False(RecordingEndpointResolver.TryGetOrbitEndpointUT(rec, out _));
            Assert.True(RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out string bodyName));
            Assert.Equal("Kerbin", bodyName);
        }

        [Fact]
        public void LoadRecordingFiles_LegacyRecording_BackfillsEndpointDecisionFromTerminalOrbitAlignedSegment()
        {
            RecordingStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            string tempDir = Path.Combine(Path.GetTempPath(), "parsek-endpoint-backfill-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string precPath = Path.Combine(tempDir, "legacy-endpoint.prec");
                string vesselPath = Path.Combine(tempDir, "legacy-endpoint_vessel.craft");
                string ghostPath = Path.Combine(tempDir, "legacy-endpoint_ghost.craft");

                Recording source = BuildExactBoundaryBodyTransitionRecording(
                    RecordingEndpointPhase.Unknown,
                    null);
                source.RecordingId = "legacy-endpoint";
                RecordingStore.WriteTrajectorySidecar(precPath, source, sidecarEpoch: 1);

                var loaded = new Recording
                {
                    RecordingId = "legacy-endpoint",
                    TerminalStateValue = TerminalState.Orbiting,
                    TerminalOrbitBody = "Mun",
                    TerminalOrbitSemiMajorAxis = 250000.0,
                    TerminalOrbitEccentricity = 0.01,
                    TerminalOrbitInclination = 1.0,
                    TerminalOrbitLAN = 2.0,
                    TerminalOrbitArgumentOfPeriapsis = 3.0,
                    TerminalOrbitMeanAnomalyAtEpoch = 0.4,
                    TerminalOrbitEpoch = 150.0
                };

                Assert.True(RecordingStore.LoadRecordingFilesFromPathsForTesting(
                    loaded, precPath, vesselPath, ghostPath));

                Assert.Equal(RecordingEndpointPhase.OrbitSegment, loaded.EndpointPhase);
                Assert.Equal("Mun", loaded.EndpointBodyName);
                Assert.True(RecordingEndpointResolver.ShouldUseOrbitEndpoint(loaded));
                Assert.True(RecordingEndpointResolver.TryGetPreferredEndpointBodyName(loaded, out string bodyName));
                Assert.Equal("Mun", bodyName);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void LoadRecordingFiles_PersistedEndpointDecision_RemainsAuthoritative()
        {
            RecordingStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            string tempDir = Path.Combine(Path.GetTempPath(), "parsek-endpoint-authoritative-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string precPath = Path.Combine(tempDir, "authoritative-endpoint.prec");
                string vesselPath = Path.Combine(tempDir, "authoritative-endpoint_vessel.craft");
                string ghostPath = Path.Combine(tempDir, "authoritative-endpoint_ghost.craft");

                Recording source = BuildExactBoundaryBodyTransitionRecording(
                    RecordingEndpointPhase.Unknown,
                    null);
                source.RecordingId = "authoritative-endpoint";
                RecordingStore.WriteTrajectorySidecar(precPath, source, sidecarEpoch: 1);

                var loaded = new Recording
                {
                    RecordingId = "authoritative-endpoint",
                    EndpointPhase = RecordingEndpointPhase.TrajectoryPoint,
                    EndpointBodyName = "Kerbin",
                    TerminalStateValue = TerminalState.Orbiting,
                    TerminalOrbitBody = "Mun",
                    TerminalOrbitSemiMajorAxis = 250000.0,
                    TerminalOrbitEccentricity = 0.01,
                    TerminalOrbitInclination = 1.0,
                    TerminalOrbitLAN = 2.0,
                    TerminalOrbitArgumentOfPeriapsis = 3.0,
                    TerminalOrbitMeanAnomalyAtEpoch = 0.4,
                    TerminalOrbitEpoch = 150.0
                };

                Assert.True(RecordingStore.LoadRecordingFilesFromPathsForTesting(
                    loaded, precPath, vesselPath, ghostPath));

                Assert.Equal(RecordingEndpointPhase.TrajectoryPoint, loaded.EndpointPhase);
                Assert.Equal("Kerbin", loaded.EndpointBodyName);
                Assert.False(RecordingEndpointResolver.ShouldUseOrbitEndpoint(loaded));
                Assert.True(RecordingEndpointResolver.TryGetPreferredEndpointBodyName(loaded, out string bodyName));
                Assert.Equal("Kerbin", bodyName);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
