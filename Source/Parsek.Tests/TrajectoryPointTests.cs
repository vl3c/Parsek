using System;
using Xunit;
using UnityEngine;

namespace Parsek.Tests
{
    public class TrajectoryPointTests
    {
        [Fact]
        public void TrajectoryPoint_ToString_FormatsCorrectly()
        {
            // Arrange
            var point = new ParsekSpike.TrajectoryPoint
            {
                ut = 12345.6789,
                latitude = 0.1234,
                longitude = -45.6789,
                altitude = 75000.123
            };

            // Act
            var result = point.ToString();

            // Assert - check structure, not locale-specific formatting
            Assert.Contains("UT=", result);
            Assert.Contains("lat=", result);
            Assert.Contains("lon=", result);
            Assert.Contains("alt=", result);
            Assert.Contains("12345", result); // Check number is present
        }

        [Fact]
        public void TrajectoryPoint_StoresAllFields()
        {
            // Arrange & Act
            var rotation = Quaternion.identity;
            var velocity = new Vector3(10, 20, 30);

            var point = new ParsekSpike.TrajectoryPoint
            {
                ut = 100,
                latitude = 1.5,
                longitude = 2.5,
                altitude = 70000,
                rotation = rotation,
                velocity = velocity,
                bodyName = "Kerbin"
            };

            // Assert
            Assert.Equal(100, point.ut);
            Assert.Equal(1.5, point.latitude);
            Assert.Equal(2.5, point.longitude);
            Assert.Equal(70000, point.altitude);
            Assert.Equal(rotation, point.rotation);
            Assert.Equal(velocity, point.velocity);
            Assert.Equal("Kerbin", point.bodyName);
        }
    }
}
