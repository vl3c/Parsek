using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 6 tests for <see cref="AnchorCandidateBuilder"/> (design doc
    /// §17.3.1, §18 Phase 6, §7.2 — §7.10). Touches static state
    /// (<see cref="SectionAnnotationStore"/>, <see cref="ParsekLog"/>) so
    /// runs in the Sequential collection.
    /// </summary>
    [Collection("Sequential")]
    public class AnchorCandidateBuilderTests : IDisposable
    {
        public AnchorCandidateBuilderTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            SectionAnnotationStore.ResetForTesting();
            AnchorCandidateBuilder.ResetForTesting();
            // Phase 6 flag default: on. Tests that need the flag off
            // override locally.
            AnchorCandidateBuilder.UseAnchorTaxonomyOverrideForTesting = true;
        }

        public void Dispose()
        {
            SectionAnnotationStore.ResetForTesting();
            AnchorCandidateBuilder.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // --- fixture helpers -----------------------------------------------

        private static TrajectoryPoint MakePoint(double ut, string body = "Kerbin")
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = 0.0,
                longitude = 0.0,
                altitude = 70.0,
                bodyName = body,
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            };
        }

        private static TrackSection MakeSection(
            ReferenceFrame frame, SegmentEnvironment env,
            double startUT, double endUT, string body = "Kerbin")
        {
            var s = new TrackSection
            {
                referenceFrame = frame,
                environment = env,
                startUT = startUT,
                endUT = endUT,
                anchorVesselId = frame == ReferenceFrame.Relative ? 100u : 0u,
                frames = new List<TrajectoryPoint>
                {
                    MakePoint(startUT, body),
                    MakePoint(endUT, body),
                },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            };
            return s;
        }

        private static Recording MakeRecording(string id, params TrackSection[] sections)
        {
            var rec = new Recording { RecordingId = id, VesselName = "test-" + id };
            for (int i = 0; i < sections.Length; i++)
                rec.TrackSections.Add(sections[i]);
            return rec;
        }

        // --- §7.2 Dock / Merge candidate ----------------------------------

        [Fact]
        public void EmitsDockMergeCandidate_AtBranchPointUT()
        {
            // What makes it fail: a Dock BP at UT t_m must produce candidates
            // on both sides; if either side is missed the §6.4 lerp can't
            // bracket the segment.
            const double bpUT = 50.0;
            var rec = MakeRecording("rec-merged",
                MakeSection(ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic, 0, 100));
            var tree = new RecordingTree { Id = "t-dock" };
            tree.Recordings[rec.RecordingId] = rec;
            var bp = new BranchPoint
            {
                Id = "dock-bp",
                UT = bpUT,
                Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { rec.RecordingId, "other-parent" },
                ChildRecordingIds = new List<string> { rec.RecordingId },
            };
            tree.BranchPoints.Add(bp);

            AnchorCandidateBuilder.BuildAndStorePerSection(rec, tree);

            Assert.True(SectionAnnotationStore.TryGetAnchorCandidates(rec.RecordingId, 0, out var arr));
            // Both sides should be present at the same UT.
            Assert.Contains(arr, c => c.Source == AnchorSource.DockOrMerge && c.Side == AnchorSide.Start && c.UT == bpUT);
            Assert.Contains(arr, c => c.Source == AnchorSource.DockOrMerge && c.Side == AnchorSide.End && c.UT == bpUT);
        }

        // --- §7.3 Split (Undock/EVA/JointBreak) ---------------------------

        [Fact]
        public void EmitsSplitCandidate_OnUndockBranchPoint()
        {
            // What makes it fail: split events still need a candidate so the
            // child segment's ε starts from the parent's End ε. They alias
            // to AnchorSource.DockOrMerge (Phase 6 risk #1) but the side
            // contract (Start for child, End for parent) is the same.
            const double bpUT = 30.0;
            var parent = MakeRecording("rec-parent",
                MakeSection(ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic, 0, 100));
            var child = MakeRecording("rec-child",
                MakeSection(ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic, 30, 200));

            var tree = new RecordingTree { Id = "t-split" };
            tree.Recordings[parent.RecordingId] = parent;
            tree.Recordings[child.RecordingId] = child;
            var bp = new BranchPoint
            {
                Id = "undock-bp",
                UT = bpUT,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { parent.RecordingId },
                ChildRecordingIds = new List<string> { child.RecordingId },
            };
            tree.BranchPoints.Add(bp);

            AnchorCandidateBuilder.BuildAndStorePerSection(parent, tree);
            AnchorCandidateBuilder.BuildAndStorePerSection(child, tree);

            Assert.True(SectionAnnotationStore.TryGetAnchorCandidates(parent.RecordingId, 0, out var parentArr));
            Assert.Contains(parentArr, c => c.Side == AnchorSide.End && c.UT == bpUT && c.Source == AnchorSource.DockOrMerge);

            Assert.True(SectionAnnotationStore.TryGetAnchorCandidates(child.RecordingId, 0, out var childArr));
            Assert.Contains(childArr, c => c.Side == AnchorSide.Start && c.UT == bpUT && c.Source == AnchorSource.DockOrMerge);
        }

        // --- §7.4 RELATIVE boundary --------------------------------------

        [Fact]
        public void EmitsRelativeBoundaryCandidate_OnAbsoluteSideOnly()
        {
            // ABSOLUTE -> RELATIVE: candidate on ABSOLUTE side End. The
            // RELATIVE side must NOT receive a candidate (it's exact via
            // the resolver — a candidate there would let the §7.11
            // resolver overwrite an exact value with a smoothed ε).
            var rec = MakeRecording("rec-rel-bound",
                MakeSection(ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic, 0, 50),
                MakeSection(ReferenceFrame.Relative, SegmentEnvironment.SurfaceMobile, 50, 100));
            AnchorCandidateBuilder.BuildAndStorePerSection(rec, tree: null);

            // Section 0 (ABSOLUTE side) has the End-side RELATIVE-boundary
            // candidate.
            Assert.True(SectionAnnotationStore.TryGetAnchorCandidates(rec.RecordingId, 0, out var absArr));
            Assert.Contains(absArr, c => c.Source == AnchorSource.RelativeBoundary && c.Side == AnchorSide.End && c.UT == 50.0);

            // Section 1 (RELATIVE side) only has the SurfaceContinuous
            // marker (because the section is SurfaceMobile); no
            // RelativeBoundary candidate on the RELATIVE side.
            if (SectionAnnotationStore.TryGetAnchorCandidates(rec.RecordingId, 1, out var relArr))
            {
                Assert.DoesNotContain(relArr, c => c.Source == AnchorSource.RelativeBoundary);
            }
        }

        [Fact]
        public void EmitsRelativeBoundaryCandidate_StartSide_OnRelativeToAbsoluteBoundary()
        {
            // RELATIVE -> ABSOLUTE: candidate on ABSOLUTE side Start.
            var rec = MakeRecording("rec-rel-to-abs",
                MakeSection(ReferenceFrame.Relative, SegmentEnvironment.SurfaceMobile, 0, 50),
                MakeSection(ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic, 50, 100));
            AnchorCandidateBuilder.BuildAndStorePerSection(rec, tree: null);

            Assert.True(SectionAnnotationStore.TryGetAnchorCandidates(rec.RecordingId, 1, out var absArr));
            Assert.Contains(absArr, c => c.Source == AnchorSource.RelativeBoundary && c.Side == AnchorSide.Start && c.UT == 50.0);
        }

        // --- §7.5 / §7.6 OrbitalCheckpoint + SOI -------------------------

        [Fact]
        public void EmitsOrbitalCheckpointCandidate_OnCheckpointBoundary()
        {
            // What makes it fail: the §7.5 candidate must land on the
            // ABSOLUTE side; the analytical Kepler side does not need ε.
            var rec = MakeRecording("rec-orb",
                MakeSection(ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic, 0, 50),
                MakeSection(ReferenceFrame.OrbitalCheckpoint, SegmentEnvironment.ExoBallistic, 50, 100));
            AnchorCandidateBuilder.BuildAndStorePerSection(rec, tree: null);

            Assert.True(SectionAnnotationStore.TryGetAnchorCandidates(rec.RecordingId, 0, out var arr));
            Assert.Contains(arr, c => c.Source == AnchorSource.OrbitalCheckpoint && c.Side == AnchorSide.End && c.UT == 50.0);
        }

        [Fact]
        public void EmitsSoiTransitionCandidate_OnBodyChangeAtCheckpoint()
        {
            // SOI is a §7.6 specialization of §7.5: when the body name
            // changes across the checkpoint boundary, the source flips to
            // SoiTransition (priority unchanged but log diagnostics
            // distinguish them).
            var rec = MakeRecording("rec-soi",
                MakeSection(ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic, 0, 50, body: "Kerbin"),
                MakeSection(ReferenceFrame.OrbitalCheckpoint, SegmentEnvironment.ExoBallistic, 50, 100, body: "Mun"));
            AnchorCandidateBuilder.BuildAndStorePerSection(rec, tree: null);

            Assert.True(SectionAnnotationStore.TryGetAnchorCandidates(rec.RecordingId, 0, out var arr));
            Assert.Contains(arr, c => c.Source == AnchorSource.SoiTransition && c.Side == AnchorSide.End);
            Assert.DoesNotContain(arr, c => c.Source == AnchorSource.OrbitalCheckpoint);
        }

        // --- §7.9 SurfaceContinuous marker --------------------------------

        [Fact]
        public void EmitsSurfaceContinuousMarker_OnSurfaceMobileSection()
        {
            // What makes it fail: forgetting the §7.9 marker leaves Phase 7
            // unable to identify the section that needs the per-frame
            // raycast.
            var rec = MakeRecording("rec-surf",
                MakeSection(ReferenceFrame.Absolute, SegmentEnvironment.SurfaceMobile, 0, 100));
            AnchorCandidateBuilder.BuildAndStorePerSection(rec, tree: null);

            Assert.True(SectionAnnotationStore.TryGetAnchorCandidates(rec.RecordingId, 0, out var arr));
            Assert.Contains(arr, c => c.Source == AnchorSource.SurfaceContinuous && c.Side == AnchorSide.Start);
        }

        // --- §7.10 Loop marker --------------------------------------------

        [Fact]
        public void EmitsLoopMarker_OnRecordingWithLoopAnchorPid()
        {
            var rec = MakeRecording("rec-loop",
                MakeSection(ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic, 0, 100));
            rec.LoopIntervalSeconds = 60.0;
            rec.LoopAnchorVesselId = 1234u;

            AnchorCandidateBuilder.BuildAndStorePerSection(rec, tree: null);

            Assert.True(SectionAnnotationStore.TryGetAnchorCandidates(rec.RecordingId, 0, out var arr));
            Assert.Contains(arr, c => c.Source == AnchorSource.Loop && c.Side == AnchorSide.Start);
        }

        [Fact]
        public void NoLoopMarker_WhenLoopIntervalUnset()
        {
            // What makes it fail: emitting the marker for non-looping
            // recordings would feed phantom Loop candidates into the
            // resolver and let them outrank OrbitalCheckpoint candidates
            // that should win.
            var rec = MakeRecording("rec-no-loop",
                MakeSection(ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic, 0, 100));
            AnchorCandidateBuilder.BuildAndStorePerSection(rec, tree: null);

            if (SectionAnnotationStore.TryGetAnchorCandidates(rec.RecordingId, 0, out var arr))
            {
                Assert.DoesNotContain(arr, c => c.Source == AnchorSource.Loop);
            }
        }

        // --- ordering + flag-off behaviour --------------------------------

        [Fact]
        public void Candidates_AreSortedByUTPerSection()
        {
            // What makes it fail: the §17.3.1 schema persists per-section
            // candidate arrays; downstream readers assume monotonic UT
            // order. A non-monotonic emission would make the array order
            // load-dependent.
            var rec = MakeRecording("rec-sort",
                MakeSection(ReferenceFrame.Absolute, SegmentEnvironment.ExoBallistic, 0, 100));
            // Synthesize multiple emitters by combining a Loop marker + a
            // SurfaceContinuous marker so two candidates exist for the same
            // section. We override one section to be SurfaceMobile so both
            // emitters fire.
            rec.TrackSections[0] = MakeSection(ReferenceFrame.Absolute, SegmentEnvironment.SurfaceMobile, 0, 100);
            rec.LoopIntervalSeconds = 60.0;
            rec.LoopAnchorVesselId = 1u;

            AnchorCandidateBuilder.BuildAndStorePerSection(rec, tree: null);

            Assert.True(SectionAnnotationStore.TryGetAnchorCandidates(rec.RecordingId, 0, out var arr));
            // After SelectWinners' UT-stable sort, candidates should be
            // monotonic by UT.
            for (int i = 1; i < arr.Length; i++)
                Assert.True(arr[i].UT >= arr[i - 1].UT);
        }

        [Fact]
        public void FlagOff_NoCandidatesEmitted()
        {
            // What makes it fail: the rollout-gate must early-out before
            // any work runs. A buggy gate would silently emit candidates
            // even with the flag off, defeating the regression-bisection
            // use case.
            AnchorCandidateBuilder.UseAnchorTaxonomyOverrideForTesting = false;

            var rec = MakeRecording("rec-flag-off",
                MakeSection(ReferenceFrame.Absolute, SegmentEnvironment.SurfaceMobile, 0, 100));
            AnchorCandidateBuilder.BuildAndStorePerSection(rec, tree: null);

            Assert.False(SectionAnnotationStore.TryGetAnchorCandidates(rec.RecordingId, 0, out _));
            Assert.Equal(0, SectionAnnotationStore.GetAnchorCandidateSectionCountForRecording(rec.RecordingId));
        }

        // --- AnchorCandidate bit-pack round-trip --------------------------

        [Fact]
        public void TypeByte_PacksSourceAndSide_Roundtrip()
        {
            // Bit 7 carries the AnchorSide; bits 0..6 carry the source. A
            // bug in either path would lose information across the .pann
            // round-trip.
            foreach (AnchorSource src in Enum.GetValues(typeof(AnchorSource)))
            {
                foreach (AnchorSide side in Enum.GetValues(typeof(AnchorSide)))
                {
                    var c = new AnchorCandidate(123.0, src, side);
                    byte b = c.ToTypeByte();
                    AnchorCandidate.FromTypeByte(b, out var srcOut, out var sideOut);
                    Assert.Equal(src, srcOut);
                    Assert.Equal(side, sideOut);
                }
            }
        }
    }
}
