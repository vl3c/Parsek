using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class TrajectorySidecarBinaryTests : IDisposable
    {
        private readonly string tempDir;

        public TrajectorySidecarBinaryTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            tempDir = Path.Combine(
                Path.GetTempPath(),
                "parsek-trajectory-sidecar-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Theory]
        [InlineData(2, (int)TrajectorySidecarEncoding.BinaryV2, true)]
        [InlineData(3, (int)TrajectorySidecarEncoding.BinaryV3, true)]
        [InlineData(4, (int)TrajectorySidecarEncoding.BinaryV3, true)]
        [InlineData(5, (int)TrajectorySidecarEncoding.BinaryV3, true)]
        [InlineData(6, (int)TrajectorySidecarEncoding.BinaryV3, true)]
        [InlineData(7, (int)TrajectorySidecarEncoding.BinaryV3, true)]
        [InlineData(99, (int)TrajectorySidecarEncoding.BinaryV3, false)]
        public void TryProbe_MapsVersionToEncodingAndSupport(
            int version,
            int expectedEncoding,
            bool expectedSupported)
        {
            string recordingId = "probe-" + version;
            string path = Path.Combine(tempDir, recordingId + ".prec");
            WriteProbeOnlyBinary(path, version, sidecarEpoch: 42, recordingId);

            TrajectorySidecarProbe probe;
            Assert.True(TrajectorySidecarBinary.TryProbe(path, out probe));
            Assert.True(probe.Success);
            Assert.Equal((TrajectorySidecarEncoding)expectedEncoding, probe.Encoding);
            Assert.Equal(version, probe.FormatVersion);
            Assert.Equal(42, probe.SidecarEpoch);
            Assert.Equal(recordingId, probe.RecordingId);
            Assert.Equal(expectedSupported, probe.Supported);

            if (expectedSupported)
                Assert.Null(probe.FailureReason);
            else
                Assert.Equal($"unsupported binary trajectory version {version}", probe.FailureReason);
        }

        [Fact]
        public void Read_DoesNotDemoteHigherInMemoryVersion_AndBackfillsMissingRecordingId()
        {
            var original = BuildSimpleFlatFixture();
            original.RecordingId = "promote-not-demote";
            original.RecordingFormatVersion = 3;

            string path = Path.Combine(tempDir, "promote-not-demote.prec");
            TrajectorySidecarBinary.Write(path, original, sidecarEpoch: 7);

            TrajectorySidecarProbe probe;
            Assert.True(TrajectorySidecarBinary.TryProbe(path, out probe));
            Assert.Equal(3, probe.FormatVersion);

            var restored = new Recording
            {
                RecordingId = null,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion
            };
            TrajectorySidecarBinary.Read(path, restored, probe);

            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, restored.RecordingFormatVersion);
            Assert.Equal(original.RecordingId, restored.RecordingId);
            AssertTrajectoryPayloadEqual(original, restored);
        }

        [Fact]
        public void WriteRead_SectionAuthoritativeFixture_RebuildsFlatPayloadFromTrackSections()
        {
            Recording original = BuildSectionAuthoritativeFixture();
            Assert.True(RecordingStore.ShouldWriteSectionAuthoritativeTrajectory(original));

            string path = Path.Combine(tempDir, "section-authoritative.prec");
            TrajectorySidecarBinary.Write(path, original, sidecarEpoch: 9);

            BinaryTrajectoryEnvelope envelope = ReadEnvelopeHeader(path);
            Assert.True(envelope.SectionAuthoritative);
            Assert.Equal(0, envelope.PointCount);
            Assert.Equal(0, envelope.OrbitSegmentCount);

            TrajectorySidecarProbe probe;
            Assert.True(TrajectorySidecarBinary.TryProbe(path, out probe));
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, probe.FormatVersion);

            var restored = new Recording();
            TrajectorySidecarBinary.Read(path, restored, probe);

            AssertTrajectoryPayloadEqual(original, restored);
            Assert.Equal(original.Points.Count, restored.Points.Count);
            Assert.Equal(original.OrbitSegments.Count, restored.OrbitSegments.Count);
        }

        [Fact]
        public void WriteRead_RelativeSection_PreservesAbsoluteShadowFrames()
        {
            Recording original = BuildRelativeShadowFixture();

            string path = Path.Combine(tempDir, "relative-shadow.prec");
            TrajectorySidecarBinary.Write(path, original, sidecarEpoch: 13);

            TrajectorySidecarProbe probe;
            Assert.True(TrajectorySidecarBinary.TryProbe(path, out probe));
            Assert.Equal(RecordingStore.RelativeAbsoluteShadowFormatVersion, probe.FormatVersion);

            var restored = new Recording();
            TrajectorySidecarBinary.Read(path, restored, probe);

            AssertTrajectoryPayloadEqual(original, restored);
            Assert.Single(restored.TrackSections);
            Assert.Equal(2, restored.TrackSections[0].absoluteFrames.Count);
            AssertPointEqual(original.TrackSections[0].absoluteFrames[0], restored.TrackSections[0].absoluteFrames[0]);
            AssertPointEqual(original.TrackSections[0].absoluteFrames[1], restored.TrackSections[0].absoluteFrames[1]);
        }

        [Fact]
        public void WriteRead_FlatFallbackFixture_PreservesTailOutsideTrackSections_AndExistingRecordingId()
        {
            Recording original = BuildFlatFallbackFixture();
            Assert.False(RecordingStore.ShouldWriteSectionAuthoritativeTrajectory(original));

            string path = Path.Combine(tempDir, "flat-fallback.prec");
            TrajectorySidecarBinary.Write(path, original, sidecarEpoch: 11);

            TrajectorySidecarProbe probe;
            Assert.True(TrajectorySidecarBinary.TryProbe(path, out probe));
            Assert.Equal(original.RecordingId, probe.RecordingId);

            var restored = new Recording { RecordingId = "existing-id" };
            TrajectorySidecarBinary.Read(path, restored, probe);

            Assert.Equal("existing-id", restored.RecordingId);
            AssertTrajectoryPayloadEqual(original, restored);
            Assert.True(restored.OrbitSegments[restored.OrbitSegments.Count - 1].isPredicted);
        }

        private static void WriteProbeOnlyBinary(string path, int version, int sidecarEpoch, string recordingId)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Encoding.ASCII.GetBytes("PRKB"));
                writer.Write(version);
                writer.Write(sidecarEpoch);
                writer.Write(recordingId ?? string.Empty);
            }
        }

        private static Recording BuildSimpleFlatFixture()
        {
            var rec = new Recording
            {
                RecordingFormatVersion = 3
            };

            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100,
                latitude = -0.10,
                longitude = -74.50,
                altitude = 120,
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(0f, 50f, 0f),
                bodyName = "Kerbin",
                funds = 1000,
                science = 1.5f,
                reputation = 0.25f
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 110,
                latitude = -0.09,
                longitude = -74.49,
                altitude = 300,
                rotation = new Quaternion(0.1f, 0f, 0f, 0.99f),
                velocity = new Vector3(0f, 75f, 0f),
                bodyName = "Kerbin",
                funds = 1010,
                science = 1.75f,
                reputation = 0.5f
            });

            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 120,
                endUT = 420,
                inclination = 28.5,
                eccentricity = 0.01,
                semiMajorAxis = 700000,
                longitudeOfAscendingNode = 90,
                argumentOfPeriapsis = 45,
                meanAnomalyAtEpoch = 0.2,
                epoch = 120,
                bodyName = "Kerbin",
                isPredicted = false,
                orbitalFrameRotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f),
                angularVelocity = new Vector3(0.4f, 0.5f, 0.6f)
            });

            return rec;
        }

        private static Recording BuildSectionAuthoritativeFixture()
        {
            const double t0 = 20000.0;
            var boundary = new TrajectoryPoint
            {
                ut = t0 + 10,
                latitude = -0.09,
                longitude = -74.55,
                altitude = 500,
                rotation = new Quaternion(0f, 0f, 0.1f, 0.99f),
                velocity = new Vector3(0f, 100f, 0f),
                bodyName = "Kerbin",
                funds = 20000,
                science = 1.0f,
                reputation = 0.5f
            };

            var rec = new Recording
            {
                RecordingId = "section-authoritative",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion
            };

            rec.Points.Add(new TrajectoryPoint
            {
                ut = t0,
                latitude = -0.10,
                longitude = -74.56,
                altitude = 100,
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(0f, 50f, 0f),
                bodyName = "Kerbin",
                funds = 19000,
                science = 0f,
                reputation = 0f
            });
            rec.Points.Add(boundary);
            rec.Points.Add(new TrajectoryPoint
            {
                ut = t0 + 20,
                latitude = -0.08,
                longitude = -74.54,
                altitude = 1000,
                rotation = new Quaternion(0f, 0f, 0.2f, 0.98f),
                velocity = new Vector3(0f, 200f, 0f),
                bodyName = "Kerbin",
                funds = 21000,
                science = 2.0f,
                reputation = 1.0f
            });

            rec.OrbitSegments.Add(MakeOrbitSegment(t0 + 30, t0 + 330, isPredicted: false));
            rec.OrbitSegments.Add(MakeOrbitSegment(t0 + 330, t0 + 630, isPredicted: true));

            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = t0,
                endUT = t0 + 10,
                sampleRateHz = 10f,
                boundaryDiscontinuityMeters = 0f,
                minAltitude = 100f,
                maxAltitude = 500f,
                frames = new List<TrajectoryPoint>
                {
                    rec.Points[0],
                    boundary
                },
                checkpoints = new List<OrbitSegment>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = t0 + 10,
                endUT = t0 + 20,
                sampleRateHz = 10f,
                boundaryDiscontinuityMeters = 0f,
                minAltitude = 500f,
                maxAltitude = 1000f,
                frames = new List<TrajectoryPoint>
                {
                    boundary,
                    rec.Points[2]
                },
                checkpoints = new List<OrbitSegment>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                source = TrackSectionSource.Checkpoint,
                startUT = rec.OrbitSegments[0].startUT,
                endUT = rec.OrbitSegments[rec.OrbitSegments.Count - 1].endUT,
                sampleRateHz = 0.1f,
                boundaryDiscontinuityMeters = 0f,
                minAltitude = 700000f,
                maxAltitude = 710000f,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>
                {
                    rec.OrbitSegments[0],
                    rec.OrbitSegments[1]
                }
            });

            return rec;
        }

        private static Recording BuildFlatFallbackFixture()
        {
            Recording rec = BuildSectionAuthoritativeFixture();
            rec.RecordingId = "flat-fallback";
            double tailStartUt = rec.OrbitSegments[rec.OrbitSegments.Count - 1].endUT;
            rec.OrbitSegments.Add(MakeOrbitSegment(tailStartUt, tailStartUt + 300, isPredicted: true));
            return rec;
        }

        private static TrajectoryPoint MakePoint(
            double ut,
            double latitude,
            double longitude,
            double altitude,
            string bodyName)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = latitude,
                longitude = longitude,
                altitude = altitude,
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
                bodyName = bodyName,
                funds = 1000f,
                science = 1f,
                reputation = 0.5f
            };
        }

        private static Recording BuildRelativeShadowFixture()
        {
            var rec = new Recording
            {
                RecordingId = "relative-shadow",
                RecordingFormatVersion = RecordingStore.RelativeAbsoluteShadowFormatVersion,
                VesselName = "Upper Stage",
                VesselPersistentId = 12001u
            };

            var relativeA = MakePoint(100.0, 1.0, 2.0, 3.0, "Kerbin");
            var relativeB = MakePoint(101.0, 4.0, 5.0, 6.0, "Kerbin");
            var absoluteA = MakePoint(100.0, -0.04, -74.55, 78000.0, "Kerbin");
            var absoluteB = MakePoint(101.0, -0.03, -74.54, 78120.0, "Kerbin");

            rec.Points.Add(relativeA);
            rec.Points.Add(relativeB);
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Relative,
                source = TrackSectionSource.Active,
                startUT = 100.0,
                endUT = 101.0,
                anchorVesselId = 3314061462u,
                sampleRateHz = 2f,
                minAltitude = 3f,
                maxAltitude = 6f,
                frames = new List<TrajectoryPoint> { relativeA, relativeB },
                absoluteFrames = new List<TrajectoryPoint> { absoluteA, absoluteB },
                checkpoints = new List<OrbitSegment>()
            });

            return rec;
        }

        private static BinaryTrajectoryEnvelope ReadEnvelopeHeader(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                string magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
                Assert.Equal("PRKB", magic);

                reader.ReadInt32(); // version
                reader.ReadInt32(); // sidecarEpoch
                reader.ReadString(); // recordingId
                byte flags = reader.ReadByte();

                int stringCount = reader.ReadInt32();
                for (int i = 0; i < stringCount; i++)
                    reader.ReadString();

                int pointCount = reader.ReadInt32();
                int orbitSegmentCount = pointCount == 0 ? reader.ReadInt32() : -1;

                return new BinaryTrajectoryEnvelope
                {
                    SectionAuthoritative = (flags & 0x1) != 0,
                    PointCount = pointCount,
                    OrbitSegmentCount = orbitSegmentCount
                };
            }
        }

        private static OrbitSegment MakeOrbitSegment(double startUT, double endUT, bool isPredicted)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                inclination = 28.5,
                eccentricity = 0.01,
                semiMajorAxis = 700000 + startUT,
                longitudeOfAscendingNode = 90,
                argumentOfPeriapsis = 45,
                meanAnomalyAtEpoch = 0.2,
                epoch = startUT,
                bodyName = "Kerbin",
                isPredicted = isPredicted,
                orbitalFrameRotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f),
                angularVelocity = new Vector3(0.4f, 0.5f, 0.6f)
            };
        }

        private static void AssertTrajectoryPayloadEqual(Recording expected, Recording actual)
        {
            Assert.Equal(expected.Points.Count, actual.Points.Count);
            for (int i = 0; i < expected.Points.Count; i++)
                AssertPointEqual(expected.Points[i], actual.Points[i]);

            Assert.Equal(expected.OrbitSegments.Count, actual.OrbitSegments.Count);
            for (int i = 0; i < expected.OrbitSegments.Count; i++)
                AssertOrbitSegmentEqual(expected.OrbitSegments[i], actual.OrbitSegments[i]);

            Assert.Equal(expected.TrackSections.Count, actual.TrackSections.Count);
            for (int i = 0; i < expected.TrackSections.Count; i++)
                AssertTrackSectionEqual(expected.TrackSections[i], actual.TrackSections[i]);
        }

        private static void AssertPointEqual(TrajectoryPoint expected, TrajectoryPoint actual)
        {
            Assert.Equal(expected.ut, actual.ut);
            Assert.Equal(expected.latitude, actual.latitude);
            Assert.Equal(expected.longitude, actual.longitude);
            Assert.Equal(expected.altitude, actual.altitude);
            Assert.Equal(expected.rotation.x, actual.rotation.x);
            Assert.Equal(expected.rotation.y, actual.rotation.y);
            Assert.Equal(expected.rotation.z, actual.rotation.z);
            Assert.Equal(expected.rotation.w, actual.rotation.w);
            Assert.Equal(expected.bodyName, actual.bodyName);
            Assert.Equal(expected.velocity.x, actual.velocity.x);
            Assert.Equal(expected.velocity.y, actual.velocity.y);
            Assert.Equal(expected.velocity.z, actual.velocity.z);
            Assert.Equal(expected.funds, actual.funds);
            Assert.Equal(expected.science, actual.science);
            Assert.Equal(expected.reputation, actual.reputation);
        }

        private static void AssertOrbitSegmentEqual(OrbitSegment expected, OrbitSegment actual)
        {
            Assert.Equal(expected.startUT, actual.startUT);
            Assert.Equal(expected.endUT, actual.endUT);
            Assert.Equal(expected.inclination, actual.inclination);
            Assert.Equal(expected.eccentricity, actual.eccentricity);
            Assert.Equal(expected.semiMajorAxis, actual.semiMajorAxis);
            Assert.Equal(expected.longitudeOfAscendingNode, actual.longitudeOfAscendingNode);
            Assert.Equal(expected.argumentOfPeriapsis, actual.argumentOfPeriapsis);
            Assert.Equal(expected.meanAnomalyAtEpoch, actual.meanAnomalyAtEpoch);
            Assert.Equal(expected.epoch, actual.epoch);
            Assert.Equal(expected.bodyName, actual.bodyName);
            Assert.Equal(expected.isPredicted, actual.isPredicted);
            Assert.Equal(expected.orbitalFrameRotation.x, actual.orbitalFrameRotation.x);
            Assert.Equal(expected.orbitalFrameRotation.y, actual.orbitalFrameRotation.y);
            Assert.Equal(expected.orbitalFrameRotation.z, actual.orbitalFrameRotation.z);
            Assert.Equal(expected.orbitalFrameRotation.w, actual.orbitalFrameRotation.w);
            Assert.Equal(expected.angularVelocity.x, actual.angularVelocity.x);
            Assert.Equal(expected.angularVelocity.y, actual.angularVelocity.y);
            Assert.Equal(expected.angularVelocity.z, actual.angularVelocity.z);
        }

        private static void AssertTrackSectionEqual(TrackSection expected, TrackSection actual)
        {
            Assert.Equal(expected.environment, actual.environment);
            Assert.Equal(expected.referenceFrame, actual.referenceFrame);
            Assert.Equal(expected.startUT, actual.startUT);
            Assert.Equal(expected.endUT, actual.endUT);
            Assert.Equal(expected.anchorVesselId, actual.anchorVesselId);
            Assert.Equal(expected.sampleRateHz, actual.sampleRateHz);
            Assert.Equal(expected.source, actual.source);
            Assert.Equal(expected.boundaryDiscontinuityMeters, actual.boundaryDiscontinuityMeters);
            Assert.Equal(expected.minAltitude, actual.minAltitude);
            Assert.Equal(expected.maxAltitude, actual.maxAltitude);

            Assert.Equal(expected.frames.Count, actual.frames.Count);
            for (int i = 0; i < expected.frames.Count; i++)
                AssertPointEqual(expected.frames[i], actual.frames[i]);

            List<TrajectoryPoint> expectedAbsoluteFrames =
                expected.absoluteFrames ?? new List<TrajectoryPoint>();
            List<TrajectoryPoint> actualAbsoluteFrames =
                actual.absoluteFrames ?? new List<TrajectoryPoint>();
            Assert.Equal(expectedAbsoluteFrames.Count, actualAbsoluteFrames.Count);
            for (int i = 0; i < expectedAbsoluteFrames.Count; i++)
                AssertPointEqual(expectedAbsoluteFrames[i], actualAbsoluteFrames[i]);

            Assert.Equal(expected.checkpoints.Count, actual.checkpoints.Count);
            for (int i = 0; i < expected.checkpoints.Count; i++)
                AssertOrbitSegmentEqual(expected.checkpoints[i], actual.checkpoints[i]);
        }

        private sealed class BinaryTrajectoryEnvelope
        {
            public bool SectionAuthoritative { get; set; }
            public int PointCount { get; set; }
            public int OrbitSegmentCount { get; set; }
        }
    }
}
