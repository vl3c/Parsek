using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class BallisticExtrapolatorTests : IDisposable
    {
        private const double KerbinRadius = 600000.0;
        private const double KerbinAtmosphereDepth = 70000.0;
        private const double KerbinGravParameter = 3.5316e12;
        private const double KerbinSoi = 1500000.0;

        private const double MunRadius = 200000.0;
        private const double MunGravParameter = 6.5138398e10;

        private const double StarRadius = 1000000.0;
        private const double StarGravParameter = 1.0e14;

        private readonly List<string> logLines = new List<string>();

        public BallisticExtrapolatorTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

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
        public void Extrapolate_SubSurfaceStart_ClassifiesAsDestroyedWithoutRunningScan()
        {
            // Regression for the post-v0.8.3 booster misclassification: when
            // `PatchedConicSnapshot` fails with `NullSolver` (KSP torn down
            // the solver for a destroyed vessel), the finalizer's live-orbit
            // fallback yields a position collapsed to the body frame origin
            // (altitude ≈ -body radius). Without the sub-surface guard, the
            // extrapolator scanned for a non-existent surface intersection,
            // horizon-capped, and silently returned Orbiting. It must now
            // classify the recording as Destroyed immediately.
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody("Kerbin", KerbinGravParameter, KerbinRadius, atmosphereDepth: KerbinAtmosphereDepth)
            };

            // Mirror the playtest log's fingerprint: alt = -594383 m.
            var startState = new BallisticStateVector
            {
                ut = 743.238,
                bodyName = "Kerbin",
                position = new Vector3d(KerbinRadius + -594383.0, 0.0, 0.0),
                velocity = new Vector3d(0.0, 50.0, 0.0)
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(startState, bodies);

            Assert.Equal(TerminalState.Destroyed, result.terminalState);
            Assert.Equal(ExtrapolationFailureReason.SubSurfaceStart, result.failureReason);
            Assert.Equal(startState.ut, result.terminalUT);
            Assert.Equal("Kerbin", result.terminalBodyName);
            Assert.Empty(result.segments);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN][Extrapolator]")
                && l.Contains("Start rejected: sub-surface state")
                && l.Contains("body=Kerbin")
                && l.Contains("classifying recording as Destroyed"));
        }

        [Fact]
        public void Extrapolate_SurfaceGrazingStart_DoesNotTripSubSurfaceGuard()
        {
            // Guard must not mis-fire on a legitimate sea-level start state:
            // altitude 0 is ON the surface, above the -100 m threshold.
            // Trajectory should still be scanned normally (terminal state
            // depends on the tangential speed — this test just pins the
            // absence of the sub-surface short-circuit).
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody("Kerbin", KerbinGravParameter, KerbinRadius, atmosphereDepth: KerbinAtmosphereDepth)
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                MakeTangentialState("Kerbin", KerbinRadius, altitude: 0.0, tangentialSpeed: 400.0),
                bodies,
                new ExtrapolationLimits
                {
                    maxHorizonYears = 0.005,
                    maxSoiTransitions = 4,
                    soiSampleStep = 60.0
                });

            Assert.NotEqual(ExtrapolationFailureReason.SubSurfaceStart, result.failureReason);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Extrapolator]") && l.Contains("Start rejected: sub-surface state"));
        }

        [Fact]
        public void Extrapolate_JustAboveThresholdStart_DoesNotTripSubSurfaceGuard()
        {
            // Start state at alt = -50 m (above the -100 m threshold): e.g. a
            // vessel momentarily dipping into a canyon or below the sea
            // reference ellipsoid. Guard must not trip.
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody("Kerbin", KerbinGravParameter, KerbinRadius, atmosphereDepth: KerbinAtmosphereDepth)
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                MakeTangentialState("Kerbin", KerbinRadius, altitude: -50.0, tangentialSpeed: 400.0),
                bodies);

            Assert.NotEqual(ExtrapolationFailureReason.SubSurfaceStart, result.failureReason);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Extrapolator]") && l.Contains("Start rejected: sub-surface state"));
        }

        [Fact]
        public void Extrapolate_JustBelowThresholdStart_TripsSubSurfaceGuard()
        {
            // Start state at alt = -150 m (below the -100 m threshold): clearly
            // not a real trajectory. Should classify as Destroyed.
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody("Kerbin", KerbinGravParameter, KerbinRadius, atmosphereDepth: KerbinAtmosphereDepth)
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                MakeTangentialState("Kerbin", KerbinRadius, altitude: -150.0, tangentialSpeed: 400.0),
                bodies);

            Assert.Equal(TerminalState.Destroyed, result.terminalState);
            Assert.Equal(ExtrapolationFailureReason.SubSurfaceStart, result.failureReason);
            Assert.Empty(result.segments);
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

        // ---- TryFindAtmosphericReentryClip: clip a re-entering predicted orbit
        // tail (sub-orbital impact arc captured as a closed ellipse) at the
        // descending atmosphere-entry crossing, so playback hands the descent to
        // the ballistic extrapolator instead of flying the ellipse underground.

        // Real "Kerbal X" capsule geometry: apoapsis ~98.7 km above Kerbin,
        // periapsis ~598 km below the surface.
        private const double SuborbitalImpactSma = 350210.756;
        private const double SuborbitalImpactEcc = 0.99519589;

        private static OrbitSegment MakeOrbitSegment(
            string bodyName,
            double startUT,
            double endUT,
            double semiMajorAxis,
            double eccentricity,
            double meanAnomalyAtEpoch,
            double epoch)
        {
            return new OrbitSegment
            {
                bodyName = bodyName,
                startUT = startUT,
                endUT = endUT,
                semiMajorAxis = semiMajorAxis,
                eccentricity = eccentricity,
                meanAnomalyAtEpoch = meanAnomalyAtEpoch,
                epoch = epoch,
                isPredicted = true
            };
        }

        [Fact]
        public void TryFindAtmosphericReentryClip_SuborbitalReentry_ClipsAtAtmosphereEntry()
        {
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody(
                    "Kerbin", KerbinGravParameter, KerbinRadius, atmosphereDepth: KerbinAtmosphereDepth)
            };
            // Epoch at apoapsis (mean anomaly = PI) so the segment starts well
            // above the atmosphere and descends through the boundary.
            var segments = new List<OrbitSegment>
            {
                MakeOrbitSegment(
                    "Kerbin", startUT: 0.0, endUT: 693.0,
                    SuborbitalImpactSma, SuborbitalImpactEcc,
                    meanAnomalyAtEpoch: Math.PI, epoch: 0.0)
            };

            bool clipped = BallisticExtrapolator.TryFindAtmosphericReentryClip(
                segments, bodies, out int clipIndex, out double entryUT);

            Assert.True(clipped);
            Assert.Equal(0, clipIndex);
            Assert.InRange(entryUT, 0.0, 693.0);

            // The crossing must sit exactly at the atmosphere top.
            Assert.True(BallisticExtrapolator.TryPropagate(
                segments[0], KerbinGravParameter, entryUT, out Vector3d pos, out _));
            double altitude = Altitude(pos, KerbinRadius);
            Assert.InRange(altitude, KerbinAtmosphereDepth - 50.0, KerbinAtmosphereDepth + 50.0);
        }

        [Fact]
        public void TryFindAtmosphericReentryClip_GenuineOrbitalCoast_DoesNotClip()
        {
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody(
                    "Kerbin", KerbinGravParameter, KerbinRadius, atmosphereDepth: KerbinAtmosphereDepth)
            };
            // Periapsis 686 km (above the 670 km atmosphere top): a real orbit.
            var segments = new List<OrbitSegment>
            {
                MakeOrbitSegment(
                    "Kerbin", startUT: 0.0, endUT: 5000.0,
                    semiMajorAxis: 700000.0, eccentricity: 0.02,
                    meanAnomalyAtEpoch: Math.PI, epoch: 0.0)
            };

            bool clipped = BallisticExtrapolator.TryFindAtmosphericReentryClip(
                segments, bodies, out int clipIndex, out _);

            Assert.False(clipped);
            Assert.Equal(-1, clipIndex);
        }

        [Fact]
        public void TryFindAtmosphericReentryClip_PeriapsisJustAboveAtmosphere_DoesNotClip()
        {
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody(
                    "Kerbin", KerbinGravParameter, KerbinRadius, atmosphereDepth: KerbinAtmosphereDepth)
            };
            // Periapsis 673.2 km, grazing just above the 670 km boundary.
            var segments = new List<OrbitSegment>
            {
                MakeOrbitSegment(
                    "Kerbin", startUT: 0.0, endUT: 5000.0,
                    semiMajorAxis: 680000.0, eccentricity: 0.01,
                    meanAnomalyAtEpoch: Math.PI, epoch: 0.0)
            };

            bool clipped = BallisticExtrapolator.TryFindAtmosphericReentryClip(
                segments, bodies, out _, out _);

            Assert.False(clipped);
        }

        [Fact]
        public void TryFindAtmosphericReentryClip_AirlessBody_DoesNotClip()
        {
            // Sub-surface periapsis on an airless body is handled by the
            // solver-impact short-circuit, not this atmospheric clip.
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Mun"] = MakeBody("Mun", MunGravParameter, MunRadius, atmosphereDepth: 0.0)
            };
            // Apoapsis 270 km above Mun, periapsis 90 km below its surface.
            var segments = new List<OrbitSegment>
            {
                MakeOrbitSegment(
                    "Mun", startUT: 0.0, endUT: 5000.0,
                    semiMajorAxis: 180000.0, eccentricity: 0.5,
                    meanAnomalyAtEpoch: Math.PI, epoch: 0.0)
            };

            bool clipped = BallisticExtrapolator.TryFindAtmosphericReentryClip(
                segments, bodies, out int clipIndex, out _);

            Assert.False(clipped);
            Assert.Equal(-1, clipIndex);
        }

        [Fact]
        public void TryFindAtmosphericReentryClip_SecondSegmentReenters_ClipsLaterSegment()
        {
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody(
                    "Kerbin", KerbinGravParameter, KerbinRadius, atmosphereDepth: KerbinAtmosphereDepth)
            };
            // Segment 0: a genuine coast (periapsis above atmosphere). Segment 1:
            // the sub-orbital re-entry. The clip must skip the coast and fire on
            // segment 1.
            var segments = new List<OrbitSegment>
            {
                MakeOrbitSegment(
                    "Kerbin", startUT: 0.0, endUT: 100.0,
                    semiMajorAxis: 700000.0, eccentricity: 0.02,
                    meanAnomalyAtEpoch: Math.PI, epoch: 0.0),
                MakeOrbitSegment(
                    "Kerbin", startUT: 100.0, endUT: 793.0,
                    SuborbitalImpactSma, SuborbitalImpactEcc,
                    meanAnomalyAtEpoch: Math.PI, epoch: 100.0)
            };

            bool clipped = BallisticExtrapolator.TryFindAtmosphericReentryClip(
                segments, bodies, out int clipIndex, out double entryUT);

            Assert.True(clipped);
            Assert.Equal(1, clipIndex);
            Assert.InRange(entryUT, 100.0, 793.0);
        }

        [Fact]
        public void TryFindAtmosphericReentryClip_SegmentStartsBelowAtmosphere_ClipsAtSegmentStart()
        {
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody(
                    "Kerbin", KerbinGravParameter, KerbinRadius, atmosphereDepth: KerbinAtmosphereDepth)
            };
            // KSP can capture a patch that begins on the descending arc already
            // inside the atmosphere (mean anomaly past the descending boundary
            // crossing). The whole segment is a re-entry, so the clip is its start.
            var segments = new List<OrbitSegment>
            {
                MakeOrbitSegment(
                    "Kerbin", startUT: 0.0, endUT: 300.0,
                    SuborbitalImpactSma, SuborbitalImpactEcc,
                    meanAnomalyAtEpoch: 4.5, epoch: 0.0)
            };

            // Sanity: the segment really does start below the 670 km boundary.
            Assert.True(BallisticExtrapolator.TryPropagate(
                segments[0], KerbinGravParameter, 0.0, out Vector3d startPos, out _));
            double startAltitude = Altitude(startPos, KerbinRadius);
            Assert.True(startAltitude < KerbinAtmosphereDepth,
                $"test fixture start altitude {startAltitude} should be below the atmosphere top");

            bool clipped = BallisticExtrapolator.TryFindAtmosphericReentryClip(
                segments, bodies, out int clipIndex, out double entryUT);

            Assert.True(clipped);
            Assert.Equal(0, clipIndex);
            Assert.Equal(0.0, entryUT, 6); // clipped at the segment start
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
            Assert.Contains(logLines, l => l.Contains("[Extrapolator]") && l.Contains("Start: body=Kerbin"));
            Assert.Contains(logLines, l => l.Contains("Terminal reason=horizon-cap"));
        }

        [Fact]
        public void Extrapolate_StableOrbit_CarriesFrozenPlaybackRotationIntoPredictedSegment()
        {
            double circularAltitude = 100000.0;
            double radius = KerbinRadius + circularAltitude;
            double circularSpeed = Math.Sqrt(KerbinGravParameter / radius);
            Quaternion frozenRotation = new Quaternion(0.1f, 0.7f, -0.1f, -0.7f);
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody("Kerbin", KerbinGravParameter, KerbinRadius, atmosphereDepth: KerbinAtmosphereDepth)
            };

            BallisticStateVector startState = MakeTangentialState(
                "Kerbin",
                KerbinRadius,
                circularAltitude,
                circularSpeed);
            startState.orbitalFrameRotation = frozenRotation;

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                startState,
                bodies,
                new ExtrapolationLimits
                {
                    maxHorizonYears = 0.0001,
                    maxSoiTransitions = 4,
                    soiSampleStep = 60.0
                });

            Assert.Single(result.segments);
            Assert.True(TrajectoryMath.HasOrbitalFrameRotation(result.segments[0]));
            AssertQuaternionEqual(frozenRotation, result.segments[0].orbitalFrameRotation);
        }

        [Fact]
        public void ComputeOrbitalFrameRotationFromState_RoundTripsWorldRotation()
        {
            Quaternion worldRotation = new Quaternion(0.2f, -0.6f, 0.3f, 0.7f);
            Vector3d position = new Vector3d(KerbinRadius + 100000.0, 0.0, 0.0);
            Vector3d velocity = new Vector3d(0.0, 2200.0, 0.0);

            Quaternion orbitalFrameRotation = BallisticExtrapolator.ComputeOrbitalFrameRotationFromState(
                worldRotation,
                position,
                velocity);
            Quaternion resolvedWorldRotation = BallisticExtrapolator.ResolveWorldRotation(
                orbitalFrameRotation,
                position,
                velocity);

            AssertQuaternionEqual(worldRotation, resolvedWorldRotation);
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
        public void Extrapolate_HyperbolicHomeEscape_PreservesParentSegmentStartState()
        {
            ParentFrameStateResolver fixedParentState = FixedState(
                new Vector3d(200000000.0, 0.0, 0.0),
                new Vector3d(0.0, 200.0, 0.0));
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
                    parentFrameState: fixedParentState)
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

            Assert.Equal(2, result.segments.Count);
            OrbitSegment homeSegment = result.segments[0];
            OrbitSegment starSegment = result.segments[1];

            Assert.True(BallisticExtrapolator.TryPropagate(
                homeSegment,
                KerbinGravParameter,
                homeSegment.endUT,
                out Vector3d homeBoundaryPosition,
                out Vector3d homeBoundaryVelocity));
            fixedParentState(
                homeSegment.endUT,
                out Vector3d homeBodyPosition,
                out Vector3d homeBodyVelocity);
            Assert.True(BallisticExtrapolator.TryPropagate(
                starSegment,
                StarGravParameter,
                starSegment.startUT,
                out Vector3d starBoundaryPosition,
                out Vector3d starBoundaryVelocity));

            Assert.True(Distance(homeBoundaryPosition + homeBodyPosition, starBoundaryPosition) < 1.0);
            Assert.True(Distance(homeBoundaryVelocity + homeBodyVelocity, starBoundaryVelocity) < 0.01);
        }

        [Fact]
        public void Extrapolate_SoiTransitions_PreserveFrozenPlaybackWorldRotationAcrossSegments()
        {
            Quaternion frozenRotation = new Quaternion(-0.6f, -0.3f, 0.6f, 0.3f);
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

            BallisticStateVector startState = MakeTangentialState(
                "Home",
                KerbinRadius,
                altitude: 100000.0,
                tangentialSpeed: 3600.0);
            startState.orbitalFrameRotation = frozenRotation;

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                startState,
                bodies,
                new ExtrapolationLimits
                {
                    maxHorizonYears = 0.0005,
                    maxSoiTransitions = 4,
                    soiSampleStep = 60.0
                });

            Assert.Equal(2, result.segments.Count);
            OrbitSegment homeSegment = result.segments[0];
            OrbitSegment starSegment = result.segments[1];

            Assert.True(TrajectoryMath.HasOrbitalFrameRotation(homeSegment));
            Assert.True(TrajectoryMath.HasOrbitalFrameRotation(starSegment));
            Assert.True(BallisticExtrapolator.TryPropagate(
                homeSegment,
                KerbinGravParameter,
                homeSegment.endUT,
                out Vector3d homeBoundaryPosition,
                out Vector3d homeBoundaryVelocity));
            Assert.True(BallisticExtrapolator.TryPropagate(
                starSegment,
                StarGravParameter,
                starSegment.startUT,
                out Vector3d starBoundaryPosition,
                out Vector3d starBoundaryVelocity));

            Quaternion homeWorldRotation = BallisticExtrapolator.ResolveWorldRotation(
                homeSegment.orbitalFrameRotation,
                homeBoundaryPosition,
                homeBoundaryVelocity);
            Quaternion starWorldRotation = BallisticExtrapolator.ResolveWorldRotation(
                starSegment.orbitalFrameRotation,
                starBoundaryPosition,
                starBoundaryVelocity);

            AssertQuaternionEqual(homeWorldRotation, starWorldRotation);
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
            Assert.Contains(logLines, l => l.Contains("Surface fallback: body=Mun reason=no-terrain-resolver -> sea-level"));
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
            Assert.Contains(logLines, l => l.Contains("Terminal reason=missing-parent-frame-resolver"));
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
            // Recoverable terminal (callers handle the failure reason), so it is logged
            // at Warn, not Error -- matching the other "couldn't fully extrapolate"
            // terminal reasons.
            Assert.Contains(logLines, l =>
                l.Contains("Terminal reason=degenerate-state") && l.Contains("[WARN]"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("Terminal reason=degenerate-state") && l.Contains("[ERROR]"));
        }

        [Fact]
        public void Extrapolate_ImmediateParentExit_HandsOffToParent()
        {
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Star"] = MakeBody("Star", StarGravParameter, StarRadius),
                ["Home"] = MakeBody(
                    "Home",
                    KerbinGravParameter,
                    KerbinRadius,
                    sphereOfInfluence: KerbinSoi,
                    parentBodyName: "Star",
                    parentFrameState: FixedState(
                        new Vector3d(200000000.0, 0.0, 0.0),
                        new Vector3d(0.0, 200.0, 0.0)))
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                new BallisticStateVector
                {
                    ut = 42.0,
                    bodyName = "Home",
                    position = new Vector3d(KerbinSoi + 0.0000005, 0.0, 0.0),
                    velocity = new Vector3d(0.0, 100.0, 0.0)
                },
                bodies,
                new ExtrapolationLimits
                {
                    maxHorizonYears = 0.001,
                    maxSoiTransitions = 4,
                    soiSampleStep = 60.0
                });

            Assert.Equal(TerminalState.Orbiting, result.terminalState);
            Assert.Equal("Star", result.terminalBodyName);
            Assert.Single(result.segments);
            Assert.Equal("Star", result.segments[0].bodyName);
            Assert.True(result.terminalUT > 42.0);
            Assert.Contains(logLines, l => l.Contains("SOI transition: child=Home parent=Star"));
            Assert.DoesNotContain(logLines, l => l.Contains("Terminal reason=zero-progress-guard"));
        }

        [Fact]
        public void Extrapolate_LongHorizonSeaLevelImpact_NarrowsSurfaceScan()
        {
            const double periapsisRadius = MunRadius - 1000.0;
            const double apoapsisRadius = MunRadius + 5000000.0;
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Mun"] = MakeBody("Mun", MunGravParameter, MunRadius)
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                MakeApoapsisState("Mun", apoapsisRadius, periapsisRadius, MunGravParameter),
                bodies,
                new ExtrapolationLimits
                {
                    maxHorizonYears = 0.01,
                    maxSoiTransitions = 2,
                    soiSampleStep = 60.0
                });

            Assert.Equal(TerminalState.Destroyed, result.terminalState);
            Assert.Equal(ExtrapolationFailureReason.PqsUnavailable, result.failureReason);
            Assert.InRange(Altitude(result.terminalPosition, MunRadius), -1.0, 100.0);
            Assert.Contains(logLines, l => l.Contains("Surface scan narrowed: body=Mun reason=sea-level"));
            Assert.Contains(logLines, l => l.Contains("Surface fallback: body=Mun reason=no-terrain-resolver -> sea-level"));
        }

        [Fact]
        public void Extrapolate_SurfaceCoordinatesResolver_OverridesFallbackLatitudeLongitude()
        {
            double observedLatitude = 0.0;
            double observedLongitude = 0.0;
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Mun"] = MakeBody(
                    "Mun",
                    MunGravParameter,
                    MunRadius,
                    terrainAltitude: (double latitude, double longitude, out double altitude) =>
                    {
                        observedLatitude = latitude;
                        observedLongitude = longitude;
                        altitude = 2500.0;
                        return true;
                    },
                    surfaceCoordinates: (double ut, Vector3d position, out double latitude, out double longitude) =>
                    {
                        latitude = 12.5;
                        longitude = -45.25;
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
            Assert.Equal(12.5, observedLatitude, 6);
            Assert.Equal(-45.25, observedLongitude, 6);
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

        // ---- OrbitSegment unit contract (see OrbitSegment.cs): inc/LAN/argPe are
        // DEGREES, meanAnomalyAtEpoch is RADIANS. These tests pin the extrapolator's
        // boundary conversion in both directions: recorder-authored degree segments
        // must propagate to correctly oriented positions, and extrapolator-authored
        // segments must come back out carrying degrees.

        [Fact]
        public void TryPropagate_DegreeLanSegment_PlacesEpochPositionOnRotatedAxis()
        {
            // Equatorial circular orbit with LAN=90 degrees, periapsis direction at
            // the node, vessel at periapsis: the epoch position must sit on the +y
            // axis. Reading 90 as radians would place it at an arbitrary in-plane
            // angle (90 rad wraps to ~116.6 degrees).
            double radius = 700000.0;
            var segment = new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 0.0,
                endUT = 1000.0,
                inclination = 0.0,
                eccentricity = 0.0,
                semiMajorAxis = radius,
                longitudeOfAscendingNode = 90.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = 0.0
            };

            Assert.True(BallisticExtrapolator.TryPropagate(
                segment, KerbinGravParameter, 0.0, out Vector3d position, out _));

            Assert.True(Math.Abs(position.x) < radius * 1e-9,
                $"x should be ~0 for LAN=90deg, was {position.x:F1}");
            Assert.True(Math.Abs(position.y - radius) < radius * 1e-9,
                $"y should be ~{radius:F0} for LAN=90deg, was {position.y:F1}");
            Assert.True(Math.Abs(position.z) < radius * 1e-9,
                $"z should be ~0 for an equatorial orbit, was {position.z:F1}");
        }

        [Fact]
        public void TryPropagate_DegreeInclinationSegment_ReachesPolarAxis()
        {
            // Polar circular orbit (inclination=90 degrees), quarter orbit past the
            // ascending node: the position must sit on the +z (polar) axis. Reading
            // 90 as radians tilts the plane to cos(90 rad) = -0.448 instead.
            double radius = 700000.0;
            var segment = new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 0.0,
                endUT = 1000.0,
                inclination = 90.0,
                eccentricity = 0.0,
                semiMajorAxis = radius,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = Math.PI / 2.0,
                epoch = 0.0
            };

            Assert.True(BallisticExtrapolator.TryPropagate(
                segment, KerbinGravParameter, 0.0, out Vector3d position, out _));

            Assert.True(Math.Abs(position.z - radius) < radius * 1e-9,
                $"z should be ~{radius:F0} a quarter orbit into a polar orbit, was {position.z:F1}");
            Assert.True(Math.Abs(position.x) < radius * 1e-9,
                $"x should be ~0, was {position.x:F1}");
            Assert.True(Math.Abs(position.y) < radius * 1e-9,
                $"y should be ~0, was {position.y:F1}");
        }

        [Fact]
        public void TryPropagate_DegreeArgumentOfPeriapsisSegment_RotatesPeriapsisAxis()
        {
            // Equatorial eccentric orbit with argPe=90 degrees, vessel at periapsis:
            // the epoch position must sit on the +y axis at the periapsis radius.
            // Reading 90 as radians rotates the periapsis to an arbitrary angle.
            double sma = 700000.0;
            double ecc = 0.2;
            double periapsisRadius = sma * (1.0 - ecc);
            var segment = new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 0.0,
                endUT = 1000.0,
                inclination = 0.0,
                eccentricity = ecc,
                semiMajorAxis = sma,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 90.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = 0.0
            };

            Assert.True(BallisticExtrapolator.TryPropagate(
                segment, KerbinGravParameter, 0.0, out Vector3d position, out _));

            Assert.True(Math.Abs(position.x) < periapsisRadius * 1e-9,
                $"x should be ~0 for argPe=90deg at periapsis, was {position.x:F1}");
            Assert.True(Math.Abs(position.y - periapsisRadius) < periapsisRadius * 1e-9,
                $"y should be ~{periapsisRadius:F0} for argPe=90deg at periapsis, was {position.y:F1}");
            Assert.True(Math.Abs(position.z) < periapsisRadius * 1e-9,
                $"z should be ~0 for an equatorial orbit, was {position.z:F1}");
        }

        [Fact]
        public void Extrapolate_ProducedSegments_CarryDegreeLanAndArgPe()
        {
            // Eccentric polar orbit seeded at periapsis ON the +z (polar) axis with
            // velocity along +x: h = (0, r*v, 0), so the ascending node points along
            // -x (LAN 180 degrees) and the periapsis sits a quarter turn above the
            // node (argPe 90 degrees). The produced segment must store 180 / 90
            // degrees, not pi / pi-over-2 radians.
            double periapsisAltitude = 100000.0;
            double radius = KerbinRadius + periapsisAltitude;
            double speed = 1.1 * Math.Sqrt(KerbinGravParameter / radius);
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody("Kerbin", KerbinGravParameter, KerbinRadius, atmosphereDepth: KerbinAtmosphereDepth)
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                new BallisticStateVector
                {
                    ut = 0.0,
                    bodyName = "Kerbin",
                    position = new Vector3d(0.0, 0.0, radius),
                    velocity = new Vector3d(speed, 0.0, 0.0)
                },
                bodies,
                new ExtrapolationLimits
                {
                    maxHorizonYears = 0.0001,
                    maxSoiTransitions = 2,
                    soiSampleStep = 60.0
                });

            Assert.True(result.segments.Count > 0, "expected at least one produced segment");
            OrbitSegment produced = result.segments[0];
            Assert.True(Math.Abs(produced.inclination - 90.0) < 1e-6,
                $"produced inclination should be 90 degrees, was {produced.inclination:F6}");
            Assert.True(Math.Abs(produced.longitudeOfAscendingNode - 180.0) < 1e-6,
                $"produced LAN should be 180 degrees, was {produced.longitudeOfAscendingNode:F6}");
            Assert.True(Math.Abs(produced.argumentOfPeriapsis - 90.0) < 1e-6,
                $"produced argPe should be 90 degrees, was {produced.argumentOfPeriapsis:F6}");
        }

        [Fact]
        public void Extrapolate_ProducedSegments_CarryDegreeInclination()
        {
            // Seed a circular orbit in the xz-plane: angular momentum lies in the
            // equatorial plane, so the orbit is polar (inclination 90). The
            // produced segment must store 90 (degrees), not pi/2 (radians).
            double circularAltitude = 100000.0;
            double radius = KerbinRadius + circularAltitude;
            double circularSpeed = Math.Sqrt(KerbinGravParameter / radius);
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody("Kerbin", KerbinGravParameter, KerbinRadius, atmosphereDepth: KerbinAtmosphereDepth)
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                new BallisticStateVector
                {
                    ut = 0.0,
                    bodyName = "Kerbin",
                    position = new Vector3d(radius, 0.0, 0.0),
                    velocity = new Vector3d(0.0, 0.0, circularSpeed)
                },
                bodies,
                new ExtrapolationLimits
                {
                    maxHorizonYears = 0.0001,
                    maxSoiTransitions = 2,
                    soiSampleStep = 60.0
                });

            Assert.True(result.segments.Count > 0, "expected at least one produced segment");
            OrbitSegment produced = result.segments[0];
            Assert.True(Math.Abs(produced.inclination - 90.0) < 1e-6,
                $"produced inclination should be 90 degrees, was {produced.inclination:F6}");
            Assert.InRange(produced.longitudeOfAscendingNode, 0.0, 360.0);
            Assert.InRange(produced.argumentOfPeriapsis, 0.0, 360.0);
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

        private static BallisticStateVector MakeApoapsisState(
            string bodyName,
            double apoapsisRadius,
            double periapsisRadius,
            double gravParameter)
        {
            double semiMajorAxis = (apoapsisRadius + periapsisRadius) * 0.5;
            double tangentialSpeed = Math.Sqrt(
                gravParameter * ((2.0 / apoapsisRadius) - (1.0 / semiMajorAxis)));
            return new BallisticStateVector
            {
                ut = 0.0,
                bodyName = bodyName,
                position = new Vector3d(apoapsisRadius, 0.0, 0.0),
                velocity = new Vector3d(0.0, tangentialSpeed, 0.0)
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
            ParentFrameStateResolver parentFrameState = null,
            SurfaceCoordinatesResolver surfaceCoordinates = null)
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
                ParentFrameState = parentFrameState,
                SurfaceCoordinates = surfaceCoordinates
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

        private static void AssertQuaternionEqual(Quaternion expected, Quaternion actual, float tolerance = 0.0001f)
        {
            expected = NormalizeAndCanonicalizeQuaternion(expected);
            actual = NormalizeAndCanonicalizeQuaternion(actual);
            float dot = Mathf.Abs(
                (expected.x * actual.x)
                + (expected.y * actual.y)
                + (expected.z * actual.z)
                + (expected.w * actual.w));
            Assert.True(
                1f - dot < tolerance,
                $"dot={dot} expected={expected} actual={actual}");
        }

        private static Quaternion NormalizeAndCanonicalizeQuaternion(Quaternion quaternion)
        {
            float magnitude = Mathf.Sqrt(
                quaternion.x * quaternion.x
                + quaternion.y * quaternion.y
                + quaternion.z * quaternion.z
                + quaternion.w * quaternion.w);
            if (magnitude > 1e-6f)
            {
                quaternion = new Quaternion(
                    quaternion.x / magnitude,
                    quaternion.y / magnitude,
                    quaternion.z / magnitude,
                    quaternion.w / magnitude);
            }

            if (quaternion.w < 0f
                || (quaternion.w == 0f
                    && (quaternion.z < 0f
                        || (quaternion.z == 0f
                            && (quaternion.y < 0f
                                || (quaternion.y == 0f && quaternion.x < 0f))))))
            {
                quaternion = new Quaternion(
                    -quaternion.x,
                    -quaternion.y,
                    -quaternion.z,
                    -quaternion.w);
            }

            return quaternion;
        }
    }
}
