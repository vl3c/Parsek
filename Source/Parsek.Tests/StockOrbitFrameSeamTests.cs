using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;
using StockOrbitPort = Parsek.Tests.StockOrbitElementFrameParityTests.StockOrbitPort;

namespace Parsek.Tests
{
    /// <summary>
    /// The PRODUCTION site-4 / element-frame seam, driven headlessly against a REAL non-identity
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
    /// SINCE FINDING A (2026-08-05, branch <c>twobody-element-frame</c>) the element-frame crossing
    /// is applied ONCE, inside <c>TwoBodyOrbit.TryCreateFromSegment</c>, rather than by a local
    /// <c>ToStockOrbitFrame</c> wrapper in the finalizer. The cells below therefore drive the
    /// boundary through its production entry points — <c>BallisticExtrapolator.TryPropagate</c> and
    /// <c>SeedPredictedSegmentOrbitalFrameRotations</c> — and the anti-vacuity half now breaks the
    /// round trip by SKIPPING the boundary conversion (propagating the segment's raw KSP-native
    /// LAN) rather than by re-encoding off a raw-frame state, which is no longer reachable through
    /// the production API.
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
        /// <c>Planetarium.Awake</c> — is DECLINED rather than read for an angle, and the boundary
        /// becomes the identity. Without the gate, every headless fixture in the suite would have
        /// its LAN shifted by whatever <c>Atan2(0, 0)</c> happens to return off a collapsed basis.
        /// The decline is LOGGED, because a silent identity is indistinguishable from a working
        /// conversion.
        /// </summary>
        [Fact]
        public void ElementFrameBoundary_DeclinesADegenerateZupAndBecomesTheIdentity()
        {
            Planetarium.Zup = default(Planetarium.CelestialFrame);

            Assert.Equal(0.0, BallisticExtrapolator.GetStockElementFrameZupAngleRadians(), 12);
            Assert.Contains(logLines, l =>
                l.Contains("[Extrapolator]") && l.Contains("not an orthonormal polar rotation"));
        }

        /// <summary>
        /// With the frame the game installs, the boundary recovers <c>inverseRotAngle</c> itself —
        /// the angle of the pure polar rotation <c>Planetarium.Zup</c> reduces to. Signed, so a
        /// sign slip here is caught before it reaches a LAN.
        /// </summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(37.5)]
        [InlineData(MeasuredZupAngleDegrees)]
        [InlineData(-130.0)]
        public void ElementFrameBoundary_RecoversTheZupPolarAngle(double inverseRotAngleDegrees)
        {
            Planetarium.Zup = BuildZup(inverseRotAngleDegrees);

            double recovered = BallisticExtrapolator.GetStockElementFrameZupAngleRadians()
                * (180.0 / Math.PI);

            Assert.Equal(
                NormalizeDegrees(inverseRotAngleDegrees), NormalizeDegrees(recovered), 6);
        }

        /// <summary>
        /// An orthonormal basis that is NOT a rotation about the polar axis is declined too: a
        /// single angle cannot describe it, and silently returning one would mis-rotate every
        /// extrapolated tail. <c>Planetarium.Zup</c> is polar by construction, so this can only
        /// fire on a frame nothing in KSP builds — which is the point of stating it.
        /// </summary>
        [Fact]
        public void ElementFrameBoundary_DeclinesAnOrthonormalNonPolarFrame()
        {
            // A 90 deg rotation about the X axis: orthonormal, but its Z axis is not the pole.
            Planetarium.Zup = new Planetarium.CelestialFrame
            {
                X = new Vector3d(1.0, 0.0, 0.0),
                Y = new Vector3d(0.0, 0.0, 1.0),
                Z = new Vector3d(0.0, -1.0, 0.0)
            };

            Assert.Equal(0.0, BallisticExtrapolator.GetStockElementFrameZupAngleRadians(), 12);
            Assert.Contains(logLines, l =>
                l.Contains("[Extrapolator]") && l.Contains("not an orthonormal polar rotation"));
        }

        /// <summary>
        /// THE PRODUCTION BOUNDARY, both ends: a segment's KSP-native elements propagate to the
        /// state a stock <c>Orbit</c> built from the SAME elements reports — directly, with no
        /// <c>WorldToLocal</c> applied by the caller — and an extrapolated conic written back out
        /// through <c>CreateSegment</c> restores the KSP-native LAN it came in with.
        /// </summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(37.5)]
        [InlineData(MeasuredZupAngleDegrees)]
        [InlineData(-130.0)]
        public void TryPropagate_MatchesStocksOwnElementToStateChainDirectly(
            double inverseRotAngleDegrees)
        {
            Planetarium.Zup = BuildZup(inverseRotAngleDegrees);
            OrbitSegment segment = BuildSegments()[0];

            var stock = StockOrbitPort.FromSegment(
                segment, KerbinMu, StockOrbitPort.PlanetaryZup(inverseRotAngleDegrees));

            for (int i = 0; i <= 8; i++)
            {
                double ut = segment.epoch + (i * 137.0);
                Assert.True(BallisticExtrapolator.TryPropagate(
                    segment, KerbinMu, ut, out Vector3d position, out Vector3d velocity));

                AssertVectorsAgree(stock.GetRelativePositionAtUT(ut), position, 1e-9, $"position {i}");
                AssertVectorsAgree(stock.GetOrbitalVelocityAtUT(ut), velocity, 1e-9, $"velocity {i}");
            }
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
        /// ANTI-VACUITY, and the regression this whole investigation exists for: SKIPPING the
        /// element-frame boundary breaks the same round trip by the double-digit degrees the
        /// in-game probe measured.
        /// <para>
        /// The pre-fix encoding is no longer reachable through the production API (that is the
        /// fix), so this cell reproduces it the only honest way left: it propagates a segment whose
        /// LAN was NOT shifted at the boundary — i.e. it hands <c>TryPropagate</c> elements already
        /// carrying the boundary's own correction, which cancels the shift and leaves the raw
        /// element-frame state the producer used to encode off. Without a non-identity Zup
        /// installed there is nothing to break, which is precisely why the earlier headless cell
        /// stayed green while the flight red'd.
        /// </para>
        /// </summary>
        [Fact]
        public void SeedPredictedSegment_SkippingTheElementFrameBoundaryBreaksTheRoundTrip()
        {
            Planetarium.Zup = BuildZup(MeasuredZupAngleDegrees);

            var frozenWorldRotation = new Quaternion(0.2f, -0.4f, 0.3f, 0.8f);
            OrbitSegment segment = BuildSegments()[0];

            // Pre-cancel the boundary's subtraction so the propagated state is the RAW
            // element-frame state the producer encoded off before the fix. The ORBITAL-frame
            // convention below is held at the shipping world/world one on purpose: the only
            // thing this cell varies is the ELEMENT frame, so the error it measures cannot be
            // confused with the (separate, later) fifth-frame-mismatch correction.
            OrbitSegment unconverted = segment;
            unconverted.longitudeOfAscendingNode =
                NormalizeDegrees(segment.longitudeOfAscendingNode + MeasuredZupAngleDegrees);

            Assert.True(BallisticExtrapolator.TryPropagate(
                unconverted, KerbinMu, segment.startUT,
                out Vector3d rawPosition, out Vector3d rawVelocity));

            segment.orbitalFrameRotation = BallisticExtrapolator.ComputeOrbitalFrameRotationFromState(
                frozenWorldRotation,
                TrajectoryMath.SwizzleZupBodyRelativeToWorld(rawPosition),
                TrajectoryMath.SwizzleZupBodyRelativeToWorld(rawVelocity));

            float error = ResolveErrorThroughStockConsumer(
                segment, MeasuredZupAngleDegrees, frozenWorldRotation);
            Assert.True(error > 10f,
                $"skipping the element-frame boundary still round-trips (error={error} deg); "
                + "this fixture no longer discriminates the frame the flights measured");
        }

        /// <summary>
        /// The OUTBOUND half of the boundary: elements in, extrapolate, elements out — the
        /// KSP-native LAN is restored under a non-identity Zup. Without the <c>+ zupAngle</c> in
        /// <c>CreateSegment</c> this reads back the internal (stock-state-frame) LAN and every
        /// extrapolator-authored segment replays rotated.
        /// </summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(MeasuredZupAngleDegrees)]
        [InlineData(-130.0)]
        public void ExtrapolatedSegments_RoundTripTheKspNativeLongitudeOfAscendingNode(
            double inverseRotAngleDegrees)
        {
            Planetarium.Zup = BuildZup(inverseRotAngleDegrees);
            OrbitSegment segment = BuildSegments()[0];

            Assert.True(IncompleteBallisticSceneExitFinalizer.TryBuildStartStateFromSegment(
                segment, BuildBodies(), segment.startUT, out BallisticStateVector startState));

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(startState, BuildBodies());

            Assert.NotEmpty(result.segments);
            Assert.Equal(
                NormalizeDegrees(segment.longitudeOfAscendingNode),
                NormalizeDegrees(result.segments[0].longitudeOfAscendingNode),
                4);
            Assert.Equal(
                segment.inclination, result.segments[0].inclination, 4);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// The <c>hasOfr</c> branch of <c>ParsekFlight.ComputeOrbitalRotation</c>, off the state a
        /// stock <c>Orbit</c> built from the same segment reports.
        /// <para>
        /// WORLD/WORLD since branch <c>orbital-rotation-frame</c>: the consumer lifts its
        /// Zup-swizzled <c>getOrbitalVelocityAtUT</c> velocity into world axes before pairing it
        /// with the world radial, so this transcription does the same. Dropping the velocity
        /// swizzle here would re-pin the fifth frame mismatch as if it were the contract.
        /// </para>
        /// </summary>
        private static float ResolveErrorThroughStockConsumer(
            OrbitSegment segment, double inverseRotAngleDegrees, Quaternion expectedWorldRotation)
        {
            var stock = StockOrbitPort.FromSegment(
                segment, KerbinMu, StockOrbitPort.PlanetaryZup(inverseRotAngleDegrees));

            Vector3 velocity = ((Vector3)TrajectoryMath.SwizzleZupBodyRelativeToWorld(
                stock.GetOrbitalVelocityAtUT(segment.startUT))).normalized;
            Vector3 radialOut = ((Vector3)TrajectoryMath.SwizzleZupBodyRelativeToWorld(
                stock.GetRelativePositionAtUT(segment.startUT))).normalized;
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

        private static void AssertVectorsAgree(
            Vector3d expected, Vector3d actual, double tolerance, string what)
        {
            double scale = Math.Max(Magnitude(expected), 1e-12);
            double error = Magnitude(new Vector3d(
                expected.x - actual.x, expected.y - actual.y, expected.z - actual.z)) / scale;
            Assert.True(error <= tolerance,
                $"{what}: expected ({expected.x},{expected.y},{expected.z}), "
                + $"got ({actual.x},{actual.y},{actual.z}) (relative error {error} > {tolerance})");
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
