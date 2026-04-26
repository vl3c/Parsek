using System;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the small <see cref="PauseMenuGate"/> wrapper. The wrapper
    /// guards every Parsek IMGUI surface (flight, KSC, Tracking Station) so
    /// custom icons / labels / windows don't punch through the Esc pause
    /// overlay, and tests pin both the probe-injection contract and the
    /// production fallback when no probe is installed.
    /// </summary>
    [Collection("Sequential")]
    public class PauseMenuGateTests : IDisposable
    {
        public PauseMenuGateTests()
        {
            PauseMenuGate.ResetForTesting();
        }

        public void Dispose()
        {
            PauseMenuGate.ResetForTesting();
        }

        [Fact]
        public void IsPauseMenuOpen_NoProbe_DefaultsToFalse()
        {
            // Production has no probe installed; the live PauseMenu lookup
            // throws under xUnit (no KSP runtime), so the gate must swallow
            // and report "not paused" rather than blowing up the OnGUI hook.
            Assert.False(PauseMenuGate.IsPauseMenuOpen());
        }

        [Fact]
        public void IsPauseMenuOpen_ProbeReturnsTrue_PassesThrough()
        {
            PauseMenuGate.ProbeForTesting = () => true;

            Assert.True(PauseMenuGate.IsPauseMenuOpen());
        }

        [Fact]
        public void IsPauseMenuOpen_ProbeReturnsFalse_PassesThrough()
        {
            PauseMenuGate.ProbeForTesting = () => false;

            Assert.False(PauseMenuGate.IsPauseMenuOpen());
        }

        [Fact]
        public void ResetForTesting_ClearsProbe()
        {
            PauseMenuGate.ProbeForTesting = () => true;
            Assert.True(PauseMenuGate.IsPauseMenuOpen());

            PauseMenuGate.ResetForTesting();

            // After reset the probe is gone; we fall back to the production
            // PauseMenu lookup (which can't resolve in xUnit), so the gate
            // returns the swallowed-failure default of false.
            Assert.False(PauseMenuGate.IsPauseMenuOpen());
        }
    }
}
