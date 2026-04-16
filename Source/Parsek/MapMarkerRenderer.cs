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
    /// Label has stock-style hover/sticky behavior: hidden by default, shown on
    /// hover, pinned by click (#386). Sticky set is keyed by recording ID and
    /// cleared on scene change. The custom marker is only drawn when the stock
    /// MapNode for the same recording is absent or suppressed, so there is no
    /// double-labeling when a ghost ProtoVessel exists.
    /// </summary>
    internal static class MapMarkerRenderer
    {
        private const string Tag = "MapMarker";
        private const int IconSize = 20;
        private const int ClickPadding = 6; // add to each side of the icon for easier hit-testing

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

        private static Texture2D vesselIconAtlas;
        private static Dictionary<VesselType, Rect> vesselIconUVs;
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

        /// <summary>Test-only accessor for the computed UV cache.</summary>
        internal static IReadOnlyDictionary<VesselType, Rect> VesselIconUVsForTesting => vesselIconUVs;

        /// <summary>Test-only accessor for the atlas texture resolved at init.</summary>
        internal static Texture2D VesselIconAtlasForTesting => vesselIconAtlas;

        /// <summary>
        /// Returns the orbit color for a ghost marker by vessel type.
        /// Matches KSP's own orbit color scheme — single source of truth used
        /// by both ParsekUI (flight) and ParsekTrackingStation (TS).
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

            // Draw the icon.
            Color prevColor = GUI.color;
            Rect uvRect;
            if (vesselIconAtlas != null && vesselIconUVs != null
                && vesselIconUVs.TryGetValue(vtype, out uvRect))
            {
                GUI.color = Color.white;
                GUI.DrawTextureWithTexCoords(iconRect, vesselIconAtlas, uvRect);
            }
            else if (fallbackDiamond != null)
            {
                GUI.color = color;
                GUI.DrawTexture(iconRect, fallbackDiamond);
            }
            GUI.color = prevColor;

            // Label hover / sticky / click (#386).
            bool sticky = !string.IsNullOrEmpty(markerKey) && stickyMarkers.Contains(markerKey);
            bool hover = false;

            if (Event.current != null && AllowClickInteraction())
            {
                Rect hitRect = new Rect(
                    iconRect.x - ClickPadding, iconRect.y - ClickPadding,
                    iconRect.width + ClickPadding * 2, iconRect.height + ClickPadding * 2);
                hover = hitRect.Contains(Event.current.mousePosition);

                if (hover
                    && !string.IsNullOrEmpty(markerKey)
                    && Event.current.type == EventType.MouseDown
                    && Event.current.button == 0)
                {
                    bool nowSticky = ToggleSticky(markerKey, stickyMarkers);
                    sticky = nowSticky;
                    Event.current.Use();
                    ParsekLog.Info(Tag,
                        string.Format(CultureInfo.InvariantCulture,
                            "Ghost icon '{0}' label sticky={1} key={2}",
                            label ?? "(null)", nowSticky ? "on" : "off", markerKey));
                }
            }

            if (ShouldDrawLabel(hover, sticky))
            {
                labelStyle.normal.textColor = color;
                GUI.Label(
                    new Rect(x - 75, y + IconSize / 2 + 2, 150, 20),
                    "Ghost: " + (label ?? "(unknown)"),
                    labelStyle);
            }
        }

        /// <summary>
        /// Pure: should the label be drawn given (hover, sticky)?
        /// Kept as an internal helper so the decision table is readable
        /// at call sites even though its body is trivial.
        /// </summary>
        internal static bool ShouldDrawLabel(bool hover, bool sticky) => hover || sticky;

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
            vesselIconAtlas = null;
            vesselIconUVs = null;
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
            vesselIconAtlas = null;
            vesselIconUVs = null;
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
            if (vesselIconAtlas == null && MapView.fetch != null)
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
            if (initAttempted) return; // one attempt per scene; ResetForSceneChange clears this
            initAttempted = true;

            var prefab = MapView.UINodePrefab;
            if (prefab == null)
            {
                ParsekLog.Verbose(Tag, "InitVesselTypeIcons: UINodePrefab is null");
                return;
            }

            var fi = typeof(MapNode).GetField("iconSprites",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi == null)
            {
                ParsekLog.Warn(Tag, "InitVesselTypeIcons: iconSprites field not found");
                return;
            }

            var sprites = fi.GetValue(prefab) as Sprite[];
            if (sprites == null || sprites.Length == 0)
            {
                ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                    "InitVesselTypeIcons: iconSprites is null or empty (sprites={0})",
                    sprites == null ? 0 : sprites.Length));
                return;
            }

            // Pick the first non-null sprite's texture as atlas. MapView.OrbitIconsMap
            // may point at a different Texture2D instance than the sprite atlas in
            // the tracking station, so the sprite is authoritative.
            Texture2D spriteAtlas = null;
            for (int i = 0; i < sprites.Length; i++)
            {
                if (sprites[i] != null && sprites[i].texture != null)
                {
                    spriteAtlas = sprites[i].texture;
                    break;
                }
            }
            if (spriteAtlas == null)
            {
                ParsekLog.Warn(Tag, "InitVesselTypeIcons: no sprite has a texture");
                return;
            }

            vesselIconAtlas = spriteAtlas;
            vesselIconUVs = new Dictionary<VesselType, Rect>();
            float atlasW = spriteAtlas.width;
            float atlasH = spriteAtlas.height;

            int loaded = 0, outOfRange = 0, mismatchedAtlas = 0, missingSprite = 0;
            var summary = new StringBuilder();

            foreach (var kv in StockIconIndexByVesselType)
            {
                int idx = kv.Value;
                if (idx < 0 || idx >= sprites.Length)
                {
                    outOfRange++;
                    continue;
                }
                Sprite s = sprites[idx];
                if (s == null)
                {
                    missingSprite++;
                    continue;
                }
                if (s.texture != spriteAtlas)
                {
                    mismatchedAtlas++;
                    ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                        "InitVesselTypeIcons: {0} sprite at index {1} uses texture '{2}' " +
                        "different from resolved atlas '{3}' — skipping",
                        kv.Key, idx,
                        s.texture != null ? s.texture.name : "(null)",
                        spriteAtlas.name));
                    continue;
                }

                Rect r = s.textureRect;
                var uv = new Rect(r.x / atlasW, r.y / atlasH, r.width / atlasW, r.height / atlasH);
                vesselIconUVs[kv.Key] = uv;
                loaded++;
                summary.AppendFormat(CultureInfo.InvariantCulture,
                    " {0}=[{1}]({2:F3},{3:F3},{4:F3},{5:F3})",
                    kv.Key, idx, uv.x, uv.y, uv.width, uv.height);
            }

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "InitVesselTypeIcons: loaded={0} outOfRange={1} missingSprite={2} " +
                "mismatchedAtlas={3} atlas={4}x{5} spriteCount={6}",
                loaded, outOfRange, missingSprite, mismatchedAtlas,
                atlasW, atlasH, sprites.Length));
            ParsekLog.Verbose(Tag, "InitVesselTypeIcons UVs:" + summary);
        }
    }
}
