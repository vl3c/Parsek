using System;
using Xunit;
using UnityEngine;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for quaternion sanitization logic.
    /// Tests call the actual ParsekSpike.SanitizeQuaternion method (exposed via InternalsVisibleTo).
    /// </summary>
    public class QuaternionSanitizationTests
    {
        private readonly Parsek.ParsekSpike spike;

        public QuaternionSanitizationTests()
        {
            // Create a ParsekSpike instance to access internal methods
            spike = new Parsek.ParsekSpike();
        }

        [Fact]
        public void SanitizeQuaternion_ValidQuaternion_ReturnsNormalized()
        {
            // Arrange
            var q = new Quaternion(1, 2, 3, 4); // Not normalized

            // Act
            var result = spike.SanitizeQuaternion(q);

            // Assert
            float magnitude = Mathf.Sqrt(result.x * result.x + result.y * result.y +
                                        result.z * result.z + result.w * result.w);
            Assert.InRange(magnitude, 0.999f, 1.001f); // Should be normalized
        }

        [Fact]
        public void SanitizeQuaternion_NaNInX_ReplacesWithZero()
        {
            // Arrange
            var q = new Quaternion(float.NaN, 0, 0, 1);

            // Act
            var result = spike.SanitizeQuaternion(q);

            // Assert
            Assert.False(float.IsNaN(result.x));
            Assert.False(float.IsNaN(result.y));
            Assert.False(float.IsNaN(result.z));
            Assert.False(float.IsNaN(result.w));
        }

        [Fact]
        public void SanitizeQuaternion_AllNaN_ReturnsIdentity()
        {
            // Arrange
            var q = new Quaternion(float.NaN, float.NaN, float.NaN, float.NaN);

            // Act
            var result = spike.SanitizeQuaternion(q);

            // Assert - after sanitizing NaN to 0,0,0,1 and normalizing
            Assert.False(float.IsNaN(result.x));
            Assert.False(float.IsNaN(result.y));
            Assert.False(float.IsNaN(result.z));
            Assert.False(float.IsNaN(result.w));
        }

        [Fact]
        public void SanitizeQuaternion_ZeroMagnitude_ReturnsIdentity()
        {
            // Arrange
            var q = new Quaternion(0, 0, 0, 0);

            // Act
            var result = spike.SanitizeQuaternion(q);

            // Assert
            Assert.Equal(Quaternion.identity, result);
        }

        [Fact]
        public void SanitizeQuaternion_VerySmallMagnitude_ReturnsIdentity()
        {
            // Arrange
            var q = new Quaternion(0.0001f, 0.0001f, 0.0001f, 0.0001f);

            // Act
            var result = spike.SanitizeQuaternion(q);

            // Assert
            Assert.Equal(Quaternion.identity, result);
        }

        [Fact]
        public void SanitizeQuaternion_Identity_RemainsIdentity()
        {
            // Arrange
            var q = Quaternion.identity;

            // Act
            var result = spike.SanitizeQuaternion(q);

            // Assert
            Assert.InRange(result.x, -0.01f, 0.01f);
            Assert.InRange(result.y, -0.01f, 0.01f);
            Assert.InRange(result.z, -0.01f, 0.01f);
            Assert.InRange(result.w, 0.99f, 1.01f);
        }

        [Fact]
        public void SanitizeQuaternion_InfinityInX_ReturnsIdentity()
        {
            // Arrange - Codex bug #2: Infinity causes NaN during normalization
            var q = new Quaternion(float.PositiveInfinity, 0, 0, 1);

            // Act
            var result = spike.SanitizeQuaternion(q);

            // Assert - should not contain NaN or Infinity
            Assert.True(!float.IsNaN(result.x) && !float.IsInfinity(result.x));
            Assert.True(!float.IsNaN(result.y) && !float.IsInfinity(result.y));
            Assert.True(!float.IsNaN(result.z) && !float.IsInfinity(result.z));
            Assert.True(!float.IsNaN(result.w) && !float.IsInfinity(result.w));
            Assert.Equal(Quaternion.identity, result);
        }

        [Fact]
        public void SanitizeQuaternion_AllInfinity_ReturnsIdentity()
        {
            // Arrange
            var q = new Quaternion(float.PositiveInfinity, float.NegativeInfinity,
                                   float.PositiveInfinity, float.NegativeInfinity);

            // Act
            var result = spike.SanitizeQuaternion(q);

            // Assert
            Assert.True(!float.IsNaN(result.x) && !float.IsInfinity(result.x));
            Assert.True(!float.IsNaN(result.y) && !float.IsInfinity(result.y));
            Assert.True(!float.IsNaN(result.z) && !float.IsInfinity(result.z));
            Assert.True(!float.IsNaN(result.w) && !float.IsInfinity(result.w));
            Assert.Equal(Quaternion.identity, result);
        }

        [Fact]
        public void SanitizeQuaternion_NearIdentity_StaysNormalized()
        {
            // Arrange - Codex suggestion #6
            var q = new Quaternion(0, 0, 0, 0.99999f);

            // Act
            var result = spike.SanitizeQuaternion(q);

            // Assert - should remain stable and normalized
            float magnitude = Mathf.Sqrt(result.x * result.x + result.y * result.y +
                                        result.z * result.z + result.w * result.w);
            Assert.InRange(magnitude, 0.999f, 1.001f);
            Assert.True(!float.IsNaN(result.x) && !float.IsInfinity(result.x));
            Assert.True(!float.IsNaN(result.y) && !float.IsInfinity(result.y));
            Assert.True(!float.IsNaN(result.z) && !float.IsInfinity(result.z));
            Assert.True(!float.IsNaN(result.w) && !float.IsInfinity(result.w));
        }
    }
}
