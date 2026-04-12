using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for EnvironmentDetector.Classify (pure static, no setup needed)
    /// and EnvironmentHysteresis (stateful debounce wrapper).
    /// </summary>
    [Collection("Sequential")]
    public class EnvironmentDetectorTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public EnvironmentDetectorTests()
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

        #region Classify — Atmospheric

        [Fact]
        public void Classify_BelowAtmosphere_ReturnsAtmospheric()
        {
            // Kerbin: hasAtmosphere=true, altitude 5000m < atmosphereDepth 70000m, FLYING
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 5000,
                atmosphereDepth: 70000,
                situation: 8, // FLYING
                srfSpeed: 200,
                hasActiveThrust: true);

            Assert.Equal(SegmentEnvironment.Atmospheric, result);
        }

        [Fact]
        public void Classify_AtAtmosphereEdge_StillAtmospheric()
        {
            // Just below the atmosphere ceiling
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 69999.9,
                atmosphereDepth: 70000,
                situation: 8, // FLYING
                srfSpeed: 2200,
                hasActiveThrust: false);

            Assert.Equal(SegmentEnvironment.Atmospheric, result);
        }

        #endregion

        #region Classify — ExoPropulsive

        [Fact]
        public void Classify_AboveAtmosphereWithThrust_ReturnsExoPropulsive()
        {
            // Above Kerbin atmosphere, engine firing
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 80000,
                atmosphereDepth: 70000,
                situation: 16, // SUB_ORBITAL
                srfSpeed: 2200,
                hasActiveThrust: true);

            Assert.Equal(SegmentEnvironment.ExoPropulsive, result);
        }

        [Fact]
        public void Classify_NoAtmosphereBodyWithThrust_ReturnsExoPropulsive()
        {
            // Mun: no atmosphere, engine firing, FLYING
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: false,
                altitude: 50,
                atmosphereDepth: 0,
                situation: 8, // FLYING
                srfSpeed: 100,
                hasActiveThrust: true);

            Assert.Equal(SegmentEnvironment.ExoPropulsive, result);
        }

        #endregion

        #region Classify — ExoBallistic

        [Fact]
        public void Classify_AboveAtmosphereNoThrust_ReturnsExoBallistic()
        {
            // Above Kerbin atmosphere, coasting
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 80000,
                atmosphereDepth: 70000,
                situation: 16, // SUB_ORBITAL
                srfSpeed: 2200,
                hasActiveThrust: false);

            Assert.Equal(SegmentEnvironment.ExoBallistic, result);
        }

        [Fact]
        public void Classify_AtmosphereBoundaryExact_ReturnsExoBallistic()
        {
            // Altitude == atmosphereDepth is NOT atmospheric (uses strict <)
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 70000,
                atmosphereDepth: 70000,
                situation: 16, // SUB_ORBITAL
                srfSpeed: 2200,
                hasActiveThrust: false);

            Assert.Equal(SegmentEnvironment.ExoBallistic, result);
        }

        [Fact]
        public void Classify_NoAtmosphereBodyNoThrust_ReturnsExoBallistic()
        {
            // Mun: no atmosphere, no thrust, FLYING
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: false,
                altitude: 50,
                atmosphereDepth: 0,
                situation: 8, // FLYING
                srfSpeed: 100,
                hasActiveThrust: false);

            Assert.Equal(SegmentEnvironment.ExoBallistic, result);
        }

        #endregion

        #region Classify — ORBITING and ESCAPING situations

        [Fact]
        public void Classify_OrbitingAboveAtmosphereNoThrust_ReturnsExoBallistic()
        {
            // Kerbin: hasAtmosphere=true, above atmosphere, ORBITING, coasting
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 100000,
                atmosphereDepth: 70000,
                situation: 32, // ORBITING
                srfSpeed: 2200,
                hasActiveThrust: false);

            Assert.Equal(SegmentEnvironment.ExoBallistic, result);
        }

        [Fact]
        public void Classify_OrbitingAboveAtmosphereWithThrust_ReturnsExoPropulsive()
        {
            // Kerbin: hasAtmosphere=true, above atmosphere, ORBITING, engine firing
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 100000,
                atmosphereDepth: 70000,
                situation: 32, // ORBITING
                srfSpeed: 2200,
                hasActiveThrust: true);

            Assert.Equal(SegmentEnvironment.ExoPropulsive, result);
        }

        [Fact]
        public void Classify_EscapingNoThrust_ReturnsExoBallistic()
        {
            // ESCAPING trajectory, no thrust — coasting on escape
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 500000,
                atmosphereDepth: 70000,
                situation: 64, // ESCAPING
                srfSpeed: 3000,
                hasActiveThrust: false);

            Assert.Equal(SegmentEnvironment.ExoBallistic, result);
        }

        [Fact]
        public void Classify_EscapingWithThrust_ReturnsExoPropulsive()
        {
            // ESCAPING trajectory, engine firing
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 500000,
                atmosphereDepth: 70000,
                situation: 64, // ESCAPING
                srfSpeed: 3000,
                hasActiveThrust: true);

            Assert.Equal(SegmentEnvironment.ExoPropulsive, result);
        }

        [Fact]
        public void Classify_OrbitingAirlessBodyNoThrust_ReturnsExoBallistic()
        {
            // Mun: no atmosphere, ORBITING, coasting
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: false,
                altitude: 50000,
                atmosphereDepth: 0,
                situation: 32, // ORBITING
                srfSpeed: 500,
                hasActiveThrust: false);

            Assert.Equal(SegmentEnvironment.ExoBallistic, result);
        }

        #endregion

        #region Classify — SurfaceMobile

        [Fact]
        public void Classify_LandedMoving_ReturnsSurfaceMobile()
        {
            // Landed, moving > 0.1 m/s
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 75,
                atmosphereDepth: 70000,
                situation: 1, // LANDED
                srfSpeed: 5.0,
                hasActiveThrust: false);

            Assert.Equal(SegmentEnvironment.SurfaceMobile, result);
        }

        [Fact]
        public void Classify_SplashedMoving_ReturnsSurfaceMobile()
        {
            // Splashed, speed > 0.1 m/s
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 0,
                atmosphereDepth: 70000,
                situation: 2, // SPLASHED
                srfSpeed: 3.5,
                hasActiveThrust: false);

            Assert.Equal(SegmentEnvironment.SurfaceMobile, result);
        }

        [Fact]
        public void Classify_LandedOnAirlessBodyMoving_ReturnsSurfaceMobile()
        {
            // Mun: no atmosphere, LANDED, moving — should NOT return Atmospheric
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: false,
                altitude: 0,
                atmosphereDepth: 0,
                situation: 1, // LANDED
                srfSpeed: 2.0,
                hasActiveThrust: false);

            Assert.Equal(SegmentEnvironment.SurfaceMobile, result);
        }

        #endregion

        #region Classify — SurfaceStationary

        [Fact]
        public void Classify_LandedStationary_ReturnsSurfaceStationary()
        {
            // Landed, essentially not moving
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 75,
                atmosphereDepth: 70000,
                situation: 1, // LANDED
                srfSpeed: 0.05,
                hasActiveThrust: false);

            Assert.Equal(SegmentEnvironment.SurfaceStationary, result);
        }

        [Fact]
        public void Classify_Prelaunch_ReturnsSurfaceStationary()
        {
            // PRELAUNCH with zero speed
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 75,
                atmosphereDepth: 70000,
                situation: 4, // PRELAUNCH
                srfSpeed: 0.0,
                hasActiveThrust: false);

            Assert.Equal(SegmentEnvironment.SurfaceStationary, result);
        }

        [Fact]
        public void Classify_LandedOnAirlessBodyStationary_ReturnsSurfaceStationary()
        {
            // Mun: no atmosphere, LANDED, stationary — should NOT return Atmospheric
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: false,
                altitude: 0,
                atmosphereDepth: 0,
                situation: 1, // LANDED
                srfSpeed: 0.01,
                hasActiveThrust: false);

            Assert.Equal(SegmentEnvironment.SurfaceStationary, result);
        }

        [Fact]
        public void Classify_SurfaceSpeedExactlyAtThreshold_ReturnsSurfaceStationary()
        {
            // srfSpeed == 0.1 is NOT > 0.1, so stationary
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 75,
                atmosphereDepth: 70000,
                situation: 1, // LANDED
                srfSpeed: 0.1,
                hasActiveThrust: false);

            Assert.Equal(SegmentEnvironment.SurfaceStationary, result);
        }

        #endregion

        #region Classify — Approach (airless body near surface)

        [Fact]
        public void Classify_AirlessBelowApproachNoThrust_ReturnsApproach()
        {
            // Mun: no atmosphere, below approach altitude, coasting
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: false,
                altitude: 15000,
                atmosphereDepth: 0,
                situation: 16, // SUB_ORBITAL
                srfSpeed: 500,
                hasActiveThrust: false,
                approachAltitude: 25000);

            Assert.Equal(SegmentEnvironment.Approach, result);
        }

        [Fact]
        public void Classify_AirlessBelowApproachWithThrust_ReturnsApproach()
        {
            // Powered descent on Mun — still Approach, not ExoPropulsive
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: false,
                altitude: 10000,
                atmosphereDepth: 0,
                situation: 16, // SUB_ORBITAL
                srfSpeed: 200,
                hasActiveThrust: true,
                approachAltitude: 25000);

            Assert.Equal(SegmentEnvironment.Approach, result);
        }

        [Fact]
        public void Classify_AirlessAboveApproach_ReturnsExoBallistic()
        {
            // Above approach altitude — normal exo classification
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: false,
                altitude: 30000,
                atmosphereDepth: 0,
                situation: 32, // ORBITING
                srfSpeed: 500,
                hasActiveThrust: false,
                approachAltitude: 25000);

            Assert.Equal(SegmentEnvironment.ExoBallistic, result);
        }

        [Fact]
        public void Classify_AirlessAtExactApproachAltitude_ReturnsExoBallistic()
        {
            // Altitude == approachAltitude is NOT < approachAltitude (strict <)
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: false,
                altitude: 25000,
                atmosphereDepth: 0,
                situation: 32, // ORBITING
                srfSpeed: 500,
                hasActiveThrust: false,
                approachAltitude: 25000);

            Assert.Equal(SegmentEnvironment.ExoBallistic, result);
        }

        [Fact]
        public void Classify_AtmosphericBodyWithApproachAltitude_StillReturnsAtmospheric()
        {
            // Kerbin with approachAltitude passed — atmosphere check wins
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 5000,
                atmosphereDepth: 70000,
                situation: 8, // FLYING
                srfSpeed: 200,
                hasActiveThrust: false,
                approachAltitude: 25000);

            Assert.Equal(SegmentEnvironment.Atmospheric, result);
        }

        [Fact]
        public void Classify_AirlessZeroApproachAltitude_ReturnsExo()
        {
            // approachAltitude=0 disables approach classification (backward compat)
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: false,
                altitude: 50,
                atmosphereDepth: 0,
                situation: 8, // FLYING
                srfSpeed: 100,
                hasActiveThrust: false,
                approachAltitude: 0);

            Assert.Equal(SegmentEnvironment.ExoBallistic, result);
        }

        [Fact]
        public void Classify_AirlessOrbitingBelowApproach_ReturnsExoBallistic()
        {
            // Stable low orbit on Mun at 15km (below 25km approach altitude)
            // ORBITING is Keplerian, not an approach — should NOT be classified as Approach
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: false,
                altitude: 15000,
                atmosphereDepth: 0,
                situation: 32, // ORBITING
                srfSpeed: 500,
                hasActiveThrust: false,
                approachAltitude: 25000);

            Assert.Equal(SegmentEnvironment.ExoBallistic, result);
        }

        [Fact]
        public void Classify_AirlessOrbitingBelowApproachWithThrust_ReturnsExoPropulsive()
        {
            // Thrusting in a low orbit — still not Approach
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: false,
                altitude: 15000,
                atmosphereDepth: 0,
                situation: 32, // ORBITING
                srfSpeed: 500,
                hasActiveThrust: true,
                approachAltitude: 25000);

            Assert.Equal(SegmentEnvironment.ExoPropulsive, result);
        }

        [Fact]
        public void Classify_LandedOnAirlessBelowApproach_ReturnsSurface()
        {
            // Landed takes priority over approach
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: false,
                altitude: 0,
                atmosphereDepth: 0,
                situation: 1, // LANDED
                srfSpeed: 0.0,
                hasActiveThrust: false,
                approachAltitude: 25000);

            Assert.Equal(SegmentEnvironment.SurfaceStationary, result);
        }

        [Fact]
        public void Classify_AirlessNearSurfaceFlyingSituation_ReturnsSurface()
        {
            // EVA kerbal on Mun with situation=FLYING (physics jitter) at 50m altitude.
            // Should return Surface, not Approach (#246).
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: false,
                altitude: 50,
                atmosphereDepth: 0,
                situation: 8, // FLYING
                srfSpeed: 2.0,
                hasActiveThrust: false,
                approachAltitude: 25000);

            Assert.Equal(SegmentEnvironment.SurfaceMobile, result);
        }

        [Fact]
        public void Classify_AirlessNearSurfaceSubOrbitalSituation_ReturnsSurface()
        {
            // EVA kerbal on Mun with situation=SUB_ORBITAL (jetpack hop) at 20m altitude.
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: false,
                altitude: 20,
                atmosphereDepth: 0,
                situation: 16, // SUB_ORBITAL
                srfSpeed: 0.05,
                hasActiveThrust: false,
                approachAltitude: 25000);

            Assert.Equal(SegmentEnvironment.SurfaceStationary, result);
        }

        [Fact]
        public void Classify_AirlessAt150m_ReturnsApproach_NotSurface()
        {
            // 150m is above the 100m near-surface threshold — normal Approach.
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: false,
                altitude: 150,
                atmosphereDepth: 0,
                situation: 8, // FLYING
                srfSpeed: 50,
                hasActiveThrust: false,
                approachAltitude: 25000);

            Assert.Equal(SegmentEnvironment.Approach, result);
        }

        [Fact]
        public void Classify_AtmosphericEvaNearGroundFlying_ReturnsSurface()
        {
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 75,
                atmosphereDepth: 70000,
                situation: 8, // FLYING
                srfSpeed: 1.5,
                hasActiveThrust: false,
                isEva: true,
                heightFromTerrain: 1.0,
                heightFromTerrainValid: true);

            Assert.Equal(SegmentEnvironment.SurfaceMobile, result);
        }

        [Fact]
        public void Classify_AtmosphericEvaWithoutValidTerrainHeight_RemainsAtmospheric()
        {
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 75,
                atmosphereDepth: 70000,
                situation: 8, // FLYING
                srfSpeed: 1.5,
                hasActiveThrust: false,
                isEva: true,
                heightFromTerrain: -1.0,
                heightFromTerrainValid: false);

            Assert.Equal(SegmentEnvironment.Atmospheric, result);
        }

        #endregion

        #region Classify — Surface takes priority over atmosphere

        [Fact]
        public void Classify_LandedInAtmosphere_ReturnsSurface_NotAtmospheric()
        {
            // Landed below atmosphere ceiling — surface situation takes priority
            var result = EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 75,
                atmosphereDepth: 70000,
                situation: 1, // LANDED
                srfSpeed: 0.0,
                hasActiveThrust: true);

            Assert.Equal(SegmentEnvironment.SurfaceStationary, result);
        }

        #endregion

        #region Hysteresis — No change

        [Fact]
        public void Hysteresis_SameClassificationRepeatedly_NoTransition()
        {
            var h = new EnvironmentHysteresis(SegmentEnvironment.Atmospheric);

            Assert.False(h.Update(SegmentEnvironment.Atmospheric, 100.0));
            Assert.False(h.Update(SegmentEnvironment.Atmospheric, 101.0));
            Assert.False(h.Update(SegmentEnvironment.Atmospheric, 102.0));

            Assert.Equal(SegmentEnvironment.Atmospheric, h.CurrentEnvironment);
        }

        #endregion

        #region Hysteresis — Immediate transition (zero debounce)

        [Fact]
        public void Hysteresis_AtmosphericToSurfaceStationary_DebouncedTransition()
        {
            // Atmospheric -> SurfaceStationary has 0.5s debounce (surface/atmospheric bounce)
            var h = new EnvironmentHysteresis(SegmentEnvironment.Atmospheric);

            Assert.False(h.Update(SegmentEnvironment.SurfaceStationary, 100.0));
            Assert.True(h.Update(SegmentEnvironment.SurfaceStationary, 100.5));

            Assert.Equal(SegmentEnvironment.SurfaceStationary, h.CurrentEnvironment);
        }

        [Fact]
        public void Hysteresis_AtmosphericToExoBallistic_ImmediateTransition()
        {
            // Atmospheric -> ExoBallistic has zero debounce (leaving atmosphere)
            var h = new EnvironmentHysteresis(SegmentEnvironment.Atmospheric);

            bool changed = h.Update(SegmentEnvironment.ExoBallistic, 100.0);

            Assert.True(changed);
            Assert.Equal(SegmentEnvironment.ExoBallistic, h.CurrentEnvironment);
        }

        [Fact]
        public void Hysteresis_SurfaceStationaryToAtmospheric_DebouncedTransition()
        {
            // SurfaceStationary -> Atmospheric has 0.5s debounce (surface/atmospheric bounce)
            var h = new EnvironmentHysteresis(SegmentEnvironment.SurfaceStationary);

            Assert.False(h.Update(SegmentEnvironment.Atmospheric, 100.0));
            Assert.True(h.Update(SegmentEnvironment.Atmospheric, 100.5));

            Assert.Equal(SegmentEnvironment.Atmospheric, h.CurrentEnvironment);
        }

        #endregion

        #region Hysteresis — Thrust debounce (1.0s)

        [Fact]
        public void Hysteresis_ThrustToggle_WithinDebounce_NoTransition()
        {
            var h = new EnvironmentHysteresis(SegmentEnvironment.ExoPropulsive);

            // Start pending ExoPropulsive -> ExoBallistic
            Assert.False(h.Update(SegmentEnvironment.ExoBallistic, 100.0));
            // 0.5s later, still pending (need 1.0s)
            Assert.False(h.Update(SegmentEnvironment.ExoBallistic, 100.5));

            Assert.Equal(SegmentEnvironment.ExoPropulsive, h.CurrentEnvironment);
        }

        [Fact]
        public void Hysteresis_ThrustToggle_AfterDebounce_TransitionConfirmed()
        {
            var h = new EnvironmentHysteresis(SegmentEnvironment.ExoPropulsive);

            // Start pending at UT=100
            Assert.False(h.Update(SegmentEnvironment.ExoBallistic, 100.0));
            // 1.0s later, debounce elapsed
            Assert.True(h.Update(SegmentEnvironment.ExoBallistic, 101.0));

            Assert.Equal(SegmentEnvironment.ExoBallistic, h.CurrentEnvironment);
        }

        [Fact]
        public void Hysteresis_ThrustToggle_ExactlyAtDebounce_TransitionConfirmed()
        {
            var h = new EnvironmentHysteresis(SegmentEnvironment.ExoBallistic);

            Assert.False(h.Update(SegmentEnvironment.ExoPropulsive, 200.0));
            // Exactly 1.0s later
            Assert.True(h.Update(SegmentEnvironment.ExoPropulsive, 201.0));

            Assert.Equal(SegmentEnvironment.ExoPropulsive, h.CurrentEnvironment);
        }

        #endregion

        #region Hysteresis — Surface speed debounce (3.0s)

        [Fact]
        public void Hysteresis_SurfaceSpeedToggle_WithinDebounce_NoTransition()
        {
            var h = new EnvironmentHysteresis(SegmentEnvironment.SurfaceMobile);

            Assert.False(h.Update(SegmentEnvironment.SurfaceStationary, 100.0));
            // 2.0s later, still pending (need 3.0s)
            Assert.False(h.Update(SegmentEnvironment.SurfaceStationary, 102.0));

            Assert.Equal(SegmentEnvironment.SurfaceMobile, h.CurrentEnvironment);
        }

        [Fact]
        public void Hysteresis_SurfaceSpeedToggle_AfterDebounce_TransitionConfirmed()
        {
            var h = new EnvironmentHysteresis(SegmentEnvironment.SurfaceMobile);

            Assert.False(h.Update(SegmentEnvironment.SurfaceStationary, 100.0));
            // 3.0s later, debounce elapsed
            Assert.True(h.Update(SegmentEnvironment.SurfaceStationary, 103.0));

            Assert.Equal(SegmentEnvironment.SurfaceStationary, h.CurrentEnvironment);
        }

        [Fact]
        public void Hysteresis_SurfaceStationaryToMobile_AfterDebounce_TransitionConfirmed()
        {
            var h = new EnvironmentHysteresis(SegmentEnvironment.SurfaceStationary);

            Assert.False(h.Update(SegmentEnvironment.SurfaceMobile, 500.0));
            Assert.False(h.Update(SegmentEnvironment.SurfaceMobile, 502.0));
            Assert.True(h.Update(SegmentEnvironment.SurfaceMobile, 503.0));

            Assert.Equal(SegmentEnvironment.SurfaceMobile, h.CurrentEnvironment);
        }

        #endregion

        #region Hysteresis — Cancel pending

        [Fact]
        public void Hysteresis_CancelPending_RevertBeforeDebounce_NoTransition()
        {
            var h = new EnvironmentHysteresis(SegmentEnvironment.ExoPropulsive);

            // Start pending ExoPropulsive -> ExoBallistic
            Assert.False(h.Update(SegmentEnvironment.ExoBallistic, 100.0));
            // Revert to ExoPropulsive before debounce
            Assert.False(h.Update(SegmentEnvironment.ExoPropulsive, 100.3));

            Assert.Equal(SegmentEnvironment.ExoPropulsive, h.CurrentEnvironment);

            // Even after more time, no transition (pending was cancelled)
            Assert.False(h.Update(SegmentEnvironment.ExoPropulsive, 102.0));
            Assert.Equal(SegmentEnvironment.ExoPropulsive, h.CurrentEnvironment);
        }

        #endregion

        #region Hysteresis — Different pending resets timer

        [Fact]
        public void Hysteresis_DifferentPending_ResetsTimer()
        {
            var h = new EnvironmentHysteresis(SegmentEnvironment.ExoPropulsive);

            // Start pending toward ExoBallistic at UT=100
            Assert.False(h.Update(SegmentEnvironment.ExoBallistic, 100.0));

            // Switch to different target (Atmospheric) at UT=100.5 — timer resets
            // ExoPropulsive -> Atmospheric has zero debounce, so it's immediate
            Assert.True(h.Update(SegmentEnvironment.Atmospheric, 100.5));

            Assert.Equal(SegmentEnvironment.Atmospheric, h.CurrentEnvironment);
        }

        [Fact]
        public void Hysteresis_DifferentPendingWithDebounce_ResetsTimer()
        {
            // Start at SurfaceMobile
            var h = new EnvironmentHysteresis(SegmentEnvironment.SurfaceMobile);

            // Pending toward SurfaceStationary (3s debounce) at UT=100
            Assert.False(h.Update(SegmentEnvironment.SurfaceStationary, 100.0));

            // At UT=102, switch to Atmospheric (0.5s debounce) — resets timer
            Assert.False(h.Update(SegmentEnvironment.Atmospheric, 102.0));
            // After 0.5s, debounce elapsed
            Assert.True(h.Update(SegmentEnvironment.Atmospheric, 102.5));

            Assert.Equal(SegmentEnvironment.Atmospheric, h.CurrentEnvironment);
        }

        #endregion

        #region GetDebounceFor

        [Fact]
        public void GetDebounceFor_ThrustToggle_Returns1s()
        {
            Assert.Equal(1.0, EnvironmentHysteresis.GetDebounceFor(
                SegmentEnvironment.ExoPropulsive, SegmentEnvironment.ExoBallistic));
            Assert.Equal(1.0, EnvironmentHysteresis.GetDebounceFor(
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.ExoPropulsive));
        }

        [Fact]
        public void GetDebounceFor_SurfaceSpeed_Returns3s()
        {
            Assert.Equal(3.0, EnvironmentHysteresis.GetDebounceFor(
                SegmentEnvironment.SurfaceMobile, SegmentEnvironment.SurfaceStationary));
            Assert.Equal(3.0, EnvironmentHysteresis.GetDebounceFor(
                SegmentEnvironment.SurfaceStationary, SegmentEnvironment.SurfaceMobile));
        }

        [Fact]
        public void GetDebounceFor_AtmosphericToExo_ReturnsZero()
        {
            Assert.Equal(0.0, EnvironmentHysteresis.GetDebounceFor(
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic));
            Assert.Equal(0.0, EnvironmentHysteresis.GetDebounceFor(
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoPropulsive));
        }

        [Fact]
        public void GetDebounceFor_SurfaceToAtmospheric_ReturnsHalfSecond()
        {
            Assert.Equal(0.5, EnvironmentHysteresis.GetDebounceFor(
                SegmentEnvironment.SurfaceStationary, SegmentEnvironment.Atmospheric));
            Assert.Equal(0.5, EnvironmentHysteresis.GetDebounceFor(
                SegmentEnvironment.SurfaceMobile, SegmentEnvironment.Atmospheric));
        }

        [Fact]
        public void GetDebounceFor_ExoToSurface_ReturnsZero()
        {
            Assert.Equal(0.0, EnvironmentHysteresis.GetDebounceFor(
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.SurfaceStationary));
            Assert.Equal(0.0, EnvironmentHysteresis.GetDebounceFor(
                SegmentEnvironment.ExoPropulsive, SegmentEnvironment.SurfaceMobile));
        }

        [Fact]
        public void GetDebounceFor_ApproachToExo_ReturnsApproachDebounce()
        {
            Assert.Equal(EnvironmentHysteresis.ApproachDebounceSeconds, EnvironmentHysteresis.GetDebounceFor(
                SegmentEnvironment.Approach, SegmentEnvironment.ExoBallistic));
            Assert.Equal(EnvironmentHysteresis.ApproachDebounceSeconds, EnvironmentHysteresis.GetDebounceFor(
                SegmentEnvironment.Approach, SegmentEnvironment.ExoPropulsive));
        }

        [Fact]
        public void GetDebounceFor_ExoToApproach_ReturnsApproachDebounce()
        {
            Assert.Equal(EnvironmentHysteresis.ApproachDebounceSeconds, EnvironmentHysteresis.GetDebounceFor(
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.Approach));
            Assert.Equal(EnvironmentHysteresis.ApproachDebounceSeconds, EnvironmentHysteresis.GetDebounceFor(
                SegmentEnvironment.ExoPropulsive, SegmentEnvironment.Approach));
        }

        [Fact]
        public void GetDebounceFor_ApproachToSurface_ReturnsApproachDebounce()
        {
            // Rough Mun landing / EVA hopping can bounce between LANDED and SUB_ORBITAL.
            // Uses ApproachDebounceSeconds (3.0s) to filter EVA physics jitter (#246).
            Assert.Equal(EnvironmentHysteresis.ApproachDebounceSeconds, EnvironmentHysteresis.GetDebounceFor(
                SegmentEnvironment.Approach, SegmentEnvironment.SurfaceMobile));
            Assert.Equal(EnvironmentHysteresis.ApproachDebounceSeconds, EnvironmentHysteresis.GetDebounceFor(
                SegmentEnvironment.Approach, SegmentEnvironment.SurfaceStationary));
            Assert.Equal(EnvironmentHysteresis.ApproachDebounceSeconds, EnvironmentHysteresis.GetDebounceFor(
                SegmentEnvironment.SurfaceMobile, SegmentEnvironment.Approach));
            Assert.Equal(EnvironmentHysteresis.ApproachDebounceSeconds, EnvironmentHysteresis.GetDebounceFor(
                SegmentEnvironment.SurfaceStationary, SegmentEnvironment.Approach));
        }

        #endregion

        #region Hysteresis — Log assertions

        [Fact]
        public void Hysteresis_ConfirmedTransition_LogsOldAndNewEnvironmentAndUT()
        {
            var h = new EnvironmentHysteresis(SegmentEnvironment.ExoPropulsive);

            h.Update(SegmentEnvironment.ExoBallistic, 100.0);
            h.Update(SegmentEnvironment.ExoBallistic, 101.0); // debounce elapsed

            Assert.Contains(logLines, l =>
                l.Contains("[INFO]") &&
                l.Contains("[Environment]") &&
                l.Contains("Environment transition") &&
                l.Contains("ExoPropulsive") &&
                l.Contains("ExoBallistic") &&
                l.Contains("101.00"));
        }

        [Fact]
        public void Hysteresis_ImmediateTransition_LogsDebounceZero()
        {
            // Use a transition that still has zero debounce (Atmospheric -> ExoBallistic)
            var h = new EnvironmentHysteresis(SegmentEnvironment.Atmospheric);

            h.Update(SegmentEnvironment.ExoBallistic, 200.0);

            Assert.Contains(logLines, l =>
                l.Contains("[INFO]") &&
                l.Contains("[Environment]") &&
                l.Contains("Environment transition") &&
                l.Contains("Atmospheric") &&
                l.Contains("ExoBallistic") &&
                l.Contains("immediate"));
        }

        [Fact]
        public void Hysteresis_Initialization_LogsVerbose()
        {
            logLines.Clear();
            var h = new EnvironmentHysteresis(SegmentEnvironment.ExoBallistic);

            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]") &&
                l.Contains("[Environment]") &&
                l.Contains("initialized") &&
                l.Contains("ExoBallistic"));
        }

        #endregion

        #region IsSurfaceEnvironment / IsSurfaceForAnchorDetection (bug #256 D-companion)

        [Theory]
        [InlineData(SegmentEnvironment.SurfaceMobile, true)]
        [InlineData(SegmentEnvironment.SurfaceStationary, true)]
        [InlineData(SegmentEnvironment.Atmospheric, false)]
        [InlineData(SegmentEnvironment.Approach, false)]
        [InlineData(SegmentEnvironment.ExoBallistic, false)]
        [InlineData(SegmentEnvironment.ExoPropulsive, false)]
        public void IsSurfaceEnvironment_OnlyTrueForSurfaceStates(SegmentEnvironment env, bool expected)
        {
            Assert.Equal(expected, EnvironmentDetector.IsSurfaceEnvironment(env));
        }

        [Fact]
        public void IsSurfaceForAnchorDetection_PrefersDebouncedEnvironment()
        {
            // KSP raw situation says FLYING (8), but the debounced environment is
            // SurfaceMobile (still walking on the Mun, just transient jitter to FLYING).
            // Function must trust the debounced classification.
            var result = EnvironmentDetector.IsSurfaceForAnchorDetection(
                envHint: SegmentEnvironment.SurfaceMobile,
                situation: 8); // FLYING

            Assert.True(result);
        }

        [Fact]
        public void IsSurfaceForAnchorDetection_DebouncedFlyingTrumpsLandedSituation()
        {
            // Inverse: KSP raw situation says LANDED (1), but the debounced environment
            // says Atmospheric. Trust the debounced classification.
            var result = EnvironmentDetector.IsSurfaceForAnchorDetection(
                envHint: SegmentEnvironment.Atmospheric,
                situation: 1); // LANDED

            Assert.False(result);
        }

        [Theory]
        [InlineData(1, true)]   // LANDED
        [InlineData(2, true)]   // SPLASHED
        [InlineData(4, true)]   // PRELAUNCH
        [InlineData(8, false)]  // FLYING
        [InlineData(16, false)] // SUB_ORBITAL
        [InlineData(32, false)] // ORBITING
        [InlineData(64, false)] // ESCAPING
        public void IsSurfaceForAnchorDetection_FallsBackToSituation_WhenNoEnvironment(
            int situation, bool expected)
        {
            // No environment classifier available (envHint = null) — must fall back to
            // raw situation.
            var result = EnvironmentDetector.IsSurfaceForAnchorDetection(
                envHint: null,
                situation: situation);

            Assert.Equal(expected, result);
        }

        // FlightRecorder.ResolveAnchorOnSurface integration tests — exercise the wiring
        // (null hysteresis branch + CurrentEnvironment read) that UpdateAnchorDetection
        // uses, without needing a Vessel instance. Catches refactor regressions where
        // someone changes UpdateAnchorDetection to bypass the helper.

        [Theory]
        [InlineData(1, true)]   // LANDED
        [InlineData(2, true)]   // SPLASHED
        [InlineData(4, true)]   // PRELAUNCH
        [InlineData(8, false)]  // FLYING
        [InlineData(16, false)] // SUB_ORBITAL
        [InlineData(32, false)] // ORBITING
        [InlineData(64, false)] // ESCAPING
        public void ResolveAnchorOnSurface_NullHysteresis_FallsBackToSituation(
            int situation, bool expected)
        {
            // Null hysteresis (e.g. early in StartRecording before
            // InitializeEnvironmentAndAnchorTracking runs) → must fall back to
            // raw situation. Exercises the `?.` null branch.
            var result = FlightRecorder.ResolveAnchorOnSurface(null, situation);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void ResolveAnchorOnSurface_LiveHysteresis_PrefersDebouncedClassification()
        {
            // Debounced says SurfaceMobile despite raw FLYING — the EVA-jitter case
            // (kerbal stepping above ground for one frame, situation flipping LANDED
            // → FLYING). Bug #246's pattern applied to anchor detection.
            var landedDebounce = new EnvironmentHysteresis(SegmentEnvironment.SurfaceMobile);
            Assert.True(FlightRecorder.ResolveAnchorOnSurface(landedDebounce, situation: 8));

            // Inverse: debounced says Atmospheric despite raw LANDED. Trust the
            // debounced classification.
            var atmoDebounce = new EnvironmentHysteresis(SegmentEnvironment.Atmospheric);
            Assert.False(FlightRecorder.ResolveAnchorOnSurface(atmoDebounce, situation: 1));
        }

        [Fact]
        public void ResolveAnchorOnSurface_LiveHysteresis_AllSurfaceEnvironmentsRecognized()
        {
            // Both surface environment values must produce true regardless of raw situation.
            var mobile = new EnvironmentHysteresis(SegmentEnvironment.SurfaceMobile);
            var stationary = new EnvironmentHysteresis(SegmentEnvironment.SurfaceStationary);
            Assert.True(FlightRecorder.ResolveAnchorOnSurface(mobile, situation: 8));      // FLYING raw
            Assert.True(FlightRecorder.ResolveAnchorOnSurface(stationary, situation: 16)); // SUB_ORBITAL raw
        }

        [Fact]
        public void ResolveAnchorOnSurface_LiveHysteresis_NonSurfaceEnvironmentsReturnFalse()
        {
            // Approach, ExoBallistic, ExoPropulsive must produce false regardless of raw situation.
            var approach = new EnvironmentHysteresis(SegmentEnvironment.Approach);
            var exoBallistic = new EnvironmentHysteresis(SegmentEnvironment.ExoBallistic);
            var exoPropulsive = new EnvironmentHysteresis(SegmentEnvironment.ExoPropulsive);
            Assert.False(FlightRecorder.ResolveAnchorOnSurface(approach, situation: 1));      // LANDED raw
            Assert.False(FlightRecorder.ResolveAnchorOnSurface(exoBallistic, situation: 2));  // SPLASHED raw
            Assert.False(FlightRecorder.ResolveAnchorOnSurface(exoPropulsive, situation: 4)); // PRELAUNCH raw
        }

        #endregion
    }
}
