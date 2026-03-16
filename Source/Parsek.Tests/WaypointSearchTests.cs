using System;
using System.Collections.Generic;
using Xunit;
using UnityEngine;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for FindWaypointIndex binary search logic.
    /// Calls TrajectoryMath.FindWaypointIndex directly (exposed via InternalsVisibleTo).
    /// </summary>
    public class WaypointSearchTests : IDisposable
    {
        private List<TrajectoryPoint> points;
        private int cachedIndex;

        public WaypointSearchTests()
        {
            points = new List<TrajectoryPoint>();
            cachedIndex = 0;
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private void PopulateRecording(params double[] timestamps)
        {
            points.Clear();
            cachedIndex = 0;
            foreach (var ut in timestamps)
            {
                points.Add(new TrajectoryPoint
                {
                    ut = ut,
                    latitude = 0,
                    longitude = 0,
                    altitude = 0,
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero,
                    bodyName = "Kerbin"
                });
            }
        }

        private int FindWaypointIndex(double targetUT)
        {
            return TrajectoryMath.FindWaypointIndex(points, ref cachedIndex, targetUT);
        }

        [Fact]
        public void FindWaypointIndex_BeforeFirstPoint_ReturnsMinusOne()
        {
            // Arrange
            PopulateRecording(10, 20, 30, 40, 50);

            // Act
            var result = FindWaypointIndex(5);

            // Assert
            Assert.Equal(-1, result);
        }

        [Fact]
        public void FindWaypointIndex_AtOrAfterLastPoint_ReturnsSecondToLast()
        {
            // Arrange
            PopulateRecording(10, 20, 30, 40, 50);

            // Act
            var resultAt = FindWaypointIndex(50);
            var resultAfter = FindWaypointIndex(60);

            // Assert
            Assert.Equal(3, resultAt); // points.Count - 2
            Assert.Equal(3, resultAfter);
        }

        [Fact]
        public void FindWaypointIndex_BetweenPoints_ReturnsCorrectIndex()
        {
            // Arrange
            PopulateRecording(10, 20, 30, 40, 50);

            // Act & Assert
            Assert.Equal(0, FindWaypointIndex(15)); // Between 10 and 20
            Assert.Equal(1, FindWaypointIndex(25)); // Between 20 and 30
            Assert.Equal(2, FindWaypointIndex(35)); // Between 30 and 40
            Assert.Equal(3, FindWaypointIndex(45)); // Between 40 and 50
        }

        [Fact]
        public void FindWaypointIndex_ExactlyOnPoint_ReturnsCorrectIndex()
        {
            // Arrange
            PopulateRecording(10, 20, 30, 40, 50);

            // Act & Assert
            Assert.Equal(0, FindWaypointIndex(10)); // Exactly at first
            Assert.Equal(1, FindWaypointIndex(20)); // Exactly at second
            Assert.Equal(2, FindWaypointIndex(30)); // Exactly at middle
        }

        [Fact]
        public void FindWaypointIndex_SequentialAccess_UsesCachedIndex()
        {
            // Arrange
            PopulateRecording(10, 20, 30, 40, 50, 60, 70, 80, 90, 100);

            // Act - simulate sequential playback
            var index1 = FindWaypointIndex(15);
            var index2 = FindWaypointIndex(25); // Next segment
            var index3 = FindWaypointIndex(35); // Next segment

            // Assert
            Assert.Equal(0, index1);
            Assert.Equal(1, index2);
            Assert.Equal(2, index3);
            Assert.Equal(2, cachedIndex); // Cache should be updated
        }

        [Fact]
        public void FindWaypointIndex_NonSequentialAccess_FallsBackToBinarySearch()
        {
            // Arrange
            PopulateRecording(10, 20, 30, 40, 50, 60, 70, 80, 90, 100);
            cachedIndex = 2; // Cached at index 2 (ut=30)

            // Act - jump to much later time
            var result = FindWaypointIndex(85);

            // Assert
            Assert.Equal(7, result); // Between 80 and 90
            Assert.Equal(7, cachedIndex); // Cache updated
        }

        [Fact]
        public void FindWaypointIndex_LargeRecording_PerformsBinarySearch()
        {
            // Arrange - create recording with 1000 points
            var timestamps = new double[1000];
            for (int i = 0; i < 1000; i++)
            {
                timestamps[i] = i * 10;
            }
            PopulateRecording(timestamps);

            // Act - search in middle
            var result = FindWaypointIndex(5005);

            // Assert
            Assert.Equal(500, result); // Between 5000 and 5010
        }

        [Fact]
        public void FindWaypointIndex_DuplicateTimestamps_HandlesGracefully()
        {
            // Arrange - recordings shouldn't have duplicates, but test robustness
            PopulateRecording(10, 20, 20, 30, 40);

            // Act
            var result = FindWaypointIndex(20);

            // Assert - should find one of the duplicate indices
            Assert.InRange(result, 1, 2);
        }

        [Fact]
        public void FindWaypointIndex_TwoPoints_WorksCorrectly()
        {
            // Arrange - minimum valid recording
            PopulateRecording(10, 20);

            // Act
            var result = FindWaypointIndex(15);

            // Assert
            Assert.Equal(0, result);
        }

        [Fact]
        public void FindWaypointIndex_UnevenSpacing_WorksCorrectly()
        {
            // Arrange - realistic spacing with variable intervals
            PopulateRecording(0, 0.5, 1.1, 5.3, 5.4, 10.0, 25.7);

            // Act & Assert
            Assert.Equal(0, FindWaypointIndex(0.3));
            Assert.Equal(2, FindWaypointIndex(3.0));
            Assert.Equal(4, FindWaypointIndex(7.5));
        }

        [Fact]
        public void FindWaypointIndex_EmptyRecording_ReturnsMinusOne()
        {
            // Arrange - empty points list by default

            // Act
            var result = FindWaypointIndex(50);

            // Assert
            Assert.Equal(-1, result);
        }

        [Fact]
        public void FindWaypointIndex_SinglePoint_ReturnsMinusOne()
        {
            // Arrange - can't interpolate with single point
            PopulateRecording(10);

            // Act
            var result = FindWaypointIndex(10);

            // Assert
            Assert.Equal(-1, result);
        }

        [Fact]
        public void FindWaypointIndex_DuplicatesAtEnd_HandlesConsistently()
        {
            // Arrange
            PopulateRecording(10, 20, 30, 30);

            // Act
            var result = FindWaypointIndex(30);

            // Assert - should find one of indices 1 or 2
            Assert.InRange(result, 1, 2);
        }

        [Fact]
        public void FindWaypointIndex_BackwardTimeSearch()
        {
            // Arrange — advance cache forward then search backward
            PopulateRecording(10, 20, 30, 40, 50, 60, 70, 80);
            FindWaypointIndex(65); // advance cache to index 5
            Assert.Equal(5, cachedIndex);

            // Act — search backward, cache miss, falls to binary search
            var result = FindWaypointIndex(25);

            // Assert
            Assert.Equal(1, result);
            Assert.Equal(1, cachedIndex);
        }

        [Fact]
        public void FindWaypointIndex_AllSameTimestamp_ReturnsLastPair()
        {
            // All points at same UT — degenerate case
            PopulateRecording(100, 100, 100, 100);

            // At or after last point → returns Count-2
            var result = FindWaypointIndex(100);
            Assert.Equal(2, result);
        }

        [Fact]
        public void FindWaypointIndex_CachedIndex_BeyondListSize_FallsBack()
        {
            // Arrange — set cache to a stale value larger than list
            PopulateRecording(10, 20, 30, 40, 50);
            cachedIndex = 100; // stale cache, way beyond list size

            // Act
            var result = FindWaypointIndex(35);

            // Assert — should still find via binary search
            Assert.Equal(2, result);
            Assert.Equal(2, cachedIndex);
        }
    }
}
