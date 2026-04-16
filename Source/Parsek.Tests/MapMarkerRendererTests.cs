using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure unit tests for <see cref="MapMarkerRenderer"/> — covers the decompiled
    /// stock icon index table (#387), sticky-state toggle behavior (#386), and
    /// the hover/sticky decision helper.
    /// </summary>
    [Collection("Sequential")]
    public class MapMarkerRendererTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MapMarkerRendererTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            MapMarkerRenderer.ResetForTesting();
        }

        public void Dispose()
        {
            MapMarkerRenderer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // #387 — the dict must match decompiled MapNode icon indices exactly.
        // A regression here means ghosts will show a different vessel type's icon
        // (the symptom users reported: ships appearing as stations, etc.).
        [Fact]
        public void StockIconIndexByVesselType_MatchesDecompiledMapNodeIndices()
        {
            var expected = new Dictionary<VesselType, int>
            {
                { VesselType.Station, 0 },
                { VesselType.Base, 5 },
                { VesselType.Debris, 7 },
                { VesselType.Flag, 11 },
                { VesselType.EVA, 13 },
                { VesselType.Lander, 14 },
                { VesselType.Probe, 18 },
                { VesselType.Rover, 19 },
                { VesselType.Ship, 20 },
                { VesselType.SpaceObject, 21 },
                { VesselType.Plane, 23 },
                { VesselType.Relay, 24 },
                { VesselType.DeployedScienceController, 28 },
                { VesselType.DeployedGroundPart, 29 },
            };

            Assert.Equal(expected.Count, MapMarkerRenderer.StockIconIndexByVesselType.Count);
            foreach (var kv in expected)
            {
                Assert.True(
                    MapMarkerRenderer.StockIconIndexByVesselType.TryGetValue(kv.Key, out int actual),
                    $"StockIconIndexByVesselType missing entry for {kv.Key}");
                Assert.Equal(kv.Value, actual);
            }
        }

        // #387 — the fix deliberately omits DeployedSciencePart (stock has no index
        // for it; the renderer falls back to the diamond texture).
        [Fact]
        public void StockIconIndexByVesselType_OmitsDeployedSciencePart()
        {
            Assert.False(
                MapMarkerRenderer.StockIconIndexByVesselType.ContainsKey(VesselType.DeployedSciencePart),
                "DeployedSciencePart should fall back to the diamond, not an atlas index");
        }

        // #386 — toggling a fresh key adds it; toggling a present key removes it.
        // Verified separately from DrawMarkerAtScreen because that path requires
        // a live Unity GUI. Keeping the helper pure makes this directly testable.
        [Fact]
        public void ToggleSticky_AddsThenRemoves()
        {
            var set = new HashSet<string>();
            Assert.True(MapMarkerRenderer.ToggleSticky("rec-1", set));
            Assert.Contains("rec-1", set);
            Assert.False(MapMarkerRenderer.ToggleSticky("rec-1", set));
            Assert.DoesNotContain("rec-1", set);
        }

        [Fact]
        public void ToggleSticky_IndependentKeys()
        {
            var set = new HashSet<string>();
            MapMarkerRenderer.ToggleSticky("a", set);
            MapMarkerRenderer.ToggleSticky("b", set);
            Assert.Contains("a", set);
            Assert.Contains("b", set);
            MapMarkerRenderer.ToggleSticky("a", set);
            Assert.DoesNotContain("a", set);
            Assert.Contains("b", set);
        }

        [Fact]
        public void ToggleSticky_RejectsNullOrEmptyKey()
        {
            var set = new HashSet<string>();
            Assert.False(MapMarkerRenderer.ToggleSticky(null, set));
            Assert.False(MapMarkerRenderer.ToggleSticky("", set));
            Assert.Empty(set);
        }

        [Fact]
        public void ToggleSticky_RejectsNullSet()
        {
            Assert.False(MapMarkerRenderer.ToggleSticky("x", null));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true,  false, true)]
        [InlineData(false, true,  true)]
        [InlineData(true,  true,  true)]
        public void ShouldDrawLabel_ReturnsHoverOrSticky(bool hover, bool sticky, bool expected)
        {
            Assert.Equal(expected, MapMarkerRenderer.ShouldDrawLabel(hover, sticky));
        }

        // #386 — ResetForSceneChange clears sticky state (users expect stickies
        // to survive toggle-on/off on the Tracking Station ghost filter #388 but
        // to reset between scenes).
        [Fact]
        public void ResetForSceneChange_ClearsStickyAndLogsCount()
        {
            MapMarkerRenderer.ToggleSticky("rec-a", MapMarkerRenderer.StickyMarkersForTesting);
            MapMarkerRenderer.ToggleSticky("rec-b", MapMarkerRenderer.StickyMarkersForTesting);
            Assert.Equal(2, MapMarkerRenderer.StickyMarkersForTesting.Count);

            MapMarkerRenderer.ResetForSceneChange();

            Assert.Empty(MapMarkerRenderer.StickyMarkersForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[MapMarker]") &&
                l.Contains("ResetForSceneChange") &&
                l.Contains("2"));
        }

        // P2 (post-PR-328 review): the init latch is a terminal-outcome marker,
        // not a "we tried once" marker. Transient startup failures (null prefab
        // or null iconSprites array) must NOT set it — otherwise every ghost
        // marker stays on the fallback diamond for the rest of the scene.
        // Unity dependencies make the full InitVesselTypeIcons path non-trivial
        // to drive from xUnit; these tests pin the latch state machine around
        // the public scene-change / reset contract.

        [Fact]
        public void InitAttemptedForTesting_DefaultsFalseAfterReset()
        {
            MapMarkerRenderer.InitAttemptedForTesting = true;
            MapMarkerRenderer.ResetForTesting();
            Assert.False(MapMarkerRenderer.InitAttemptedForTesting);
        }

        [Fact]
        public void ResetForSceneChange_ClearsInitLatch()
        {
            // A new scene must always re-attempt icon init — the previous scene's
            // terminal latch (successful atlas resolve OR permanent structural
            // error) does not carry forward because the new scene may ship a
            // fresh MapView with a different sprite set.
            MapMarkerRenderer.InitAttemptedForTesting = true;
            MapMarkerRenderer.ResetForSceneChange();
            Assert.False(MapMarkerRenderer.InitAttemptedForTesting);
        }

        [Fact]
        public void ResetForTesting_ClearsInitLatch()
        {
            MapMarkerRenderer.InitAttemptedForTesting = true;
            MapMarkerRenderer.ResetForTesting();
            Assert.False(MapMarkerRenderer.InitAttemptedForTesting);
        }
    }
}
