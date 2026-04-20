using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class BallisticExtrapolatorTests
    {
        private const double KerbinRadius = 600000.0;
        private const double KerbinAtmosphereDepth = 70000.0;
        private const double KerbinGravParameter = 3.5316e12;
        private const double KerbinSoi = 1500000.0;

        private const double MunRadius = 200000.0;
        private const double MunGravParameter = 6.5138398e10;

        private const double StarRadius = 1000000.0;
        private const double StarGravParameter = 1.0e14;

        [Theory]
        [InlineData((int)Vessel.Situations.LANDED)]
        [InlineData((int)Vessel.Situations.SPLASHED)]
        [InlineData((int)Vessel.Situations.PRELAUNCH)]
        [InlineData((int)Vessel.Situations.DOCKED)]
        public void ShouldExtrapolate_SurfaceAndHeldStates_ReturnFalse(int situationValue)
        {
            bool result = BallisticExtrapolator.ShouldExtrapolate(
                (Vessel.Situations)situationValue,
                eccentricity: 0.0,
                periapsisAltitude: 100000.0,
                cutoffAltitude: KerbinAtmosphereDepth);

            Assert.False(result);
        }

        [Theory]
        [InlineData((int)Vessel.Situations.FLYING)]
        [InlineData((int)Vessel.Situations.SUB_ORBITAL)]
        [InlineData((int)Vessel.Situations.ESCAPING)]
        public void ShouldExtrapolate_UnstableFlightStates_ReturnTrue(int situationValue)
        {
            bool result = BallisticExtrapolator.ShouldExtrapolate(
                (Vessel.Situations)situationValue,
                eccentricity: 0.25,
                periapsisAltitude: 100000.0,
                cutoffAltitude: KerbinAtmosphereDepth);

            Assert.True(result);
        }

        [Fact]
        public void ShouldExtrapolate_OrbitingStable_ReturnsFalse()
        {
            bool result = BallisticExtrapolator.ShouldExtrapolate(
                Vessel.Situations.ORBITING,
                eccentricity: 0.01,
                periapsisAltitude: 120000.0,
                cutoffAltitude: KerbinAtmosphereDepth);

            Assert.False(result);
        }

        [Fact]
        public void ShouldExtrapolate_OrbitingDecayingIntoAtmo_ReturnsTrue()
        {
            bool result = BallisticExtrapolator.ShouldExtrapolate(
                Vessel.Situations.ORBITING,
                eccentricity: 0.1,
                periapsisAltitude: 45000.0,
                cutoffAltitude: KerbinAtmosphereDepth);

            Assert.True(result);
        }

        [Fact]
        public void Extrapolate_SuborbitalKerbin_TerminatesAtAtmoEntry()
        {
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody(
                    "Kerbin",
                    KerbinGravParameter,
                    KerbinRadius,
                    atmosphereDepth: KerbinAtmosphereDepth)
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                MakeTangentialState("Kerbin", KerbinRadius, altitude: 100000.0, tangentialSpeed: 2100.0),
                bodies,
                new ExtrapolationLimits
                {
                    maxHorizonYears = 0.01,
                    maxSoiTransitions = 4,
                    soiSampleStep = 60.0
                });

            Assert.Equal(TerminalState.Destroyed, result.terminalState);
            Assert.Equal("Kerbin", result.terminalBodyName);
            Assert.Single(result.segments);
            Assert.Equal(result.terminalUT, result.segments[0].endUT, 6);

            double terminalAltitude = Altitude(result.terminalPosition, KerbinRadius);
            Assert.InRange(terminalAltitude, KerbinAtmosphereDepth - 100.0, KerbinAtmosphereDepth + 100.0);
            Assert.Equal(ExtrapolationFailureReason.None, result.failureReason);
        }

        [Fact]
        public void Extrapolate_InAtmoPlaneArc_TerminatesAtGround()
        {
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody(
                    "Kerbin",
                    KerbinGravParameter,
                    KerbinRadius,
                    atmosphereDepth: KerbinAtmosphereDepth)
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                MakeTangentialState("Kerbin", KerbinRadius, altitude: 5000.0, tangentialSpeed: 400.0),
                bodies,
                new ExtrapolationLimits
                {
                    maxHorizonYears = 0.005,
                    maxSoiTransitions = 4,
                    soiSampleStep = 60.0
                });

            Assert.Equal(TerminalState.Destroyed, result.terminalState);
            Assert.Equal("Kerbin", result.terminalBodyName);
            Assert.Single(result.segments);
            Assert.True(result.terminalUT > 0.0);

            double terminalAltitude = Altitude(result.terminalPosition, KerbinRadius);
            Assert.InRange(terminalAltitude, -1.0, 100.0);
        }

        [Fact]
        public void Extrapolate_StableOrbit_HitsHorizonCap()
        {
            double circularAltitude = 100000.0;
            double radius = KerbinRadius + circularAltitude;
            double circularSpeed = Math.Sqrt(KerbinGravParameter / radius);
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody("Kerbin", KerbinGravParameter, KerbinRadius, atmosphereDepth: KerbinAtmosphereDepth)
            };

            ExtrapolationLimits limits = new ExtrapolationLimits
            {
                maxHorizonYears = 0.0001,
                maxSoiTransitions = 4,
                soiSampleStep = 60.0
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                MakeTangentialState("Kerbin", KerbinRadius, circularAltitude, circularSpeed),
                bodies,
                limits);

            double expectedTerminalUT = limits.maxHorizonYears * 365.0 * 24.0 * 60.0 * 60.0;
            Assert.Equal(TerminalState.Orbiting, result.terminalState);
            Assert.Single(result.segments);
            Assert.Equal(expectedTerminalUT, result.terminalUT, 4);
            Assert.Equal(expectedTerminalUT, result.segments[0].endUT, 4);
            Assert.Equal("Kerbin", result.terminalBodyName);
            Assert.True(result.segments[0].isPredicted);
            Assert.InRange(Altitude(result.terminalPosition, KerbinRadius), circularAltitude - 5.0, circularAltitude + 5.0);
        }

        [Fact]
        public void Extrapolate_HyperbolicHomeEscape_HandsOffToStar()
        {
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Star"] = MakeBody("Star", StarGravParameter, StarRadius),
                ["Home"] = MakeBody(
                    "Home",
                    KerbinGravParameter,
                    KerbinRadius,
                    atmosphereDepth: KerbinAtmosphereDepth,
                    sphereOfInfluence: KerbinSoi,
                    parentBodyName: "Star",
                    parentFrameState: FixedState(
                        new Vector3d(200000000.0, 0.0, 0.0),
                        new Vector3d(0.0, 200.0, 0.0)))
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                MakeTangentialState("Home", KerbinRadius, altitude: 100000.0, tangentialSpeed: 3600.0),
                bodies,
                new ExtrapolationLimits
                {
                    maxHorizonYears = 0.0005,
                    maxSoiTransitions = 4,
                    soiSampleStep = 60.0
                });

            Assert.Equal(TerminalState.Orbiting, result.terminalState);
            Assert.Equal("Star", result.terminalBodyName);
            Assert.Equal(2, result.segments.Count);
            Assert.Equal("Home", result.segments[0].bodyName);
            Assert.Equal("Star", result.segments[1].bodyName);
            Assert.Equal(result.segments[0].endUT, result.segments[1].startUT, 6);
            Assert.True(result.segments[0].endUT < result.terminalUT);
        }

        [Fact]
        public void Extrapolate_MaxSoiTransitionsReached_TerminatesAtCap()
        {
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Star"] = MakeBody("Star", StarGravParameter, StarRadius),
                ["Home"] = MakeBody(
                    "Home",
                    KerbinGravParameter,
                    KerbinRadius,
                    atmosphereDepth: KerbinAtmosphereDepth,
                    sphereOfInfluence: KerbinSoi,
                    parentBodyName: "Star",
                    parentFrameState: FixedState(
                        new Vector3d(200000000.0, 0.0, 0.0),
                        new Vector3d(0.0, 200.0, 0.0)))
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                MakeTangentialState("Home", KerbinRadius, altitude: 100000.0, tangentialSpeed: 3600.0),
                bodies,
                new ExtrapolationLimits
                {
                    maxHorizonYears = 0.0005,
                    maxSoiTransitions = 1,
                    soiSampleStep = 60.0
                });

            Assert.Equal(TerminalState.Orbiting, result.terminalState);
            Assert.Equal("Home", result.terminalBodyName);
            Assert.Single(result.segments);
            Assert.Equal(result.segments[0].endUT, result.terminalUT, 6);
        }

        [Fact]
        public void Extrapolate_NonAtmoBody_PqsUnavailable_UsesSeaLevel()
        {
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Mun"] = MakeBody("Mun", MunGravParameter, MunRadius)
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                MakeTangentialState("Mun", MunRadius, altitude: 10000.0, tangentialSpeed: 150.0),
                bodies,
                new ExtrapolationLimits
                {
                    maxHorizonYears = 0.01,
                    maxSoiTransitions = 2,
                    soiSampleStep = 60.0
                });

            Assert.Equal(TerminalState.Destroyed, result.terminalState);
            Assert.Equal(ExtrapolationFailureReason.PqsUnavailable, result.failureReason);
            Assert.Equal("Mun", result.terminalBodyName);
            Assert.InRange(Altitude(result.terminalPosition, MunRadius), -1.0, 100.0);
        }

        [Fact]
        public void Extrapolate_TerrainAltitude_SamplesAlongCrossingAndUsesActualSurface()
        {
            int terrainLookups = 0;
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Mun"] = MakeBody(
                    "Mun",
                    MunGravParameter,
                    MunRadius,
                    terrainAltitude: (double latitude, double longitude, out double altitude) =>
                    {
                        terrainLookups++;
                        altitude = 2500.0;
                        return true;
                    })
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                MakeTangentialState("Mun", MunRadius, altitude: 10000.0, tangentialSpeed: 150.0),
                bodies,
                new ExtrapolationLimits
                {
                    maxHorizonYears = 0.01,
                    maxSoiTransitions = 2,
                    soiSampleStep = 60.0
                });

            Assert.Equal(TerminalState.Destroyed, result.terminalState);
            Assert.Equal(ExtrapolationFailureReason.None, result.failureReason);
            Assert.True(terrainLookups > 1);
            Assert.InRange(Altitude(result.terminalPosition, MunRadius), 2400.0, 2600.0);
        }

        [Fact]
        public void Extrapolate_ParentExitWithoutResolver_FailsCleanly()
        {
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Star"] = MakeBody("Star", StarGravParameter, StarRadius),
                ["Home"] = MakeBody(
                    "Home",
                    KerbinGravParameter,
                    KerbinRadius,
                    atmosphereDepth: KerbinAtmosphereDepth,
                    sphereOfInfluence: KerbinSoi,
                    parentBodyName: "Star")
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                MakeTangentialState("Home", KerbinRadius, altitude: 100000.0, tangentialSpeed: 3600.0),
                bodies,
                new ExtrapolationLimits
                {
                    maxHorizonYears = 0.0005,
                    maxSoiTransitions = 4,
                    soiSampleStep = 60.0
                });

            Assert.Equal(ExtrapolationFailureReason.MissingParentFrameResolver, result.failureReason);
            Assert.Equal(TerminalState.Orbiting, result.terminalState);
            Assert.Equal("Home", result.terminalBodyName);
            Assert.Single(result.segments);
            Assert.Equal(result.segments[0].endUT, result.terminalUT, 6);
        }

        [Fact]
        public void Extrapolate_DegenerateStateVector_ReturnsFailureReason()
        {
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody("Kerbin", KerbinGravParameter, KerbinRadius, atmosphereDepth: KerbinAtmosphereDepth)
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                new BallisticStateVector
                {
                    ut = 123.0,
                    bodyName = "Kerbin",
                    position = new Vector3d(KerbinRadius + 1000.0, 0.0, 0.0),
                    velocity = Vector3d.zero
                },
                bodies);

            Assert.Equal(ExtrapolationFailureReason.DegenerateStateVector, result.failureReason);
            Assert.Empty(result.segments);
            Assert.Equal(123.0, result.terminalUT, 6);
            Assert.Equal("Kerbin", result.terminalBodyName);
        }

        [Fact]
        public void TryPropagate_ProducedSegment_RecreatesTerminalState()
        {
            double circularAltitude = 100000.0;
            double radius = KerbinRadius + circularAltitude;
            double circularSpeed = Math.Sqrt(KerbinGravParameter / radius);
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody("Kerbin", KerbinGravParameter, KerbinRadius, atmosphereDepth: KerbinAtmosphereDepth)
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                MakeTangentialState("Kerbin", KerbinRadius, circularAltitude, circularSpeed),
                bodies,
                new ExtrapolationLimits
                {
                    maxHorizonYears = 0.0001,
                    maxSoiTransitions = 2,
                    soiSampleStep = 60.0
                });

            Assert.True(BallisticExtrapolator.TryPropagate(
                result.segments[0],
                KerbinGravParameter,
                result.terminalUT,
                out Vector3d propagatedPosition,
                out Vector3d propagatedVelocity));

            Assert.True(Distance(propagatedPosition, result.terminalPosition) < 1.0);
            Assert.True(Distance(propagatedVelocity, result.terminalVelocity) < 0.01);
        }

        private static BallisticStateVector MakeTangentialState(
            string bodyName,
            double bodyRadius,
            double altitude,
            double tangentialSpeed,
            double radialSpeed = 0.0)
        {
            return new BallisticStateVector
            {
                ut = 0.0,
                bodyName = bodyName,
                position = new Vector3d(bodyRadius + altitude, 0.0, 0.0),
                velocity = new Vector3d(radialSpeed, tangentialSpeed, 0.0)
            };
        }

        private static ExtrapolationBody MakeBody(
            string name,
            double gravParameter,
            double radius,
            double atmosphereDepth = 0.0,
            double sphereOfInfluence = 0.0,
            string parentBodyName = null,
            TerrainAltitudeResolver terrainAltitude = null,
            ParentFrameStateResolver parentFrameState = null)
        {
            return new ExtrapolationBody
            {
                Name = name,
                ParentBodyName = parentBodyName,
                GravitationalParameter = gravParameter,
                Radius = radius,
                AtmosphereDepth = atmosphereDepth,
                SphereOfInfluence = sphereOfInfluence,
                TerrainAltitude = terrainAltitude,
                ParentFrameState = parentFrameState
            };
        }

        private static ParentFrameStateResolver FixedState(Vector3d position, Vector3d velocity)
        {
            return (double ut, out Vector3d bodyPosition, out Vector3d bodyVelocity) =>
            {
                bodyPosition = position;
                bodyVelocity = velocity;
            };
        }

        private static double Altitude(Vector3d position, double radius)
        {
            return Math.Sqrt(position.x * position.x + position.y * position.y + position.z * position.z) - radius;
        }

        private static double Distance(Vector3d a, Vector3d b)
        {
            Vector3d delta = a - b;
            return Math.Sqrt(delta.x * delta.x + delta.y * delta.y + delta.z * delta.z);
        }
    }
}
