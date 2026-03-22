using System;
using Xunit;

namespace Parsek.Tests
{
    public class ResolveLocalizedNameTests : IDisposable
    {
        public ResolveLocalizedNameTests()
        {
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.SuppressLogging = false;
        }

        [Fact]
        public void ReturnsNullForNull()
        {
            Assert.Null(Recording.ResolveLocalizedName(null));
        }

        [Fact]
        public void ReturnsEmptyForEmpty()
        {
            Assert.Equal("", Recording.ResolveLocalizedName(""));
        }

        [Fact]
        public void ReturnsRegularNameUnchanged()
        {
            Assert.Equal("My Rocket", Recording.ResolveLocalizedName("My Rocket"));
        }

        [Fact]
        public void ReturnsNonLocKeySpecialCharsUnchanged()
        {
            Assert.Equal("Rocket #5", Recording.ResolveLocalizedName("Rocket #5"));
        }

        [Fact]
        public void AutoLocKeyReturnedUnchangedWhenLocalizerUnavailable()
        {
            // In test environment, KSP Localizer is not initialized,
            // so #autoLOC keys should be returned as-is (graceful fallback)
            string result = Recording.ResolveLocalizedName("#autoLOC_501220");
            Assert.Equal("#autoLOC_501220", result);
        }

        [Fact]
        public void HashPrefixStringHandledGracefully()
        {
            // Any string starting with # that isn't a valid loc key should survive
            string result = Recording.ResolveLocalizedName("#foobar");
            Assert.Equal("#foobar", result);
        }
    }
}
