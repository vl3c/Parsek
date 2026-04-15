using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="TrajectoryMath.HasReentryPotential"/>. This gate skips the
    /// expensive reentry FX build (mesh combine, ParticleSystem, glow material clones)
    /// for trajectories that cannot possibly produce reentry visuals — the dominant
    /// cost in the #406 showcase-loop map-view perf tank.
    /// </summary>
    [Collection("Sequential")]
    public class ReentryPotentialTests
    {
        private static List<string> CaptureLog()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            return lines;
        }

        public ReentryPotentialTests() { CaptureLog(); }
        // xunit will create a fresh instance per test; Sequential collection prevents
        // ParsekLog static state from racing across tests.

        [Fact]
        public void NullTrajectory_ReturnsFalse()
        {
            Assert.False(TrajectoryMath.HasReentryPotential(null));
        }

        [Fact]
        public void EmptyTrajectory_NoOrbitSegments_ReturnsFalse()
        {
            var traj = new MockTrajectory();
            Assert.False(TrajectoryMath.HasReentryPotential(traj));
        }

        [Fact]
        public void StationaryShowcase_AllZeroVelocity_ReturnsFalse()
        {
            // Stationary KSC-surface part showcase — the canonical case the fix targets.
            // Peak velocity is literally zero.
            var traj = new MockTrajectory().WithTimeRange(0, 30);
            Assert.False(TrajectoryMath.HasReentryPotential(traj));
        }

        [Fact]
        public void EvaWalk_SlowVelocity_ReturnsFalse()
        {
            var traj = new MockTrajectory();
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 0, bodyName = "Kerbin",
                velocity = new Vector3(3f, 0f, 0f),
                rotation = Quaternion.identity
            });
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 10, bodyName = "Kerbin",
                velocity = new Vector3(5f, 0f, 0f),
                rotation = Quaternion.identity
            });
            Assert.False(TrajectoryMath.HasReentryPotential(traj));
        }

        [Fact]
        public void SlowFleaHop_UnderFloor_ReturnsFalse()
        {
            // Flea-class solid booster hop: max velocity ~200 m/s, no orbit segments.
            // Well below Mach 1 anywhere — cannot produce reentry FX.
            var traj = new MockTrajectory();
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 0, bodyName = "Kerbin",
                velocity = new Vector3(0f, 0f, 0f),
                rotation = Quaternion.identity
            });
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 15, bodyName = "Kerbin",
                velocity = new Vector3(0f, 200f, 0f),
                rotation = Quaternion.identity
            });
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 30, bodyName = "Kerbin",
                velocity = new Vector3(0f, 120f, 0f),
                rotation = Quaternion.identity
            });
            Assert.False(TrajectoryMath.HasReentryPotential(traj));
        }

        [Fact]
        public void JustBelowFloor_399MetersPerSecond_ReturnsFalse()
        {
            var traj = new MockTrajectory();
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 0, bodyName = "Kerbin",
                velocity = new Vector3(399f, 0f, 0f),
                rotation = Quaternion.identity
            });
            Assert.False(TrajectoryMath.HasReentryPotential(traj));
        }

        [Fact]
        public void AtFloor_400MetersPerSecond_ReturnsTrue()
        {
            var traj = new MockTrajectory();
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 0, bodyName = "Kerbin",
                velocity = new Vector3(TrajectoryMath.ReentryPotentialSpeedFloor, 0f, 0f),
                rotation = Quaternion.identity
            });
            Assert.True(TrajectoryMath.HasReentryPotential(traj));
        }

        [Fact]
        public void SuborbitalAscent_FastPoint_ReturnsTrue()
        {
            var traj = new MockTrajectory();
            // Mostly slow points, one high-speed sample — should still flag the recording.
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 0, bodyName = "Kerbin",
                velocity = Vector3.zero, rotation = Quaternion.identity
            });
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 30, bodyName = "Kerbin",
                velocity = new Vector3(0f, 800f, 0f),
                rotation = Quaternion.identity
            });
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 60, bodyName = "Kerbin",
                velocity = new Vector3(0f, 50f, 0f),
                rotation = Quaternion.identity
            });
            Assert.True(TrajectoryMath.HasReentryPotential(traj));
        }

        [Fact]
        public void DiagonalVelocity_MagnitudeAboveFloor_ReturnsTrue()
        {
            // 300/300/300 has magnitude ~520 m/s — tests that we use magnitude,
            // not any single axis component.
            var traj = new MockTrajectory();
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 0, bodyName = "Kerbin",
                velocity = new Vector3(300f, 300f, 300f),
                rotation = Quaternion.identity
            });
            Assert.True(TrajectoryMath.HasReentryPotential(traj));
        }

        [Fact]
        public void OrbitSegmentsPresent_AlwaysReturnsTrue()
        {
            // Orbital ghost with no high-speed point samples — e.g., a recording that
            // only captured points during a slow rendezvous phase but has an orbit
            // segment covering the faster parts. Must still build FX because de-orbit
            // heat happens at orbital speed (~2300 m/s).
            var traj = new MockTrajectory();
            traj.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                semiMajorAxis = 700000,
                eccentricity = 0.01,
                startUT = 0, endUT = 3000
            });
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 0, bodyName = "Kerbin",
                velocity = new Vector3(5f, 0f, 0f),
                rotation = Quaternion.identity
            });
            Assert.True(TrajectoryMath.HasReentryPotential(traj));
        }

        [Fact]
        public void OrbitSegmentOnly_NoPoints_ReturnsTrue()
        {
            var traj = new MockTrajectory();
            traj.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                semiMajorAxis = 700000,
                eccentricity = 0.01,
                startUT = 0, endUT = 3000
            });
            Assert.True(TrajectoryMath.HasReentryPotential(traj));
        }

        [Fact]
        public void SpeedFloor_SaneDefault()
        {
            // Lock the constant so accidental drift is caught. 400 m/s chosen because
            // it is well below Mach 1.5 on every stock body with an atmosphere,
            // guaranteeing no false negatives on reentry heating.
            Assert.Equal(400f, TrajectoryMath.ReentryPotentialSpeedFloor);
        }
    }
}
