using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using KSP.UI.Screens.Mapview;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Static helper for drawing ghost vessel map markers via OnGUI.
    /// Shared between flight-scene map view (ParsekUI.DrawMapMarkers via
    /// <see cref="DrawMarkerAtScreen"/>) and the tracking station
    /// (ParsekTrackingStation.OnGUI via <see cref="DrawMarker"/>).
    ///
    /// Icon lookup uses <see cref="StockIconIndexByVesselType"/> — indices
    /// taken from the decompiled stock KSP.UI.Screens.Mapview.MapNode logic,
    /// so ghost icons match stock ProtoVessel icons for every VesselType (#387).
    ///
    /// Label is hidden by default, revealed while hovering the icon, and
    /// toggled sticky by left-clicking the icon. Non-left clicks pass through
    /// so KSP's stock map/tracking handlers still see them. The sticky set is keyed by
    /// recording ID and cleared on scene change. The custom marker is only
    /// drawn when the stock MapNode for the same recording is absent or
    /// suppressed, so there is no double-labeling when a ghost ProtoVessel
    /// exists.
    /// </summary>
    internal static class MapMarkerRenderer
    {
        private const string Tag = "MapMarker";
        private const int IconSize = 20;
        private const int ClickPadding = 6; // add to each side of the icon for easier hit-testing
        private const float UnpinnedMarkerAlpha = 0.8f;

        // VesselType -> sprite index in MapNode.iconSprites, taken from the
        // decompiled KSP.UI.Screens.Mapview.MapNode icon-index lookup.
        // KSP-version dependent: if the atlas is reordered, these indices
        // must be updated. Missing entries fall back to the diamond texture.
        // Exposed as IReadOnlyDictionary so tests can iterate it without
        // any caller being able to mutate the shared table.
        internal static readonly IReadOnlyDictionary<VesselType, int> StockIconIndexByVesselType =
            new Dictionary<VesselType, int>
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

        /// <summary>
        /// Per-vessel-type atlas + UV entry. Stock KSP ships some vessel-type
        /// icons on separate atlas textures (`DeployedScienceController` lives
        /// on `OrbitIcons_DeployedScience`, `DeployedGroundPart` on
        /// `ConstructionModeAppIcon`), so a single-atlas-plus-UV model silently
        /// drops those icons. See #387 follow-up (2026-04-18).
        /// </summary>
        internal readonly struct VesselIconEntry
        {
            public readonly Texture2D Atlas;
            public readonly Rect UV;
            public VesselIconEntry(Texture2D atlas, Rect uv) { Atlas = atlas; UV = uv; }
        }

        private static Dictionary<VesselType, VesselIconEntry> vesselIconEntries;
        private static Texture2D fallbackDiamond;
        private static GUIStyle labelStyle;
        private static bool initAttempted;

        /// <summary>
        /// Recording IDs whose label is pinned open. Toggled by click-on-icon.
        /// Keyed by rec.RecordingId (stable across index reuse, see bug #279).
        /// </summary>
        private static readonly HashSet<string> stickyMarkers = new HashSet<string>();

        /// <summary>Test-only accessor for sticky state.</summary>
        internal static HashSet<string> StickyMarkersForTesting => stickyMarkers;

        /// <summary>Test-only accessor for the computed per-type icon entries.
        /// Replaces the old single-atlas/UV-dict pair since #387's follow-up
        /// fix made icons live on multiple atlas textures.</summary>
        internal static IReadOnlyDictionary<VesselType, VesselIconEntry> VesselIconEntriesForTesting => vesselIconEntries;

        /// <summary>Test-only accessor for the init latch. True after a terminal
        /// outcome (success OR permanent structural error). Transient startup
        /// failures (null prefab, null/empty sprite array) must leave this false
        /// so the next frame can retry — see <see cref="InitVesselTypeIcons"/>.</summary>
        internal static bool InitAttemptedForTesting
        {
            get => initAttempted;
            set => initAttempted = value;
        }

        /// <summary>
        /// Returns the per-vessel-type accent color for a ghost marker.
        /// Stock atlas icons ignore this tint, but the fallback diamond still
        /// uses it and flight/TS callers keep one shared vessel-type palette.
        /// </summary>
        internal static Color GetColorForType(VesselType vtype)
        {
            switch (vtype)
            {
                case VesselType.Ship:    return new Color(0.78f, 0.78f, 0.0f);  // yellow
                case VesselType.Probe:   return new Color(0.84f, 0.46f, 0.0f);  // orange
                case VesselType.Relay:   return new Color(0.41f, 0.67f, 0.0f);  // green
                case VesselType.Rover:   return new Color(0.55f, 0.78f, 0.22f); // lime
                case VesselType.Station: return new Color(0.0f, 0.63f, 0.90f);  // blue
                case VesselType.Plane:   return new Color(0.63f, 0.46f, 0.78f); // purple
                case VesselType.Lander:  return new Color(0.78f, 0.67f, 0.0f);  // gold
                case VesselType.Base:    return new Color(0.22f, 0.67f, 0.67f); // teal
                case VesselType.EVA:     return new Color(0.78f, 0.78f, 0.78f); // light grey
                default:                 return new Color(0.63f, 0.63f, 0.63f); // grey
            }
        }

        /// <summary>
        /// Returns the shared ghost label color used in map view. Labels stay
        /// yellow for every vessel type; the icon itself already carries the
        /// vehicle distinction, so per-type label tints add noise without
        /// improving scanability.
        /// </summary>
        internal static Color GetLabelColor()
            => GetColorForType(VesselType.Ship);

        /// <summary>
        /// Draw a marker for a world-space position (tracking station path).
        /// Projects through <see cref="PlanetariumCamera"/> — map-only.
        /// </summary>
        internal static void DrawMarker(
            Vector3d worldPos, string markerKey, string label, Color color,
            VesselType vtype = VesselType.Ship)
        {
            if (PlanetariumCamera.Camera == null) return;

            Vector3d scaledPos = ScaledSpace.LocalToScaledSpace(worldPos);
            Vector3 screenPos = PlanetariumCamera.Camera.WorldToScreenPoint(scaledPos);
            if (screenPos.z < 0) return; // behind camera

            DrawMarkerAtScreen(new Vector2(screenPos.x, screenPos.y), markerKey, label, color, vtype);
        }

        /// <summary>
        /// Draw a marker from an already-projected screen position (flight-scene path).
        /// ParsekUI picks between FlightCamera and PlanetariumCamera itself, then
        /// calls this overload so the camera branch isn't duplicated here.
        /// </summary>
        /// <param name="screenPos">Unity screen-space coordinates (origin bottom-left,
        /// as returned by Camera.WorldToScreenPoint). Caller must have already handled
        /// the z &lt; 0 behind-camera case.</param>
        internal static void DrawMarkerAtScreen(
            Vector2 screenPos, string markerKey, string label, Color color,
            VesselType vtype = VesselType.Ship)
        {
            EnsureResources();

            float x = screenPos.x;
            float y = Screen.height - screenPos.y; // GUI Y is inverted

            Rect iconRect = new Rect(x - IconSize / 2f, y - IconSize / 2f, IconSize, IconSize);

            // Label hover + sticky click toggle. Left click only so non-left
            // clicks still reach KSP's stock handlers. This runs before icon
            // draw so a newly-pinned marker reaches full opacity immediately.
            bool sticky = !string.IsNullOrEmpty(markerKey) && stickyMarkers.Contains(markerKey);
            bool mouseOver = false;

            if (Event.current != null && AllowClickInteraction())
            {
                Rect hitRect = new Rect(
                    iconRect.x - ClickPadding, iconRect.y - ClickPadding,
                    iconRect.width + ClickPadding * 2, iconRect.height + ClickPadding * 2);
                mouseOver = hitRect.Contains(Event.current.mousePosition);

                if (mouseOver
                    && !string.IsNullOrEmpty(markerKey)
                    && IsToggleClick(Event.current.type, Event.current.button))
                {
                    int button = Event.current.button;
                    bool nowSticky = ToggleSticky(markerKey, stickyMarkers);
                    sticky = nowSticky;
                    Event.current.Use();
                    ParsekLog.Info(Tag, FormatClickLogLine(label, markerKey, nowSticky, button));
                }
            }

            // Draw the icon.
            Color prevColor = GUI.color;
            VesselIconEntry entry;
            if (vesselIconEntries != null
                && vesselIconEntries.TryGetValue(vtype, out entry)
                && entry.Atlas != null)
            {
                GUI.color = WithMarkerOpacity(Color.white, sticky);
                GUI.DrawTextureWithTexCoords(iconRect, entry.Atlas, entry.UV);
            }
            else if (fallbackDiamond != null)
            {
                GUI.color = WithMarkerOpacity(color, sticky);
                GUI.DrawTexture(iconRect, fallbackDiamond);
            }
            GUI.color = prevColor;

            if (ShouldDrawLabel(sticky, mouseOver))
            {
                labelStyle.normal.textColor = WithMarkerOpacity(GetLabelColor(), sticky);
                GUI.Label(
                    new Rect(x - 75, y + IconSize / 2 + 2, 150, 20),
                    "Ghost: " + (label ?? "(unknown)"),
                    labelStyle);
            }
        }

        /// <summary>
        /// Pure: should the label be drawn given sticky and hover state?
        /// Sticky pins the label, while hover reveals it transiently. Kept as
        /// an internal helper so the decision table is readable at call sites
        /// and so tests can pin the hover/pin contract.
        /// </summary>
        internal static bool ShouldDrawLabel(bool sticky, bool hover) => sticky || hover;

        /// <summary>
        /// Replaces the source alpha with the marker alpha: unpinned hover
        /// markers render muted, while pinned markers and labels render fully
        /// opaque. RGB values are deliberately preserved.
        /// </summary>
        internal static Color WithMarkerOpacity(Color color, bool sticky)
        {
            color.a = sticky ? 1f : UnpinnedMarkerAlpha;
            return color;
        }

        /// <summary>
        /// Pure: is this event a label-toggle click (MouseDown + left button)?
        /// Non-left clicks must pass through so stock map/tracking handlers
        /// still receive them. Extracted so the decision is unit-testable
        /// without a Unity GUI context — callers pass
        /// <c>Event.current.type</c> and <c>Event.current.button</c>
        /// explicitly.
        /// </summary>
        internal static bool IsToggleClick(EventType type, int button)
            => type == EventType.MouseDown && button == 0;

        /// <summary>
        /// Pure: format the INFO log line emitted when the user toggles a
        /// marker's sticky state by clicking its icon. Extracted so tests can
        /// pin the wire format (label / sticky on/off / key / button id) that
        /// log reviews rely on.
        /// </summary>
        internal static string FormatClickLogLine(
            string label, string markerKey, bool nowSticky, int button)
            => string.Format(CultureInfo.InvariantCulture,
                "Ghost icon '{0}' label sticky={1} key={2} button={3}",
                label ?? "(null)", nowSticky ? "on" : "off", markerKey, button);

        /// <summary>
        /// Pure: flip the sticky state for <paramref name="key"/> in <paramref name="set"/>.
        /// Returns the new state (true = now sticky, false = now not sticky).
        /// </summary>
        internal static bool ToggleSticky(string key, HashSet<string> set)
        {
            if (set == null || string.IsNullOrEmpty(key)) return false;
            if (set.Contains(key))
            {
                set.Remove(key);
                return false;
            }
            set.Add(key);
            return true;
        }

        /// <summary>
        /// Reset sticky state and force icon re-init on the next draw.
        /// Called from ParsekFlight.OnSceneChangeRequested and
        /// ParsekTrackingStation.OnDestroy so each scene rebuilds its
        /// icon atlas against the sprite atlas that scene actually uses.
        /// Also destroys the generated fallback-diamond texture so the
        /// re-created one on next scene doesn't leak its predecessor.
        /// </summary>
        internal static void ResetForSceneChange()
        {
            int stickyCount = stickyMarkers.Count;
            stickyMarkers.Clear();
            initAttempted = false;
            vesselIconEntries = null;
            if (fallbackDiamond != null)
            {
                UnityEngine.Object.Destroy(fallbackDiamond);
                fallbackDiamond = null;
            }
            ParsekLog.Info(Tag,
                string.Format(CultureInfo.InvariantCulture,
                    "ResetForSceneChange: cleared {0} sticky marker(s), forced icon re-init",
                    stickyCount));
        }

        /// <summary>Pure test hook — reset everything including fallback resources.</summary>
        internal static void ResetForTesting()
        {
            stickyMarkers.Clear();
            initAttempted = false;
            vesselIconEntries = null;
            fallbackDiamond = null;
            labelStyle = null;
        }

        /// <summary>
        /// Test-only: run the icon atlas reflection/init path without requiring
        /// a GUI context. Used by the in-game MapView icon verification test
        /// (InGameTestRunner coroutines don't always execute during OnGUI).
        /// </summary>
        internal static void ForceInitForTesting()
        {
            if (MapView.fetch != null)
                InitVesselTypeIcons();
        }

        /// <summary>
        /// Gate click / hover interaction to map-view-ish scenes.
        /// Without this, a flight-scene main-window click that happens to overlap
        /// a projected ghost marker position could double-fire (#386 edge case).
        /// </summary>
        private static bool AllowClickInteraction()
        {
            if (HighLogic.LoadedScene == GameScenes.TRACKSTATION) return true;
            if (MapView.MapIsEnabled) return true;
            return false;
        }

        private static void EnsureResources()
        {
            if (vesselIconEntries == null && MapView.fetch != null)
                InitVesselTypeIcons();

            if (fallbackDiamond == null)
            {
                int size = 16;
                fallbackDiamond = new Texture2D(size, size, TextureFormat.ARGB32, false);
                float center = size / 2f;
                float halfDiag = size / 2f - 1f;
                for (int py = 0; py < size; py++)
                    for (int px = 0; px < size; px++)
                    {
                        float manhattan = Mathf.Abs(px - center + 0.5f) + Mathf.Abs(py - center + 0.5f);
                        fallbackDiamond.SetPixel(px, py, manhattan <= halfDiag ? Color.white : Color.clear);
                    }
                fallbackDiamond.Apply();
            }

            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label);
                labelStyle.fontSize = 11;
                labelStyle.fontStyle = FontStyle.Bold;
                labelStyle.alignment = TextAnchor.UpperCenter;
            }
        }

        private static void InitVesselTypeIcons()
        {
            // Do NOT latch on entry — we want to retry across frames as long as the
            // failure mode is transient (Unity hasn't populated MapView.UINodePrefab
            // yet, or the prefab's iconSprites array is still being initialized).
            // Otherwise the very first draw of the scene can lock every ghost marker
            // to the fallback diamond for the rest of the scene's lifetime. The latch
            // is instead set at (a) the structural-failure return (field not found)
            // which won't improve on retry and would otherwise spam logs, and (b) the
            // successful-completion path at the end.
            if (initAttempted) return; // latched only after a terminal outcome

            var prefab = MapView.UINodePrefab;
            if (prefab == null)
            {
                // Transient — MapView may still be initializing. Retry next frame.
                ParsekLog.Verbose(Tag, "InitVesselTypeIcons: UINodePrefab is null (will retry next frame)");
                return;
            }

            var fi = typeof(MapNode).GetField("iconSprites",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi == null)
            {
                // Structural — KSP renamed/removed the private field. Retrying won't
                // help and would spam a warn every frame. Latch.
                ParsekLog.Warn(Tag, "InitVesselTypeIcons: iconSprites field not found — latching, no retry");
                initAttempted = true;
                return;
            }

            var sprites = fi.GetValue(prefab) as Sprite[];
            if (sprites == null || sprites.Length == 0)
            {
                // Transient — iconSprites array may still be populating at early
                // scene load. Retry next frame; use Verbose so a slow startup
                // doesn't flood the log.
                ParsekLog.Verbose(Tag, string.Format(CultureInfo.InvariantCulture,
                    "InitVesselTypeIcons: iconSprites is null or empty (sprites={0}) — will retry next frame",
                    sprites == null ? 0 : sprites.Length));
                return;
            }

            // Lift each sprite into a plain-data record so the mapping pass can
            // be unit-tested without needing a live Unity runtime to construct
            // Sprite/Texture2D instances.
            var spriteData = new SpriteAtlasRecord[sprites.Length];
            for (int i = 0; i < sprites.Length; i++)
            {
                Sprite s = sprites[i];
                if (s == null) continue;
                Texture2D tex = s.texture;
                if (tex == null)
                {
                    spriteData[i] = SpriteAtlasRecord.SpriteOnly();
                    continue;
                }
                spriteData[i] = new SpriteAtlasRecord(tex, s.textureRect, tex.width, tex.height, tex.name);
            }

            vesselIconEntries = BuildVesselIconEntries(spriteData, out int loaded,
                out int outOfRange, out int missingSprite, out int missingTexture,
                out string perAtlasSummary, out string perTypeUvDetail);

            if (ShouldRetryIconInit(loaded, missingSprite, missingTexture))
            {
                // Transient: the sprite array exists but none of the stock-indexed
                // entries carry a usable Texture2D yet. Keep the latch open so the
                // next frame can retry instead of pinning the whole scene to the
                // fallback diamond with an empty entry cache.
                vesselIconEntries = null;
                ParsekLog.Verbose(Tag, string.Format(CultureInfo.InvariantCulture,
                    "InitVesselTypeIcons: no usable icon entries yet (missingSprite={0} missingTexture={1} spriteCount={2}) — will retry next frame",
                    missingSprite, missingTexture, sprites.Length));
                return;
            }

            // Init succeeded — latch so we don't re-reflect on every frame.
            initAttempted = true;

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "InitVesselTypeIcons: loaded={0} ({1}) outOfRange={2} missingSprite={3} " +
                "missingTexture={4} spriteCount={5}",
                loaded, perAtlasSummary, outOfRange, missingSprite, missingTexture,
                sprites.Length));
            ParsekLog.Verbose(Tag, "InitVesselTypeIcons UVs:" + perTypeUvDetail);
        }

        /// <summary>
        /// Per-sprite data lifted out of a Unity <see cref="Sprite"/> so the
        /// mapping pass can be unit-tested. <see cref="HasSprite"/> separates
        /// "index points at a null slot" from "sprite exists but has no texture"
        /// so callers can distinguish the two reject reasons in counters.
        /// </summary>
        internal readonly struct SpriteAtlasRecord
        {
            public readonly bool HasSprite;
            public readonly Texture2D Texture;
            public readonly Rect TextureRect;
            public readonly float AtlasWidth;
            public readonly float AtlasHeight;
            public readonly string AtlasName;

            public SpriteAtlasRecord(Texture2D texture, Rect textureRect,
                float atlasWidth, float atlasHeight, string atlasName)
            {
                HasSprite = true;
                Texture = texture;
                TextureRect = textureRect;
                AtlasWidth = atlasWidth;
                AtlasHeight = atlasHeight;
                AtlasName = atlasName;
            }

            private SpriteAtlasRecord(bool hasSprite)
            {
                HasSprite = hasSprite;
                Texture = null;
                TextureRect = default(Rect);
                AtlasWidth = 0f;
                AtlasHeight = 0f;
                AtlasName = null;
            }

            /// <summary>Sprite slot exists but carries no Texture2D. Rare —
            /// keeps the reject reason distinguishable in the summary.</summary>
            public static SpriteAtlasRecord SpriteOnly() => new SpriteAtlasRecord(true);
        }

        /// <summary>
        /// Pure mapping pass: given a lifted sprite array, build the per-type
        /// entry dict. Each vessel type keeps its own atlas reference so icons
        /// that live on separate KSP atlases (OrbitIcons_DeployedScience,
        /// ConstructionModeAppIcon, …) are accepted instead of skipped.
        /// Replaces the single-atlas-plus-UV-dict model from #387.
        /// </summary>
        internal static Dictionary<VesselType, VesselIconEntry> BuildVesselIconEntries(
            SpriteAtlasRecord[] spriteData,
            out int loaded, out int outOfRange, out int missingSprite, out int missingTexture,
            out string perAtlasSummary, out string perTypeUvDetail)
        {
            var result = new Dictionary<VesselType, VesselIconEntry>();
            loaded = 0;
            outOfRange = 0;
            missingSprite = 0;
            missingTexture = 0;

            // Per-atlas bucketing uses parallel lists compared by
            // object.ReferenceEquals so Unity's overridden ==/Equals/GetHashCode
            // on UnityEngine.Object can't merge distinct atlases with equal
            // "fake null" native pointers (matters for unit tests that fake
            // Texture2D via FormatterServices, and harmless in production).
            var perAtlasTextures = new List<Texture2D>();
            var perAtlasCounts = new List<int>();
            var perAtlasNames = new List<string>();
            var uvDetail = new StringBuilder();

            foreach (var kv in StockIconIndexByVesselType)
            {
                int idx = kv.Value;
                if (spriteData == null || idx < 0 || idx >= spriteData.Length)
                {
                    outOfRange++;
                    continue;
                }
                var rec = spriteData[idx];
                if (!rec.HasSprite)
                {
                    missingSprite++;
                    continue;
                }
                // object.ReferenceEquals instead of `== null` so Unity's
                // overloaded equality (treats destroyed/unconstructed objects
                // as null via native-ptr check) doesn't reject test fakes.
                // Production sprites created through Unity's own lifecycle
                // never present a managed non-null wrapper with a zero native
                // ptr at init time, so this is also safe at runtime.
                if (object.ReferenceEquals(rec.Texture, null)
                    || rec.AtlasWidth <= 0f || rec.AtlasHeight <= 0f)
                {
                    missingTexture++;
                    continue;
                }

                Rect r = rec.TextureRect;
                var uv = new Rect(
                    r.x / rec.AtlasWidth,
                    r.y / rec.AtlasHeight,
                    r.width / rec.AtlasWidth,
                    r.height / rec.AtlasHeight);
                result[kv.Key] = new VesselIconEntry(rec.Texture, uv);
                loaded++;

                int bucket = -1;
                for (int b = 0; b < perAtlasTextures.Count; b++)
                {
                    if (object.ReferenceEquals(perAtlasTextures[b], rec.Texture))
                    {
                        bucket = b;
                        break;
                    }
                }
                if (bucket >= 0)
                {
                    perAtlasCounts[bucket] = perAtlasCounts[bucket] + 1;
                }
                else
                {
                    perAtlasTextures.Add(rec.Texture);
                    perAtlasCounts.Add(1);
                    perAtlasNames.Add(rec.AtlasName);
                }

                uvDetail.AppendFormat(CultureInfo.InvariantCulture,
                    " {0}=[{1}]tex={2}({3:F3},{4:F3},{5:F3},{6:F3})",
                    kv.Key, idx,
                    !string.IsNullOrEmpty(rec.AtlasName) ? rec.AtlasName : "(unnamed)",
                    uv.x, uv.y, uv.width, uv.height);
            }

            perAtlasSummary = FormatPerAtlasSummary(perAtlasCounts, perAtlasNames);
            perTypeUvDetail = uvDetail.ToString();
            return result;
        }

        internal static bool ShouldRetryIconInit(int loaded, int missingSprite, int missingTexture)
            => loaded == 0 && (missingSprite > 0 || missingTexture > 0);

        private static string FormatPerAtlasSummary(List<int> counts, List<string> names)
        {
            if (counts == null || counts.Count == 0) return "atlas=(none)";
            var sb = new StringBuilder();
            for (int i = 0; i < counts.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                string name = i < names.Count && !string.IsNullOrEmpty(names[i])
                    ? names[i]
                    : "(unnamed)";
                sb.AppendFormat(CultureInfo.InvariantCulture, "atlas{0}={1}:{2}",
                    (char)('A' + i), name, counts[i]);
            }
            return sb.ToString();
        }
    }
}
