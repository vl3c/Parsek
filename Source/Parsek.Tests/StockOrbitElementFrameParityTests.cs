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
    /// production API now.
    /// </para>
    /// <para>
    /// <b>FINDING B IS ALSO FIXED</b> (2026-08-05, branch <c>twobody-extreme-ecc-solver</c>).
    /// <c>TwoBodyOrbit</c> used to run plain Newton seeded at <c>E = M</c> at every eccentricity;
    /// it now ports stock's <c>solveEccentricAnomaly</c> dispatch and BOTH of its branches, so the
    /// two propagators agree in every eccentricity regime rather than only below 0.8. Section 4
    /// at the bottom of this file used to pin that disagreement and now pins the agreement, on
    /// the same fixture and the same full mean-anomaly sweep - and the pad-fixture cell, which
    /// deliberately sampled near apoapsis only because the solver divergence dominated everywhere
    /// else, now sweeps a whole period.
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
        /// Eccentricities stay at or below 0.9 here because this cell is about the FRAME; the
        /// solver's own regimes - both sides of stock's 0.8 dispatch, and extreme eccentricity in
        /// particular - are covered in section 4 by
        /// <see cref="HighEccentricity_TwoBodyOrbitTracksStocksExtremeEccSolverOverTheFullSweep"/>
        /// and <see cref="DispatchThreshold_IsContinuousAcrossTheTwoSolvers"/>.
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
        /// SAMPLED OVER THE WHOLE ORBIT since 2026-08-05, branch
        /// <c>twobody-extreme-ecc-solver</c>. It used to sample near APOAPSIS ONLY, and that
        /// restriction was Finding B rather than a weakening of this claim: at e = 0.9948
        /// <c>TwoBodyOrbit</c>'s plain Newton solve diverged from stock's
        /// <c>solveEccentricAnomalyExtremeEcc</c> near periapsis by double-digit degrees of true
        /// anomaly, so a full-orbit sweep would have red'd on the SOLVER and said nothing about
        /// the FRAME. Finding B is fixed - the solve is now stock's dispatch, pinned by
        /// <see cref="HighEccentricity_TwoBodyOrbitTracksStocksExtremeEccSolverOverTheFullSweep"/>
        /// - so the frame claim is stated where it always should have been: at every anomaly on
        /// the fixture the flight ran, periapsis included.
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

            // A full period in 64 steps, from periapsis through apoapsis and back.
            for (int i = 0; i <= 64; i++)
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
        // 4. THE SOLVER (Finding B), fixed 2026-08-05 on branch
        //    twobody-extreme-ecc-solver: these cells pin the AGREEMENT
        // ------------------------------------------------------------------

        /// <summary>
        /// THE FLIPPED PIN. This cell was written on 2026-08-05 as
        /// <c>HighEccentricity_TwoBodyOrbitNewtonDivergesFromStocksExtremeEccSolver</c> and it
        /// asserted the DISAGREEMENT: above e = 0.8 stock dispatches to
        /// <c>solveEccentricAnomalyExtremeEcc</c> (a fixed 8-iteration Laguerre-style solve seeded
        /// at <c>M + 0.85 e sign(sin M)</c>) while <c>TwoBodyOrbit</c> kept plain Newton seeded at
        /// <c>E = M</c> and capped at 16 iterations. At e = 0.9948 - not exotic: the
        /// surface-rotation ellipse of EVERY landed or prelaunch vessel, and the fixture the H9
        /// probes fly on - that Newton solve failed to converge near periapsis and landed
        /// double-digit degrees of true anomaly from the root over this exact sweep, peaking
        /// around 134 deg, where stock stayed exact throughout. The cell's failure message said to
        /// retire it if the divergence was ever deliberately fixed.
        /// <para>
        /// It was, on branch <c>twobody-extreme-ecc-solver</c> (Finding B of the todo entry
        /// "TwoBodyOrbit's element-seeded propagation works in KSP's raw element frame, not stock
        /// Orbit's"): <c>TwoBodyOrbit.SolveEllipticKepler</c> now ports stock's 0.8 dispatch and
        /// BOTH branches. So the cell is kept and INVERTED - same fixture, same full 360-sample
        /// mean-anomaly sweep, agreement instead of divergence - because the sweep that exposed
        /// the gap is the sweep that proves it closed.
        /// </para>
        /// </summary>
        [Fact]
        public void HighEccentricity_TwoBodyOrbitTracksStocksExtremeEccSolverOverTheFullSweep()
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

                orbit.GetStateAtUT(0.0, out Vector3d position, out Vector3d velocity);
                Vector3d stockPosition = stock.GetRelativePositionAtUT(0.0);

                AssertVectorsAgree(stockPosition, position, 1e-9, $"position at M={k} deg");
                AssertVectorsAgree(
                    stock.GetOrbitalVelocityAtUT(0.0), velocity, 1e-9, $"velocity at M={k} deg");

                double disagreement = AngleBetweenDegrees(position, stockPosition);
                if (disagreement > worstDisagreementDegrees)
                    worstDisagreementDegrees = disagreement;
            }

            // The SHARP claim is the per-sample vector agreement above (1e-9 relative). This
            // angular summary is the one the pre-fix cell measured at ~134 deg, kept so the
            // before/after reads on one number - but its floor is the arc-cosine's, not the
            // solver's: acos loses half the mantissa near an argument of 1, so two vectors that
            // agree to double precision still report ~sqrt(2 * 2.2e-16) rad = 8e-7 deg. The bound
            // is set an order of magnitude above that floor and four orders below the old
            // divergence.
            Assert.True(worstDisagreementDegrees < 1e-4,
                "the extreme-eccentricity solver has drifted from stock's; worst true-anomaly "
                + $"disagreement {worstDisagreementDegrees:E3} deg over the full mean-anomaly sweep");
        }

        /// <summary>
        /// THE CONVERGENCE PROPERTY, stated against Kepler's equation itself rather than against
        /// the port - so it holds even if the transcription and the production copy were BOTH
        /// wrong in the same way. Over a full mean-anomaly sweep at extreme eccentricity,
        /// INCLUDING the periapsis neighbourhood where the old 16-iteration Newton solve gave up,
        /// the returned E satisfies <c>M = E - e sin E</c> to near machine precision.
        /// <para>
        /// Periapsis is the hard region on purpose: there <c>1 - e cos E</c> collapses toward
        /// <c>1 - e</c> (5.2e-3 at e = 0.9948), so a Newton step is amplified by ~190 and the
        /// iteration overshoots into the wrong basin. Laguerre's order-5 step is what tames that.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(0.8)]
        [InlineData(0.9)]
        [InlineData(0.9948)]
        [InlineData(0.999)]
        [InlineData(0.99999)]
        public void ExtremeEccentricity_TheSolvedAnomalySatisfiesKeplersEquation(double eccentricity)
        {
            double worstResidual = 0.0;
            double worstAtMeanAnomaly = 0.0;

            // 0.5-degree steps, so the periapsis neighbourhood (M near 0 and near 2pi) is sampled
            // densely rather than stepped over.
            for (int k = 0; k < 720; k++)
            {
                double meanAnomaly = k * 0.5 * DegToRad;
                double eccentricAnomaly =
                    TwoBodyOrbit.SolveEllipticKepler(meanAnomaly, eccentricity);
                Assert.False(double.IsNaN(eccentricAnomaly) || double.IsInfinity(eccentricAnomaly),
                    $"non-finite E at M={meanAnomaly}, e={eccentricity}");

                double residual = Math.Abs(
                    (eccentricAnomaly - (eccentricity * Math.Sin(eccentricAnomaly))) - meanAnomaly);
                if (residual > worstResidual)
                {
                    worstResidual = residual;
                    worstAtMeanAnomaly = meanAnomaly;
                }
            }

            Assert.True(worstResidual < 1e-10,
                $"Kepler residual {worstResidual:E3} at M={worstAtMeanAnomaly} rad, e={eccentricity} "
                + "- the extreme-eccentricity solve is not converging");
        }

        /// <summary>
        /// NO DISCONTINUITY AT THE DISPATCH THRESHOLD. The 0.8 boundary is stock's, and it is a
        /// switch between two solvers of the SAME equation, not between two models - so straddling
        /// it must not move the answer by more than the eccentricity step itself explains.
        /// <para>
        /// Pinned because the threshold is the one place a porting mistake hides: a seed or a sign
        /// wrong in only one branch leaves both branches individually plausible and shows up only
        /// as a step at 0.8. The first two assertions keep the cell from passing vacuously if the
        /// dispatch ever stopped straddling the constant it names.
        /// </para>
        /// </summary>
        [Fact]
        public void DispatchThreshold_IsContinuousAcrossTheTwoSolvers()
        {
            const double step = 1e-9;
            double below = TwoBodyOrbit.ExtremeEccentricityThreshold - step;
            double above = TwoBodyOrbit.ExtremeEccentricityThreshold;

            Assert.Equal(
                TwoBodyOrbit.SolveEllipticKeplerStandard(1.0, below),
                TwoBodyOrbit.SolveEllipticKepler(1.0, below),
                12);
            Assert.Equal(
                TwoBodyOrbit.SolveEllipticKeplerExtremeEccentricity(1.0, above),
                TwoBodyOrbit.SolveEllipticKepler(1.0, above),
                12);

            double worstJump = 0.0;
            for (int k = 0; k < 720; k++)
            {
                double meanAnomaly = k * 0.5 * DegToRad;
                double jump = Math.Abs(
                    TwoBodyOrbit.SolveEllipticKepler(meanAnomaly, above)
                    - TwoBodyOrbit.SolveEllipticKepler(meanAnomaly, below));
                if (jump > worstJump)
                    worstJump = jump;
            }

            // dE/de = sin E / (1 - e cos E), bounded by 1/(1 - e) = 5 at e = 0.8, so a 1e-9
            // eccentricity step can legitimately move E by ~5e-9. Anything larger is a solver step.
            Assert.True(worstJump < 1e-7,
                $"the eccentric anomaly jumps by {worstJump:E3} rad across the 0.8 dispatch "
                + "threshold - the two branches disagree where they meet");
        }

        /// <summary>
        /// TOTALITY of the standard branch's iteration cap, which is this port's ONE structural
        /// deviation from stock (stock's <c>solveEccentricAnomalyStd</c> loop is uncapped; a
        /// propagator the physics-frame recorder calls before every sample may not hang).
        /// <para>
        /// Driven with a NON-PHYSICAL negative eccentricity, which is the only way to reach the
        /// cap at all: for <c>0 &lt;= e &lt; 0.8</c> Newton from stock's series seed converges in a
        /// handful of iterations at every mean anomaly, and no production entry point can deliver
        /// anything else - <c>AreSegmentElementsPropagatable</c> refuses <c>e &lt; 0</c> outright
        /// and <c>TryCreate</c> derives e as a vector magnitude. At e = -5 the equation
        /// <c>M = E + 5 sin E</c> is non-monotonic, <c>1 + 5 cos E</c> passes through zero, and
        /// Newton wanders instead of converging. The contract is: FINITE result, a rate-limited
        /// log naming the cap, and a return rather than a throw or a hang.
        /// </para>
        /// </summary>
        [Fact]
        public void StandardBranchIterationCap_ReturnsFiniteAndLogsRatherThanHanging()
        {
            var logLines = new System.Collections.Generic.List<string>();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                // M = 25 deg with e = -5: Newton cycles and the cap is what ends the loop.
                double eccentricAnomaly = TwoBodyOrbit.SolveEllipticKepler(25.0 * DegToRad, -5.0);

                Assert.False(double.IsNaN(eccentricAnomaly) || double.IsInfinity(eccentricAnomaly),
                    $"the capped solve must return a finite best estimate, got {eccentricAnomaly}");
                Assert.Contains(logLines, l =>
                    l.Contains("[Extrapolator]") && l.Contains("iteration cap"));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
            }
        }

        /// <summary>
        /// NaN-IN, NaN-OUT survives the port. Both stock branches call <c>Math.Sign</c>, which
        /// THROWS <c>ArithmeticException</c> on NaN instead of propagating it - the exact
        /// mechanism that once cost the recorder every sample of every frame through the
        /// HYPERBOLIC solve (see <c>SolveHyperbolicKepler</c>'s docstring and the "AN ORBITAL EVA
        /// RECORDS NOTHING" todo entry). Porting two more <c>Math.Sign</c> call sites into the
        /// elliptic path re-opened that door, so the guard is pinned rather than assumed.
        /// </summary>
        [Theory]
        [InlineData(double.NaN, 0.9948)]
        [InlineData(double.PositiveInfinity, 0.9948)]
        [InlineData(double.NegativeInfinity, 0.3)]
        [InlineData(1.0, double.NaN)]
        [InlineData(1.0, double.PositiveInfinity)]
        public void NonFiniteInputs_ReturnNaNRatherThanThrowing(double meanAnomaly, double eccentricity)
        {
            double eccentricAnomaly = TwoBodyOrbit.SolveEllipticKepler(meanAnomaly, eccentricity);
            Assert.True(double.IsNaN(eccentricAnomaly),
                $"expected NaN for M={meanAnomaly}, e={eccentricity}, got {eccentricAnomaly}");
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
