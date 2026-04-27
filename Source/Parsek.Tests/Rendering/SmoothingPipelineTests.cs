using System;
using System.Collections.Generic;
using System.IO;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Behavioural tests for the Phase 1 <see cref="SmoothingPipeline"/>
    /// orchestrator (design doc §17.3.1, §18 Phase 1, §19.2 Stage 1 +
    /// Sidecar tables, HR-1 / HR-7 / HR-9 / HR-10 / HR-12). Touches disk
    /// (writes / reads .pann via SmoothingPipeline + PannotationsSidecarBinary)
    /// and shared static state (SectionAnnotationStore, ParsekLog), so runs
    /// in the Sequential collection.
    /// </summary>
    [Collection("Sequential")]
    public class SmoothingPipelineTests : IDisposable
    {
        private readonly string tempDir;
        private readonly List<string> logLines = new List<string>();

        public SmoothingPipelineTests()
        {
            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek_pipeline_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            SmoothingPipeline.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            SmoothingPipeline.ResetForTesting();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // --- fixtures ---

        private static List<TrajectoryPoint> MakeFrames(int count, double startUT, double dutPerSample)
        {
            var frames = new List<TrajectoryPoint>(count);
            for (int i = 0; i < count; i++)
            {
                frames.Add(new TrajectoryPoint
                {
                    ut = startUT + i * dutPerSample,
                    latitude = 0.1 + i * 0.01,
                    longitude = 1.0 + i * 0.05,
                    altitude = 80000 + i * 100,
                    rotation = Quaternion.identity,
                    bodyName = "Kerbin",
                });
            }
            return frames;
        }

        private static TrackSection MakeSection(SegmentEnvironment env, ReferenceFrame refFrame,
            int frameCount, double startUT = 100.0, double dutPerSample = 1.0)
        {
            return new TrackSection
            {
                environment = env,
                referenceFrame = refFrame,
                source = TrackSectionSource.Active,
                startUT = startUT,
                endUT = startUT + (frameCount - 1) * dutPerSample,
                anchorVesselId = 0,
                frames = MakeFrames(frameCount, startUT, dutPerSample),
                checkpoints = new List<OrbitSegment>(),
                sampleRateHz = 1f / (float)dutPerSample,
                boundaryDiscontinuityMeters = 0f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
            };
        }

        private static Recording MakeRecording(string id, params TrackSection[] sections)
        {
            var rec = new Recording
            {
                RecordingId = id,
                RecordingFormatVersion = 7,
                SidecarEpoch = 1,
            };
            rec.TrackSections.AddRange(sections);
            return rec;
        }

        // --- T5c.1: per-section fit ---

        [Fact]
        public void FitAndStorePerSection_AbsoluteExoPropulsive_FitsSpline()
        {
            // What makes it fail: ExoPropulsive isn't recognised as eligible →
            // stores no spline → smoothing never runs and HR-9 silent-failure
            // mode kicks in.
            var rec = MakeRecording("rec-prop",
                MakeSection(SegmentEnvironment.ExoPropulsive, ReferenceFrame.Absolute, frameCount: 10));
            SmoothingPipeline.FitAndStorePerSection(rec);
            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline("rec-prop", 0, out var spline));
            Assert.True(spline.IsValid);
            Assert.True(spline.KnotsUT.Length >= 4);
        }

        [Fact]
        public void FitAndStorePerSection_AbsoluteExoBallistic_FitsSpline()
        {
            // What makes it fail: ExoBallistic dropped from the eligible set
            // → coast / drifting trajectories never get smoothed (the most
            // visually noticeable Phase 1 case).
            var rec = MakeRecording("rec-ball",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 10));
            SmoothingPipeline.FitAndStorePerSection(rec);
            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline("rec-ball", 0, out var spline));
            Assert.True(spline.IsValid);
        }

        [Fact]
        public void FitAndStorePerSection_Atmospheric_NotFitted()
        {
            // What makes it fail: Phase 1 scoping creep — Atmospheric belongs
            // to Phase 7. Fitting it here would silently change atmospheric
            // ghost rendering behaviour.
            var rec = MakeRecording("rec-atmo",
                MakeSection(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, frameCount: 10));
            SmoothingPipeline.FitAndStorePerSection(rec);
            Assert.False(SectionAnnotationStore.TryGetSmoothingSpline("rec-atmo", 0, out _));
            Assert.Equal(0, SectionAnnotationStore.GetSplineCountForRecording("rec-atmo"));
        }

        [Fact]
        public void FitAndStorePerSection_RelativeFrame_NotFitted()
        {
            // What makes it fail: HR-7 violation — smoothing across the
            // anchor-local v6/v7 frame would mix metres-along-anchor-axes
            // values with body-fixed degrees and produce gibberish positions.
            var rec = MakeRecording("rec-rel",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Relative, frameCount: 10));
            SmoothingPipeline.FitAndStorePerSection(rec);
            Assert.False(SectionAnnotationStore.TryGetSmoothingSpline("rec-rel", 0, out _));
        }

        [Fact]
        public void FitAndStorePerSection_OrbitalCheckpoint_NotFitted()
        {
            // What makes it fail: HR-7 — checkpoints are analytical orbit
            // data (Keplerian elements at discrete UTs), not body-fixed
            // sample frames. Fitting them would produce splines that don't
            // correspond to any real trajectory.
            var rec = MakeRecording("rec-ckpt",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, frameCount: 10));
            SmoothingPipeline.FitAndStorePerSection(rec);
            Assert.False(SectionAnnotationStore.TryGetSmoothingSpline("rec-ckpt", 0, out _));
        }

        [Fact]
        public void FitAndStorePerSection_FewerThanMinSamples_LogsWarn()
        {
            // What makes it fail: 3-sample sections must NOT crash the fitter
            // and must surface the rejection in KSP.log so anyone diagnosing
            // a not-smoothed-as-expected section can grep for the reason.
            // Build a section with a frames count that triggers the gating
            // but is high enough to pass the orchestrator's MinSamples check
            // (which mirrors CatmullRomFit's internal 4-sample requirement).
            // The orchestrator's MinSamples = 4 means a 3-sample section is
            // skipped entirely (no Warn) — for the Warn path we need to push
            // a section with >= MinSamples but with a NaN that fails the fit.
            var nanSection = new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = 100.0,
                endUT = 105.0,
                anchorVesselId = 0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, latitude = 0, longitude = 0, altitude = 80000, rotation = Quaternion.identity, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 101.0, latitude = double.NaN, longitude = 0, altitude = 80000, rotation = Quaternion.identity, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 102.0, latitude = 0, longitude = 0, altitude = 80000, rotation = Quaternion.identity, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 103.0, latitude = 0, longitude = 0, altitude = 80000, rotation = Quaternion.identity, bodyName = "Kerbin" },
                },
                checkpoints = new List<OrbitSegment>(),
                sampleRateHz = 1f,
            };
            var rec = MakeRecording("rec-nan", nanSection);
            SmoothingPipeline.FitAndStorePerSection(rec);

            Assert.Contains(logLines, l => l.Contains("[WARN][Pipeline-Smoothing]")
                && l.Contains("Catmull-Rom fit failed")
                && l.Contains("recordingId=rec-nan"));
        }

        // --- T5c.7-12: LoadOrCompute drift / cache key behaviour ---

        [Fact]
        public void LoadOrCompute_PannMissing_FitsAndWrites()
        {
            // What makes it fail: missing .pann should trigger lazy compute
            // and write — without this, every load would re-fit (perf hit)
            // and the write step would never validate (HR-12 atomic write).
            var rec = MakeRecording("rec-miss",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 10));
            string pannPath = Path.Combine(tempDir, "rec-miss.pann");

            SmoothingPipeline.LoadOrCompute(rec, pannPath);

            Assert.True(File.Exists(pannPath));
            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline("rec-miss", 0, out var spline));
            Assert.True(spline.IsValid);
            Assert.Contains(logLines, l => l.Contains("[Pipeline-Smoothing]")
                && l.Contains("Lazy compute") && l.Contains("reason=file-missing"));
        }

        [Fact]
        public void LoadOrCompute_PannPresent_NoRefit()
        {
            // What makes it fail: a fresh .pann should be read, not refit —
            // re-fitting on every load defeats the cache and is HR-9 noise
            // ("the smoothing pipeline ran" without a fit-failure shouldn't
            // emit a Pipeline-Smoothing Info "Spline fit" line).
            var rec = MakeRecording("rec-warm",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 10));
            string pannPath = Path.Combine(tempDir, "rec-warm.pann");

            // First call: lazy compute + write.
            SmoothingPipeline.LoadOrCompute(rec, pannPath);
            // Reset store + log so the second call's behaviour is observable.
            SectionAnnotationStore.ResetForTesting();
            logLines.Clear();

            SmoothingPipeline.LoadOrCompute(rec, pannPath);

            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline("rec-warm", 0, out _));
            Assert.DoesNotContain(logLines, l => l.Contains("[INFO][Pipeline-Smoothing]")
                && l.Contains("Spline fit:") && l.Contains("recordingId=rec-warm"));
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][Pipeline-Sidecar]")
                && l.Contains("Pannotations read OK") && l.Contains("recordingId=rec-warm"));
        }

        [Fact]
        public void LoadOrCompute_StalePann_AlgStampMismatch_Recomputes()
        {
            // What makes it fail: HR-10 — silently accepting a .pann written
            // by a different algorithm version would feed stale annotations
            // to the renderer.
            string pannPath = Path.Combine(tempDir, "rec-alg.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(pannPath, "rec-alg",
                sourceSidecarEpoch: 1, sourceRecordingFormatVersion: 7,
                configurationHash: hash, splines: new List<KeyValuePair<int, SmoothingSpline>>());

            // Mutate AlgorithmStampVersion (offset 8..11 = magic[0..3] + binVer[4..7] + algStamp[8..11])
            byte[] bytes = File.ReadAllBytes(pannPath);
            bytes[8] = 99; bytes[9] = 0; bytes[10] = 0; bytes[11] = 0;
            File.WriteAllBytes(pannPath, bytes);

            var rec = MakeRecording("rec-alg",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 10));
            SmoothingPipeline.LoadOrCompute(rec, pannPath);

            Assert.Contains(logLines, l => l.Contains("[INFO][Pipeline-Sidecar]")
                && l.Contains("whole-file invalidation")
                && l.Contains("reason=alg-stamp-drift"));
            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline("rec-alg", 0, out var spline));
            Assert.True(spline.IsValid);
        }

        [Fact]
        public void LoadOrCompute_StalePann_EpochDrift_Recomputes()
        {
            // What makes it fail: HR-10 — a .prec rewrite (supersede commit,
            // healing pass) bumps SidecarEpoch but if the orchestrator didn't
            // verify, stale splines from the previous epoch would be reused.
            string pannPath = Path.Combine(tempDir, "rec-ep.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(pannPath, "rec-ep",
                sourceSidecarEpoch: 1, sourceRecordingFormatVersion: 7,
                configurationHash: hash, splines: new List<KeyValuePair<int, SmoothingSpline>>());

            // The recording has SidecarEpoch=2 — file holds epoch=1.
            var rec = MakeRecording("rec-ep",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 10));
            rec.SidecarEpoch = 2;
            SmoothingPipeline.LoadOrCompute(rec, pannPath);

            Assert.Contains(logLines, l => l.Contains("[INFO][Pipeline-Sidecar]")
                && l.Contains("whole-file invalidation")
                && l.Contains("reason=epoch-drift"));
        }

        [Fact]
        public void LoadOrCompute_StalePann_FormatDrift_Recomputes()
        {
            // What makes it fail: HR-10 — recording-format version bumps
            // (e.g. v7 → v8) reshape what the splines represent. Reusing
            // splines fitted at v7 against a v8 recording would silently
            // smooth the wrong coordinate space.
            string pannPath = Path.Combine(tempDir, "rec-fmt.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(pannPath, "rec-fmt",
                sourceSidecarEpoch: 1, sourceRecordingFormatVersion: 6,
                configurationHash: hash, splines: new List<KeyValuePair<int, SmoothingSpline>>());

            var rec = MakeRecording("rec-fmt",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 10));
            // Recording is v7 — file holds v6.
            SmoothingPipeline.LoadOrCompute(rec, pannPath);

            Assert.Contains(logLines, l => l.Contains("[INFO][Pipeline-Sidecar]")
                && l.Contains("whole-file invalidation")
                && l.Contains("reason=format-drift"));
        }

        [Fact]
        public void LoadOrCompute_StalePann_ConfigHashDrift_Recomputes()
        {
            // What makes it fail: HR-10 — perturbing a tunable (tension, etc.)
            // changes the canonical hash. If the orchestrator skipped this
            // check, future tunable changes would silently reuse the old
            // splines.
            string pannPath = Path.Combine(tempDir, "rec-cfg.pann");
            byte[] realHash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(pannPath, "rec-cfg",
                sourceSidecarEpoch: 1, sourceRecordingFormatVersion: 7,
                configurationHash: realHash,
                splines: new List<KeyValuePair<int, SmoothingSpline>>());

            // Mutate a single byte of the configuration hash (offset 20..51 in header:
            // magic(4) + binVer(4) + algStamp(4) + epoch(4) + fmtVer(4) = 20, so hash starts at 20).
            byte[] bytes = File.ReadAllBytes(pannPath);
            bytes[20] = (byte)(bytes[20] ^ 0xFF);
            File.WriteAllBytes(pannPath, bytes);

            var rec = MakeRecording("rec-cfg",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 10));
            SmoothingPipeline.LoadOrCompute(rec, pannPath);

            Assert.Contains(logLines, l => l.Contains("[INFO][Pipeline-Sidecar]")
                && l.Contains("whole-file invalidation")
                && l.Contains("reason=config-hash-drift"));
        }

        // --- T5c.13-15: PersistAfterCommit ---

        [Fact]
        public void PersistAfterCommit_WritesPannSibling()
        {
            // What makes it fail: commit-time path that doesn't write .pann
            // means every subsequent load would re-fit (defeating the cache).
            var rec = MakeRecording("rec-commit",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 8));
            string pannPath = Path.Combine(tempDir, "rec-commit.pann");

            SmoothingPipeline.PersistAfterCommit(rec, pannPath);

            Assert.True(File.Exists(pannPath));
            Assert.True(PannotationsSidecarBinary.TryProbe(pannPath, out var probe));
            Assert.True(probe.Success);
            Assert.True(probe.Supported);
            Assert.Equal("rec-commit", probe.RecordingId);
            Assert.Equal(rec.SidecarEpoch, probe.SourceSidecarEpoch);
            Assert.True(PannotationsSidecarBinary.TryRead(pannPath, probe, out var splines, out _));
            Assert.Single(splines);
            Assert.Equal(0, splines[0].Key);
            Assert.True(splines[0].Value.IsValid);
        }

        [Fact]
        public void PersistAfterCommit_PrecUntouched()
        {
            // What makes it fail: HR-1 violation — SmoothingPipeline must
            // never touch .prec. Even an mtime change here would mean the
            // pipeline secretly mutated raw recording data.
            string precPath = Path.Combine(tempDir, "rec-untouch.prec");
            File.WriteAllBytes(precPath, new byte[] { 1, 2, 3, 4, 5 });
            DateTime originalMtime = File.GetLastWriteTimeUtc(precPath);

            // Sleep a moment so any spurious mtime bump would be visible.
            System.Threading.Thread.Sleep(20);

            var rec = MakeRecording("rec-untouch",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 8));
            string pannPath = Path.Combine(tempDir, "rec-untouch.pann");
            SmoothingPipeline.PersistAfterCommit(rec, pannPath);

            Assert.Equal(originalMtime, File.GetLastWriteTimeUtc(precPath));
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(precPath));
        }

        [Fact]
        public void PersistAfterCommit_WriteFailure_LogsWarn_DoesNotThrow()
        {
            // What makes it fail: HR-9 — a .pann write failure must NEVER
            // propagate up to abort the user-facing commit. The cache is
            // regenerable; visibility comes from a Warn line.
            var rec = MakeRecording("rec-fail",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 8));

            // Non-existent drive letter forces File.WriteAllBytes to throw
            // DirectoryNotFoundException — exactly the disk-full / disk-
            // unavailable IO failure HR-9 has to absorb.
            string pannPath = @"Z:\parsek-nonexistent-drive\rec-fail.pann";

            Exception caught = Record.Exception(() =>
                SmoothingPipeline.PersistAfterCommit(rec, pannPath));

            Assert.Null(caught);
            Assert.Contains(logLines, l => l.Contains("[WARN][Pipeline-Sidecar]")
                && l.Contains("Pannotations write failure")
                && l.Contains("recordingId=rec-fail"));
        }

        // --- T5c.16: Pipeline-Format line ---

        [Fact]
        public void LoadOrCompute_DegradedFormatVersion_LogsPipelineFormat()
        {
            // What makes it fail: skipping the Pipeline-Format Info line on
            // load means future phases that DO have format-version-gated
            // features (Phase 7 / 9) wouldn't be able to surface "this older
            // recording loaded with a degraded feature set" without adding
            // their own log site. Log it once now so the location is stable.
            var rec = MakeRecording("rec-v5",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 8));
            rec.RecordingFormatVersion = 5;
            string pannPath = Path.Combine(tempDir, "rec-v5.pann");

            SmoothingPipeline.LoadOrCompute(rec, pannPath);

            Assert.Contains(logLines, l => l.Contains("[INFO][Pipeline-Format]")
                && l.Contains("recordingId=rec-v5")
                && l.Contains("formatVersion=5")
                && l.Contains("degradedFeatures=[]"));
        }
    }
}
