using System.Collections.Generic;
using UnityEngine;
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

        // ghost-label-click-toggle follow-up: visibility is driven exclusively by
        // sticky. Hover MUST NOT reveal the label — that behavior from the
        // original #386 ship was explicitly removed. These cases pin the new
        // contract: the decision table is just `sticky`.
        [Theory]
        [InlineData(false, false)]
        [InlineData(true,  true)]
        public void ShouldDrawLabel_ReturnsStickyOnly(bool sticky, bool expected)
        {
            Assert.Equal(expected, MapMarkerRenderer.ShouldDrawLabel(sticky));
        }

        // IsToggleClick — a MouseDown with left (0) OR right (1) button is a
        // toggle; middle (2) and non-MouseDown events are not. The production
        // click handler gates on this predicate, so the matrix here defines the
        // full toggle contract.
        [Theory]
        [InlineData(EventType.MouseDown, 0, true)]   // left click down
        [InlineData(EventType.MouseDown, 1, true)]   // right click down
        [InlineData(EventType.MouseDown, 2, false)]  // middle click down — not a toggle
        [InlineData(EventType.MouseDown, 3, false)]  // any other button — not a toggle
        [InlineData(EventType.MouseUp,   0, false)]  // left click up — not a toggle
        [InlineData(EventType.MouseUp,   1, false)]  // right click up — not a toggle
        [InlineData(EventType.KeyDown,   0, false)]  // key event — not a toggle
        [InlineData(EventType.Repaint,   0, false)]  // repaint — not a toggle
        [InlineData(EventType.Layout,    0, false)]  // layout — not a toggle
        [InlineData(EventType.MouseDrag, 0, false)]  // drag — not a toggle
        public void IsToggleClick_MatchesMouseDownLeftOrRight(
            EventType type, int button, bool expected)
        {
            Assert.Equal(expected, MapMarkerRenderer.IsToggleClick(type, button));
        }

        // ghost-label-click-toggle follow-up: the click log line must include
        // the button id so future log reviews can distinguish left-click from
        // right-click toggles. This test pins the wire format (label / sticky
        // on/off / key / button) by format-building directly and emitting via
        // ParsekLog.Info — driving DrawMarkerAtScreen requires a live Unity
        // GUI context, but the pure formatter owns the contract.
        [Fact]
        public void FormatClickLogLine_RightButtonToggleOn_IncludesButtonAndStickyOn()
        {
            string line = MapMarkerRenderer.FormatClickLogLine(
                label: "Bob Kerman Lander", markerKey: "rec-42",
                nowSticky: true, button: 1);
            Assert.Contains("sticky=on", line);
            Assert.Contains("button=1", line);
            Assert.Contains("key=rec-42", line);
            Assert.Contains("Bob Kerman Lander", line);
        }

        [Fact]
        public void FormatClickLogLine_LeftButtonToggleOff_IncludesButtonAndStickyOff()
        {
            string line = MapMarkerRenderer.FormatClickLogLine(
                label: "Probe 7", markerKey: "rec-7",
                nowSticky: false, button: 0);
            Assert.Contains("sticky=off", line);
            Assert.Contains("button=0", line);
        }

        [Fact]
        public void FormatClickLogLine_NullLabel_RendersAsPlaceholder()
        {
            string line = MapMarkerRenderer.FormatClickLogLine(
                label: null, markerKey: "rec-x", nowSticky: true, button: 0);
            Assert.Contains("(null)", line);
        }

        // Log-assertion test required by the spec: emit via ParsekLog.Info and
        // assert the captured line goes under tag MapMarker and carries the
        // button id + sticky=on after a right-button toggle.
        [Fact]
        public void ClickLogLine_RightButtonToggleOn_LoggedUnderMapMarkerTagWithButton()
        {
            string line = MapMarkerRenderer.FormatClickLogLine(
                label: "Ghost A", markerKey: "rec-log", nowSticky: true, button: 1);
            ParsekLog.Info("MapMarker", line);

            Assert.Contains(logLines, l =>
                l.Contains("[MapMarker]") &&
                l.Contains("sticky=on") &&
                l.Contains("button=1") &&
                l.Contains("key=rec-log"));
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
