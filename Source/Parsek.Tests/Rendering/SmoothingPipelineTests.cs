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
        private readonly CelestialBody fakeKerbin;
        private const double KerbinRotationPeriod = 21549.425;

        public SmoothingPipelineTests()
        {
            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek_pipeline_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            SmoothingPipeline.ResetForTesting();
            TrajectoryMath.FrameTransform.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;

            // Phase 4: ExoPropulsive / ExoBallistic sections lift to inertial
            // before fitting (design doc §6.2). The lift needs a CelestialBody
            // and its rotationPeriod — both injected via test seams since
            // xUnit cannot stand up FlightGlobals.Bodies.
            fakeKerbin = TestBodyRegistry.CreateBody("Kerbin", radius: 600000.0, gravParameter: 3.5316e12);
            CelestialBody capturedKerbin = fakeKerbin;
            SmoothingPipeline.BodyResolverForTesting = name => name == "Kerbin" ? capturedKerbin : null;
            TrajectoryMath.FrameTransform.RotationPeriodForTesting = b =>
                object.ReferenceEquals(b, capturedKerbin) ? KerbinRotationPeriod : double.NaN;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            SmoothingPipeline.ResetForTesting();
            TrajectoryMath.FrameTransform.ResetForTesting();
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
            // mode kicks in. Phase 4 regression: FrameTag must now be 1
            // (inertial-longitude) — a 0 here means the lift is missing and
            // along-track drift returns.
            var rec = MakeRecording("rec-prop",
                MakeSection(SegmentEnvironment.ExoPropulsive, ReferenceFrame.Absolute, frameCount: 10));
            SmoothingPipeline.FitAndStorePerSection(rec);
            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline("rec-prop", 0, out var spline));
            Assert.True(spline.IsValid);
            Assert.True(spline.KnotsUT.Length >= 4);
            Assert.Equal((byte)1, spline.FrameTag);
        }

        [Fact]
        public void FitAndStorePerSection_AbsoluteExoBallistic_FitsSpline()
        {
            // What makes it fail: ExoBallistic dropped from the eligible set
            // → coast / drifting trajectories never get smoothed (the most
            // visually noticeable Phase 1 case). Phase 4 regression: FrameTag
            // must now be 1 (inertial-longitude); a 0 means the inertial fit
            // path didn't run.
            var rec = MakeRecording("rec-ball",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 10));
            SmoothingPipeline.FitAndStorePerSection(rec);
            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline("rec-ball", 0, out var spline));
            Assert.True(spline.IsValid);
            Assert.Equal((byte)1, spline.FrameTag);
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
        public void LoadOrCompute_StalePann_RecordingIdMismatch_Recomputes()
        {
            // What makes it fail: a .pann from a different recording was
            // copied or left in place under our filename. Every other field
            // can match by chance (same epoch, format, alg stamp, config
            // hash); without an id check, the foreign spline sections would
            // be installed under the current recording's id and produce
            // wrong-coordinate ghost positions. .prec already rejects id
            // mismatches at TrajectorySidecarBinary load — .pann mirrors
            // that defense.
            string pannPath = Path.Combine(tempDir, "rec-mismatch.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            // Write the .pann under a different recording id ("foreign-rec")
            // to a file the loader will look up under "rec-mismatch".
            PannotationsSidecarBinary.Write(pannPath, "foreign-rec",
                sourceSidecarEpoch: 1, sourceRecordingFormatVersion: 7,
                configurationHash: hash, splines: new List<KeyValuePair<int, SmoothingSpline>>());

            var rec = MakeRecording("rec-mismatch",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 10));
            SmoothingPipeline.LoadOrCompute(rec, pannPath);

            Assert.Contains(logLines, l => l.Contains("[INFO][Pipeline-Sidecar]")
                && l.Contains("whole-file invalidation")
                && l.Contains("reason=recording-id-mismatch"));
            // Recompute path should have populated annotations under the
            // *correct* recording id.
            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline("rec-mismatch", 0, out var spline));
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
            Assert.True(PannotationsSidecarBinary.TryRead(pannPath, probe, out var splines, out _, out _));
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

        // --- Phase 4: inertial frame transformation ---

        [Fact]
        public void FitAndStorePerSection_ExoBallistic_FitsInInertial()
        {
            // What makes it fail: missing the lift in FitAndStorePerSection
            // means the spline ControlsY[0] equals the raw body-fixed
            // longitude. Phase 4 requires it to equal lifted longitude
            // (raw + recordingUT*360/period, wrapped). This is THE check that
            // proves the lift ran.
            const double startUT = 100.0;
            var rec = MakeRecording("rec-ball-inertial",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute,
                    frameCount: 10, startUT: startUT));
            SmoothingPipeline.FitAndStorePerSection(rec);

            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline("rec-ball-inertial", 0, out var spline));
            Assert.True(spline.IsValid);
            Assert.Equal((byte)1, spline.FrameTag);

            // Raw frame[0]: longitude = 1.0 at UT=100 (per MakeFrames default).
            // Inertial longitude = 1.0 + 100*360/period, wrapped to (-180,180].
            double phase = (startUT * 360.0) / KerbinRotationPeriod;
            double expectedInertial = 1.0 + phase;
            // Wrap.
            double wrapped = expectedInertial % 360.0;
            if (wrapped > 180.0) wrapped -= 360.0;
            else if (wrapped <= -180.0) wrapped += 360.0;
            Assert.Equal((float)wrapped, spline.ControlsY[0], precision: 4);
        }

        [Fact]
        public void FitAndStorePerSection_ExoPropulsive_FitsInInertial()
        {
            // What makes it fail: same as the ExoBallistic case but for the
            // propulsive environment. Both EXO_* environments must lift; if
            // ExoPropulsive is dropped from the inertial branch, long-burn
            // playback drifts.
            const double startUT = 200.0;
            var rec = MakeRecording("rec-prop-inertial",
                MakeSection(SegmentEnvironment.ExoPropulsive, ReferenceFrame.Absolute,
                    frameCount: 10, startUT: startUT));
            SmoothingPipeline.FitAndStorePerSection(rec);

            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline("rec-prop-inertial", 0, out var spline));
            Assert.Equal((byte)1, spline.FrameTag);

            double phase = (startUT * 360.0) / KerbinRotationPeriod;
            double expectedInertial = 1.0 + phase;
            double wrapped = expectedInertial % 360.0;
            if (wrapped > 180.0) wrapped -= 360.0;
            else if (wrapped <= -180.0) wrapped += 360.0;
            Assert.Equal((float)wrapped, spline.ControlsY[0], precision: 4);
        }

        [Fact]
        public void FitAndStorePerSection_NullBody_LogsWarnAndSkips()
        {
            // What makes it fail: HR-9 — a section whose bodyName cannot be
            // resolved in FlightGlobals.Bodies (planet pack uninstalled, body
            // renamed) must NOT crash, must NOT silently fall back to a body-
            // fixed fit (which would silently corrupt rendering), and MUST
            // surface a Pipeline-Frame Warn so the failure is greppable.
            var sec = MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 10);
            // Override every frame's bodyName to one the resolver doesn't know.
            for (int i = 0; i < sec.frames.Count; i++)
            {
                var p = sec.frames[i];
                p.bodyName = "Bogus";
                sec.frames[i] = p;
            }
            var rec = MakeRecording("rec-bogus", sec);

            SmoothingPipeline.FitAndStorePerSection(rec);

            Assert.False(SectionAnnotationStore.TryGetSmoothingSpline("rec-bogus", 0, out _));
            Assert.Contains(logLines, l => l.Contains("[WARN][Pipeline-Frame]")
                && l.Contains("body not found")
                && l.Contains("recordingId=rec-bogus")
                && l.Contains("bodyName=Bogus"));
        }

        [Fact]
        public void FitAndStorePerSection_LogIncludesFrameTag()
        {
            // What makes it fail: the L1 Spline-fit Info line is the primary
            // observation point for Phase 4 — without `frameTag=1` in the
            // line, no one debugging KSP.log can confirm the inertial path
            // ran. (Atmospheric / Surface* will print frameTag=0 once the
            // gate enables them; preserving the field keeps that future-proof.)
            var rec = MakeRecording("rec-tag",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 8));
            SmoothingPipeline.FitAndStorePerSection(rec);

            Assert.Contains(logLines, l => l.Contains("[INFO][Pipeline-Smoothing]")
                && l.Contains("Spline fit:")
                && l.Contains("recordingId=rec-tag")
                && l.Contains("frameTag=1")
                && l.Contains("body=Kerbin"));

            // Pipeline-Frame Verbose lift-decision line.
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][Pipeline-Frame]")
                && l.Contains("Section lift to inertial decision")
                && l.Contains("recordingId=rec-tag")
                && l.Contains("frameTag=1"));
        }

        // --- Phase 4 Task 4: AlgorithmStampVersion bump ---

        [Fact]
        public void AlgorithmStampBump_V1FilesInvalidatedAsAlgStampDrift()
        {
            // What makes it fail: HR-10 — a Phase-3-shipped .pann (alg stamp
            // v1, body-fixed splines) must NOT be silently reused under the
            // Phase 4 binary (alg stamp v2, inertial splines). The reason
            // token "alg-stamp-drift" is the canonical signal that this
            // happened; tests pin to it so a future bump that forgets to
            // change AlgorithmStampVersion shows up as a green test that
            // shouldn't be green.
            string pannPath = Path.Combine(tempDir, "rec-v1stamp.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(pannPath, "rec-v1stamp",
                sourceSidecarEpoch: 1, sourceRecordingFormatVersion: 7,
                configurationHash: hash, splines: new List<KeyValuePair<int, SmoothingSpline>>());

            // Mutate AlgorithmStampVersion (offset 8..11) to 1 — represents
            // a .pann written by the Phase 3 build before the Phase 4 bump.
            byte[] bytes = File.ReadAllBytes(pannPath);
            bytes[8] = 1; bytes[9] = 0; bytes[10] = 0; bytes[11] = 0;
            File.WriteAllBytes(pannPath, bytes);

            var rec = MakeRecording("rec-v1stamp",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 10));
            SmoothingPipeline.LoadOrCompute(rec, pannPath);

            Assert.Contains(logLines, l => l.Contains("[INFO][Pipeline-Sidecar]")
                && l.Contains("whole-file invalidation")
                && l.Contains("reason=alg-stamp-drift"));
            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline("rec-v1stamp", 0, out var spline));
            // Recompute under Phase 4 produces FrameTag = 1.
            Assert.Equal((byte)1, spline.FrameTag);
        }

        // --- P1#3: stale-spline clearing on recompute ---

        private static SmoothingSpline MakeSentinelSpline(int knotCount = 4, byte frameTag = 0)
        {
            double[] knots = new double[knotCount];
            float[] cx = new float[knotCount];
            float[] cy = new float[knotCount];
            float[] cz = new float[knotCount];
            for (int i = 0; i < knotCount; i++)
            {
                knots[i] = 9000.0 + i;
                cx[i] = 999f + i;
                cy[i] = 999f + i;
                cz[i] = 999f + i;
            }
            return new SmoothingSpline
            {
                SplineType = 0,
                Tension = 0.5f,
                KnotsUT = knots,
                ControlsX = cx,
                ControlsY = cy,
                ControlsZ = cz,
                FrameTag = frameTag,
                IsValid = true,
            };
        }

        [Fact]
        public void FitAndStorePerSection_ClearsPriorEntries_OnRecompute()
        {
            // What makes it fail: P1#3 — without the recording-bucket clear at
            // the top of FitAndStorePerSection, a section that was fit
            // previously but is no longer eligible (or fails the fit this
            // round) keeps its stale spline forever. The Add-only
            // PutSmoothingSpline path means the new fit can never erase a
            // prior section's entry.
            var rec = MakeRecording("rec-stale-fit",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 10));

            // Plant a sentinel spline at a section index the fresh fit will
            // NOT emit (fresh fit emits only sectionIndex=0 here).
            SectionAnnotationStore.PutSmoothingSpline("rec-stale-fit", 99, MakeSentinelSpline());
            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline("rec-stale-fit", 99, out _));

            SmoothingPipeline.FitAndStorePerSection(rec);

            // Sentinel must be gone after recompute.
            Assert.False(SectionAnnotationStore.TryGetSmoothingSpline("rec-stale-fit", 99, out _));
            // Real fit result must still be present.
            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline("rec-stale-fit", 0, out var fresh));
            Assert.True(fresh.IsValid);
        }

        [Fact]
        public void LoadOrCompute_ReadPath_ClearsPriorEntries()
        {
            // What makes it fail: P1#3 read path — without the clear before
            // populating from the freshly-read .pann, a sentinel left in the
            // in-memory store from a prior session (or from a healed
            // recording's earlier sidecar shape) escapes the cache-key
            // freshness check and is silently consumed by the renderer.
            var rec = MakeRecording("rec-stale-read",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 10));
            string pannPath = Path.Combine(tempDir, "rec-stale-read.pann");

            // Prime the .pann via a successful compute+write.
            SmoothingPipeline.LoadOrCompute(rec, pannPath);
            Assert.True(File.Exists(pannPath));

            // Reset in-memory store + plant a sentinel at a section index the
            // .pann's read will NOT emit.
            SectionAnnotationStore.ResetForTesting();
            SectionAnnotationStore.PutSmoothingSpline("rec-stale-read", 99, MakeSentinelSpline());
            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline("rec-stale-read", 99, out _));
            logLines.Clear();

            // Re-load: read path should fire (no drift), populate from disk,
            // and discard the sentinel.
            SmoothingPipeline.LoadOrCompute(rec, pannPath);

            Assert.Contains(logLines, l => l.Contains("[VERBOSE][Pipeline-Sidecar]")
                && l.Contains("Pannotations read OK") && l.Contains("recordingId=rec-stale-read"));
            Assert.False(SectionAnnotationStore.TryGetSmoothingSpline("rec-stale-read", 99, out _));
            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline("rec-stale-read", 0, out _));
        }

        [Fact]
        public void PannotationsHeader_WritesCurrentAlgorithmStamp()
        {
            // What makes it fail: a forgotten bump of AlgorithmStampVersion
            // would leave new .pann files with a stale stamp, so a future
            // phase that bumps further would not be able to invalidate
            // them via alg-stamp-drift. Phase 6 bumped 2 -> 3 to invalidate
            // existing files that lack the AnchorCandidatesList payload.
            // Pin to the constant so a future bump updates the value here
            // and the test still passes.
            var rec = MakeRecording("rec-v2write",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 8));
            string pannPath = Path.Combine(tempDir, "rec-v2write.pann");

            SmoothingPipeline.PersistAfterCommit(rec, pannPath);

            Assert.True(PannotationsSidecarBinary.TryProbe(pannPath, out var probe));
            Assert.True(probe.Success);
            Assert.Equal(PannotationsSidecarBinary.AlgorithmStampVersion, probe.AlgorithmStampVersion);
        }

        // ---- ultrareview P1-A: useAnchorTaxonomy flag in ConfigurationHash ----

        [Fact]
        public void ConfigurationHash_DiffersByUseAnchorTaxonomy()
        {
            // What makes it fail: a hash that ignores the flag would let a
            // .pann written when the flag was off cache-hit when the flag
            // is on (and vice-versa). Phase 6's writer emits an empty
            // AnchorCandidatesList when off and a populated one when on,
            // so the two are NOT equivalent — the cache key must reflect
            // that.
            byte[] hashOn = PannotationsSidecarBinary.ComputeConfigurationHash(
                SmoothingConfiguration.Default, useAnchorTaxonomy: true);
            byte[] hashOff = PannotationsSidecarBinary.ComputeConfigurationHash(
                SmoothingConfiguration.Default, useAnchorTaxonomy: false);
            Assert.NotEqual(hashOn, hashOff);
        }

        [Fact]
        public void ConfigurationHash_SingleArgOverload_DefaultsToFlagOn()
        {
            // What makes it fail: existing call sites (test fixtures pinned
            // before P1-A landed) pass the single-arg overload. They must
            // continue to compute the same hash they always did, which
            // means defaulting to the shipped Phase 6 default (true).
            byte[] singleArg = PannotationsSidecarBinary.ComputeConfigurationHash(
                SmoothingConfiguration.Default);
            byte[] explicitOn = PannotationsSidecarBinary.ComputeConfigurationHash(
                SmoothingConfiguration.Default, useAnchorTaxonomy: true);
            Assert.Equal(singleArg, explicitOn);
        }

        [Fact]
        public void LoadOrCompute_DiscardsAndRecomputesOn_UseAnchorTaxonomy_FlagFlip()
        {
            // What makes it fail: the canonical regression for ultrareview
            // P1-A. Write a .pann while the flag is off, then load it
            // while the flag is on. Without the flag in the
            // ConfigurationHash the load path treats the file as fresh
            // and skips the lazy-recompute → §7.4-§7.10 anchors never
            // re-emit. With the flag in the hash, the load surfaces
            // config-hash-drift and the recompute fires.
            var rec = MakeRecording("rec-flag-flip",
                MakeSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, frameCount: 8));
            string pannPath = Path.Combine(tempDir, "rec-flag-flip.pann");

            // Write .pann with flag off.
            AnchorCandidateBuilder.UseAnchorTaxonomyOverrideForTesting = false;
            try { SmoothingPipeline.PersistAfterCommit(rec, pannPath); }
            finally { AnchorCandidateBuilder.ResetForTesting(); }
            Assert.True(File.Exists(pannPath));

            // Re-read with flag on. The cached hash should differ from
            // the hash baked into the file → ClassifyDrift returns
            // config-hash-drift → orchestrator logs invalidation, runs
            // FitAndStorePerSection, rewrites .pann.
            AnchorCandidateBuilder.UseAnchorTaxonomyOverrideForTesting = true;
            // Reset the cached hash so CurrentConfigurationHash recomputes
            // for the new flag value. ResetForTesting also reseats the
            // body resolver / surface lookup seams the test ctor injected,
            // so we re-install them after the reset.
            SmoothingPipeline.ResetForTesting();
            CelestialBody capturedKerbin = fakeKerbin;
            SmoothingPipeline.BodyResolverForTesting = name => name == "Kerbin" ? capturedKerbin : null;
            TrajectoryMath.FrameTransform.RotationPeriodForTesting = b =>
                object.ReferenceEquals(b, capturedKerbin) ? KerbinRotationPeriod : double.NaN;
            AnchorCandidateBuilder.UseAnchorTaxonomyOverrideForTesting = true;
            try { SmoothingPipeline.LoadOrCompute(rec, pannPath); }
            finally { AnchorCandidateBuilder.ResetForTesting(); }

            // The Pipeline-Sidecar invalidation Info line should fire with
            // reason=config-hash-drift (the canonical token for hash
            // mismatch).
            Assert.Contains(logLines,
                l => l.Contains("[INFO][Pipeline-Sidecar]")
                    && l.Contains("Pannotations whole-file invalidation")
                    && l.Contains("recordingId=rec-flag-flip")
                    && l.Contains("reason=config-hash-drift"));
            Assert.Contains(logLines,
                l => l.Contains("[INFO][Pipeline-Smoothing]")
                    && l.Contains("Lazy compute")
                    && l.Contains("recordingId=rec-flag-flip")
                    && l.Contains("reason=config-hash-drift"));
        }
    }
}
