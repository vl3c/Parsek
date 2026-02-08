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
        public void TrajectoryPoint_StoresAllFields()
        {
            // Arrange & Act
            var rotation = Quaternion.identity;
            var velocity = new Vector3(10, 20, 30);

            var point = new TrajectoryPoint
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
        public void DefaultValues_AreZeroOrNull()
        {
            var point = new TrajectoryPoint();

            Assert.Equal(0, point.ut);
            Assert.Equal(0, point.latitude);
            Assert.Equal(0, point.longitude);
            Assert.Equal(0, point.altitude);
            Assert.Equal(0, point.funds);
            Assert.Equal(0f, point.science);
            Assert.Equal(0f, point.reputation);
            Assert.Null(point.bodyName);
            Assert.Equal(Vector3.zero, point.velocity);
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

        [Fact]
        public void EqualityCheck_SameValues()
        {
            var rot = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f);
            var vel = new Vector3(5, 10, 15);

            var a = new TrajectoryPoint
            {
                ut = 100, latitude = 1, longitude = 2, altitude = 300,
                rotation = rot, velocity = vel, bodyName = "Mun",
                funds = 1000, science = 5, reputation = 3
            };

            var b = new TrajectoryPoint
            {
                ut = 100, latitude = 1, longitude = 2, altitude = 300,
                rotation = rot, velocity = vel, bodyName = "Mun",
                funds = 1000, science = 5, reputation = 3
            };

            Assert.Equal(a.ut, b.ut);
            Assert.Equal(a.latitude, b.latitude);
            Assert.Equal(a.longitude, b.longitude);
            Assert.Equal(a.altitude, b.altitude);
            Assert.Equal(a.rotation, b.rotation);
            Assert.Equal(a.velocity, b.velocity);
            Assert.Equal(a.bodyName, b.bodyName);
            Assert.Equal(a.funds, b.funds);
            Assert.Equal(a.science, b.science);
            Assert.Equal(a.reputation, b.reputation);
        }
    }
}
