using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 5 unit tests for <see cref="CoBubbleOverlapDetector"/>. Cover
    /// the bubble-membership rule (Active / Background ∩ Absolute), the
    /// minimum-window cutoff, the bubble-radius truncation, and the
    /// per-pair determinism (HR-3).
    /// </summary>
    [Collection("Sequential")]
    public class CoBubbleOverlapDetectorTests : IDisposable
    {
        public CoBubbleOverlapDetectorTests()
        {
            CoBubbleOverlapDetector.ResetForTesting();
        }

        public void Dispose()
        {
            CoBubbleOverlapDetector.ResetForTesting();
        }

        // ---------- Helpers ----------

        private static Recording MakeRecording(string id, double startUT, double endUT,
            TrackSectionSource source, ReferenceFrame frame, SegmentEnvironment env,
            Vector3d worldOffset = default)
        {
            var pt = new TrajectoryPoint
            {
                ut = startUT,
                latitude = 0.0,
                longitude = 0.0,
                altitude = 0.0,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            };
            var section = new TrackSection
            {
                referenceFrame = frame,
                environment = env,
                source = source,
                startUT = startUT,
                endUT = endUT,
                anchorVesselId = 0,
                frames = new List<TrajectoryPoint> { pt },
                checkpoints = new List<OrbitSegment>(),
                sampleRateHz = 4.0f,
            };
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                Points = new List<TrajectoryPoint> { pt },
                TrackSections = new List<TrackSection> { section },
            };
        }

        [Fact]
        public void Detect_NullOrSingleRecording_ReturnsEmpty()
        {
            Assert.Empty(CoBubbleOverlapDetector.Detect(null));
            var rec = MakeRecording("rec-A", 100, 110, TrackSectionSource.Active,
                ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            Assert.Empty(CoBubbleOverlapDetector.Detect(new List<Recording> { rec }));
        }

        [Fact]
        public void Detect_TwoActiveAbsoluteSections_OverlappingUT_EmitsWindow()
        {
            // Both sides Active + Absolute + same body + sufficient overlap.
            // Inject seam so separation stays inside bubble radius.
            CoBubbleOverlapDetector.SamplePositionResolverForTesting =
                (rec, ut) => new Vector3d(0, 0, 0);

            var rA = MakeRecording("rec-A", 100, 120,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            var rB = MakeRecording("rec-B", 105, 130,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);

            List<CoBubbleOverlapDetector.OverlapWindow> windows =
                CoBubbleOverlapDetector.Detect(new List<Recording> { rA, rB });
            Assert.Single(windows);
            var w = windows[0];
            Assert.Equal("rec-A", w.RecordingA);
            Assert.Equal("rec-B", w.RecordingB);
            Assert.Equal(105.0, w.StartUT);
            Assert.Equal(120.0, w.EndUT);
            Assert.Equal((byte)1, w.FrameTag);
        }

        [Fact]
        public void Detect_RelativeFrameSection_NotEligible()
        {
            // Relative-frame sections do not contribute to co-bubble
            // detection (HR-7 guards). Even with full overlap, no window.
            CoBubbleOverlapDetector.SamplePositionResolverForTesting =
                (rec, ut) => new Vector3d(0, 0, 0);

            var rA = MakeRecording("rec-A", 100, 120,
                TrackSectionSource.Active, ReferenceFrame.Relative, SegmentEnvironment.ExoBallistic);
            var rB = MakeRecording("rec-B", 100, 120,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            Assert.Empty(CoBubbleOverlapDetector.Detect(new List<Recording> { rA, rB }));
        }

        [Fact]
        public void Detect_CheckpointSourceSection_NotEligible()
        {
            // Checkpoint source = orbital propagation, never in bubble.
            CoBubbleOverlapDetector.SamplePositionResolverForTesting =
                (rec, ut) => new Vector3d(0, 0, 0);

            var rA = MakeRecording("rec-A", 100, 120,
                TrackSectionSource.Checkpoint, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            var rB = MakeRecording("rec-B", 100, 120,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            Assert.Empty(CoBubbleOverlapDetector.Detect(new List<Recording> { rA, rB }));
        }

        [Fact]
        public void Detect_BackgroundSourceSection_IsEligible()
        {
            // Background = peer was loaded + unpacked while another vessel
            // was Active in the same scene → in-bubble.
            CoBubbleOverlapDetector.SamplePositionResolverForTesting =
                (rec, ut) => new Vector3d(0, 0, 0);

            var rA = MakeRecording("rec-A", 100, 120,
                TrackSectionSource.Background, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            var rB = MakeRecording("rec-B", 100, 120,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            List<CoBubbleOverlapDetector.OverlapWindow> windows =
                CoBubbleOverlapDetector.Detect(new List<Recording> { rA, rB });
            Assert.Single(windows);
        }

        [Fact]
        public void Detect_TooShortOverlap_DropsWindow()
        {
            // Overlap of 0.2s < default minWindowDuration of 0.5s.
            CoBubbleOverlapDetector.SamplePositionResolverForTesting =
                (rec, ut) => new Vector3d(0, 0, 0);

            var rA = MakeRecording("rec-A", 100.0, 100.2,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            var rB = MakeRecording("rec-B", 100.0, 100.2,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            Assert.Empty(CoBubbleOverlapDetector.Detect(new List<Recording> { rA, rB }));
        }

        [Fact]
        public void Detect_SeparationExceedsBubbleRadius_TruncatesWindow()
        {
            // Inject a seam that returns a steadily-growing separation.
            // The window should truncate at the first sample that exceeds
            // 2.5 km.
            CoBubbleOverlapDetector.SamplePositionResolverForTesting = (rec, ut) =>
            {
                if (rec.RecordingId == "rec-A") return new Vector3d(0, 0, 0);
                // rec-B drifts away at 1 km/s.
                return new Vector3d((ut - 100.0) * 1000.0, 0, 0);
            };
            var rA = MakeRecording("rec-A", 100, 110,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            var rB = MakeRecording("rec-B", 100, 110,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            List<CoBubbleOverlapDetector.OverlapWindow> windows =
                CoBubbleOverlapDetector.Detect(new List<Recording> { rA, rB });
            // Should yield one truncated window ending around UT=102.5
            // (separation crosses 2500m). Tolerate ±0.5s for stepping.
            Assert.Single(windows);
            Assert.InRange(windows[0].EndUT, 102.0, 103.0);
        }

        [Fact]
        public void Detect_DifferentBodies_NoWindow()
        {
            CoBubbleOverlapDetector.SamplePositionResolverForTesting =
                (rec, ut) => new Vector3d(0, 0, 0);
            var rA = MakeRecording("rec-A", 100, 110,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            var rB = MakeRecording("rec-B", 100, 110,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            // Mutate B's section's first frame to a different body name.
            var sec = rB.TrackSections[0];
            var pt = sec.frames[0];
            pt.bodyName = "Mun";
            sec.frames[0] = pt;
            rB.TrackSections[0] = sec;

            Assert.Empty(CoBubbleOverlapDetector.Detect(new List<Recording> { rA, rB }));
        }

        [Fact]
        public void Detect_OutputSortedDeterministically()
        {
            // Three recordings: detect should emit pairs (A,B), (A,C), (B,C)
            // in that order.
            CoBubbleOverlapDetector.SamplePositionResolverForTesting =
                (rec, ut) => new Vector3d(0, 0, 0);
            var rA = MakeRecording("rec-A", 100, 120,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            var rB = MakeRecording("rec-B", 100, 120,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            var rC = MakeRecording("rec-C", 100, 120,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            List<CoBubbleOverlapDetector.OverlapWindow> windows =
                CoBubbleOverlapDetector.Detect(new List<Recording> { rC, rA, rB });
            Assert.Equal(3, windows.Count);
            Assert.Equal("rec-A", windows[0].RecordingA);
            Assert.Equal("rec-B", windows[0].RecordingB);
            Assert.Equal("rec-A", windows[1].RecordingA);
            Assert.Equal("rec-C", windows[1].RecordingB);
            Assert.Equal("rec-B", windows[2].RecordingA);
            Assert.Equal("rec-C", windows[2].RecordingB);
        }

        [Fact]
        public void ComputePeerContentSignature_DeterministicAcrossCalls()
        {
            var pt1 = new TrajectoryPoint
            {
                ut = 100.0, latitude = 1.0, longitude = 2.0, altitude = 3.0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero,
            };
            var pt2 = new TrajectoryPoint
            {
                ut = 102.0, latitude = 1.5, longitude = 2.5, altitude = 3.5,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero,
            };
            var rec = new Recording
            {
                RecordingId = "peer",
                Points = new List<TrajectoryPoint> { pt1, pt2 },
            };
            byte[] s1 = CoBubbleOverlapDetector.ComputePeerContentSignature(rec, 100.0, 110.0);
            byte[] s2 = CoBubbleOverlapDetector.ComputePeerContentSignature(rec, 100.0, 110.0);
            Assert.NotNull(s1);
            Assert.Equal(32, s1.Length);
            Assert.Equal(s1, s2);
        }

        [Fact]
        public void ComputePeerContentSignature_MutationFlipsHash()
        {
            // Even a single-point edit must flip the SHA-256 (HR-10).
            var pt = new TrajectoryPoint
            {
                ut = 100.0, latitude = 1.0, longitude = 2.0, altitude = 3.0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero,
            };
            var rec = new Recording
            {
                RecordingId = "peer",
                Points = new List<TrajectoryPoint> { pt },
            };
            byte[] s1 = CoBubbleOverlapDetector.ComputePeerContentSignature(rec, 100.0, 110.0);
            pt.altitude = 999.0;
            rec.Points[0] = pt;
            byte[] s2 = CoBubbleOverlapDetector.ComputePeerContentSignature(rec, 100.0, 110.0);
            Assert.NotEqual(s1, s2);
        }
    }
}
