using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class Bug458BinaryFlatFallbackHealTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public Bug458BinaryFlatFallbackHealTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(Path.GetTempPath(), "parsek-bug458-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = null;
            RecordingStore.ResetForTesting();

            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        [Fact]
        public void BinaryFlatFallbackWithDuplicatedTrackSectionPrefix_HealsOnLoadAndMarksFilesDirty()
        {
            var written = BuildMalformedBinaryFlatFallbackRecording();
            string precPath = Path.Combine(tempDir, written.RecordingId + ".prec");
            string vesselPath = Path.Combine(tempDir, written.RecordingId + "_vessel.craft");
            string ghostPath = Path.Combine(tempDir, written.RecordingId + "_ghost.craft");

            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                written, precPath, vesselPath, ghostPath, incrementEpoch: true));

            var restored = new Recording { RecordingId = written.RecordingId };
            logLines.Clear();

            Assert.True(RecordingStore.LoadTrajectorySidecarForTesting(precPath, restored));

            Assert.Equal(14, restored.Points.Count);
            Assert.Single(restored.TrackSections);
            Assert.True(restored.FilesDirty);
            Assert.Equal(215.84, restored.Points[restored.Points.Count - 1].ut, 6);
            for (int i = 1; i < restored.Points.Count; i++)
            {
                Assert.True(restored.Points[i].ut >= restored.Points[i - 1].ut,
                    $"Non-monotonic point at {i}: {restored.Points[i].ut} < {restored.Points[i - 1].ut}");
            }

            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("ReadBinaryTrajectoryFile") &&
                l.Contains("used flat fallback path") &&
                l.Contains("healed=true") &&
                l.Contains("prePoints=27") &&
                l.Contains("postPoints=14"));
        }

        private static Recording BuildMalformedBinaryFlatFallbackRecording()
        {
            var rec = new Recording
            {
                RecordingId = "bug458-malformed-binary-flat-fallback",
                RecordingFormatVersion = 3
            };

            var frames = new List<TrajectoryPoint>();
            double[] uts =
            {
                155.84, 156.86, 157.52, 158.24, 159.12, 160.10, 161.20,
                162.38, 163.62, 164.92, 166.92, 168.92, 170.92
            };
            for (int i = 0; i < uts.Length; i++)
                frames.Add(MakePoint(uts[i], i));

            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = frames[0].ut,
                endUT = frames[frames.Count - 1].ut,
                source = TrackSectionSource.Background,
                frames = new List<TrajectoryPoint>(frames),
                checkpoints = new List<OrbitSegment>()
            });

            rec.Points.AddRange(frames);
            rec.Points.AddRange(frames);
            rec.Points.Add(MakePoint(215.84, uts.Length));
            return rec;
        }

        private static TrajectoryPoint MakePoint(double ut, int index)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = 0.1 + (index * 0.01),
                longitude = -74.0 + (index * 0.02),
                altitude = 1000.0 + (index * 25.0),
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(1f + index, 2f + index, 3f + index),
                bodyName = "Kerbin",
                funds = 0.0,
                science = 0.0f,
                reputation = 0.0f
            };
        }
    }
}
