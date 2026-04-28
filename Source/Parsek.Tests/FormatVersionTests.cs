using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for recording format version dispatch and the v1/v2 storage paths.
    /// </summary>
    [Collection("Sequential")]
    public class FormatVersionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public FormatVersionTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
        }

        #region Version constants

        [Fact]
        public void CurrentRecordingFormatVersion_Is8()
        {
            Assert.Equal(8, RecordingStore.CurrentRecordingFormatVersion);
        }

        [Fact]
        public void BoundarySeamFlagFormatVersion_Is8()
        {
            Assert.Equal(8, RecordingStore.BoundarySeamFlagFormatVersion);
        }

        // Cross-codec sync guard. The binary `.prec` codec gates the seam-flag
        // write/read on its own `BoundarySeamFlagBinaryVersion` constant, while the
        // public `RecordingStore.BoundarySeamFlagFormatVersion` drives the recording's
        // `RecordingFormatVersion` stamp and the version-selection ladder. If those two
        // ever drift (e.g. someone bumps one without the other), v8 round-trip silently
        // breaks: the writer might emit the seam byte at a different version threshold
        // than the reader expects. This test pins them together.
        [Fact]
        public void BoundarySeamFlag_FormatVersion_MatchesBinaryVersion()
        {
            Assert.Equal(
                RecordingStore.BoundarySeamFlagFormatVersion,
                TrajectorySidecarBinary.BoundarySeamFlagBinaryVersion);
        }

        [Fact]
        public void UsesRelativeLocalFrameContract_V5False_V6True()
        {
            Assert.False(RecordingStore.UsesRelativeLocalFrameContract(
                RecordingStore.PredictedOrbitSegmentFormatVersion));
            Assert.True(RecordingStore.UsesRelativeLocalFrameContract(
                RecordingStore.RelativeLocalFrameFormatVersion));
        }

        [Fact]
        public void NormalizeRecordingFormatVersionAfterLegacyLoopMigration_UpgradesOnlyToV4()
        {
            var rec = new Recording
            {
                RecordingId = "legacy-loop-v3",
                RecordingFormatVersion = 3
            };

            RecordingStore.NormalizeRecordingFormatVersionAfterLegacyLoopMigration(rec);

            Assert.Equal(
                RecordingStore.LaunchToLaunchLoopIntervalFormatVersion,
                rec.RecordingFormatVersion);
            Assert.False(RecordingStore.UsesRelativeLocalFrameContract(rec.RecordingFormatVersion));
        }

        [Fact]
        public void NewRecording_DefaultsToCurrentVersion()
        {
            var rec = new Recording();
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, rec.RecordingFormatVersion);
        }

        [Fact]
        public void RecordingBuilder_DefaultVersion_IsCurrentVersion()
        {
            var builder = new RecordingBuilder("TestVessel");
            builder.AddPoint(17000, 0, 0, 100);
            builder.AddPoint(17010, 0, 0, 200);

            var trajectoryNode = builder.BuildTrajectoryNode();
            Assert.Equal(
                RecordingStore.CurrentRecordingFormatVersion.ToString(CultureInfo.InvariantCulture),
                trajectoryNode.GetValue("version"));
        }

        [Fact]
        public void RecordingBuilder_WithCustomFormatVersion_WritesCustomVersion()
        {
            var builder = new RecordingBuilder("TestVessel");
            builder.WithFormatVersion(42);
            builder.AddPoint(17000, 0, 0, 100);
            builder.AddPoint(17010, 0, 0, 200);

            var trajectoryNode = builder.BuildTrajectoryNode();
            Assert.Equal("42", trajectoryNode.GetValue("version"));

            var metadataNode = builder.BuildV3Metadata();
            Assert.Equal("42", metadataNode.GetValue("recordingFormatVersion"));
        }

        #endregion

        #region Predicted format normalization

        [Fact]
        public void NormalizeRecordingFormatVersionForPredictedSegments_NullRecording_NoOp()
        {
            logLines.Clear();

            RecordingStore.NormalizeRecordingFormatVersionForPredictedSegments(null);

            Assert.Empty(logLines);
        }

        [Fact]
        public void NormalizeRecordingFormatVersionForPredictedSegments_VersionBelow2_DoesNotUpgrade()
        {
            var rec = new Recording
            {
                RecordingId = "predicted-v1",
                RecordingFormatVersion = 1
            };
            rec.OrbitSegments.Add(MakeOrbitSegment(100.0, 200.0, isPredicted: true));

            logLines.Clear();
            RecordingStore.NormalizeRecordingFormatVersionForPredictedSegments(rec);

            Assert.Equal(1, rec.RecordingFormatVersion);
            Assert.DoesNotContain(logLines, line =>
                line.Contains("NormalizeRecordingFormatVersionForPredictedSegments"));
        }

        [Fact]
        public void NormalizeRecordingFormatVersionForPredictedSegments_AlreadyV5_DoesNotLog()
        {
            var rec = new Recording
            {
                RecordingId = "predicted-v5",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion
            };
            rec.OrbitSegments.Add(MakeOrbitSegment(100.0, 200.0, isPredicted: true));

            logLines.Clear();
            RecordingStore.NormalizeRecordingFormatVersionForPredictedSegments(rec);

            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, rec.RecordingFormatVersion);
            Assert.DoesNotContain(logLines, line =>
                line.Contains("NormalizeRecordingFormatVersionForPredictedSegments"));
        }

        [Fact]
        public void NormalizeRecordingFormatVersionForPredictedSegments_NoPredictedSegments_DoesNotUpgrade()
        {
            var rec = new Recording
            {
                RecordingId = "non-predicted-v4",
                RecordingFormatVersion = RecordingStore.LaunchToLaunchLoopIntervalFormatVersion
            };
            rec.OrbitSegments.Add(MakeOrbitSegment(100.0, 200.0, isPredicted: false));
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 100.0,
                endUT = 200.0,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>
                {
                    MakeOrbitSegment(100.0, 200.0, isPredicted: false)
                }
            });

            logLines.Clear();
            RecordingStore.NormalizeRecordingFormatVersionForPredictedSegments(rec);

            Assert.Equal(RecordingStore.LaunchToLaunchLoopIntervalFormatVersion, rec.RecordingFormatVersion);
            Assert.DoesNotContain(logLines, line =>
                line.Contains("NormalizeRecordingFormatVersionForPredictedSegments"));
        }

        [Fact]
        public void NormalizeRecordingFormatVersionForPredictedSegments_PredictedOnlyInTrackSections_UpgradesAndLogsCounts()
        {
            var rec = new Recording
            {
                RecordingId = "checkpoint-only-predicted",
                RecordingFormatVersion = RecordingStore.LaunchToLaunchLoopIntervalFormatVersion
            };
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                source = TrackSectionSource.Checkpoint,
                startUT = 100.0,
                endUT = 300.0,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>
                {
                    MakeOrbitSegment(100.0, 200.0, isPredicted: false),
                    MakeOrbitSegment(200.0, 300.0, isPredicted: true)
                }
            });

            logLines.Clear();
            RecordingStore.NormalizeRecordingFormatVersionForPredictedSegments(rec);

            Assert.Equal(RecordingStore.PredictedOrbitSegmentFormatVersion, rec.RecordingFormatVersion);
            Assert.Contains(logLines, line =>
                line.Contains("[WARN]") &&
                line.Contains("[RecordingStore]") &&
                line.Contains("NormalizeRecordingFormatVersionForPredictedSegments") &&
                line.Contains("recording=checkpoint-only-predicted") &&
                line.Contains("version=4->5") &&
                line.Contains("predictedOrbitSegments=0") &&
                line.Contains("predictedCheckpoints=1"));
        }

        #endregion

        #region Backward compat

        [Fact]
        public void Recording_WithTrackSections_LoadsPopulatedTrackSections_FromV0()
        {
            var precNode = new ConfigNode("PARSEK_RECORDING");
            precNode.AddValue("version", "0");
            precNode.AddValue("recordingId", "track_test");

            AddMinimalPoint(precNode, 17000.0);

            var tsNode = precNode.AddNode("TRACK_SECTION");
            tsNode.AddValue("env", "0");
            tsNode.AddValue("ref", "0");
            tsNode.AddValue("startUT", "17000");
            tsNode.AddValue("endUT", "17050");
            tsNode.AddValue("sampleRate", "10");

            var frameNode = tsNode.AddNode("POINT");
            frameNode.AddValue("ut", "17000");
            frameNode.AddValue("lat", "-0.097");
            frameNode.AddValue("lon", "-74.558");
            frameNode.AddValue("alt", "70");
            frameNode.AddValue("rotX", "0");
            frameNode.AddValue("rotY", "0");
            frameNode.AddValue("rotZ", "0");
            frameNode.AddValue("rotW", "1");
            frameNode.AddValue("body", "Kerbin");
            frameNode.AddValue("velX", "0");
            frameNode.AddValue("velY", "5");
            frameNode.AddValue("velZ", "0");
            frameNode.AddValue("funds", "0");
            frameNode.AddValue("science", "0");
            frameNode.AddValue("rep", "0");

            var rec = new Recording { RecordingId = "track_test" };
            RecordingStore.DeserializeTrajectoryFrom(precNode, rec);

            Assert.Single(rec.Points);
            Assert.Single(rec.TrackSections);
            Assert.Equal(SegmentEnvironment.Atmospheric, rec.TrackSections[0].environment);
            Assert.Equal(ReferenceFrame.Absolute, rec.TrackSections[0].referenceFrame);
            Assert.Single(rec.TrackSections[0].frames);
        }

        #endregion

        #region V1+ section-authoritative write path

        [Fact]
        public void SerializeTrajectoryInto_V1WithTrackSections_SkipsTopLevelTrajectoryCopies()
        {
            Recording rec = RecordingStorageFixtures.MaterializeTrajectory(
                RecordingStorageFixtures.OrbitalCheckpointTransition().Builder);

            var node = new ConfigNode("PARSEK_RECORDING");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            Assert.Empty(node.GetNodes("POINT"));
            Assert.Empty(node.GetNodes("ORBIT_SEGMENT"));
            Assert.Equal(2, node.GetNodes("TRACK_SECTION").Length);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("SerializeTrajectoryInto") &&
                l.Contains("section-authoritative path"));
        }

        [Fact]
        public void SerializeTrajectoryInto_V1WithoutTrackSections_FallsBackToFlatTrajectory()
        {
            var rec = new Recording
            {
                RecordingId = "v1-flat-fallback",
                RecordingFormatVersion = 1
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100, latitude = 0, longitude = 0, altitude = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 110, latitude = 0.1, longitude = 0.1, altitude = 200, bodyName = "Kerbin" });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 110,
                endUT = 500,
                semiMajorAxis = 700000,
                eccentricity = 0.01,
                bodyName = "Kerbin"
            });

            var node = new ConfigNode("PARSEK_RECORDING");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            Assert.Equal(2, node.GetNodes("POINT").Length);
            Assert.Single(node.GetNodes("ORBIT_SEGMENT"));
            Assert.Empty(node.GetNodes("TRACK_SECTION"));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("SerializeTrajectoryInto") &&
                l.Contains("used flat fallback path"));
        }

        [Fact]
        public void SerializeTrajectoryInto_V1WithIncompleteCheckpointTrackSections_FallsBackToFlatTrajectory()
        {
            var rec = new Recording
            {
                RecordingId = "v1-mixed-background",
                RecordingFormatVersion = 1
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100, latitude = 0, longitude = 0, altitude = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 110, latitude = 0.1, longitude = 0.1, altitude = 200, bodyName = "Kerbin" });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 110,
                endUT = 500,
                semiMajorAxis = 700000,
                eccentricity = 0.01,
                bodyName = "Kerbin"
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Background,
                startUT = 100,
                endUT = 110,
                frames = new List<TrajectoryPoint>
                {
                    rec.Points[0],
                    rec.Points[1]
                },
                checkpoints = new List<OrbitSegment>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                source = TrackSectionSource.Checkpoint,
                startUT = 110,
                endUT = 500,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>()
            });

            var node = new ConfigNode("PARSEK_RECORDING");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            Assert.Equal(2, node.GetNodes("POINT").Length);
            Assert.Single(node.GetNodes("ORBIT_SEGMENT"));
            Assert.Equal(2, node.GetNodes("TRACK_SECTION").Length);
            Assert.Equal("False", node.GetValue("sectionAuthoritative"));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("SerializeTrajectoryInto") &&
                l.Contains("used flat fallback path"));
        }

        [Fact]
        public void SerializeTrajectoryInto_V1RecorderLoadedToOnRailsFallback_FallsBackToFlatTrajectory()
        {
            var rec = BuildRecorderLoadedToOnRailsFallbackRecording(formatVersion: 1);

            var node = new ConfigNode("PARSEK_RECORDING");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            Assert.Equal(3, node.GetNodes("POINT").Length);
            Assert.Single(node.GetNodes("ORBIT_SEGMENT"));
            Assert.Single(node.GetNodes("TRACK_SECTION"));
            Assert.Equal("False", node.GetValue("sectionAuthoritative"));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("SerializeTrajectoryInto") &&
                l.Contains("used flat fallback path"));
        }

        [Fact]
        public void SerializeTrajectoryInto_V1WithStaleTrackSections_FallsBackToFlatTrajectoryAndRoundTripsTail()
        {
            var rec = BuildStaleTrackSectionTailRecording(formatVersion: 1);

            logLines.Clear();
            var node = new ConfigNode("PARSEK_RECORDING");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            ConfigNode[] pointNodes = node.GetNodes("POINT");
            Assert.Equal(3, pointNodes.Length);
            Assert.Empty(node.GetNodes("ORBIT_SEGMENT"));
            Assert.Single(node.GetNodes("TRACK_SECTION"));
            Assert.Equal("False", node.GetValue("sectionAuthoritative"));
            Assert.Equal(120.0, double.Parse(
                pointNodes[2].GetValue("ut"),
                CultureInfo.InvariantCulture));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("SerializeTrajectoryInto") &&
                l.Contains("used flat fallback path"));

            logLines.Clear();
            var restored = new Recording { RecordingId = rec.RecordingId };
            RecordingStore.DeserializeTrajectoryFrom(node, restored);

            Assert.Equal(3, restored.Points.Count);
            Assert.Equal(100.0, restored.Points[0].ut);
            Assert.Equal(110.0, restored.Points[1].ut);
            Assert.Equal(120.0, restored.Points[2].ut);
            Assert.Single(restored.TrackSections);
            Assert.Equal(2, restored.TrackSections[0].frames.Count);
            Assert.Equal(100.0, restored.TrackSections[0].startUT);
            Assert.Equal(110.0, restored.TrackSections[0].endUT);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("DeserializeTrajectoryFrom") &&
                l.Contains("used flat fallback path"));
        }

        [Fact]
        public void SerializeTrajectoryInto_MissingHeader_BackfillsVersionAndRecordingId()
        {
            var rec = new Recording
            {
                RecordingId = "missing-header",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100, latitude = 0, longitude = 0, altitude = 100, bodyName = "Kerbin" });

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion.ToString(CultureInfo.InvariantCulture), node.GetValue("version"));
            Assert.Equal("missing-header", node.GetValue("recordingId"));
        }

        [Fact]
        public void SerializeTrajectoryInto_ExistingHeader_IsPreserved()
        {
            var rec = new Recording
            {
                RecordingId = "preserve-header",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100, latitude = 0, longitude = 0, altitude = 100, bodyName = "Kerbin" });

            var node = new ConfigNode("TEST");
            node.AddValue("version", "42");
            node.AddValue("recordingId", "custom-id");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            Assert.Equal("42", node.GetValue("version"));
            Assert.Equal("custom-id", node.GetValue("recordingId"));
        }

        #endregion

        #region V1 section-authoritative read path

        [Fact]
        public void DeserializeTrajectoryFrom_V1WithTrackSections_RebuildsPointsWithoutBoundaryDupes()
        {
            var fixture = RecordingStorageFixtures.AtmosphericActiveMultiSection();
            var rec = new Recording { RecordingId = fixture.Builder.GetRecordingId() };

            RecordingStore.DeserializeTrajectoryFrom(fixture.Builder.BuildTrajectoryNode(), rec);

            Assert.Equal(5, rec.Points.Count);
            Assert.Equal(2, rec.TrackSections.Count);
            Assert.Equal(17020.0, rec.Points[2].ut);
            Assert.Equal(17030.0, rec.Points[3].ut);

            int sectionFrameCount = rec.TrackSections.Sum(ts => ts.frames.Count);
            Assert.Equal(6, sectionFrameCount);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("DeserializeTrajectoryFrom") &&
                l.Contains("section-authoritative path") &&
                l.Contains("dedupedPointCopies=1"));
        }

        [Fact]
        public void DeserializeTrajectoryFrom_V1WithTrackSections_RebuildsOrbitSegmentsFromCheckpoints()
        {
            var fixture = RecordingStorageFixtures.OrbitalCheckpointTransition();
            var rec = new Recording { RecordingId = fixture.Builder.GetRecordingId() };

            RecordingStore.DeserializeTrajectoryFrom(fixture.Builder.BuildTrajectoryNode(), rec);

            Assert.Equal(3, rec.Points.Count);
            Assert.Single(rec.OrbitSegments);
            Assert.Equal(2, rec.TrackSections.Count);
            Assert.Equal(18320.0, rec.OrbitSegments[0].startUT);
            Assert.Equal(19820.0, rec.OrbitSegments[0].endUT);
        }

        [Fact]
        public void DeserializeTrajectoryFrom_V4OrbitSegmentWithoutIsPredicted_DefaultsFalse()
        {
            var node = new ConfigNode("PARSEK_RECORDING");
            node.AddValue("version", "4");
            node.AddValue("recordingId", "legacy_v4");

            var segNode = node.AddNode("ORBIT_SEGMENT");
            segNode.AddValue("startUT", "100");
            segNode.AddValue("endUT", "200");
            segNode.AddValue("inc", "28.5");
            segNode.AddValue("ecc", "0.01");
            segNode.AddValue("sma", "700000");
            segNode.AddValue("lan", "90");
            segNode.AddValue("argPe", "45");
            segNode.AddValue("mna", "0.2");
            segNode.AddValue("epoch", "100");
            segNode.AddValue("body", "Kerbin");

            var rec = new Recording { RecordingId = "legacy_v4" };
            RecordingStore.DeserializeTrajectoryFrom(node, rec);

            Assert.Single(rec.OrbitSegments);
            Assert.False(rec.OrbitSegments[0].isPredicted);
        }

        [Fact]
        public void DeserializeTrajectoryFrom_V1WithoutTrackSections_UsesFlatFallbackPath()
        {
            var node = new ConfigNode("PARSEK_RECORDING");
            node.AddValue("version", "1");
            node.AddValue("recordingId", "flat_v1");
            AddMinimalPoint(node, 17000.0);
            AddMinimalPoint(node, 17010.0);

            var rec = new Recording { RecordingId = "flat_v1" };
            RecordingStore.DeserializeTrajectoryFrom(node, rec);

            Assert.Equal(2, rec.Points.Count);
            Assert.Empty(rec.TrackSections);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("DeserializeTrajectoryFrom") &&
                l.Contains("used flat fallback path"));
        }

        [Fact]
        public void DeserializeTrajectoryFrom_V1WithTrackSectionsAndFlatTrajectory_UsesFlatFallbackPath()
        {
            var node = new ConfigNode("PARSEK_RECORDING");
            node.AddValue("version", "1");
            node.AddValue("recordingId", "mixed_v1");
            AddMinimalPoint(node, 17000.0);
            AddMinimalPoint(node, 17010.0);

            var seg = node.AddNode("ORBIT_SEGMENT");
            seg.AddValue("startUT", "17010");
            seg.AddValue("endUT", "17500");
            seg.AddValue("inc", "0");
            seg.AddValue("ecc", "0.01");
            seg.AddValue("sma", "700000");
            seg.AddValue("lan", "0");
            seg.AddValue("argPe", "0");
            seg.AddValue("mna", "0");
            seg.AddValue("epoch", "17010");
            seg.AddValue("body", "Kerbin");

            var tsNode = node.AddNode("TRACK_SECTION");
            tsNode.AddValue("env", ((int)SegmentEnvironment.Atmospheric).ToString(CultureInfo.InvariantCulture));
            tsNode.AddValue("ref", ((int)ReferenceFrame.Absolute).ToString(CultureInfo.InvariantCulture));
            tsNode.AddValue("startUT", "17000");
            tsNode.AddValue("endUT", "17010");

            var cpNode = node.AddNode("TRACK_SECTION");
            cpNode.AddValue("env", ((int)SegmentEnvironment.ExoBallistic).ToString(CultureInfo.InvariantCulture));
            cpNode.AddValue("ref", ((int)ReferenceFrame.OrbitalCheckpoint).ToString(CultureInfo.InvariantCulture));
            cpNode.AddValue("startUT", "17010");
            cpNode.AddValue("endUT", "17500");

            var rec = new Recording { RecordingId = "mixed_v1" };
            RecordingStore.DeserializeTrajectoryFrom(node, rec);

            Assert.Equal(2, rec.Points.Count);
            Assert.Single(rec.OrbitSegments);
            Assert.Equal(2, rec.TrackSections.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("DeserializeTrajectoryFrom") &&
                l.Contains("used flat fallback path"));
        }

        [Fact]
        public void DeserializeTrajectoryFrom_V1WithMalformedDuplicatedFlatPrefix_HealsFromTrackSectionPrefixAndKeepsTail()
        {
            var rec = BuildStaleTrackSectionTailRecording(formatVersion: 1);
            rec.Points.Insert(2, MakePoint(100.0, 0.0, 0.0, 1000.0));
            rec.Points.Insert(3, MakePoint(110.0, 0.05, 0.02, 1200.0));

            var node = new ConfigNode("PARSEK_RECORDING");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            logLines.Clear();
            var restored = new Recording { RecordingId = rec.RecordingId };
            RecordingStore.DeserializeTrajectoryFrom(node, restored);

            Assert.Equal(new[] { 100.0, 110.0, 120.0 }, restored.Points.Select(p => p.ut).ToArray());
            Assert.Single(restored.TrackSections);
            Assert.True(restored.FilesDirty);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("DeserializeTrajectoryFrom") &&
                l.Contains("healed malformed flat fallback"));
        }

        [Fact]
        public void DeserializeTrajectoryFrom_V1WithMalformedDuplicatedFlatPrefixWithoutSafeTail_DoesNotRewrite()
        {
            var rec = BuildStaleTrackSectionTailRecording(formatVersion: 1);
            rec.Points.Clear();
            rec.Points.Add(MakePoint(100.0, 0.0, 0.0, 1000.0));
            rec.Points.Add(MakePoint(110.0, 0.05, 0.02, 1200.0));
            rec.Points.Add(MakePoint(100.0, 0.0, 0.0, 1000.0));
            rec.Points.Add(MakePoint(110.0, 0.05, 0.02, 1200.0));
            rec.Points.Add(MakePoint(90.0, -0.03, -0.02, 800.0));

            var node = new ConfigNode("PARSEK_RECORDING");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            logLines.Clear();
            var restored = new Recording { RecordingId = rec.RecordingId };
            RecordingStore.DeserializeTrajectoryFrom(node, restored);

            Assert.Equal(new[] { 100.0, 110.0, 100.0, 110.0, 90.0 }, restored.Points.Select(p => p.ut).ToArray());
            Assert.Single(restored.TrackSections);
            Assert.False(restored.FilesDirty);
            Assert.DoesNotContain(logLines, l => l.Contains("healed malformed flat fallback"));
        }

        [Fact]
        public void DeserializeTrajectoryFrom_V1WithMalformedDuplicatedOrbitPrefix_HealsFromTrackSectionPrefixAndKeepsTail()
        {
            var rec = BuildStaleTrackSectionTailOrbitRecording(formatVersion: 1);
            rec.OrbitSegments.Insert(1, MakeOrbitSegment(100.0, 150.0, 700000.0));

            var node = new ConfigNode("PARSEK_RECORDING");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            logLines.Clear();
            var restored = new Recording { RecordingId = rec.RecordingId };
            RecordingStore.DeserializeTrajectoryFrom(node, restored);

            Assert.Equal(new[] { 100.0, 200.0 }, restored.OrbitSegments.Select(s => s.startUT).ToArray());
            Assert.Single(restored.TrackSections);
            Assert.True(restored.FilesDirty);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("DeserializeTrajectoryFrom") &&
                l.Contains("healed malformed flat fallback"));
        }

        [Fact]
        public void DeserializeTrajectoryFrom_InvalidVersion_LogsWarningAndTreatsAsV0()
        {
            var node = new ConfigNode("PARSEK_RECORDING");
            node.AddValue("version", "bogus");
            node.AddValue("recordingId", "bad_version");
            AddMinimalPoint(node, 17000.0);

            var rec = new Recording { RecordingId = "bad_version" };
            RecordingStore.DeserializeTrajectoryFrom(node, rec);

            Assert.Single(rec.Points);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[RecordingStore]") &&
                l.Contains("invalid trajectory version"));
        }

        #endregion

        #region Helpers

        private static void AddMinimalPoint(ConfigNode parent, double ut)
        {
            var pt = parent.AddNode("POINT");
            pt.AddValue("ut", ut.ToString("R", CultureInfo.InvariantCulture));
            pt.AddValue("lat", "0");
            pt.AddValue("lon", "0");
            pt.AddValue("alt", "100");
            pt.AddValue("rotX", "0");
            pt.AddValue("rotY", "0");
            pt.AddValue("rotZ", "0");
            pt.AddValue("rotW", "1");
            pt.AddValue("body", "Kerbin");
            pt.AddValue("velX", "0");
            pt.AddValue("velY", "0");
            pt.AddValue("velZ", "0");
            pt.AddValue("funds", "0");
            pt.AddValue("science", "0");
            pt.AddValue("rep", "0");
        }

        private static Recording BuildRecorderLoadedToOnRailsFallbackRecording(int formatVersion)
        {
            const uint pid = 9101;
            const string recId = "recorder-loaded-to-onrails";

            var tree = new RecordingTree { Id = "tree_fmt_bg" };
            var rec = new Recording
            {
                RecordingId = recId,
                RecordingFormatVersion = formatVersion,
                VesselName = "Background Booster",
                VesselPersistentId = pid
            };
            tree.Recordings[recId] = rec;
            tree.BackgroundMap[pid] = recId;

            var bgRecorder = new BackgroundRecorder(tree);
            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 100.0);
            bgRecorder.InjectCurrentTrackSectionFrameForTesting(pid, new TrajectoryPoint
            {
                ut = 100.0,
                latitude = 0.0,
                longitude = 0.0,
                altitude = 1000.0,
                rotation = new UnityEngine.Quaternion(0, 0, 0, 1),
                bodyName = "Kerbin",
                velocity = new UnityEngine.Vector3(0, 120, 0)
            });
            bgRecorder.InjectCurrentTrackSectionFrameForTesting(pid, new TrajectoryPoint
            {
                ut = 105.0,
                latitude = 0.01,
                longitude = 0.02,
                altitude = 1500.0,
                rotation = new UnityEngine.Quaternion(0, 0.1f, 0, 0.99f),
                bodyName = "Kerbin",
                velocity = new UnityEngine.Vector3(0, 200, 0)
            });
            bgRecorder.FlushLoadedStateForOnRailsTransitionForTesting(
                pid,
                SegmentEnvironment.ExoBallistic,
                willHavePlayableOnRailsPayload: true,
                boundaryPoint: new TrajectoryPoint
                {
                    ut = 110.0,
                    latitude = 0.02,
                    longitude = 0.04,
                    altitude = 2000.0,
                    rotation = new UnityEngine.Quaternion(0, 0.2f, 0, 0.98f),
                    bodyName = "Kerbin",
                    velocity = new UnityEngine.Vector3(0, 260, 0)
                },
                ut: 110.0);

            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 110.0,
                endUT = 500.0,
                semiMajorAxis = 705000.0,
                eccentricity = 0.01,
                inclination = 28.5,
                longitudeOfAscendingNode = 90.0,
                argumentOfPeriapsis = 45.0,
                meanAnomalyAtEpoch = 0.2,
                epoch = 110.0,
                bodyName = "Kerbin"
            });
            return rec;
        }

        private static Recording BuildStaleTrackSectionTailRecording(int formatVersion)
        {
            var first = MakePoint(100.0, 0.0, 0.0, 1000.0);
            var second = MakePoint(110.0, 0.05, 0.02, 1200.0);
            var tail = MakePoint(120.0, 0.08, 0.06, 900.0);

            var rec = new Recording
            {
                RecordingId = "stale-track-tail",
                RecordingFormatVersion = formatVersion,
                VesselName = "Tail Vessel"
            };
            rec.Points.Add(first);
            rec.Points.Add(second);
            rec.Points.Add(tail);
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Background,
                startUT = first.ut,
                endUT = second.ut,
                frames = new List<TrajectoryPoint> { first, second },
                checkpoints = new List<OrbitSegment>()
            });
            return rec;
        }

        private static Recording BuildStaleTrackSectionTailOrbitRecording(int formatVersion)
        {
            var first = MakeOrbitSegment(100.0, 150.0, 700000.0);
            var tail = MakeOrbitSegment(200.0, 260.0, 705000.0);

            var rec = new Recording
            {
                RecordingId = "stale-track-orbit-tail",
                RecordingFormatVersion = formatVersion,
                VesselName = "Tail Vessel Orbit"
            };
            rec.OrbitSegments.Add(first);
            rec.OrbitSegments.Add(tail);
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                source = TrackSectionSource.Background,
                startUT = first.startUT,
                endUT = first.endUT,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment> { first }
            });
            return rec;
        }

        private static TrajectoryPoint MakePoint(
            double ut,
            double latitude,
            double longitude,
            double altitude)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = latitude,
                longitude = longitude,
                altitude = altitude,
                rotation = new UnityEngine.Quaternion(0, 0, 0, 1),
                bodyName = "Kerbin",
                velocity = new UnityEngine.Vector3(0, 0, 0)
            };
        }

        private static OrbitSegment MakeOrbitSegment(
            double startUT,
            double endUT,
            double semiMajorAxis,
            bool isPredicted = false)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                semiMajorAxis = semiMajorAxis,
                eccentricity = 0.01,
                inclination = 28.5,
                longitudeOfAscendingNode = 90.0,
                argumentOfPeriapsis = 45.0,
                meanAnomalyAtEpoch = 0.2,
                epoch = startUT,
                bodyName = "Kerbin",
                isPredicted = isPredicted
            };
        }

        private static OrbitSegment MakeOrbitSegment(double startUT, double endUT, bool isPredicted)
        {
            var segment = MakeOrbitSegment(startUT, endUT, 700000.0);
            segment.isPredicted = isPredicted;
            return segment;
        }

        #endregion
    }
}
