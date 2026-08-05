using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;
using StockOrbitPort = Parsek.Tests.StockOrbitElementFrameParityTests.StockOrbitPort;

namespace Parsek.Tests
{
    /// <summary>
    /// The PRODUCTION site-4 / site-5 seam, driven headlessly against a REAL non-identity
    /// <c>Planetarium.Zup</c>.
    /// <para>
    /// <c>Planetarium.Zup</c> is a plain public static field, so a headless test can install the
    /// frame the game installs in <c>Planetarium.Awake</c> and make the finalizer's producer take
    /// the same path it takes in flight. That is what lets these cells state the site-4 claim
    /// end-to-end without a live KSP — and it is exactly what the pre-existing headless round-trip
    /// cell in <c>SceneExitFinalizationIntegrationTests</c> could not do: that cell models the
    /// consumer through the SAME <c>TwoBodyOrbit</c> propagation the producer uses, so the Zup
    /// rotation cancels inside the fixture and the mismatch only ever appeared in flight.
    /// </para>
    /// <para>
    /// Touches shared static state (<c>Planetarium.Zup</c>, <c>ParsekLog</c>), hence
    /// <c>[Collection("Sequential")]</c> and the restore in <see cref="Dispose"/>.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class StockOrbitFrameSeamTests : IDisposable
    {
        /// <summary>The polar-axis angle measured between the two frames in H9 run 2026-08-04_2224.</summary>
        private const double MeasuredZupAngleDegrees = 230.01;

        private const double KerbinMu = 3.5316e12;

        private readonly Planetarium.CelestialFrame originalZup;
        private readonly List<string> logLines = new List<string>();

        public StockOrbitFrameSeamTests()
        {
            originalZup = Planetarium.Zup;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            Planetarium.Zup = originalZup;
            ParsekLog.ResetTestOverrides();
        }

        // ------------------------------------------------------------------
        // The frame helper
        // ------------------------------------------------------------------

        /// <summary>
        /// A default-constructed <c>Planetarium.CelestialFrame</c> — all three axes zero, which is
        /// what <c>Planetarium.Zup</c> carries in any process that never ran
        /// <c>Planetarium.Awake</c> — is DECLINED rather than applied. Applying it would collapse
        /// every state vector to the origin, so the orthonormality gate is the thing standing
        /// between the headless fixtures and a silent zero.
        /// </summary>
        [Fact]
        public void ToStockOrbitFrame_DeclinesADegenerateZupAndPassesTheVectorThrough()
        {
            Planetarium.Zup = default(Planetarium.CelestialFrame);
            var raw = new Vector3d(-496283.195, 337326.756, -1018.081);

            Vector3d resolved = IncompleteBallisticSceneExitFinalizer.ToStockOrbitFrame(raw);

            Assert.Equal(raw.x, resolved.x, 6);
            Assert.Equal(raw.y, resolved.y, 6);
            Assert.Equal(raw.z, resolved.z, 6);
            Assert.Contains(logLines, l =>
                l.Contains("[Extrapolator]") && l.Contains("not an orthonormal frame"));
        }

        /// <summary>
        /// With the frame the game installs, the helper reproduces
        /// <c>Planetarium.Zup.WorldToLocal</c>: a rotation about the POLAR axis, leaving the polar
        /// component and the magnitude alone.
        /// </summary>
        [Fact]
        public void ToStockOrbitFrame_AppliesTheZupRotationAboutThePolarAxis()
        {
            Planetarium.Zup = BuildZup(MeasuredZupAngleDegrees);
            var raw = new Vector3d(-496283.195, 337326.756, -1018.081);

            Vector3d resolved = IncompleteBallisticSceneExitFinalizer.ToStockOrbitFrame(raw);

            Assert.Equal(raw.z, resolved.z, 6);
            Assert.Equal(Magnitude(raw), Magnitude(resolved), 3);
            Assert.Equal(
                NormalizeDegrees(PlaneAngleDegrees(raw) - MeasuredZupAngleDegrees),
                PlaneAngleDegrees(resolved),
                6);
        }

        // ------------------------------------------------------------------
        // The site-4 round trip, through the real producer
        // ------------------------------------------------------------------

        /// <summary>
        /// THE SITE-4 CLAIM, headless: under the real frame the game installs, the attitude the
        /// finalizer seeds onto a predicted segment comes back out of a STOCK-FRAME consumer
        /// unchanged.
        /// <para>
        /// The consumer is modelled through <see cref="StockOrbitPort"/> — the transcription of
        /// stock's own element-to-state chain — rather than through the extrapolator's propagator,
        /// which is the whole point: it reaches the segment's state the way
        /// <c>ParsekFlight.ComputeOrbitalRotation</c> does (<c>new Orbit(...)</c> then
        /// <c>getPositionAtUT</c> / <c>getOrbitalVelocityAtUT</c>), so a producer encoding in the
        /// raw element frame cannot cancel here any more than it cancelled in flight.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(MeasuredZupAngleDegrees)]
        [InlineData(-130.0)]
        [InlineData(47.5)]
        public void SeedPredictedSegment_RoundTripsThroughAStockFrameConsumerUnderAnyZup(
            double inverseRotAngleDegrees)
        {
            Planetarium.Zup = BuildZup(inverseRotAngleDegrees);

            var frozenWorldRotation = new Quaternion(0.2f, -0.4f, 0.3f, 0.8f);
            List<OrbitSegment> segments = BuildSegments();

            IncompleteBallisticSceneExitFinalizer.SeedPredictedSegmentOrbitalFrameRotations(
                "scene-exit-site5-round-trip", segments, frozenWorldRotation, BuildBodies());

            Assert.True(BallisticExtrapolator.HasOrbitalFrameRotation(segments[0].orbitalFrameRotation));

            float error = ResolveErrorThroughStockConsumer(
                segments[0], inverseRotAngleDegrees, frozenWorldRotation);
            Assert.True(error < 0.05f,
                $"the seeded attitude did not round-trip through the stock-frame consumer "
                + $"(inverseRotAngle={inverseRotAngleDegrees}, error={error} deg)");
        }

        /// <summary>
        /// ANTI-VACUITY, and the regression this whole investigation exists for: encoding off the
        /// RAW element-frame state — what the producer did before the site-5 correction — breaks
        /// the same round trip by the double-digit degrees the in-game probe measured. Without a
        /// non-identity Zup installed there is nothing to break, which is precisely why the
        /// earlier headless cell stayed green while the flight red'd.
        /// </summary>
        [Fact]
        public void SeedPredictedSegment_RawElementFrameEncodingBreaksTheRoundTrip()
        {
            Planetarium.Zup = BuildZup(MeasuredZupAngleDegrees);

            var frozenWorldRotation = new Quaternion(0.2f, -0.4f, 0.3f, 0.8f);
            OrbitSegment segment = BuildSegments()[0];

            Assert.True(BallisticExtrapolator.TryPropagate(
                segment, KerbinMu, segment.startUT,
                out Vector3d rawPosition, out Vector3d rawVelocity));

            segment.orbitalFrameRotation = BallisticExtrapolator.ComputeOrbitalFrameRotationFromState(
                frozenWorldRotation,
                IncompleteBallisticSceneExitFinalizer.SwizzleZupBodyRelativeToWorld(rawPosition),
                rawVelocity);

            float error = ResolveErrorThroughStockConsumer(
                segment, MeasuredZupAngleDegrees, frozenWorldRotation);
            Assert.True(error > 10f,
                $"the pre-fix raw-element-frame producer still round-trips (error={error} deg); "
                + "this fixture no longer discriminates the frame the flights measured");
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// The <c>hasOfr</c> branch of <c>ParsekFlight.ComputeOrbitalRotation</c>, off the state a
        /// stock <c>Orbit</c> built from the same segment reports.
        /// </summary>
        private static float ResolveErrorThroughStockConsumer(
            OrbitSegment segment, double inverseRotAngleDegrees, Quaternion expectedWorldRotation)
        {
            var stock = StockOrbitPort.FromSegment(
                segment, KerbinMu, StockOrbitPort.PlanetaryZup(inverseRotAngleDegrees));

            Vector3 velocity = ((Vector3)stock.GetOrbitalVelocityAtUT(segment.startUT)).normalized;
            Vector3 radialOut = ((Vector3)IncompleteBallisticSceneExitFinalizer
                .SwizzleZupBodyRelativeToWorld(stock.GetRelativePositionAtUT(segment.startUT))).normalized;
            Assert.True(Mathf.Abs(Vector3.Dot(velocity, radialOut)) <= 0.99f,
                "fixture drove the consumer into its near-parallel LookRotation fallback; "
                + "the round trip would not be measuring the orbital frame");

            Quaternion resolved = TrajectoryMath.PureMultiply(
                TrajectoryMath.PureLookRotation(velocity, radialOut), segment.orbitalFrameRotation);
            return TrajectoryMath.ComputeQuaternionAngleDegrees(resolved, expectedWorldRotation);
        }

        private static Planetarium.CelestialFrame BuildZup(double inverseRotAngleDegrees)
        {
            var port = StockOrbitPort.PlanetaryZup(inverseRotAngleDegrees);
            return new Planetarium.CelestialFrame { X = port.X, Y = port.Y, Z = port.Z };
        }

        private static Dictionary<string, ExtrapolationBody> BuildBodies()
        {
            return new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = new ExtrapolationBody
                {
                    Name = "Kerbin",
                    GravitationalParameter = KerbinMu,
                    Radius = 600000.0
                }
            };
        }

        private static List<OrbitSegment> BuildSegments()
        {
            return new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    bodyName = "Kerbin",
                    startUT = 1000.0,
                    endUT = 1100.0,
                    semiMajorAxis = 900000.0,
                    eccentricity = 0.42,
                    inclination = 35.0,
                    longitudeOfAscendingNode = 128.0,
                    argumentOfPeriapsis = 77.0,
                    meanAnomalyAtEpoch = 0.75,
                    epoch = 1000.0
                }
            };
        }

        private static double Magnitude(Vector3d v)
        {
            return Math.Sqrt((v.x * v.x) + (v.y * v.y) + (v.z * v.z));
        }

        private static double PlaneAngleDegrees(Vector3d v)
        {
            return NormalizeDegrees(Math.Atan2(v.y, v.x) * (180.0 / Math.PI));
        }

        private static double NormalizeDegrees(double degrees)
        {
            double normalized = degrees % 360.0;
            return normalized < 0.0 ? normalized + 360.0 : normalized;
        }
    }
}
