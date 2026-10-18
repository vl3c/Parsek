using System;
using UnityEngine;
using Xunit;
using TwoBodyOrbit = Parsek.BallisticExtrapolator.TwoBodyOrbit;

namespace Parsek.Tests
{
    /// <summary>
    /// TERMINAL-SPAWN-TAIL-ORBIT-SURFACE-VELOCITY (LAB-7 runs 2026-10-17_2308 / _2318_a2).
    /// A recorder sample taken below the body's inverse-rotation altitude carries a
    /// SURFACE-RELATIVE velocity (the Unity world frame co-rotates with the body there).
    /// The spawn tail seed used to feed it to the orbit as if it were inertial, losing
    /// Kerbin's ~202 m/s rotation speed at 93 km: a 92.5 x 107 km orbit reseeded as
    /// sma=596265.8 ecc=0.1622 periAlt=-100447 and the spawn was refused.
    ///
    /// These cells pin the pure conversion (<see cref="OrbitReseed"/>'s frame helpers)
    /// against the LAB-7 numbers, then derive elements through Parsek's own pure two-body
    /// solver and the pure spawn-safety decision. The Unity-bound wiring
    /// (<c>body.GetWorldSurfacePosition</c> + <c>Orbit.UpdateFromStateVectors</c>) is the
    /// in-game cell <c>RuntimeTests.TerminalOrbitFromTail_SurfaceRelativeLowOrbitTail_DerivesInertialOrbit</c>.
    /// </summary>
    public class TerminalSpawnTailOrbitRotatingFrameTests
    {
        // Stock Kerbin.
        private const double KerbinRadius = 600000.0;
        private const double KerbinMu = 3.5316e12;
        private const double KerbinRotationPeriod = 21549.425;
        private const double KerbinInverseRotThreshold = 100000.0;
        private const double KerbinAtmosphereDepth = 70000.0;

        // LAB-7 tail sample (KSP.log: FromLatLonAltAndRecordedVelocity ... alt=92950.7
        // |bodyRelPos|=692950.7 |vel|=2066.42) and the stock orbit of the live vessel at the
        // same moment (FinalizeIndividualRecording orbit[sma=699745.8 ecc=0.0104]).
        private const double Lab7TailAlt = 92950.7;
        private const double Lab7RecordedSpeed = 2066.42;
        private const double Lab7StockSma = 699745.8;

        // ---------- rotating-frame classification ----------

        [Theory]
        [InlineData(true, 100000.0, 92950.7, true)]     // LAB-7: below the threshold
        [InlineData(true, 100000.0, 99999.9, true)]
        [InlineData(true, 100000.0, 100000.0, false)]   // stock rule is strict <
        [InlineData(true, 100000.0, 203587.0, false)]   // high orbit: inertial sample
        [InlineData(true, 100000.0, 150000.0, false)]   // suborbital apex above threshold
        [InlineData(false, 100000.0, 92950.7, false)]   // non-rotating body
        [InlineData(true, 0.0, 92950.7, false)]         // unusable threshold
        [InlineData(true, double.NaN, 92950.7, false)]
        [InlineData(true, 100000.0, double.NaN, false)]
        public void IsRecordedVelocityInRotatingFrame_FollowsStockReferenceFrameRule(
            bool bodyRotates, double threshold, double altitude, bool expected)
        {
            Assert.Equal(
                expected,
                OrbitReseed.IsRecordedVelocityInRotatingFrame(bodyRotates, threshold, altitude));
        }

        // ---------- frame velocity (stock getRFrmVel) ----------

        [Fact]
        public void RotatingFrameVelocity_EquatorialPoint_IsOmegaRAlongTheProgradeTangent()
        {
            double r = KerbinRadius + Lab7TailAlt;
            Vector3d pos = new Vector3d(r, 0.0, 0.0);

            Vector3d frameVel = OrbitReseed.ComputeRotatingFrameVelocityYup(pos, KerbinRotationPeriod);

            double omegaR = 2.0 * Math.PI / KerbinRotationPeriod * r;
            Assert.Equal(202.04, omegaR, 2);
            AssertVector(new Vector3d(0.0, 0.0, omegaR), frameVel, 6);
            // Prograde tangent of the in-game tail tests: Cross(radial, Y-up).
            Vector3d prograde = Vector3d.Cross(pos.normalized, new Vector3d(0.0, 1.0, 0.0));
            Assert.Equal(1.0, Vector3d.Dot(frameVel.normalized, prograde), 9);
        }

        [Fact]
        public void RotatingFrameVelocity_PolarPointAndBadPeriod_AreZero()
        {
            AssertVector(
                Vector3d.zero,
                OrbitReseed.ComputeRotatingFrameVelocityYup(new Vector3d(0.0, 700000.0, 0.0), KerbinRotationPeriod),
                9);
            AssertVector(
                Vector3d.zero,
                OrbitReseed.ComputeRotatingFrameVelocityYup(new Vector3d(700000.0, 0.0, 0.0), 0.0),
                9);
            AssertVector(
                Vector3d.zero,
                OrbitReseed.ComputeRotatingFrameVelocityYup(new Vector3d(700000.0, 0.0, 0.0), double.NaN),
                9);
        }

        [Fact]
        public void InertialRecordedVelocity_InertialSample_PassesThroughUnchanged()
        {
            Vector3d recorded = new Vector3d(12.0, -34.0, 2200.0);
            Vector3d result = OrbitReseed.ComputeInertialRecordedVelocityYup(
                recorded, new Vector3d(803587.0, 0.0, 0.0), false, KerbinRotationPeriod);
            AssertVector(recorded, result, 12);
        }

        // ---------- LAB-7 reproduction ----------

        [Fact]
        public void Lab7Tail_PassThroughReproducesTheRefusedOrbit()
        {
            // The pre-fix spawn path: the surface-relative speed read as inertial.
            TwoBodyOrbit orbit = SolveLab7(sampleInRotatingFrame: false);

            Assert.InRange(orbit.SemiMajorAxis, 596260.0, 596272.0);   // log: sma=596265.8
            Assert.InRange(orbit.Eccentricity, 0.1617, 0.1627);        // log: ecc=0.1622
            double periAlt = orbit.PeriapsisRadius - KerbinRadius;
            double apoAlt = orbit.ApoapsisRadius - KerbinRadius;
            Assert.InRange(periAlt, -100500.0, -100400.0);   // log: periAlt=-100447.2
            Assert.InRange(apoAlt, 92900.0, 93000.0);        // log: apoAlt=92978.8

            var decision = EvaluateSpawn(periAlt, apoAlt);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.CannotSpawnSafely, decision.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonPeriapsisBelowSafeAltitude, decision.ReasonCode);
        }

        [Fact]
        public void Lab7Tail_RotatingFrameConversionRecoversTheStockOrbitAndSpawns()
        {
            TwoBodyOrbit orbit = SolveLab7(sampleInRotatingFrame: true);

            // sma depends only on |v| and r (vis-viva), so it must land on the live vessel's
            // stock orbit; ecc is ~0.01 (the synthetic sample has no radial component, the
            // real one sat just past periapsis: stock ecc 0.0104).
            Assert.InRange(orbit.SemiMajorAxis, Lab7StockSma - 100.0, Lab7StockSma + 100.0);
            Assert.InRange(orbit.Eccentricity, 0.005, 0.015);
            double periAlt = orbit.PeriapsisRadius - KerbinRadius;
            double apoAlt = orbit.ApoapsisRadius - KerbinRadius;
            Assert.True(periAlt > KerbinAtmosphereDepth + TerminalOrbitSpawnSafety.DefaultSafetyMarginMeters,
                $"periAlt={periAlt:F1} must clear the safe altitude");
            Assert.InRange(apoAlt, 105000.0, 108000.0);

            var decision = EvaluateSpawn(periAlt, apoAlt);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.SpawnNow, decision.Action);
        }

        [Fact]
        public void HighAltitudeInertialSample_FrameAwareInputsEqualTheLegacyInputs()
        {
            // A 203.6 km tail (above the threshold) keeps the existing contract byte-for-byte.
            var bodyPosition = new Vector3d(2000000.0, -500000.0, 750000.0);
            var bodyRelPos = new Vector3d(803587.0, 1000.0, -2000.0);
            Vector3d worldPos = bodyPosition + bodyRelPos;
            var recordedVel = new Vector3d(5.0, 30.0, 2216.0);
            bool rotating = OrbitReseed.IsRecordedVelocityInRotatingFrame(
                true, KerbinInverseRotThreshold, 203587.0);
            Assert.False(rotating);

            var legacy = OrbitReseed.ComputeRecordedVelocityInputs(worldPos, bodyPosition, recordedVel);
            var frameAware = OrbitReseed.ComputeRecordedVelocityInputs(
                worldPos, bodyPosition, recordedVel, rotating, KerbinRotationPeriod);

            AssertVector(legacy.PositionForUpdate, frameAware.PositionForUpdate, 12);
            AssertVector(legacy.VelocityForUpdate, frameAware.VelocityForUpdate, 12);
        }

        [Fact]
        public void SuborbitalTailAboveThreshold_ElementsUnchanged()
        {
            // A suborbital coast sampled at 150 km falling back: inertial sample, so the
            // derived (sub-surface) ellipse is the one the recording describes.
            double r = KerbinRadius + 150000.0;
            var bodyRelPos = new Vector3d(r, 0.0, 0.0);
            var recordedVel = new Vector3d(-400.0, 0.0, 1200.0);
            bool rotating = OrbitReseed.IsRecordedVelocityInRotatingFrame(
                true, KerbinInverseRotThreshold, 150000.0);
            Assert.False(rotating);

            var legacy = OrbitReseed.ComputeRecordedVelocityInputs(bodyRelPos, Vector3d.zero, recordedVel);
            var frameAware = OrbitReseed.ComputeRecordedVelocityInputs(
                bodyRelPos, Vector3d.zero, recordedVel, rotating, KerbinRotationPeriod);
            Assert.True(TwoBodyOrbit.TryCreate(legacy.PositionForUpdate, legacy.VelocityForUpdate, KerbinMu, 0.0, out TwoBodyOrbit a));
            Assert.True(TwoBodyOrbit.TryCreate(frameAware.PositionForUpdate, frameAware.VelocityForUpdate, KerbinMu, 0.0, out TwoBodyOrbit b));
            Assert.Equal(a.SemiMajorAxis, b.SemiMajorAxis, 9);
            Assert.Equal(a.Eccentricity, b.Eccentricity, 12);
            Assert.True(a.PeriapsisRadius < KerbinRadius, "the suborbital ellipse stays suborbital");
        }

        // ---------- helpers ----------

        private static TwoBodyOrbit SolveLab7(bool sampleInRotatingFrame)
        {
            double r = KerbinRadius + Lab7TailAlt;
            var bodyPosition = new Vector3d(1221799.0 - r, 0.0, 0.0);   // floating-origin offset
            var bodyRelPos = new Vector3d(r, 0.0, 0.0);
            Vector3d worldPos = bodyPosition + bodyRelPos;
            // Prograde, horizontal: the LAB-7 orbit is equatorial (inc 0.09 deg) and the
            // sample sat at the low apsis.
            Vector3d prograde = Vector3d.Cross(bodyRelPos.normalized, new Vector3d(0.0, 1.0, 0.0));
            Vector3d recordedSurfaceRelative = prograde * Lab7RecordedSpeed;

            if (sampleInRotatingFrame)
            {
                Assert.True(OrbitReseed.IsRecordedVelocityInRotatingFrame(
                    true, KerbinInverseRotThreshold, Lab7TailAlt));
            }

            var inputs = OrbitReseed.ComputeRecordedVelocityInputs(
                worldPos, bodyPosition, recordedSurfaceRelative, sampleInRotatingFrame, KerbinRotationPeriod);
            Assert.Equal(r, inputs.PositionForUpdate.magnitude, 6);
            Assert.True(
                TwoBodyOrbit.TryCreate(inputs.PositionForUpdate, inputs.VelocityForUpdate, KerbinMu, 3606.65, out TwoBodyOrbit orbit),
                "two-body solve failed");
            return orbit;
        }

        private static TerminalOrbitSpawnSafetyDecision EvaluateSpawn(double periAlt, double apoAlt)
        {
            return TerminalOrbitSpawnSafety.Evaluate(
                currentAltitude: Lab7TailAlt,
                atmosphereDepth: KerbinAtmosphereDepth,
                safetyMargin: TerminalOrbitSpawnSafety.DefaultSafetyMarginMeters,
                periapsisAltitude: periAlt,
                apoapsisAltitude: apoAlt);
        }

        private static void AssertVector(Vector3d expected, Vector3d actual, int precision)
        {
            Assert.Equal(expected.x, actual.x, precision);
            Assert.Equal(expected.y, actual.y, precision);
            Assert.Equal(expected.z, actual.z, precision);
        }
    }
}
