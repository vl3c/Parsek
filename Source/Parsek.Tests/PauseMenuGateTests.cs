using System;
using System.Collections.Generic;
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
            ParsekLog.ResetTestOverrides();
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
            // false is also the no-probe fallback, so the verdict alone cannot tell a
            // pass-through from a gate that ignored the probe: count the probe call, and
            // prove the live PauseMenu lookup (which logs its failure under xUnit) never ran.
            var logLines = new List<string>();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            int probeCalls = 0;
            PauseMenuGate.ProbeForTesting = () => { probeCalls++; return false; };

            Assert.False(PauseMenuGate.IsPauseMenuOpen());

            Assert.Equal(1, probeCalls);
            Assert.DoesNotContain(logLines, l => l.Contains("PauseMenu probe failed"));
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
