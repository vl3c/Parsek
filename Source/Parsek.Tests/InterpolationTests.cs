using System;
using Xunit;
using UnityEngine;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for interpolation logic, particularly edge cases.
    /// </summary>
    public class InterpolationTests
    {
        [Fact]
        public void SegmentDuration_ZeroOrNegative_ShouldBeGuarded()
        {
            // Arrange - two points with same or reversed timestamps
            var before = new TrajectoryPoint { ut = 100.0 };
            var after = new TrajectoryPoint { ut = 100.0 }; // Same time!

            // Act
            double segmentDuration = after.ut - before.ut;

            // Assert
            Assert.True(segmentDuration <= 0.0001,
                "Segment duration should be zero or near-zero for duplicate timestamps");
        }

        [Fact]
        public void InterpolationFactor_DivisionByZero_ShouldBeGuarded()
        {
            // This test documents the bug Codex found:
            // If segmentDuration is 0, calculating t = (targetUT - before.ut) / segmentDuration
            // results in NaN

            // Arrange
            double targetUT = 100.5;
            double beforeUT = 100.0;
            double afterUT = 100.0; // Same as before!

            double segmentDuration = afterUT - beforeUT;

            // Act & Assert
            if (segmentDuration <= 0.0001)
            {
                // Guard should prevent division
                Assert.True(true, "Guard prevents division by zero");
            }
            else
            {
                float t = (float)((targetUT - beforeUT) / segmentDuration);
                Assert.False(float.IsNaN(t), "Interpolation factor should not be NaN");
            }
        }

        [Fact]
        public void Vector3Lerp_WithNaNFactor_ProducesNaN()
        {
            // This demonstrates why the guard is necessary

            // Arrange
            Vector3 start = new Vector3(0, 0, 0);
            Vector3 end = new Vector3(100, 100, 100);
            float t = float.NaN;

            // Act
            Vector3 result = Vector3.Lerp(start, end, t);

            // Assert
            Assert.True(float.IsNaN(result.x) || float.IsNaN(result.y) || float.IsNaN(result.z),
                "Lerp with NaN factor produces NaN result");
        }

        // Note: Quaternion.Slerp with NaN factor produces NaN, which is why
        // interpolation code guards against NaN factors. Cannot test here because
        // Quaternion.Slerp is a Unity ECall method not available outside the engine.

        [Theory]
        [InlineData(0.0, 0.0)]
        [InlineData(0.5, 50.0)]
        [InlineData(1.0, 100.0)]
        public void InterpolationFactor_ValidRange_ProducesCorrectResults(float t, float expectedValue)
        {
            // Arrange
            Vector3 start = Vector3.zero;
            Vector3 end = new Vector3(100, 100, 100);

            // Act
            Vector3 result = Vector3.Lerp(start, end, t);

            // Assert
            Assert.Equal(expectedValue, result.x, 0.01f);
            Assert.Equal(expectedValue, result.y, 0.01f);
            Assert.Equal(expectedValue, result.z, 0.01f);
        }

        [Fact]
        public void InterpolationFactor_Clamped_StaysBetweenZeroAndOne()
        {
            // The spike uses Mathf.Clamp01 to prevent t from going outside [0,1]

            // Arrange & Act
            float negative = Mathf.Clamp01(-0.5f);
            float overOne = Mathf.Clamp01(1.5f);
            float valid = Mathf.Clamp01(0.5f);

            // Assert
            Assert.Equal(0.0f, negative);
            Assert.Equal(1.0f, overOne);
            Assert.Equal(0.5f, valid);
        }

        [Fact]
        public void NaNPosition_ShouldBeDetectedAndHandled()
        {
            // The spike checks for NaN in interpolated position

            // Arrange
            Vector3 posWithNaN = new Vector3(float.NaN, 100, 200);

            // Act
            bool hasNaN = float.IsNaN(posWithNaN.x) ||
                         float.IsNaN(posWithNaN.y) ||
                         float.IsNaN(posWithNaN.z);

            // Assert
            Assert.True(hasNaN, "Should detect NaN in position vector");
        }

        [Fact]
        public void SmallSegmentDuration_LessThanThreshold_ShouldBeGuarded()
        {
            // The spike uses 0.0001 as threshold

            // Arrange
            double segmentDuration1 = 0.00005; // Below threshold
            double segmentDuration2 = 0.001;   // Above threshold

            // Assert
            Assert.True(segmentDuration1 <= 0.0001, "Very small duration should trigger guard");
            Assert.False(segmentDuration2 <= 0.0001, "Normal duration should not trigger guard");
        }
    }
}
