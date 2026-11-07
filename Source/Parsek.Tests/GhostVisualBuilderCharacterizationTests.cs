using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization tests for the ConfigNode-only helpers of <see cref="GhostVisualBuilder"/>
    /// that no test called directly: the deployable clip length fallback. The fairing
    /// structure-node parsing has its own suite, FairingGhostStructureTests.
    /// </summary>
    public class GhostVisualBuilderCharacterizationTests
    {
        [Fact]
        public void ResolveDeployableClipSeconds_UnsampledPart_ReturnsDefault()
        {
            // Headless runs never sample an Animation, so every key reads the default.
            Assert.Equal(GhostPlaybackLogic.DefaultDeployableClipSeconds,
                GhostVisualBuilder.ResolveDeployableClipSeconds(null));
            Assert.Equal(GhostPlaybackLogic.DefaultDeployableClipSeconds,
                GhostVisualBuilder.ResolveDeployableClipSeconds(""));
            Assert.Equal(GhostPlaybackLogic.DefaultDeployableClipSeconds,
                GhostVisualBuilder.ResolveDeployableClipSeconds("never-sampled-part-key"));
        }
    }
}
