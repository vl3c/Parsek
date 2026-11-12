using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 6 tests covering the ε-resolver wiring (design doc §7.4 /
    /// section 7.6 / section 7.10). The propagator delegates three world-frame
    /// lookups through <see cref="IAnchorWorldFrameResolver"/>; tests
    /// inject a hand-crafted stub so xUnit can exercise the dispatch
    /// without standing up live KSP. Touches static state so runs in
    /// Sequential.
    /// </summary>
    [Collection("Sequential")]
    public class AnchorWorldFrameResolverTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public AnchorWorldFrameResolverTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RenderSessionState.ResetForTesting();
            SectionAnnotationStore.ResetForTesting();
            AnchorPropagator.ResetForTesting();
        }

        public void Dispose()
        {
            RenderSessionState.ResetForTesting();
            SectionAnnotationStore.ResetForTesting();
            AnchorPropagator.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // --- helpers --------------------------------------------------------

        private static Recording MakeRec(string id, ReferenceFrame frame, double startUT, double endUT)
        {
            var rec = new Recording { RecordingId = id, VesselName = id };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = frame,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = startUT,
                endUT = endUT,
                anchorVesselId = frame == ReferenceFrame.Relative ? 100u : 0u,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = startUT, bodyName = "Kerbin", rotation = Quaternion.identity },
                    new TrajectoryPoint { ut = endUT, bodyName = "Kerbin", rotation = Quaternion.identity },
                },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            });
            return rec;
        }

        private sealed class StubResolver : IAnchorWorldFrameResolver
        {
            public Vector3d? RelWorldPos;
            public Vector3d? LoopWorldPos;
            public int RelCalls, LoopCalls;
            public string LastRelRecordingId, LastLoopRecordingId;
            public AnchorSide LastRelSide, LastLoopSide;
            public double LastRelUT, LastLoopUT;

            public bool TryResolveRelativeBoundaryWorldPos(
                Recording rec, int sectionIndex, AnchorSide side, double boundaryUT, out Vector3d worldPos)
            {
                RelCalls++;
                LastRelRecordingId = rec?.RecordingId;
                LastRelSide = side;
                LastRelUT = boundaryUT;
                if (RelWorldPos.HasValue) { worldPos = RelWorldPos.Value; return true; }
                worldPos = default; return false;
            }
            public bool TryResolveLoopAnchorWorldPos(
                Recording rec, int sectionIndex, AnchorSide side, double sampleUT, out Vector3d worldPos)
            {
                LoopCalls++;
                LastLoopRecordingId = rec?.RecordingId;
                LastLoopSide = side;
                LastLoopUT = sampleUT;
                if (LoopWorldPos.HasValue) { worldPos = LoopWorldPos.Value; return true; }
                worldPos = default; return false;
            }
        }

        private static void SeedSmoothed(Recording rec, double ut, Vector3d world)
        {
            // Inject a deterministic spline so the propagator's
            // smoothed-position evaluator returns 'world' for any UT/section.
            // Easier: route through the SmoothedPositionForTesting seam.
            AnchorPropagator.SmoothedPositionForTesting = (r, s, u) => world;
        }

        // --- §7.4 RelativeBoundary -----------------------------------------

        [Fact]
        public void RelativeBoundary_ResolverHit_WritesEpsilonIntoSessionMap()
        {
            // What makes it fail: a missed dispatch on AnchorSource.RelativeBoundary
            // (e.g. the switch falling through to default) would leave ε = 0
            // even with a good resolver — defeating §7.4 entirely.
            var rec = MakeRec("rec-rel", ReferenceFrame.Absolute, 0, 100);
            var stub = new StubResolver { RelWorldPos = new Vector3d(110, 220, 330) };
            AnchorPropagator.ResolverOverrideForTesting = stub;
            SeedSmoothed(rec, 50.0, new Vector3d(100, 200, 300));
            SectionAnnotationStore.PutAnchorCandidates(rec.RecordingId, 0, new[]
            {
                new AnchorCandidate(50.0, AnchorSource.RelativeBoundary, AnchorSide.End),
            });

            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "rel-sess" },
                new[] { rec }, new RecordingTree[0], surfaceLookup: null);

            Assert.Equal(1, stub.RelCalls);
            Assert.Equal(rec.RecordingId, stub.LastRelRecordingId);
            Assert.Equal(AnchorSide.End, stub.LastRelSide);
            Assert.Equal(50.0, stub.LastRelUT, 6);

            Assert.True(RenderSessionState.TryLookup(rec.RecordingId, 0, AnchorSide.End, out AnchorCorrection ac));
            Assert.Equal(AnchorSource.RelativeBoundary, ac.Source);
            // ε = worldRef - smoothed = (110,220,330) - (100,200,300) = (10,20,30)
            Assert.Equal(10.0, ac.Epsilon.x, 6);
            Assert.Equal(20.0, ac.Epsilon.y, 6);
            Assert.Equal(30.0, ac.Epsilon.z, 6);
        }

        [Fact]
        public void RelativeBoundary_ResolverMiss_LeavesEpsilonZeroAndLogsVerbose()
        {
            // What makes it fail: HR-9 visible failure. A resolver miss must
            // leave ε = 0 (priority slot still reserved) AND emit a Verbose
            // log line so the operator can attribute the missing anchor in
            // KSP.log. Silent fall-through would leave the slot occupied by
            // ε = 0 with no diagnostic.
            var rec = MakeRec("rec-rel-miss", ReferenceFrame.Absolute, 0, 100);
            var stub = new StubResolver { RelWorldPos = null };
            AnchorPropagator.ResolverOverrideForTesting = stub;
            SectionAnnotationStore.PutAnchorCandidates(rec.RecordingId, 0, new[]
            {
                new AnchorCandidate(50.0, AnchorSource.RelativeBoundary, AnchorSide.End),
            });

            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "rel-miss-sess" },
                new[] { rec }, new RecordingTree[0], surfaceLookup: null);

            Assert.True(RenderSessionState.TryLookup(rec.RecordingId, 0, AnchorSide.End, out AnchorCorrection ac));
            Assert.Equal(AnchorSource.RelativeBoundary, ac.Source);
            Assert.Equal(0.0, ac.Epsilon.x, 6);
            Assert.Equal(0.0, ac.Epsilon.y, 6);
            Assert.Equal(0.0, ac.Epsilon.z, 6);

            Assert.Contains(logLines,
                l => l.Contains("[VERBOSE][Pipeline-Anchor]") && l.Contains("resolver-miss")
                    && l.Contains("RelativeBoundary"));
        }

        [Fact]
        public void RelativeBoundary_NoSpline_LeavesEpsilonZeroAndLogsVerbose()
        {
            // What makes it fail: when the section has no spline cached, the
            // smoothed-position evaluator can't run; the propagator must
            // surface that as a "skip-no-spline" Verbose and fall back to
            // ε = 0. Without the gate, the propagator would compute
            // ε = worldRef - default(Vector3d) = worldRef, which is exactly
            // the absolute world position, not a correction.
            var rec = MakeRec("rec-rel-nospline", ReferenceFrame.Absolute, 0, 100);
            var stub = new StubResolver { RelWorldPos = new Vector3d(50, 60, 70) };
            AnchorPropagator.ResolverOverrideForTesting = stub;
            // Deliberately do NOT seed smoothed-position. Force the evaluator
            // to fall through to TryGetSmoothingSpline which returns false.
            AnchorPropagator.SmoothedPositionForTesting = null;
            SectionAnnotationStore.PutAnchorCandidates(rec.RecordingId, 0, new[]
            {
                new AnchorCandidate(50.0, AnchorSource.RelativeBoundary, AnchorSide.End),
            });

            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "rel-no-spline" },
                new[] { rec }, new RecordingTree[0], surfaceLookup: null);

            Assert.True(RenderSessionState.TryLookup(rec.RecordingId, 0, AnchorSide.End, out AnchorCorrection ac));
            Assert.Equal(0.0, ac.Epsilon.magnitude, 6);
            Assert.Contains(logLines,
                l => l.Contains("[VERBOSE][Pipeline-Anchor]") && l.Contains("skip-no-spline")
                    && l.Contains("RelativeBoundary"));
        }

        // --- section 7.5 OrbitalCheckpoint / 7.6 SoiTransition / 7.7 BubbleEntry, BubbleExit ---

        [Theory]
        [InlineData((int)AnchorSource.OrbitalCheckpoint, (int)AnchorSide.Start)]
        [InlineData((int)AnchorSource.SoiTransition, (int)AnchorSide.End)]
        [InlineData((int)AnchorSource.SoiTransition, (int)AnchorSide.Start)]
        [InlineData((int)AnchorSource.BubbleExit, (int)AnchorSide.Start)]
        [InlineData((int)AnchorSource.BubbleEntry, (int)AnchorSide.End)]
        public void BoundaryInstantSources_NeverConsultTheResolver(int sourceValue, int sideValue)
        {
            var source = (AnchorSource)sourceValue;
            var side = (AnchorSide)sideValue;
            // These sources compare two RECORDED positions in the body-fixed frame of the
            // boundary UT (AnchorPropagator.TryResolveBoundaryInstantEpsilon). A resolver
            // returning a world position, read at the rebuild instant, is what carried the
            // body's rotation into epsilon (todo REFLY-ANCHOR-EPSILON-1300KM), so a stub that
            // would answer every method must see no call, and the single-section recording
            // here (no checkpoint neighbour) resolves as a logged miss with epsilon 0.
            var rec = MakeRec("rec-boundary-instant-" + source, ReferenceFrame.Absolute, 0, 100);
            var stub = new StubResolver
            {
                RelWorldPos = new Vector3d(1, 1, 1),
                LoopWorldPos = new Vector3d(1, 1, 1),
            };
            AnchorPropagator.ResolverOverrideForTesting = stub;
            SeedSmoothed(rec, 50.0, Vector3d.zero);
            SectionAnnotationStore.PutAnchorCandidates(rec.RecordingId, 0, new[]
            {
                new AnchorCandidate(50.0, source, side),
            });

            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "boundary-instant-" + source },
                new[] { rec }, new RecordingTree[0], surfaceLookup: null);

            Assert.Equal(0, stub.RelCalls);
            Assert.Equal(0, stub.LoopCalls);
            Assert.True(RenderSessionState.TryLookup(rec.RecordingId, 0, side, out AnchorCorrection ac));
            Assert.Equal(source, ac.Source);
            Assert.Equal(0.0, ac.Epsilon.magnitude, 6);
            Assert.Contains(logLines,
                l => l.Contains("[VERBOSE][Pipeline-Anchor]") && l.Contains("resolver-miss")
                    && l.Contains("source=" + source) && l.Contains("reason=neighbour-out-of-range"));
        }

        // --- §7.10 Loop ----------------------------------------------------

        [Fact]
        public void Loop_ResolverHit_WritesEpsilonIntoSessionMap()
        {
            var rec = MakeRec("rec-loop", ReferenceFrame.Absolute, 0, 100);
            rec.LoopIntervalSeconds = 60.0;
            rec.LoopAnchorVesselId = 1234u;
            var stub = new StubResolver { LoopWorldPos = new Vector3d(500, 0, 0) };
            AnchorPropagator.ResolverOverrideForTesting = stub;
            SeedSmoothed(rec, 0.0, new Vector3d(100, 0, 0));
            SectionAnnotationStore.PutAnchorCandidates(rec.RecordingId, 0, new[]
            {
                new AnchorCandidate(0.0, AnchorSource.Loop, AnchorSide.Start),
            });

            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "loop-sess" },
                new[] { rec }, new RecordingTree[0], surfaceLookup: null);

            Assert.Equal(1, stub.LoopCalls);
            Assert.True(RenderSessionState.TryLookup(rec.RecordingId, 0, AnchorSide.Start, out AnchorCorrection ac));
            Assert.Equal(AnchorSource.Loop, ac.Source);
            // ε = (500,0,0) - (100,0,0) = (400,0,0)
            Assert.Equal(400.0, ac.Epsilon.x, 6);
        }

        // --- Deferred sources stay ε = 0 ----------------------------------

        [Fact]
        public void DeferredSource_SurfaceContinuous_DoesNotCallResolver_AndStaysZero()
        {
            // §7.9 marker only — Phase 7 wires the per-frame raycast. The
            // resolver must NOT be called for SurfaceContinuous candidates;
            // a stray call would mean the propagator was misclassifying the
            // source dispatch.
            var rec = MakeRec("rec-surface", ReferenceFrame.Absolute, 0, 100);
            var stub = new StubResolver { RelWorldPos = new Vector3d(99, 99, 99) };
            AnchorPropagator.ResolverOverrideForTesting = stub;
            SectionAnnotationStore.PutAnchorCandidates(rec.RecordingId, 0, new[]
            {
                new AnchorCandidate(0.0, AnchorSource.SurfaceContinuous, AnchorSide.Start),
            });

            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "surf-sess" },
                new[] { rec }, new RecordingTree[0], surfaceLookup: null);

            Assert.Equal(0, stub.RelCalls);
            Assert.Equal(0, stub.LoopCalls);
            Assert.True(RenderSessionState.TryLookup(rec.RecordingId, 0, AnchorSide.Start, out AnchorCorrection ac));
            Assert.Equal(0.0, ac.Epsilon.magnitude, 6);
        }

        [Fact]
        public void NullResolver_LogsSkipNoResolverAndLeavesEpsilonZero()
        {
            // What makes it fail: the propagator's null-resolver fallback
            // must be a clean ε = 0 + Verbose log, not a NullReferenceException.
            // RenderSessionState.RebuildFromMarker can never pass null in
            // production (always constructs a ProductionAnchorWorldFrameResolver),
            // but the test overload accepts null and exercises the same gate.
            var rec = MakeRec("rec-null-resolver", ReferenceFrame.Absolute, 0, 100);
            // Explicitly clear any test override.
            AnchorPropagator.ResolverOverrideForTesting = null;
            SectionAnnotationStore.PutAnchorCandidates(rec.RecordingId, 0, new[]
            {
                new AnchorCandidate(50.0, AnchorSource.RelativeBoundary, AnchorSide.End),
            });

            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "null-resolver" },
                new[] { rec }, new RecordingTree[0], surfaceLookup: null,
                resolver: null);

            Assert.True(RenderSessionState.TryLookup(rec.RecordingId, 0, AnchorSide.End, out AnchorCorrection ac));
            Assert.Equal(0.0, ac.Epsilon.magnitude, 6);
            Assert.Contains(logLines,
                l => l.Contains("[VERBOSE][Pipeline-Anchor]") && l.Contains("skip-no-resolver"));
        }
    }
}
