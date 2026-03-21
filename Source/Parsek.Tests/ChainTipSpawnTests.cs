using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for PID preservation logic used by chain-tip spawns.
    /// ShouldRegenerateIdentity is a pure decision method — no shared state needed.
    /// </summary>
    public class ChainTipSpawnTests
    {
        [Fact]
        public void ShouldRegenerateIdentity_DefaultFalse_ReturnsTrue()
        {
            // Default behavior: preserveIdentity=false means we DO regenerate identity
            // (normal spawn — new GUID to avoid PID collisions after revert)
            bool result = VesselSpawner.ShouldRegenerateIdentity(preserveIdentity: false);
            Assert.True(result);
        }

        [Fact]
        public void ShouldRegenerateIdentity_PreserveTrue_ReturnsFalse()
        {
            // Chain-tip behavior: preserveIdentity=true means we skip regeneration
            // (vessel keeps original PIDs for continuity with the recording chain)
            bool result = VesselSpawner.ShouldRegenerateIdentity(preserveIdentity: true);
            Assert.False(result);
        }
    }
}
