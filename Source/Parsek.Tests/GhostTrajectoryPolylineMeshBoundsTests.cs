using Parsek.Display;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for the Bug 1 (looped-mission trajectory lines vanish when zoomed out) pure helper
    /// that touches only headless types: the
    /// <see cref="GhostTrajectoryPolylineRenderer.ComputeScaledSpaceAabb"/> instrumentation AABB and the
    /// <see cref="GhostTrajectoryPolylineRenderer.InfiniteMeshBoundsHalfExtent"/> override-box constant.
    ///
    /// The fix itself (<c>OverrideLineMeshBoundsAfterDraw</c>) and its <c>ShouldOverrideMeshBounds</c>
    /// null-guard both take a Vectrosity <c>VectorLine</c>, whose type lives in
    /// <c>Assembly-CSharp-firstpass</c> - deliberately NOT referenced by this test assembly (the project
    /// keeps tests headless, so any Vectrosity-typed signature is unreachable here). Those branches plus
    /// the runtime mesh-component path + far-zoom frustum pass are covered by the in-game test
    /// (RuntimeTests, MapView category).
    /// </summary>
    public class GhostTrajectoryPolylineMeshBoundsTests
    {
        // --- InfiniteMeshBoundsHalfExtent: the override box dwarfs scaled space ---

        [Fact]
        public void InfiniteMeshBoundsHalfExtent_IsLargeEnoughToNeverCull()
        {
            // The half-extent must dwarf the scaled-space extent of the whole solar system so the mesh
            // AABB always intersects the map / scaled camera frustum (1e9 vs scaled-space radii of a few
            // 1e5). Guards against an accidental edit shrinking the box back into the cull-prone range.
            Assert.Equal(1e9f, GhostTrajectoryPolylineRenderer.InfiniteMeshBoundsHalfExtent);
            Assert.True(GhostTrajectoryPolylineRenderer.InfiniteMeshBoundsHalfExtent > 1e6f);
        }

        // --- ComputeScaledSpaceAabb: pure Vector3 extent math ---

        [Fact]
        public void ComputeScaledSpaceAabb_NullPoints_ReturnsDegenerateBox()
        {
            Bounds b = GhostTrajectoryPolylineRenderer.ComputeScaledSpaceAabb(null, 5);
            Assert.Equal(Vector3.zero, b.center);
            Assert.Equal(Vector3.zero, b.size);
        }

        [Fact]
        public void ComputeScaledSpaceAabb_ZeroCount_ReturnsDegenerateBox()
        {
            var pts = new[] { new Vector3(1f, 2f, 3f), new Vector3(4f, 5f, 6f) };
            Bounds b = GhostTrajectoryPolylineRenderer.ComputeScaledSpaceAabb(pts, 0);
            Assert.Equal(Vector3.zero, b.center);
            Assert.Equal(Vector3.zero, b.size);
        }

        [Fact]
        public void ComputeScaledSpaceAabb_SinglePoint_ZeroSizeBoxAtThatPoint()
        {
            var pts = new[] { new Vector3(10f, -20f, 30f) };
            Bounds b = GhostTrajectoryPolylineRenderer.ComputeScaledSpaceAabb(pts, 1);
            Assert.Equal(new Vector3(10f, -20f, 30f), b.center);
            Assert.Equal(Vector3.zero, b.size);
        }

        [Fact]
        public void ComputeScaledSpaceAabb_MultiplePoints_TightlyBoundsAllPoints()
        {
            var pts = new[]
            {
                new Vector3(1f, 1f, 1f),
                new Vector3(-3f, 5f, 2f),
                new Vector3(4f, -2f, -6f),
            };
            Bounds b = GhostTrajectoryPolylineRenderer.ComputeScaledSpaceAabb(pts, 3);
            Vector3 min = b.min;
            Vector3 max = b.max;
            Assert.Equal(new Vector3(-3f, -2f, -6f), min);
            Assert.Equal(new Vector3(4f, 5f, 2f), max);
            // Every input point must lie inside the computed AABB (manual containment - Bounds.Contains is
            // a Unity ECall that cannot run headless).
            foreach (var p in pts)
            {
                Assert.True(p.x >= min.x && p.x <= max.x, $"AABB x must contain {p}");
                Assert.True(p.y >= min.y && p.y <= max.y, $"AABB y must contain {p}");
                Assert.True(p.z >= min.z && p.z <= max.z, $"AABB z must contain {p}");
            }
        }

        [Fact]
        public void ComputeScaledSpaceAabb_CountClampedToArrayLength()
        {
            // A count larger than the array length must not throw; it clamps to the available points
            // (the third point is ignored).
            var pts = new[] { new Vector3(0f, 0f, 0f), new Vector3(2f, 2f, 2f) };
            Bounds b = GhostTrajectoryPolylineRenderer.ComputeScaledSpaceAabb(pts, 99);
            Assert.Equal(Vector3.zero, b.min);
            Assert.Equal(new Vector3(2f, 2f, 2f), b.max);
        }

        [Fact]
        public void ComputeScaledSpaceAabb_HonoursPartialCount()
        {
            // count=2 must bound only the first two points, not the third outlier.
            var pts = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 1f, 1f),
                new Vector3(100f, 100f, 100f),
            };
            Bounds b = GhostTrajectoryPolylineRenderer.ComputeScaledSpaceAabb(pts, 2);
            Vector3 max = b.max;
            Assert.Equal(Vector3.zero, b.min);
            Assert.Equal(new Vector3(1f, 1f, 1f), max);
            // The third outlier must be outside the partial-count AABB (manual check - Bounds.Contains is
            // a Unity ECall unavailable headless).
            Assert.True(100f > max.x, "the count=2 AABB must exclude the third outlier point");
        }
    }
}
