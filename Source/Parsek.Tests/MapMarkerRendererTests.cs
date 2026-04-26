using System.Collections.Generic;
using System.Runtime.Serialization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure unit tests for <see cref="MapMarkerRenderer"/> — covers the decompiled
    /// stock icon index table (#387), per-vessel-type atlas selection (#387
    /// multi-atlas follow-up), sticky-state toggle behavior (#386), and the
    /// hover/sticky decision helper.
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

        // Simple ghost marker labels are transient on hover and persistent when
        // pinned by click. These cases pin the full decision table.
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, true,  true)]
        [InlineData(true,  false, true)]
        [InlineData(true,  true,  true)]
        public void ShouldDrawLabel_ReturnsHoverOrSticky(
            bool sticky, bool hover, bool expected)
        {
            Assert.Equal(expected, MapMarkerRenderer.ShouldDrawLabel(sticky, hover));
        }

        [Fact]
        public void GetLabelColor_ReturnsSharedYellowInsteadOfPerTypePalette()
        {
            Color labelColor = MapMarkerRenderer.GetLabelColor();
            Color shipColor = MapMarkerRenderer.GetColorForType(VesselType.Ship);
            Color stationColor = MapMarkerRenderer.GetColorForType(VesselType.Station);

            Assert.Equal(shipColor.r, labelColor.r);
            Assert.Equal(shipColor.g, labelColor.g);
            Assert.Equal(shipColor.b, labelColor.b);
            Assert.Equal(shipColor.a, labelColor.a);

            Assert.NotEqual(stationColor.r, labelColor.r);
            Assert.NotEqual(stationColor.g, labelColor.g);
        }

        [Fact]
        public void WithMarkerOpacity_SetsEightyPercentAlphaWithoutChangingRgb()
        {
            Color source = new Color(0.1f, 0.2f, 0.3f, 0.4f);

            Color markerColor = MapMarkerRenderer.WithMarkerOpacity(source, sticky: false);

            Assert.Equal(source.r, markerColor.r);
            Assert.Equal(source.g, markerColor.g);
            Assert.Equal(source.b, markerColor.b);
            Assert.Equal(0.8f, markerColor.a, 0.001f);
        }

        [Fact]
        public void WithMarkerOpacity_UsesFullAlphaWhenPinned()
        {
            Color source = new Color(0.1f, 0.2f, 0.3f, 0.4f);

            Color markerColor = MapMarkerRenderer.WithMarkerOpacity(source, sticky: true);

            Assert.Equal(source.r, markerColor.r);
            Assert.Equal(source.g, markerColor.g);
            Assert.Equal(source.b, markerColor.b);
            Assert.Equal(1f, markerColor.a, 0.001f);
        }

        // IsToggleClick — only left-button MouseDown toggles sticky state.
        // Non-left clicks must pass through so stock map/tracking handlers can
        // still react normally. The production click handler gates on this
        // predicate, so the matrix here defines the full toggle contract.
        [Theory]
        [InlineData(EventType.MouseDown, 0, true)]   // left click down
        [InlineData(EventType.MouseDown, 1, false)]  // right click down — pass through
        [InlineData(EventType.MouseDown, 2, false)]  // middle click down — not a toggle
        [InlineData(EventType.MouseDown, 3, false)]  // any other button — not a toggle
        [InlineData(EventType.MouseUp,   0, false)]  // left click up — not a toggle
        [InlineData(EventType.MouseUp,   1, false)]  // right click up — not a toggle
        [InlineData(EventType.KeyDown,   0, false)]  // key event — not a toggle
        [InlineData(EventType.Repaint,   0, false)]  // repaint — not a toggle
        [InlineData(EventType.Layout,    0, false)]  // layout — not a toggle
        [InlineData(EventType.MouseDrag, 0, false)]  // drag — not a toggle
        public void IsToggleClick_MatchesMouseDownLeftOnly(
            EventType type, int button, bool expected)
        {
            Assert.Equal(expected, MapMarkerRenderer.IsToggleClick(type, button));
        }

        // The click log line keeps the button id in the payload so log reviews
        // can confirm the production left-click path. This test pins the wire
        // format (label / sticky on/off / key / button) by format-building
        // directly — driving DrawMarkerAtScreen requires a live Unity GUI
        // context, but the pure formatter owns the contract.
        [Fact]
        public void FormatClickLogLine_LeftButtonToggleOn_IncludesButtonAndStickyOn()
        {
            string line = MapMarkerRenderer.FormatClickLogLine(
                label: "Bob Kerman Lander", markerKey: "rec-42",
                nowSticky: true, button: 0);
            Assert.Contains("sticky=on", line);
            Assert.Contains("button=0", line);
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

        // Log-assertion test: emit via ParsekLog.Info and assert the captured
        // line goes under tag MapMarker and carries the button id + sticky=on
        // after a left-button toggle.
        [Fact]
        public void ClickLogLine_LeftButtonToggleOn_LoggedUnderMapMarkerTagWithButton()
        {
            string line = MapMarkerRenderer.FormatClickLogLine(
                label: "Ghost A", markerKey: "rec-log", nowSticky: true, button: 0);
            ParsekLog.Info("MapMarker", line);

            Assert.Contains(logLines, l =>
                l.Contains("[MapMarker]") &&
                l.Contains("sticky=on") &&
                l.Contains("button=0") &&
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

        [Theory]
        [InlineData(0, 1, 0, true)]
        [InlineData(0, 0, 1, true)]
        [InlineData(0, 0, 0, false)]
        [InlineData(1, 5, 5, false)]
        public void ShouldRetryIconInit_OnlyForZeroLoadedTransientMisses(
            int loaded, int missingSprite, int missingTexture, bool expected)
        {
            Assert.Equal(expected,
                MapMarkerRenderer.ShouldRetryIconInit(loaded, missingSprite, missingTexture));
        }

        // #387 follow-up (2026-04-18 playtest). The original fix forced every
        // sprite onto a single resolved atlas and silently dropped sprites
        // whose texture differed — losing DeployedScienceController (atlas
        // OrbitIcons_DeployedScience) and DeployedGroundPart (atlas
        // ConstructionModeAppIcon). These tests pin the per-type atlas model
        // that replaces the single-atlas logic.

        private static Texture2D FakeTexture(string name)
        {
            // FormatterServices creates the managed wrapper without calling
            // Unity's native constructor, so we can produce distinguishable
            // Texture2D instances outside a Unity runtime. The production
            // mapping pass only touches the AtlasName string + reference
            // identity (via object.ReferenceEquals), so the native-side noop
            // is fine.
            var tex = (Texture2D)FormatterServices.GetUninitializedObject(typeof(Texture2D));
            // tex.name is backed by a native call; set via reflection on the
            // lifted SpriteAtlasRecord instead.
            return tex;
        }

        private static MapMarkerRenderer.SpriteAtlasRecord FakeRecord(
            Texture2D tex, float x, float y, float w, float h,
            float atlasW = 256f, float atlasH = 256f, string name = "fake")
        {
            return new MapMarkerRenderer.SpriteAtlasRecord(
                tex, new Rect(x, y, w, h), atlasW, atlasH, name);
        }

        [Fact]
        public void BuildVesselIconEntries_AcceptsSpritesOnSeparateAtlases()
        {
            // Three distinguishable atlas textures, each carrying a different
            // stock vessel-type sprite index. A and B are the "problem"
            // multi-atlas case that used to be silently dropped; C is the
            // baseline single-atlas case.
            var atlasA = FakeTexture("OrbitIcons_DeployedScience");
            var atlasB = FakeTexture("ConstructionModeAppIcon");
            var atlasC = FakeTexture("OrbitIcons");

            // Allocate a 32-slot sprite-data array covering every StockIconIndex.
            var sprites = new MapMarkerRenderer.SpriteAtlasRecord[32];
            // Per-type atlas assignments. 20=Ship (atlas C), 23=Plane (atlas C),
            // 28=DeployedScienceController (atlas A — the #387 follow-up case),
            // 29=DeployedGroundPart (atlas B — same).
            sprites[20] = FakeRecord(atlasC, 0f, 0f, 32f, 32f, name: "OrbitIcons");
            sprites[23] = FakeRecord(atlasC, 32f, 0f, 32f, 32f, name: "OrbitIcons");
            sprites[28] = FakeRecord(atlasA, 0f, 0f, 64f, 64f, 64f, 64f, "OrbitIcons_DeployedScience");
            sprites[29] = FakeRecord(atlasB, 0f, 0f, 128f, 128f, 128f, 128f, "ConstructionModeAppIcon");

            var entries = MapMarkerRenderer.BuildVesselIconEntries(sprites,
                out int loaded, out int outOfRange, out int missingSprite,
                out int missingTexture, out string perAtlasSummary, out string perTypeUvDetail);

            Assert.NotNull(entries);
            // All four types we provided sprites for must be present.
            Assert.True(entries.ContainsKey(VesselType.Ship));
            Assert.True(entries.ContainsKey(VesselType.Plane));
            Assert.True(entries.ContainsKey(VesselType.DeployedScienceController));
            Assert.True(entries.ContainsKey(VesselType.DeployedGroundPart));

            // Crucially: each entry carries its OWN atlas texture reference —
            // the DeployedScience and DeployedGroundPart entries used to be
            // skipped with a "different from resolved atlas — skipping" WARN.
            Assert.Same(atlasA, entries[VesselType.DeployedScienceController].Atlas);
            Assert.Same(atlasB, entries[VesselType.DeployedGroundPart].Atlas);
            Assert.Same(atlasC, entries[VesselType.Ship].Atlas);
            Assert.Same(atlasC, entries[VesselType.Plane].Atlas);

            // UVs are per-sprite's own atlas dimensions (not a single shared atlas).
            Assert.Equal(0f, entries[VesselType.DeployedScienceController].UV.x);
            Assert.Equal(1f, entries[VesselType.DeployedScienceController].UV.width);
            Assert.Equal(1f, entries[VesselType.DeployedGroundPart].UV.width);

            Assert.Equal(4, loaded);
            Assert.True(missingSprite > 0, "Untyped slots should count as missingSprite");
            Assert.Equal(0, missingTexture);

            // Per-atlas summary groups by texture reference — expect 3 distinct atlases.
            Assert.Contains("OrbitIcons_DeployedScience", perAtlasSummary);
            Assert.Contains("ConstructionModeAppIcon", perAtlasSummary);
            Assert.Contains("OrbitIcons", perAtlasSummary);
        }

        [Fact]
        public void BuildVesselIconEntries_CountsMissingSpriteVsMissingTextureDistinctly()
        {
            var atlasA = FakeTexture("A");
            var sprites = new MapMarkerRenderer.SpriteAtlasRecord[32];

            // Ship (20): normal entry.
            sprites[20] = FakeRecord(atlasA, 0f, 0f, 16f, 16f, name: "A");
            // Plane (23): the sprite exists but carries no texture. New
            // missingTexture counter should catch this (under the old model
            // these were silently accepted into the wrong atlas).
            sprites[23] = MapMarkerRenderer.SpriteAtlasRecord.SpriteOnly();
            // Probe (18): left default (HasSprite == false) — missingSprite.

            var entries = MapMarkerRenderer.BuildVesselIconEntries(sprites,
                out int loaded, out int outOfRange, out int missingSprite,
                out int missingTexture, out _, out _);

            Assert.True(entries.ContainsKey(VesselType.Ship));
            Assert.False(entries.ContainsKey(VesselType.Plane));
            Assert.False(entries.ContainsKey(VesselType.Probe));
            Assert.Equal(1, loaded);
            Assert.Equal(1, missingTexture);
            Assert.True(missingSprite >= 1, "Default/non-HasSprite slots must count as missingSprite");
        }

        [Fact]
        public void BuildVesselIconEntries_OutOfRangeIsCounted()
        {
            // Sprite array shorter than the max index (29). Every entry whose
            // index falls outside should land in outOfRange.
            var sprites = new MapMarkerRenderer.SpriteAtlasRecord[10];
            var entries = MapMarkerRenderer.BuildVesselIconEntries(sprites,
                out int loaded, out int outOfRange, out _, out _, out _, out _);

            Assert.Equal(0, loaded);
            // Indices >= 10 in the table: 11, 13, 14, 18, 19, 20, 21, 23, 24, 28, 29 — 11 entries.
            Assert.Equal(11, outOfRange);
            Assert.Empty(entries);
        }

        // Log-assertion regression: the multi-atlas mismatch WARN must not fire.
        // Before the fix, the "different from resolved atlas — skipping" WARN
        // was logged for DeployedScienceController and DeployedGroundPart.
        [Fact]
        public void BuildVesselIconEntries_DoesNotLogMultiAtlasMismatchWarn()
        {
            var atlasA = FakeTexture("OrbitIcons_DeployedScience");
            var atlasB = FakeTexture("ConstructionModeAppIcon");
            var atlasC = FakeTexture("OrbitIcons");

            var sprites = new MapMarkerRenderer.SpriteAtlasRecord[32];
            sprites[20] = FakeRecord(atlasC, 0f, 0f, 32f, 32f, name: "OrbitIcons");
            sprites[28] = FakeRecord(atlasA, 0f, 0f, 64f, 64f, 64f, 64f, "OrbitIcons_DeployedScience");
            sprites[29] = FakeRecord(atlasB, 0f, 0f, 128f, 128f, 128f, 128f, "ConstructionModeAppIcon");

            MapMarkerRenderer.BuildVesselIconEntries(sprites,
                out _, out _, out _, out _, out _, out _);

            // No stale "different from resolved atlas" or "skipping" lines.
            Assert.DoesNotContain(logLines, l => l.Contains("different from resolved atlas"));
            Assert.DoesNotContain(logLines, l => l.Contains("skipping"));
        }
    }
}
