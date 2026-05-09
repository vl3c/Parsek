using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class FormatRoundtripTests : IDisposable
    {
        private readonly string tempDir;

        public FormatRoundtripTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            tempDir = Path.Combine(
                Path.GetTempPath(),
                "parsek-format-roundtrip-tests-" + Guid.NewGuid().ToString("N"));
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
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void RecordingAnchorChainVersion_PinnedAt11_AndCurrentVersionsTrackV12()
        {
            // PR 3a (format v12) bumped CurrentRecordingFormatVersion to 12
            // (`DebrisParentRecordingFormatVersion`). The anchor-chain layout was
            // introduced at v11 and is unchanged at v12 — `RecordingAnchorChainFormatVersion`
            // / `RecordingAnchorChainBinaryVersion` stay pinned at 11 to identify the
            // anchor-chain boundary, while the current-version constants advance to 12.
            Assert.Equal(11, RecordingStore.RecordingAnchorChainFormatVersion);
            Assert.Equal(
                RecordingStore.RecordingAnchorChainFormatVersion,
                TrajectorySidecarBinary.RecordingAnchorChainBinaryVersion);
            Assert.Equal(12, RecordingStore.CurrentRecordingFormatVersion);
            Assert.Equal(
                RecordingStore.CurrentRecordingFormatVersion,
                TrajectorySidecarBinary.CurrentBinaryVersion);
        }

        [Fact]
        public void BinaryV11_RelativeTrackSection_RoundTripsAnchorRecordingIdWithoutLegacyPid()
        {
            Recording original = BuildV11RelativeAnchorFixture();
            string path = Path.Combine(tempDir, "v11-anchor-recording.prec");

            TrajectorySidecarBinary.Write(path, original, sidecarEpoch: 1);

            Assert.True(TrajectorySidecarBinary.TryProbe(path, out TrajectorySidecarProbe probe));
            // Fresh writes stamp the recording's format version (post-PR-3a v12); the
            // anchor-chain layout introduced at v11 is unchanged at v12 so the probe
            // and recording version match by equality.
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, probe.FormatVersion);
            Assert.True(probe.Supported);

            var restored = new Recording();
            TrajectorySidecarBinary.Read(path, restored, probe);

            Assert.Single(restored.TrackSections);
            TrackSection section = restored.TrackSections[0];
            Assert.Equal("anchor-recording-a", section.anchorRecordingId);
            Assert.Equal(0u, section.anchorVesselId);
            Assert.Equal(2, section.frames.Count);
            Assert.Equal(2, section.absoluteFrames.Count);
        }

        [Fact]
        public void TextV11_TrackSection_WritesAnchorRecordingIdWithoutAnchorPid()
        {
            Recording original = BuildV11RelativeAnchorFixture();
            var node = new ConfigNode("PARSEK_RECORDING");

            TrajectoryTextSidecarCodec.SerializeTrajectoryInto(node, original);

            ConfigNode[] sectionNodes = node.GetNodes("TRACK_SECTION");
            Assert.Single(sectionNodes);
            Assert.Equal("anchor-recording-a", sectionNodes[0].GetValue("anchorRecordingId"));
            Assert.Null(sectionNodes[0].GetValue("anchorPid"));

            var restored = new Recording();
            TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, restored);

            Assert.Single(restored.TrackSections);
            Assert.Equal("anchor-recording-a", restored.TrackSections[0].anchorRecordingId);
            Assert.Equal(0u, restored.TrackSections[0].anchorVesselId);
        }

        private static Recording BuildV11RelativeAnchorFixture()
        {
            var relativeA = MakePoint(100.0, 1.0, 2.0, 3.0);
            var relativeB = MakePoint(101.0, 4.0, 5.0, 6.0);
            var absoluteA = MakePoint(100.0, -0.04, -74.55, 78000.0);
            var absoluteB = MakePoint(101.0, -0.03, -74.54, 78120.0);

            var rec = new Recording
            {
                RecordingId = "v11-anchor-recording",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                VesselName = "Upper Stage",
                VesselPersistentId = 12001u
            };

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
                anchorRecordingId = "anchor-recording-a",
                sampleRateHz = 2f,
                minAltitude = 3f,
                maxAltitude = 6f,
                frames = new List<TrajectoryPoint> { relativeA, relativeB },
                absoluteFrames = new List<TrajectoryPoint> { absoluteA, absoluteB },
                checkpoints = new List<OrbitSegment>()
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
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
                bodyName = "Kerbin",
                recordedGroundClearance = double.NaN
            };
        }
    }
}
