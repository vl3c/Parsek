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
            var point = new TrajectoryPoint
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
        public void ResourceFields_StoreCorrectly()
        {
            var point = new TrajectoryPoint
            {
                funds = 123456.78,
                science = 42.5f,
                reputation = -10.3f
            };

            Assert.Equal(123456.78, point.funds);
            Assert.Equal(42.5f, point.science);
            Assert.Equal(-10.3f, point.reputation);
        }

        [Fact]
        public void ToString_DoesNotIncludeResourceFields()
        {
            var point = new TrajectoryPoint
            {
                ut = 100, latitude = 1, longitude = 2, altitude = 300,
                funds = 50000, science = 10, reputation = 5
            };

            var result = point.ToString();

            Assert.DoesNotContain("funds", result);
            Assert.DoesNotContain("science", result);
            Assert.DoesNotContain("rep", result);
        }

        [Fact]
        public void ToString_WithNegativeCoordinates()
        {
            var point = new TrajectoryPoint
            {
                ut = 500,
                latitude = -28.6083,
                longitude = -80.6041,
                altitude = 10
            };

            var result = point.ToString();

            Assert.Contains("lat=", result);
            Assert.Contains("lon=", result);
            Assert.Contains("-", result);
        }

        [Fact]
        public void ToString_WithExtremeAltitude()
        {
            var point = new TrajectoryPoint
            {
                ut = 1000,
                latitude = 0,
                longitude = 0,
                altitude = 70000000 // 70,000 km
            };

            var result = point.ToString();

            Assert.Contains("alt=", result);
            Assert.Contains("70000000", result);
        }

    }
}
