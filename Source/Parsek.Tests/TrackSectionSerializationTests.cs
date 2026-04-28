using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class TrackSectionSerializationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TrackSectionSerializationTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        #region Helpers

        private static TrajectoryPoint MakePoint(double ut, double lat, double lon, double alt,
            string body = "Kerbin", float velX = 0, float velY = 0, float velZ = 0,
            float rotX = 0, float rotY = 0, float rotZ = 0, float rotW = 1,
            double funds = 0, float science = 0, float rep = 0)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                bodyName = body,
                velocity = new Vector3(velX, velY, velZ),
                rotation = new Quaternion(rotX, rotY, rotZ, rotW),
                funds = funds,
                science = science,
                reputation = rep
            };
        }

        private static OrbitSegment MakeOrbitSegment(double startUT, double endUT, string body = "Kerbin",
            double inc = 28.5, double ecc = 0.01, double sma = 700000,
            double lan = 90, double argPe = 45, double mna = 0.5, double epoch = 1000,
            bool isPredicted = false)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = body,
                inclination = inc,
                eccentricity = ecc,
                semiMajorAxis = sma,
                longitudeOfAscendingNode = lan,
                argumentOfPeriapsis = argPe,
                meanAnomalyAtEpoch = mna,
                epoch = epoch,
                isPredicted = isPredicted
            };
        }

        private static void AssertPointsEqual(TrajectoryPoint expected, TrajectoryPoint actual, string label)
        {
            Assert.Equal(expected.ut, actual.ut);
            Assert.Equal(expected.latitude, actual.latitude);
            Assert.Equal(expected.longitude, actual.longitude);
            Assert.Equal(expected.altitude, actual.altitude);
            Assert.Equal(expected.bodyName, actual.bodyName);
            Assert.Equal(expected.velocity.x, actual.velocity.x);
            Assert.Equal(expected.velocity.y, actual.velocity.y);
            Assert.Equal(expected.velocity.z, actual.velocity.z);
            Assert.Equal(expected.rotation.x, actual.rotation.x);
            Assert.Equal(expected.rotation.y, actual.rotation.y);
            Assert.Equal(expected.rotation.z, actual.rotation.z);
            Assert.Equal(expected.rotation.w, actual.rotation.w);
            Assert.Equal(expected.funds, actual.funds);
            Assert.Equal(expected.science, actual.science);
            Assert.Equal(expected.reputation, actual.reputation);
        }

        private static void AssertOrbitSegmentsEqual(OrbitSegment expected, OrbitSegment actual, string label)
        {
            Assert.Equal(expected.startUT, actual.startUT);
            Assert.Equal(expected.endUT, actual.endUT);
            Assert.Equal(expected.bodyName, actual.bodyName);
            Assert.Equal(expected.inclination, actual.inclination);
            Assert.Equal(expected.eccentricity, actual.eccentricity);
            Assert.Equal(expected.semiMajorAxis, actual.semiMajorAxis);
            Assert.Equal(expected.longitudeOfAscendingNode, actual.longitudeOfAscendingNode);
            Assert.Equal(expected.argumentOfPeriapsis, actual.argumentOfPeriapsis);
            Assert.Equal(expected.meanAnomalyAtEpoch, actual.meanAnomalyAtEpoch);
            Assert.Equal(expected.epoch, actual.epoch);
            Assert.Equal(expected.isPredicted, actual.isPredicted);
        }

        #endregion

        #region Test 1: ATMOSPHERIC + ABSOLUTE round-trip with 5 frames

        [Fact]
        public void RoundTrip_AtmosphericAbsolute_5Frames()
        {
            var original = new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 17000.0,
                endUT = 17050.0,
                sampleRateHz = 10.0f,
                frames = new List<TrajectoryPoint>
                {
                    MakePoint(17000.0, -0.097, -74.558, 70, velY: 5f, funds: 25000, science: 1.5f, rep: 0.5f),
                    MakePoint(17010.0, -0.097, -74.558, 120, velY: 50f, funds: 25000),
                    MakePoint(17020.0, -0.097, -74.557, 400, velY: 110f, funds: 25000),
                    MakePoint(17030.0, -0.096, -74.557, 900, velY: 150f, funds: 25000),
                    MakePoint(17050.0, -0.095, -74.556, 2000, velY: 200f, funds: 24800),
                },
                checkpoints = new List<OrbitSegment>()
            };

            var parent = new ConfigNode("TEST");
            var tracks = new List<TrackSection> { original };
            RecordingStore.SerializeTrackSections(parent, tracks);

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(parent, loaded);

            Assert.Single(loaded);
            var result = loaded[0];
            Assert.Equal(SegmentEnvironment.Atmospheric, result.environment);
            Assert.Equal(ReferenceFrame.Absolute, result.referenceFrame);
            Assert.Equal(17000.0, result.startUT);
            Assert.Equal(17050.0, result.endUT);
            Assert.Equal(10.0f, result.sampleRateHz);
            Assert.Equal(TrackSectionSource.Active, result.source);
            Assert.Equal(0u, result.anchorVesselId);
            Assert.Equal(5, result.frames.Count);
            Assert.Empty(result.checkpoints);

            for (int i = 0; i < 5; i++)
                AssertPointsEqual(original.frames[i], result.frames[i], $"point[{i}]");
        }

        #endregion

        #region Test 2: EXO_BALLISTIC + ORBITAL_CHECKPOINT round-trip with 2 orbit segments

        [Fact]
        public void RoundTrip_ExoBallisticOrbitalCheckpoint_2Segments()
        {
            var original = new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 18000.0,
                endUT = 20000.0,
                sampleRateHz = 0.1f,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>
                {
                    MakeOrbitSegment(18000, 19000, "Kerbin", 28.5, 0.01, 700000, 90, 45, 0.5, 18000),
                    MakeOrbitSegment(19000, 20000, "Kerbin", 28.5, 0.005, 710000, 90, 46, 0.2, 19000),
                }
            };

            var parent = new ConfigNode("TEST");
            var tracks = new List<TrackSection> { original };
            RecordingStore.SerializeTrackSections(parent, tracks);

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(parent, loaded);

            Assert.Single(loaded);
            var result = loaded[0];
            Assert.Equal(SegmentEnvironment.ExoBallistic, result.environment);
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, result.referenceFrame);
            Assert.Equal(18000.0, result.startUT);
            Assert.Equal(20000.0, result.endUT);
            Assert.Equal(0.1f, result.sampleRateHz);
            Assert.Empty(result.frames);
            Assert.Equal(2, result.checkpoints.Count);

            for (int i = 0; i < 2; i++)
                AssertOrbitSegmentsEqual(original.checkpoints[i], result.checkpoints[i], $"checkpoint[{i}]");
        }

        #endregion

        #region Test 3: RELATIVE reference frame with anchorVesselId

        [Fact]
        public void RoundTrip_RelativeFrame_WithAnchorVesselId()
        {
            var original = new TrackSection
            {
                environment = SegmentEnvironment.ExoPropulsive,
                referenceFrame = ReferenceFrame.Relative,
                startUT = 17500.0,
                endUT = 17600.0,
                sampleRateHz = 5.0f,
                anchorVesselId = 42u,
                frames = new List<TrajectoryPoint>
                {
                    MakePoint(17500.0, 0.0, 0.0, 100, velX: 1f, velY: 2f, velZ: 3f),
                    MakePoint(17550.0, 0.1, 0.1, 200, velX: 4f, velY: 5f, velZ: 6f),
                    MakePoint(17600.0, 0.2, 0.2, 300, velX: 7f, velY: 8f, velZ: 9f),
                },
                checkpoints = new List<OrbitSegment>()
            };

            var parent = new ConfigNode("TEST");
            var tracks = new List<TrackSection> { original };
            RecordingStore.SerializeTrackSections(parent, tracks);

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(parent, loaded);

            Assert.Single(loaded);
            var result = loaded[0];
            Assert.Equal(ReferenceFrame.Relative, result.referenceFrame);
            Assert.Equal(42u, result.anchorVesselId);
            Assert.Equal(3, result.frames.Count);

            for (int i = 0; i < 3; i++)
                AssertPointsEqual(original.frames[i], result.frames[i], $"point[{i}]");
        }

        #endregion

        #region Test 4: Multiple sections in sequence

        [Fact]
        public void RoundTrip_MultipleSections_OrderPreserved()
        {
            var sections = new List<TrackSection>
            {
                new TrackSection
                {
                    environment = SegmentEnvironment.SurfaceStationary,
                    referenceFrame = ReferenceFrame.Absolute,
                    startUT = 16000.0,
                    endUT = 17000.0,
                    sampleRateHz = 1.0f,
                    frames = new List<TrajectoryPoint>
                    {
                        MakePoint(16000.0, -0.1, -74.5, 70),
                        MakePoint(17000.0, -0.1, -74.5, 70),
                    },
                    checkpoints = new List<OrbitSegment>()
                },
                new TrackSection
                {
                    environment = SegmentEnvironment.Atmospheric,
                    referenceFrame = ReferenceFrame.Absolute,
                    startUT = 17000.0,
                    endUT = 17300.0,
                    sampleRateHz = 10.0f,
                    frames = new List<TrajectoryPoint>
                    {
                        MakePoint(17000.0, -0.1, -74.5, 70),
                        MakePoint(17150.0, -0.05, -74.4, 40000),
                        MakePoint(17300.0, -0.02, -74.3, 72000),
                    },
                    checkpoints = new List<OrbitSegment>()
                },
                new TrackSection
                {
                    environment = SegmentEnvironment.ExoBallistic,
                    referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                    startUT = 17300.0,
                    endUT = 20000.0,
                    sampleRateHz = 0.1f,
                    frames = new List<TrajectoryPoint>(),
                    checkpoints = new List<OrbitSegment>
                    {
                        MakeOrbitSegment(17300, 20000, "Kerbin", 28.5, 0.01, 700000),
                    }
                },
            };

            var parent = new ConfigNode("TEST");
            RecordingStore.SerializeTrackSections(parent, sections);

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(parent, loaded);

            Assert.Equal(3, loaded.Count);

            Assert.Equal(SegmentEnvironment.SurfaceStationary, loaded[0].environment);
            Assert.Equal(16000.0, loaded[0].startUT);
            Assert.Equal(2, loaded[0].frames.Count);

            Assert.Equal(SegmentEnvironment.Atmospheric, loaded[1].environment);
            Assert.Equal(17000.0, loaded[1].startUT);
            Assert.Equal(3, loaded[1].frames.Count);

            Assert.Equal(SegmentEnvironment.ExoBallistic, loaded[2].environment);
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, loaded[2].referenceFrame);
            Assert.Equal(17300.0, loaded[2].startUT);
            Assert.Single(loaded[2].checkpoints);
        }

        #endregion

        #region Test 5: Backward compat - no TRACK_SECTION nodes

        [Fact]
        public void BackwardCompat_NoTrackSectionNodes_EmptyList()
        {
            var parent = new ConfigNode("TEST");
            // Add some POINT nodes (legacy format) but no TRACK_SECTION
            var ptNode = parent.AddNode("POINT");
            ptNode.AddValue("ut", "17000");

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(parent, loaded);

            Assert.Empty(loaded);
        }

        #endregion

        #region Test 6: Forward tolerance - unknown env value

        [Fact]
        public void ForwardTolerance_UnknownEnv_SkippedNoCrash()
        {
            var parent = new ConfigNode("TEST");
            var tsNode = parent.AddNode("TRACK_SECTION");
            tsNode.AddValue("env", "99");
            tsNode.AddValue("ref", "0");
            tsNode.AddValue("startUT", "17000");
            tsNode.AddValue("endUT", "17100");
            tsNode.AddValue("sampleRate", "10");

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(parent, loaded);

            Assert.Empty(loaded);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") && l.Contains("unknown env=99") && l.Contains("skipping"));
        }

        #endregion

        #region Test 7: Forward tolerance - unknown ref value

        [Fact]
        public void ForwardTolerance_UnknownRef_SkippedNoCrash()
        {
            var parent = new ConfigNode("TEST");
            var tsNode = parent.AddNode("TRACK_SECTION");
            tsNode.AddValue("env", "0");
            tsNode.AddValue("ref", "99");
            tsNode.AddValue("startUT", "17000");
            tsNode.AddValue("endUT", "17100");
            tsNode.AddValue("sampleRate", "10");

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(parent, loaded);

            Assert.Empty(loaded);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") && l.Contains("unknown ref=99") && l.Contains("skipping"));
        }

        #endregion

        #region Test 8: Empty section - 0 frames and 0 checkpoints

        [Fact]
        public void RoundTrip_EmptySection_ZeroFramesZeroCheckpoints()
        {
            var original = new TrackSection
            {
                environment = SegmentEnvironment.SurfaceMobile,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 15000.0,
                endUT = 15100.0,
                sampleRateHz = 2.0f,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>()
            };

            var parent = new ConfigNode("TEST");
            var tracks = new List<TrackSection> { original };
            RecordingStore.SerializeTrackSections(parent, tracks);

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(parent, loaded);

            Assert.Single(loaded);
            var result = loaded[0];
            Assert.Equal(SegmentEnvironment.SurfaceMobile, result.environment);
            Assert.Equal(ReferenceFrame.Absolute, result.referenceFrame);
            Assert.Equal(15000.0, result.startUT);
            Assert.Equal(15100.0, result.endUT);
            Assert.Equal(2.0f, result.sampleRateHz);
            Assert.Empty(result.frames);
            Assert.Empty(result.checkpoints);
        }

        #endregion

        #region Test 9: Background flag

        [Fact]
        public void RoundTrip_BackgroundFlag_PreservedCorrectly()
        {
            var original = new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 14000.0,
                endUT = 16000.0,
                sampleRateHz = 0.5f,
                source = TrackSectionSource.Background,
                frames = new List<TrajectoryPoint>
                {
                    MakePoint(14000.0, -0.1, -74.5, 70),
                },
                checkpoints = new List<OrbitSegment>()
            };

            var parent = new ConfigNode("TEST");
            var tracks = new List<TrackSection> { original };
            RecordingStore.SerializeTrackSections(parent, tracks);

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(parent, loaded);

            Assert.Single(loaded);
            Assert.Equal(TrackSectionSource.Background, loaded[0].source);
        }

        [Fact]
        public void RoundTrip_SourceActive_SrcKeyNotSerialized()
        {
            // When source is Active (default), the src key should not be written
            var original = new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 17000.0,
                endUT = 17100.0,
                sampleRateHz = 10.0f,
                source = TrackSectionSource.Active,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>()
            };

            var parent = new ConfigNode("TEST");
            RecordingStore.SerializeTrackSections(parent, new List<TrackSection> { original });

            // Verify the src key was not written
            var tsNode = parent.GetNodes("TRACK_SECTION")[0];
            Assert.Null(tsNode.GetValue("src"));

            // Still round-trips correctly (defaults to Active)
            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(parent, loaded);
            Assert.Single(loaded);
            Assert.Equal(TrackSectionSource.Active, loaded[0].source);
        }

        #endregion

        #region AnchorPid not written when zero

        [Fact]
        public void Serialize_AnchorPidZero_NotWritten()
        {
            var original = new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 17000.0,
                endUT = 17100.0,
                sampleRateHz = 10.0f,
                anchorVesselId = 0,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>()
            };

            var parent = new ConfigNode("TEST");
            RecordingStore.SerializeTrackSections(parent, new List<TrackSection> { original });

            var tsNode = parent.GetNodes("TRACK_SECTION")[0];
            Assert.Null(tsNode.GetValue("anchorPid"));
        }

        #endregion

        #region Wiring: SerializeTrajectoryInto / DeserializeTrajectoryFrom

        [Fact]
        public void SerializeTrajectoryInto_IncludesTrackSections()
        {
            var rec = new Recording();
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 17000.0,
                endUT = 17100.0,
                sampleRateHz = 10.0f,
                frames = new List<TrajectoryPoint>
                {
                    MakePoint(17000.0, 0, 0, 100),
                },
                checkpoints = new List<OrbitSegment>()
            });

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            // Verify TRACK_SECTION node was written
            Assert.Single(node.GetNodes("TRACK_SECTION"));
        }

        [Fact]
        public void DeserializeTrajectoryFrom_LoadsTrackSections()
        {
            // Build a ConfigNode with both legacy data and track sections
            var node = new ConfigNode("TEST");

            // Add a legacy POINT
            var ptNode = node.AddNode("POINT");
            ptNode.AddValue("ut", "17000");
            ptNode.AddValue("lat", "0");
            ptNode.AddValue("lon", "0");
            ptNode.AddValue("alt", "100");
            ptNode.AddValue("rotX", "0");
            ptNode.AddValue("rotY", "0");
            ptNode.AddValue("rotZ", "0");
            ptNode.AddValue("rotW", "1");
            ptNode.AddValue("body", "Kerbin");
            ptNode.AddValue("velX", "0");
            ptNode.AddValue("velY", "0");
            ptNode.AddValue("velZ", "0");
            ptNode.AddValue("funds", "0");
            ptNode.AddValue("science", "0");
            ptNode.AddValue("rep", "0");

            // Add a TRACK_SECTION
            var tsNode = node.AddNode("TRACK_SECTION");
            tsNode.AddValue("env", "0");
            tsNode.AddValue("ref", "0");
            tsNode.AddValue("startUT", "17000");
            tsNode.AddValue("endUT", "17100");
            tsNode.AddValue("sampleRate", "10");

            var rec = new Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, rec);

            // Legacy point loaded
            Assert.Single(rec.Points);
            // Track section loaded
            Assert.Single(rec.TrackSections);
            Assert.Equal(SegmentEnvironment.Atmospheric, rec.TrackSections[0].environment);
        }

        [Fact]
        public void SerializeTrajectoryInto_NoTrackSections_NoTrackSectionNodes()
        {
            var rec = new Recording();
            rec.Points.Add(MakePoint(17000, 0, 0, 100));

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            // No TRACK_SECTION nodes should be written
            Assert.Empty(node.GetNodes("TRACK_SECTION"));
        }

        [Fact]
        public void AltitudeMetadata_RoundTrip()
        {
            var tracks = new List<TrackSection>
            {
                new TrackSection
                {
                    environment = SegmentEnvironment.Atmospheric,
                    referenceFrame = ReferenceFrame.Absolute,
                    startUT = 17000, endUT = 17060,
                    frames = new List<TrajectoryPoint>(),
                    checkpoints = new List<OrbitSegment>(),
                    minAltitude = 500f,
                    maxAltitude = 72000f
                }
            };

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrackSections(node, tracks);

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(node, loaded);

            Assert.Single(loaded);
            Assert.Equal(500f, loaded[0].minAltitude);
            Assert.Equal(72000f, loaded[0].maxAltitude);
        }

        [Fact]
        public void OrbitalCheckpoint_IsPredicted_RoundTrip()
        {
            var track = new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 18000,
                endUT = 19000,
                checkpoints = new List<OrbitSegment>
                {
                    MakeOrbitSegment(18000, 19000, isPredicted: true)
                },
                frames = new List<TrajectoryPoint>()
            };

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrackSections(node, new List<TrackSection> { track });

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(node, loaded);

            Assert.Single(loaded);
            Assert.Single(loaded[0].checkpoints);
            Assert.True(loaded[0].checkpoints[0].isPredicted);
        }

        [Fact]
        public void SerializeTrackSections_V4Checkpoint_OmitsPredictedFlag()
        {
            var track = new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 18000,
                endUT = 19000,
                checkpoints = new List<OrbitSegment>
                {
                    MakeOrbitSegment(18000, 19000, isPredicted: true)
                },
                frames = new List<TrajectoryPoint>()
            };

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrackSections(
                node,
                new List<TrackSection> { track },
                RecordingStore.LaunchToLaunchLoopIntervalFormatVersion);

            ConfigNode tsNode = node.GetNode("TRACK_SECTION");
            Assert.NotNull(tsNode);
            ConfigNode checkpointNode = tsNode.GetNode("ORBIT_SEGMENT");
            Assert.NotNull(checkpointNode);
            Assert.Null(checkpointNode.GetValue("isPredicted"));
        }

        [Fact]
        public void AltitudeMetadata_LegacyMissing_DefaultsToNaN()
        {
            // Simulate a legacy TRACK_SECTION without minAlt/maxAlt
            var node = new ConfigNode("TEST");
            var tsNode = node.AddNode("TRACK_SECTION");
            tsNode.AddValue("env", "0"); // Atmospheric
            tsNode.AddValue("ref", "0"); // Absolute
            tsNode.AddValue("startUT", "17000");
            tsNode.AddValue("endUT", "17060");
            // No minAlt/maxAlt keys

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(node, loaded);

            Assert.Single(loaded);
            Assert.True(float.IsNaN(loaded[0].minAltitude));
            Assert.True(float.IsNaN(loaded[0].maxAltitude));
        }

        [Fact]
        public void AltitudeMetadata_NaN_NotSerialized()
        {
            // TrackSection with NaN altitude (not tracked) should not write minAlt/maxAlt
            var tracks = new List<TrackSection>
            {
                new TrackSection
                {
                    environment = SegmentEnvironment.ExoBallistic,
                    referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                    startUT = 17000, endUT = 17300,
                    frames = new List<TrajectoryPoint>(),
                    checkpoints = new List<OrbitSegment>(),
                    minAltitude = float.NaN,
                    maxAltitude = float.NaN
                }
            };

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrackSections(node, tracks);

            var tsNode = node.GetNodes("TRACK_SECTION")[0];
            Assert.Null(tsNode.GetValue("minAlt"));
            Assert.Null(tsNode.GetValue("maxAlt"));
        }

        #endregion

        #region IsBoundarySeam — text codec round-trip (Phase 1, plan §5.3)

        // Test #26 from docs/dev/plans/optimizer-persistence-split.md §9.3.
        // Guards the text codec sparse-field write/read path: a seam-flagged TrackSection
        // round-trips through the text codec without losing the flag.
        [Fact]
        public void TrackSection_BoundarySeamFlag_RoundTripsThroughTextCodec()
        {
            var original = new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 17000.0,
                endUT = 17000.0,
                sampleRateHz = 0f,
                source = TrackSectionSource.Background,
                frames = new List<TrajectoryPoint> { MakePoint(17000.0, 0.0, 0.0, 70000) },
                checkpoints = new List<OrbitSegment>(),
                isBoundarySeam = true
            };

            var parent = new ConfigNode("TEST");
            var tracks = new List<TrackSection> { original };
            RecordingStore.SerializeTrackSections(parent, tracks);

            // Sparse: the seam key is written exactly when the flag is set.
            var tsNode = parent.GetNodes("TRACK_SECTION")[0];
            Assert.Equal("1", tsNode.GetValue("seam"));

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(parent, loaded);

            Assert.Single(loaded);
            Assert.True(loaded[0].isBoundarySeam);
        }

        // Test #27 from §9.3.
        // Guards forward-tolerance for legacy text recordings: a TRACK_SECTION node without
        // the "seam" key deserializes with isBoundarySeam == false (struct default).
        [Fact]
        public void TrackSection_BoundarySeamFlag_DefaultsFalseOnLegacyTextLoad()
        {
            var parent = new ConfigNode("TEST");
            var tsNode = parent.AddNode("TRACK_SECTION");
            tsNode.AddValue("env", ((int)SegmentEnvironment.Atmospheric).ToString(CultureInfo.InvariantCulture));
            tsNode.AddValue("ref", ((int)ReferenceFrame.Absolute).ToString(CultureInfo.InvariantCulture));
            tsNode.AddValue("startUT", "17000");
            tsNode.AddValue("endUT", "17050");
            tsNode.AddValue("sampleRate", "10");
            // Deliberately no "seam" key — pre-v8 layout.

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(parent, loaded);

            Assert.Single(loaded);
            Assert.False(loaded[0].isBoundarySeam);
        }

        // Sparse-write contract: when isBoundarySeam == false, the "seam" key is NOT written.
        // Keeps the on-disk footprint small for the common case (the vast majority of sections
        // are not seams) and matches the established sparse pattern of the text codec.
        [Fact]
        public void TrackSection_BoundarySeamFlag_NotWrittenWhenFalse()
        {
            var tracks = new List<TrackSection>
            {
                new TrackSection
                {
                    environment = SegmentEnvironment.Atmospheric,
                    referenceFrame = ReferenceFrame.Absolute,
                    startUT = 17000, endUT = 17050,
                    sampleRateHz = 10f,
                    frames = new List<TrajectoryPoint>(),
                    checkpoints = new List<OrbitSegment>(),
                    isBoundarySeam = false
                }
            };

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrackSections(node, tracks);

            var tsNode = node.GetNodes("TRACK_SECTION")[0];
            Assert.Null(tsNode.GetValue("seam"));
        }

        #endregion
    }
}
