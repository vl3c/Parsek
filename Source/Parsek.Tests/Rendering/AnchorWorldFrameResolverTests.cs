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
    /// §7.5 / §7.6 / §7.10). The propagator delegates four world-frame
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
            AnchorCandidateBuilder.ResetForTesting();
            AnchorPropagator.ResetForTesting();
            AnchorCandidateBuilder.UseAnchorTaxonomyOverrideForTesting = true;
        }

        public void Dispose()
        {
            RenderSessionState.ResetForTesting();
            SectionAnnotationStore.ResetForTesting();
            AnchorCandidateBuilder.ResetForTesting();
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
            public Vector3d? OrbWorldPos;
            public Vector3d? SoiWorldPos;
            public Vector3d? LoopWorldPos;
            public Vector3d? BubbleWorldPos;
            public int RelCalls, OrbCalls, SoiCalls, LoopCalls, BubbleCalls;
            public string LastRelRecordingId, LastOrbRecordingId, LastSoiRecordingId, LastLoopRecordingId, LastBubbleRecordingId;
            public AnchorSide LastRelSide, LastOrbSide, LastSoiSide, LastLoopSide, LastBubbleSide;
            public double LastRelUT, LastOrbUT, LastSoiUT, LastLoopUT, LastBubbleUT;

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
            public bool TryResolveOrbitalCheckpointWorldPos(
                Recording rec, int sectionIndex, AnchorSide side, double boundaryUT, out Vector3d worldPos)
            {
                OrbCalls++;
                LastOrbRecordingId = rec?.RecordingId;
                LastOrbSide = side;
                LastOrbUT = boundaryUT;
                if (OrbWorldPos.HasValue) { worldPos = OrbWorldPos.Value; return true; }
                worldPos = default; return false;
            }
            public bool TryResolveSoiBoundaryWorldPos(
                Recording rec, int sectionIndex, AnchorSide side, double boundaryUT, out Vector3d worldPos)
            {
                SoiCalls++;
                LastSoiRecordingId = rec?.RecordingId;
                LastSoiSide = side;
                LastSoiUT = boundaryUT;
                if (SoiWorldPos.HasValue) { worldPos = SoiWorldPos.Value; return true; }
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
            public bool TryResolveBubbleEntryExitWorldPos(
                Recording rec, int sectionIndex, AnchorSide side, double boundaryUT, out Vector3d worldPos)
            {
                BubbleCalls++;
                LastBubbleRecordingId = rec?.RecordingId;
                LastBubbleSide = side;
                LastBubbleUT = boundaryUT;
                if (BubbleWorldPos.HasValue) { worldPos = BubbleWorldPos.Value; return true; }
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

        // --- §7.5 OrbitalCheckpoint ----------------------------------------

        [Fact]
        public void OrbitalCheckpoint_ResolverHit_WritesEpsilonIntoSessionMap()
        {
            var rec = MakeRec("rec-orb", ReferenceFrame.Absolute, 0, 100);
            var stub = new StubResolver { OrbWorldPos = new Vector3d(1000, 2000, 3000) };
            AnchorPropagator.ResolverOverrideForTesting = stub;
            SeedSmoothed(rec, 25.0, new Vector3d(900, 1800, 2700));
            SectionAnnotationStore.PutAnchorCandidates(rec.RecordingId, 0, new[]
            {
                new AnchorCandidate(25.0, AnchorSource.OrbitalCheckpoint, AnchorSide.Start),
            });

            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "orb-sess" },
                new[] { rec }, new RecordingTree[0], surfaceLookup: null);

            Assert.Equal(1, stub.OrbCalls);
            Assert.True(RenderSessionState.TryLookup(rec.RecordingId, 0, AnchorSide.Start, out AnchorCorrection ac));
            Assert.Equal(AnchorSource.OrbitalCheckpoint, ac.Source);
            // ε = (1000,2000,3000) - (900,1800,2700) = (100,200,300)
            Assert.Equal(100.0, ac.Epsilon.x, 6);
            Assert.Equal(200.0, ac.Epsilon.y, 6);
            Assert.Equal(300.0, ac.Epsilon.z, 6);
        }

        // --- §7.6 SoiTransition --------------------------------------------

        [Fact]
        public void SoiTransition_ResolverHit_WritesEpsilonIntoSessionMap()
        {
            var rec = MakeRec("rec-soi", ReferenceFrame.Absolute, 0, 100);
            var stub = new StubResolver { SoiWorldPos = new Vector3d(7, 8, 9) };
            AnchorPropagator.ResolverOverrideForTesting = stub;
            SeedSmoothed(rec, 75.0, new Vector3d(2, 3, 4));
            SectionAnnotationStore.PutAnchorCandidates(rec.RecordingId, 0, new[]
            {
                new AnchorCandidate(75.0, AnchorSource.SoiTransition, AnchorSide.End),
            });

            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "soi-sess" },
                new[] { rec }, new RecordingTree[0], surfaceLookup: null);

            Assert.Equal(1, stub.SoiCalls);
            Assert.True(RenderSessionState.TryLookup(rec.RecordingId, 0, AnchorSide.End, out AnchorCorrection ac));
            Assert.Equal(AnchorSource.SoiTransition, ac.Source);
            Assert.Equal(5.0, ac.Epsilon.x, 6);
            Assert.Equal(5.0, ac.Epsilon.y, 6);
            Assert.Equal(5.0, ac.Epsilon.z, 6);
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

        // --- §7.7 BubbleEntry / BubbleExit ---------------------------------

        [Fact]
        public void BubbleExit_ResolverHit_WritesEpsilonIntoSessionMap()
        {
            // §7.7 BubbleExit candidate lands on the Checkpoint segment's
            // Side=Start. The resolver returns a known world position;
            // the propagator computes ε = worldRef − smoothed.
            var rec = MakeRec("rec-bubble-exit", ReferenceFrame.Absolute, 0, 100);
            var stub = new StubResolver { BubbleWorldPos = new Vector3d(11, 22, 33) };
            AnchorPropagator.ResolverOverrideForTesting = stub;
            SeedSmoothed(rec, 60.0, new Vector3d(1, 2, 3));
            SectionAnnotationStore.PutAnchorCandidates(rec.RecordingId, 0, new[]
            {
                new AnchorCandidate(60.0, AnchorSource.BubbleExit, AnchorSide.Start),
            });

            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "bubble-exit-sess" },
                new[] { rec }, new RecordingTree[0], surfaceLookup: null);

            Assert.Equal(1, stub.BubbleCalls);
            Assert.Equal(rec.RecordingId, stub.LastBubbleRecordingId);
            Assert.Equal(AnchorSide.Start, stub.LastBubbleSide);
            Assert.Equal(60.0, stub.LastBubbleUT, 6);

            Assert.True(RenderSessionState.TryLookup(rec.RecordingId, 0, AnchorSide.Start, out AnchorCorrection ac));
            Assert.Equal(AnchorSource.BubbleExit, ac.Source);
            // ε = (11,22,33) - (1,2,3) = (10,20,30)
            Assert.Equal(10.0, ac.Epsilon.x, 6);
            Assert.Equal(20.0, ac.Epsilon.y, 6);
            Assert.Equal(30.0, ac.Epsilon.z, 6);
        }

        [Fact]
        public void BubbleEntry_ResolverHit_WritesEpsilonIntoSessionMap()
        {
            // §7.7 BubbleEntry candidate lands on the Checkpoint segment's
            // Side=End. Same shape as BubbleExit but the side flips.
            var rec = MakeRec("rec-bubble-entry", ReferenceFrame.Absolute, 0, 100);
            var stub = new StubResolver { BubbleWorldPos = new Vector3d(50, 60, 70) };
            AnchorPropagator.ResolverOverrideForTesting = stub;
            SeedSmoothed(rec, 40.0, new Vector3d(45, 55, 65));
            SectionAnnotationStore.PutAnchorCandidates(rec.RecordingId, 0, new[]
            {
                new AnchorCandidate(40.0, AnchorSource.BubbleEntry, AnchorSide.End),
            });

            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "bubble-entry-sess" },
                new[] { rec }, new RecordingTree[0], surfaceLookup: null);

            Assert.Equal(1, stub.BubbleCalls);
            Assert.Equal(AnchorSide.End, stub.LastBubbleSide);
            Assert.Equal(40.0, stub.LastBubbleUT, 6);

            Assert.True(RenderSessionState.TryLookup(rec.RecordingId, 0, AnchorSide.End, out AnchorCorrection ac));
            Assert.Equal(AnchorSource.BubbleEntry, ac.Source);
            // ε = (50,60,70) - (45,55,65) = (5,5,5)
            Assert.Equal(5.0, ac.Epsilon.x, 6);
            Assert.Equal(5.0, ac.Epsilon.y, 6);
            Assert.Equal(5.0, ac.Epsilon.z, 6);
        }

        [Fact]
        public void BubbleEntry_ResolverMiss_LeavesEpsilonZeroAndLogsVerbose()
        {
            // HR-9 visible failure: a resolver miss must reserve the slot
            // with ε = 0 AND emit a Pipeline-Anchor Verbose so the operator
            // can attribute the missing anchor in KSP.log.
            var rec = MakeRec("rec-bubble-entry-miss", ReferenceFrame.Absolute, 0, 100);
            var stub = new StubResolver { BubbleWorldPos = null };
            AnchorPropagator.ResolverOverrideForTesting = stub;
            SectionAnnotationStore.PutAnchorCandidates(rec.RecordingId, 0, new[]
            {
                new AnchorCandidate(50.0, AnchorSource.BubbleEntry, AnchorSide.End),
            });

            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "bubble-entry-miss-sess" },
                new[] { rec }, new RecordingTree[0], surfaceLookup: null);

            Assert.True(RenderSessionState.TryLookup(rec.RecordingId, 0, AnchorSide.End, out AnchorCorrection ac));
            Assert.Equal(AnchorSource.BubbleEntry, ac.Source);
            Assert.Equal(0.0, ac.Epsilon.magnitude, 6);
            Assert.Contains(logLines,
                l => l.Contains("[VERBOSE][Pipeline-Anchor]") && l.Contains("resolver-miss")
                    && l.Contains("BubbleEntry"));
        }

        [Fact]
        public void BubbleExit_NoSpline_LeavesEpsilonZeroAndLogsVerbose()
        {
            // When the section has no spline cached AND the section is not
            // a Checkpoint section (so the new Kepler-dispatch path doesn't
            // fire either), the smoothed-position evaluator returns null
            // and the propagator surfaces a "skip-no-spline" Verbose. ε
            // stays 0 (priority slot reserved per HR-9).
            var rec = MakeRec("rec-bubble-exit-nospline", ReferenceFrame.Absolute, 0, 100);
            var stub = new StubResolver { BubbleWorldPos = new Vector3d(7, 8, 9) };
            AnchorPropagator.ResolverOverrideForTesting = stub;
            // No spline, no test seam — force the evaluator to return null.
            AnchorPropagator.SmoothedPositionForTesting = null;
            SectionAnnotationStore.PutAnchorCandidates(rec.RecordingId, 0, new[]
            {
                new AnchorCandidate(50.0, AnchorSource.BubbleExit, AnchorSide.Start),
            });

            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "bubble-exit-nospline" },
                new[] { rec }, new RecordingTree[0], surfaceLookup: null);

            Assert.True(RenderSessionState.TryLookup(rec.RecordingId, 0, AnchorSide.Start, out AnchorCorrection ac));
            Assert.Equal(0.0, ac.Epsilon.magnitude, 6);
            Assert.Contains(logLines,
                l => l.Contains("[VERBOSE][Pipeline-Anchor]") && l.Contains("skip-no-spline")
                    && l.Contains("BubbleExit"));
        }

        [Fact]
        public void DeferredSource_BubbleEntry_NoLongerDeferred_ResolverIsCalled()
        {
            // Regression pin for the §7.7 gate removal. Pre-v0.9.1 the
            // deferred-source switch in TryResolveSeedEpsilon early-returned
            // for BubbleEntry / BubbleExit so the resolver was NEVER called.
            // After the gate removal a BubbleEntry candidate must hit the
            // resolver (BubbleCalls > 0) — a future regression that re-adds
            // the gate would silently drop ε to zero again.
            var rec = MakeRec("rec-bubble-no-defer", ReferenceFrame.Absolute, 0, 100);
            var stub = new StubResolver { BubbleWorldPos = new Vector3d(1, 1, 1) };
            AnchorPropagator.ResolverOverrideForTesting = stub;
            SeedSmoothed(rec, 50.0, Vector3d.zero);
            SectionAnnotationStore.PutAnchorCandidates(rec.RecordingId, 0, new[]
            {
                new AnchorCandidate(50.0, AnchorSource.BubbleEntry, AnchorSide.End),
            });

            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "bubble-no-defer" },
                new[] { rec }, new RecordingTree[0], surfaceLookup: null);

            Assert.Equal(1, stub.BubbleCalls);
            // No other resolver method should have fired — guards against
            // a switch fall-through bug that hits the wrong dispatch.
            Assert.Equal(0, stub.RelCalls);
            Assert.Equal(0, stub.OrbCalls);
            Assert.Equal(0, stub.SoiCalls);
            Assert.Equal(0, stub.LoopCalls);
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
            Assert.Equal(0, stub.OrbCalls);
            Assert.Equal(0, stub.SoiCalls);
            Assert.Equal(0, stub.LoopCalls);
            Assert.Equal(0, stub.BubbleCalls);
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
