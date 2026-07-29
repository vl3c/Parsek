using System;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the terminal-orbit spawn-safety guard against the BUG-C career-playtest geometry
    /// (`docs/dev/todo-and-known-bugs.md`, "BUG-C ... 2. Terminal-orbit ghost 'permanently
    /// abandoned' 3x").
    ///
    /// The BUG-C repro spawned a terminal-orbit recording at `ORBITING, body=Sun,
    /// alt~13.4 Gm` three times in one session. Two separate layers are involved and the
    /// tests below keep them apart, because conflating them is what made the bug hard to read:
    ///
    ///   1. The GEOMETRY layer (VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry ->
    ///      TerminalOrbitSpawnSafety.Evaluate) asks only "would this vessel materialize inside
    ///      an atmosphere or a planet?". The Sun is airless, so the safe altitude collapses to
    ///      zero and a finite 13.4 Gm heliocentric coast is geometrically safe. It ADMITS the
    ///      BUG-C spawn, by design.
    ///   2. The DURABILITY layer (TerminalOrbitSpawnSafety.MarkCannotSpawnSafely +
    ///      GetDeferredSpawnState, backed by the persisted Recording.TerminalSpawn* fields) is
    ///      what actually stops the retry: an earlier spawn-death is remembered and holds the
    ///      recording off. That flag being transient is what let the spawn repeat 3x.
    ///
    /// What the geometry layer DOES reject is the non-finite family - the NaN-orbit shape from
    /// BUG-C signature 1, where a degenerate on-rails orbit yields a NaN altitude or periapsis.
    /// Those are asserted below in both directions so the suite fails whichever way the
    /// predicate is broken.
    /// </summary>
    [Collection("Sequential")]
    public class TerminalOrbitSpawnSafetyGeometryTests : IDisposable
    {
        // The BUG-C spawn, from the 2026-06-07 career playtest log.
        private const double SunReferencedCoastAltitude = 13.4e9;
        private const double SunReferencedPeriapsisAltitude = 1.33e10;
        private const double SunReferencedApoapsisAltitude = 1.50e10;

        // Kerbin, the ordinary case.
        private const double KerbinAtmosphereDepth = 70000.0;

        public TerminalOrbitSpawnSafetyGeometryTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static double KerbinSafeAltitude =>
            TerminalOrbitSpawnSafety.ComputeSafeAltitude(
                KerbinAtmosphereDepth, TerminalOrbitSpawnSafety.DefaultSafetyMarginMeters);

        #region atmosphere-depth normalization

        [Fact]
        public void ResolveSpawnSafetyAtmosphereDepth_AirlessBody_ContributesNoDepth()
        {
            // An airless body must ignore whatever atmosphereDepth the CelestialBody carries.
            Assert.Equal(0.0,
                VesselSpawner.ResolveSpawnSafetyAtmosphereDepth(
                    bodyHasAtmosphere: false, bodyAtmosphereDepth: KerbinAtmosphereDepth));
        }

        [Fact]
        public void ResolveSpawnSafetyAtmosphereDepth_AtmosphericBody_UsesItsDepth()
        {
            Assert.Equal(KerbinAtmosphereDepth,
                VesselSpawner.ResolveSpawnSafetyAtmosphereDepth(
                    bodyHasAtmosphere: true, bodyAtmosphereDepth: KerbinAtmosphereDepth));
        }

        [Fact]
        public void SafeAltitude_IsAtmosphereDepthPlusMargin_AndCollapsesToZeroWhenAirless()
        {
            Assert.Equal(
                KerbinAtmosphereDepth + TerminalOrbitSpawnSafety.DefaultSafetyMarginMeters,
                TerminalOrbitSpawnSafety.ComputeSafeAltitude(
                    KerbinAtmosphereDepth, TerminalOrbitSpawnSafety.DefaultSafetyMarginMeters));

            // No atmosphere means no margin either - not "0 depth plus 5 km".
            Assert.Equal(0.0,
                TerminalOrbitSpawnSafety.ComputeSafeAltitude(
                    0.0, TerminalOrbitSpawnSafety.DefaultSafetyMarginMeters));
        }

        #endregion

        #region BUG-C geometry

        [Fact]
        public void BugC_HeliocentricCoast_IsGeometricallyAdmitted()
        {
            // Documented BUG-C behaviour: the geometry re-check PASSES this spawn. If this ever
            // flips, the spawn-safety layer grew a new responsibility and the BUG-C write-up
            // (which pins the fix in the durability layer instead) is stale.
            var decision = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                bodyHasAtmosphere: false,
                bodyAtmosphereDepth: 0.0,
                currentAltitude: SunReferencedCoastAltitude,
                periapsisAltitude: SunReferencedPeriapsisAltitude,
                apoapsisAltitude: SunReferencedApoapsisAltitude);

            Assert.Equal(TerminalOrbitSpawnSafetyAction.SpawnNow, decision.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonAboveSafeAltitude, decision.ReasonCode);
            Assert.Equal(0.0, decision.SafeAltitude);
        }

        [Fact]
        public void BugC_PersistedAbandon_HoldsTheRespawnTheGeometryWouldAdmit()
        {
            var rec = new Recording
            {
                RecordingId = "rec-r2-b2-s5",
                VesselName = "R2-B2-S5",
                TerminalOrbitBody = "Sun",
                TerminalOrbitSemiMajorAxis = 1.36e10,
            };

            // Nothing recorded yet: the recording is free to spawn.
            Assert.Equal(TerminalOrbitDeferredSpawnState.None,
                TerminalOrbitSpawnSafety.GetDeferredSpawnState(rec, 1000.0, out _));
            Assert.False(TerminalOrbitSpawnSafety.HasActiveHold(rec));

            // A prior spawn died and was reaped. That verdict - not the geometry - is what has
            // to survive to the next scene, which is the durability gap BUG-C fixed.
            var deathDecision = new TerminalOrbitSpawnSafetyDecision
            {
                Action = TerminalOrbitSpawnSafetyAction.CannotSpawnSafely,
                ReasonCode = TerminalOrbitSpawnSafety.ReasonSpawnedVesselDied,
                Reason = "spawned terminal orbit vessel died",
                CurrentAltitude = SunReferencedCoastAltitude,
                SafeAltitude = 0.0,
                PeriapsisAltitude = SunReferencedPeriapsisAltitude,
                ApoapsisAltitude = SunReferencedApoapsisAltitude,
            };
            TerminalOrbitSpawnSafety.MarkCannotSpawnSafely(rec, deathDecision, 2000.0, double.NaN);

            Assert.True(rec.TerminalSpawnCannotSpawnSafely);
            Assert.Equal(TerminalOrbitDeferredSpawnState.Hold,
                TerminalOrbitSpawnSafety.GetDeferredSpawnState(rec, 9.9e9, out string reason));
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonSpawnedVesselDied, reason);
            Assert.True(TerminalOrbitSpawnSafety.HasActiveHold(rec));

            // The hold is unconditional in UT: no later time releases a hard abandon.
            Assert.Equal(TerminalOrbitDeferredSpawnState.Hold,
                TerminalOrbitSpawnSafety.GetDeferredSpawnState(rec, double.MaxValue, out _));

            // And the two layers really are orthogonal - the same geometry still reads safe.
            Assert.Equal(TerminalOrbitSpawnSafetyAction.SpawnNow,
                VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                    false, 0.0,
                    SunReferencedCoastAltitude,
                    SunReferencedPeriapsisAltitude,
                    SunReferencedApoapsisAltitude).Action);
        }

        [Fact]
        public void BugC_NanDrivenAltitude_IsRejected()
        {
            // BUG-C signature 1's shape: a degenerate on-rails orbit whose propagated altitude
            // comes back NaN. Materializing a vessel there is what stock then exploded on.
            var decision = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                bodyHasAtmosphere: false,
                bodyAtmosphereDepth: 0.0,
                currentAltitude: double.NaN,
                periapsisAltitude: SunReferencedPeriapsisAltitude,
                apoapsisAltitude: SunReferencedApoapsisAltitude);

            Assert.Equal(TerminalOrbitSpawnSafetyAction.CannotSpawnSafely, decision.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonNonFinitePropagatedAltitude,
                decision.ReasonCode);
        }

        [Fact]
        public void BugC_NanDrivenPeriapsis_IsRejected()
        {
            var decision = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                bodyHasAtmosphere: false,
                bodyAtmosphereDepth: 0.0,
                currentAltitude: SunReferencedCoastAltitude,
                periapsisAltitude: double.NaN,
                apoapsisAltitude: SunReferencedApoapsisAltitude);

            Assert.Equal(TerminalOrbitSpawnSafetyAction.CannotSpawnSafely, decision.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonNonFinitePeriapsis, decision.ReasonCode);
        }

        [Fact]
        public void BugC_InfiniteEscapeAltitude_IsRejected()
        {
            // The Kerbin -> Sun escape leg of the repro: an unbounded hyperbolic element can
            // propagate to an infinite altitude, which is just as unspawnable as a NaN.
            var decision = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                bodyHasAtmosphere: false,
                bodyAtmosphereDepth: 0.0,
                currentAltitude: double.PositiveInfinity,
                periapsisAltitude: SunReferencedPeriapsisAltitude,
                apoapsisAltitude: double.PositiveInfinity);

            Assert.Equal(TerminalOrbitSpawnSafetyAction.CannotSpawnSafely, decision.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonNonFinitePropagatedAltitude,
                decision.ReasonCode);
        }

        [Fact]
        public void SubSurfaceAltitudeWithNonFiniteApoapsis_IsRejectedNotDeferred()
        {
            // Debris that clipped through terrain: below the safe altitude with no apoapsis to
            // wait for. There is no future safe UT, so this must abandon rather than defer.
            var decision = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                bodyHasAtmosphere: true,
                bodyAtmosphereDepth: KerbinAtmosphereDepth,
                currentAltitude: -500.0,
                periapsisAltitude: -900.0,
                apoapsisAltitude: double.NaN);

            Assert.Equal(TerminalOrbitSpawnSafetyAction.CannotSpawnSafely, decision.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonOrbitNeverClearsSafeAltitude,
                decision.ReasonCode);
        }

        #endregion

        #region sane geometry is admitted (the other direction)

        [Fact]
        public void SaneKerbinOrbit_IsAdmitted()
        {
            double safeAltitude = KerbinSafeAltitude;
            var decision = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                bodyHasAtmosphere: true,
                bodyAtmosphereDepth: KerbinAtmosphereDepth,
                currentAltitude: safeAltitude + 50000.0,
                periapsisAltitude: safeAltitude + 20000.0,
                apoapsisAltitude: safeAltitude + 120000.0);

            Assert.Equal(TerminalOrbitSpawnSafetyAction.SpawnNow, decision.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonAboveSafeAltitude, decision.ReasonCode);
            Assert.Equal(safeAltitude, decision.SafeAltitude);
        }

        [Fact]
        public void KerbinOrbitWithPeriapsisWellBelowSafeAltitude_IsRejected()
        {
            double safeAltitude = KerbinSafeAltitude;
            var decision = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                bodyHasAtmosphere: true,
                bodyAtmosphereDepth: KerbinAtmosphereDepth,
                currentAltitude: safeAltitude + 50000.0,
                periapsisAltitude: safeAltitude - 20000.0,
                apoapsisAltitude: safeAltitude + 120000.0);

            // Currently outside the atmosphere but the orbit dips back in: the spawn is barred
            // outright rather than deferred, because there is no altitude to wait for.
            Assert.Equal(TerminalOrbitSpawnSafetyAction.CannotSpawnSafely, decision.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonPeriapsisBelowSafeAltitude,
                decision.ReasonCode);
        }

        [Fact]
        public void KerbinOrbitCurrentlyInsideAtmosphereWithApoapsisAbove_Defers()
        {
            double safeAltitude = KerbinSafeAltitude;
            var decision = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                bodyHasAtmosphere: true,
                bodyAtmosphereDepth: KerbinAtmosphereDepth,
                currentAltitude: safeAltitude - 40000.0,
                periapsisAltitude: safeAltitude - 50000.0,
                apoapsisAltitude: safeAltitude + 60000.0);

            Assert.Equal(TerminalOrbitSpawnSafetyAction.DeferUntilSafe, decision.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonCurrentAltitudeBelowSafeAltitude,
                decision.ReasonCode);
        }

        [Fact]
        public void AirlessLowCoast_IsAdmittedWhereTheSameAltitudeWouldBeRejectedAtKerbin()
        {
            // Boundary semantics, stated as a contract rather than against the constant: the
            // SAME altitudes flip verdict purely on whether the body has an atmosphere.
            const double lowAltitude = 3000.0;
            const double lowPeriapsis = 2500.0;
            const double lowApoapsis = 12000.0;

            Assert.Equal(TerminalOrbitSpawnSafetyAction.SpawnNow,
                VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                    bodyHasAtmosphere: false,
                    bodyAtmosphereDepth: 0.0,
                    currentAltitude: lowAltitude,
                    periapsisAltitude: lowPeriapsis,
                    apoapsisAltitude: lowApoapsis).Action);

            Assert.Equal(TerminalOrbitSpawnSafetyAction.DeferUntilSafe,
                VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                    bodyHasAtmosphere: true,
                    bodyAtmosphereDepth: KerbinAtmosphereDepth,
                    currentAltitude: lowAltitude,
                    periapsisAltitude: lowPeriapsis,
                    apoapsisAltitude: KerbinSafeAltitude + 10000.0).Action);
        }

        #endregion

        #region deferral downgrade

        [Fact]
        public void DowngradeDeferral_TurnsASoftHoldIntoAHardAbandon()
        {
            double safeAltitude = KerbinSafeAltitude;
            var decision = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                bodyHasAtmosphere: true,
                bodyAtmosphereDepth: KerbinAtmosphereDepth,
                currentAltitude: safeAltitude - 40000.0,
                periapsisAltitude: safeAltitude - 50000.0,
                apoapsisAltitude: safeAltitude + 60000.0);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.DeferUntilSafe, decision.Action);

            // The orbit search found no future UT clearing the safe altitude.
            VesselSpawner.DowngradeTerminalOrbitDeferralToNoFutureSafeUT(
                ref decision, safeAltitude - 40000.0);

            Assert.Equal(TerminalOrbitSpawnSafetyAction.CannotSpawnSafely, decision.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonNoFutureSafeUT, decision.ReasonCode);
            Assert.Contains("no future safe UT was found", decision.Reason);
            // The measured geometry is preserved for the log line / persisted reason.
            Assert.Equal(safeAltitude, decision.SafeAltitude);
        }

        [Fact]
        public void MarkDeferred_ThenMarkCannotSpawnSafely_ClearsTheNextAttemptUT()
        {
            var rec = new Recording { RecordingId = "rec-defer", VesselName = "Probe" };
            double safeAltitude = KerbinSafeAltitude;

            var decision = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                bodyHasAtmosphere: true,
                bodyAtmosphereDepth: KerbinAtmosphereDepth,
                currentAltitude: safeAltitude - 40000.0,
                periapsisAltitude: safeAltitude - 50000.0,
                apoapsisAltitude: safeAltitude + 60000.0);
            TerminalOrbitSpawnSafety.MarkDeferred(rec, decision, 1000.0, 1600.0, 0.5);

            Assert.Equal(TerminalOrbitDeferredSpawnState.Hold,
                TerminalOrbitSpawnSafety.GetDeferredSpawnState(rec, 1500.0, out _));
            Assert.Equal(TerminalOrbitDeferredSpawnState.Ready,
                TerminalOrbitSpawnSafety.GetDeferredSpawnState(rec, 1700.0, out _));

            VesselSpawner.DowngradeTerminalOrbitDeferralToNoFutureSafeUT(
                ref decision, safeAltitude - 40000.0);
            TerminalOrbitSpawnSafety.MarkCannotSpawnSafely(rec, decision, 1000.0, 0.5);

            // A hard abandon must not leave a retry UT behind that could release the hold.
            Assert.False(rec.TerminalSpawnSafetyDeferred);
            Assert.True(double.IsNaN(rec.TerminalSpawnNextAttemptUT));
            Assert.Equal(TerminalOrbitDeferredSpawnState.Hold,
                TerminalOrbitSpawnSafety.GetDeferredSpawnState(rec, 1700.0, out string reason));
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonNoFutureSafeUT, reason);
        }

        #endregion
    }
}
