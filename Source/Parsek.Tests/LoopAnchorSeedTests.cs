using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// LOOP-ANCHOR-ACTIVE-VESSEL-NEVER-MARKED-LOADED: the vessels already loaded when the
    /// flight scene starts (the vessel being flown above all) never fire onVesselLoaded into
    /// ParsekFlight, so onFlightReady seeds the loaded-anchor set from them.
    /// </summary>
    public class LoopAnchorSeedTests
    {
        [Fact]
        public void Seed_AddsEveryLoadedVessel_SkippingGhostMapVessels()
        {
            var anchors = new HashSet<uint>();
            var ghostPids = new HashSet<uint> { 777u };

            int added = ParsekFlight.SeedLoadedAnchorVessels(
                anchors, new List<uint> { 101u, 777u, 202u }, ghostPids.Contains, out int ghostSkipped);

            Assert.Equal(2, added);
            Assert.Equal(1, ghostSkipped);
            Assert.Contains(101u, anchors);
            Assert.Contains(202u, anchors);
            Assert.DoesNotContain(777u, anchors);
        }

        [Fact]
        public void Seed_KeepsPidsAlreadyPresent_AndCountsOnlyNewOnes()
        {
            var anchors = new HashSet<uint> { 101u, 999u };

            int added = ParsekFlight.SeedLoadedAnchorVessels(
                anchors, new List<uint> { 101u, 202u, 0u }, null, out int ghostSkipped);

            Assert.Equal(1, added);
            Assert.Equal(0, ghostSkipped);
            Assert.Equal(new HashSet<uint> { 101u, 202u, 999u }, anchors);
        }

        [Fact]
        public void Seed_NullInputs_AddNothing()
        {
            Assert.Equal(0, ParsekFlight.SeedLoadedAnchorVessels(
                null, new List<uint> { 1u }, null, out int skippedA));
            Assert.Equal(0, skippedA);

            var anchors = new HashSet<uint>();
            Assert.Equal(0, ParsekFlight.SeedLoadedAnchorVessels(anchors, null, null, out int skippedB));
            Assert.Equal(0, skippedB);
            Assert.Empty(anchors);
        }
    }
}
