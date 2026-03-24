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
            Bounds bounds = SpawnCollisionDetector.ComputeVesselBounds(null);

            // Fallback: 2m cube at origin
            Assert.Equal(Vector3.zero, bounds.center);
            Assert.Equal(new Vector3(2, 2, 2), bounds.size);
        }

        [Fact]
        public void ComputeVesselBounds_NoParts_ReturnsFallbackBounds()
        {
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
            logLines.Clear();

            SpawnCollisionDetector.ComputeBoundsFromParts(
                new List<(Vector3 localPos, float halfExtent)>());

            Assert.Contains(logLines, l =>
                l.Contains("[SpawnCollision]") && l.Contains("empty part list"));
        }

        [Fact]
        public void ComputeVesselBounds_NullSnapshot_LogsFallback()
        {
            logLines.Clear();

            SpawnCollisionDetector.ComputeVesselBounds(null);

            Assert.Contains(logLines, l =>
                l.Contains("[SpawnCollision]") && l.Contains("null snapshot"));
        }

        [Fact]
        public void ComputeVesselBounds_NoParts_LogsFallback()
        {
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
    }
}
