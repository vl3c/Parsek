using System;
using System.Collections.Generic;
using Parsek;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Headless coverage for the map / KSC recorded-anchor pose wrapper
    /// (<see cref="RecordedRelativeAnchorPoseResolver"/>). The wrapper was
    /// symbol-dark: its guards, its store-backed context build, and its
    /// body-pose reads had no test of any kind, while the ghost-inside-the-
    /// planet failure mode (reading a RELATIVE frame's anchor-local METRE
    /// offsets as body-fixed lat/lon/alt) lives exactly here.
    ///
    /// The suite drives the real entry point
    /// (<c>TryResolveSectionAnchorPose</c>) against recordings committed to
    /// <see cref="RecordingStore"/>, with the Unity body read replaced through
    /// the <see cref="IAnchorBodyPoseSource"/> seam by a deterministic
    /// spherical stand-in.
    /// </summary>
    [Collection("Sequential")]
    public class RecordedRelativeAnchorPoseResolverTests : IDisposable
    {
        private const double KerbinRadius = 600000.0;
        private const string BodyName = "Kerbin";

        private readonly List<string> logLines = new List<string>();
        private readonly SphericalBodyPoseSource bodySource = new SphericalBodyPoseSource();

        public RecordedRelativeAnchorPoseResolverTests()
        {
            RecordingStore.ResetForTesting();
            RelativeAnchorResolver.ResetForTesting();
            RecordedRelativeAnchorPoseResolver.BodyPoseSourceForTesting = bodySource;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            RecordedRelativeAnchorPoseResolver.ResetForTesting();
            RelativeAnchorResolver.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ==================================================================
        // Sample selection
        // ==================================================================

        [Fact]
        public void SampleSelection_PicksBracketingPair_ForInRangeUT()
        {
            // Anchor samples sit on the +x ray at altitudes 1000 / 3000 / 9000,
            // so the world position is (600000 + alt, 0, 0) and the chosen
            // bracket is readable straight off the x coordinate. UT 15 must
            // bracket [10, 20] -> 600000 + (3000 + 9000) / 2. The earlier
            // bracket [0, 10] would answer 602000, a 4 km error.
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeAbsoluteAnchor(
                "anchor-rec",
                tree.Id,
                new[] { (0.0, 1000.0), (10.0, 3000.0), (20.0, 9000.0) });
            Recording focus = MakeRelativeFocus("focus-rec", tree.Id, anchor.RecordingId, Vector3d.zero);
            Commit(tree, anchor, focus);

            Assert.True(Resolve(focus, 15.0, out AnchorPose pose, out RelativeAnchorResolveFailure failure), failure.Reason);
            Assert.Equal(606000.0, pose.WorldPos.x, 3);
            Assert.Equal(0.0, pose.WorldPos.y, 3);
            Assert.Equal(0.0, pose.WorldPos.z, 3);
        }

        [Theory]
        [InlineData(0.0, 601000.0)]
        [InlineData(10.0, 603000.0)]
        [InlineData(20.0, 609000.0)]
        public void SampleSelection_ExactSampleUT_ReturnsThatSample(double ut, double expectedX)
        {
            // Endpoint behaviour: the first sample UT, an interior sample UT,
            // and the final sample UT each resolve to that sample's own world
            // position (no off-by-one into the neighbouring bracket, no
            // rejection at the inclusive range edges).
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeAbsoluteAnchor(
                "anchor-rec",
                tree.Id,
                new[] { (0.0, 1000.0), (10.0, 3000.0), (20.0, 9000.0) });
            Recording focus = MakeRelativeFocus("focus-rec", tree.Id, anchor.RecordingId, Vector3d.zero);
            Commit(tree, anchor, focus);

            Assert.True(Resolve(focus, ut, out AnchorPose pose, out RelativeAnchorResolveFailure failure), failure.Reason);
            Assert.Equal(expectedX, pose.WorldPos.x, 3);
        }

        [Fact]
        public void Interpolation_PositionIsLinearAndMonotoneInT()
        {
            // Position interpolates linearly in world space (Vector3d.Lerp over
            // the two resolved world positions) and is strictly monotone across
            // the bracket. A stuck / clamped / snapped selection reads as a
            // flat or non-monotone run here.
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeAbsoluteAnchor(
                "anchor-rec",
                tree.Id,
                new[] { (10.0, 3000.0), (20.0, 9000.0) });
            Recording focus = MakeRelativeFocus("focus-rec", tree.Id, anchor.RecordingId, Vector3d.zero);
            Commit(tree, anchor, focus);

            double previousX = double.NegativeInfinity;
            for (int step = 0; step <= 10; step++)
            {
                double t = step / 10.0;
                double ut = 10.0 + 10.0 * t;
                Assert.True(Resolve(focus, ut, out AnchorPose pose, out RelativeAnchorResolveFailure failure), failure.Reason);
                Assert.Equal(603000.0 + 6000.0 * t, pose.WorldPos.x, 2);
                Assert.True(pose.WorldPos.x > previousX,
                    $"interpolated x must increase with t: t={t} x={pose.WorldPos.x} previous={previousX}");
                previousX = pose.WorldPos.x;
            }
        }

        [Fact]
        public void Interpolation_RotationSlerpsBetweenSamples_ComposedWithBodyRotation()
        {
            // Recorded rotations are surface-relative; the world rotation the
            // resolver hands back is body.bodyTransform.rotation * srfRelRotation
            // (CLAUDE.md "Rotation / world frame"). Samples 0 deg -> 90 deg about
            // z; at the midpoint the surface-relative part must slerp to 45 deg
            // and the body rotation must be applied on the LEFT. Dropping the
            // body rotation, or multiplying on the right, both fail.
            Quaternion bodyRotation = RotationXDegrees(30.0);
            bodySource.BodyRotation = bodyRotation;

            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeAbsoluteAnchor(
                "anchor-rec",
                tree.Id,
                new[] { (10.0, 3000.0), (20.0, 9000.0) },
                new[] { RotationZDegrees(0.0), RotationZDegrees(90.0) });
            Recording focus = MakeRelativeFocus("focus-rec", tree.Id, anchor.RecordingId, Vector3d.zero);
            Commit(tree, anchor, focus);

            Assert.True(Resolve(focus, 15.0, out AnchorPose pose, out RelativeAnchorResolveFailure failure), failure.Reason);

            Quaternion expected = TrajectoryMath.PureMultiply(bodyRotation, RotationZDegrees(45.0));
            // Tolerance is float-quaternion slerp slop, not a loose bound: the
            // wrong compositions this test guards against are tens of degrees out.
            AssertRotationsClose(expected, pose.WorldRotation, 0.1f);

            // Guard the composition ORDER explicitly: the mirrored product is a
            // different rotation for this pair, so a swapped multiply is caught.
            Quaternion mirrored = TrajectoryMath.PureMultiply(RotationZDegrees(45.0), bodyRotation);
            Assert.True(RotationAngleDegrees(mirrored, pose.WorldRotation) > 1.0,
                "body-rotation composition order must be bodyRotation * srfRelRotation");
        }

        // ==================================================================
        // The RELATIVE-frame contract (ghost-inside-the-planet)
        // ==================================================================

        [Fact]
        public void RelativeSection_ComposesAnchorLocalMetreOffsets_NeverAsLatLonAlt()
        {
            // THE load-bearing assertion. A RELATIVE TrackSection stores
            // anchor-local Cartesian METRE offsets in TrajectoryPoint
            // latitude / longitude / altitude. Resolution must compose
            //     anchorWorldPos + anchor.rotation * localOffset
            // (TrajectoryMath.ApplyRelativeLocalOffset), NOT call a
            // body.GetWorldSurfacePosition-style read on those three fields.
            //
            // Fixture: focus -> mid (RELATIVE, offsets in metres) -> base
            // (ABSOLUTE, 100 km up). The mid recording rides 12 / -3 / 45 m
            // off its base, so the correct pose is ~700 km from the body
            // centre. Read as lat/lon/alt, the very same numbers land at
            // 600045 m from the centre - on the surface, ~100 km BELOW the
            // craft, the classic ghost-inside-the-planet failure.
            var tree = new RecordingTree { Id = "tree" };
            Recording baseRec = MakeAbsoluteAnchor(
                "base-rec",
                tree.Id,
                new[] { (0.0, 100000.0), (20.0, 100000.0) });
            var localOffset = new Vector3d(12.0, -3.0, 45.0);
            Recording mid = MakeRelativeFocus("mid-rec", tree.Id, baseRec.RecordingId, localOffset);
            Recording focus = MakeRelativeFocus("focus-rec", tree.Id, mid.RecordingId, Vector3d.zero);
            Commit(tree, baseRec, mid, focus);

            Assert.True(Resolve(focus, 10.0, out AnchorPose pose, out RelativeAnchorResolveFailure failure), failure.Reason);

            Vector3d baseWorld = SphericalBodyPoseSource.SurfacePosition(0.0, 0.0, 100000.0);
            Vector3d expected = TrajectoryMath.ResolveRelativePlaybackPosition(
                baseWorld,
                Quaternion.identity,
                localOffset.x,
                localOffset.y,
                localOffset.z);
            Assert.True((pose.WorldPos - expected).magnitude < 1e-3,
                $"expected composed anchor-local pose {expected}, got {pose.WorldPos}");

            // And it is emphatically NOT the lat/lon/alt misreading.
            Vector3d misread = SphericalBodyPoseSource.SurfacePosition(
                localOffset.x,
                localOffset.y,
                localOffset.z);
            Assert.True(misread.magnitude < KerbinRadius + 1000.0,
                "fixture sanity: the misread position must sit at the surface datum");
            Assert.True(pose.WorldPos.magnitude > KerbinRadius + 90000.0,
                $"resolved pose must stay ~100 km up, got |p|={pose.WorldPos.magnitude}");
            Assert.True((pose.WorldPos - misread).magnitude > 90000.0,
                "resolved pose must be nowhere near the lat/lon/alt misreading");

            // Mechanical proof that the metre offsets never reached the
            // surface-position read at all: the only body reads are the base
            // recording's own absolute samples.
            Assert.DoesNotContain(bodySource.SurfaceCalls, c =>
                Math.Abs(c.altitude - localOffset.z) < 1e-9);
            Assert.All(bodySource.SurfaceCalls, c => Assert.Equal(100000.0, c.altitude, 9));
        }

        [Fact]
        public void RelativeSection_AnchorRotationRotatesTheLocalOffset()
        {
            // The offset is anchor-LOCAL: rotating the anchor must swing the
            // composed world position with it. A 90 deg z rotation of the
            // anchor maps a +x local offset onto +y. An implementation that
            // added the offset in world space (skipping the rotation) keeps
            // it on +x and fails.
            bodySource.BodyRotation = RotationZDegrees(90.0);

            var tree = new RecordingTree { Id = "tree" };
            Recording baseRec = MakeAbsoluteAnchor(
                "base-rec",
                tree.Id,
                new[] { (0.0, 100000.0), (20.0, 100000.0) });
            var localOffset = new Vector3d(100.0, 0.0, 0.0);
            Recording mid = MakeRelativeFocus("mid-rec", tree.Id, baseRec.RecordingId, localOffset);
            Recording focus = MakeRelativeFocus("focus-rec", tree.Id, mid.RecordingId, Vector3d.zero);
            Commit(tree, baseRec, mid, focus);

            Assert.True(Resolve(focus, 10.0, out AnchorPose pose, out RelativeAnchorResolveFailure failure), failure.Reason);

            Vector3d baseWorld = SphericalBodyPoseSource.SurfacePosition(0.0, 0.0, 100000.0);
            Vector3d delta = pose.WorldPos - baseWorld;
            Assert.Equal(0.0, delta.x, 2);
            Assert.Equal(100.0, delta.y, 2);
            Assert.Equal(0.0, delta.z, 2);
        }

        [Fact]
        public void RelativeSection_InterpolatesOffsetsBetweenFrames()
        {
            // Anchor-local offsets interpolate per component between the two
            // relative frames, exactly like the absolute path interpolates
            // world positions.
            var tree = new RecordingTree { Id = "tree" };
            Recording baseRec = MakeAbsoluteAnchor(
                "base-rec",
                tree.Id,
                new[] { (0.0, 100000.0), (20.0, 100000.0) });
            Recording mid = MakeRelativeRecordingWithFrames(
                "mid-rec",
                tree.Id,
                baseRec.RecordingId,
                new[]
                {
                    (0.0, new Vector3d(0.0, 0.0, 0.0)),
                    (20.0, new Vector3d(200.0, -40.0, 60.0)),
                });
            Recording focus = MakeRelativeFocus("focus-rec", tree.Id, mid.RecordingId, Vector3d.zero);
            Commit(tree, baseRec, mid, focus);

            Assert.True(Resolve(focus, 5.0, out AnchorPose pose, out RelativeAnchorResolveFailure failure), failure.Reason);

            Vector3d baseWorld = SphericalBodyPoseSource.SurfacePosition(0.0, 0.0, 100000.0);
            Vector3d delta = pose.WorldPos - baseWorld;
            Assert.Equal(50.0, delta.x, 2);
            Assert.Equal(-10.0, delta.y, 2);
            Assert.Equal(15.0, delta.z, 2);
        }

        // ==================================================================
        // Out-of-range contract (pinned as observed)
        // ==================================================================

        [Fact]
        public void OutOfRange_FarPastRecordedEnd_FailsWithOutOfRecordedRange()
        {
            // Beyond the terminal clamp window the resolver refuses rather than
            // clamping to a stale pose: false, NoSectionAtUT, and the recorded
            // range reported on the failure for the caller's log.
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeAbsoluteAnchor(
                "anchor-rec",
                tree.Id,
                new[] { (0.0, 1000.0), (20.0, 9000.0) });
            Recording focus = MakeRelativeFocus("focus-rec", tree.Id, anchor.RecordingId, Vector3d.zero, endUT: 200.0);
            Commit(tree, anchor, focus);

            Assert.False(Resolve(focus, 120.0, out AnchorPose pose, out RelativeAnchorResolveFailure failure));
            Assert.Equal("anchor-out-of-recorded-range", failure.Reason);
            Assert.Equal(RelativeAnchorResolveOutcome.NoSectionAtUT, failure.Outcome);
            Assert.Equal(0.0, pose.WorldPos.magnitude, 9);
        }

        [Fact]
        public void OutOfRange_BeforeRecordedStart_FailsWithOutOfRecordedRange()
        {
            // The terminal clamp is an END-side allowance only; a UT before the
            // first recorded sample is a plain refusal.
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeAbsoluteAnchor(
                "anchor-rec",
                tree.Id,
                new[] { (100.0, 1000.0), (120.0, 9000.0) });
            Recording focus = MakeRelativeFocus(
                "focus-rec",
                tree.Id,
                anchor.RecordingId,
                Vector3d.zero,
                startUT: 0.0,
                endUT: 200.0);
            Commit(tree, anchor, focus);

            Assert.False(Resolve(focus, 50.0, out _, out RelativeAnchorResolveFailure failure));
            Assert.Equal("anchor-out-of-recorded-range", failure.Reason);
            Assert.Equal(RelativeAnchorResolveOutcome.NoSectionAtUT, failure.Outcome);
        }

        [Fact]
        public void OutOfRange_WithinOnePhysicsTickPastEnd_ClampsToTerminalSample()
        {
            // PINNED AS-IS, not endorsed: a probe arriving up to one physics
            // tick (headless 0.02 s) plus slop past the anchor's final sample
            // is answered with the TERMINAL sample's pose rather than a
            // refusal (RelativeAnchorResolver.TryFindTerminalClamp). It is a
            // deliberate endpoint-playback allowance; this test exists so any
            // widening of that window is a visible change.
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeAbsoluteAnchor(
                "anchor-rec",
                tree.Id,
                new[] { (0.0, 1000.0), (20.0, 9000.0) });
            Recording focus = MakeRelativeFocus("focus-rec", tree.Id, anchor.RecordingId, Vector3d.zero, endUT: 200.0);
            Commit(tree, anchor, focus);

            Assert.True(Resolve(focus, 20.01, out AnchorPose clamped, out RelativeAnchorResolveFailure failure), failure.Reason);
            Assert.Equal(609000.0, clamped.WorldPos.x, 3);

            // One tick further out is past the window and refuses.
            Assert.False(Resolve(focus, 20.5, out _, out RelativeAnchorResolveFailure lateFailure));
            Assert.Equal("anchor-out-of-recorded-range", lateFailure.Reason);
        }

        // ==================================================================
        // Wrapper guards + store-backed context build
        // ==================================================================

        [Fact]
        public void Guard_NullFocusRecording_FailsPrecondition()
        {
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                anchorRecordingId = "anchor-rec",
            };

            Assert.False(RecordedRelativeAnchorPoseResolver.TryResolveSectionAnchorPose(
                null,
                section,
                1.0,
                out _,
                out RelativeAnchorResolveFailure failure));
            Assert.Equal(RelativeAnchorResolveOutcome.PreconditionFailed, failure.Outcome);
            Assert.Equal("anchor-recording-null", failure.Reason);
        }

        [Fact]
        public void Guard_NonRelativeSection_IsRejected()
        {
            // The wrapper resolves RELATIVE sections only; an Absolute section
            // arriving here would otherwise be composed against an anchor it
            // was never recorded against.
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeAbsoluteAnchor("anchor-rec", tree.Id, new[] { (0.0, 1000.0), (20.0, 9000.0) });
            Recording focus = MakeRelativeFocus("focus-rec", tree.Id, anchor.RecordingId, Vector3d.zero);
            Commit(tree, anchor, focus);

            TrackSection absolute = anchor.TrackSections[0];
            Assert.False(RecordedRelativeAnchorPoseResolver.TryResolveSectionAnchorPose(
                focus,
                absolute,
                10.0,
                out _,
                out RelativeAnchorResolveFailure failure));
            Assert.Equal(RelativeAnchorResolveOutcome.PreconditionFailed, failure.Outcome);
            Assert.Equal("anchor-section-frame-unknown", failure.Reason);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Guard_MissingAnchorRecordingId_IsRejected(string anchorRecordingId)
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording focus = MakeRelativeFocus("focus-rec", tree.Id, anchorRecordingId, Vector3d.zero);
            Commit(tree, focus);

            Assert.False(Resolve(focus, 5.0, out _, out RelativeAnchorResolveFailure failure));
            Assert.Equal(RelativeAnchorResolveOutcome.PreconditionFailed, failure.Outcome);
            Assert.Equal("anchor-recording-id-missing", failure.Reason);
        }

        [Fact]
        public void Guard_FocusRecordingNotInAnyTree_FailsContextBuild()
        {
            // The context is built from RecordingStore; a focus recording that
            // belongs to no committed or pending tree cannot resolve.
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeAbsoluteAnchor("anchor-rec", tree.Id, new[] { (0.0, 1000.0), (20.0, 9000.0) });
            Recording focus = MakeRelativeFocus("focus-rec", tree.Id, anchor.RecordingId, Vector3d.zero);
            tree.AddOrReplaceRecording(anchor);
            tree.AddOrReplaceRecording(focus);
            // Deliberately NOT committed.

            Assert.False(Resolve(focus, 10.0, out _, out RelativeAnchorResolveFailure failure));
            Assert.Equal(RelativeAnchorResolveOutcome.Other, failure.Outcome);
            Assert.Equal("focus-tree-missing", failure.Reason);
        }

        [Fact]
        public void Guard_UnknownAnchorRecordingId_IsReported()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording focus = MakeRelativeFocus("focus-rec", tree.Id, "no-such-anchor", Vector3d.zero);
            Commit(tree, focus);

            Assert.False(Resolve(focus, 5.0, out _, out RelativeAnchorResolveFailure failure));
            Assert.True(failure.HasFailure);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]")
                && l.Contains("focusRecordingId=focus-rec")
                && l.Contains("anchorRecordingId=no-such-anchor"));
        }

        [Fact]
        public void BodyPoseSource_UnknownBody_YieldsUnresolvedAbsolutePosition()
        {
            // The seam's negative path is the old `body == null` branch: a NaN
            // world position, which the resolver must reject rather than
            // propagate into a ghost transform.
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeAbsoluteAnchor("anchor-rec", tree.Id, new[] { (0.0, 1000.0), (20.0, 9000.0) });
            foreach (TrackSection section in anchor.TrackSections)
            {
                for (int i = 0; i < section.frames.Count; i++)
                {
                    TrajectoryPoint p = section.frames[i];
                    p.bodyName = "Gilly-that-is-not-loaded";
                    section.frames[i] = p;
                }
            }
            Recording focus = MakeRelativeFocus("focus-rec", tree.Id, anchor.RecordingId, Vector3d.zero);
            Commit(tree, anchor, focus);

            Assert.False(Resolve(focus, 10.0, out _, out RelativeAnchorResolveFailure failure));
            Assert.Equal("absolute-position-unresolved", failure.Reason);
        }

        // ==================================================================
        // TryFindFocusRecording
        // ==================================================================

        [Fact]
        public void TryFindFocusRecording_ReturnsTheRecordingItself_WhenTrajectoryIsARecording()
        {
            Recording focus = MakeRelativeFocus("focus-rec", "tree", "anchor-rec", Vector3d.zero);

            Assert.True(RecordedRelativeAnchorPoseResolver.TryFindFocusRecording(focus, out Recording found));
            Assert.Same(focus, found);
        }

        [Fact]
        public void TryFindFocusRecording_ResolvesById_FromCommittedTree()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording focus = MakeRelativeFocus("focus-rec", tree.Id, "anchor-rec", Vector3d.zero);
            Commit(tree, focus);

            Assert.True(RecordedRelativeAnchorPoseResolver.TryFindFocusRecording(
                new MockTrajectory { RecordingId = "focus-rec" },
                out Recording found));
            Assert.Same(focus, found);
        }

        [Fact]
        public void TryFindFocusRecording_ReturnsFalse_ForUnknownIdAndNullTrajectory()
        {
            Assert.False(RecordedRelativeAnchorPoseResolver.TryFindFocusRecording(
                new MockTrajectory { RecordingId = "nope" },
                out Recording unknown));
            Assert.Null(unknown);

            Assert.False(RecordedRelativeAnchorPoseResolver.TryFindFocusRecording(null, out Recording none));
            Assert.Null(none);
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        private static bool Resolve(
            Recording focus,
            double ut,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            return RecordedRelativeAnchorPoseResolver.TryResolveSectionAnchorPose(
                focus,
                focus.TrackSections[0],
                ut,
                out pose,
                out failure);
        }

        private static void Commit(RecordingTree tree, params Recording[] recordings)
        {
            foreach (Recording rec in recordings)
                tree.AddOrReplaceRecording(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        /// <summary>
        /// Absolute anchor recording whose samples all sit on the +x ray
        /// (lat 0 / lon 0), so their world position is (600000 + alt, 0, 0).
        /// </summary>
        private static Recording MakeAbsoluteAnchor(
            string recordingId,
            string treeId,
            (double ut, double altitude)[] samples,
            Quaternion[] rotations = null)
        {
            var rec = NewRecording(recordingId, treeId);
            var frames = new List<TrajectoryPoint>();
            for (int i = 0; i < samples.Length; i++)
            {
                frames.Add(new TrajectoryPoint
                {
                    ut = samples[i].ut,
                    latitude = 0.0,
                    longitude = 0.0,
                    altitude = samples[i].altitude,
                    bodyName = BodyName,
                    rotation = rotations != null ? rotations[i] : Quaternion.identity,
                });
            }

            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = samples[0].ut,
                endUT = samples[samples.Length - 1].ut,
                frames = frames,
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            });
            return rec;
        }

        private static Recording MakeRelativeFocus(
            string recordingId,
            string treeId,
            string anchorRecordingId,
            Vector3d localOffset,
            double startUT = 0.0,
            double endUT = 20.0)
        {
            return MakeRelativeRecordingWithFrames(
                recordingId,
                treeId,
                anchorRecordingId,
                new[] { (startUT, localOffset), (endUT, localOffset) });
        }

        /// <summary>
        /// RELATIVE recording. The frames' latitude / longitude / altitude
        /// fields carry anchor-local Cartesian METRE offsets - the misleading
        /// field names are the contract, not a fixture shortcut.
        /// </summary>
        private static Recording MakeRelativeRecordingWithFrames(
            string recordingId,
            string treeId,
            string anchorRecordingId,
            (double ut, Vector3d localOffset)[] samples)
        {
            var rec = NewRecording(recordingId, treeId);
            var frames = new List<TrajectoryPoint>();
            foreach ((double ut, Vector3d localOffset) sample in samples)
            {
                frames.Add(new TrajectoryPoint
                {
                    ut = sample.ut,
                    latitude = sample.localOffset.x,
                    longitude = sample.localOffset.y,
                    altitude = sample.localOffset.z,
                    bodyName = BodyName,
                    rotation = Quaternion.identity,
                });
            }

            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = samples[0].ut,
                endUT = samples[samples.Length - 1].ut,
                anchorRecordingId = anchorRecordingId,
                frames = frames,
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            });
            return rec;
        }

        private static Recording NewRecording(string recordingId, string treeId)
        {
            return new Recording
            {
                RecordingId = recordingId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                TreeId = treeId,
                VesselName = recordingId,
            };
        }

        private static Quaternion RotationZDegrees(double degrees)
        {
            double half = degrees * Math.PI / 360.0;
            return new Quaternion(0f, 0f, (float)Math.Sin(half), (float)Math.Cos(half));
        }

        private static Quaternion RotationXDegrees(double degrees)
        {
            double half = degrees * Math.PI / 360.0;
            return new Quaternion((float)Math.Sin(half), 0f, 0f, (float)Math.Cos(half));
        }

        private static double RotationAngleDegrees(Quaternion a, Quaternion b)
        {
            double dot = Math.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w);
            dot = Math.Min(1.0, dot);
            return 2.0 * Math.Acos(dot) * 180.0 / Math.PI;
        }

        private static void AssertRotationsClose(Quaternion expected, Quaternion actual, float toleranceDegrees)
        {
            double angle = RotationAngleDegrees(expected, actual);
            Assert.True(angle <= toleranceDegrees,
                $"expected rotation {expected}, got {actual} (off by {angle} deg)");
        }

        /// <summary>
        /// Deterministic stand-in for the FlightGlobals body read: a sphere of
        /// Kerbin's radius centred on the origin, y-up, with an injectable
        /// body rotation. Records every surface-position query so a test can
        /// prove which recorded fields reached the geodetic read.
        /// </summary>
        private sealed class SphericalBodyPoseSource : IAnchorBodyPoseSource
        {
            internal Quaternion BodyRotation = Quaternion.identity;

            internal readonly List<(string bodyName, double latitude, double longitude, double altitude)> SurfaceCalls =
                new List<(string, double, double, double)>();

            public bool TryGetSurfaceWorldPosition(
                string bodyName,
                double latitude,
                double longitude,
                double altitude,
                out Vector3d worldPos)
            {
                worldPos = default;
                if (!string.Equals(bodyName, BodyName, StringComparison.Ordinal))
                    return false;

                SurfaceCalls.Add((bodyName, latitude, longitude, altitude));
                worldPos = SurfacePosition(latitude, longitude, altitude);
                return true;
            }

            public bool TryGetBodyWorldRotation(string bodyName, out Quaternion rotation)
            {
                rotation = BodyRotation;
                return string.Equals(bodyName, BodyName, StringComparison.Ordinal);
            }

            internal static Vector3d SurfacePosition(double latitude, double longitude, double altitude)
            {
                double latRad = latitude * Math.PI / 180.0;
                double lonRad = longitude * Math.PI / 180.0;
                double r = KerbinRadius + altitude;
                return new Vector3d(
                    r * Math.Cos(latRad) * Math.Cos(lonRad),
                    r * Math.Sin(latRad),
                    r * Math.Cos(latRad) * Math.Sin(lonRad));
            }
        }
    }
}
