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

            // Seed rec so the worklist starts walking. Without a seed the
            // worklist-based propagator (P2-A) never reaches the
            // duplicate-edge path because there's no anchor to propagate.
            RenderSessionState.PutAnchorForTesting(new AnchorCorrection(
                recordingId: rec.RecordingId, sectionIndex: 0,
                side: AnchorSide.Start, ut: 0.0,
                epsilon: new Vector3d(1.0, 0, 0),
                source: AnchorSource.LiveSeparation));

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

            // Seed parent so the worklist starts. The suppression check
            // fires inside the edge walk, after the seed pulls the parent
            // onto the queue.
            RenderSessionState.PutAnchorForTesting(new AnchorCorrection(
                recordingId: parent.RecordingId, sectionIndex: 0,
                side: AnchorSide.End, ut: 75.0,
                epsilon: new Vector3d(1.0, 0, 0),
                source: AnchorSource.LiveSeparation));

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

        // ----- ultrareview P1-B: §9.1 inertial-frame dispatch -------------

        [Fact]
        public void Run_NinePointOne_Inertial_AppliesRecordedMinusSmoothedDelta()
        {
            // Reviewer P1-B: the previous helper treated only FrameTag=0
            // splines as a real spline hit. FrameTag=1 (ExoPropulsive /
            // ExoBallistic — every burn / coast section in production)
            // fell back to smoothedWorld = recordedWorld so the §9.1
            // correction term cancelled to zero. This test pins the fix
            // by injecting an inertial-FrameTag spline on both sides of
            // an edge, providing a CelestialBody body resolver and
            // RotationPeriodForTesting / WorldSurfacePositionForTesting
            // seams, and asserting the propagator computes a non-zero
            // (recordedOffset - smoothedOffset) term using
            // TrajectoryMath.FrameTransform.LowerFromInertialToWorld.
            const double bpUT = 50.0;
            const double rotationPeriod = 21549.425; // Kerbin sidereal day

            // Construct a synthetic CelestialBody and seed FrameTransform
            // seams so DispatchSplineWorldByFrameTag's tag=1 branch can run
            // without the live KSP API.
            CelestialBody fakeKerbin = TestBodyRegistry.CreateBody(
                "Kerbin", radius: 600000.0, gravParameter: 3.5316e12);
            try
            {
                TrajectoryMath.FrameTransform.RotationPeriodForTesting = b =>
                    object.ReferenceEquals(b, fakeKerbin) ? rotationPeriod : double.NaN;
                // Body resolver returns the fake Kerbin; surface-lookup
                // (lat,lon,alt) -> world maps lat/lon/alt linearly so the
                // arithmetic is human-checkable.
                TrajectoryMath.FrameTransform.WorldSurfacePositionForTesting =
                    (b, lat, lon, alt) => new Vector3d(lat, lon, alt);
                AnchorPropagator.BodyResolverForTesting = name =>
                    string.Equals(name, "Kerbin", StringComparison.Ordinal) ? fakeKerbin : null;

                var parent = MakeRec("rec-inertial-p", 0, 100);
                var child = MakeRec("rec-inertial-c", 50, 150);

                // Recorded points (lat,lon,alt) → surfaceLookup returns
                // them as world-space directly. Parent.lat=100, child.lat=110.
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

                // Inertial splines: the spline controls are
                // (lat, inertialLon, alt). LowerFromInertialToWorld at
                // playbackUT=bpUT subtracts the rotation phase from the
                // inertial-lon, then surfaceLookup maps to world. We
                // pre-bake the inertial-lon control values so that
                // (controlLon - phase) returns the desired body-fixed
                // longitude after lowering — and surfaceLookup turns
                // (lat, lonAfterLower, alt) into world.x/y/z.
                //
                // Pick: parent smoothed.lat=101, child smoothed.lat=105.
                // We don't care about the rotation phase as long as both
                // sides see the same phase (they do — same body, same UT).
                // Set inertialLon = 0 + phase so the lowered lon = 0.
                double phaseAtBpUT = (bpUT * 360.0) / rotationPeriod;
                SectionAnnotationStore.PutSmoothingSpline(parent.RecordingId, 0,
                    new SmoothingSpline
                    {
                        SplineType = 0, Tension = 0.5f, FrameTag = 1, IsValid = true,
                        KnotsUT = new[] { 0.0, 100.0 },
                        ControlsX = new[] { 101f, 101f },
                        ControlsY = new[] { (float)phaseAtBpUT, (float)phaseAtBpUT },
                        ControlsZ = new[] { 0f, 0f },
                    });
                SectionAnnotationStore.PutSmoothingSpline(child.RecordingId, 0,
                    new SmoothingSpline
                    {
                        SplineType = 0, Tension = 0.5f, FrameTag = 1, IsValid = true,
                        KnotsUT = new[] { 50.0, 150.0 },
                        ControlsX = new[] { 105f, 105f },
                        ControlsY = new[] { (float)phaseAtBpUT, (float)phaseAtBpUT },
                        ControlsZ = new[] { 0f, 0f },
                    });

                // Δ.x = (110 - 100) - (105 - 101) = 10 - 4 = 6
                // ε_upstream = (3, 0, 0) → ε_child = (9, 0, 0)
                RenderSessionState.PutAnchorForTesting(new AnchorCorrection(
                    recordingId: parent.RecordingId, sectionIndex: 0,
                    side: AnchorSide.Start, ut: 0.0,
                    epsilon: new Vector3d(3.0, 0.0, 0.0),
                    source: AnchorSource.LiveSeparation));

                var tree = new RecordingTree { Id = "t-inertial" };
                tree.Recordings[parent.RecordingId] = parent;
                tree.Recordings[child.RecordingId] = child;
                tree.BranchPoints.Add(new BranchPoint
                {
                    Id = "dock-inertial", UT = bpUT, Type = BranchPointType.Dock,
                    ParentRecordingIds = new List<string> { parent.RecordingId },
                    ChildRecordingIds = new List<string> { child.RecordingId },
                });

                Func<string, double, double, double, Vector3d> surfaceLookup =
                    (b, lat, lon, alt) => new Vector3d(lat, lon, alt);

                AnchorPropagator.Run(
                    new ReFlySessionMarker { SessionId = "inertial-91-sess", TreeId = tree.Id },
                    new[] { parent, child }, new[] { tree }, surfaceLookup);

                Assert.True(RenderSessionState.TryLookup(child.RecordingId, 0, AnchorSide.Start, out AnchorCorrection ac));
                Assert.Equal(9.0, ac.Epsilon.x, 3);
                Assert.Equal(0.0, ac.Epsilon.y, 3);
                Assert.Equal(0.0, ac.Epsilon.z, 3);

                // The Edge-propagated Verbose advertises nineOneApplied=true
                // — proving the helper did NOT fall through to the recorded
                // sample (which would have produced nineOneApplied=true but
                // with offset delta = 0 because smoothed=recorded on both
                // sides). We pin the ε value above; the log line is the
                // belt-and-braces check.
                Assert.Contains(logLines, l =>
                    l.Contains("[VERBOSE][Pipeline-AnchorPropagate]")
                    && l.Contains("Edge propagated")
                    && l.Contains("nineOneApplied=true"));
                // Negative pin: the helper must NOT log the no-spline-skip
                // Verbose because both sides DID hit the inertial spline.
                Assert.DoesNotContain(logLines, l =>
                    l.Contains("[VERBOSE][Pipeline-AnchorPropagate]")
                    && l.Contains("no-spline-skip"));
            }
            finally
            {
                TrajectoryMath.FrameTransform.ResetForTesting();
                AnchorPropagator.ResetForTesting();
            }
        }

        [Fact]
        public void TryEvaluatePerSegmentWorldPositions_InertialSpline_DispatchesThroughBodyResolver()
        {
            // Direct unit test of the inertial branch in
            // TryEvaluatePerSegmentWorldPositions. Pins that with a body
            // resolver returning a non-null CelestialBody plus the
            // FrameTransform seams set, the helper drives
            // DispatchSplineWorldByFrameTag(FrameTag=1) and writes the
            // lowered world position into smoothedWorld.
            CelestialBody fakeKerbin = TestBodyRegistry.CreateBody(
                "Kerbin", radius: 600000.0, gravParameter: 3.5316e12);
            try
            {
                const double rotationPeriod = 21549.425;
                TrajectoryMath.FrameTransform.RotationPeriodForTesting = b =>
                    object.ReferenceEquals(b, fakeKerbin) ? rotationPeriod : double.NaN;
                TrajectoryMath.FrameTransform.WorldSurfacePositionForTesting =
                    (b, lat, lon, alt) => new Vector3d(lat, lon, alt);

                var rec = MakeRec("rec-direct-inertial", 0, 100);
                var p = new TrajectoryPoint
                {
                    ut = 50, latitude = 50, longitude = 0, altitude = 0,
                    bodyName = "Kerbin", rotation = Quaternion.identity,
                };
                rec.Points.Clear(); rec.Points.Add(p);
                rec.TrackSections[0] = new TrackSection
                {
                    referenceFrame = ReferenceFrame.Absolute,
                    environment = SegmentEnvironment.ExoBallistic,
                    startUT = 0, endUT = 100,
                    frames = new List<TrajectoryPoint> { p },
                    checkpoints = new List<OrbitSegment>(),
                    source = TrackSectionSource.Active,
                };
                double phase = (50.0 * 360.0) / rotationPeriod;
                SectionAnnotationStore.PutSmoothingSpline(rec.RecordingId, 0,
                    new SmoothingSpline
                    {
                        SplineType = 0, Tension = 0.5f, FrameTag = 1, IsValid = true,
                        KnotsUT = new[] { 0.0, 100.0 },
                        ControlsX = new[] { 77f, 77f },
                        ControlsY = new[] { (float)phase, (float)phase },
                        ControlsZ = new[] { 0f, 0f },
                    });

                Func<string, double, double, double, Vector3d> surfaceLookup =
                    (b, lat, lon, alt) => new Vector3d(lat, lon, alt);
                Func<string, CelestialBody> bodyResolver = name =>
                    string.Equals(name, "Kerbin", StringComparison.Ordinal) ? fakeKerbin : null;

                bool ok = RenderSessionState.TryEvaluatePerSegmentWorldPositions(
                    rec, sectionIndex: 0, ut: 50.0,
                    surfaceLookup,
                    out Vector3d recordedWorld, out Vector3d smoothedWorld,
                    out bool splineHit, out string failureReason,
                    bodyResolver);

                Assert.True(ok);
                Assert.Null(failureReason);
                Assert.True(splineHit, "Inertial path must set splineHit=true with a body resolver");
                // Recorded sample.lat=50, lon=0, alt=0 → recordedWorld=(50,0,0).
                Assert.Equal(50.0, recordedWorld.x, 6);
                // Smoothed: lat=77, lonAfterLower=phase-phase=0, alt=0 → (77,0,0).
                Assert.Equal(77.0, smoothedWorld.x, 3);
                Assert.NotEqual(recordedWorld.x, smoothedWorld.x);
            }
            finally
            {
                TrajectoryMath.FrameTransform.ResetForTesting();
            }
        }

        // ----- ultrareview P2-A: worklist propagation is order-independent ----

        [Fact]
        public void Run_DeepChain_ReversedBranchPointOrder_StillPropagatesEnd2End()
        {
            // Reviewer P2-A: RecordingTree.BranchPoints has no enforced
            // topological order. The previous single-pass walk processed
            // edges in list order — if a downstream edge appeared before
            // its upstream parent had its anchor seeded, the propagator
            // wrote zero ε and never revisited the child. Production
            // multi-recording chains can easily have non-topological
            // ordering. The worklist refactor follows edges outward from
            // anchored seeds, so list order is irrelevant.
            //
            // Build a 4-recording linear chain via 3 BranchPoints in
            // REVERSED list order:
            //   recA -- bp0 --> recB -- bp1 --> recC -- bp2 --> recD
            // BranchPoints are inserted bp2, bp1, bp0 — so a single-pass
            // walk processes recC->recD first (recC has no anchor yet),
            // then recB->recC (recB has no anchor yet), then recA->recB
            // (recA HAS the seed). Pre-fix: only recB inherits ε; recC
            // and recD stay at zero. Post-fix: all three inherit.
            var recA = MakeRec("rec-rev-A", 0, 100);
            var recB = MakeRec("rec-rev-B", 100, 200);
            var recC = MakeRec("rec-rev-C", 200, 300);
            var recD = MakeRec("rec-rev-D", 300, 400);

            var seed = new Vector3d(13.0, 0.0, 0.0);
            RenderSessionState.PutAnchorForTesting(new AnchorCorrection(
                recordingId: recA.RecordingId, sectionIndex: 0,
                side: AnchorSide.End, ut: 100.0,
                epsilon: seed, source: AnchorSource.LiveSeparation));

            var tree = new RecordingTree { Id = "t-rev" };
            tree.Recordings[recA.RecordingId] = recA;
            tree.Recordings[recB.RecordingId] = recB;
            tree.Recordings[recC.RecordingId] = recC;
            tree.Recordings[recD.RecordingId] = recD;

            var bp0 = new BranchPoint
            {
                Id = "bp0", UT = 100.0, Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { recA.RecordingId },
                ChildRecordingIds = new List<string> { recB.RecordingId },
            };
            var bp1 = new BranchPoint
            {
                Id = "bp1", UT = 200.0, Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { recB.RecordingId },
                ChildRecordingIds = new List<string> { recC.RecordingId },
            };
            var bp2 = new BranchPoint
            {
                Id = "bp2", UT = 300.0, Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { recC.RecordingId },
                ChildRecordingIds = new List<string> { recD.RecordingId },
            };
            // Reversed order — bp2 (deepest) appears FIRST in the list.
            tree.BranchPoints.Add(bp2);
            tree.BranchPoints.Add(bp1);
            tree.BranchPoints.Add(bp0);

            AnchorPropagator.Run(
                new ReFlySessionMarker { SessionId = "rev-sess", TreeId = tree.Id },
                new[] { recA, recB, recC, recD }, new[] { tree },
                surfaceLookup: null);

            // recB / recC / recD should each have ε equal to the seed
            // (chain edges identity-propagate; non-chain Dock edges
            // identity-propagate when neither side has a spline cached
            // — which is the case here because no
            // PutSmoothingSpline calls were made).
            Assert.True(RenderSessionState.TryLookup(recB.RecordingId, 0, AnchorSide.Start, out AnchorCorrection acB));
            Assert.True(RenderSessionState.TryLookup(recC.RecordingId, 0, AnchorSide.Start, out AnchorCorrection acC));
            Assert.True(RenderSessionState.TryLookup(recD.RecordingId, 0, AnchorSide.Start, out AnchorCorrection acD));
            Assert.Equal(seed.x, acB.Epsilon.x, 3);
            Assert.Equal(seed.x, acC.Epsilon.x, 3);
            Assert.Equal(seed.x, acD.Epsilon.x, 3);

            // Three Edge-propagated Verbose lines should fire (one per
            // edge), regardless of list order.
            int edgePropagatedCount = 0;
            foreach (var line in logLines)
            {
                if (line.Contains("[VERBOSE][Pipeline-AnchorPropagate]")
                    && line.Contains("Edge propagated"))
                {
                    edgePropagatedCount++;
                }
            }
            Assert.Equal(3, edgePropagatedCount);
        }

        [Fact]
        public void Run_NoSeed_NoEdgesProcessed()
        {
            // Companion test: the worklist starts empty when no recording
            // has a seed anchor. With no live re-fly session and no
            // pre-emitted candidates, the propagator should write nothing.
            // This pins the seed-driven nature of the walk.
            var recA = MakeRec("rec-noseed-A", 0, 100);
            var recB = MakeRec("rec-noseed-B", 100, 200);
            var tree = new RecordingTree { Id = "t-noseed" };
            tree.Recordings[recA.RecordingId] = recA;
            tree.Recordings[recB.RecordingId] = recB;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "noseed-bp", UT = 100, Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { recA.RecordingId },
                ChildRecordingIds = new List<string> { recB.RecordingId },
            });

            AnchorPropagator.Run(
                new ReFlySessionMarker { SessionId = "noseed-sess", TreeId = tree.Id },
                new[] { recA, recB }, new[] { tree }, surfaceLookup: null);

            Assert.False(RenderSessionState.TryLookup(recA.RecordingId, 0, AnchorSide.Start, out _));
            Assert.False(RenderSessionState.TryLookup(recA.RecordingId, 0, AnchorSide.End, out _));
            Assert.False(RenderSessionState.TryLookup(recB.RecordingId, 0, AnchorSide.Start, out _));
            // No "Edge propagated" log line.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[VERBOSE][Pipeline-AnchorPropagate]")
                && l.Contains("Edge propagated"));
        }

        [Fact]
        public void TryEvaluatePerSegmentWorldPositions_InertialSpline_FallsBackWithoutBodyResolver()
        {
            // Companion negative test: when no body resolver is supplied
            // the inertial dispatch can't run, so the helper falls back
            // to splineHit=false and smoothedWorld=recordedWorld. This
            // pins the "no resolver" fallback and matches the docstring's
            // contract.
            var rec = MakeRec("rec-inertial-no-resolver", 0, 100);
            var p = new TrajectoryPoint
            {
                ut = 50, latitude = 50, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity,
            };
            rec.Points.Clear(); rec.Points.Add(p);
            rec.TrackSections[0] = new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 0, endUT = 100,
                frames = new List<TrajectoryPoint> { p },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            };
            SectionAnnotationStore.PutSmoothingSpline(rec.RecordingId, 0,
                new SmoothingSpline
                {
                    SplineType = 0, Tension = 0.5f, FrameTag = 1, IsValid = true,
                    KnotsUT = new[] { 0.0, 100.0 },
                    ControlsX = new[] { 99f, 99f },
                    ControlsY = new[] { 0f, 0f },
                    ControlsZ = new[] { 0f, 0f },
                });

            Func<string, double, double, double, Vector3d> surfaceLookup =
                (b, lat, lon, alt) => new Vector3d(lat, lon, alt);

            bool ok = RenderSessionState.TryEvaluatePerSegmentWorldPositions(
                rec, sectionIndex: 0, ut: 50.0,
                surfaceLookup,
                out Vector3d recordedWorld, out Vector3d smoothedWorld,
                out bool splineHit, out string failureReason,
                bodyResolver: null);

            Assert.True(ok);
            Assert.Null(failureReason);
            Assert.False(splineHit);
            // Without a resolver, smoothed mirrors recorded.
            Assert.Equal(recordedWorld.x, smoothedWorld.x, 6);
            Assert.Equal(recordedWorld.y, smoothedWorld.y, 6);
            Assert.Equal(recordedWorld.z, smoothedWorld.z, 6);
        }

        [Fact]
        public void Run_TwoSectionParent_EdgeOnUnanchoredSection_DefersUntilSlotIsSeededLater()
        {
            // /ultrareview P1: the per-recording worklist seeded a recording
            // when ANY of its sections had an anchor. An edge whose
            // parentSectionIdx pointed to a DIFFERENT, still-unanchored
            // section would then fall through to ε=0, write a stale child
            // anchor, and mark the edge in visitedEdges. When the real
            // upstream anchor for that section was later written, the edge
            // was already visited and the corrected ε never flowed
            // downstream — propagation was still order-dependent.
            //
            // The slot-keyed worklist guarantees an edge only fires when
            // its specific parent slot (recordingId, sectionIndex) has been
            // seeded. This test pins the recovery case: recB has section 0
            // seeded but section 1 unanchored; bp_BC requires recB.1; bp_AB
            // writes recB.1 from recA's seed. With recordings iterated in
            // [recB, recC, recA] order and BranchPoints in reversed list
            // order, the per-recording worklist would burn bp_BC with ε=0
            // when popping recB (because recB.1 is empty at that moment).
            // The slot-keyed worklist instead defers bp_BC until recB.1's
            // anchor lands via bp_AB, so recC inherits the correct ε.

            var recA = MakeRec("rec-multi-A", 0, 200);
            var recC = MakeRec("rec-multi-C", 200, 300);

            // Build recB with two sections [0,100] and [100,200] inline —
            // MakeRec only creates a single-section recording.
            var recB = new Recording { RecordingId = "rec-multi-B", VesselName = "rec-multi-B" };
            recB.TrackSections.Clear();
            recB.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 0, endUT = 100,
                anchorVesselId = 0u,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 0, bodyName = "Kerbin", rotation = Quaternion.identity },
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin", rotation = Quaternion.identity },
                },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            });
            recB.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 100, endUT = 200,
                anchorVesselId = 0u,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin", rotation = Quaternion.identity },
                    new TrajectoryPoint { ut = 200, bodyName = "Kerbin", rotation = Quaternion.identity },
                },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            });

            var seedA = new Vector3d(7.0, 0.0, 0.0);
            var seedB0 = new Vector3d(11.0, 0.0, 0.0);
            // Seed recA at section 0 START (UT=0). bp_AB at UT=150 still
            // falls inside recA section 0 [0,200], so the edge index
            // picks it up under parentSlot="rec-multi-A@0".
            RenderSessionState.PutAnchorForTesting(new AnchorCorrection(
                recA.RecordingId, 0, AnchorSide.Start, 0.0, seedA, AnchorSource.LiveSeparation));
            RenderSessionState.PutAnchorForTesting(new AnchorCorrection(
                recB.RecordingId, 0, AnchorSide.End, 100.0, seedB0, AnchorSource.LiveSeparation));

            var tree = new RecordingTree { Id = "t-multi-section" };
            tree.Recordings[recA.RecordingId] = recA;
            tree.Recordings[recB.RecordingId] = recB;
            tree.Recordings[recC.RecordingId] = recC;

            // bp_AB at UT 150 lands inside recB.1; this is the path that
            // anchors recB.1 from recA's seed.
            var bp_AB = new BranchPoint
            {
                Id = "bp-AB", UT = 150.0, Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { recA.RecordingId },
                ChildRecordingIds = new List<string> { recB.RecordingId },
            };
            // bp_BC at UT 200 sits at the boundary between recB.1 [100,200]
            // and recC.0 [200,300]; both inclusive-end last-section ranges
            // contain UT=200 per FindTrackSectionForUT semantics. The
            // edge requires the recB.1 anchor to fire.
            var bp_BC = new BranchPoint
            {
                Id = "bp-BC", UT = 200.0, Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { recB.RecordingId },
                ChildRecordingIds = new List<string> { recC.RecordingId },
            };
            // Reversed list order — bp_BC (deepest) first.
            tree.BranchPoints.Add(bp_BC);
            tree.BranchPoints.Add(bp_AB);

            // recordings iterated [recB, recC, recA] so recB enqueues
            // before recA writes recB.1 — the exact ordering that burnt
            // bp_BC with ε=0 under the per-recording worklist.
            AnchorPropagator.Run(
                new ReFlySessionMarker { SessionId = "multi-sect", TreeId = tree.Id },
                new[] { recB, recC, recA }, new[] { tree }, surfaceLookup: null);

            // Post-fix: recB.1 inherits seedA via bp_AB; recC inherits
            // recB.1's ε via bp_BC. Both are non-zero (= seedA), proving
            // the edge wasn't burnt and recovered after the slot's
            // anchor landed.
            Assert.True(RenderSessionState.TryLookup(recB.RecordingId, 1, AnchorSide.Start, out AnchorCorrection acB1));
            Assert.True(RenderSessionState.TryLookup(recC.RecordingId, 0, AnchorSide.Start, out AnchorCorrection acC));
            // No spline cached → §9.1 helper returns identity, ε flows unchanged.
            Assert.Equal(seedA.x, acB1.Epsilon.x, 3);
            Assert.Equal(seedA.x, acC.Epsilon.x, 3);
        }
    }
}
