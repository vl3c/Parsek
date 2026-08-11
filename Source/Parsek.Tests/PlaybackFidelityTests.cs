using System;
using System.Collections.Generic;
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
            // The steering drive negates on the way in, so the two inversions cancel and a LEFT
            // turn produces a positive caliper angle. This cell pins that cancellation.
            float gentle = GhostPlaybackLogic.ComputeSynthDeflectionDegrees(
                -10f, GhostPlaybackLogic.WheelSteeringGainDegPerDegPerSec,
                GhostPlaybackLogic.MaxWheelSteeringDegrees);
            float hard = GhostPlaybackLogic.ComputeSynthDeflectionDegrees(
                -400f, GhostPlaybackLogic.WheelSteeringGainDegPerDegPerSec,
                GhostPlaybackLogic.MaxWheelSteeringDegrees);

            AssertClose(15f, gentle, 3);
            AssertClose(GhostPlaybackLogic.MaxWheelSteeringDegrees, hard, 3);
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

        private static List<TrajectoryPoint> Points(params (double alt, double clearance)[] rows)
        {
            var list = new List<TrajectoryPoint>();
            foreach (var row in rows)
            {
                list.Add(new TrajectoryPoint
                {
                    altitude = row.alt,
                    recordedGroundClearance = row.clearance
                });
            }
            return list;
        }

        [Fact]
        public void DustGroundLatch_TakesTheFirstPointCarryingAFiniteClearance()
        {
            Assert.True(GhostPlaybackLogic.TryLatchLaunchDustGroundReference(
                Points((100.0, double.NaN), (105.0, 5.0), (400.0, double.NaN)),
                out double groundRef));
            AssertClose(100.0, groundRef, 4);
        }

        [Fact]
        public void DustGroundLatch_NoClearanceAnywhereMeansNoDustEver()
        {
            // The honest degradation: an orbital recording carries no recordedGroundClearance, and
            // inventing a reference would put a dust cloud around a ghost in space.
            Assert.False(GhostPlaybackLogic.TryLatchLaunchDustGroundReference(
                Points((80000.0, double.NaN), (81000.0, double.NaN)), out double groundRef));
            Assert.True(double.IsNaN(groundRef));
        }

        [Fact]
        public void DustGroundLatch_EmptyOrNullInputDeclines()
        {
            Assert.False(GhostPlaybackLogic.TryLatchLaunchDustGroundReference(null, out _));
            Assert.False(GhostPlaybackLogic.TryLatchLaunchDustGroundReference(
                new List<TrajectoryPoint>(), out _));
        }

        [Fact]
        public void DustIntensity_NoEnginePowerIsTheFirstGate()
        {
            Assert.False(GhostPlaybackLogic.TryComputeLaunchDustIntensity(
                0f, 100.0, 100.0, out float intensity));
            Assert.Equal(0f, intensity);
        }

        [Fact]
        public void DustIntensity_UnlatchedGroundReferenceIsTheSecondGate()
        {
            Assert.False(GhostPlaybackLogic.TryComputeLaunchDustIntensity(
                1f, 100.0, double.NaN, out _));
        }

        [Fact]
        public void DustIntensity_FadesOutWithHeightAboveGroundAndStopsAtTheCeiling()
        {
            const double ground = 70.0;
            Assert.True(GhostPlaybackLogic.TryComputeLaunchDustIntensity(
                1f, ground, ground, out float onDeck));
            Assert.True(GhostPlaybackLogic.TryComputeLaunchDustIntensity(
                1f, ground + GhostPlaybackLogic.LaunchDustMaxAglMeters / 2.0, ground, out float halfway));

            AssertClose(1f, onDeck, 3);
            AssertClose(0.5f, halfway, 3);
            Assert.True(halfway < onDeck);

            Assert.False(GhostPlaybackLogic.TryComputeLaunchDustIntensity(
                1f, ground + GhostPlaybackLogic.LaunchDustMaxAglMeters, ground, out _));
            Assert.False(GhostPlaybackLogic.TryComputeLaunchDustIntensity(
                1f, ground + 5000.0, ground, out _));
        }

        [Fact]
        public void DustIntensity_ScalesWithThrottleAtTheSameHeight()
        {
            const double ground = 70.0;
            GhostPlaybackLogic.TryComputeLaunchDustIntensity(0.25f, ground, ground, out float quarter);
            GhostPlaybackLogic.TryComputeLaunchDustIntensity(1f, ground, ground, out float full);
            Assert.True(quarter < full, $"expected {quarter} < {full}");
        }

        [Fact]
        public void DustIntensity_TolerantOfSmallNegativeAglButNotOfAWrongReference()
        {
            const double ground = 70.0;
            // Terrain-model disagreement between record and playback, not a ghost underground.
            Assert.True(GhostPlaybackLogic.TryComputeLaunchDustIntensity(
                1f, ground - 5.0, ground, out float slightlyBelow));
            AssertClose(1f, slightlyBelow, 3);

            // A reference belonging to somewhere else entirely.
            Assert.False(GhostPlaybackLogic.TryComputeLaunchDustIntensity(
                1f, ground - 900.0, ground, out _));
        }

        // ------------------------------------ S1: world-space emitters keep their minimum flow

        [Fact]
        public void EmitterVelocity_LocalSpaceIsAPlainRatio()
        {
            Vector3 scaled = GhostPlaybackLogic.ScaleEmitterLocalVelocity(
                new Vector3(0f, 0f, -20f), 0.25f, useWorldSpace: false);
            AssertClose(-5f, scaled.z, 4);
            AssertClose(0f, scaled.x, 4);
        }

        [Fact]
        public void EmitterVelocity_WorldSpaceNeverScalesBelowTheMinimumFlowFloor()
        {
            // The captured baseline IS the floor (6 m/s): ApplyWorldSpaceEmitterVelocityFloor runs
            // before the capture. A plain 0.2 ratio would write 1.2 m/s — under the 4 m/s threshold
            // the floor exists to clear — and pool the ReStock SRB smoke at the nozzle again.
            Vector3 baseline = new Vector3(0f, -6f, 0f);
            Vector3 scaled = GhostPlaybackLogic.ScaleEmitterLocalVelocity(
                baseline, 0.2f, useWorldSpace: true);

            AssertClose(6f, scaled.magnitude, 3);
            Assert.True(scaled.y < 0f, "the flow must keep its exhaust-ward direction");
        }

        [Fact]
        public void EmitterVelocity_WorldSpaceKeepsTheRatioWhileItStaysAboveTheFloor()
        {
            // A world-space emitter carrying real velocity (never floored at build time) throttles
            // like any other until it reaches the floor, then stops.
            Vector3 baseline = new Vector3(0f, 0f, -40f);

            Vector3 half = GhostPlaybackLogic.ScaleEmitterLocalVelocity(baseline, 0.5f, true);
            AssertClose(20f, half.magnitude, 3);

            Vector3 tenth = GhostPlaybackLogic.ScaleEmitterLocalVelocity(baseline, 0.1f, true);
            AssertClose(GhostVisualBuilder.WorldSpaceEmitterFloorSpeed, tenth.magnitude, 3);
        }

        [Fact]
        public void EmitterVelocity_TheClampIsAFloorNeverABoost()
        {
            // A world-space emitter whose whole baseline is under the floor must not be inflated
            // past its own build-time magnitude by the clamp.
            Vector3 baseline = new Vector3(0f, -2f, 0f);
            Vector3 scaled = GhostPlaybackLogic.ScaleEmitterLocalVelocity(baseline, 0.25f, true);
            AssertClose(2f, scaled.magnitude, 3);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        public void EmitterVelocity_NonPositiveRatioIsLeftAloneEvenInWorldSpace(float ratio)
        {
            // Zero is a shutdown, where the emitters are being gated off anyway; re-flooring there
            // would write a live flow onto a plume that is supposed to be going out.
            Vector3 scaled = GhostPlaybackLogic.ScaleEmitterLocalVelocity(
                new Vector3(0f, -6f, 0f), ratio, useWorldSpace: true);
            AssertClose(-6f * ratio, scaled.y, 4);
        }

        [Fact]
        public void EmitterVelocity_AZeroBaselineStaysZero()
        {
            Vector3 scaled = GhostPlaybackLogic.ScaleEmitterLocalVelocity(
                Vector3.zero, 0.5f, useWorldSpace: true);
            AssertClose(0f, scaled.magnitude, 5);
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
