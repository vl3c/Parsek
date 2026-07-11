using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the addon's fail-closed env gate
    /// (<see cref="ParsekTestCommandAddon.IsArmed"/>). Only the literal <c>"1"</c> arms
    /// the seam; every other value (unset / <c>"0"</c> / <c>"true"</c> / empty) stays
    /// inert. Fails if the addon could consume commands without the exact gate, which
    /// would ship the seam enabled by accident.
    /// </summary>
    public class TestCommandEnvGateTests
    {
        [Fact]
        public void ExactlyOne_Arms()
        {
            Assert.True(ParsekTestCommandAddon.IsArmed("1"));
        }

        [Theory]
        [InlineData(null)]   // env var unset
        [InlineData("0")]
        [InlineData("true")]
        [InlineData("")]     // present but empty
        [InlineData(" 1")]   // whitespace is not an exact match
        [InlineData("1 ")]
        [InlineData("01")]
        [InlineData("yes")]
        [InlineData("TRUE")]
        public void AnythingElse_StaysInert(string envValue)
        {
            Assert.False(ParsekTestCommandAddon.IsArmed(envValue));
        }

        [Theory]
        [InlineData(null, "unset")]
        [InlineData("", "empty")]
        [InlineData("0", "0")]
        [InlineData("true", "true")]
        public void FormatEnvForLog_RendersDistinctly(string envValue, string expected)
        {
            Assert.Equal(expected, ParsekTestCommandAddon.FormatEnvForLog(envValue));
        }
    }
}
