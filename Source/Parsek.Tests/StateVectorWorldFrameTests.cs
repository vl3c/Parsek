using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure-static tests for <see cref="GhostMapPresence.ResolveStateVectorWorldPositionPure"/>.
    /// Guards the latent #582 / #571 contributor: state-vector ghost paths must not feed
    /// anchor-local XYZ offsets (RELATIVE-frame TrajectoryPoint.lat/lon/alt) into
    /// CelestialBody.GetWorldSurfacePosition. Wrong interpretation places the ghost
    /// roughly at the body surface but at a horizontally meaningless lat/lon, which
    /// the user observes as "ghost icons going inside the planet".
    /// </summary>
    [Collection("Sequential")]
    public class StateVectorWorldFrameTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public StateVectorWorldFrameTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Func<double, double, double, Vector3d> SurfaceLookupReturning(
            Vector3d sentinel)
        {
            return (lat, lon, alt) => sentinel;
        }

        private static TrackSection AbsoluteSection(double startUT, double endUT)
        {
            return new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = endUT,
                anchorVesselId = 0u,
            };
        }

        private static TrackSection RelativeSection(double startUT, double endUT, uint anchorPid)
        {
            return new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = startUT,
                endUT = endUT,
                anchorVesselId = anchorPid,
            };
        }

        private static TrackSection OrbitalCheckpointSection(double startUT, double endUT)
        {
            return new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = startUT,
                endUT = endUT,
                anchorVesselId = 0u,
            };
        }

        // -----------------------------------------------------------------
        // ABSOLUTE branch — preserves the existing GetWorldSurfacePosition lookup.
        // -----------------------------------------------------------------

        [Fact]
        public void Absolute_UsesSurfaceLookup_ReturnsLookupResult()
        {
            var point = new TrajectoryPoint
            {
                ut = 100.0,
                latitude = -0.0972,
                longitude = -74.5575,
                altitude = 67.0,
                bodyName = "Kerbin",
            };
            var sentinel = new Vector3d(123.0, 456.0, 789.0);
            var section = AbsoluteSection(50.0, 150.0);

            var result = GhostMapPresence.ResolveStateVectorWorldPositionPure(
                point,
                section,
                recordingFormatVersion: 6,
                absoluteSurfaceLookup: SurfaceLookupReturning(sentinel),
                anchorFound: false,
                anchorWorldPos: default(Vector3d),
                anchorWorldRot: Quaternion.identity,
                anchorVesselId: 0u);

            Assert.True(result.Resolved);
            Assert.Equal("absolute", result.Branch);
            Assert.Null(result.FailureReason);
            Assert.Equal(0u, result.AnchorPid);
            Assert.Equal(sentinel.x, result.WorldPos.x, 6);
            Assert.Equal(sentinel.y, result.WorldPos.y, 6);
            Assert.Equal(sentinel.z, result.WorldPos.z, 6);
        }

        // -----------------------------------------------------------------
        // RELATIVE branch — must use anchor + canonical helper, NOT surface lookup.
        // -----------------------------------------------------------------

        [Fact]
        public void Relative_FormatV6_UsesAnchorPosPlusRotatedOffset_NotSurfaceLookup()
        {
            // RELATIVE point: lat/lon/alt slots carry the anchor-local XYZ offset.
            var point = new TrajectoryPoint
            {
                ut = 100.0,
                latitude = 5.0,    // anchor-local dx
                longitude = 7.0,   // anchor-local dy
                altitude = 11.0,   // anchor-local dz
                bodyName = "Kerbin",
            };
            var anchorPos = new Vector3d(600000, 50, 600000);
            var anchorRot = Quaternion.identity; // identity ⇒ local == world for V6
            var surfaceSentinel = new Vector3d(999999.0, 999999.0, 999999.0);
            var section = RelativeSection(50.0, 150.0, anchorPid: 42u);

            var result = GhostMapPresence.ResolveStateVectorWorldPositionPure(
                point,
                section,
                recordingFormatVersion: 6,
                absoluteSurfaceLookup: SurfaceLookupReturning(surfaceSentinel),
                anchorFound: true,
                anchorWorldPos: anchorPos,
                anchorWorldRot: anchorRot,
                anchorVesselId: 42u);

            Assert.True(result.Resolved);
            Assert.Equal("relative", result.Branch);
            Assert.Null(result.FailureReason);
            Assert.Equal(42u, result.AnchorPid);

            // V6 contract: world = anchorPos + anchorRot * (dx,dy,dz). With identity rot,
            // result == anchorPos + offset.
            Assert.Equal(anchorPos.x + 5.0, result.WorldPos.x, 4);
            Assert.Equal(anchorPos.y + 7.0, result.WorldPos.y, 4);
            Assert.Equal(anchorPos.z + 11.0, result.WorldPos.z, 4);

            // And critically NOT the surface lookup sentinel.
            Assert.NotEqual(surfaceSentinel.x, result.WorldPos.x);
        }

        [Fact]
        public void Relative_FormatV6_AnchorRotated90Y_OffsetRotatesIntoWorldFrame()
        {
            // Offset (1, 0, 0) in anchor-local with anchor rotated 90° around Y world.
            // Expected world delta: (0, 0, -1) — Unity's Quaternion * Vector3 convention.
            var point = new TrajectoryPoint
            {
                ut = 100.0,
                latitude = 1.0,
                longitude = 0.0,
                altitude = 0.0,
                bodyName = "Kerbin",
            };
            var anchorPos = new Vector3d(0, 0, 0);
            var anchorRot = TrajectoryMath.PureAngleAxis(90f, Vector3.up);
            var section = RelativeSection(50.0, 150.0, anchorPid: 42u);

            var result = GhostMapPresence.ResolveStateVectorWorldPositionPure(
                point,
                section,
                recordingFormatVersion: 6,
                absoluteSurfaceLookup: SurfaceLookupReturning(new Vector3d(99, 99, 99)),
                anchorFound: true,
                anchorWorldPos: anchorPos,
                anchorWorldRot: anchorRot,
                anchorVesselId: 42u);

            Assert.True(result.Resolved);
            Assert.Equal("relative", result.Branch);

            // Cross-check against the canonical helper (reuse the same contract).
            Vector3d expected = TrajectoryMath.ResolveRelativePlaybackPosition(
                anchorPos, anchorRot, 1.0, 0.0, 0.0, recordingFormatVersion: 6);
            Assert.Equal(expected.x, result.WorldPos.x, 3);
            Assert.Equal(expected.y, result.WorldPos.y, 3);
            Assert.Equal(expected.z, result.WorldPos.z, 3);
        }

        [Fact]
        public void Relative_LegacyFormatV5_UsesWorldOffsetContract()
        {
            // V5 legacy: offset is world-space, anchor rotation ignored.
            var point = new TrajectoryPoint
            {
                ut = 100.0,
                latitude = 5.0,
                longitude = 7.0,
                altitude = 11.0,
                bodyName = "Kerbin",
            };
            var anchorPos = new Vector3d(100, 200, 300);
            var anchorRot = TrajectoryMath.PureAngleAxis(45f, Vector3.right); // should be ignored
            var section = RelativeSection(50.0, 150.0, anchorPid: 42u);

            var result = GhostMapPresence.ResolveStateVectorWorldPositionPure(
                point,
                section,
                recordingFormatVersion: 5, // legacy
                absoluteSurfaceLookup: SurfaceLookupReturning(new Vector3d(99, 99, 99)),
                anchorFound: true,
                anchorWorldPos: anchorPos,
                anchorWorldRot: anchorRot,
                anchorVesselId: 42u);

            Assert.True(result.Resolved);
            Assert.Equal("relative", result.Branch);
            Assert.Equal(105.0, result.WorldPos.x, 4);
            Assert.Equal(207.0, result.WorldPos.y, 4);
            Assert.Equal(311.0, result.WorldPos.z, 4);
        }

        [Fact]
        public void Relative_AnchorNotFound_ReturnsUnresolvedWithReason()
        {
            var point = new TrajectoryPoint
            {
                ut = 100.0,
                latitude = 5.0,
                longitude = 7.0,
                altitude = 11.0,
                bodyName = "Kerbin",
            };
            var section = RelativeSection(50.0, 150.0, anchorPid: 42u);

            var result = GhostMapPresence.ResolveStateVectorWorldPositionPure(
                point,
                section,
                recordingFormatVersion: 6,
                absoluteSurfaceLookup: SurfaceLookupReturning(new Vector3d(99, 99, 99)),
                anchorFound: false,
                anchorWorldPos: default(Vector3d),
                anchorWorldRot: Quaternion.identity,
                anchorVesselId: 42u);

            Assert.False(result.Resolved);
            Assert.Equal("relative", result.Branch);
            Assert.Equal("anchor-not-found", result.FailureReason);
            Assert.Equal(42u, result.AnchorPid);
        }

        // -----------------------------------------------------------------
        // OrbitalCheckpoint branch — refuses (state-vector data should never
        // come from a checkpoint section).
        // -----------------------------------------------------------------

        [Fact]
        public void OrbitalCheckpoint_ReturnsUnresolvedWithReason()
        {
            var point = new TrajectoryPoint
            {
                ut = 100.0,
                latitude = 0.0,
                longitude = 0.0,
                altitude = 0.0,
                bodyName = "Kerbin",
            };
            var section = OrbitalCheckpointSection(50.0, 150.0);

            var result = GhostMapPresence.ResolveStateVectorWorldPositionPure(
                point,
                section,
                recordingFormatVersion: 6,
                absoluteSurfaceLookup: SurfaceLookupReturning(new Vector3d(99, 99, 99)),
                anchorFound: false,
                anchorWorldPos: default(Vector3d),
                anchorWorldRot: Quaternion.identity,
                anchorVesselId: 0u);

            Assert.False(result.Resolved);
            Assert.Equal("orbital-checkpoint", result.Branch);
            Assert.Equal("state-vector-from-orbital-checkpoint", result.FailureReason);
        }

        [Fact]
        public void OrbitalCheckpoint_ExplicitSoiGapRecovery_UsesSurfaceLookup()
        {
            var point = new TrajectoryPoint
            {
                ut = 100.0,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 47481.0,
                bodyName = "Mun",
            };
            var sentinel = new Vector3d(10.0, 20.0, 30.0);
            var section = OrbitalCheckpointSection(50.0, 150.0);

            var result = GhostMapPresence.ResolveStateVectorWorldPositionPure(
                point,
                section,
                recordingFormatVersion: 6,
                absoluteSurfaceLookup: SurfaceLookupReturning(sentinel),
                anchorFound: false,
                anchorWorldPos: default(Vector3d),
                anchorWorldRot: Quaternion.identity,
                anchorVesselId: 0u,
                allowOrbitalCheckpointStateVector: true);

            Assert.True(result.Resolved);
            Assert.Equal("orbital-checkpoint", result.Branch);
            Assert.Null(result.FailureReason);
            Assert.Equal(sentinel.x, result.WorldPos.x, 6);
            Assert.Equal(sentinel.y, result.WorldPos.y, 6);
            Assert.Equal(sentinel.z, result.WorldPos.z, 6);
        }

        // -----------------------------------------------------------------
        // No-section fallback — preserves Absolute interpretation for legacy
        // recordings that have not been split into sections.
        // -----------------------------------------------------------------

        [Fact]
        public void NoSection_FallsBackToAbsoluteSurfaceLookup()
        {
            var point = new TrajectoryPoint
            {
                ut = 100.0,
                latitude = -0.0972,
                longitude = -74.5575,
                altitude = 67.0,
                bodyName = "Kerbin",
            };
            var sentinel = new Vector3d(7, 8, 9);

            var result = GhostMapPresence.ResolveStateVectorWorldPositionPure(
                point,
                section: null,
                recordingFormatVersion: 0,
                absoluteSurfaceLookup: SurfaceLookupReturning(sentinel),
                anchorFound: false,
                anchorWorldPos: default(Vector3d),
                anchorWorldRot: Quaternion.identity,
                anchorVesselId: 0u);

            Assert.True(result.Resolved);
            Assert.Equal("no-section", result.Branch);
            Assert.Equal(sentinel.x, result.WorldPos.x, 6);
            Assert.Equal(sentinel.y, result.WorldPos.y, 6);
            Assert.Equal(sentinel.z, result.WorldPos.z, 6);
        }

        // -----------------------------------------------------------------
        // Discriminator: same data in Absolute vs Relative section yields
        // different world positions. Documents the bug-class this guards.
        // -----------------------------------------------------------------

        [Fact]
        public void Absolute_vs_Relative_SameData_DifferentWorldPositions()
        {
            // Identical point data — the ONLY difference is the section's reference frame.
            var point = new TrajectoryPoint
            {
                ut = 100.0,
                latitude = 5.0,
                longitude = 7.0,
                altitude = 11.0,
                bodyName = "Kerbin",
            };
            var surfaceSentinel = new Vector3d(1000.0, 2000.0, 3000.0);
            var anchorPos = new Vector3d(600000, 50, 600000);

            var absResult = GhostMapPresence.ResolveStateVectorWorldPositionPure(
                point,
                AbsoluteSection(50, 150),
                recordingFormatVersion: 6,
                absoluteSurfaceLookup: SurfaceLookupReturning(surfaceSentinel),
                anchorFound: true,
                anchorWorldPos: anchorPos,
                anchorWorldRot: Quaternion.identity,
                anchorVesselId: 42u);

            var relResult = GhostMapPresence.ResolveStateVectorWorldPositionPure(
                point,
                RelativeSection(50, 150, anchorPid: 42u),
                recordingFormatVersion: 6,
                absoluteSurfaceLookup: SurfaceLookupReturning(surfaceSentinel),
                anchorFound: true,
                anchorWorldPos: anchorPos,
                anchorWorldRot: Quaternion.identity,
                anchorVesselId: 42u);

            Assert.Equal("absolute", absResult.Branch);
            Assert.Equal("relative", relResult.Branch);

            // Absolute: surface lookup
            Assert.Equal(surfaceSentinel.x, absResult.WorldPos.x, 4);

            // Relative: anchor + offset (anchorPos.x = 600000, dx = 5)
            Assert.Equal(anchorPos.x + 5.0, relResult.WorldPos.x, 4);

            // The two world positions must differ — this is exactly the divergence
            // that was silently absent before the fix. Use a generous tolerance to
            // make the assertion explicit (596005 vs 1000 is far apart).
            double dx = Math.Abs(absResult.WorldPos.x - relResult.WorldPos.x);
            Assert.True(dx > 1.0,
                $"Expected divergence between absolute and relative world positions, got dx={dx}");
        }

        // -----------------------------------------------------------------
        // Logging assertions — the production wrappers emit a branch tag on
        // each path. Pure helper does not log; we verify branch metadata
        // round-trips into the StateVectorWorldFrame so the wrapper can log it.
        // -----------------------------------------------------------------

        [Fact]
        public void EveryBranch_PopulatesBranchTagForLogging()
        {
            var point = new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" };
            var lookup = SurfaceLookupReturning(Vector3d.zero);

            string absBranch = GhostMapPresence.ResolveStateVectorWorldPositionPure(
                point, AbsoluteSection(50, 150), 6, lookup,
                false, default(Vector3d), Quaternion.identity, 0u).Branch;

            string relBranch = GhostMapPresence.ResolveStateVectorWorldPositionPure(
                point, RelativeSection(50, 150, 42u), 6, lookup,
                true, new Vector3d(0, 0, 0), Quaternion.identity, 42u).Branch;

            string ocBranch = GhostMapPresence.ResolveStateVectorWorldPositionPure(
                point, OrbitalCheckpointSection(50, 150), 6, lookup,
                false, default(Vector3d), Quaternion.identity, 0u).Branch;

            string nsBranch = GhostMapPresence.ResolveStateVectorWorldPositionPure(
                point, null, 6, lookup,
                false, default(Vector3d), Quaternion.identity, 0u).Branch;

            Assert.Equal("absolute", absBranch);
            Assert.Equal("relative", relBranch);
            Assert.Equal("orbital-checkpoint", ocBranch);
            Assert.Equal("no-section", nsBranch);
        }
    }
}
