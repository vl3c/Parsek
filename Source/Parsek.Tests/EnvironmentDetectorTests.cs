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

        #region Classify — Verbose logging

        [Fact]
        public void Classify_LogsInputParametersAndResult()
        {
            EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 5000,
                atmosphereDepth: 70000,
                situation: 8,
                srfSpeed: 200,
                hasActiveThrust: true);

            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]") &&
                l.Contains("[Environment]") &&
                l.Contains("Atmospheric"));
        }

        [Fact]
        public void Classify_Surface_LogsSituationAndSpeed()
        {
            EnvironmentDetector.Classify(
                hasAtmosphere: true,
                altitude: 75,
                atmosphereDepth: 70000,
                situation: 1,
                srfSpeed: 5.0,
                hasActiveThrust: false);

            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]") &&
                l.Contains("[Environment]") &&
                l.Contains("situation=1") &&
                l.Contains("SurfaceMobile"));
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
        public void Hysteresis_PendingStart_LogsVerbose()
        {
            var h = new EnvironmentHysteresis(SegmentEnvironment.ExoPropulsive);

            h.Update(SegmentEnvironment.ExoBallistic, 100.0);

            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]") &&
                l.Contains("[Environment]") &&
                l.Contains("pending") &&
                l.Contains("ExoPropulsive") &&
                l.Contains("ExoBallistic") &&
                l.Contains("started"));
        }

        [Fact]
        public void Hysteresis_PendingCancel_LogsVerbose()
        {
            var h = new EnvironmentHysteresis(SegmentEnvironment.ExoPropulsive);

            h.Update(SegmentEnvironment.ExoBallistic, 100.0); // start pending
            h.Update(SegmentEnvironment.ExoPropulsive, 100.3); // cancel

            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]") &&
                l.Contains("[Environment]") &&
                l.Contains("cancelled"));
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
    }
}
