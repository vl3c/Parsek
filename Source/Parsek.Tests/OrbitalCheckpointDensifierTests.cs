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

        [Fact]
        public void LongMultiSegmentCheckpoint_DensificationCapIsSpreadToSectionEnd()
        {
            Recording rec = BuildCappedMultiSegmentRecording();

            int added = DensifySynthetic(rec);

            TrackSection section = rec.TrackSections[0];
            Assert.Equal(OrbitalCheckpointDensifier.MaxAddedPointsPerSection, added);
            Assert.Equal(OrbitalCheckpointDensifier.MaxAddedPointsPerSection, section.frames.Count);
            Assert.True(section.frames[section.frames.Count - 1].ut >= section.endUT - 0.001);
            Assert.Contains(section.frames, p => p.ut > section.checkpoints[1].startUT);
            Assert.Contains(logLines, line =>
                line.Contains("[Recorder]")
                && line.Contains("OrbitalCheckpoint densified")
                && line.Contains("rec=571a-capped-multi-segment")
                && line.Contains("addedPoints=360")
                && line.Contains("capped=True"));
        }

        [Fact]
        public void ManySegmentCheckpoint_CapBudgetDoesNotFrontLoad()
        {
            const int extraSegments = 5;
            Recording rec = BuildManySegmentCappedRecording(
                OrbitalCheckpointDensifier.MaxAddedPointsPerSection + extraSegments);

            int added = DensifySynthetic(rec);

            TrackSection section = rec.TrackSections[0];
            int firstRetainedSegmentIndex = section.checkpoints.Count - (OrbitalCheckpointDensifier.MaxAddedPointsPerSection - 1);
            Assert.Equal(OrbitalCheckpointDensifier.MaxAddedPointsPerSection, added);
            Assert.Equal(OrbitalCheckpointDensifier.MaxAddedPointsPerSection, section.frames.Count);
            Assert.True(section.frames[0].ut >= section.checkpoints[firstRetainedSegmentIndex].startUT - 0.001);
            Assert.True(section.frames[section.frames.Count - 1].ut >= section.endUT - 0.001);
            Assert.DoesNotContain(section.frames, point => point.ut < section.checkpoints[firstRetainedSegmentIndex].startUT - 0.001);
        }

        [Fact]
        public void OptimizerTrimCheckpointFrames_PreservesInterpolatedTrimEndpoint()
        {
            var frames = new List<TrajectoryPoint>
            {
                BuildPointForTrim(100.0, 10.0, 20.0, 1000.0, 10.0, 1.0f, 2.0f, Quaternion.identity),
                BuildPointForTrim(200.0, 30.0, 60.0, 3000.0, 30.0, 5.0f, 6.0f, new Quaternion(0f, 1f, 0f, 0f)),
                BuildPointForTrim(250.0, 50.0, 90.0, 5000.0, 50.0, 9.0f, 10.0f, new Quaternion(0f, 1f, 0f, 0f))
            };

            RecordingOptimizer.TrimCheckpointFramesAtUT(frames, 150.0);

            Assert.Equal(2, frames.Count);
            Assert.Equal(100.0, frames[0].ut, precision: 3);
            TrajectoryPoint trimmed = frames[1];
            Assert.Equal(150.0, trimmed.ut, precision: 3);
            Assert.Equal(20.0, trimmed.latitude, precision: 3);
            Assert.Equal(40.0, trimmed.longitude, precision: 3);
            Assert.Equal(2000.0, trimmed.altitude, precision: 3);
            Assert.Equal(20.0, trimmed.funds, precision: 3);
            Assert.Equal(3.0f, trimmed.science, precision: 3);
            Assert.Equal(4.0f, trimmed.reputation, precision: 3);
            Assert.Equal("Kerbin", trimmed.bodyName);
            Assert.Equal(150f, trimmed.velocity.x, precision: 3);
            Assert.True(trimmed.rotation.y > 0.6f, "trim endpoint should slerp rotation toward the after frame");
            Assert.True(trimmed.rotation.w > 0.6f, "trim endpoint should keep a normalized halfway rotation");
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

        private static Recording BuildCappedMultiSegmentRecording()
        {
            const double sma = 1200000.0;
            double period = 2.0 * Math.PI * Math.Sqrt((sma * sma * sma) / KerbinMu);
            double firstStart = LongWarpStartUT;
            double firstEnd = firstStart + period * 10.0;
            double secondStart = firstEnd + 10.0;
            double secondEnd = secondStart + period * 10.0;

            OrbitSegment first = BuildCircularSegment(firstStart, firstEnd, sma);
            OrbitSegment second = BuildCircularSegment(secondStart, secondEnd, sma);

            var rec = new Recording
            {
                RecordingId = "571a-capped-multi-segment",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                VesselName = "Synthetic capped checkpoint",
                ExplicitStartUT = firstStart,
                ExplicitEndUT = secondEnd
            };
            rec.OrbitSegments.Add(first);
            rec.OrbitSegments.Add(second);
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                source = TrackSectionSource.Checkpoint,
                startUT = firstStart,
                endUT = secondEnd,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment> { first, second },
                minAltitude = float.NaN,
                maxAltitude = float.NaN
            });
            return rec;
        }

        private static Recording BuildManySegmentCappedRecording(int segmentCount)
        {
            const double sma = 1200000.0;
            const double segmentDuration = OrbitalCheckpointDensifier.MinDensifyDurationSeconds + 10.0;
            const double segmentGap = 1.0;
            double firstStart = LongWarpStartUT;

            var rec = new Recording
            {
                RecordingId = "571a-many-segment-budget",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                VesselName = "Synthetic many segment checkpoint",
                ExplicitStartUT = firstStart
            };

            var checkpoints = new List<OrbitSegment>();
            for (int i = 0; i < segmentCount; i++)
            {
                double startUT = firstStart + i * (segmentDuration + segmentGap);
                double endUT = startUT + segmentDuration;
                OrbitSegment segment = BuildCircularSegment(startUT, endUT, sma);
                rec.OrbitSegments.Add(segment);
                checkpoints.Add(segment);
                rec.ExplicitEndUT = endUT;
            }

            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                source = TrackSectionSource.Checkpoint,
                startUT = firstStart,
                endUT = rec.ExplicitEndUT,
                frames = new List<TrajectoryPoint>(),
                checkpoints = checkpoints,
                minAltitude = float.NaN,
                maxAltitude = float.NaN
            });
            return rec;
        }

        private static OrbitSegment BuildCircularSegment(double startUT, double endUT, double semiMajorAxis)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                inclination = 0.0,
                eccentricity = 0.0,
                semiMajorAxis = semiMajorAxis,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = startUT,
                bodyName = "Kerbin"
            };
        }

        private static TrajectoryPoint BuildPointForTrim(
            double ut,
            double latitude,
            double longitude,
            double altitude,
            double funds,
            float science,
            float reputation,
            Quaternion rotation)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = latitude,
                longitude = longitude,
                altitude = altitude,
                rotation = rotation,
                velocity = new Vector3((float)ut, 1f, 2f),
                bodyName = "Kerbin",
                funds = funds,
                science = science,
                reputation = reputation
            };
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
