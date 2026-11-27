using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A ghost lamp fades on and off as stock's <c>ModuleLight</c> does (decompiled FixedUpdate):
    /// the pure fade, when a recorded change starts one, and the driver's bookkeeping. The Light
    /// writes need live components (proven in flight); these cells run with none.
    /// </summary>
    [Collection("Sequential")]
    public class GhostLightFadeTests : IDisposable
    {
        public GhostLightFadeTests()
        {
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // Stock lamps (spotLight1, navLight1, the gear lights): 2.5 / 2.5, useAnimationDim.
        private const float LampSpeed = 2.5f;

        [Fact]
        public void ResolveLightFade_SwitchedOn_BrightensAtStocksRate()
        {
            // Stock: Lerp(current, 1, 2.5 * 0.02) each step; after 0.4 s (20 steps) 1 - 0.95^20.
            LightFadeFrame frame = GhostPlaybackLogic.ResolveLightFade(
                true, 0.4, 0f, LampSpeed, LampSpeed, true);
            double stockSteps = 1.0 - Math.Pow(1.0 - LampSpeed * 0.02, 20);
            Assert.InRange(frame.level, stockSteps - 0.01, stockSteps + 0.01);
            Assert.True(frame.lightsEnabled);
            Assert.Equal((double)(0.4f / LampSpeed), (double)frame.lensFraction, 4);
            Assert.False(frame.settled);
        }

        [Fact]
        public void ResolveLightFade_SwitchedOff_AnimationDimLampDimsLit()
        {
            LightFadeFrame frame = GhostPlaybackLogic.ResolveLightFade(
                false, 0.4, 1f, LampSpeed, LampSpeed, true);
            Assert.InRange(frame.level, 0.36, 0.38);
            Assert.True(frame.lightsEnabled);
        }

        [Fact]
        public void ResolveLightFade_SwitchedOff_PlainLampGoesDarkAtOnce()
        {
            // Stock SetLightState(false) disables the lights unless useAnimationDim.
            LightFadeFrame frame = GhostPlaybackLogic.ResolveLightFade(
                false, 0.1, 1f, 0.3f, 0.8f, false);
            Assert.False(frame.lightsEnabled);
            Assert.True(frame.level > 0.5f);
        }

        [Fact]
        public void ResolveLightFade_DimmingUsesTheDimSpeed()
        {
            LightFadeFrame frame = GhostPlaybackLogic.ResolveLightFade(
                false, 1.0, 1f, 0.3f, 0.8f, true);
            Assert.Equal(Math.Exp(-0.8), (double)frame.level, 3);
        }

        [Fact]
        public void ResolveLightFade_LongAfter_IsSettled()
        {
            LightFadeFrame frame = GhostPlaybackLogic.ResolveLightFade(
                true, 30.0, 0f, LampSpeed, LampSpeed, true);
            Assert.True(frame.settled);
            Assert.Equal(1f, frame.level);
            Assert.Equal(1f, frame.lensFraction);
        }

        [Fact]
        public void ResolveLightFade_SettlesOnlyWhenTheLensHasArrivedToo()
        {
            // The ladder: intensity tau 3.3 s, lens 0.3 s; a lamp: tau 0.4 s, lens 2.5 s.
            LightFadeFrame lamp = GhostPlaybackLogic.ResolveLightFade(
                true, 2.4, 0f, LampSpeed, LampSpeed, true);
            Assert.True(lamp.level > 0.99f);
            Assert.False(lamp.settled);
        }

        [Theory]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void ResolveLightFade_ClockBeforeTheChange_IsTheSettledState(double elapsed)
        {
            LightFadeFrame frame = GhostPlaybackLogic.ResolveLightFade(
                true, elapsed, 0f, LampSpeed, LampSpeed, true);
            Assert.True(frame.settled);
            Assert.Equal(1f, frame.level);
            Assert.True(frame.lightsEnabled);
        }

        [Fact]
        public void ResolveLightFade_StartsFromWhereAnInterruptedFadeStood()
        {
            LightFadeFrame frame = GhostPlaybackLogic.ResolveLightFade(
                true, 0.0, 0.6f, LampSpeed, LampSpeed, true);
            Assert.Equal(0.6, (double)frame.level, 4);
        }

        [Theory]
        [InlineData(100.0, 2.5f, true)]
        [InlineData(double.NaN, 2.5f, false)]
        [InlineData(double.PositiveInfinity, 2.5f, false)]
        [InlineData(100.0, 0f, false)]
        public void ShouldStartLightFade_NeedsTheEventsUTAndAFadingModule(
            double eventUT, float brightenSpeed, bool expected)
        {
            Assert.Equal(expected, GhostPlaybackLogic.ShouldStartLightFade(eventUT, brightenSpeed));
        }

        private static GhostPlaybackState StateWithLamp(uint pid, out LightGhostInfo info)
        {
            var state = new GhostPlaybackState();
            info = new LightGhostInfo
            {
                partPersistentId = pid,
                lights = new List<UnityEngine.Light>(),
                stockDriven = new List<bool>(),
                onIntensities = new List<float>(),
                brightenSpeed = LampSpeed,
                dimSpeed = LampSpeed,
                animationDim = true
            };
            state.lightInfos = new Dictionary<uint, LightGhostInfo> { { pid, info } };
            return state;
        }

        [Fact]
        public void RecordedChange_StartsAFadeAtTheEventsUT_AndTheDriverRunsIt()
        {
            var state = StateWithLamp(5u, out LightGhostInfo info);
            GhostPlaybackLogic.ApplyLightPowerEventWithOutcomes(
                state, 5u, true, out _, out _, eventUT: 100.0);
            LightPlaybackState lamp = state.lightPlaybackStates[5u];
            Assert.Equal(100.0, lamp.fadeStartUT);
            Assert.Equal(0f, lamp.fadeStartLevel);

            GhostPlaybackLogic.UpdateBlinkingLights(state, 100.4);
            Assert.InRange(info.level, 0.6f, 0.65f);
            Assert.Equal(100.0, lamp.fadeStartUT);

            GhostPlaybackLogic.UpdateBlinkingLights(state, 130.0);
            Assert.Equal(1f, info.level);
            Assert.True(double.IsNaN(lamp.fadeStartUT));
        }

        [Fact]
        public void SwitchedOffMidFade_DimsFromWhereItStood()
        {
            var state = StateWithLamp(5u, out LightGhostInfo info);
            GhostPlaybackLogic.ApplyLightPowerEventWithOutcomes(state, 5u, true, out _, out _, eventUT: 100.0);
            GhostPlaybackLogic.UpdateBlinkingLights(state, 100.4);
            float reached = info.level;
            GhostPlaybackLogic.ApplyLightPowerEventWithOutcomes(state, 5u, false, out _, out _, eventUT: 100.4);
            Assert.Equal(reached, state.lightPlaybackStates[5u].fadeStartLevel);
        }

        [Fact]
        public void BaselineChange_WithNoUT_IsSettledAtOnce()
        {
            var state = StateWithLamp(5u, out LightGhostInfo info);
            GhostPlaybackLogic.ApplyLightPowerEvent(state, 5u, true);
            Assert.True(double.IsNaN(state.lightPlaybackStates[5u].fadeStartUT));
            GhostPlaybackLogic.UpdateBlinkingLights(state, 100.0);
            Assert.Equal(1f, info.level);
        }

        [Fact]
        public void RepeatedEvent_DoesNotRestartTheFade()
        {
            var state = StateWithLamp(5u, out _);
            GhostPlaybackLogic.ApplyLightPowerEventWithOutcomes(state, 5u, true, out _, out _, eventUT: 100.0);
            GhostPlaybackLogic.ApplyLightPowerEventWithOutcomes(state, 5u, true, out _, out _, eventUT: 101.0);
            Assert.Equal(100.0, state.lightPlaybackStates[5u].fadeStartUT);
        }

        [Fact]
        public void Blinking_EndsAFadeInProgress()
        {
            var state = StateWithLamp(5u, out LightGhostInfo info);
            GhostPlaybackLogic.ApplyLightPowerEventWithOutcomes(state, 5u, true, out _, out _, eventUT: 100.0);
            GhostPlaybackLogic.ApplyLightBlinkModeEventWithOutcome(state, 5u, true, 1f);
            GhostPlaybackLogic.UpdateBlinkingLights(state, 100.2);
            Assert.True(double.IsNaN(state.lightPlaybackStates[5u].fadeStartUT));
        }

        [Fact]
        public void LampWithoutLightInfo_NeverFades()
        {
            var state = new GhostPlaybackState();
            GhostPlaybackLogic.ApplyLightPowerEventWithOutcomes(state, 5u, true, out _, out _, eventUT: 100.0);
            Assert.True(double.IsNaN(state.lightPlaybackStates[5u].fadeStartUT));
        }
    }
}
