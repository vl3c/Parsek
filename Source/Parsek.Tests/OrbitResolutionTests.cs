using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public sealed class OrbitResolutionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public OrbitResolutionTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            TestBodyRegistry.Install(("Kerbin", 600000.0, 3.5316e12));
        }

        public void Dispose()
        {
            TestBodyRegistry.Reset();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void TryValidateOrbitSegment_AcceptsSubOrbitalButNonDegenerateSma()
        {
            OrbitSegment segment = KerbalXProbeSubOrbitalSegment();

            bool ok = OrbitResolution.TryValidateOrbitSegment(
                segment,
                ResolveBody,
                OrbitSegmentValidationMode.ValidateAndLog,
                "rec_f1363fc",
                "distance",
                out CelestialBody body,
                out OrbitRejectionReason reason);

            Assert.True(ok);
            Assert.NotNull(body);
            Assert.Equal(OrbitRejectionReason.None, reason);
            Assert.DoesNotContain(logLines, l => l.Contains("orbit-resolver-reject"));
        }

        [Theory]
        [InlineData(0.0, "below-min-sma")]
        [InlineData(double.NaN, "non-finite-elements")]
        [InlineData(double.PositiveInfinity, "non-finite-elements")]
        public void TryValidateOrbitSegment_RejectsDegenerateSma(double sma, string reasonToken)
        {
            OrbitSegment segment = KerbalXProbeSubOrbitalSegment();
            segment.semiMajorAxis = sma;

            bool ok = OrbitResolution.TryValidateOrbitSegment(
                segment,
                ResolveBody,
                OrbitSegmentValidationMode.ValidateAndLog,
                "rec_bad",
                "distance",
                out _,
                out OrbitRejectionReason reason);

            Assert.False(ok);
            Assert.NotEqual(OrbitRejectionReason.None, reason);
            Assert.Contains(logLines, l =>
                l.Contains("orbit-resolver-reject-rec_bad-distance")
                && l.Contains("reason=" + reasonToken));
        }

        [Fact]
        public void TryValidateOrbitSegment_RejectsEachNonSmaElementNaN()
        {
            Func<OrbitSegment, OrbitSegment>[] mutators =
            {
                s => { s.inclination = double.NaN; return s; },
                s => { s.eccentricity = double.NaN; return s; },
                s => { s.longitudeOfAscendingNode = double.NaN; return s; },
                s => { s.argumentOfPeriapsis = double.NaN; return s; },
                s => { s.meanAnomalyAtEpoch = double.NaN; return s; },
                s => { s.epoch = double.NaN; return s; }
            };

            foreach (Func<OrbitSegment, OrbitSegment> mutate in mutators)
            {
                OrbitSegment segment = mutate(KerbalXProbeSubOrbitalSegment());

                bool ok = OrbitResolution.TryValidateOrbitSegment(
                    segment,
                    ResolveBody,
                    OrbitSegmentValidationMode.ValidateAndLog,
                    "rec_bad_element",
                    "distance",
                    out _,
                    out OrbitRejectionReason reason);

                Assert.False(ok);
                Assert.Equal(OrbitRejectionReason.NonFiniteElements, reason);
            }
        }

        [Fact]
        public void TryValidateOrbitSegment_RejectsNegativeEccentricity()
        {
            OrbitSegment segment = KerbalXProbeSubOrbitalSegment();
            segment.eccentricity = -0.1;

            bool ok = OrbitResolution.TryValidateOrbitSegment(
                segment,
                ResolveBody,
                OrbitSegmentValidationMode.ValidateAndLog,
                "rec_negative_ecc",
                "distance",
                out _,
                out OrbitRejectionReason reason);

            Assert.False(ok);
            Assert.Equal(OrbitRejectionReason.InvalidEccentricity, reason);
            Assert.Contains(logLines, l =>
                l.Contains("orbit-resolver-reject-rec_negative_ecc-distance")
                && l.Contains("reason=invalid-eccentricity"));
        }

        [Fact]
        public void TryValidateOrbitSegment_RejectsMissingBody()
        {
            OrbitSegment segment = KerbalXProbeSubOrbitalSegment();
            segment.bodyName = "Missing";

            bool ok = OrbitResolution.TryValidateOrbitSegment(
                segment,
                ResolveBody,
                OrbitSegmentValidationMode.ValidateAndLog,
                "rec_missing_body",
                "map-presence-proto",
                out _,
                out OrbitRejectionReason reason);

            Assert.False(ok);
            Assert.Equal(OrbitRejectionReason.MissingBody, reason);
            Assert.Contains(logLines, l =>
                l.Contains("orbit-resolver-reject-rec_missing_body-map-presence-proto")
                && l.Contains("reason=missing-body"));
        }

        [Fact]
        public void TryValidateOrbitElements_RejectsTinySelectedSeed()
        {
            bool ok = OrbitResolution.TryValidateOrbitElements(
                inclination: 0.1,
                eccentricity: 0.01,
                semiMajorAxis: 0.5,
                longitudeOfAscendingNode: 0.2,
                argumentOfPeriapsis: 0.3,
                meanAnomalyAtEpoch: 0.4,
                epoch: 100.0,
                bodyName: "Kerbin",
                bodyResolver: ResolveBody,
                mode: OrbitSegmentValidationMode.ValidateAndLog,
                recordingId: "rec_seed_tiny",
                context: "spawn-terminal-orbit",
                body: out _,
                reason: out OrbitRejectionReason reason);

            Assert.False(ok);
            Assert.Equal(OrbitRejectionReason.BelowMinSma, reason);
            Assert.Contains(logLines, l =>
                l.Contains("orbit-resolver-reject-rec_seed_tiny-spawn-terminal-orbit")
                && l.Contains("reason=below-min-sma"));
        }

        [Fact]
        public void TryValidateOrbitElements_RejectsNonFiniteSelectedSeed()
        {
            bool ok = OrbitResolution.TryValidateOrbitElements(
                inclination: 0.1,
                eccentricity: double.NaN,
                semiMajorAxis: 700000.0,
                longitudeOfAscendingNode: 0.2,
                argumentOfPeriapsis: 0.3,
                meanAnomalyAtEpoch: 0.4,
                epoch: 100.0,
                bodyName: "Kerbin",
                bodyResolver: ResolveBody,
                mode: OrbitSegmentValidationMode.ValidateAndLog,
                recordingId: "rec_seed_nan",
                context: "map-presence-state-vector",
                body: out _,
                reason: out OrbitRejectionReason reason);

            Assert.False(ok);
            Assert.Equal(OrbitRejectionReason.NonFiniteElements, reason);
            Assert.Contains(logLines, l =>
                l.Contains("orbit-resolver-reject-rec_seed_nan-map-presence-state-vector")
                && l.Contains("reason=non-finite-elements"));
        }

        [Fact]
        public void TryResolveOrbitFromSegment_RebuildsCacheWhenTupleChanges()
        {
            var cache = new Dictionary<long, Orbit>();
            OrbitSegment firstSegment = KerbalXProbeSubOrbitalSegment();

            Assert.True(OrbitResolution.TryResolveOrbitFromSegment(
                firstSegment,
                cacheKey: 42,
                orbitCache: cache,
                bodyResolver: ResolveBody,
                mode: OrbitSegmentValidationMode.ValidateAndLog,
                recordingId: "rec_cache",
                context: "distance",
                out Orbit firstOrbit,
                out _,
                out _));

            OrbitSegment changedSegment = firstSegment;
            changedSegment.meanAnomalyAtEpoch += 0.25;
            changedSegment.epoch += 10.0;

            Assert.True(OrbitResolution.TryResolveOrbitFromSegment(
                changedSegment,
                cacheKey: 42,
                orbitCache: cache,
                bodyResolver: ResolveBody,
                mode: OrbitSegmentValidationMode.ValidateAndLog,
                recordingId: "rec_cache",
                context: "distance",
                out Orbit secondOrbit,
                out _,
                out _));

            Assert.False(object.ReferenceEquals(firstOrbit, secondOrbit));
            Assert.Equal(changedSegment.meanAnomalyAtEpoch, secondOrbit.meanAnomalyAtEpoch, 10);
            Assert.Equal(changedSegment.epoch, secondOrbit.epoch, 10);
            Assert.Same(secondOrbit, cache[42]);
        }

        [Fact]
        public void TryComputeOrbitWorldPositionCore_RejectsNonFiniteBeforeBodyApi()
        {
            bool altitudeCalled = false;
            bool clampCalled = false;

            bool ok = OrbitResolution.TryComputeOrbitWorldPositionCore(
                new Vector3d(double.NaN, 1.0, 2.0),
                Vector3d.zero,
                body: null,
                clampToSurface: true,
                altitudeResolver: _ =>
                {
                    altitudeCalled = true;
                    return 0.0;
                },
                latitudeResolver: _ => 0.0,
                longitudeResolver: _ => 0.0,
                surfacePositionResolver: (_, __) =>
                {
                    clampCalled = true;
                    return Vector3d.zero;
                },
                out _,
                out OrbitWorldPositionFailureReason reason);

            Assert.False(ok);
            Assert.Equal(OrbitWorldPositionFailureReason.NonFinitePositionBeforeBodyApi, reason);
            Assert.False(altitudeCalled);
            Assert.False(clampCalled);
        }

        [Fact]
        public void TryComputeOrbitWorldPositionCore_RejectsNonFiniteClampOutput()
        {
            bool altitudeCalled = false;
            bool clampCalled = false;

            bool ok = OrbitResolution.TryComputeOrbitWorldPositionCore(
                new Vector3d(1.0, 2.0, 3.0),
                Vector3d.zero,
                body: null,
                clampToSurface: true,
                altitudeResolver: _ =>
                {
                    altitudeCalled = true;
                    return -1.0;
                },
                latitudeResolver: _ => 10.0,
                longitudeResolver: _ => 20.0,
                surfacePositionResolver: (_, __) =>
                {
                    clampCalled = true;
                    return new Vector3d(double.NaN, 0.0, 0.0);
                },
                out _,
                out OrbitWorldPositionFailureReason reason);

            Assert.False(ok);
            Assert.Equal(OrbitWorldPositionFailureReason.NonFinitePositionAfterClamp, reason);
            Assert.True(altitudeCalled);
            Assert.True(clampCalled);
        }

        [Theory]
        [InlineData(100.0, false, true)]
        [InlineData(200.0, false, false)]
        [InlineData(200.0, true, true)]
        public void IsSegmentActiveAtUT_UsesHalfOpenNonFinalSegments(
            double targetUT,
            bool isFinalSegment,
            bool expected)
        {
            OrbitSegment segment = new OrbitSegment
            {
                startUT = 100.0,
                endUT = 200.0
            };

            Assert.Equal(expected, OrbitResolution.IsSegmentActiveAtUT(
                segment,
                targetUT,
                isFinalSegment));
        }

        [Fact]
        public void ShouldRejectLegacySurfaceOrbitSegment_RejectsSurfaceOnlyTrackSection()
        {
            OrbitSegment segment = KerbalXProbeSubOrbitalSegment();
            var traj = new MockTrajectory
            {
                RecordingId = "surface_junk",
                OrbitSegments = new List<OrbitSegment> { segment },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        environment = SegmentEnvironment.SurfaceStationary,
                        referenceFrame = ReferenceFrame.Absolute,
                        startUT = 140.0,
                        endUT = 160.0
                    }
                }
            };

            bool rejected = OrbitResolution.ShouldRejectLegacySurfaceOrbitSegment(
                traj,
                segment,
                150.0,
                traj.RecordingId,
                "distance");

            Assert.True(rejected);
            Assert.Contains(logLines, l =>
                l.Contains("legacy-surface-orbit-reject-surface_junk-distance")
                && l.Contains("reason=surface-track-section"));
        }

        [Fact]
        public void ShouldRejectLegacySurfaceOrbitSegment_AllowsSubOrbitalTrajectoryThatLaterLands()
        {
            OrbitSegment segment = KerbalXProbeSubOrbitalSegment();
            var traj = new MockTrajectory().WithTimeRange(140.0, 160.0);
            traj.RecordingId = "suborbital_landing";
            traj.TerminalStateValue = TerminalState.Landed;
            traj.OrbitSegments = new List<OrbitSegment> { segment };
            traj.Points[0] = WithAltitude(traj.Points[0], 100000.0);
            traj.Points[1] = WithAltitude(traj.Points[1], 120000.0);
            traj.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 140.0,
                endUT = 160.0
            });

            bool rejected = OrbitResolution.ShouldRejectLegacySurfaceOrbitSegment(
                traj,
                segment,
                150.0,
                traj.RecordingId,
                "distance");

            Assert.False(rejected);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("legacy-surface-orbit-reject-suborbital_landing-distance"));
        }

        [Theory]
        [InlineData(SegmentEnvironment.Atmospheric, "atmospheric-track-section")]
        [InlineData(SegmentEnvironment.Approach, "approach-track-section")]
        public void ShouldRejectLegacySurfaceOrbitSegment_RejectsNonKeplerianTrackSections(
            SegmentEnvironment environment,
            string reason)
        {
            OrbitSegment segment = KerbalXProbeSubOrbitalSegment();
            var traj = new MockTrajectory().WithTimeRange(140.0, 160.0);
            traj.RecordingId = "non_keplerian_junk";
            traj.OrbitSegments = new List<OrbitSegment> { segment };
            traj.TrackSections.Add(new TrackSection
            {
                environment = environment,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 140.0,
                endUT = 160.0
            });

            bool rejected = OrbitResolution.ShouldRejectLegacySurfaceOrbitSegment(
                traj,
                segment,
                150.0,
                traj.RecordingId,
                "distance");

            Assert.True(rejected);
            Assert.Contains(logLines, l =>
                l.Contains("legacy-surface-orbit-reject-non_keplerian_junk-distance")
                && l.Contains("reason=" + reason));
        }

        [Fact]
        public void ShouldRejectLegacySurfaceOrbitSegment_RejectsPrelaunchLowAltitudeLegacyPoints()
        {
            OrbitSegment segment = KerbalXProbeSubOrbitalSegment();
            var rec = new Recording
            {
                RecordingId = "prelaunch_low_points",
                StartSituation = "Prelaunch",
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 140.0, bodyName = "Kerbin", altitude = 80.0 },
                    new TrajectoryPoint { ut = 160.0, bodyName = "Kerbin", altitude = 120.0 }
                },
                OrbitSegments = new List<OrbitSegment> { segment }
            };

            bool rejected = OrbitResolution.ShouldRejectLegacySurfaceOrbitSegment(
                rec,
                segment,
                150.0,
                rec.RecordingId,
                "distance");

            Assert.True(rejected);
            Assert.Contains(logLines, l =>
                l.Contains("legacy-surface-orbit-reject-prelaunch_low_points-distance")
                && l.Contains("reason=surface-start-situation"));
        }

        [Fact]
        public void ShouldSuppressChainOrbitFallbackForLegacySurfaceOrbit_RejectsSurfaceTrackSection()
        {
            var rec = ChainFallbackRecording(SegmentEnvironment.SurfaceStationary);
            rec.RecordingId = "chain_surface_junk";

            bool suppressed = ParsekFlight.ShouldSuppressChainOrbitFallbackForLegacySurfaceOrbit(
                rec,
                currentUT: 150.0);

            Assert.True(suppressed);
            Assert.Contains(logLines, l =>
                l.Contains("legacy-surface-orbit-reject-chain_surface_junk-chain-fallback")
                && l.Contains("reason=surface-track-section"));
        }

        [Fact]
        public void ShouldSuppressChainOrbitFallbackForLegacySurfaceOrbit_AllowsExoBallisticTrackSection()
        {
            var rec = ChainFallbackRecording(SegmentEnvironment.ExoBallistic);
            rec.RecordingId = "chain_exo_ballistic";

            bool suppressed = ParsekFlight.ShouldSuppressChainOrbitFallbackForLegacySurfaceOrbit(
                rec,
                currentUT: 150.0);

            Assert.False(suppressed);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("legacy-surface-orbit-reject-chain_exo_ballistic-chain-fallback"));
        }

        private static CelestialBody ResolveBody(string bodyName)
        {
            return TestBodyRegistry.ResolveBodyByName(bodyName, out CelestialBody body)
                ? body
                : null;
        }

        private static OrbitSegment KerbalXProbeSubOrbitalSegment()
        {
            return new OrbitSegment
            {
                startUT = 142.16,
                endUT = 453.66,
                inclination = 0.02,
                eccentricity = 0.575,
                semiMajorAxis = 512941.0,
                longitudeOfAscendingNode = 1.0,
                argumentOfPeriapsis = 2.0,
                meanAnomalyAtEpoch = 0.5,
                epoch = 142.16,
                bodyName = "Kerbin"
            };
        }

        private static TrajectoryPoint WithAltitude(TrajectoryPoint point, double altitude)
        {
            point.altitude = altitude;
            return point;
        }

        private static Recording ChainFallbackRecording(SegmentEnvironment environment)
        {
            return new Recording
            {
                RecordingId = "chain_fallback",
                ExplicitStartUT = 140.0,
                ExplicitEndUT = 160.0,
                SurfacePos = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = 0.0,
                    longitude = 0.0,
                    altitude = 0.0
                },
                OrbitSegments = new List<OrbitSegment> { KerbalXProbeSubOrbitalSegment() },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        environment = environment,
                        referenceFrame = ReferenceFrame.Absolute,
                        startUT = 140.0,
                        endUT = 160.0
                    }
                }
            };
        }
    }
}
