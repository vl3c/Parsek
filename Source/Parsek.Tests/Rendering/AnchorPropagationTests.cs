using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 6 tests for <see cref="AnchorPropagator"/> (design doc §9.1,
    /// §17.3.1, §18 Phase 6, HR-13). Touches static state — runs in
    /// Sequential.
    /// </summary>
    [Collection("Sequential")]
    public class AnchorPropagationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public AnchorPropagationTests()
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

        // --- §9.1 Propagate pure helper ------------------------------------

        [Fact]
        public void Propagate_AppliesNinePointOneFormula()
        {
            // What makes it fail: a sign flip or argument swap would shift
            // the propagated ε by twice the offset delta — every chained
            // ghost would land 2x farther from the live vessel than the
            // recording shows.
            var eps = new Vector3d(10.0, 0.0, 0.0);
            var rec = new Vector3d(2.0, 1.0, 0.0);
            var smoothed = new Vector3d(2.0, 0.0, 0.0);
            // ε' = ε + (rec - smoothed) = (10, 0, 0) + (0, 1, 0) = (10, 1, 0)
            Vector3d result = AnchorPropagator.Propagate(eps, rec, smoothed);
            Assert.Equal(10.0, result.x, 6);
            Assert.Equal(1.0, result.y, 6);
            Assert.Equal(0.0, result.z, 6);
        }

        [Fact]
        public void Propagate_ZeroOffsetDelta_KeepsUpstreamEpsilon()
        {
            // Chain edges (PID continuity) feed zero recordedOffset and
            // zero smoothedOffset; the propagation must be the identity.
            var eps = new Vector3d(7.0, 8.0, 9.0);
            Vector3d result = AnchorPropagator.Propagate(eps, Vector3d.zero, Vector3d.zero);
            Assert.Equal(eps.x, result.x, 6);
            Assert.Equal(eps.y, result.y, 6);
            Assert.Equal(eps.z, result.z, 6);
        }

        // --- DAG walk wiring (test overload) -------------------------------

        private static Recording MakeRec(string id, double startUT, double endUT)
        {
            var rec = new Recording { RecordingId = id, VesselName = id };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = startUT,
                endUT = endUT,
                anchorVesselId = 0u,
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

        [Fact]
        public void Run_WithoutSeed_EmitsNonDockCandidatesIntoSession()
        {
            // What makes it fail: the propagator's seed pass must inject
            // the per-recording candidate set into the session map even
            // when no LiveSeparation seed exists. Otherwise re-fly sessions
            // outside the active branch wouldn't get any Phase 6 anchors.
            var rec = MakeRec("rec-seed", 0, 100);
            // Place a Loop candidate in the store directly so we can
            // assert the propagator copies it across.
            SectionAnnotationStore.PutAnchorCandidates(rec.RecordingId, 0,
                new[]
                {
                    new AnchorCandidate(0.0, AnchorSource.Loop, AnchorSide.Start),
                });

            var marker = new ReFlySessionMarker { SessionId = "seed-sess" };
            AnchorPropagator.Run(marker, new[] { rec }, new RecordingTree[0],
                surfaceLookup: null);

            Assert.True(RenderSessionState.TryLookup(rec.RecordingId, 0, AnchorSide.Start, out AnchorCorrection ac));
            Assert.Equal(AnchorSource.Loop, ac.Source);
        }

        [Fact]
        public void Run_FlagOff_SkipsEntireWalk()
        {
            // What makes it fail: the rollout flag must be respected at
            // the outermost gate — a buggy gate would let candidates bleed
            // into the session map even with the flag off.
            AnchorCandidateBuilder.UseAnchorTaxonomyOverrideForTesting = false;
            var rec = MakeRec("rec-flag-off", 0, 100);
            SectionAnnotationStore.PutAnchorCandidates(rec.RecordingId, 0,
                new[] { new AnchorCandidate(0.0, AnchorSource.Loop, AnchorSide.Start) });

            var marker = new ReFlySessionMarker { SessionId = "flag-off-sess" };
            AnchorPropagator.Run(marker, new[] { rec }, new RecordingTree[0],
                surfaceLookup: null);

            Assert.Equal(0, RenderSessionState.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-AnchorPropagate]") && l.Contains("useAnchorTaxonomy=false"));
        }

        [Fact]
        public void Run_PropagatesAcrossDockEdge_WithLiveSeed()
        {
            // What makes it fail: a Dock BP between rec-A (parent, has Phase
            // 2 LiveSeparation seed) and rec-B (child, no seed) must let
            // rec-B inherit ε via propagation. A missing edge enumeration
            // would leave rec-B with ε=0.
            var parent = MakeRec("rec-parent", 0, 100);
            var child = MakeRec("rec-child", 50, 150);

            // Seed parent's Start ε via PutAnchorForTesting (Phase 2-style).
            RenderSessionState.PutAnchorForTesting(new AnchorCorrection(
                recordingId: parent.RecordingId, sectionIndex: 0,
                side: AnchorSide.Start, ut: 0.0,
                epsilon: new Vector3d(5.0, 0.0, 0.0),
                source: AnchorSource.LiveSeparation));

            var tree = new RecordingTree { Id = "t-dag" };
            tree.Recordings[parent.RecordingId] = parent;
            tree.Recordings[child.RecordingId] = child;
            var bp = new BranchPoint
            {
                Id = "dock", UT = 75.0,
                Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { parent.RecordingId },
                ChildRecordingIds = new List<string> { child.RecordingId },
            };
            tree.BranchPoints.Add(bp);

            var marker = new ReFlySessionMarker { SessionId = "dag-sess", TreeId = tree.Id };
            AnchorPropagator.Run(marker, new[] { parent, child }, new[] { tree },
                surfaceLookup: null);

            // Child's start ε should be propagated. Phase 6 uses zero for
            // the recordedOffset/smoothedOffset placeholders, so the result
            // equals the upstream ε (5,0,0).
            Assert.True(RenderSessionState.TryLookup(child.RecordingId, 0, AnchorSide.Start, out AnchorCorrection ac));
            Assert.Equal(5.0, ac.Epsilon.x, 3);
            Assert.Equal(AnchorSource.DockOrMerge, ac.Source);
        }

        [Fact]
        public void Run_DuplicateEdge_TriggersCycleGuard_Warn()
        {
            // Reviewer Nit: this test was misleadingly named
            // Run_SuppressedChild_SkipsEdgeAndLogsVerbose — it actually
            // exercises the duplicate-edge → cycle-guard path, not the
            // §9.4 / HR-8 suppression path. SessionSuppressionState's
            // full closure needs ParsekScenario.Instance which xUnit
            // can't stand up; this test verifies HR-13's defense-in-depth
            // catches a malformed DAG with duplicate edges.
            var rec = MakeRec("rec-cycle-1", 0, 100);
            var rec2 = MakeRec("rec-cycle-2", 0, 100);
            var tree = new RecordingTree { Id = "t-cycle" };
            tree.Recordings[rec.RecordingId] = rec;
            tree.Recordings[rec2.RecordingId] = rec2;
            var bpA = new BranchPoint
            {
                Id = "bp-cycle-a", UT = 50.0, Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { rec.RecordingId },
                ChildRecordingIds = new List<string> { rec2.RecordingId },
            };
            var bpB = new BranchPoint
            {
                Id = "bp-cycle-b", UT = 50.0, Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { rec.RecordingId },
                ChildRecordingIds = new List<string> { rec2.RecordingId },
            };
            tree.BranchPoints.Add(bpA);
            tree.BranchPoints.Add(bpB);

            var marker = new ReFlySessionMarker { SessionId = "cycle-sess", TreeId = tree.Id };
            AnchorPropagator.Run(marker, new[] { rec, rec2 }, new[] { tree },
                surfaceLookup: null);

            Assert.Contains(logLines, l =>
                l.Contains("[WARN][Pipeline-AnchorPropagate]") && l.Contains("cycle suspected"));
        }

        [Fact]
        public void Run_DAGWalkSummary_LogsInfoLine()
        {
            // What makes it fail: the close-out summary is the diagnostic
            // surface the operator uses to confirm Phase 6 actually ran.
            // A dropped Info line would let a silent flag-off or scope-empty
            // case escape detection.
            var rec = MakeRec("rec-summary", 0, 100);
            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "sum-sess" },
                new[] { rec }, new RecordingTree[0], surfaceLookup: null);

            Assert.Contains(logLines, l =>
                l.Contains("[INFO][Pipeline-AnchorPropagate]") && l.Contains("DAG walk start"));
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][Pipeline-AnchorPropagate]") && l.Contains("DAG walk summary"));
        }

        // ----- P1-1: Real §9.1 propagation rule ----------------------------

        private static Recording MakeRecWithSplineHook(string id, double startUT, double endUT)
        {
            var rec = MakeRec(id, startUT, endUT);
            // Inject a fake spline so TryEvaluatePerSegmentWorldPositions's
            // "spline cached + tag-0" branch fires. The control values are
            // irrelevant — we replace the smoothed-position call via the
            // SmoothedPositionForTesting seam in the test that uses this.
            SectionAnnotationStore.PutSmoothingSpline(rec.RecordingId, 0,
                new SmoothingSpline
                {
                    SplineType = 0, Tension = 0.5f, FrameTag = 0, IsValid = true,
                    KnotsUT = new[] { startUT, endUT },
                    ControlsX = new[] { 0f, 0f },
                    ControlsY = new[] { 0f, 0f },
                    ControlsZ = new[] { 0f, 0f },
                });
            return rec;
        }

        [Fact]
        public void Run_NinePointOne_AppliesRecordedMinusSmoothedDelta()
        {
            // Reviewer P1-1: the §9.1 rule must apply ε' = ε + (recordedOffset
            // - smoothedOffset) for non-chain edges. Pre-Phase-6 this was an
            // identity, which silently dropped the few-tick sampling-noise
            // correction for pre-Phase-9 recordings. The test pins the
            // non-identity behaviour: we inject deterministic recorded /
            // smoothed positions for parent and child via
            // TryEvaluatePerSegmentWorldPositions's surfaceLookup seam, and
            // verify ε'.x reflects the (recordedOffset - smoothedOffset)
            // delta exactly.
            //
            // recordedOffset = childRecorded - parentRecorded = (110,0,0) - (100,0,0) = (10,0,0)
            // smoothedOffset = childSmoothed - parentSmoothed = (105,0,0) - (101,0,0) = (4,0,0)
            // Δ = recordedOffset - smoothedOffset = (6,0,0)
            // ε_upstream = (3,0,0) (LiveSeparation seed on parent)
            // ε_child = (3+6,0,0) = (9,0,0)
            const double bpUT = 50.0;
            var parent = MakeRecWithSplineHook("rec-p", 0, 100);
            var child = MakeRecWithSplineHook("rec-c", 50, 150);

            // Surface lookup returns the recorded (lat,lon,alt) as the
            // world position directly — which is what TryFindFirstPointAtOrAfter
            // sees in our fixtures (lat=lon=alt=0). Override the smoothed
            // path via SmoothedPositionForTesting? No — that only feeds the
            // resolver-side smoothed path; the §9.1 rule reads through
            // TryEvaluatePerSegmentWorldPositions which calls
            // CatmullRomFit.Evaluate directly. Adjust the recorded points
            // and the spline control values to encode the desired offsets.
            //
            // Easier: stub the recorded points with distinct lat values so
            // surfaceLookup(body, lat, lon, alt) returns lat as world.x.
            // Set parent point at lat=100, child at lat=110, parent spline
            // controls at lat=101, child at lat=105.
            var parentPoint = new TrajectoryPoint
            {
                ut = bpUT, latitude = 100, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity,
            };
            parent.Points.Clear();
            parent.Points.Add(parentPoint);
            parent.TrackSections[0] = new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 0, endUT = 100,
                frames = new List<TrajectoryPoint> { parentPoint },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            };
            var childPoint = new TrajectoryPoint
            {
                ut = bpUT, latitude = 110, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity,
            };
            child.Points.Clear();
            child.Points.Add(childPoint);
            child.TrackSections[0] = new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 50, endUT = 150,
                frames = new List<TrajectoryPoint> { childPoint },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            };
            // Override splines to constant lat values for deterministic
            // smoothed-eval results. CatmullRomFit.Evaluate returns the
            // control point's (x,y,z) as (lat,lon,alt) when the spline has
            // exactly two knots at the same value.
            SectionAnnotationStore.PutSmoothingSpline(parent.RecordingId, 0,
                new SmoothingSpline
                {
                    SplineType = 0, Tension = 0.5f, FrameTag = 0, IsValid = true,
                    KnotsUT = new[] { 0.0, 100.0 },
                    ControlsX = new[] { 101f, 101f },
                    ControlsY = new[] { 0f, 0f },
                    ControlsZ = new[] { 0f, 0f },
                });
            SectionAnnotationStore.PutSmoothingSpline(child.RecordingId, 0,
                new SmoothingSpline
                {
                    SplineType = 0, Tension = 0.5f, FrameTag = 0, IsValid = true,
                    KnotsUT = new[] { 50.0, 150.0 },
                    ControlsX = new[] { 105f, 105f },
                    ControlsY = new[] { 0f, 0f },
                    ControlsZ = new[] { 0f, 0f },
                });

            var seedEpsilon = new Vector3d(3.0, 0.0, 0.0);
            RenderSessionState.PutAnchorForTesting(new AnchorCorrection(
                recordingId: parent.RecordingId, sectionIndex: 0,
                side: AnchorSide.Start, ut: 0.0,
                epsilon: seedEpsilon, source: AnchorSource.LiveSeparation));

            var tree = new RecordingTree { Id = "t-91" };
            tree.Recordings[parent.RecordingId] = parent;
            tree.Recordings[child.RecordingId] = child;
            var bp = new BranchPoint
            {
                Id = "dock-91", UT = bpUT, Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { parent.RecordingId },
                ChildRecordingIds = new List<string> { child.RecordingId },
            };
            tree.BranchPoints.Add(bp);

            // surfaceLookup returns lat/lon/alt as world.x/y/z so the test
            // can predict the offset arithmetic deterministically.
            Func<string, double, double, double, Vector3d> surfaceLookup =
                (b, lat, lon, alt) => new Vector3d(lat, lon, alt);

            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "91-sess", TreeId = tree.Id },
                new[] { parent, child }, new[] { tree }, surfaceLookup);

            Assert.True(RenderSessionState.TryLookup(child.RecordingId, 0, AnchorSide.Start, out AnchorCorrection ac));
            // Δ = (recorded.x - smoothed.x parent) - (recorded.x - smoothed.x child)
            // recordedOffset.x = 110 - 100 = 10
            // smoothedOffset.x = 105 - 101 = 4
            // Δ.x = 10 - 4 = 6
            // ε_child.x = 3 + 6 = 9
            Assert.Equal(9.0, ac.Epsilon.x, 3);
            Assert.Equal(0.0, ac.Epsilon.y, 3);
            Assert.Equal(0.0, ac.Epsilon.z, 3);

            // The Edge-propagated Verbose should advertise nineOneApplied=true.
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE][Pipeline-AnchorPropagate]")
                && l.Contains("Edge propagated")
                && l.Contains("nineOneApplied=true"));
        }

        [Fact]
        public void Run_NinePointOne_FallsBackToIdentity_WhenNoSpline()
        {
            // Reviewer P1-1: when either side lacks a spline cache, the
            // §9.1 helper still succeeds (it falls through to "smoothed =
            // recorded" so the offset delta is zero) but emits a
            // no-spline-skip Verbose so the operator can see the
            // degraded propagation. ε' = ε in that configuration —
            // identity propagation is the safe fallback.
            const double bpUT = 50.0;
            var parent = MakeRec("rec-no-spline-p", 0, 100);
            var child = MakeRec("rec-no-spline-c", 50, 150);

            // Make recorded points distinct so the recorded delta would
            // be non-zero IF the §9.1 path read them — but with no spline
            // cached, smoothed equals recorded on both sides and the
            // delta cancels, returning identity.
            var parentPoint2 = new TrajectoryPoint
            {
                ut = bpUT, latitude = 100, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity,
            };
            parent.Points.Clear();
            parent.Points.Add(parentPoint2);
            parent.TrackSections[0] = new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 0, endUT = 100,
                frames = new List<TrajectoryPoint> { parentPoint2 },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            };
            var childPoint2 = new TrajectoryPoint
            {
                ut = bpUT, latitude = 110, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity,
            };
            child.Points.Clear();
            child.Points.Add(childPoint2);
            child.TrackSections[0] = new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 50, endUT = 150,
                frames = new List<TrajectoryPoint> { childPoint2 },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            };
            var seedEpsilon = new Vector3d(7.0, 0.0, 0.0);
            RenderSessionState.PutAnchorForTesting(new AnchorCorrection(
                recordingId: parent.RecordingId, sectionIndex: 0,
                side: AnchorSide.Start, ut: 0.0,
                epsilon: seedEpsilon, source: AnchorSource.LiveSeparation));

            var tree = new RecordingTree { Id = "t-no-spline" };
            tree.Recordings[parent.RecordingId] = parent;
            tree.Recordings[child.RecordingId] = child;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "dock-no-spline", UT = bpUT, Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { parent.RecordingId },
                ChildRecordingIds = new List<string> { child.RecordingId },
            });

            Func<string, double, double, double, Vector3d> surfaceLookup =
                (b, lat, lon, alt) => new Vector3d(lat, lon, alt);

            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "no-spline-sess" },
                new[] { parent, child }, new[] { tree }, surfaceLookup);

            // Identity propagation — ε' = ε_upstream = (7,0,0).
            Assert.True(RenderSessionState.TryLookup(child.RecordingId, 0, AnchorSide.Start, out AnchorCorrection ac));
            Assert.Equal(7.0, ac.Epsilon.x, 3);

            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE][Pipeline-AnchorPropagate]") && l.Contains("no-spline-skip"));
        }

        // ----- P1-2: Chain edges enumerated by ChainId + ChainIndex ------

        [Fact]
        public void Run_ChainContinuity_PropagatesAcrossChainIdGroup()
        {
            // Reviewer P1-2: chain continuity is encoded by Recording.ChainId
            // + ChainIndex, NOT ParentRecordingId (which is EVA linkage).
            // The propagator must enumerate consecutive chain members
            // (sorted by ChainIndex) and emit a chain edge between them.
            // Without this, multi-recording chains — exactly the §9.3 case
            // — silently lose ε propagation.
            //
            // chain[0] = parent (ChainIndex=0), chain[1] = child (ChainIndex=1).
            // parent.EndUT = child.StartUT = 100.
            var part0 = MakeRec("chain-0", 0, 100);
            part0.ChainId = "abc";
            part0.ChainIndex = 0;
            // EndUT comes from rec.Points; ensure parent.EndUT == 100 by
            // adding a final point at ut=100.
            part0.Points.Add(new TrajectoryPoint
            {
                ut = 100, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity,
            });
            var part1 = MakeRec("chain-1", 100, 200);
            part1.ChainId = "abc";
            part1.ChainIndex = 1;
            // child.StartUT = 100 (rec.Points[0].ut from MakeRec).
            // (MakeRec creates points at startUT and endUT.)

            var seedEpsilon = new Vector3d(11.0, 0.0, 0.0);
            RenderSessionState.PutAnchorForTesting(new AnchorCorrection(
                recordingId: part0.RecordingId, sectionIndex: 0,
                side: AnchorSide.End, ut: 100.0,
                epsilon: seedEpsilon, source: AnchorSource.LiveSeparation));

            AnchorPropagator.Run(
                new ReFlySessionMarker { SessionId = "chain-sess" },
                new[] { part0, part1 },
                new RecordingTree[0], // no BranchPoints — only chain edges in scope
                surfaceLookup: null);

            // chain-1's Start ε should equal chain-0's End ε (chain edges
            // identity-propagate: recordedOffset is zero by PID continuity).
            Assert.True(RenderSessionState.TryLookup(part1.RecordingId, 0, AnchorSide.Start, out AnchorCorrection ac));
            Assert.Equal(seedEpsilon.x, ac.Epsilon.x, 3);
            Assert.Equal(AnchorSource.DockOrMerge, ac.Source);

            // Edge-propagated Verbose with chainEdge=true.
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE][Pipeline-AnchorPropagate]")
                && l.Contains("Edge propagated")
                && l.Contains("chainEdge=true"));
        }

        [Fact]
        public void Run_ChainContinuity_DoesNotEnumerateParentRecordingIdEvaLinkage()
        {
            // Reviewer P1-2: ParentRecordingId is EVA child linkage, not
            // chain continuity. Two recordings linked ONLY by
            // ParentRecordingId (no ChainId, no BranchPoint) must NOT
            // produce a propagated edge — that path is covered by the
            // BranchPointType.EVA loop and would be double-counted under
            // chain treatment.
            var parent = MakeRec("eva-parent", 0, 100);
            var evaChild = MakeRec("eva-child", 50, 150);
            evaChild.ParentRecordingId = parent.RecordingId;
            // Deliberately no ChainId / ChainIndex / BranchPoint.

            // Seed parent so a propagation would write something we can detect.
            RenderSessionState.PutAnchorForTesting(new AnchorCorrection(
                recordingId: parent.RecordingId, sectionIndex: 0,
                side: AnchorSide.End, ut: 100.0,
                epsilon: new Vector3d(33.0, 0.0, 0.0),
                source: AnchorSource.LiveSeparation));

            int beforeCount = RenderSessionState.Count;
            AnchorPropagator.Run(
                new ReFlySessionMarker { SessionId = "no-eva-prop" },
                new[] { parent, evaChild }, new RecordingTree[0],
                surfaceLookup: null);

            // No propagation to evaChild — only the seed survives.
            Assert.False(RenderSessionState.TryLookup(evaChild.RecordingId, 0, AnchorSide.Start, out _));
        }

        // ----- §9.4 / HR-8 suppressed-predecessor -------------------------

        [Fact]
        public void Run_SuppressedChild_SkipsEdgeAndLogsVerbose()
        {
            // Reviewer Nit + design doc §9.4 / HR-8: a suppressed child
            // cannot inherit ε from its parent. The walk must skip the
            // edge AND emit a Pipeline-AnchorPropagate Verbose
            // "suppressed-predecessor" line so the operator can attribute
            // the missing ε in KSP.log. Without the test, the §9.4
            // closure could regress silently.
            var parent = MakeRec("supp-parent", 0, 100);
            var child = MakeRec("supp-child", 50, 150);
            // Seed the parent so a propagation would write something to
            // the child if HR-8 were broken.
            RenderSessionState.PutAnchorForTesting(new AnchorCorrection(
                recordingId: parent.RecordingId, sectionIndex: 0,
                side: AnchorSide.End, ut: 75.0,
                epsilon: new Vector3d(99.0, 0.0, 0.0),
                source: AnchorSource.LiveSeparation));

            var tree = new RecordingTree { Id = "t-supp" };
            tree.Recordings[parent.RecordingId] = parent;
            tree.Recordings[child.RecordingId] = child;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "supp-bp", UT = 75.0, Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { parent.RecordingId },
                ChildRecordingIds = new List<string> { child.RecordingId },
            });

            // Inject the suppression seam so the propagator treats the
            // child as suppressed. Production code routes this through
            // SessionSuppressionState which xUnit can't stand up.
            AnchorPropagator.SuppressionPredicateForTesting = id =>
                string.Equals(id, child.RecordingId, StringComparison.Ordinal);

            AnchorPropagator.Run(
                new ReFlySessionMarker { SessionId = "supp-sess", TreeId = tree.Id },
                new[] { parent, child }, new[] { tree }, surfaceLookup: null);

            Assert.False(RenderSessionState.TryLookup(child.RecordingId, 0, AnchorSide.Start, out _),
                "Suppressed child must not receive a propagated ε");
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE][Pipeline-AnchorPropagate]")
                && l.Contains("suppressed-predecessor")
                && l.Contains("reason=suppressed-child"));
        }

        [Fact]
        public void Run_SuppressedParent_SkipsEdgeAndLogsVerbose()
        {
            // §9.4 / HR-8 symmetric case: a suppressed parent cannot
            // export ε to a non-suppressed child either. The reason
            // string flips to "suppressed-parent" so the diagnostic
            // surface tells the operator which side blocked the edge.
            var parent = MakeRec("supp-p2", 0, 100);
            var child = MakeRec("supp-c2", 50, 150);
            var tree = new RecordingTree { Id = "t-supp2" };
            tree.Recordings[parent.RecordingId] = parent;
            tree.Recordings[child.RecordingId] = child;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "supp-bp2", UT = 75.0, Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { parent.RecordingId },
                ChildRecordingIds = new List<string> { child.RecordingId },
            });

            AnchorPropagator.SuppressionPredicateForTesting = id =>
                string.Equals(id, parent.RecordingId, StringComparison.Ordinal);

            AnchorPropagator.Run(
                new ReFlySessionMarker { SessionId = "supp2-sess", TreeId = tree.Id },
                new[] { parent, child }, new[] { tree }, surfaceLookup: null);

            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE][Pipeline-AnchorPropagate]")
                && l.Contains("suppressed-predecessor")
                && l.Contains("reason=suppressed-parent"));
        }

        [Fact]
        public void Run_ChainContinuity_BoundaryMismatch_LogsAndSkips()
        {
            // Reviewer P1-2: when chain members are misordered or have a
            // gap (parent.EndUT != child.StartUT within tolerance), the
            // emitter must skip the edge and surface the mismatch.
            var part0 = MakeRec("chain-mis-0", 0, 100);
            part0.ChainId = "mis";
            part0.ChainIndex = 0;
            // Add explicit endUT point to make parent.EndUT well-defined.
            part0.Points.Add(new TrajectoryPoint
            {
                ut = 100, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity,
            });
            var part1 = MakeRec("chain-mis-1", 200, 300); // gap of 100 s
            part1.ChainId = "mis";
            part1.ChainIndex = 1;

            AnchorPropagator.Run(
                new ReFlySessionMarker { SessionId = "chain-mis-sess" },
                new[] { part0, part1 }, new RecordingTree[0],
                surfaceLookup: null);

            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE][Pipeline-AnchorPropagate]")
                && l.Contains("chain-edge-boundary-mismatch"));
            Assert.False(RenderSessionState.TryLookup(part1.RecordingId, 0, AnchorSide.Start, out _));
        }
    }
}
