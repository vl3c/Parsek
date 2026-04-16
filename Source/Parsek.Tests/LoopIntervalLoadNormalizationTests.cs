using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// #412 regression: recordings with <c>LoopPlayback=true</c> and
    /// <c>LoopIntervalSeconds</c> below <c>MinCycleDuration</c> (e.g. the legacy 0.0 default
    /// written by older synthetic fixtures) must be auto-repaired to a usable period on load
    /// so <c>ResolveLoopInterval</c> never sees a degenerate value at playback.
    /// </summary>
    [Collection("Sequential")]
    public class LoopIntervalLoadNormalizationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public LoopIntervalLoadNormalizationTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            GhostPlaybackLogic.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek-412-normalize-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GhostPlaybackLogic.ResetForTesting();

            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        private static Recording BuildRecordingWithTrajectory(
            string recordingId, string vesselName,
            double startUT, double endUT,
            double loopIntervalSeconds, bool loopPlayback)
        {
            var rec = new Recording
            {
                RecordingId = recordingId,
                VesselName = vesselName,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                LoopPlayback = loopPlayback,
                LoopIntervalSeconds = loopIntervalSeconds,
                LoopTimeUnit = LoopTimeUnit.Sec,
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = startUT, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });
            return rec;
        }

        private string WriteSidecar(Recording rec, int epoch = 1)
        {
            string path = Path.Combine(tempDir, rec.RecordingId + ".prec");
            RecordingStore.WriteTrajectorySidecar(path, rec, sidecarEpoch: epoch);
            rec.SidecarEpoch = epoch;
            return path;
        }

        [Fact]
        public void LoadRecordingFiles_LoopEnabledWithZeroInterval_NormalizesToTrajectoryDuration()
        {
            // On-disk: loop on, interval 0 (pre-#412 synthetic fixture), 68 s trajectory.
            var original = BuildRecordingWithTrajectory(
                "rec-412-pad-walk", "Pad Walk",
                startUT: 100_000, endUT: 100_068,
                loopIntervalSeconds: 0.0, loopPlayback: true);
            string precPath = WriteSidecar(original);

            // Loader sees .sfs-derived state: loop on, interval 0, format v4, trajectory empty
            // until sidecar hydration. This mirrors RecordingTree.LoadLoopFields handing a
            // partially-populated Recording to RecordingStore.LoadRecordingFiles.
            var loaded = new Recording
            {
                RecordingId = original.RecordingId,
                VesselName = original.VesselName,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                LoopPlayback = true,
                LoopIntervalSeconds = 0.0,
                LoopTimeUnit = LoopTimeUnit.Sec,
                SidecarEpoch = 1,
            };

            Assert.True(RecordingStore.LoadRecordingFilesFromPathsForTesting(
                loaded, precPath, vesselPath: null, ghostPath: null));

            Assert.Equal(68.0, loaded.LoopIntervalSeconds);
            Assert.Equal(1, logLines.Count(l =>
                l.Contains("[Loop]") &&
                l.Contains("NormalizeDegenerateLoopInterval") &&
                l.Contains("'Pad Walk'") &&
                l.Contains("normalizing to 68")));
        }

        [Fact]
        public void LoadRecordingFiles_LoopEnabledWithZeroInterval_FallsBackWhenDurationTooShort()
        {
            // Trajectory duration is below MinCycleDuration (1 s) — normalization must fall
            // through to DefaultLoopIntervalSeconds so the recording still loops at a sane rate.
            var original = BuildRecordingWithTrajectory(
                "rec-412-brief", "Brief",
                startUT: 0.0, endUT: 0.5,
                loopIntervalSeconds: 0.0, loopPlayback: true);
            string precPath = WriteSidecar(original);

            var loaded = new Recording
            {
                RecordingId = original.RecordingId,
                VesselName = original.VesselName,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                LoopPlayback = true,
                LoopIntervalSeconds = 0.0,
                LoopTimeUnit = LoopTimeUnit.Sec,
                SidecarEpoch = 1,
            };

            Assert.True(RecordingStore.LoadRecordingFilesFromPathsForTesting(
                loaded, precPath, vesselPath: null, ghostPath: null));

            Assert.Equal(GhostPlaybackLogic.DefaultLoopIntervalSeconds, loaded.LoopIntervalSeconds);
        }

        [Fact]
        public void LoadRecordingFiles_LoopDisabled_DoesNotNormalize()
        {
            // Loop off — resolver never reads the interval, so leave the raw field alone and
            // emit no warning even if the value is 0.
            var original = BuildRecordingWithTrajectory(
                "rec-412-nonloop", "NonLoop",
                startUT: 0, endUT: 30,
                loopIntervalSeconds: 0.0, loopPlayback: false);
            string precPath = WriteSidecar(original);

            var loaded = new Recording
            {
                RecordingId = original.RecordingId,
                VesselName = original.VesselName,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                LoopPlayback = false,
                LoopIntervalSeconds = 0.0,
                LoopTimeUnit = LoopTimeUnit.Sec,
                SidecarEpoch = 1,
            };

            Assert.True(RecordingStore.LoadRecordingFilesFromPathsForTesting(
                loaded, precPath, vesselPath: null, ghostPath: null));

            Assert.Equal(0.0, loaded.LoopIntervalSeconds);
            Assert.DoesNotContain(logLines, l => l.Contains("NormalizeDegenerateLoopInterval"));
        }

        [Fact]
        public void LoadRecordingFiles_LoopAutoMode_DoesNotNormalize()
        {
            // Auto-mode pulls the interval from the global slider via ResolveLoopInterval, so
            // the per-recording value is irrelevant — leave it as-is even if loop is on.
            var original = BuildRecordingWithTrajectory(
                "rec-412-auto", "AutoMode",
                startUT: 0, endUT: 42,
                loopIntervalSeconds: 0.0, loopPlayback: true);
            original.LoopTimeUnit = LoopTimeUnit.Auto;
            string precPath = WriteSidecar(original);

            var loaded = new Recording
            {
                RecordingId = original.RecordingId,
                VesselName = original.VesselName,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                LoopPlayback = true,
                LoopIntervalSeconds = 0.0,
                LoopTimeUnit = LoopTimeUnit.Auto,
                SidecarEpoch = 1,
            };

            Assert.True(RecordingStore.LoadRecordingFilesFromPathsForTesting(
                loaded, precPath, vesselPath: null, ghostPath: null));

            Assert.Equal(0.0, loaded.LoopIntervalSeconds);
            Assert.DoesNotContain(logLines, l => l.Contains("NormalizeDegenerateLoopInterval"));
        }

        [Fact]
        public void LoadRecordingFiles_HealthyInterval_PassesThroughUntouched()
        {
            var original = BuildRecordingWithTrajectory(
                "rec-412-healthy", "Healthy",
                startUT: 100, endUT: 200,
                loopIntervalSeconds: 45.0, loopPlayback: true);
            string precPath = WriteSidecar(original);

            var loaded = new Recording
            {
                RecordingId = original.RecordingId,
                VesselName = original.VesselName,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                LoopPlayback = true,
                LoopIntervalSeconds = 45.0,
                LoopTimeUnit = LoopTimeUnit.Sec,
                SidecarEpoch = 1,
            };

            Assert.True(RecordingStore.LoadRecordingFilesFromPathsForTesting(
                loaded, precPath, vesselPath: null, ghostPath: null));

            Assert.Equal(45.0, loaded.LoopIntervalSeconds);
            Assert.DoesNotContain(logLines, l => l.Contains("NormalizeDegenerateLoopInterval"));
        }

        [Fact]
        public void ResolveLoopInterval_AfterNormalization_DoesNotFireClampWarning()
        {
            // End-to-end proof: a freshly loaded recording whose on-disk interval was 0 must,
            // after the normalization pass, pass through ResolveLoopInterval silently.
            var original = BuildRecordingWithTrajectory(
                "rec-412-e2e", "E2E",
                startUT: 0, endUT: 68,
                loopIntervalSeconds: 0.0, loopPlayback: true);
            string precPath = WriteSidecar(original);

            var loaded = new Recording
            {
                RecordingId = original.RecordingId,
                VesselName = original.VesselName,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                LoopPlayback = true,
                LoopIntervalSeconds = 0.0,
                LoopTimeUnit = LoopTimeUnit.Sec,
                SidecarEpoch = 1,
            };

            Assert.True(RecordingStore.LoadRecordingFilesFromPathsForTesting(
                loaded, precPath, vesselPath: null, ghostPath: null));

            logLines.Clear();
            for (int i = 0; i < 100; i++)
            {
                double resolved = GhostPlaybackLogic.ResolveLoopInterval(
                    loaded,
                    globalAutoInterval: GhostPlaybackLogic.DefaultLoopIntervalSeconds,
                    defaultInterval: GhostPlaybackLogic.DefaultLoopIntervalSeconds,
                    minCycleDuration: GhostPlaybackLogic.MinCycleDuration);
                Assert.Equal(68.0, resolved);
            }

            Assert.DoesNotContain(logLines, l =>
                l.Contains("ResolveLoopInterval:") && l.Contains("MinCycleDuration"));
        }
    }
}
