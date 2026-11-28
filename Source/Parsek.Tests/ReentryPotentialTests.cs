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
        public void SlowRover_UnderFloor_ReturnsFalse()
        {
            // A rover / taxiing plane: max velocity 60 m/s, no orbit segments. Slower than
            // stock's effect starts even in Eve's sea-level air.
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
                velocity = new Vector3(0f, 60f, 0f),
                rotation = Quaternion.identity
            });
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 30, bodyName = "Kerbin",
                velocity = new Vector3(0f, 40f, 0f),
                rotation = Quaternion.identity
            });
            Assert.False(TrajectoryMath.HasReentryPotential(traj));
        }

        [Fact]
        public void JustBelowFloor_ReturnsFalse()
        {
            var traj = new MockTrajectory();
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 0, bodyName = "Kerbin",
                velocity = new Vector3(TrajectoryMath.ReentryPotentialSpeedFloor - 1f, 0f, 0f),
                rotation = Quaternion.identity
            });
            Assert.False(TrajectoryMath.HasReentryPotential(traj));
        }

        [Fact]
        public void AtFloor_ReturnsTrue()
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
        public void SpeedFloor_IsStocksStartInEvesSeaLevelAirLessTheMargin()
        {
            // Stock's FxScalar has no Mach gate: 0.5 * densityFactor(rho) * v^3.5 = p0 is
            // the start. In Eve's sea-level air that is about 80 m/s; the floor sits 5 % under.
            double eveStart = TrajectoryMath.MinimumAeroFxSpeed(TrajectoryMath.StockDensestSurfaceAirDensity);
            Assert.InRange(eveStart, 78.0, 82.0);
            Assert.InRange(TrajectoryMath.ReentryPotentialSpeedFloor, (float)(eveStart * 0.95) - 0.001f, (float)(eveStart * 0.95) + 0.001f);
            Assert.InRange(TrajectoryMath.MinimumAeroFxSpeed(1.225), 196.0, 201.0);
            Assert.True(double.IsPositiveInfinity(TrajectoryMath.MinimumAeroFxSpeed(0.0)));
            Assert.True(double.IsPositiveInfinity(TrajectoryMath.MinimumAeroFxSpeed(double.NaN)));
        }

        [Fact]
        public void SpeedFloor_ConstantsAreTheLiveEffectsOwn()
        {
            AeroFxConstants c = AeroFxConstants.StockDefaults;
            Assert.Equal(c.DensityScalar1, TrajectoryMath.AeroFxDensityScalar1);
            Assert.Equal(c.DensityExponent1, TrajectoryMath.AeroFxDensityExponent1);
            Assert.Equal(c.DensityScalar2, TrajectoryMath.AeroFxDensityScalar2);
            Assert.Equal(c.DensityExponent2, TrajectoryMath.AeroFxDensityExponent2);
            Assert.Equal(c.VelocityExponent, TrajectoryMath.AeroFxVelocityExponent);
            Assert.Equal(GhostAeroFx.HeatFluxThreshold, TrajectoryMath.AeroFxHeatFluxThreshold);
        }

        [Theory]
        [InlineData(6.24)]   // Eve sea level
        [InlineData(1.225)]  // Kerbin sea level
        [InlineData(0.2)]
        public void MinimumAeroFxSpeed_IsWhereStocksScalarCrossesZero(double density)
        {
            double start = TrajectoryMath.MinimumAeroFxSpeed(density);
            AeroFxConstants c = AeroFxConstants.StockDefaults;
            float mach = 0.5f;
            Assert.True(GhostAeroFx.ComputeFxScalar(density, start * 0.99, mach, c, out _) < 0f);
            Assert.True(GhostAeroFx.ComputeFxScalar(density, start * 1.01, mach, c, out _) > 0f);
        }

        [Fact]
        public void LowLevelPass_380MetersPerSecondPeak_HasPotential()
        {
            // Stock shows this at FxScalar 0.6..1.0 at Kerbin sea level; the old 400 m/s floor
            // never built its fire.
            var traj = new MockTrajectory();
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 0, bodyName = "Kerbin",
                velocity = new Vector3(300f, 0f, 0f),
                rotation = Quaternion.identity
            });
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 10, bodyName = "Kerbin",
                velocity = new Vector3(380f, 0f, 0f),
                rotation = Quaternion.identity
            });
            Assert.True(TrajectoryMath.HasReentryPotential(traj));
            Assert.True(GhostAeroFx.ComputeFxScalar(1.225, 380.0, 1.12f, AeroFxConstants.StockDefaults, out _) > 0.6f);
        }

        [Fact]
        public void LazyBuild_350MetersPerSecondInKerbinSeaLevelAir_Fires()
        {
            Assert.True(GhostPlaybackLogic.ShouldBuildLazyReentryFx(
                pendingFlag: true, bodyName: "Kerbin", bodyHasAtmosphere: true,
                altitudeMeters: 100, atmosphereDepthMeters: 70_000,
                surfaceSpeedMetersPerSecond: 350f,
                speedFloorMetersPerSecond: TrajectoryMath.ReentryPotentialSpeedFloor));
            Assert.True(GhostAeroFx.ComputeFxScalar(1.225, 350.0, 1.03f, AeroFxConstants.StockDefaults, out _) > 0.5f);
        }

        [Fact]
        public void NanVelocityComponent_DoesNotThrow_ReturnsFalse()
        {
            // Malformed point with a NaN component — sqrMagnitude yields NaN and
            // `NaN >= floorSq` is false, so the gate conservatively returns false
            // without crashing. Protects against any upstream bug that leaks a
            // degenerate velocity vector into the trajectory.
            var traj = new MockTrajectory();
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 0, bodyName = "Kerbin",
                velocity = new Vector3(float.NaN, 0f, 0f),
                rotation = Quaternion.identity
            });
            bool result = false;
            var ex = Record.Exception(() => result = TrajectoryMath.HasReentryPotential(traj));
            Assert.Null(ex);
            Assert.False(result);
        }

        [Fact]
        public void InfinityVelocityComponent_ReturnsTrue()
        {
            // Infinity passes the floor comparison (Infinity >= floorSq), so the gate
            // plays it safe and builds reentry FX. Again, this is not expected in
            // production — the test just locks in the behavior so a later refactor
            // that adds input sanitization is a conscious choice, not an accident.
            var traj = new MockTrajectory();
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 0, bodyName = "Kerbin",
                velocity = new Vector3(float.PositiveInfinity, 0f, 0f),
                rotation = Quaternion.identity
            });
            Assert.True(TrajectoryMath.HasReentryPotential(traj));
        }

        [Fact]
        public void NullPointsList_ReturnsFalse()
        {
            // IPlaybackTrajectory.Points is not nominally null, but the helper
            // defensively handles it (we have no ProtoVessel-sourced trajectories
            // that might return null today, but the guard is cheap insurance).
            var traj = new MockTrajectory { Points = null };
            Assert.False(TrajectoryMath.HasReentryPotential(traj));
        }
    }
}
