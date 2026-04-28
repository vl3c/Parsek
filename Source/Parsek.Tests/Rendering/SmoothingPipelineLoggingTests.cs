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
    /// Per-log-line contract tests for the Phase 1 <see cref="SmoothingPipeline"/>
    /// (design doc §19.2 Stage 1 + Sidecar tables, log lines L1-L11). One test
    /// per line, captured via <see cref="ParsekLog.TestSinkForTesting"/>.
    /// </summary>
    /// <remarks>
    /// L4 (per-frame VerboseRateLimited eval count) is asserted in the
    /// consumer-side hot-path tests — it lives in <c>ParsekFlight</c>'s
    /// per-frame pipeline, not in this orchestrator. Skipped here so this
    /// suite stays focused on the orchestrator surface.
    ///
    /// L5 (settings-flip Info) is owned by
    /// <c>UseSmoothingSplinesSettingTests.UseSmoothingSplines_FlipLogsInfo</c>
    /// — not duplicated here.
    ///
    /// L10 (atomic-write Warn) is exercised end-to-end in
    /// <c>SmoothingPipelineTests.PersistAfterCommit_WriteFailure_LogsWarn_DoesNotThrow</c>.
    /// </remarks>
    [Collection("Sequential")]
    public class SmoothingPipelineLoggingTests : IDisposable
    {
        private readonly string tempDir;
        private readonly List<string> logLines = new List<string>();
        private readonly CelestialBody fakeKerbin;
        private const double KerbinRotationPeriod = 21549.425;

        public SmoothingPipelineLoggingTests()
        {
            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek_pipeline_log_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            SmoothingPipeline.ResetForTesting();
            TrajectoryMath.FrameTransform.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;

            // Phase 4: ExoPropulsive / ExoBallistic sections lift to inertial
            // before fitting (design doc §6.2). Inject the body + rotation-
            // period seams so xUnit can drive the lift without FlightGlobals.
            fakeKerbin = TestBodyRegistry.CreateBody("Kerbin", radius: 600000.0, gravParameter: 3.5316e12);
            SmoothingPipeline.BodyResolverForTesting = name => name == "Kerbin" ? fakeKerbin : null;
            TrajectoryMath.FrameTransform.RotationPeriodForTesting = b =>
                object.ReferenceEquals(b, fakeKerbin) ? KerbinRotationPeriod : double.NaN;
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

        private static List<TrajectoryPoint> MakeFrames(int count, double startUT)
        {
            var frames = new List<TrajectoryPoint>(count);
            for (int i = 0; i < count; i++)
            {
                frames.Add(new TrajectoryPoint
                {
                    ut = startUT + i,
                    latitude = 0.1 + i * 0.01,
                    longitude = 1.0 + i * 0.05,
                    altitude = 80000 + i * 100,
                    rotation = Quaternion.identity,
                    bodyName = "Kerbin",
                });
            }
            return frames;
        }

        private static Recording MakeRecording(string id,
            SegmentEnvironment env = SegmentEnvironment.ExoBallistic, int frameCount = 10,
            int formatVersion = 7, int sidecarEpoch = 1)
        {
            var section = new TrackSection
            {
                environment = env,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = 100.0,
                endUT = 100.0 + frameCount - 1,
                anchorVesselId = 0,
                frames = MakeFrames(frameCount, 100.0),
                checkpoints = new List<OrbitSegment>(),
                sampleRateHz = 1f,
            };
            var rec = new Recording
            {
                RecordingId = id,
                RecordingFormatVersion = formatVersion,
                SidecarEpoch = sidecarEpoch,
            };
            rec.TrackSections.Add(section);
            return rec;
        }

        // --- L1: Info Pipeline-Smoothing per spline fit (commit-time) ---

        [Fact]
        public void L1_SplineFit_LogsRequiredFields()
        {
            // What makes it fail: a missing field on the L1 line means the
            // post-hoc log validator can't reconstruct which section was fit
            // or how long it took — defeats §19.2 Stage 1's "review by log".
            var rec = MakeRecording("rec-L1");
            SmoothingPipeline.FitAndStorePerSection(rec);

            Assert.Contains(logLines, l => l.Contains("[INFO][Pipeline-Smoothing]")
                && l.Contains("Spline fit:")
                && l.Contains("recordingId=rec-L1")
                && l.Contains("sectionIndex=0")
                && l.Contains("env=ExoBallistic")
                && l.Contains("sampleCount=10")
                && l.Contains("knotCount=")
                && l.Contains("fitDurationMs="));
        }

        // --- L2: Info Pipeline-Smoothing for lazy fit on missing annotation ---

        [Fact]
        public void L2_LazyFitOnMissingAnnotation_LogsReason()
        {
            // What makes it fail: dropping the lazy-compute Info line would
            // mean a session that lazy-fits on every load (because .pann was
            // wiped) looks identical in KSP.log to a session that read the
            // cache — silent perf regression.
            var rec = MakeRecording("rec-L2");
            string pannPath = Path.Combine(tempDir, "rec-L2.pann");
            SmoothingPipeline.LoadOrCompute(rec, pannPath);

            Assert.Contains(logLines, l => l.Contains("[INFO][Pipeline-Smoothing]")
                && l.Contains("Lazy compute")
                && l.Contains("recordingId=rec-L2")
                && l.Contains("reason=file-missing"));
        }

        // --- L3: Warn Pipeline-Smoothing on fit failure ---

        [Fact]
        public void L3_FitFailure_LogsWarnWithSampleCountAndReason()
        {
            // What makes it fail: silent Catmull-Rom failure leaves the
            // section unsmoothed without surfacing why — the L3 line is
            // the only signal a fit didn't take.
            var section = new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = 100.0,
                endUT = 105.0,
                anchorVesselId = 0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, latitude = 0, longitude = 0, altitude = 80000, rotation = Quaternion.identity, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 101, latitude = 0, longitude = double.PositiveInfinity, altitude = 80000, rotation = Quaternion.identity, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 102, latitude = 0, longitude = 0, altitude = 80000, rotation = Quaternion.identity, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 103, latitude = 0, longitude = 0, altitude = 80000, rotation = Quaternion.identity, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 104, latitude = 0, longitude = 0, altitude = 80000, rotation = Quaternion.identity, bodyName = "Kerbin" },
                },
                checkpoints = new List<OrbitSegment>(),
            };
            var rec = new Recording { RecordingId = "rec-L3", RecordingFormatVersion = 7, SidecarEpoch = 1 };
            rec.TrackSections.Add(section);

            SmoothingPipeline.FitAndStorePerSection(rec);

            Assert.Contains(logLines, l => l.Contains("[WARN][Pipeline-Smoothing]")
                && l.Contains("Catmull-Rom fit failed")
                && l.Contains("recordingId=rec-L3")
                && l.Contains("sampleCount=5")
                && l.Contains("reason="));
        }

        // --- L4: VerboseRateLimited per-frame eval count ---
        // L4 asserted in the consumer hot-path (ParsekFlight) once Phase 1's
        // per-frame summary log lands. Tracked in T6b code comment so it's
        // grep-discoverable from both code and tests.

        // --- L6: Verbose Pipeline-Sidecar on .pann read OK ---

        [Fact]
        public void L6_PannReadOk_LogsRecordingIdBlockVersionAlgStampBytes()
        {
            // What makes it fail: a missing read-OK line means there's no
            // way to distinguish "pipeline read .pann" from "pipeline lazy-
            // computed". Every diagnostic case relies on this discriminator.
            var rec = MakeRecording("rec-L6");
            string pannPath = Path.Combine(tempDir, "rec-L6.pann");
            // Prime the cache with a write.
            SmoothingPipeline.LoadOrCompute(rec, pannPath);
            SectionAnnotationStore.ResetForTesting();
            logLines.Clear();
            // Re-load — this should produce a read-OK line, not a recompute.
            SmoothingPipeline.LoadOrCompute(rec, pannPath);

            Assert.Contains(logLines, l => l.Contains("[VERBOSE][Pipeline-Sidecar]")
                && l.Contains("Pannotations read OK")
                && l.Contains("recordingId=rec-L6")
                && l.Contains("block=SmoothingSplineList")
                && l.Contains("version=")
                && l.Contains("algStamp=")
                && l.Contains("bytes="));
        }

        // --- L7: Info Pipeline-Sidecar on lazy compute (annotation absent) ---

        [Fact]
        public void L7_LazyCompute_LogsFileMissingReason()
        {
            // What makes it fail: missing-file path doesn't surface the
            // distinct reason → can't differentiate a fresh recording from
            // a recording whose .pann was deleted.
            //
            // L7 split (design doc §19.2 Sidecar table): the absent-file path
            // emits BOTH a Pipeline-Sidecar Info line ("Pannotations missing →
            // lazy compute scheduled", asserted in
            // L7_PannotationsMissing_LogsLazyComputeScheduled below) AND the
            // existing Pipeline-Smoothing "Lazy compute" Info line asserted
            // here. The two serve distinct concerns — sidecar I/O state vs
            // smoothing-stage recompute — and were collapsed into a single log
            // site during initial implementation; the split restores §19.2's
            // documented contract.
            var rec = MakeRecording("rec-L7");
            string pannPath = Path.Combine(tempDir, "rec-L7.pann");

            SmoothingPipeline.LoadOrCompute(rec, pannPath);

            Assert.Contains(logLines, l => l.Contains("[INFO][Pipeline-Smoothing]")
                && l.Contains("Lazy compute")
                && l.Contains("reason=file-missing"));
        }

        [Fact]
        public void L7_PannotationsMissing_LogsLazyComputeScheduled()
        {
            // What makes it fail: the Pipeline-Sidecar half of L7 ("Node
            // missing → lazy compute scheduled" in §19.2's Sidecar table) was
            // collapsed into the Pipeline-Smoothing line during initial
            // implementation. Re-splitting the two lets a developer grep the
            // sidecar tag for I/O concerns independently from the smoothing
            // stage's recompute concerns.
            var rec = MakeRecording("rec-L7-sidecar");
            string pannPath = Path.Combine(tempDir, "rec-L7-sidecar.pann");

            SmoothingPipeline.LoadOrCompute(rec, pannPath);

            Assert.Contains(logLines, l => l.Contains("[INFO][Pipeline-Sidecar]")
                && l.Contains("Pannotations missing")
                && l.Contains("lazy compute scheduled")
                && l.Contains("recordingId=rec-L7-sidecar")
                && l.Contains("block=SmoothingSplineList")
                && l.Contains("reason=file-missing"));
        }

        // --- L8: Info Pipeline-Sidecar on whole-file invalidation ---

        [Theory]
        [InlineData("version-drift")]
        [InlineData("epoch-drift")]
        [InlineData("format-drift")]
        [InlineData("config-hash-drift")]
        [InlineData("alg-stamp-drift")]
        public void L8_WholeFileInvalidation_LogsCanonicalReason(string expectedReason)
        {
            // What makes it fail: a non-canonical reason string would break
            // log greps that key on the documented reason set in §19.2 (the
            // Sidecar row "Whole-file invalidation").
            //
            // The five InlineData cases above cover the full canonical
            // whole-file-invalidation reason set documented in design doc
            // §19.2 / §21.3. The remaining two members of the six-token
            // canonical set (`file-missing` and `payload-corrupt`) are
            // emitted on different code paths (absent-file branch and
            // mid-stream read failure respectively); see
            // L7_PannotationsMissing_LogsLazyComputeScheduled and the
            // payload-corrupt assertion in SmoothingPipelineTests for those.
            string id = "rec-L8-" + expectedReason;
            string pannPath = Path.Combine(tempDir, id + ".pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(pannPath, id,
                sourceSidecarEpoch: 1, sourceRecordingFormatVersion: 7,
                configurationHash: hash, splines: new List<KeyValuePair<int, SmoothingSpline>>());

            byte[] bytes = File.ReadAllBytes(pannPath);
            // Header layout: magic[0..3] + binVer[4..7] + algStamp[8..11] +
            // epoch[12..15] + fmtVer[16..19] + cfgHash[20..51] + recId(string).
            switch (expectedReason)
            {
                case "version-drift":
                    bytes[4] = 99; bytes[5] = 0; bytes[6] = 0; bytes[7] = 0;
                    break;
                case "alg-stamp-drift":
                    bytes[8] = 99; bytes[9] = 0; bytes[10] = 0; bytes[11] = 0;
                    break;
                case "epoch-drift":
                    bytes[12] = 99; bytes[13] = 0; bytes[14] = 0; bytes[15] = 0;
                    break;
                case "format-drift":
                    bytes[16] = 99; bytes[17] = 0; bytes[18] = 0; bytes[19] = 0;
                    break;
                case "config-hash-drift":
                    bytes[20] = (byte)(bytes[20] ^ 0xFF);
                    break;
            }
            File.WriteAllBytes(pannPath, bytes);

            var rec = MakeRecording(id);
            SmoothingPipeline.LoadOrCompute(rec, pannPath);

            // Every reason — including version-drift — flows through the
            // canonical L8 "whole-file invalidation" line with the documented
            // reason token. The previous "version-drift surfaces as
            // probe-failed" carve-out hid the real reason behind a generic
            // label and broke greps keyed on §19.2's documented set.
            Assert.Contains(logLines, l => l.Contains("[INFO][Pipeline-Sidecar]")
                && l.Contains("whole-file invalidation")
                && l.Contains("recordingId=" + id)
                && l.Contains("reason=" + expectedReason));

            if (expectedReason == "version-drift")
            {
                // Pin the foundVersion / expectedVersion fields specifically
                // for the version-drift case so the diagnostic data carries
                // enough context to classify the mismatch from the log alone.
                Assert.Contains(logLines, l => l.Contains("[INFO][Pipeline-Sidecar]")
                    && l.Contains("whole-file invalidation")
                    && l.Contains("reason=version-drift")
                    && l.Contains("foundVersion=99")
                    && l.Contains("expectedVersion="
                        + PannotationsSidecarBinary.PannotationsBinaryVersion));
            }
        }

        // --- L9: Verbose Pipeline-Sidecar on atomic write OK ---

        [Fact]
        public void L9_AtomicWriteOk_LogsBytesAndPath()
        {
            // What makes it fail: missing write-OK line means a healthy
            // commit can't be distinguished from a silently-failed one (HR-9).
            var rec = MakeRecording("rec-L9");
            string pannPath = Path.Combine(tempDir, "rec-L9.pann");

            SmoothingPipeline.PersistAfterCommit(rec, pannPath);

            Assert.Contains(logLines, l => l.Contains("[VERBOSE][Pipeline-Sidecar]")
                && l.Contains("Pannotations write OK")
                && l.Contains("recordingId=rec-L9")
                && l.Contains("bytes=")
                && l.Contains("path="));
        }

        // --- L11: Info Pipeline-Format per recording load ---

        [Fact]
        public void L11_PipelineFormat_LogsFormatVersionPannPresentDegradedFeatures()
        {
            // What makes it fail: missing the per-load Pipeline-Format line
            // would force every later phase to add its own degraded-feature
            // log site. Centralising it here once gives §19.2 Format Gating
            // a stable home.
            var rec = MakeRecording("rec-L11", formatVersion: 6);
            string pannPath = Path.Combine(tempDir, "rec-L11.pann");

            SmoothingPipeline.LoadOrCompute(rec, pannPath);

            Assert.Contains(logLines, l => l.Contains("[INFO][Pipeline-Format]")
                && l.Contains("recordingId=rec-L11")
                && l.Contains("formatVersion=6")
                && l.Contains("pannPresent=")
                && l.Contains("degradedFeatures=[]"));
        }
    }
}
