using System.Collections.Generic;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The H41 cell's stock oracle: a prefab renderer the rock ghost clones beside its generated
    /// rock must still be rendered by the live rock part.
    /// </summary>
    public class GrappleCaptureRockRendererOracleTests
    {
        private const string Rock = "ParsekRock";

        [Fact]
        public void GeneratedRockAndKeptPrefabRenderers_DropNothing()
        {
            var dropped = GrappleCaptureInGameTest.PrefabClonesStockDropped(
                new List<string> { "Cube", Rock },
                new List<string> { "Cube", "PA_A(Clone)" },
                Rock);
            Assert.Empty(dropped);
        }

        [Fact]
        public void APrefabCloneTheLiveRockLost_IsReported()
        {
            var dropped = GrappleCaptureInGameTest.PrefabClonesStockDropped(
                new List<string> { "potatoroid", Rock },
                new List<string> { "PA_A(Clone)" },
                Rock);
            Assert.Equal(new[] { "potatoroid" }, dropped);
        }

        [Fact]
        public void NamesCompareOrdinal_AndNullListsAreSafe()
        {
            Assert.Equal(new[] { "cube" }, GrappleCaptureInGameTest.PrefabClonesStockDropped(
                new List<string> { "cube" }, new List<string> { "Cube" }, Rock));
            Assert.Empty(GrappleCaptureInGameTest.PrefabClonesStockDropped(null, null, Rock));
            Assert.Equal(new[] { "Cube" }, GrappleCaptureInGameTest.PrefabClonesStockDropped(
                new List<string> { "Cube" }, null, Rock));
        }
    }
}
