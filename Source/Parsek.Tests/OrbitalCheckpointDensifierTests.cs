using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class OrbitalCheckpointDensifierTests : IDisposable
    {
        private const double KerbinRadius = 600000.0;
        private const double KerbinMu = 3.5316e12;
        private const double LongWarpStartUT = 171496.6;
        private const double LongWarpEndUT = 193774.6;

        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;
        private readonly bool priorLogSuppress;
        private readonly bool priorStoreSuppress;

        public OrbitalCheckpointDensifierTests()
        {
            priorLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;

            tempDir = Path.Combine(
                Path.GetTempPath(),
                "parsek-orbital-checkpoint-densifier-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        [Fact]
        public void LongWarpOrbitalCheckpoint_DensifiesTrajectoryPoints()
        {
            Recording rec = BuildWarpRecording(LongWarpEndUT);

            int added = DensifySynthetic(rec);

            TrackSection section = rec.TrackSections[0];
            Assert.InRange(added, 40, 60);
            Assert.Equal(added, section.frames.Count);
            Assert.Equal(added, rec.Points.Count);
            Assert.True(section.frames[0].ut <= LongWarpStartUT + 0.001);
            Assert.True(section.frames[section.frames.Count - 1].ut >= LongWarpEndUT - 0.001);
            Assert.Contains(logLines, line =>
                line.Contains("[Recorder]")
                && line.Contains("OrbitalCheckpoint densified")
                && line.Contains("rec=571a-long-warp")
                && line.Contains("addedPoints=")
                && line.Contains("trueAnomalyStep=5deg"));
        }

        [Fact]
        public void ShortWarpOrbitalCheckpoint_DoesNotDensify()
        {
            Recording rec = BuildWarpRecording(LongWarpStartUT + 300.0);

            int added = DensifySynthetic(rec);

            Assert.Equal(0, added);
            Assert.Empty(rec.TrackSections[0].frames);
            Assert.Empty(rec.Points);
            Assert.Contains(logLines, line =>
                line.Contains("[Recorder]")
                && line.Contains("OrbitalCheckpoint densified")
                && line.Contains("rec=571a-long-warp")
                && line.Contains("addedPoints=0")
                && line.Contains("reason=below-duration-threshold"));
        }

        [Fact]
        public void CurrentFormatSidecar_RoundTripsDensifiedCheckpointPoints()
        {
            Recording original = BuildWarpRecording(LongWarpEndUT);
            int added = DensifySynthetic(original);
            Assert.InRange(added, 40, 60);

            string path = Path.Combine(tempDir, "densified-checkpoint.prec");
            RecordingStore.WriteTrajectorySidecar(path, original, sidecarEpoch: 571);

            TrajectorySidecarProbe probe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(path, out probe));
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, probe.FormatVersion);

            var restored = new Recording { RecordingId = original.RecordingId };
            RecordingStore.DeserializeTrajectorySidecar(path, probe, restored);

            Assert.Single(restored.TrackSections);
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, restored.TrackSections[0].referenceFrame);
            Assert.Equal(original.TrackSections[0].frames.Count, restored.TrackSections[0].frames.Count);
            Assert.Equal(original.Points.Count, restored.Points.Count);
            Assert.Equal(
                original.TrackSections[0].frames[0].ut,
                restored.TrackSections[0].frames[0].ut,
                precision: 3);
            Assert.Equal(
                original.TrackSections[0].frames[0].bodyName,
                restored.TrackSections[0].frames[0].bodyName);
            Assert.Equal(
                original.TrackSections[0].frames[original.TrackSections[0].frames.Count - 1].ut,
                restored.TrackSections[0].frames[restored.TrackSections[0].frames.Count - 1].ut,
                precision: 3);
        }

        [Fact]
        public void MapSourceLog_CheckpointPointsUseStructuredStateVectorDecision()
        {
            Recording rec = BuildWarpRecording(LongWarpEndUT);
            DensifySynthetic(rec);
            logLines.Clear();

            int cachedIndex = -1;
            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveMapPresenceGhostSource(
                    rec,
                    isSuppressed: false,
                    alreadyMaterialized: false,
                    currentUT: LongWarpStartUT + 1000.0,
                    allowTerminalOrbitFallback: true,
                    logOperationName: "571a-map-source-test",
                    ref cachedIndex,
                    out _,
                    out TrajectoryPoint stateVectorPoint,
                    out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.StateVector, source);
            Assert.Null(skipReason);
            Assert.Equal("Kerbin", stateVectorPoint.bodyName);
            Assert.Contains(logLines, line =>
                line.Contains("[GhostMap]")
                && line.Contains("571a-map-source-test")
                && line.Contains("rec=571a-long-warp")
                && line.Contains("source=StateVector")
                && line.Contains("stateVectorSource=OrbitalCheckpoint")
                && line.Contains("sourceKind=StateVector")
                && line.Contains("world="));
        }

        private static int DensifySynthetic(Recording rec)
        {
            return OrbitalCheckpointDensifier.DensifyRecording(
                rec,
                ResolveSyntheticBody,
                BuildSyntheticPoint,
                syncFlatTrajectory: true,
                logDecisions: true);
        }

        private static Recording BuildWarpRecording(double endUT)
        {
            var segment = new OrbitSegment
            {
                startUT = LongWarpStartUT,
                endUT = endUT,
                inclination = 0.0,
                eccentricity = 0.844672,
                semiMajorAxis = 4070696.0,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 1.185624,
                epoch = LongWarpStartUT,
                bodyName = "Kerbin"
            };

            var rec = new Recording
            {
                RecordingId = "571a-long-warp",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                VesselName = "Synthetic long-warp checkpoint",
                ExplicitStartUT = LongWarpStartUT,
                ExplicitEndUT = endUT
            };
            rec.OrbitSegments.Add(segment);
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                source = TrackSectionSource.Checkpoint,
                startUT = LongWarpStartUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment> { segment },
                minAltitude = float.NaN,
                maxAltitude = float.NaN
            });
            return rec;
        }

        private static bool ResolveSyntheticBody(
            string bodyName,
            out double bodyRadius,
            out double gravParameter)
        {
            bodyRadius = KerbinRadius;
            gravParameter = KerbinMu;
            return bodyName == "Kerbin";
        }

        private static bool BuildSyntheticPoint(
            OrbitSegment segment,
            double ut,
            double bodyRadius,
            double gravParameter,
            out TrajectoryPoint point)
        {
            double phase = (ut - segment.epoch) / 60.0;
            point = new TrajectoryPoint
            {
                ut = ut,
                latitude = Math.Sin(phase) * 5.0,
                longitude = phase % 360.0,
                altitude = Math.Max(0.0, segment.semiMajorAxis - bodyRadius),
                rotation = Quaternion.identity,
                velocity = new Vector3(0f, 2500f, 0f),
                bodyName = segment.bodyName,
                funds = 1.0,
                science = 2.0f,
                reputation = 3.0f
            };
            return true;
        }
    }
}
