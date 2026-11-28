using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Parsek;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// P5/P6 playback fidelity: the pure decision core behind plume magnitude (S1), deployable
    /// interpolation (S2) and synthesized motion (S3).
    ///
    /// Everything asserted here is Unity-ECall-free by construction. <c>Quaternion</c> and
    /// <c>Vector3</c> are safe as STRUCTS (field access, the managed Dot/Cross/normalized helpers)
    /// but their static rotation math — <c>Quaternion.AngleAxis</c>, <c>Quaternion.Inverse</c>,
    /// <c>operator*</c>, <c>ToAngleAxis</c> — is native and throws outside a Unity runtime, which
    /// is why <see cref="GhostPlaybackLogic.ComputeLocalAngularVelocityDegPerSec"/> multiplies
    /// quaternions by hand and why the cells below construct rotations from raw components.
    /// The transform-writing appliers are in-game territory (the PlaybackFidelity category).
    /// </summary>
    public class PlaybackFidelityTests
    {
        /// <summary>
        /// xUnit's Assert.Equal(float, float, int) is ambiguous against the (float, float, float)
        /// tolerance overload in this version. One double-typed helper, so every approximate cell
        /// below reads the same way.
        /// </summary>
        private static void AssertClose(double expected, double actual, int precision)
            => Assert.Equal(expected, actual, precision);

        private static Quaternion RotationAboutY(double degrees)
        {
            double half = degrees * Math.PI / 360.0;
            return new Quaternion(0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half));
        }

        private static Quaternion RotationAboutX(double degrees)
        {
            double half = degrees * Math.PI / 360.0;
            return new Quaternion((float)Math.Sin(half), 0f, 0f, (float)Math.Cos(half));
        }

        // ---------------------------------------------------------------- S1: plume magnitude

        [Fact]
        public void FxMagnitudeRatio_ZeroPowerIsZero()
        {
            Assert.Equal(0f, GhostPlaybackLogic.ComputeFxMagnitudeRatio(0f, 100f, 0f));
        }

        [Fact]
        public void FxMagnitudeRatio_FullPowerIsExactlyOne()
        {
            // Not "close to one": at full throttle the write must be the captured baseline itself,
            // so a ghost at 100% looks bit-identical to the pre-S1 build.
            Assert.Equal(1f, GhostPlaybackLogic.ComputeFxMagnitudeRatio(100f, 100f, 1f));
            Assert.Equal(1f, GhostPlaybackLogic.ComputeFxMagnitudeRatio(100f, 100f, 1.5f));
        }

        [Fact]
        public void FxMagnitudeRatio_MidThrottleIsTheMagnitudeRatio()
        {
            AssertClose(0.6f, GhostPlaybackLogic.ComputeFxMagnitudeRatio(60f, 100f, 0.6f), 4);
        }

        [Fact]
        public void FxMagnitudeRatio_HoldsTheVisibilityFloorAtVeryLowThrottle()
        {
            // A 1%-throttle plume must still read as "this engine is running": the boolean it
            // replaces was at least legible.
            float ratio = GhostPlaybackLogic.ComputeFxMagnitudeRatio(0.5f, 100f, 0.01f);
            AssertClose(GhostPlaybackLogic.FxMagnitudeMinVisibleRatio, ratio, 4);
        }

        [Theory]
        [InlineData(50f, 0f)]              // no usable full-power reference
        [InlineData(50f, float.NaN)]
        [InlineData(float.NaN, 100f)]
        [InlineData(float.PositiveInfinity, 100f)]
        [InlineData(-1f, 100f)]
        public void FxMagnitudeRatio_DegradesToOneRatherThanToZero(float atPower, float atFull)
        {
            // The degradation direction is the whole safety story: an unknowable ratio writes the
            // baseline back unchanged (today's behaviour), never a zero-magnitude plume.
            Assert.Equal(1f, GhostPlaybackLogic.ComputeFxMagnitudeRatio(atPower, atFull, 0.4f));
        }

        [Fact]
        public void FxMagnitudeRatio_NeverExceedsOneEvenOnARisingCurve()
        {
            Assert.Equal(1f, GhostPlaybackLogic.ComputeFxMagnitudeRatio(140f, 100f, 0.7f));
        }

        [Fact]
        public void EngineMagnitudeHelpers_FallBackLinearlyWithoutACurve()
        {
            Assert.Equal(0f, GhostPlaybackLogic.ComputeEngineEmissionRate(null, 0f));
            AssertClose(30f, GhostPlaybackLogic.ComputeEngineEmissionRate(null, 0.3f), 4);
            AssertClose(3f, GhostPlaybackLogic.ComputeEngineSpeed(null, 0.3f), 4);
        }

        [Fact]
        public void EngineRatio_TracksThrottleMonotonicallyThroughTheHelpers()
        {
            float low = GhostPlaybackLogic.ComputeFxMagnitudeRatio(
                GhostPlaybackLogic.ComputeEngineEmissionRate(null, 0.3f),
                GhostPlaybackLogic.ComputeEngineEmissionRate(null, 1f), 0.3f);
            float high = GhostPlaybackLogic.ComputeFxMagnitudeRatio(
                GhostPlaybackLogic.ComputeEngineEmissionRate(null, 0.8f),
                GhostPlaybackLogic.ComputeEngineEmissionRate(null, 1f), 0.8f);

            Assert.True(low < high, $"expected 0.3 throttle ratio {low} < 0.8 throttle ratio {high}");
            AssertClose(0.3f, low, 4);
            AssertClose(0.8f, high, 4);
        }

        [Fact]
        public void RcsRatio_KeepsTheShowcaseVisibilityFloorAliveAsARatio()
        {
            // The showcase floors are the reason SetRcsEmission routes through the scaled helpers
            // rather than reading the curve twice: at 1% power the floor lifts the NUMERATOR, and
            // the ratio inherits that lift. A raw curve ratio here would be 0.01.
            const float showcaseScale = 120f;
            float numerator = GhostPlaybackLogic.ComputeScaledRcsEmissionRate(null, 0.01f, showcaseScale);
            float denominator = GhostPlaybackLogic.ComputeScaledRcsEmissionRate(null, 1f, showcaseScale);
            float ratio = GhostPlaybackLogic.ComputeFxMagnitudeRatio(numerator, denominator, 0.01f);

            Assert.True(numerator >= 60f, $"floor should lift the numerator, got {numerator}");
            Assert.True(ratio > 0.004f, $"floored ratio should beat the raw 0.01 curve ratio, got {ratio}");
            Assert.True(ratio >= GhostPlaybackLogic.FxMagnitudeMinVisibleRatio);
        }

        [Fact]
        public void RcsRatio_NonShowcaseIsTheStraightCurveRatio()
        {
            float numerator = GhostPlaybackLogic.ComputeScaledRcsEmissionRate(null, 0.5f, 1f);
            float denominator = GhostPlaybackLogic.ComputeScaledRcsEmissionRate(null, 1f, 1f);
            AssertClose(0.5f, GhostPlaybackLogic.ComputeFxMagnitudeRatio(numerator, denominator, 0.5f), 4);
        }

        // ------------------------------------------------------- S2: deployable interpolation

        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void ClipSeconds_UnreadableLengthStillAnimates(float raw)
        {
            // The failure being replaced is the SNAP, not a wrong duration: an unreadable clip
            // must still produce a transition, at the default pace.
            Assert.Equal(
                GhostPlaybackLogic.DefaultDeployableClipSeconds,
                GhostPlaybackLogic.ClampDeployableClipSeconds(raw));
        }

        [Fact]
        public void ClipSeconds_ClampsAbsurdEnds()
        {
            Assert.Equal(GhostPlaybackLogic.MinDeployableClipSeconds,
                GhostPlaybackLogic.ClampDeployableClipSeconds(0.01f));
            Assert.Equal(GhostPlaybackLogic.MaxDeployableClipSeconds,
                GhostPlaybackLogic.ClampDeployableClipSeconds(600f));
            AssertClose(1.4f, GhostPlaybackLogic.ClampDeployableClipSeconds(1.4f), 4);
        }

        [Fact]
        public void TransitionFraction_StartsAtTheStowedEndAndReachesDeployed()
        {
            float atStart = GhostPlaybackLogic.ComputeDeployableTransitionFraction(
                100.0, 100.0, 0f, 1f, 4f, out bool completeAtStart);
            float halfway = GhostPlaybackLogic.ComputeDeployableTransitionFraction(
                102.0, 100.0, 0f, 1f, 4f, out bool completeHalf);
            float atEnd = GhostPlaybackLogic.ComputeDeployableTransitionFraction(
                104.0, 100.0, 0f, 1f, 4f, out bool completeAtEnd);

            AssertClose(0f, atStart, 4);
            Assert.False(completeAtStart);
            AssertClose(0.5f, halfway, 4);
            Assert.False(completeHalf);
            AssertClose(1f, atEnd, 4);
            Assert.True(completeAtEnd);
        }

        [Fact]
        public void TransitionFraction_StrictlyBetweenTheEndpointsMidClip()
        {
            float mid = GhostPlaybackLogic.ComputeDeployableTransitionFraction(
                101.0, 100.0, 0f, 1f, 4f, out _);
            Assert.True(mid > 0f && mid < 1f, $"expected a strictly interior pose, got {mid}");
        }

        [Fact]
        public void TransitionFraction_CatchUpFromFarInThePastLandsAtTheTarget()
        {
            // A ghost spawned mid-recording replays the prefix at once; a bay whose open event is
            // an hour old must appear OPEN, not caught mid-clip.
            float f = GhostPlaybackLogic.ComputeDeployableTransitionFraction(
                4000.0, 100.0, 0f, 1f, 4f, out bool complete);
            AssertClose(1f, f, 4);
            Assert.True(complete);
        }

        [Fact]
        public void TransitionFraction_ReversalTakesOnlyTheDistanceItHasToTravel()
        {
            // Retract fired at UT 200 while the panel was 30% out. Getting back to stowed is 0.3 of
            // a clip, not a whole one, because duration scales with |target - start|.
            const float clip = 10f;
            float quarterBack = GhostPlaybackLogic.ComputeDeployableTransitionFraction(
                200.0 + 1.5, 200.0, 0.3f, 0f, clip, out bool half);
            float done = GhostPlaybackLogic.ComputeDeployableTransitionFraction(
                200.0 + 3.0, 200.0, 0.3f, 0f, clip, out bool complete);

            AssertClose(0.15f, quarterBack, 4);
            Assert.False(half);
            AssertClose(0f, done, 4);
            Assert.True(complete);
        }

        [Fact]
        public void TransitionFraction_BackwardsUtHoldsTheStartPoseRatherThanInvertingTheClip()
        {
            float f = GhostPlaybackLogic.ComputeDeployableTransitionFraction(
                90.0, 100.0, 0.25f, 1f, 4f, out bool complete);
            AssertClose(0.25f, f, 4);
            Assert.False(complete);
        }

        [Fact]
        public void TransitionFraction_NoDistanceIsInstantlyComplete()
        {
            float f = GhostPlaybackLogic.ComputeDeployableTransitionFraction(
                100.0, 100.0, 1f, 1f, 4f, out bool complete);
            AssertClose(1f, f, 4);
            Assert.True(complete);
        }

        [Fact]
        public void TransitionFraction_NonFiniteUtSnapsToTheTargetInsteadOfFreezing()
        {
            float f = GhostPlaybackLogic.ComputeDeployableTransitionFraction(
                double.NaN, 100.0, 0f, 1f, 4f, out bool complete);
            AssertClose(1f, f, 4);
            Assert.True(complete);
        }

        // ------------------------------------------------------ S3: attitude derivative

        [Fact]
        public void AngularVelocity_IdenticalRotationsProduceNoRate()
        {
            Vector3 w = GhostPlaybackLogic.ComputeLocalAngularVelocityDegPerSec(
                Quaternion.identity, Quaternion.identity, 0.5);
            AssertClose(0f, w.magnitude, 5);
        }

        [Fact]
        public void AngularVelocity_TenDegreesAboutYOverHalfASecondIsTwentyDegPerSecOnY()
        {
            Vector3 w = GhostPlaybackLogic.ComputeLocalAngularVelocityDegPerSec(
                Quaternion.identity, RotationAboutY(10.0), 0.5);

            AssertClose(20f, w.y, 2);
            AssertClose(0f, w.x, 3);
            AssertClose(0f, w.z, 3);
        }

        [Fact]
        public void AngularVelocity_SignFollowsTheDirectionOfRotation()
        {
            Vector3 positive = GhostPlaybackLogic.ComputeLocalAngularVelocityDegPerSec(
                Quaternion.identity, RotationAboutX(6.0), 1.0);
            Vector3 negative = GhostPlaybackLogic.ComputeLocalAngularVelocityDegPerSec(
                Quaternion.identity, RotationAboutX(-6.0), 1.0);

            AssertClose(6f, positive.x, 2);
            AssertClose(-6f, negative.x, 2);
        }

        [Fact]
        public void AngularVelocity_TakesTheShortestArcAcrossTheDoubleCover()
        {
            // q and -q are the same rotation; a naive difference reads the -q form as a 350-degree
            // turn. 10 degrees over 1 s must stay 10 deg/s either way.
            Quaternion target = RotationAboutY(10.0);
            Quaternion negated = new Quaternion(-target.x, -target.y, -target.z, -target.w);

            Vector3 w = GhostPlaybackLogic.ComputeLocalAngularVelocityDegPerSec(
                Quaternion.identity, negated, 1.0);
            AssertClose(10f, w.magnitude, 2);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        public void AngularVelocity_NonPositiveDeltaAnswersZeroRatherThanInfinity(double dt)
        {
            Vector3 w = GhostPlaybackLogic.ComputeLocalAngularVelocityDegPerSec(
                Quaternion.identity, RotationAboutY(10.0), dt);
            AssertClose(0f, w.magnitude, 6);
        }

        [Fact]
        public void EmaVector_BlendsTowardTheNewSampleWithoutOvershooting()
        {
            Vector3 blended = GhostPlaybackLogic.ComputeEmaVector(
                new Vector3(0f, 0f, 0f), new Vector3(10f, 0f, 0f), 0.25f);
            AssertClose(2.5f, blended.x, 4);

            AssertClose(10f, GhostPlaybackLogic.ComputeEmaVector(
                Vector3.zero, new Vector3(10f, 0f, 0f), 1f).x, 4);
            AssertClose(0f, GhostPlaybackLogic.ComputeEmaVector(
                Vector3.zero, new Vector3(10f, 0f, 0f), 0f).x, 4);
        }

        // ------------------------------------------------- S3: deflection mapping and slew

        [Fact]
        public void Deflection_OpposesTheBodyRateThatProducedIt()
        {
            float deflection = GhostPlaybackLogic.ComputeSynthDeflectionDegrees(4f, 2f, 30f);
            AssertClose(-8f, deflection, 4);
        }

        [Fact]
        public void Deflection_ClampsToTheModulesOwnAuthority()
        {
            AssertClose(-4f, GhostPlaybackLogic.ComputeSynthDeflectionDegrees(100f, 1f, 4f), 4);
            AssertClose(4f, GhostPlaybackLogic.ComputeSynthDeflectionDegrees(-100f, 1f, 4f), 4);
        }

        [Fact]
        public void Deflection_DeadbandsSamplingNoiseToExactlyNeutral()
        {
            // Below the deadband the answer must be EXACTLY 0, not a small number: anything else
            // shimmers a control surface for the whole coast.
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSynthDeflectionDegrees(0.2f, 2f, 30f));
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSynthDeflectionDegrees(-0.2f, 2f, 30f));
            Assert.NotEqual(0f, GhostPlaybackLogic.ComputeSynthDeflectionDegrees(2f, 2f, 30f));
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void Deflection_NonFiniteRateIsNeutral(float rate)
        {
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSynthDeflectionDegrees(rate, 2f, 30f));
        }

        [Fact]
        public void Deflection_UnusableRangeFallsBackToTheDefaultAuthority()
        {
            float clamped = GhostPlaybackLogic.ComputeSynthDeflectionDegrees(1000f, 1f, 0f);
            AssertClose(-GhostPlaybackLogic.DefaultControlSurfaceRangeDegrees, clamped, 4);
        }

        [Fact]
        public void Slew_MovesAtMostTheRateTimesTheStep()
        {
            AssertClose(9f, GhostPlaybackLogic.SlewTowardDegrees(0f, 45f, 90f, 0.1), 4);
            AssertClose(-9f, GhostPlaybackLogic.SlewTowardDegrees(0f, -45f, 90f, 0.1), 4);
        }

        [Fact]
        public void Slew_SnapsWhenTheTargetIsWithinOneStep()
        {
            AssertClose(2f, GhostPlaybackLogic.SlewTowardDegrees(0f, 2f, 90f, 0.1), 4);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-0.5)]
        [InlineData(double.NaN)]
        public void Slew_NonPositiveStepHolds(double dt)
        {
            AssertClose(7f, GhostPlaybackLogic.SlewTowardDegrees(7f, 45f, 90f, dt), 4);
        }

        [Fact]
        public void Slew_NonFiniteTargetHolds()
        {
            AssertClose(7f, GhostPlaybackLogic.SlewTowardDegrees(7f, float.NaN, 90f, 0.5), 4);
        }

        // ------------------------------------------------------------ S3: wheel steering

        [Fact]
        public void HeadingRate_TurningLeftAboutUpIsPositive()
        {
            Vector3 up = new Vector3(0f, 1f, 0f);
            Vector3 before = new Vector3(1f, 0f, 0f);
            Vector3 after = new Vector3(0f, 0f, -1f);   // +90 degrees about +Y (right-handed)

            float rate = GhostPlaybackLogic.ComputeHeadingRateDegPerSec(before, after, up, 2.0);
            AssertClose(45f, rate, 2);
        }

        [Fact]
        public void HeadingRate_ReversesSignWithTheTurn()
        {
            Vector3 up = new Vector3(0f, 1f, 0f);
            Vector3 before = new Vector3(1f, 0f, 0f);
            Vector3 after = new Vector3(0f, 0f, 1f);

            AssertClose(-45f, GhostPlaybackLogic.ComputeHeadingRateDegPerSec(before, after, up, 2.0), 2);
        }

        [Fact]
        public void HeadingRate_StraightLineTravelIsZero()
        {
            Vector3 up = new Vector3(0f, 1f, 0f);
            Vector3 heading = new Vector3(0.6f, 0f, 0.8f);
            AssertClose(0f, GhostPlaybackLogic.ComputeHeadingRateDegPerSec(heading, heading, up, 1.0), 4);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(double.NaN)]
        public void HeadingRate_NonPositiveStepIsZero(double dt)
        {
            Vector3 up = new Vector3(0f, 1f, 0f);
            AssertClose(0f, GhostPlaybackLogic.ComputeHeadingRateDegPerSec(
                new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, -1f), up, dt), 4);
        }

        [Fact]
        public void HeadingRate_DegenerateInputsAnswerZeroRatherThanNaN()
        {
            Vector3 up = new Vector3(0f, 1f, 0f);
            AssertClose(0f, GhostPlaybackLogic.ComputeHeadingRateDegPerSec(
                Vector3.zero, new Vector3(1f, 0f, 0f), up, 1.0), 4);
            AssertClose(0f, GhostPlaybackLogic.ComputeHeadingRateDegPerSec(
                new Vector3(1f, 0f, 0f), new Vector3(1f, 0f, 0f), Vector3.zero, 1.0), 4);
            // Heading straight up the axis: the projection collapses, so there is no heading.
            AssertClose(0f, GhostPlaybackLogic.ComputeHeadingRateDegPerSec(
                up, new Vector3(1f, 0f, 0f), up, 1.0), 4);
        }

        [Fact]
        public void SteeringAngle_PointsIntoTheTurnAndClampsAtTheCaliperLimit()
        {
            // The steering drive negates on the way in and ComputeSynthDeflectionDegrees inverts
            // again, so the two cancel: the caliper angle carries the SAME SIGN as the ground-track
            // heading rate - the wheels point INTO the turn. The inputs below are HEADING RATES
            // driven through the production caller ComputeTargetWheelSteeringDegrees; the cell used
            // to hand ComputeSynthDeflectionDegrees the already-negated value, so the caller-side
            // minus - the half that decides which way the ghost wheels point - was never run, and
            // its sign was the opposite of a real heading rate.
            float gentle = GhostPlaybackLogic.ComputeTargetWheelSteeringDegrees(10f);
            float hard = GhostPlaybackLogic.ComputeTargetWheelSteeringDegrees(400f);

            AssertClose(15f, gentle, 3);
            AssertClose(GhostPlaybackLogic.MaxWheelSteeringDegrees, hard, 3);

            // The mirror direction: the opposite heading rate steers the opposite way (and clamps
            // at the same limit). Without it a body that dropped BOTH negations would still pass.
            AssertClose(-15f, GhostPlaybackLogic.ComputeTargetWheelSteeringDegrees(-10f), 3);
            AssertClose(-GhostPlaybackLogic.MaxWheelSteeringDegrees,
                GhostPlaybackLogic.ComputeTargetWheelSteeringDegrees(-400f), 3);
        }

        // -------------------------------------------------------------- S3: sun tracking

        [Fact]
        public void AimAngle_ResolvesTheInPlaneAngleBetweenReferenceAndTarget()
        {
            Vector3 axis = new Vector3(0f, 1f, 0f);
            Vector3 reference = new Vector3(1f, 0f, 0f);
            Vector3 target = new Vector3(0f, 0f, -1f);   // +90 about +Y

            Assert.True(GhostPlaybackLogic.TryComputeAimAngleDegrees(
                target, axis, reference, out float angle));
            AssertClose(90f, angle, 2);
        }

        [Fact]
        public void AimAngle_IgnoresTheComponentAlongTheAxis()
        {
            Vector3 axis = new Vector3(0f, 1f, 0f);
            Vector3 reference = new Vector3(1f, 0f, 0f);
            // Same in-plane direction as the reference, plus a large along-axis component.
            Vector3 target = new Vector3(1f, 50f, 0f);

            Assert.True(GhostPlaybackLogic.TryComputeAimAngleDegrees(
                target, axis, reference, out float angle));
            AssertClose(0f, angle, 2);
        }

        [Fact]
        public void AimAngle_TargetAlongTheAxisDeclinesSoTheCallerHolds()
        {
            Vector3 axis = new Vector3(0f, 1f, 0f);
            Assert.False(GhostPlaybackLogic.TryComputeAimAngleDegrees(
                new Vector3(0f, 5f, 0f), axis, new Vector3(1f, 0f, 0f), out _));
        }

        [Fact]
        public void AimAngle_DegenerateInputsDecline()
        {
            Vector3 axis = new Vector3(0f, 1f, 0f);
            Assert.False(GhostPlaybackLogic.TryComputeAimAngleDegrees(
                Vector3.zero, axis, new Vector3(1f, 0f, 0f), out _));
            Assert.False(GhostPlaybackLogic.TryComputeAimAngleDegrees(
                new Vector3(1f, 0f, 0f), Vector3.zero, new Vector3(1f, 0f, 0f), out _));
            Assert.False(GhostPlaybackLogic.TryComputeAimAngleDegrees(
                new Vector3(1f, 0f, 0f), axis, Vector3.zero, out _));
        }

        // --------------------------------------------------------------- S3: launch dust

        [Fact]
        public void DustSurface_StockTagsDecideThePadAndTheWheelPiston()
        {
            // Decompiled ModuleSurfaceFX.Update: LaunchpadFX wins over everything, a wheel piston
            // is nothing, any other collider is terrain.
            Assert.Equal(LaunchDustSurface.Launchpad, GhostPlaybackLogic.ClassifyLaunchDustHit(true, false));
            Assert.Equal(LaunchDustSurface.Launchpad, GhostPlaybackLogic.ClassifyLaunchDustHit(true, true));
            Assert.Equal(LaunchDustSurface.None, GhostPlaybackLogic.ClassifyLaunchDustHit(false, true));
            Assert.Equal(LaunchDustSurface.Terrain, GhostPlaybackLogic.ClassifyLaunchDustHit(false, false));
            Assert.Equal("LaunchpadFX", GhostPlaybackLogic.LaunchpadFxTag);
            Assert.Equal("Wheel_Piston_Collider", GhostPlaybackLogic.WheelPistonColliderTag);
        }

        [Fact]
        public void DustSurface_OnlyTerrainAndWaterRaiseDust()
        {
            Assert.True(GhostPlaybackLogic.LaunchDustSurfaceRaisesDust(LaunchDustSurface.Terrain));
            Assert.True(GhostPlaybackLogic.LaunchDustSurfaceRaisesDust(LaunchDustSurface.Water));
            Assert.False(GhostPlaybackLogic.LaunchDustSurfaceRaisesDust(LaunchDustSurface.Launchpad));
            Assert.False(GhostPlaybackLogic.LaunchDustSurfaceRaisesDust(LaunchDustSurface.None));
        }

        [Fact]
        public void DustSurfaceFx_DefaultsAndMaskAreStocks()
        {
            // Decompiled ModuleSurfaceFX constructor and Update's raycast.
            Assert.Equal(1f, GhostPlaybackLogic.SurfaceFxDefaultFxMax);
            Assert.Equal(50f, GhostPlaybackLogic.SurfaceFxDefaultMaxDistance);
            Assert.Equal(2f, GhostPlaybackLogic.SurfaceFxDefaultFalloff);
            Assert.Equal(0x40008000, GhostPlaybackLogic.LaunchDustSurfaceLayerMask);
            var src = new GhostSurfaceFxSource();
            Assert.Equal(GhostPlaybackLogic.SurfaceFxDefaultFxMax, src.fxMax);
            Assert.Equal(GhostPlaybackLogic.SurfaceFxDefaultMaxDistance, src.maxDistance);
            Assert.Equal(GhostPlaybackLogic.SurfaceFxDefaultFalloff, src.falloff);
        }

        [Fact]
        public void DustThrustScale_IsThrottleTimesThrustShareTimesFxMax()
        {
            AssertClose(1f, GhostPlaybackLogic.ComputeSurfaceFxThrustScale(1f, false, 0.3f, 1f), 5);
            AssertClose(0.5f, GhostPlaybackLogic.ComputeSurfaceFxThrustScale(0.5f, false, 1f, 1f), 5);
            // The thrust share in the ghost's air counts only when the engine tracks one.
            AssertClose(0.39f, GhostPlaybackLogic.ComputeSurfaceFxThrustScale(0.5f, true, 0.78f, 1f), 5);
            AssertClose(0.8f, GhostPlaybackLogic.ComputeSurfaceFxThrustScale(1f, false, 1f, 0.8f), 5);
            // Out of range inputs clamp; nothing lit or a non-positive fxMax is nothing.
            AssertClose(1f, GhostPlaybackLogic.ComputeSurfaceFxThrustScale(3f, true, 2f, 1f), 5);
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSurfaceFxThrustScale(0f, false, 1f, 1f));
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSurfaceFxThrustScale(-1f, false, 1f, 1f));
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSurfaceFxThrustScale(float.NaN, false, 1f, 1f));
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSurfaceFxThrustScale(1f, false, 1f, 0f));
            AssertClose(1f, GhostPlaybackLogic.ComputeSurfaceFxThrustScale(1f, true, float.NaN, 1f), 5);
        }

        [Fact]
        public void DustRay_AHitAboveTheDatumIsClassifiedByItsTagsAtItsOwnDistance()
        {
            Assert.Equal(LaunchDustSurface.Terrain, GhostPlaybackLogic.ClassifySurfaceFxRay(
                true, 70.0, 12f, false, false, true, 85.0, 1f, 30f, out float d));
            Assert.Equal(12f, d);
            // From the pad stock's ray meets the PadFXReceiver box first: the pad, no dust.
            Assert.Equal(LaunchDustSurface.Launchpad, GhostPlaybackLogic.ClassifySurfaceFxRay(
                true, 70.0, 3f, true, false, true, 75.0, 1f, 30f, out d));
            Assert.Equal(3f, d);
            Assert.Equal(LaunchDustSurface.None, GhostPlaybackLogic.ClassifySurfaceFxRay(
                true, 70.0, 3f, false, true, true, 75.0, 1f, 30f, out _));
            // No ocean: no hit is nothing.
            Assert.Equal(LaunchDustSurface.None, GhostPlaybackLogic.ClassifySurfaceFxRay(
                false, double.NaN, float.NaN, false, false, false, 10.0, 1f, 30f, out d));
            Assert.True(float.IsNaN(d));
        }

        [Fact]
        public void DustRay_OverTheSea_TheWaterPointIsAltitudeOverCosAlongTheAxis()
        {
            // Engine 15 m over the sea, axis 60 deg off straight down: 30 m to the surface.
            Assert.Equal(LaunchDustSurface.Water, GhostPlaybackLogic.ClassifySurfaceFxRay(
                false, double.NaN, float.NaN, false, false, true, 15.0, 0.5f, 50f, out float d));
            AssertClose(30f, d, 4);
            // A hit below the datum (the sea bed) also falls to the water branch.
            Assert.Equal(LaunchDustSurface.Water, GhostPlaybackLogic.ClassifySurfaceFxRay(
                true, -40.0, 20f, false, false, true, 15.0, 1f, 50f, out d));
            AssertClose(15f, d, 4);
            // Pointing up, higher than the reach, or at / under the surface: nothing.
            Assert.Equal(LaunchDustSurface.None, GhostPlaybackLogic.ClassifySurfaceFxRay(
                false, double.NaN, float.NaN, false, false, true, 15.0, -0.5f, 50f, out _));
            Assert.Equal(LaunchDustSurface.None, GhostPlaybackLogic.ClassifySurfaceFxRay(
                false, double.NaN, float.NaN, false, false, true, 60.0, 1f, 50f, out _));
            Assert.Equal(LaunchDustSurface.None, GhostPlaybackLogic.ClassifySurfaceFxRay(
                true, 1.0, 1f, false, false, true, 0.0, 1f, 50f, out _));
        }

        [Fact]
        public void DustScaled_IsStocksFalloffAndNothingPastTheReach()
        {
            // LV-T45: maxDistance 30, falloff 1.7.
            AssertClose(1f, GhostPlaybackLogic.ComputeSurfaceFxScaled(1f, 0f, 30f, 1.7f), 5);
            AssertClose(Math.Pow(0.5, 1.7), GhostPlaybackLogic.ComputeSurfaceFxScaled(1f, 15f, 30f, 1.7f), 5);
            AssertClose(0.5 * Math.Pow(0.9, 2.0), GhostPlaybackLogic.ComputeSurfaceFxScaled(0.5f, 5f, 50f, 2f), 5);
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSurfaceFxScaled(1f, 30f, 30f, 1.7f));
            // Stock's water branch can pass a slant distance past the reach; no dust there.
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSurfaceFxScaled(1f, 45f, 30f, 2f));
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSurfaceFxScaled(0f, 1f, 30f, 2f));
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSurfaceFxScaled(1f, float.NaN, 30f, 2f));
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSurfaceFxScaled(1f, 1f, 0f, 2f));
            // Closer is stronger, monotonically.
            float last = 2f;
            for (int i = 0; i < 30; i++)
            {
                float v = GhostPlaybackLogic.ComputeSurfaceFxScaled(1f, i, 30f, 1.7f);
                Assert.True(v < last, $"d={i}: {v} !< {last}");
                last = v;
            }
        }

        [Fact]
        public void DustFlow_IsTheEngineToHitLineOnTheSurfaceTimesTheThrustShare()
        {
            // Straight down onto flat ground: no sideways push.
            Assert.Equal(Vector3.zero, GhostPlaybackLogic.ComputeSurfaceFxFlow(
                Vector3.zero, new Vector3(0f, 10f, 0f), Vector3.up, 1f));
            // Slanted: the horizontal part of the line, unit length, times the share.
            Vector3 f = GhostPlaybackLogic.ComputeSurfaceFxFlow(
                new Vector3(4f, 0f, 0f), new Vector3(0f, 10f, 0f), Vector3.up, 0.5f);
            Assert.True(Vector3.Distance(new Vector3(0.5f, 0f, 0f), f) < 1e-5f, $"got {f}");
            Assert.Equal(Vector3.zero, GhostPlaybackLogic.ComputeSurfaceFxFlow(
                new Vector3(4f, 0f, 0f), Vector3.zero, Vector3.zero, 1f));
        }

        [Fact]
        public void DustProbe_OnlyBelowTheHighestTerrainPlusTheReach()
        {
            Assert.True(GhostPlaybackLogic.ShouldProbeLaunchDust(70.0, 30f));
            Assert.True(GhostPlaybackLogic.ShouldProbeLaunchDust(GhostPlaybackLogic.LaunchDustProbeCeilingMeters + 30.0, 30f));
            Assert.False(GhostPlaybackLogic.ShouldProbeLaunchDust(GhostPlaybackLogic.LaunchDustProbeCeilingMeters + 31.0, 30f));
            Assert.False(GhostPlaybackLogic.ShouldProbeLaunchDust(80000.0, 50f));
            Assert.False(GhostPlaybackLogic.ShouldProbeLaunchDust(double.NaN, 50f));
            Assert.True(GhostPlaybackLogic.ShouldProbeLaunchDust(-20.0, float.NaN));
        }

        [Fact]
        public void DustEngineOrdinal_CountsOnlyEngineModulesBeforeTheIndex()
        {
            // Modules: [0] decoupler, [1] engine, [2] gimbal, [3] engine, [4] surface fx.
            var isEngine = new[] { false, true, false, true, false };
            Assert.Equal(0, GhostPlaybackLogic.ResolveSurfaceFxEngineOrdinal(1, isEngine));
            Assert.Equal(1, GhostPlaybackLogic.ResolveSurfaceFxEngineOrdinal(3, isEngine));
            Assert.Equal(-1, GhostPlaybackLogic.ResolveSurfaceFxEngineOrdinal(2, isEngine));
            Assert.Equal(-1, GhostPlaybackLogic.ResolveSurfaceFxEngineOrdinal(-1, isEngine));
            Assert.Equal(-1, GhostPlaybackLogic.ResolveSurfaceFxEngineOrdinal(5, isEngine));
            Assert.Equal(-1, GhostPlaybackLogic.ResolveSurfaceFxEngineOrdinal(0, null));
        }

        [Fact]
        public void DustAggregate_OneSourceIsThatSourceAndSeveralTheirWeightedMean()
        {
            var none = new LaunchDustAggregate();
            Assert.False(none.TryResolve(out _, out _, out _));
            Assert.Equal(0f, none.FxScale);

            var one = new LaunchDustAggregate();
            one.Add(new Vector3(1f, 2f, 3f), Vector3.up, new Vector3(0.2f, 0f, 0f), 0.4f);
            Assert.True(one.TryResolve(out Vector3 p, out Vector3 n, out Vector3 push));
            Assert.Equal(new Vector3(1f, 2f, 3f), p);
            Assert.Equal(Vector3.up, n);
            // One source: stock uses its push as it is (no pushScale).
            Assert.Equal(new Vector3(0.2f, 0f, 0f), push);
            AssertClose(0.4f, one.FxScale, 5);

            var two = new LaunchDustAggregate();
            two.Add(new Vector3(0f, 0f, 0f), Vector3.up, new Vector3(1f, 0f, 0f), 0.75f);
            two.Add(new Vector3(4f, 0f, 0f), Vector3.up, new Vector3(-1f, 0f, 0f), 0.25f);
            Assert.True(two.TryResolve(out p, out n, out push));
            Assert.True(Vector3.Distance(new Vector3(1f, 0f, 0f), p) < 1e-5f, $"got {p}");
            Assert.True(Vector3.Distance(Vector3.up, n) < 1e-5f, $"got {n}");
            Assert.True(Vector3.Distance(new Vector3(0.5f * LaunchDustAggregate.PushScale, 0f, 0f), push) < 1e-5f,
                $"got {push}");
            AssertClose(1f, two.FxScale, 5);
            Assert.Equal(5f, LaunchDustAggregate.PushScale);

            // The strength clamps at 1, as stock's Clamp01 of the sum.
            two.Add(Vector3.zero, Vector3.up, Vector3.zero, 0.9f);
            Assert.Equal(1f, two.FxScale);
        }

        [Fact]
        public void DustAggregate_AllZeroWeightsKeepTheLeadsPlace()
        {
            var agg = new LaunchDustAggregate();
            agg.Add(new Vector3(7f, 0f, 0f), Vector3.up, Vector3.one, 0f);
            agg.Add(new Vector3(9f, 0f, 0f), Vector3.up, Vector3.one, float.NaN);
            Assert.True(agg.TryResolve(out Vector3 p, out _, out Vector3 push));
            Assert.Equal(new Vector3(7f, 0f, 0f), p);
            Assert.Equal(Vector3.zero, push);
            Assert.Equal(0f, agg.FxScale);
        }

        [Fact]
        public void DustAlpha_IsStocksLerpToHalfAndTheEmissionIsLeftAlone()
        {
            // SurfaceFX.LateUpdate: startColor alpha = Lerp(0, 0.5, fxScale), nothing else scales.
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSurfaceFxAlpha(0f));
            AssertClose(0.25f, GhostPlaybackLogic.ComputeSurfaceFxAlpha(0.5f), 6);
            AssertClose(0.5f, GhostPlaybackLogic.ComputeSurfaceFxAlpha(1f), 6);
            AssertClose(0.5f, GhostPlaybackLogic.ComputeSurfaceFxAlpha(4f), 6);
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSurfaceFxAlpha(-1f));
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSurfaceFxAlpha(float.NaN));
            Assert.Equal(0.5f, GhostPlaybackLogic.SurfaceFxAlphaMax);
        }

        [Fact]
        public void DustPushWeight_IsTheSquareRootOfTheLifeLeft()
        {
            // SurfaceFX.UpdateParticle: Pow(remaining / startLifetime, pushThreshold 0.5).
            Assert.Equal(0.5f, GhostPlaybackLogic.SurfaceFxPushThreshold);
            AssertClose(1f, GhostPlaybackLogic.ComputeSurfaceFxPushWeight(4f, 4f), 6);
            AssertClose(0.5f, GhostPlaybackLogic.ComputeSurfaceFxPushWeight(1f, 4f), 6);
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSurfaceFxPushWeight(0f, 4f));
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSurfaceFxPushWeight(1f, 0f));
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSurfaceFxPushWeight(float.NaN, 4f));
            Assert.Equal(0f, GhostPlaybackLogic.ComputeSurfaceFxPushWeight(1f, float.NaN));
        }

        [Fact]
        public void DustForward_LooksAlongThePushOnTheSurfaceAndElseAnySurfaceAxis()
        {
            var up = new Vector3(0f, 0.6f, 0.8f);
            Vector3 f = GhostPlaybackLogic.ResolveSurfaceFxForward(new Vector3(2f, 0f, 0f), up);
            Assert.True(Vector3.Distance(Vector3.right, f) < 1e-5f, $"got {f}");
            // A push with a part along the normal: only its surface part counts.
            f = GhostPlaybackLogic.ResolveSurfaceFxForward(new Vector3(1f, 0f, 0f) + up * 3f, up);
            Assert.True(Vector3.Distance(Vector3.right, f) < 1e-5f, $"got {f}");
            // No push (a nozzle straight over flat ground) or one along the normal: some unit axis
            // on the surface, never zero, so LookRotation always has a forward.
            foreach (Vector3 push in new[] { Vector3.zero, up * 2f })
            {
                foreach (Vector3 n in new[] { up, Vector3.up, Vector3.right, Vector3.zero })
                {
                    Vector3 g = GhostPlaybackLogic.ResolveSurfaceFxForward(push, n);
                    Vector3 nn = n.sqrMagnitude > 0f ? n.normalized : Vector3.up;
                    AssertClose(1f, g.magnitude, 4);
                    Assert.True(Math.Abs(Vector3.Dot(g, nn)) < 1e-4f, $"push={push} n={n}: {g}");
                }
            }
        }

        [Fact]
        public void DustLogSignature_ChangesOnSurfaceColliderOrTenthOnly()
        {
            int a = GhostPlaybackLogic.ComputeLaunchDustLogSignature(1, 42, 0.51f, 0f);
            Assert.Equal(a, GhostPlaybackLogic.ComputeLaunchDustLogSignature(1, 42, 0.59f, 0f));
            Assert.NotEqual(a, GhostPlaybackLogic.ComputeLaunchDustLogSignature(1, 42, 0.61f, 0f));
            Assert.NotEqual(a, GhostPlaybackLogic.ComputeLaunchDustLogSignature(5, 42, 0.51f, 0f));
            Assert.NotEqual(a, GhostPlaybackLogic.ComputeLaunchDustLogSignature(1, 43, 0.51f, 0f));
            Assert.NotEqual(a, GhostPlaybackLogic.ComputeLaunchDustLogSignature(1, 42, 0.51f, 0.3f));
            // Out-of-range strengths clamp into the end tenths instead of inventing new keys.
            Assert.Equal(GhostPlaybackLogic.ComputeLaunchDustLogSignature(1, 1, 1f, 0f),
                GhostPlaybackLogic.ComputeLaunchDustLogSignature(1, 1, 7f, 0f));
            Assert.NotEqual(int.MinValue, GhostPlaybackLogic.ComputeLaunchDustLogSignature(0, 0, 0f, 0f));
        }

        // ------------------------------------------------ S3: warp-gap decay of a smoothed rate

        [Fact]
        public void DecayRateTowardZero_ShrinksByTheEmaWeightPerGapFrame()
        {
            float once = GhostPlaybackLogic.DecayRateTowardZero(
                10f, GhostPlaybackLogic.WheelSteeringHeadingEmaAlpha);
            AssertClose(7f, once, 4);

            float twice = GhostPlaybackLogic.DecayRateTowardZero(
                once, GhostPlaybackLogic.WheelSteeringHeadingEmaAlpha);
            AssertClose(4.9f, twice, 4);
        }

        [Fact]
        public void DecayRateTowardZero_KeepsTheSignAndSettlesExactlyAtZero()
        {
            float rate = -12f;
            for (int i = 0; i < 60; i++)
            {
                float next = GhostPlaybackLogic.DecayRateTowardZero(
                    rate, GhostPlaybackLogic.WheelSteeringHeadingEmaAlpha);
                Assert.True(Math.Abs(next) <= Math.Abs(rate), $"step {i} grew: {rate} -> {next}");
                Assert.True(next <= 0f, $"step {i} flipped sign: {rate} -> {next}");
                rate = next;
            }
            AssertClose(0f, rate, 6);
        }

        [Fact]
        public void DecayRateTowardZero_DegradesSafelyOnNonFiniteInputs()
        {
            AssertClose(0f, GhostPlaybackLogic.DecayRateTowardZero(float.NaN, 0.3f), 6);
            AssertClose(0f, GhostPlaybackLogic.DecayRateTowardZero(
                float.PositiveInfinity, 0.3f), 6);

            // A non-positive alpha means "no decay defined"; hold rather than invent one.
            AssertClose(10f, GhostPlaybackLogic.DecayRateTowardZero(10f, 0f), 4);
            AssertClose(10f, GhostPlaybackLogic.DecayRateTowardZero(10f, float.NaN), 4);
            AssertClose(0f, GhostPlaybackLogic.DecayRateTowardZero(10f, 1f), 4);
        }

        // ------------------------------------------- S1: the reflective write's TYPE contract

        /// <summary>
        /// The three emitter fields <c>ApplyFxMagnitudeScale</c> writes, named exactly as it names
        /// them. Kept as one list so a cell cannot silently cover fewer fields than the writer
        /// touches.
        /// </summary>
        private static readonly string[] WrittenEmitterFieldNames =
            { "minEmission", "maxEmission", "localVelocity" };

        /// <summary>
        /// THE CELL THE 2026-08-11 H36 FLIGHT NEEDED AND THREE REVIEW PASSES DID NOT HAVE.
        ///
        /// Every headless cell above pins the ratio ARITHMETIC — pure floats in, pure float out —
        /// and every one of them stayed green while the actual write threw on every emitter in the
        /// game, because none of them ever met the REAL field types. <c>FieldInfo.SetValue</c>
        /// performs no numeric conversion: it demands an instance of the field's declared type, and
        /// <c>KSPParticleEmitter.minEmission</c> / <c>maxEmission</c> are declared <c>int</c>.
        ///
        /// This cell reflects over the real <c>KSPParticleEmitter</c> (compile-time reachable
        /// through the Assembly-CSharp reference; reflection over a TYPE is metadata, not a Unity
        /// ECall, so it is headless-safe) and drives the production conversion for every field the
        /// writer touches, asserting the result is something SetValue would accept.
        /// </summary>
        [Fact]
        public void FxMagnitudeWrite_ConvertsForEveryRealKspParticleEmitterFieldTypeItTouches()
        {
            Type emitterType = typeof(KSPParticleEmitter);

            foreach (string fieldName in WrittenEmitterFieldNames)
            {
                FieldInfo field = emitterType.GetField(fieldName);
                Assert.True(field != null,
                    $"KSPParticleEmitter has no public field '{fieldName}'. Either KSP renamed it " +
                    "(the applier degrades to no scaling, which is fine) or this cell has gone " +
                    "stale against the applier — check ApplyFxMagnitudeScale.");

                if (field.FieldType == typeof(Vector3))
                {
                    Assert.True(
                        GhostPlaybackLogic.IsSupportedMagnitudeVectorFieldType(field.FieldType),
                        $"'{fieldName}' is a Vector3 the writer refuses to write");
                    continue;
                }

                // The scaled magnitudes the applier actually produces: a full-power write, a
                // throttled write, and a write small enough to quantise.
                foreach (float scaled in new[] { 100f, 30.4f, 0.4f })
                {
                    bool converted = GhostPlaybackLogic.TryConvertMagnitudeForField(
                        field.FieldType, scaled, out object boxed);

                    Assert.True(converted,
                        $"the writer cannot express {scaled} as '{fieldName}' " +
                        $"({field.FieldType.Name}); that field would silently stay at its baseline");
                    Assert.True(field.FieldType.IsInstanceOfType(boxed),
                        $"'{fieldName}' is {field.FieldType.Name} but the write would hand " +
                        $"SetValue a {boxed.GetType().Name} — this is EXACTLY the H36 defect " +
                        "(\"Object of type 'System.Single' cannot be converted to type " +
                        "'System.Int32'\"). IsInstanceOfType is the same check SetValue makes.");
                }
            }
        }

        /// <summary>
        /// The same claim one step stronger: an ACTUAL <c>FieldInfo.SetValue</c> against a real
        /// <c>KSPParticleEmitter</c> instance, and a read-back proving the value landed.
        ///
        /// The instance comes from <c>FormatterServices.GetUninitializedObject</c>, which allocates
        /// the managed object without running any constructor — so no Unity native call is made and
        /// this stays headless. That is the only way to touch a MonoBehaviour-derived type outside a
        /// player, and it is enough: SetValue's type check and the field store are pure managed
        /// runtime behaviour, which is the whole of what the defect broke.
        /// </summary>
        [Fact]
        public void FxMagnitudeWrite_ActuallyLandsOnARealKspParticleEmitterInstance()
        {
            object emitter = FormatterServices.GetUninitializedObject(typeof(KSPParticleEmitter));
            Type emitterType = typeof(KSPParticleEmitter);

            // Scalars: a throttled magnitude that must quantise correctly on an int field.
            foreach (string fieldName in new[] { "minEmission", "maxEmission" })
            {
                FieldInfo field = emitterType.GetField(fieldName);
                Assert.True(field != null, $"no field '{fieldName}'");

                Assert.True(GhostPlaybackLogic.TryConvertMagnitudeForField(
                    field.FieldType, 30.4f, out object boxed));
                field.SetValue(emitter, boxed);   // would throw pre-fix

                Assert.Equal(30.0, Convert.ToDouble(field.GetValue(emitter)), 6);
            }

            FieldInfo velocity = emitterType.GetField("localVelocity");
            Assert.True(velocity != null, "no field 'localVelocity'");
            Assert.True(GhostPlaybackLogic.IsSupportedMagnitudeVectorFieldType(velocity.FieldType));
            var scaledVelocity = new Vector3(0f, 3f, 0f);
            velocity.SetValue(emitter, scaledVelocity);
            Assert.Equal(scaledVelocity, (Vector3)velocity.GetValue(emitter));
        }

        [Fact]
        public void MagnitudeConversion_RoundsToTheNearestIntegerRatherThanTruncating()
        {
            Assert.True(GhostPlaybackLogic.TryConvertMagnitudeForField(
                typeof(int), 30.6f, out object boxed));
            Assert.Equal(31, (int)boxed);

            Assert.True(GhostPlaybackLogic.TryConvertMagnitudeForField(
                typeof(int), 30.4f, out boxed));
            Assert.Equal(30, (int)boxed);
        }

        [Fact]
        public void MagnitudeConversion_NeverQuantisesALitPlumeDownToZero()
        {
            // ComputeFxMagnitudeRatio's contract is that it never answers zero for a lit engine.
            // Truncating (or even rounding) 0.4 particles/s to 0 would break that contract at the
            // integer boundary, for exactly the low-rate dense assets the ratio was tuned around.
            Assert.True(GhostPlaybackLogic.TryConvertMagnitudeForField(
                typeof(int), 0.4f, out object boxed));
            Assert.Equal(1, (int)boxed);

            Assert.True(GhostPlaybackLogic.TryConvertMagnitudeForField(
                typeof(int), 0.0001f, out boxed));
            Assert.Equal(1, (int)boxed);

            // A genuine zero stays zero: that is a gate-off, not a quantisation artefact.
            Assert.True(GhostPlaybackLogic.TryConvertMagnitudeForField(
                typeof(int), 0f, out boxed));
            Assert.Equal(0, (int)boxed);
        }

        [Fact]
        public void MagnitudeConversion_KeepsTheNegativeMaxEmissionSentinelNegative()
        {
            // KSPParticleEmitter.Update early-returns on `maxEmission < 0` — it is stock's "this
            // emitter does not emit" flag. Rounding a scaled -0.4 up to 0 (or to +1) would light a
            // deliberately dead emitter on a ghost.
            Assert.True(GhostPlaybackLogic.TryConvertMagnitudeForField(
                typeof(int), -0.4f, out object boxed));
            Assert.Equal(-1, (int)boxed);
        }

        [Fact]
        public void MagnitudeConversion_PassesFloatsThroughUnchanged()
        {
            Assert.True(GhostPlaybackLogic.TryConvertMagnitudeForField(
                typeof(float), 12.5f, out object boxed));
            Assert.IsType<float>(boxed);
            Assert.Equal(12.5f, (float)boxed);

            Assert.True(GhostPlaybackLogic.TryConvertMagnitudeForField(
                typeof(double), 12.5f, out boxed));
            Assert.IsType<double>(boxed);
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void MagnitudeConversion_RefusesNonFiniteMagnitudes(float value)
        {
            Assert.False(GhostPlaybackLogic.TryConvertMagnitudeForField(typeof(int), value, out _));
            Assert.False(GhostPlaybackLogic.TryConvertMagnitudeForField(typeof(float), value, out _));
        }

        [Fact]
        public void MagnitudeConversion_RefusesTypesItCannotExpressRatherThanGuessing()
        {
            // A refusal degrades to "leave the field at its baseline" — the pre-S1 boolean plume.
            Assert.False(GhostPlaybackLogic.TryConvertMagnitudeForField(typeof(string), 1f, out _));
            Assert.False(GhostPlaybackLogic.TryConvertMagnitudeForField(typeof(Vector3), 1f, out _));
            Assert.False(GhostPlaybackLogic.TryConvertMagnitudeForField(null, 1f, out _));

            // Out of range, and negative-into-unsigned, both refuse rather than wrap around.
            Assert.False(GhostPlaybackLogic.TryConvertMagnitudeForField(typeof(byte), 5000f, out _));
            Assert.False(GhostPlaybackLogic.TryConvertMagnitudeForField(typeof(uint), -5f, out _));

            Assert.False(GhostPlaybackLogic.IsSupportedMagnitudeScalarFieldType(typeof(string)));
            Assert.True(GhostPlaybackLogic.IsSupportedMagnitudeScalarFieldType(typeof(int)));
            Assert.True(GhostPlaybackLogic.IsSupportedMagnitudeScalarFieldType(typeof(float)));
            Assert.True(GhostPlaybackLogic.IsSupportedMagnitudeVectorFieldType(typeof(Vector3)));
            Assert.False(GhostPlaybackLogic.IsSupportedMagnitudeVectorFieldType(typeof(Vector2)));
        }

        // ------------------------------------------ S3: the sun-tracking pivot frame (H36 red)

        [Fact]
        public void SunTrackingReference_IsAlwaysPerpendicularToTheAxis()
        {
            // THE H36 SUN-TRACKING DEFECT, as a property. The old code took the pivot's neutral
            // FORWARD as the zero-angle reference and measured about the PARENT's up; on a pivot
            // authored with a quarter-turn those two are parallel, the reference has no component
            // in the aim plane, and the panel never resolves an angle at all — which is exactly
            // what solarPanelOX10C did. Resolving the reference against the axis makes that
            // degeneracy unreachable by construction.
            var axes = new[]
            {
                Vector3.up, Vector3.forward, Vector3.right,
                new Vector3(1f, 1f, 0f), new Vector3(0.3f, -0.7f, 0.2f), Vector3.zero
            };

            foreach (Vector3 axisLocal in axes)
            {
                Vector3 axis = GhostPlaybackLogic.ResolveSunTrackingAxisLocal(axisLocal);
                Vector3 reference = GhostPlaybackLogic.ResolveSunTrackingReferenceLocal(axisLocal);

                AssertClose(1.0, axis.magnitude, 4);
                AssertClose(1.0, reference.magnitude, 4);
                Assert.True(Math.Abs(Vector3.Dot(axis, reference)) < 1e-4f,
                    $"axis {axis} and reference {reference} are not perpendicular for input " +
                    $"{axisLocal} — the aim plane would collapse and the panel would never track");
            }
        }

        [Fact]
        public void SunTrackingReference_KeepsTheStockZeroAngleDirectionWhereItCan()
        {
            // Stock measures from the pivot's own local +Z (Atan2(sunLocal.x, sunLocal.z)) about
            // its own local +Y. For the stock axis the resolver must answer exactly that, or every
            // ghost panel would sit at a constant offset from where the real one points.
            Assert.Equal(Vector3.forward,
                GhostPlaybackLogic.ResolveSunTrackingReferenceLocal(Vector3.up));
            Assert.Equal(Vector3.up, GhostPlaybackLogic.ResolveSunTrackingAxisLocal(Vector3.up));

            // Only a +Z axis forces the swap, and then it must be a different, still-valid axis.
            Assert.Equal(Vector3.right,
                GhostPlaybackLogic.ResolveSunTrackingReferenceLocal(Vector3.forward));
        }

        [Fact]
        public void SunTrackingAim_ResolvesWithTheResolvedReferenceWhereTheOldOneDegenerated()
        {
            // The exact pre-fix geometry: the reference direction handed in lay ALONG the axis.
            // Left arm — what the old code did on such a pivot: declined, every frame, forever.
            Assert.False(GhostPlaybackLogic.TryComputeAimAngleDegrees(
                new Vector3(1f, 0f, 1f), Vector3.up, Vector3.up,
                out _, out float degenerateTargetPerp, out float degenerateReferencePerp));
            Assert.True(degenerateTargetPerp > 0.5f,
                "the TARGET was never the degenerate one — a diagnosis that blamed the Sun's " +
                "geometry would have been chasing the wrong half");
            Assert.True(degenerateReferencePerp < 1e-4f,
                "the REFERENCE is the degenerate projection, and the discriminating log line " +
                "exists to say so");

            // Right arm — the same axis through the resolver: a real angle, every time.
            Assert.True(GhostPlaybackLogic.TryComputeAimAngleDegrees(
                new Vector3(1f, 0f, 1f), Vector3.up,
                GhostPlaybackLogic.ResolveSunTrackingReferenceLocal(Vector3.up),
                out float aim, out _, out float referencePerp));
            AssertClose(45.0, aim, 3);
            AssertClose(1.0, referencePerp, 4);
        }

        [Fact]
        public void SunTrackingAim_StillDeclinesWhenTheSunGenuinelyLiesAlongTheAxis()
        {
            // The one legitimate hold must survive the fix, and must be distinguishable from the
            // frame bug by its projections alone.
            Assert.False(GhostPlaybackLogic.TryComputeAimAngleDegrees(
                Vector3.up * 1000f, Vector3.up,
                GhostPlaybackLogic.ResolveSunTrackingReferenceLocal(Vector3.up),
                out _, out float targetPerp, out float referencePerp));
            Assert.True(targetPerp < 1e-3f, $"target projection should collapse, got {targetPerp}");
            AssertClose(1.0, referencePerp, 4);
        }

        // ---------------------------------------------------------------------- regression

        [Fact]
        public void SnapshotBaselineActions_StillResolveDeployableOpinionsUnchanged()
        {
            // S2 changed how a deployable TARGET is APPLIED, never how it is DECIDED. The M1
            // resolver is the seam between the two and must be untouched.
            var baseline = new SnapshotPartBaseline { deployableExtended = true };
            GhostPlaybackLogic.SnapshotBaselineActions actions =
                GhostPlaybackLogic.ResolveSnapshotBaselineActions(baseline);

            Assert.True(actions.deployableTarget.HasValue);
            Assert.True(actions.deployableTarget.Value);
            Assert.False(actions.deployableThroughCargoBayCascade);
        }
    }
}
