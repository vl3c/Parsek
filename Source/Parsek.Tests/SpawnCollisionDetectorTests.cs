using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="SpawnCollisionDetector"/> pure static methods.
    /// KSP-runtime methods (CheckOverlapAgainstLoadedVessels, CheckWarningProximity)
    /// are not testable here — they depend on FlightGlobals. The pure methods
    /// they delegate to are fully covered.
    /// </summary>
    [Collection("Sequential")]
    public class SpawnCollisionDetectorTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SpawnCollisionDetectorTests()
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

        // ────────────────────────────────────────────────────────────
        //  ComputeBoundsFromParts
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ComputeBoundsFromParts_SinglePart_CenteredBounds()
        {
            // Single part at (0,0,0) with halfExtent 1.25
            var parts = new List<(Vector3 localPos, float halfExtent)>
            {
                (Vector3.zero, 1.25f)
            };

            Bounds bounds = SpawnCollisionDetector.ComputeBoundsFromParts(parts);

            Assert.Equal(Vector3.zero, bounds.center);
            Assert.Equal(new Vector3(1.25f, 1.25f, 1.25f), bounds.extents);
        }

        [Fact]
        public void ComputeBoundsFromParts_MultiPart_EncloseAll()
        {
            // Parts at (0,0,0), (0,5,0), (0,10,0) with halfExtent 1.25
            var parts = new List<(Vector3 localPos, float halfExtent)>
            {
                (new Vector3(0, 0, 0), 1.25f),
                (new Vector3(0, 5, 0), 1.25f),
                (new Vector3(0, 10, 0), 1.25f)
            };

            Bounds bounds = SpawnCollisionDetector.ComputeBoundsFromParts(parts);

            // Center should be at (0, 5, 0) — midpoint of the range
            Assert.Equal(0.0, (double)bounds.center.x, 4);
            Assert.Equal(5.0, (double)bounds.center.y, 4);
            Assert.Equal(0.0, (double)bounds.center.z, 4);

            // Y extent: from (0-1.25) to (10+1.25) = range 12.5, half = 6.25
            Assert.Equal(1.25, (double)bounds.extents.x, 4);
            Assert.Equal(6.25, (double)bounds.extents.y, 4);
            Assert.Equal(1.25, (double)bounds.extents.z, 4);

            // Verify it encloses the bottom part minus extent
            Assert.True(bounds.min.y <= -1.25f);
            // Verify it encloses the top part plus extent
            Assert.True(bounds.max.y >= 11.25f);
        }

        [Fact]
        public void ComputeBoundsFromParts_EmptyList_ZeroBounds()
        {
            var parts = new List<(Vector3 localPos, float halfExtent)>();

            Bounds bounds = SpawnCollisionDetector.ComputeBoundsFromParts(parts);

            Assert.Equal(Vector3.zero, bounds.center);
            Assert.Equal(Vector3.zero, bounds.size);
        }

        // ────────────────────────────────────────────────────────────
        //  BoundsOverlap
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void BoundsOverlap_Overlapping_ReturnsTrue()
        {
            // Bug caught: coincident bounds must be detected as overlapping
            // Two 2x2x2 bounds at same position
            Bounds a = new Bounds(Vector3.zero, new Vector3(2, 2, 2));
            Bounds b = new Bounds(Vector3.zero, new Vector3(2, 2, 2));
            Vector3d center = new Vector3d(0, 0, 0);

            bool result = SpawnCollisionDetector.BoundsOverlap(a, center, b, center, 0f);

            Assert.True(result);
        }

        [Fact]
        public void BoundsOverlap_Separated_ReturnsFalse()
        {
            // Bug caught: well-separated bounds must not false-positive as overlapping
            // Two 2x2x2 bounds 100m apart on X axis
            Bounds a = new Bounds(Vector3.zero, new Vector3(2, 2, 2));
            Bounds b = new Bounds(Vector3.zero, new Vector3(2, 2, 2));
            Vector3d aCenter = new Vector3d(0, 0, 0);
            Vector3d bCenter = new Vector3d(100, 0, 0);

            bool result = SpawnCollisionDetector.BoundsOverlap(a, aCenter, b, bCenter, 0f);

            Assert.False(result);
        }

        [Fact]
        public void BoundsOverlap_PaddingCausesOverlap()
        {
            // Bug caught: padding parameter must expand effective bounds to catch near-misses
            // Two 2x2x2 bounds, centers 3m apart on X axis
            // Without padding: a.max.x=1, b.min.x=2 → gap of 1m → no overlap
            // With padding=2m: a.max.x=1+2=3, b.min.x=2-2=0 → overlap
            Bounds a = new Bounds(Vector3.zero, new Vector3(2, 2, 2));
            Bounds b = new Bounds(Vector3.zero, new Vector3(2, 2, 2));
            Vector3d aCenter = new Vector3d(0, 0, 0);
            Vector3d bCenter = new Vector3d(3, 0, 0);

            // Without padding: no overlap
            Assert.False(SpawnCollisionDetector.BoundsOverlap(a, aCenter, b, bCenter, 0f));

            // With padding: overlap
            bool result = SpawnCollisionDetector.BoundsOverlap(a, aCenter, b, bCenter, 2f);

            Assert.True(result);
        }

        [Fact]
        public void BoundsOverlap_PaddingNotEnough_ReturnsFalse()
        {
            // Bug caught: padding must not create false overlaps for well-separated bounds
            // Two 2x2x2 bounds, 10m apart on X axis, padding=2m
            // a.max.x = 1+2 = 3, b.min.x = 10-1-2 = 7 → no overlap
            Bounds a = new Bounds(Vector3.zero, new Vector3(2, 2, 2));
            Bounds b = new Bounds(Vector3.zero, new Vector3(2, 2, 2));
            Vector3d aCenter = new Vector3d(0, 0, 0);
            Vector3d bCenter = new Vector3d(10, 0, 0);

            bool result = SpawnCollisionDetector.BoundsOverlap(a, aCenter, b, bCenter, 2f);

            Assert.False(result);
        }

        // ────────────────────────────────────────────────────────────
        //  ComputeVesselBounds
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ComputeVesselBounds_ValidSnapshot_ReturnsBounds()
        {
            // Bug caught: valid PART positions must produce correct enclosing bounds
            var node = new ConfigNode("VESSEL");
            var p1 = node.AddNode("PART");
            p1.AddValue("pos", "0,0,0");
            var p2 = node.AddNode("PART");
            p2.AddValue("pos", "0,5,0");
            var p3 = node.AddNode("PART");
            p3.AddValue("pos", "0,10,0");

            Bounds bounds = SpawnCollisionDetector.ComputeVesselBounds(node);

            // Same as multi-part test: center=(0,5,0), Y extents cover -1.25 to 11.25
            Assert.Equal(0.0, (double)bounds.center.x, 4);
            Assert.Equal(5.0, (double)bounds.center.y, 4);
            Assert.Equal(0.0, (double)bounds.center.z, 4);
            Assert.True(bounds.min.y <= -1.25f);
            Assert.True(bounds.max.y >= 11.25f);
        }

        [Fact]
        public void ComputeVesselBounds_NullSnapshot_ReturnsFallbackBounds()
        {
            // Bug caught: null snapshot must not crash, must return safe fallback bounds
            Bounds bounds = SpawnCollisionDetector.ComputeVesselBounds(null);

            // Fallback: 2m cube at origin
            Assert.Equal(Vector3.zero, bounds.center);
            Assert.Equal(new Vector3(2, 2, 2), bounds.size);
        }

        [Fact]
        public void ComputeVesselBounds_NoParts_ReturnsFallbackBounds()
        {
            // Bug caught: empty vessel (no PART nodes) must return fallback, not crash
            var node = new ConfigNode("VESSEL");
            // No PART subnodes

            Bounds bounds = SpawnCollisionDetector.ComputeVesselBounds(node);

            // Fallback: 2m cube at origin
            Assert.Equal(Vector3.zero, bounds.center);
            Assert.Equal(new Vector3(2, 2, 2), bounds.size);
        }

        // ────────────────────────────────────────────────────────────
        //  ComputeVesselBounds — edge cases
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ComputeVesselBounds_MalformedPos_SkipsBadParts()
        {
            // Bug caught: malformed pos values must be skipped, good parts still produce valid bounds
            var node = new ConfigNode("VESSEL");

            // Good part
            var p1 = node.AddNode("PART");
            p1.AddValue("pos", "0,0,0");

            // Bad part: only two components
            var p2 = node.AddNode("PART");
            p2.AddValue("pos", "1,2");

            // Bad part: non-numeric
            var p3 = node.AddNode("PART");
            p3.AddValue("pos", "abc,def,ghi");

            // Good part
            var p4 = node.AddNode("PART");
            p4.AddValue("pos", "0,5,0");

            Bounds bounds = SpawnCollisionDetector.ComputeVesselBounds(node);

            // Should only use p1 and p4
            Assert.Equal(0.0, (double)bounds.center.x, 4);
            Assert.Equal(2.5, (double)bounds.center.y, 4);
            Assert.Equal(0.0, (double)bounds.center.z, 4);
        }

        [Fact]
        public void ComputeVesselBounds_AllBadPositions_ReturnsFallbackBounds()
        {
            // Bug caught: vessel with only unparseable parts must fallback, not crash or return zero bounds
            var node = new ConfigNode("VESSEL");
            var p1 = node.AddNode("PART");
            p1.AddValue("pos", "not,a,number");
            var p2 = node.AddNode("PART");
            // Missing pos value entirely

            Bounds bounds = SpawnCollisionDetector.ComputeVesselBounds(node);

            Assert.Equal(Vector3.zero, bounds.center);
            Assert.Equal(new Vector3(2, 2, 2), bounds.size);
        }

        // ────────────────────────────────────────────────────────────
        //  Log assertion tests
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ComputeBoundsFromParts_LogsPartCount()
        {
            // Bug caught: part count must be logged for debugging bounds computation
            logLines.Clear();

            var parts = new List<(Vector3 localPos, float halfExtent)>
            {
                (Vector3.zero, 1.25f),
                (new Vector3(0, 5, 0), 1.25f)
            };

            SpawnCollisionDetector.ComputeBoundsFromParts(parts);

            Assert.Contains(logLines, l =>
                l.Contains("[SpawnCollision]") && l.Contains("2 parts"));
        }

        [Fact]
        public void ComputeBoundsFromParts_EmptyList_LogsZeroBounds()
        {
            // Bug caught: empty part list must log a diagnostic (aids debugging "why no collision check")
            logLines.Clear();

            SpawnCollisionDetector.ComputeBoundsFromParts(
                new List<(Vector3 localPos, float halfExtent)>());

            Assert.Contains(logLines, l =>
                l.Contains("[SpawnCollision]") && l.Contains("empty part list"));
        }

        [Fact]
        public void ComputeVesselBounds_NullSnapshot_LogsFallback()
        {
            // Bug caught: null snapshot must produce a diagnostic log (silent fallback hides bugs)
            logLines.Clear();

            SpawnCollisionDetector.ComputeVesselBounds(null);

            Assert.Contains(logLines, l =>
                l.Contains("[SpawnCollision]") && l.Contains("null snapshot"));
        }

        [Fact]
        public void ComputeVesselBounds_NoParts_LogsFallback()
        {
            // Bug caught: VESSEL with no PART nodes must log fallback (empty vessel edge case)
            logLines.Clear();

            SpawnCollisionDetector.ComputeVesselBounds(new ConfigNode("VESSEL"));

            Assert.Contains(logLines, l =>
                l.Contains("[SpawnCollision]") && l.Contains("no PART subnodes"));
        }

        // ────────────────────────────────────────────────────────────
        //  ParsePartPositions
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ParsePartPositions_NormalParts_ReturnsPositions()
        {
            // Bug caught: basic parsing of PART pos values must produce correct Vector3 tuples
            var p1 = new ConfigNode("PART");
            p1.AddValue("pos", "1.5,2.5,3.5");
            var p2 = new ConfigNode("PART");
            p2.AddValue("pos", "-1,0,4.2");

            var result = SpawnCollisionDetector.ParsePartPositions(new[] { p1, p2 });

            Assert.Equal(2, result.Count);
            Assert.Equal(1.5, (double)result[0].localPos.x, 4);
            Assert.Equal(2.5, (double)result[0].localPos.y, 4);
            Assert.Equal(3.5, (double)result[0].localPos.z, 4);
            Assert.Equal(-1.0, (double)result[1].localPos.x, 4);
            Assert.Equal(0.0, (double)result[1].localPos.y, 4);
            Assert.Equal(4.2, (double)result[1].localPos.z, 1);

            // DefaultPartHalfExtent = 1.25
            Assert.Equal(1.25, (double)result[0].halfExtent, 4);
            Assert.Equal(1.25, (double)result[1].halfExtent, 4);
        }

        [Fact]
        public void ParsePartPositions_EmptyArray_ReturnsEmptyList()
        {
            // Bug caught: empty partNodes array must not crash, must return empty list
            var result = SpawnCollisionDetector.ParsePartPositions(new ConfigNode[0]);

            Assert.Empty(result);
        }

        [Fact]
        public void ParsePartPositions_MalformedPos_SkipsBadParts()
        {
            // Bug caught: non-numeric pos values must be skipped without crashing
            var good = new ConfigNode("PART");
            good.AddValue("pos", "1,2,3");
            var bad = new ConfigNode("PART");
            bad.AddValue("pos", "abc,def,ghi");

            var result = SpawnCollisionDetector.ParsePartPositions(new[] { good, bad });

            Assert.Single(result);
            Assert.Equal(1.0, (double)result[0].localPos.x, 4);
        }

        [Fact]
        public void ParsePartPositions_TwoComponentPos_Skipped()
        {
            // Bug caught: pos with only 2 components (missing z) must be skipped
            var bad = new ConfigNode("PART");
            bad.AddValue("pos", "1,2");

            var result = SpawnCollisionDetector.ParsePartPositions(new[] { bad });

            Assert.Empty(result);
        }

        [Fact]
        public void ParsePartPositions_MissingPosField_Skipped()
        {
            // Bug caught: PART node without any pos value must be skipped
            var noPosNode = new ConfigNode("PART");
            noPosNode.AddValue("name", "fuelTank");
            // No "pos" value

            var result = SpawnCollisionDetector.ParsePartPositions(new[] { noPosNode });

            Assert.Empty(result);
        }

        [Fact]
        public void ParsePartPositions_EmptyPosValue_Skipped()
        {
            // Bug caught: empty string pos must be handled by IsNullOrEmpty guard
            var emptyPos = new ConfigNode("PART");
            emptyPos.AddValue("pos", "");

            var result = SpawnCollisionDetector.ParsePartPositions(new[] { emptyPos });

            Assert.Empty(result);
        }

        [Fact]
        public void ParsePartPositions_WhitespaceInComponents_Trimmed()
        {
            // Bug caught: pos values with spaces around components (e.g. from hand-edited
            // config files) must be trimmed before parsing
            var node = new ConfigNode("PART");
            node.AddValue("pos", " 1.0 , 2.0 , 3.0 ");

            var result = SpawnCollisionDetector.ParsePartPositions(new[] { node });

            Assert.Single(result);
            Assert.Equal(1.0, (double)result[0].localPos.x, 4);
            Assert.Equal(2.0, (double)result[0].localPos.y, 4);
            Assert.Equal(3.0, (double)result[0].localPos.z, 4);
        }

        [Fact]
        public void ParsePartPositions_MixedGoodAndBad_OnlyGoodParsed()
        {
            // Bug caught: a mix of valid, malformed, and missing-pos parts must
            // produce exactly the valid subset — no silent data corruption
            var good1 = new ConfigNode("PART");
            good1.AddValue("pos", "0,0,0");
            var noPos = new ConfigNode("PART");
            noPos.AddValue("name", "decoupler");
            var good2 = new ConfigNode("PART");
            good2.AddValue("pos", "5,10,15");

            var result = SpawnCollisionDetector.ParsePartPositions(
                new[] { good1, noPos, good2 });

            Assert.Equal(2, result.Count);
            Assert.Equal(0.0, (double)result[0].localPos.x, 4);
            Assert.Equal(5.0, (double)result[1].localPos.x, 4);
            Assert.Equal(10.0, (double)result[1].localPos.y, 4);
            Assert.Equal(15.0, (double)result[1].localPos.z, 4);
        }

        [Fact]
        public void ParsePartPositions_LogsCountMessage()
        {
            // Bug caught: diagnostic logging must report how many parts were parsed
            // vs total, enabling debugging of snapshot parsing issues
            logLines.Clear();

            var p1 = new ConfigNode("PART");
            p1.AddValue("pos", "1,2,3");
            var p2 = new ConfigNode("PART");
            p2.AddValue("pos", "4,5,6");
            var bad = new ConfigNode("PART");
            bad.AddValue("pos", "nope");

            SpawnCollisionDetector.ParsePartPositions(new[] { p1, p2, bad });

            Assert.Contains(logLines, l =>
                l.Contains("[SpawnCollision]") &&
                l.Contains("ParsePartPositions") &&
                l.Contains("2/3"));
        }

        [Fact]
        public void ParsePartPositions_PositionKey_ReturnsPositions()
        {
            // Bug #135: KSP vessel snapshots use "position" key, not "pos"
            var p1 = new ConfigNode("PART");
            p1.AddValue("position", "1.5,2.5,3.5");
            var p2 = new ConfigNode("PART");
            p2.AddValue("position", "-1,0,4.2");

            var result = SpawnCollisionDetector.ParsePartPositions(new[] { p1, p2 });

            Assert.Equal(2, result.Count);
            Assert.Equal(1.5, (double)result[0].localPos.x, 4);
            Assert.Equal(2.5, (double)result[0].localPos.y, 4);
            Assert.Equal(3.5, (double)result[0].localPos.z, 4);
        }

        [Fact]
        public void ParsePartPositions_MixedPosAndPosition_BothParsed()
        {
            // Both "pos" (synthetic snapshots) and "position" (KSP runtime) should work
            var posNode = new ConfigNode("PART");
            posNode.AddValue("pos", "1,2,3");
            var positionNode = new ConfigNode("PART");
            positionNode.AddValue("position", "4,5,6");

            var result = SpawnCollisionDetector.ParsePartPositions(new[] { posNode, positionNode });

            Assert.Equal(2, result.Count);
            Assert.Equal(1.0, (double)result[0].localPos.x, 4);
            Assert.Equal(4.0, (double)result[1].localPos.x, 4);
        }

        // ────────────────────────────────────────────────────────────
        //  WalkbackAlongTrajectory
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Build a straight-line trajectory along latitude with uniform time steps.
        /// </summary>
        private static List<TrajectoryPoint> MakeTrajectory(
            int count, double startUT, double endUT, double startLat = 0.0, double endLat = 1.0)
        {
            var points = new List<TrajectoryPoint>(count);
            for (int i = 0; i < count; i++)
            {
                double t = count > 1 ? (double)i / (count - 1) : 0.0;
                points.Add(new TrajectoryPoint
                {
                    ut = startUT + t * (endUT - startUT),
                    latitude = startLat + t * (endLat - startLat),
                    longitude = 0.0,
                    altitude = 100.0,
                    bodyName = "Kerbin",
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero,
                });
            }
            return points;
        }

        private static Vector3d IdentityWorldPos(TrajectoryPoint pt)
        {
            return SpawnCollisionDetector.SimplePointToWorldPos(pt, 600000.0);
        }

        [Fact]
        public void Walkback_Normal_FindsNonCollidingPosition()
        {
            // Bug caught: walkback must scan backward from the end and return the
            // first (latest) non-colliding index — returning the wrong index would
            // spawn the vessel at a stale trajectory position far from its intended
            // landing site.
            var points = MakeTrajectory(8, 1000.0, 1007.0);
            var bounds = new Bounds(Vector3.zero, new Vector3(2, 2, 2));

            // Last 2 points (indices 6,7) overlap; index 5 is clear
            int callIdx = 7;
            int result = SpawnCollisionDetector.WalkbackAlongTrajectory(
                points, bounds, 1f, IdentityWorldPos, pos =>
                {
                    bool overlaps = callIdx >= 6;
                    callIdx--;
                    return overlaps;
                });

            // Bug caught: must return 5 (latest clear), not 0 (earliest) or -1 (failure)
            Assert.Equal(5, result);
        }

        [Fact]
        public void Walkback_AllCollide_ReturnsNegativeOne()
        {
            // Bug caught: when every trajectory point overlaps with a loaded vessel,
            // the method must return -1 to signal manual-placement fallback —
            // returning 0 instead would spawn at the trajectory start, still colliding.
            var points = MakeTrajectory(6, 1000.0, 1005.0);
            var bounds = new Bounds(Vector3.zero, new Vector3(2, 2, 2));

            int result = SpawnCollisionDetector.WalkbackAlongTrajectory(
                points, bounds, 1f, IdentityWorldPos, pos => true);

            Assert.Equal(-1, result);
        }

        [Fact]
        public void Walkback_SingleStepSuccess_ReturnsSecondToLast()
        {
            // Bug caught: if only the very last point overlaps and the one before it
            // is clear, walkback should return count-2 after exactly one backward step —
            // an off-by-one starting at count instead of count-1 would skip the last
            // point entirely.
            var points = MakeTrajectory(5, 1000.0, 1004.0);
            var bounds = new Bounds(Vector3.zero, new Vector3(2, 2, 2));

            int callIdx = 4;
            int result = SpawnCollisionDetector.WalkbackAlongTrajectory(
                points, bounds, 1f, IdentityWorldPos, pos =>
                {
                    bool overlaps = callIdx == 4; // only last point overlaps
                    callIdx--;
                    return overlaps;
                });

            Assert.Equal(3, result);
        }

        [Fact]
        public void Walkback_StartUTEqualsEndUT_SinglePoint()
        {
            // Bug caught: trajectory with startUT == endUT (single-point edge case,
            // e.g., vessel recorded for exactly one physics frame) must not divide
            // by zero or produce an out-of-range index.
            var points = MakeTrajectory(1, 500.0, 500.0);
            var bounds = new Bounds(Vector3.zero, new Vector3(2, 2, 2));

            // Point is clear → returns 0
            int result = SpawnCollisionDetector.WalkbackAlongTrajectory(
                points, bounds, 1f, IdentityWorldPos, pos => false);

            Assert.Equal(0, result);
        }

        [Fact]
        public void Walkback_StartUTEqualsEndUT_Blocked_ReturnsNegative()
        {
            // Bug caught: single-point trajectory where that point is blocked must
            // return -1 — there are no earlier points to walk back to.
            var points = MakeTrajectory(1, 500.0, 500.0);
            var bounds = new Bounds(Vector3.zero, new Vector3(2, 2, 2));

            int result = SpawnCollisionDetector.WalkbackAlongTrajectory(
                points, bounds, 1f, IdentityWorldPos, pos => true);

            Assert.Equal(-1, result);
        }

        // ────────────────────────────────────────────────────────────
        //  ShouldSkipVesselType (#73)
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ShouldSkipVesselType_Debris_ReturnsTrue()
        {
            Assert.True(SpawnCollisionDetector.ShouldSkipVesselType(VesselType.Debris));
        }

        [Fact]
        public void ShouldSkipVesselType_EVA_ReturnsTrue()
        {
            Assert.True(SpawnCollisionDetector.ShouldSkipVesselType(VesselType.EVA));
        }

        [Fact]
        public void ShouldSkipVesselType_Flag_ReturnsTrue()
        {
            Assert.True(SpawnCollisionDetector.ShouldSkipVesselType(VesselType.Flag));
        }

        [Fact]
        public void ShouldSkipVesselType_SpaceObject_ReturnsTrue()
        {
            Assert.True(SpawnCollisionDetector.ShouldSkipVesselType(VesselType.SpaceObject));
        }

        [Fact]
        public void ShouldSkipVesselType_Ship_ReturnsFalse()
        {
            Assert.False(SpawnCollisionDetector.ShouldSkipVesselType(VesselType.Ship));
        }

        [Fact]
        public void ShouldSkipVesselType_Relay_ReturnsFalse()
        {
            Assert.False(SpawnCollisionDetector.ShouldSkipVesselType(VesselType.Relay));
        }
    }
}
