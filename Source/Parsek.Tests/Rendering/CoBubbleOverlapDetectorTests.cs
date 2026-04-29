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
        public void Detect_ActivePlusBackgroundAbsoluteSections_OverlappingUT_EmitsWindow()
        {
            // One Active side + one Background side + Absolute + same
            // body + sufficient overlap. P2-A bans Active+Active; this is
            // the canonical valid pair (the focused vessel was Active, a
            // peer in the same physics bubble was Background-recorded).
            CoBubbleOverlapDetector.SamplePositionResolverForTesting =
                (rec, ut) => new Vector3d(0, 0, 0);

            var rA = MakeRecording("rec-A", 100, 120,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            var rB = MakeRecording("rec-B", 105, 130,
                TrackSectionSource.Background, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);

            List<CoBubbleOverlapDetector.OverlapWindow> windows =
                CoBubbleOverlapDetector.Detect(new List<Recording> { rA, rB });
            Assert.Single(windows);
            var w = windows[0];
            Assert.Equal("rec-A", w.RecordingA);
            Assert.Equal("rec-B", w.RecordingB);
            Assert.Equal(105.0, w.StartUT);
            Assert.Equal(120.0, w.EndUT);
            Assert.Equal((byte)1, w.FrameTag);
            Assert.Equal("Kerbin", w.BodyName);
        }

        [Fact]
        public void Detect_ActivePlusActive_RejectedAsPhantomPair()
        {
            // P2-A: KSP has exactly one focused vessel per scene at any
            // UT, so two simultaneous Active sections must be from
            // different sessions (re-fly bridge), not co-bubble. Detector
            // must reject the pair outright — accepting would record a
            // phantom offset that the blender later replays as a stale
            // position lock between two recordings that never shared a
            // bubble.
            CoBubbleOverlapDetector.SamplePositionResolverForTesting =
                (rec, ut) => new Vector3d(0, 0, 0);

            var rA = MakeRecording("rec-A", 100, 120,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            var rB = MakeRecording("rec-B", 105, 130,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);

            List<CoBubbleOverlapDetector.OverlapWindow> windows =
                CoBubbleOverlapDetector.Detect(new List<Recording> { rA, rB });
            Assert.Empty(windows);
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
                TrackSectionSource.Background, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
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
                TrackSectionSource.Background, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
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
                TrackSectionSource.Background, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
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
                TrackSectionSource.Background, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
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
            // in that order. Per P2-A, two Active sections never form a
            // valid pair — drive the test with one Active focus + two
            // Background peers so all 3 pairs are eligible.
            CoBubbleOverlapDetector.SamplePositionResolverForTesting =
                (rec, ut) => new Vector3d(0, 0, 0);
            var rA = MakeRecording("rec-A", 100, 120,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            var rB = MakeRecording("rec-B", 100, 120,
                TrackSectionSource.Background, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            var rC = MakeRecording("rec-C", 100, 120,
                TrackSectionSource.Background, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
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
        public void BuildTrace_WindowExceedsMax_ClampsEndUTToBlendMaxWindowSeconds()
        {
            // P2-B regression: BlendMaxWindowSeconds participates in the
            // ConfigurationHash and is the documented per-trace duration cap
            // (§17.3.1 / §22), but BuildTrace ignored it and only capped via
            // MaxCoBubbleSamplesPerTrace. For very long overlaps this either
            // emitted huge traces ignoring the duration cap, or hit the
            // sample cap mid-window leaving EndUT covering UTs without
            // sample coverage — the blender would then clamp to the last
            // offset for the rest of the window, producing visually wrong
            // results.
            //
            // Fix: BuildTrace clamps the trace to the first
            // BlendMaxWindowSeconds of the window and emits a Verbose
            // window-clamped-to-max log per HR-9.
            CoBubbleOverlapDetector.SamplePositionResolverForTesting =
                (rec, ut) => new Vector3d(0, 0, 0);

            // Make a window that is clearly longer than BlendMaxWindowSeconds.
            // Default cap is 600.0s; pick 1200s so the clamp has to fire.
            double cap = CoBubbleConfiguration.Default.BlendMaxWindowSeconds;
            Assert.True(cap > 0, "test relies on a positive BlendMaxWindowSeconds default");
            var window = new CoBubbleOverlapDetector.OverlapWindow
            {
                RecordingA = "rec-A",
                RecordingB = "rec-B",
                StartUT = 1000.0,
                EndUT = 1000.0 + cap * 2.0,    // 2× cap → must be clamped
                FrameTag = 0,                   // body-fixed → no inertial lift
                PrimaryEnv = SegmentEnvironment.ExoBallistic,
                BodyName = "Kerbin",
            };

            var primary = MakeRecording("rec-A", 1000.0, 1000.0 + cap * 2.0,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            var peer = MakeRecording("rec-B", 1000.0, 1000.0 + cap * 2.0,
                TrackSectionSource.Background, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);

            CoBubbleOffsetTrace trace = CoBubbleOverlapDetector.BuildTrace(
                window, primary, peer, primaryDesignation: 0);
            Assert.NotNull(trace);

            // EndUT must be clamped to StartUT + cap (within a small tolerance
            // for floating-point round-trip).
            double expectedEnd = window.StartUT + cap;
            Assert.True(Math.Abs(trace.EndUT - expectedEnd) < 1e-6,
                $"EndUT must be clamped to {expectedEnd}, got {trace.EndUT}");

            // Sample UTs must all fall inside [StartUT, expectedEnd].
            Assert.NotNull(trace.UTs);
            Assert.NotEmpty(trace.UTs);
            for (int i = 0; i < trace.UTs.Length; i++)
            {
                Assert.True(trace.UTs[i] >= window.StartUT - 1e-6,
                    $"UT[{i}]={trace.UTs[i]} below StartUT");
                Assert.True(trace.UTs[i] <= expectedEnd + 1e-6,
                    $"UT[{i}]={trace.UTs[i]} above clamped endUT={expectedEnd}");
            }
        }

        [Fact]
        public void BuildTrace_WindowWithinCap_NotClamped()
        {
            // Counter-test for P2-B: a window shorter than the cap must
            // round-trip its EndUT unchanged.
            CoBubbleOverlapDetector.SamplePositionResolverForTesting =
                (rec, ut) => new Vector3d(0, 0, 0);

            double cap = CoBubbleConfiguration.Default.BlendMaxWindowSeconds;
            // Pick a window well below the cap.
            double windowDuration = Math.Min(60.0, cap / 2.0);
            var window = new CoBubbleOverlapDetector.OverlapWindow
            {
                RecordingA = "rec-A",
                RecordingB = "rec-B",
                StartUT = 1000.0,
                EndUT = 1000.0 + windowDuration,
                FrameTag = 0,
                PrimaryEnv = SegmentEnvironment.ExoBallistic,
                BodyName = "Kerbin",
            };

            var primary = MakeRecording("rec-A", 1000.0, 1000.0 + windowDuration,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            var peer = MakeRecording("rec-B", 1000.0, 1000.0 + windowDuration,
                TrackSectionSource.Background, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);

            CoBubbleOffsetTrace trace = CoBubbleOverlapDetector.BuildTrace(
                window, primary, peer, primaryDesignation: 0);
            Assert.NotNull(trace);
            Assert.Equal(window.EndUT, trace.EndUT);
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

        // ---------- Phase 5 review-pass-5 P1: offset-sign round-trip ----------

        [Fact]
        public void DetectAndStore_OffsetSign_RoundTripsThroughBlenderToCorrectPeerWorldPosition()
        {
            // Phase 5 review-pass-5 P1 regression: DetectAndStore was
            // emitting both stored sides of every overlap pair with
            // reversed-sign offsets. The blender's contract is: a trace
            // stored under recording X with PeerRecordingId = Y supplies
            // worldOffset such that X_world = Y_world + offset, i.e.
            // offset = X - Y. Pre-fix, both stored sides had offset =
            // Y - X, so peer ghosts rendered on the opposite side of
            // the primary at the offset's distance. The two existing
            // in-game tests (Pipeline_CoBubble_Live, Pipeline_CoBubble_
            // GhostGhost) bypassed DetectAndStore and injected traces
            // directly via PutCoBubbleTrace, so this regression hid.
            //
            // This test exercises the round-trip from DetectAndStore →
            // SectionAnnotationStore → CoBubbleBlender.TryEvaluateOffset
            // and asserts the offset has the correct sign on BOTH
            // stored sides (asymmetric primary-assignment).
            //
            // Setup: A at world (0, 0, 0); B at world (10, 0, 0). The
            // detector's TrySampleWorld test seam returns these
            // positions for any UT. After DetectAndStore:
            //   - traces[recA] contains a trace with PeerRecordingId =
            //     recB and offset = recA - recB = (0, 0, 0) - (10, 0, 0)
            //     = (-10, 0, 0).
            //   - traces[recB] contains a trace with PeerRecordingId =
            //     recA and offset = recB - recA = (10, 0, 0) - (0, 0, 0)
            //     = (10, 0, 0).
            // When B is the rendered ghost and A is its designated
            // primary, blender returns (-10, 0, 0): A_world + (-10, 0, 0)
            // = (-10, 0, 0). Hmm — but the brief says (10, 0, 0). Let
            // me re-check.
            //
            // Blender consumer adds offset to primary's world to get
            // peer's world. So if peer = B and primary = A, want B_world
            // = A_world + offset, i.e. offset = B - A = (10, 0, 0). The
            // trace stored under B (the rendered peer) with
            // PeerRecordingId = A (the primary) has Dx = B - A. That's
            // the correct sign.
            //
            // For symmetric: peer = A, primary = B → offset = A - B =
            // (-10, 0, 0). The trace stored under A with PeerRecordingId
            // = B has Dx = A - B.

            // Use simple synthetic recordings — sample positions come
            // from the test seam, not from rec.Points / FlightGlobals.
            const string idA = "rec-A-sign";
            const string idB = "rec-B-sign";
            var recA = MakeRecording(idA, 100.0, 110.0,
                TrackSectionSource.Active, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            var recB = MakeRecording(idB, 100.0, 110.0,
                TrackSectionSource.Background, ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic);
            // Inject deterministic world positions: A at origin, B at +10x.
            CoBubbleOverlapDetector.SamplePositionResolverForTesting = (rec, ut) =>
                rec.RecordingId == idA
                    ? new Vector3d(0, 0, 0)
                    : new Vector3d(10.0, 0, 0);

            // Need RecordingStore.CommittedRecordings populated for the
            // blender's runtime peer-validation lookup
            // (ResolveLivePeerRecording). Without this, the validator
            // would see the live peer as null and skip the format/epoch
            // gate — fine for this test, but we want to exercise the
            // production-shaped path.
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.VerboseOverrideForTesting = true;
            SectionAnnotationStore.ResetForTesting();
            RenderSessionState.ResetForTesting();
            SmoothingPipeline.ResetForTesting();
            SmoothingPipeline.UseCoBubbleBlendResolverForTesting = () => true;
            // Force the FrameTag to body-fixed (0) regardless of what
            // the detector picks for the section's environment, so the
            // blender's inertial-lift branch doesn't trigger and need
            // FlightGlobals.Bodies.
            CoBubbleBlender.FrameTagOverrideForTesting = (peer, ut) => (byte)0;

            try
            {
                RecordingStore.AddCommittedInternal(recA);
                RecordingStore.AddCommittedInternal(recB);

                int emitted = CoBubbleOverlapDetector.DetectAndStore(
                    new List<Recording> { recA, recB });
                Assert.Equal(2, emitted);

                // Both sides should have one trace.
                Assert.True(SectionAnnotationStore.TryGetCoBubbleTraces(idA, out var aTraces));
                Assert.NotNull(aTraces);
                Assert.Single(aTraces);
                Assert.Equal(idB, aTraces[0].PeerRecordingId);

                Assert.True(SectionAnnotationStore.TryGetCoBubbleTraces(idB, out var bTraces));
                Assert.NotNull(bTraces);
                Assert.Single(bTraces);
                Assert.Equal(idA, bTraces[0].PeerRecordingId);

                // Pin: the trace stored under B (rendered ghost) with
                // PeerRecordingId = A (primary) must have Dx = B - A =
                // (10, 0, 0). Pre-fix this would be (-10, 0, 0).
                Assert.True(Math.Abs(bTraces[0].Dx[0] - 10.0f) < 1e-3f,
                    $"B-side Dx[0] expected 10.0, got {bTraces[0].Dx[0]}");
                // And under A with PeerRecordingId = B: Dx = A - B = (-10, 0, 0).
                Assert.True(Math.Abs(aTraces[0].Dx[0] - (-10.0f)) < 1e-3f,
                    $"A-side Dx[0] expected -10.0, got {aTraces[0].Dx[0]}");

                // Round-trip through the blender. First: B is the
                // rendered ghost, A is its primary.
                RenderSessionState.PutPrimaryAssignmentForTesting(idB, idA);
                double midpointUT = (aTraces[0].StartUT + aTraces[0].EndUT) * 0.5;
                bool ok = CoBubbleBlender.TryEvaluateOffset(
                    idB, midpointUT, out Vector3d offsetB, out CoBubbleBlendStatus statusB,
                    out string primaryB);
                Assert.True(ok, $"blender miss for B; status={statusB}");
                Assert.Equal(idA, primaryB);
                // A_world (0,0,0) + offset = B_world (10, 0, 0) → offset = (10, 0, 0).
                Assert.Equal(10.0, offsetB.x, 3);
                Assert.Equal(0.0, offsetB.y, 3);
                Assert.Equal(0.0, offsetB.z, 3);

                // Symmetric: clear B's primary mapping and assign B as
                // A's primary instead. Then A is the rendered ghost.
                RenderSessionState.ResetForTesting();
                RenderSessionState.PutPrimaryAssignmentForTesting(idA, idB);
                bool ok2 = CoBubbleBlender.TryEvaluateOffset(
                    idA, midpointUT, out Vector3d offsetA, out CoBubbleBlendStatus statusA,
                    out string primaryA);
                Assert.True(ok2, $"blender miss for A; status={statusA}");
                Assert.Equal(idB, primaryA);
                // B_world (10,0,0) + offset = A_world (0, 0, 0) → offset = (-10, 0, 0).
                Assert.Equal(-10.0, offsetA.x, 3);
                Assert.Equal(0.0, offsetA.y, 3);
                Assert.Equal(0.0, offsetA.z, 3);
            }
            finally
            {
                CoBubbleBlender.ResetForTesting();
                CoBubbleOverlapDetector.ResetForTesting();
                SmoothingPipeline.ResetForTesting();
                SectionAnnotationStore.ResetForTesting();
                RenderSessionState.ResetForTesting();
                RecordingStore.ResetForTesting();
                ParsekLog.ResetTestOverrides();
                ParsekLog.SuppressLogging = true;
            }
        }
    }
}
