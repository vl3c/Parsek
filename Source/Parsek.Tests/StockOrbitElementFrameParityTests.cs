using System;
using Xunit;
using TwoBodyOrbit = Parsek.BallisticExtrapolator.TwoBodyOrbit;

namespace Parsek.Tests
{
    /// <summary>
    /// MECHANICAL CROSS-CHECK of <c>BallisticExtrapolator.TwoBodyOrbit</c>'s element-to-state
    /// map against STOCK KSP's, for the element-seeded path
    /// (<c>TwoBodyOrbit.TryCreateFromSegment</c> -> <c>GetStateAtUT</c> versus
    /// <c>new Orbit(inc, e, sma, LAN, argPe, mEp, epoch, body)</c> ->
    /// <c>getRelativePositionAtUT</c> / <c>getOrbitalVelocityAtUT</c>).
    /// <para>
    /// <b>Why a port.</b> Stock <c>Orbit</c> cannot be exercised headlessly (it needs a live
    /// <c>CelestialBody</c> and a <c>Planetarium</c> singleton), and the in-game probe can only
    /// report ONE number - the angle it is off by - which is not a diagnosis. The chain below is
    /// a minimal, ELLIPTIC-ONLY transcription of the decompiled KSP 1.12.5 source
    /// (<c>docs/decompiled/orbit.cs</c>: <c>Init</c> / <c>getObtAtUT</c> /
    /// <c>getRelativePositionAtT</c> / <c>getRelativePositionFromTrueAnomaly</c> /
    /// <c>getOrbitalVelocityAtObT</c> / <c>getOrbitalVelocityAtTrueAnomaly</c> /
    /// <c>solveEccentricAnomaly*</c> / <c>GetTrueAnomaly</c>, plus
    /// <c>Planetarium.CelestialFrame.SetFrame</c> / <c>OrbitalFrame</c> / <c>PlanetaryFrame</c>
    /// decompiled separately from <c>Assembly-CSharp.dll</c>). It is TEST-ONLY and exists purely
    /// to pin the frame; nothing in <c>Source/Parsek</c> calls it.
    /// </para>
    /// <para>
    /// <b>What it found</b> (this file IS the diagnosis for the site-4 residual measured at
    /// angleError=131.066 deg in H9 run <c>2026-08-04_2224</c>). The two element-to-state maps
    /// are ARITHMETICALLY IDENTICAL - same 3-1-3 perifocal rotation, same mean-anomaly
    /// convention, same epoch handling, same handedness - EXCEPT for one call stock makes and
    /// <c>TwoBodyOrbit</c> does not: every state vector stock returns is passed through
    /// <c>Planetarium.Zup.WorldToLocal</c>. <c>Planetarium.Zup</c> is
    /// <c>PlanetaryFrame(0, 90, inverseRotAngle)</c>, which reduces to a pure ROTATION ABOUT THE
    /// POLAR AXIS by <c>inverseRotAngle</c> - the identity only when that angle is zero. So
    /// <c>TwoBodyOrbit</c>'s element-seeded state is stock's rotated about the polar axis, i.e.
    /// KSP's <c>LAN</c> is measured from <c>Planetarium.right</c> rather than from the raw +x of
    /// the element frame. In run <c>2026-08-04_2224</c> that angle MEASURED 230.01 deg: the
    /// producer's radial sat at plane-angle 15.806 deg where the live orbit's sat at 145.796 deg,
    /// with the polar component preserved to 3e-6 and r-perpendicular-to-v preserved on both
    /// sides - the exact signature of a rotation about the polar axis, and nothing else.
    /// </para>
    /// <para>
    /// The site-2 / state-vector-seeded path is NOT affected: it seeds from stock's own
    /// <c>getRelativePositionAtUT</c> / <c>getOrbitalVelocityAtUT</c>, so it recovers elements in
    /// the Zup frame and propagates in the Zup frame, self-consistently. Only the
    /// ELEMENT-seeded path crosses the boundary.
    /// </para>
    /// <para>
    /// <b>FINDING A IS FIXED</b> (2026-08-05, branch <c>twobody-element-frame</c>).
    /// <c>TryCreateFromSegment</c> now SUBTRACTS the <c>Planetarium.Zup</c> polar angle from a
    /// segment's KSP-native LAN and <c>CreateSegment</c> adds it back, so the element-seeded path
    /// lands DIRECTLY in stock's state frame. The cells below pin that AGREEMENT rather than the
    /// divergence: with a real non-identity <c>Zup</c> installed,
    /// <c>TryCreateFromSegment</c> -&gt; <c>GetStateAtUT</c> equals the transcription below with NO
    /// <c>WorldToLocal</c> applied. The closed form that WAS the diagnosis is still pinned, on the
    /// state reached by PRE-CANCELLING the boundary (feeding a segment whose LAN carries the
    /// boundary's own correction) - the only way to reproduce the pre-fix encoding through the
    /// production API now. FINDING B, the extreme-eccentricity solver, remains OPEN and is still
    /// pinned as a disagreement at the bottom of this file.
    /// </para>
    /// <para>
    /// Since the production boundary reads the process-wide <c>Planetarium.Zup</c>, these cells
    /// touch shared static state: hence <c>[Collection("Sequential")]</c> and the restore in
    /// <see cref="Dispose"/>. Every cell installs the frame it wants explicitly rather than relying
    /// on the ambient one.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class StockOrbitElementFrameParityTests : IDisposable
    {
        private const double KerbinMu = 3.5316e12;
        private const double DegToRad = Math.PI / 180.0;

        private readonly Planetarium.CelestialFrame originalZup;

        public StockOrbitElementFrameParityTests()
        {
            originalZup = Planetarium.Zup;
        }

        public void Dispose()
        {
            Planetarium.Zup = originalZup;
        }

        /// <summary>
        /// Installs the frame <c>Planetarium.Awake</c> installs, so the production element-frame
        /// boundary takes the same path it takes in flight.
        /// </summary>
        private static void InstallZup(double inverseRotAngleDegrees)
        {
            CelestialFramePort port = StockOrbitPort.PlanetaryZup(inverseRotAngleDegrees);
            Planetarium.Zup = new Planetarium.CelestialFrame { X = port.X, Y = port.Y, Z = port.Z };
        }

        /// <summary>
        /// A segment carrying the boundary's own correction, so that
        /// <c>TryCreateFromSegment</c>'s subtraction cancels and the propagated state is the RAW
        /// element-frame state the pre-fix producer encoded off. The only way to reach that state
        /// through the production API since Finding A was fixed.
        /// </summary>
        private static OrbitSegment PreCancelTheElementFrameBoundary(
            OrbitSegment segment, double inverseRotAngleDegrees)
        {
            OrbitSegment raw = segment;
            raw.longitudeOfAscendingNode =
                NormalizeDegrees(segment.longitudeOfAscendingNode + inverseRotAngleDegrees);
            return raw;
        }

        /// <summary>
        /// The polar-axis angle MEASURED between the two frames in H9 run 2026-08-04_2224.
        /// Not a derived constant - see the class docstring for the reduction.
        /// </summary>
        private const double MeasuredZupAngleDegrees = 230.01;

        // ------------------------------------------------------------------
        // 1. The frames agree exactly when Planetarium.Zup is the identity
        // ------------------------------------------------------------------

        /// <summary>
        /// With <c>inverseRotAngle = 0</c> (<c>Zup</c> = identity) the two propagators agree to
        /// solver precision at every element combination. This is the load-bearing negative
        /// result: there is NO handedness disagreement, NO velocity reversal, NO orbit-normal
        /// flip and NO anomaly/epoch phase offset between the textbook z-polar propagation and
        /// stock's - the lead hypothesis for the site-4 residual is false, and the entire
        /// discrepancy is the single <c>Zup</c> rotation isolated in the next cell.
        /// <para>
        /// Eccentricities stay at or below 0.9 here on purpose; see
        /// <see cref="HighEccentricity_TwoBodyOrbitNewtonDivergesFromStocksExtremeEccSolver"/>
        /// for the separate solver finding above that.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(0.0, 0.0, 0.0, 0.0)]
        [InlineData(35.0, 128.0, 77.0, 0.01)]
        [InlineData(35.0, 128.0, 77.0, 0.6)]
        [InlineData(89.5, 300.0, 250.0, 0.35)]
        [InlineData(140.0, 12.0, 190.0, 0.5)]
        [InlineData(0.0972, 235.7958, 90.0, 0.9)]
        [InlineData(63.4, 0.0, 0.0, 0.2)]
        public void IdentityZup_TwoBodyOrbitReproducesStocksElementToStateMapExactly(
            double inclinationDeg, double lanDeg, double argPeDeg, double eccentricity)
        {
            InstallZup(0.0);
            double semiMajorAxis = 900000.0;
            const double epoch = 1000.0;
            const double meanAnomalyAtEpoch = 0.75;

            OrbitSegment segment = BuildSegment(
                inclinationDeg, lanDeg, argPeDeg, eccentricity, semiMajorAxis, meanAnomalyAtEpoch, epoch);
            Assert.True(TwoBodyOrbit.TryCreateFromSegment(segment, KerbinMu, out TwoBodyOrbit orbit));

            var stock = StockOrbitPort.FromSegment(segment, KerbinMu, StockOrbitPort.IdentityZup());

            for (int i = 0; i <= 16; i++)
            {
                double ut = epoch + (stock.Period * i) / 16.0;
                orbit.GetStateAtUT(ut, out Vector3d position, out Vector3d velocity);

                AssertVectorsAgree(
                    stock.GetRelativePositionAtUT(ut), position, 1e-9, $"position at sample {i}");
                AssertVectorsAgree(
                    stock.GetOrbitalVelocityAtUT(ut), velocity, 1e-9, $"velocity at sample {i}");
            }
        }

        // ------------------------------------------------------------------
        // 2. The whole discrepancy is one rotation about the polar axis
        // ------------------------------------------------------------------

        /// <summary>
        /// THE FIX, stated as the agreement it produces: with a REAL non-identity
        /// <c>Planetarium.Zup</c> installed, the element-seeded propagator's state equals stock's
        /// DIRECTLY - no <c>WorldToLocal</c> applied by anyone downstream - at every UT.
        /// <para>
        /// And the CLOSED FORM that was the diagnosis, still pinned in the same cell:
        /// <c>stockState == Zup.WorldToLocal(rawElementFrameState)</c>, where the raw state is
        /// reached by pre-cancelling the boundary. Both statements in one place so a future reader
        /// sees what the boundary does AND why it has to.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(37.5)]
        [InlineData(MeasuredZupAngleDegrees)]
        [InlineData(-130.0)]
        public void ElementFrameBoundary_MakesTheTwoElementToStateMapsAgreeDirectly(
            double inverseRotAngleDeg)
        {
            InstallZup(inverseRotAngleDeg);
            OrbitSegment segment = BuildSegment(
                inclinationDeg: 35.0,
                lanDeg: 128.0,
                argPeDeg: 77.0,
                eccentricity: 0.42,
                semiMajorAxis: 900000.0,
                meanAnomalyAtEpoch: 0.75,
                epoch: 1000.0);
            Assert.True(TwoBodyOrbit.TryCreateFromSegment(segment, KerbinMu, out TwoBodyOrbit orbit));
            Assert.True(TwoBodyOrbit.TryCreateFromSegment(
                PreCancelTheElementFrameBoundary(segment, inverseRotAngleDeg),
                KerbinMu,
                out TwoBodyOrbit rawFrameOrbit));

            var zup = StockOrbitPort.PlanetaryZup(inverseRotAngleDeg);
            var stock = StockOrbitPort.FromSegment(segment, KerbinMu, zup);

            for (int i = 0; i <= 16; i++)
            {
                double ut = segment.epoch + (stock.Period * i) / 16.0;
                orbit.GetStateAtUT(ut, out Vector3d position, out Vector3d velocity);
                rawFrameOrbit.GetStateAtUT(ut, out Vector3d rawPosition, out Vector3d rawVelocity);

                AssertVectorsAgree(
                    stock.GetRelativePositionAtUT(ut), position, 1e-9, $"position at sample {i}");
                AssertVectorsAgree(
                    stock.GetOrbitalVelocityAtUT(ut), velocity, 1e-9, $"velocity at sample {i}");

                AssertVectorsAgree(
                    stock.GetRelativePositionAtUT(ut),
                    zup.WorldToLocal(rawPosition),
                    1e-9,
                    $"closed form, position at sample {i}");
                AssertVectorsAgree(
                    stock.GetOrbitalVelocityAtUT(ut),
                    zup.WorldToLocal(rawVelocity),
                    1e-9,
                    $"closed form, velocity at sample {i}");
            }
        }

        /// <summary>
        /// THE MEASURED CASE, on the fixture the flight actually ran: a prelaunch vessel's
        /// surface-rotation ellipse (a = 300.8 km, e = 0.9948) at 230.01 deg. The element-seeded
        /// state agrees with stock's directly.
        /// <para>
        /// SAMPLED NEAR APOAPSIS ONLY, and that restriction is FINDING B, not a weakening of this
        /// claim: at e = 0.9948 <c>TwoBodyOrbit</c>'s plain Newton solve diverges from stock's
        /// <c>solveEccentricAnomalyExtremeEcc</c> near periapsis by double-digit degrees of true
        /// anomaly (pinned by
        /// <see cref="HighEccentricity_TwoBodyOrbitNewtonDivergesFromStocksExtremeEccSolver"/>), so
        /// a full-orbit sweep here would red on the SOLVER and say nothing about the FRAME. The
        /// site-4 measurement itself was taken at apoapsis, where the solve is exact - so this is
        /// the fixture the flight ran, at the anomaly the flight ran it at.
        /// </para>
        /// </summary>
        [Fact]
        public void MeasuredPadFixture_ElementSeededStateAgreesWithStockDirectly()
        {
            InstallZup(MeasuredZupAngleDegrees);
            OrbitSegment segment = BuildSegment(
                inclinationDeg: 0.0972,
                lanDeg: 235.7958,
                argPeDeg: 90.0,
                eccentricity: 0.9948,
                semiMajorAxis: 300818.761,
                meanAnomalyAtEpoch: Math.PI,
                epoch: 0.0);
            Assert.True(TwoBodyOrbit.TryCreateFromSegment(segment, KerbinMu, out TwoBodyOrbit orbit));

            var zup = StockOrbitPort.PlanetaryZup(MeasuredZupAngleDegrees);
            var stock = StockOrbitPort.FromSegment(segment, KerbinMu, zup);

            for (int i = -4; i <= 4; i++)
            {
                double ut = segment.epoch + (stock.Period * i) / 64.0;
                orbit.GetStateAtUT(ut, out Vector3d position, out Vector3d velocity);

                AssertVectorsAgree(
                    stock.GetRelativePositionAtUT(ut), position, 1e-6, $"pad position at sample {i}");
                AssertVectorsAgree(
                    stock.GetOrbitalVelocityAtUT(ut), velocity, 1e-6, $"pad velocity at sample {i}");
            }
        }

        /// <summary>
        /// EQUATORIAL ORBITS: the boundary is a pure LAN shift, and a pure LAN shift is still the
        /// right correction at <c>inc = 0</c> - the 3-1-3 composition
        /// <c>R_z(LAN) R_x(0) R_z(argPe)</c> collapses to <c>R_z(LAN + argPe)</c>, so shifting LAN
        /// rotates the state about the polar axis exactly as it does at any other inclination.
        /// Stated closed form (the shifted-LAN state IS the polar-rotated state), then confirmed
        /// against the stock transcription.
        /// </summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(MeasuredZupAngleDegrees)]
        [InlineData(-130.0)]
        public void EquatorialOrbit_TheLanShiftIsStillThePolarRotation(double inverseRotAngleDeg)
        {
            InstallZup(inverseRotAngleDeg);
            OrbitSegment segment = BuildSegment(
                inclinationDeg: 0.0,
                lanDeg: 128.0,
                argPeDeg: 77.0,
                eccentricity: 0.42,
                semiMajorAxis: 900000.0,
                meanAnomalyAtEpoch: 0.75,
                epoch: 1000.0);
            Assert.True(TwoBodyOrbit.TryCreateFromSegment(segment, KerbinMu, out TwoBodyOrbit orbit));
            Assert.True(TwoBodyOrbit.TryCreateFromSegment(
                PreCancelTheElementFrameBoundary(segment, inverseRotAngleDeg),
                KerbinMu,
                out TwoBodyOrbit rawFrameOrbit));

            var zup = StockOrbitPort.PlanetaryZup(inverseRotAngleDeg);
            var stock = StockOrbitPort.FromSegment(segment, KerbinMu, zup);

            for (int i = 0; i <= 8; i++)
            {
                double ut = segment.epoch + (stock.Period * i) / 8.0;
                orbit.GetStateAtUT(ut, out Vector3d position, out Vector3d velocity);
                rawFrameOrbit.GetStateAtUT(ut, out Vector3d rawPosition, out Vector3d rawVelocity);

                // The shifted-LAN state IS the polar-rotated state, at inc = 0 as anywhere else.
                AssertVectorsAgree(
                    zup.WorldToLocal(rawPosition), position, 1e-9, $"equatorial position {i}");
                AssertVectorsAgree(
                    zup.WorldToLocal(rawVelocity), velocity, 1e-9, $"equatorial velocity {i}");

                // ...and it is stock's state.
                AssertVectorsAgree(
                    stock.GetRelativePositionAtUT(ut), position, 1e-9, $"equatorial vs stock {i}");

                // The plane is exactly the reference plane throughout - no polar leakage.
                Assert.Equal(0.0, position.z, 9);
                Assert.Equal(0.0, velocity.z, 9);
            }
        }

        /// <summary>
        /// HYPERBOLIC segments cross the boundary too: the conversion is a rotation of the conic,
        /// not a property of its class. The stock transcription is elliptic-only, so this states
        /// the closed form directly - the boundary-converted state is the polar rotation of the raw
        /// element-frame state, on an OPEN orbit.
        /// </summary>
        [Theory]
        [InlineData(37.5)]
        [InlineData(MeasuredZupAngleDegrees)]
        [InlineData(-130.0)]
        public void HyperbolicSegment_CrossesTheElementFrameBoundaryToo(double inverseRotAngleDeg)
        {
            InstallZup(inverseRotAngleDeg);
            OrbitSegment segment = BuildSegment(
                inclinationDeg: 22.0,
                lanDeg: 128.0,
                argPeDeg: 77.0,
                eccentricity: 1.35,
                semiMajorAxis: -900000.0,
                meanAnomalyAtEpoch: -0.4,
                epoch: 1000.0);
            Assert.True(TwoBodyOrbit.TryCreateFromSegment(segment, KerbinMu, out TwoBodyOrbit orbit));
            Assert.True(TwoBodyOrbit.TryCreateFromSegment(
                PreCancelTheElementFrameBoundary(segment, inverseRotAngleDeg),
                KerbinMu,
                out TwoBodyOrbit rawFrameOrbit));

            // The DIRECTION of the shift, pinned independently of the boundary itself: an
            // identity-Zup orbit built at (LAN - a) is by construction the state the boundary is
            // supposed to produce at LAN under Zup = a.
            OrbitSegment shifted = segment;
            shifted.longitudeOfAscendingNode =
                NormalizeDegrees(segment.longitudeOfAscendingNode - inverseRotAngleDeg);
            InstallZup(0.0);
            Assert.True(TwoBodyOrbit.TryCreateFromSegment(shifted, KerbinMu, out TwoBodyOrbit expected));
            InstallZup(inverseRotAngleDeg);

            var zup = StockOrbitPort.PlanetaryZup(inverseRotAngleDeg);

            for (int i = 0; i <= 8; i++)
            {
                double ut = segment.epoch + (i * 240.0);
                orbit.GetStateAtUT(ut, out Vector3d position, out Vector3d velocity);
                rawFrameOrbit.GetStateAtUT(ut, out Vector3d rawPosition, out Vector3d rawVelocity);
                expected.GetStateAtUT(ut, out Vector3d expectedPosition, out Vector3d expectedVelocity);

                AssertVectorsAgree(
                    zup.WorldToLocal(rawPosition), position, 1e-9, $"hyperbolic position {i}");
                AssertVectorsAgree(
                    zup.WorldToLocal(rawVelocity), velocity, 1e-9, $"hyperbolic velocity {i}");

                AssertVectorsAgree(
                    expectedPosition, position, 1e-9, $"hyperbolic shift direction, position {i}");
                AssertVectorsAgree(
                    expectedVelocity, velocity, 1e-9, $"hyperbolic shift direction, velocity {i}");
            }
        }

        /// <summary>
        /// The rotation is about the POLAR axis specifically: the component along it survives
        /// untouched. This is the property the flight measurement matched to 3e-6 (raw polar
        /// component -0.001700 against the live orbit's -0.001697), which is what rules out an
        /// anomaly error - a wrong true anomaly moves the state ALONG the orbit and does not
        /// preserve a fixed polar component while rotating position and velocity by the same
        /// angle.
        /// </summary>
        [Fact]
        public void ZupRotation_PreservesThePolarComponentAndTheRadialVelocityAngle()
        {
            InstallZup(MeasuredZupAngleDegrees);
            OrbitSegment segment = BuildSegment(
                inclinationDeg: 22.0,
                lanDeg: 128.0,
                argPeDeg: 77.0,
                eccentricity: 0.42,
                semiMajorAxis: 900000.0,
                meanAnomalyAtEpoch: 0.75,
                epoch: 1000.0);
            // The RAW element-frame state (boundary pre-cancelled) is what the rotation acts on.
            Assert.True(TwoBodyOrbit.TryCreateFromSegment(
                PreCancelTheElementFrameBoundary(segment, MeasuredZupAngleDegrees),
                KerbinMu,
                out TwoBodyOrbit orbit));

            var zup = StockOrbitPort.PlanetaryZup(MeasuredZupAngleDegrees);
            var stock = StockOrbitPort.FromSegment(segment, KerbinMu, zup);

            double ut = segment.epoch + 137.0;
            orbit.GetStateAtUT(ut, out Vector3d rawPosition, out Vector3d rawVelocity);
            Vector3d stockPosition = stock.GetRelativePositionAtUT(ut);
            Vector3d stockVelocity = stock.GetOrbitalVelocityAtUT(ut);

            Assert.Equal(rawPosition.z, stockPosition.z, 6);
            Assert.Equal(rawVelocity.z, stockVelocity.z, 6);
            AssertRelative(Magnitude(rawPosition), Magnitude(stockPosition), 1e-12, "|r|");
            AssertRelative(Magnitude(rawVelocity), Magnitude(stockVelocity), 1e-12, "|v|");
            AssertRelative(
                AngleBetweenDegrees(rawPosition, rawVelocity),
                AngleBetweenDegrees(stockPosition, stockVelocity),
                1e-9,
                "flight-path angle");

            // ...and the plane angle of both halves shifts by the SAME amount.
            double positionShift = NormalizeDegrees(
                PlaneAngleDegrees(rawPosition) - PlaneAngleDegrees(stockPosition));
            double velocityShift = NormalizeDegrees(
                PlaneAngleDegrees(rawVelocity) - PlaneAngleDegrees(stockVelocity));
            Assert.Equal(positionShift, velocityShift, 6);
            Assert.Equal(NormalizeDegrees(MeasuredZupAngleDegrees), positionShift, 6);
        }

        // ------------------------------------------------------------------
        // 3. The measured site-4 reading, reproduced from the closed form
        // ------------------------------------------------------------------

        /// <summary>
        /// Reproduces the H9 site-4 reading from the closed form on the fixture the flight
        /// actually ran: a PRELAUNCH vessel on the KSC pad, whose orbit is the surface-rotation
        /// ellipse (a=300.8 km, e=0.9948, at apoapsis) around a near-equatorial Kerbin. Encoding
        /// the orbital frame from the raw element-frame state and decoding it from stock's Zup
        /// state - which is what the PRE-FIX producer and the (unchanged) consumer respectively
        /// did - is off by an angle in the band the two flights measured (133.123 deg before the
        /// radial half was corrected, 131.066 deg after).
        /// <para>
        /// HISTORICAL, and deliberately so: it reaches both states through the test port, not
        /// through the production propagator, so it keeps saying what the flight measured even
        /// after Finding A moved the producer. The live claim that the SHIPPING producer now
        /// cancels lives in <c>StockOrbitFrameSeamTests</c>.
        /// </para>
        /// <para>
        /// This is the cell that makes the diagnosis falsifiable: it derives a number the game
        /// produced, from stock's decompiled source, with no live KSP.
        /// </para>
        /// </summary>
        [Fact]
        public void MeasuredSite4Reading_IsReproducedByTheZupRotationAlone()
        {
            // The pad state the run logged: zup=(-496283.195, 337326.756, -1018.081), whose
            // orbital velocity is Kerbin's surface rotation at that point.
            var stockPosition = new Vector3d(-496283.195, 337326.756, -1018.081);
            const double kerbinRotationPeriod = 21549.425;
            double omega = (Math.PI * 2.0) / kerbinRotationPeriod;
            var stockVelocity = new Vector3d(-omega * stockPosition.y, omega * stockPosition.x, 0.0);

            var zup = StockOrbitPort.PlanetaryZup(MeasuredZupAngleDegrees);
            // The producer reads the SEGMENT's elements, which live in the raw element frame:
            // raw = Zup.LocalToWorld(stock).
            Vector3d rawPosition = zup.LocalToWorld(stockPosition);
            Vector3d rawVelocity = zup.LocalToWorld(stockVelocity);

            // Shipping producer: world radial + Zup velocity, both taken from the RAW state.
            UnityEngine.Quaternion frozenWorldRotation =
                TrajectoryMath.PureNormalize(new UnityEngine.Quaternion(0.33308f, -0.62567f, -0.62442f, -0.32816f));
            UnityEngine.Quaternion seeded = BallisticExtrapolator.ComputeOrbitalFrameRotationFromState(
                frozenWorldRotation,
                Parsek.IncompleteBallisticSceneExitFinalizer.SwizzleZupBodyRelativeToWorld(rawPosition),
                rawVelocity);

            // Shipping consumer: the same frame shape, but off the live (Zup) orbit.
            UnityEngine.Quaternion resolved = ResolveThroughConsumerFrame(seeded, stockPosition, stockVelocity);
            float angleError = TrajectoryMath.ComputeQuaternionAngleDegrees(resolved, frozenWorldRotation);

            Assert.InRange(angleError, 125f, 140f);

            // ...and correcting the producer into stock's frame collapses it.
            UnityEngine.Quaternion corrected = BallisticExtrapolator.ComputeOrbitalFrameRotationFromState(
                frozenWorldRotation,
                Parsek.IncompleteBallisticSceneExitFinalizer.SwizzleZupBodyRelativeToWorld(
                    zup.WorldToLocal(rawPosition)),
                zup.WorldToLocal(rawVelocity));
            float correctedError = TrajectoryMath.ComputeQuaternionAngleDegrees(
                ResolveThroughConsumerFrame(corrected, stockPosition, stockVelocity),
                frozenWorldRotation);
            Assert.True(correctedError < 0.01f,
                $"correcting the producer into stock's orbit frame left {correctedError} deg of error");
        }

        /// <summary>
        /// The <c>hasOfr</c> branch of <c>ParsekFlight.ComputeOrbitalRotation</c>, verbatim, off a
        /// stock-frame (Zup) state: <c>LookRotation(zupVelocity, worldRadial) * orbitalFrameRotation</c>.
        /// </summary>
        private static UnityEngine.Quaternion ResolveThroughConsumerFrame(
            UnityEngine.Quaternion orbitalFrameRotation, Vector3d stockPosition, Vector3d stockVelocity)
        {
            UnityEngine.Vector3 velocity = ((UnityEngine.Vector3)stockVelocity).normalized;
            UnityEngine.Vector3 radialOut =
                ((UnityEngine.Vector3)Parsek.IncompleteBallisticSceneExitFinalizer
                    .SwizzleZupBodyRelativeToWorld(stockPosition)).normalized;
            return TrajectoryMath.PureMultiply(
                TrajectoryMath.PureLookRotation(velocity, radialOut), orbitalFrameRotation);
        }

        // ------------------------------------------------------------------
        // 4. A SECOND, independent finding the cross-check turned up
        // ------------------------------------------------------------------

        /// <summary>
        /// SEPARATE FINDING, not the site-4 cause: above e = 0.8 stock switches to
        /// <c>solveEccentricAnomalyExtremeEcc</c> (a fixed 8-iteration Laguerre-style solve seeded
        /// at <c>M + 0.85 e sign(sin M)</c>) while <c>TwoBodyOrbit</c> keeps plain Newton seeded at
        /// <c>E = M</c>. At e = 0.9948 - which is not exotic, it is the eccentricity of EVERY
        /// landed or prelaunch vessel's surface-rotation orbit - Newton fails to converge in its
        /// 16 iterations near periapsis and lands double-digit degrees of true anomaly away from
        /// the root, where stock stays exact.
        /// <para>
        /// This cell asserts the DISAGREEMENT rather than a fix: it is a real robustness gap in
        /// the element-seeded propagator, filed with the frame finding, and pinning it here means
        /// a later fix has a test that flips instead of a bug nobody wrote down.
        /// </para>
        /// </summary>
        [Fact]
        public void HighEccentricity_TwoBodyOrbitNewtonDivergesFromStocksExtremeEccSolver()
        {
            // Identity Zup so the element-frame boundary is inert and only the SOLVER is measured.
            InstallZup(0.0);
            const double eccentricity = 0.9948;
            const double semiMajorAxis = 300818.761;

            double worstDisagreementDegrees = 0.0;
            for (int k = 0; k < 360; k++)
            {
                double meanAnomaly = k * DegToRad;
                OrbitSegment segment = BuildSegment(
                    inclinationDeg: 0.0972,
                    lanDeg: 235.7958,
                    argPeDeg: 90.0,
                    eccentricity: eccentricity,
                    semiMajorAxis: semiMajorAxis,
                    meanAnomalyAtEpoch: meanAnomaly,
                    epoch: 0.0);
                Assert.True(TwoBodyOrbit.TryCreateFromSegment(segment, KerbinMu, out TwoBodyOrbit orbit));
                var stock = StockOrbitPort.FromSegment(segment, KerbinMu, StockOrbitPort.IdentityZup());

                orbit.GetStateAtUT(0.0, out Vector3d position, out _);
                double disagreement = AngleBetweenDegrees(position, stock.GetRelativePositionAtUT(0.0));
                if (disagreement > worstDisagreementDegrees)
                    worstDisagreementDegrees = disagreement;
            }

            Assert.True(worstDisagreementDegrees > 5.0,
                "TwoBodyOrbit's Newton solve now tracks stock's extreme-eccentricity solver; if "
                + "that is a deliberate fix, retire this cell and the matching todo entry "
                + $"(worst disagreement {worstDisagreementDegrees:F3} deg)");
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static OrbitSegment BuildSegment(
            double inclinationDeg, double lanDeg, double argPeDeg, double eccentricity,
            double semiMajorAxis, double meanAnomalyAtEpoch, double epoch)
        {
            return new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = epoch,
                endUT = epoch + 100.0,
                inclination = inclinationDeg,
                longitudeOfAscendingNode = lanDeg,
                argumentOfPeriapsis = argPeDeg,
                eccentricity = eccentricity,
                semiMajorAxis = semiMajorAxis,
                meanAnomalyAtEpoch = meanAnomalyAtEpoch,
                epoch = epoch
            };
        }

        private static double Magnitude(Vector3d v)
        {
            return Math.Sqrt((v.x * v.x) + (v.y * v.y) + (v.z * v.z));
        }

        private static double AngleBetweenDegrees(Vector3d a, Vector3d b)
        {
            double denominator = Magnitude(a) * Magnitude(b);
            if (denominator <= 0.0)
                return 0.0;
            double cosine = ((a.x * b.x) + (a.y * b.y) + (a.z * b.z)) / denominator;
            if (cosine > 1.0) cosine = 1.0;
            if (cosine < -1.0) cosine = -1.0;
            return Math.Acos(cosine) / DegToRad;
        }

        private static double PlaneAngleDegrees(Vector3d v)
        {
            return NormalizeDegrees(Math.Atan2(v.y, v.x) / DegToRad);
        }

        private static double NormalizeDegrees(double degrees)
        {
            double normalized = degrees % 360.0;
            return normalized < 0.0 ? normalized + 360.0 : normalized;
        }

        private static void AssertRelative(double expected, double actual, double tolerance, string what)
        {
            double scale = Math.Max(Math.Abs(expected), 1e-12);
            double error = Math.Abs(expected - actual) / scale;
            Assert.True(error <= tolerance,
                $"{what}: expected {expected}, got {actual} (relative error {error} > {tolerance})");
        }

        private static void AssertVectorsAgree(Vector3d expected, Vector3d actual, double tolerance, string what)
        {
            double scale = Math.Max(Magnitude(expected), 1e-12);
            double error = Magnitude(new Vector3d(
                expected.x - actual.x, expected.y - actual.y, expected.z - actual.z)) / scale;
            Assert.True(error <= tolerance,
                $"{what}: expected ({expected.x},{expected.y},{expected.z}), "
                + $"got ({actual.x},{actual.y},{actual.z}) (relative error {error} > {tolerance})");
        }

        /// <summary>
        /// TEST-ONLY transcription of stock KSP 1.12.5's ELLIPTIC element-to-state chain, from
        /// <c>docs/decompiled/orbit.cs</c> and the separately decompiled
        /// <c>Planetarium.CelestialFrame</c>. Deliberately minimal: no hyperbolic branch, no
        /// <c>ZupAtT</c> inverse-rotation temp frame (neither <c>getRelativePositionAtUT</c> nor
        /// <c>getOrbitalVelocityAtUT</c> uses it), no SOI or patched-conic machinery. Every method
        /// name below matches the stock member it transcribes so a future reader can diff it
        /// against the decompile.
        /// </summary>
        internal sealed class StockOrbitPort
        {
            private double gravParameter;
            private double eccentricity;
            private double semiMajorAxis;
            private double epoch;
            private double meanAnomalyAtEpoch;

            private Vector3d orbitFrameX;
            private Vector3d orbitFrameY;
            private CelestialFramePort zup;

            private double meanMotion;
            private double obTAtEpoch;
            private double semiLatusRectum;

            public double Period { get; private set; }

            public static StockOrbitPort FromSegment(
                OrbitSegment segment, double gravParameter, CelestialFramePort zup)
            {
                var port = new StockOrbitPort
                {
                    gravParameter = gravParameter,
                    eccentricity = segment.eccentricity,
                    semiMajorAxis = segment.semiMajorAxis,
                    epoch = segment.epoch,
                    meanAnomalyAtEpoch = segment.meanAnomalyAtEpoch,
                    zup = zup
                };
                port.Init(
                    segment.longitudeOfAscendingNode, segment.inclination, segment.argumentOfPeriapsis);
                return port;
            }

            public static CelestialFramePort IdentityZup()
            {
                return PlanetaryZup(0.0);
            }

            /// <summary><c>Planetarium.Awake</c>: <c>PlanetaryFrame(0, 90, inverseRotAngle)</c>.</summary>
            public static CelestialFramePort PlanetaryZup(double inverseRotAngleDegrees)
            {
                return CelestialFramePort.PlanetaryFrame(0.0, 90.0, inverseRotAngleDegrees);
            }

            /// <summary><c>Orbit.Init</c> (the parts the state chain reads).</summary>
            private void Init(double lanDegrees, double inclinationDegrees, double argPeDegrees)
            {
                CelestialFramePort frame =
                    CelestialFramePort.OrbitalFrame(lanDegrees, inclinationDegrees, argPeDegrees);
                orbitFrameX = frame.X;
                orbitFrameY = frame.Y;

                double absoluteSemiMajorAxis = Math.Abs(semiMajorAxis);
                meanMotion = Math.Sqrt(
                    gravParameter / (absoluteSemiMajorAxis * absoluteSemiMajorAxis * absoluteSemiMajorAxis));
                obTAtEpoch = meanAnomalyAtEpoch / meanMotion;
                Period = (Math.PI * 2.0) / meanMotion;
                semiLatusRectum = semiMajorAxis * (1.0 - (eccentricity * eccentricity));
            }

            /// <summary><c>Orbit.getObtAtUT</c>, elliptic branch.</summary>
            private double GetObtAtUT(double ut)
            {
                double obt = (ut - epoch + obTAtEpoch) % Period;
                if (obt > Period / 2.0)
                    obt -= Period;
                return obt;
            }

            /// <summary><c>Orbit.getRelativePositionAtUT</c> -&gt; <c>getRelativePositionFromTrueAnomaly</c>.</summary>
            public Vector3d GetRelativePositionAtUT(double ut)
            {
                double trueAnomaly = TrueAnomalyAtT(GetObtAtUT(ut));
                double cosine = Math.Cos(trueAnomaly);
                double sine = Math.Sin(trueAnomaly);
                double radius = semiLatusRectum / (1.0 + (eccentricity * cosine));
                return zup.WorldToLocal(Add(Scale(orbitFrameX, radius * cosine), Scale(orbitFrameY, radius * sine)));
            }

            /// <summary><c>Orbit.getOrbitalVelocityAtUT</c> -&gt; <c>getOrbitalVelocityAtTrueAnomaly</c>.</summary>
            public Vector3d GetOrbitalVelocityAtUT(double ut)
            {
                double trueAnomaly = TrueAnomalyAtT(GetObtAtUT(ut));
                double cosine = Math.Cos(trueAnomaly);
                double sine = Math.Sin(trueAnomaly);
                double scale = Math.Sqrt(
                    gravParameter / (semiMajorAxis * (1.0 - (eccentricity * eccentricity))));
                return zup.WorldToLocal(Add(
                    Scale(orbitFrameX, -sine * scale),
                    Scale(orbitFrameY, (cosine + eccentricity) * scale)));
            }

            /// <summary><c>Orbit.TrueAnomalyAtT</c>.</summary>
            private double TrueAnomalyAtT(double t)
            {
                return GetTrueAnomaly(SolveEccentricAnomaly(t * meanMotion));
            }

            /// <summary>
            /// <c>Orbit.solveEccentricAnomaly</c>'s elliptic dispatch: the extreme-eccentricity
            /// solver at or above e = 0.8, the standard Newton below it.
            /// </summary>
            private double SolveEccentricAnomaly(double meanAnomaly)
            {
                return eccentricity < 0.8
                    ? SolveEccentricAnomalyStd(meanAnomaly)
                    : SolveEccentricAnomalyExtremeEcc(meanAnomaly);
            }

            /// <summary>
            /// <c>Orbit.solveEccentricAnomalyStd</c>. Stock's loop has no iteration cap; the cap
            /// here only keeps a pathological input from hanging the test run, and the assertion
            /// makes a capped solve loud rather than silent.
            /// </summary>
            private double SolveEccentricAnomalyStd(double meanAnomaly, double maxError = 1e-7)
            {
                double delta = 1.0;
                double eccentricAnomaly = meanAnomaly
                    + (eccentricity * Math.Sin(meanAnomaly))
                    + (0.5 * eccentricity * eccentricity * Math.Sin(2.0 * meanAnomaly));
                int iterations = 0;
                while (Math.Abs(delta) > maxError)
                {
                    double solved = eccentricAnomaly - (eccentricity * Math.Sin(eccentricAnomaly));
                    delta = (meanAnomaly - solved) / (1.0 - (eccentricity * Math.Cos(eccentricAnomaly)));
                    eccentricAnomaly += delta;
                    Assert.True(++iterations < 64,
                        "the stock-port standard Kepler solve did not converge; the fixture is "
                        + "outside the regime this port models");
                }

                return eccentricAnomaly;
            }

            /// <summary><c>Orbit.solveEccentricAnomalyExtremeEcc</c>, 8 fixed iterations.</summary>
            private double SolveEccentricAnomalyExtremeEcc(double meanAnomaly, int iterations = 8)
            {
                double eccentricAnomaly =
                    meanAnomaly + (0.85 * eccentricity * Math.Sign(Math.Sin(meanAnomaly)));
                for (int i = 0; i < iterations; i++)
                {
                    double sine = eccentricity * Math.Sin(eccentricAnomaly);
                    double cosine = eccentricity * Math.Cos(eccentricAnomaly);
                    double residual = eccentricAnomaly - sine - meanAnomaly;
                    double firstDerivative = 1.0 - cosine;
                    double secondDerivative = sine;
                    eccentricAnomaly += (-5.0 * residual)
                        / (firstDerivative
                            + (Math.Sign(firstDerivative)
                                * Math.Sqrt(Math.Abs(
                                    (16.0 * firstDerivative * firstDerivative)
                                    - (20.0 * residual * secondDerivative)))));
                }

                return eccentricAnomaly;
            }

            /// <summary><c>Orbit.GetTrueAnomaly</c>, elliptic branch.</summary>
            private double GetTrueAnomaly(double eccentricAnomaly)
            {
                return 2.0 * Math.Atan2(
                    Math.Sqrt(1.0 + eccentricity) * Math.Sin(eccentricAnomaly / 2.0),
                    Math.Sqrt(1.0 - eccentricity) * Math.Cos(eccentricAnomaly / 2.0));
            }

            private static Vector3d Scale(Vector3d v, double s)
            {
                return new Vector3d(v.x * s, v.y * s, v.z * s);
            }

            private static Vector3d Add(Vector3d a, Vector3d b)
            {
                return new Vector3d(a.x + b.x, a.y + b.y, a.z + b.z);
            }
        }

        /// <summary>
        /// TEST-ONLY transcription of <c>Planetarium.CelestialFrame</c> (decompiled from
        /// <c>Assembly-CSharp.dll</c>).
        /// </summary>
        internal struct CelestialFramePort
        {
            public Vector3d X;
            public Vector3d Y;
            public Vector3d Z;

            public Vector3d WorldToLocal(Vector3d r)
            {
                return new Vector3d(Dot(r, X), Dot(r, Y), Dot(r, Z));
            }

            public Vector3d LocalToWorld(Vector3d r)
            {
                return new Vector3d(
                    (r.x * X.x) + (r.y * Y.x) + (r.z * Z.x),
                    (r.x * X.y) + (r.y * Y.y) + (r.z * Z.y),
                    (r.x * X.z) + (r.y * Y.z) + (r.z * Z.z));
            }

            public static CelestialFramePort SetFrame(double a, double b, double c)
            {
                double cosA = Math.Cos(a);
                double sinA = Math.Sin(a);
                double cosB = Math.Cos(b);
                double sinB = Math.Sin(b);
                double cosC = Math.Cos(c);
                double sinC = Math.Sin(c);
                return new CelestialFramePort
                {
                    X = new Vector3d(
                        (cosA * cosC) - (sinA * cosB * sinC),
                        (sinA * cosC) + (cosA * cosB * sinC),
                        sinB * sinC),
                    Y = new Vector3d(
                        (-cosA * sinC) - (sinA * cosB * cosC),
                        (-sinA * sinC) + (cosA * cosB * cosC),
                        sinB * cosC),
                    Z = new Vector3d(sinA * sinB, -cosA * sinB, cosB)
                };
            }

            public static CelestialFramePort OrbitalFrame(double lan, double inclination, double argPe)
            {
                return SetFrame(lan * DegToRad, inclination * DegToRad, argPe * DegToRad);
            }

            public static CelestialFramePort PlanetaryFrame(double rightAscension, double declination, double rotation)
            {
                return SetFrame(
                    (rightAscension - 90.0) * DegToRad,
                    (declination - 90.0) * DegToRad,
                    (rotation + 90.0) * DegToRad);
            }

            private static double Dot(Vector3d a, Vector3d b)
            {
                return (a.x * b.x) + (a.y * b.y) + (a.z * b.z);
            }
        }
    }
}
