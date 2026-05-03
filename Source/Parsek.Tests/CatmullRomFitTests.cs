using Xunit;
using UnityEngine;

namespace Parsek.Tests
{
    public class CatmullRomFitTests
    {
        [Fact]
        public void Evaluate_UniformKnots_MatchesLinearMidpointForLinearControls()
        {
            var samples = new[]
            {
                Point(0.0, 0.0, 0.0, 0.0),
                Point(1.0, 0.0, 0.0, 10.0),
                Point(2.0, 0.0, 0.0, 20.0),
                Point(3.0, 0.0, 0.0, 30.0),
            };

            var spline = TrajectoryMath.CatmullRomFit.Fit(samples, 0.5, out string failureReason);

            Assert.True(spline.IsValid, failureReason);
            Vector3d evaluated = TrajectoryMath.CatmullRomFit.Evaluate(spline, 1.5);
            Assert.Equal(15.0, evaluated.z, 6);
        }

        [Fact]
        public void Evaluate_NonUniformShortSegment_ScalesTangentsBySegmentDuration()
        {
            var samples = new[]
            {
                Point(0.0, 0.0, 0.0, 0.0),
                Point(1.0, 0.0, 0.0, 100.0),
                Point(1.1, 0.0, 0.0, 110.0),
                Point(10.0, 0.0, 0.0, 1000.0),
            };

            var spline = TrajectoryMath.CatmullRomFit.Fit(samples, 0.5, out string failureReason);

            Assert.True(spline.IsValid, failureReason);
            Vector3d evaluated = TrajectoryMath.CatmullRomFit.Evaluate(spline, 1.05);
            Assert.Equal(105.0, evaluated.z, 6);
        }

        [Fact]
        public void Evaluate_Pr708AtmosphericShortSegment_StaysWithinAdjacentControls()
        {
            var samples = new[]
            {
                Point(124.89846534728612, -0.0721130071451271, -68.0374375190895, 52024.924057189724),
                Point(125.71846534728596, -0.071937277156953972, -67.917931275748558, 52705.751685657306),
                Point(125.9784653472859, -0.071881883407706529, -67.880096707885329, 52921.17572358402),
                Point(128.978465347286, -0.071237061980443314, -67.44556363354171, 55392.082665248076),
            };

            var spline = TrajectoryMath.CatmullRomFit.Fit(samples, 0.5, out string failureReason);

            Assert.True(spline.IsValid, failureReason);
            Vector3d evaluated = TrajectoryMath.CatmullRomFit.Evaluate(spline, 125.758465347286);
            Assert.InRange(evaluated.x, samples[1].latitude, samples[2].latitude);
            Assert.InRange(evaluated.y, samples[1].longitude, samples[2].longitude);
            Assert.InRange(evaluated.z, samples[1].altitude, samples[2].altitude);
        }

        private static TrajectoryPoint Point(double ut, double latitude, double longitude, double altitude)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = latitude,
                longitude = longitude,
                altitude = altitude,
                bodyName = "Kerbin",
            };
        }
    }
}
