using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure decision cells for the MissionConfig seam verb (the arrival-
    /// validation lane's promotion): strict loop-arg parsing (the SetSetting
    /// convention) and the optional positive interval whose absence is the
    /// leave-alone sentinel. The Unity applier is one thin call into the
    /// production MissionStore.SetLoopEnabled and is exercised live by the
    /// dwell lane's flights.
    /// </summary>
    public class TestCommandMissionConfigTests
    {
        [Theory]
        [InlineData("true", true, true)]
        [InlineData("false", true, false)]
        [InlineData("True", false, false)]   // strict: case-sensitive
        [InlineData("1", false, false)]
        [InlineData("", false, false)]
        [InlineData(null, false, false)]
        public void Loop_arg_is_strict_true_false(string raw, bool ok, bool on)
        {
            bool parsed = TestCommandMissionConfig.TryParseLoopArg(raw, out bool loopOn);
            Assert.Equal(ok, parsed);
            if (ok)
                Assert.Equal(on, loopOn);
        }

        [Theory]
        [InlineData(null, true, 0.0)]
        [InlineData("", true, 0.0)]
        [InlineData("19645697.5", true, 19645697.5)]
        [InlineData("0", false, 0.0)]        // zero is not a usable interval
        [InlineData("-5", false, 0.0)]
        [InlineData("NaN", false, 0.0)]
        [InlineData("Infinity", false, 0.0)]
        [InlineData("bogus", false, 0.0)]
        public void Interval_arg_is_optional_and_positive(string raw, bool ok, double want)
        {
            bool parsed = TestCommandMissionConfig.TryParseIntervalArg(raw, out double s);
            Assert.Equal(ok, parsed);
            if (ok)
                Assert.Equal(want, s, 9);
        }

        [Theory]
        [InlineData(true, 300.0, true)]
        [InlineData(true, 0.0, false)]    // 0 = the leave-alone sentinel
        [InlineData(false, 300.0, false)] // disable must not rewrite config
        [InlineData(false, 0.0, false)]
        public void Interval_applies_only_on_an_enable(bool loopOn, double s, bool want)
        {
            Assert.Equal(want,
                TestCommandMissionConfig.ShouldApplyInterval(loopOn, s));
        }
    }
}
